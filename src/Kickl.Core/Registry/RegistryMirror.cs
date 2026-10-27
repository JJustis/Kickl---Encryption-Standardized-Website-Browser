using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kickl.Core.Util;

namespace Kickl.Core.Registry;

/// <summary>
/// Verifies the signature over the snapshot bytes as received. There is deliberately no
/// default implementation: hybrid signature verification is a G5 requirement, and the
/// mirror fails closed until a real verifier is supplied.
/// </summary>
public interface IRegistrySnapshotVerifier
{
    bool Verify(ReadOnlySpan<byte> payload, string signature);
}

public enum SnapshotStatus
{
    Accepted,
    AlreadyCurrent,
    Stale,
    Equivocation,
    BadSignature,
    BadRoot,
    Malformed,
    NetworkError,
    SigningKeyChanged,
    NoVerifier
}

public enum PinVerdict
{
    FirstSeen,
    Unchanged,
    Changed,
    Unpinned
}

public sealed record PinChange(string Name, PinVerdict Verdict, string? PendingFingerprint);

public sealed record SnapshotResult(SnapshotStatus Status, string Detail,
    IReadOnlyList<PinChange> PinChanges)
{
    public bool Ok => Status is SnapshotStatus.Accepted or SnapshotStatus.AlreadyCurrent;
}

/// <summary>A first-seen owner key for one name. Public keys only; safe to store unencrypted.</summary>
public sealed class RegistryPin
{
    public string Name { get; set; } = "";
    public string OwnerFingerprint { get; set; } = "";
    public long FirstSeenUtc { get; set; }
    public long PinnedUtc { get; set; }
    /// <summary>A changed key waiting for explicit user approval; the pin has not moved.</summary>
    public string? PendingFingerprint { get; set; }
    public long? ChangedUtc { get; set; }
}

/// <summary>What the mirror has already accepted, plus TOFU pins.</summary>
public sealed class MirrorState
{
    public int Version { get; set; }
    public string RootHash { get; set; } = "";
    public long GeneratedAt { get; set; }
    /// <summary>Pinned fingerprint of the registry signing key; a change fails closed.</summary>
    public string RegistryKeyFingerprint { get; set; } = "";
    public Dictionary<string, RegistryPin> Pins { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>A full registry snapshot. <see cref="Sig"/> is not part of the signed payload.</summary>
public sealed class RegistrySnapshot
{
    public int Version { get; set; }
    public long GeneratedAt { get; set; }
    public string RootHash { get; set; } = "";
    public List<RegistryRecord> Records { get; set; } = [];
    public string Sig { get; set; } = "";

    public byte[] CanonicalPayload() => CanonicalPayload(Version, GeneratedAt, RootHash, Records);

    public static byte[] CanonicalPayload(int version, long generatedAt, string rootHash,
        IEnumerable<RegistryRecord> records)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", version);
            writer.WriteNumber("generatedAt", generatedAt);
            writer.WriteString("rootHash", rootHash);
            writer.WriteStartArray("records");
            foreach (var record in records.OrderBy(r => r.Name, StringComparer.Ordinal))
                WriteRecord(writer, record);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    public static RegistrySnapshot Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > 32 * 1024 * 1024) throw new FormatException("The registry snapshot is larger than any phonebook may be.");
        var snapshot = JsonSerializer.Deserialize<RegistrySnapshot>(payload, Json)
                       ?? throw new FormatException("The registry snapshot is empty.");
        if (snapshot.Records.Count > 200_000) throw new FormatException("The registry snapshot has more records than any phonebook may.");
        if (snapshot.Records.Any(r => r.Name is null || r.Name.Length > 253))
            throw new FormatException("The registry snapshot has a malformed name.");
        if (snapshot.Version < 0)
            throw new FormatException("The registry snapshot is malformed.");
        return snapshot;
    }

    /// <summary>SHA-256 Merkle root over the canonical record bytes, sorted by name.</summary>
    public byte[] ComputeRoot()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var leaves = new List<byte[]>(Records.Count);
        foreach (var record in Records.OrderBy(r => r.Name, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(record.Name))
                throw new FormatException("A registry record has no name.");
            if (!seen.Add(record.Name))
                throw new FormatException($"Duplicate registry record '{record.Name}'.");
            leaves.Add(MerkleLog.LeafHash(RecordBytes(record)));
        }
        return MerkleLog.Root(leaves);
    }

    public string ComputeRootHex() => Hashing.Hex(ComputeRoot());

    public static byte[] RecordBytes(RegistryRecord record)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteRecord(writer, record);
        }
        return buffer.ToArray();
    }

    internal static void WriteRecord(Utf8JsonWriter writer, RegistryRecord record)
    {
        writer.WriteStartObject();
        writer.WriteString("name", record.Name);
        writer.WriteString("owner", record.Owner);
        writer.WriteString("docFile", record.DocFile);
        writer.WriteNumber("version", record.Version);
        writer.WriteNumber("notAfter", record.NotAfter);
        writer.WriteString("proof", record.Proof);
        writer.WriteString("sig", record.Sig);
        writer.WriteString("status", record.Status);
        writer.WriteString("ownerId", record.OwnerId);
        writer.WriteString("badgeId", record.BadgeId);
        writer.WriteNumber("titleSeq", record.TitleSeq);
        writer.WriteString("originDeviceId", record.OriginDeviceId);
        writer.WriteString("directPublicKey", record.DirectPublicKey);
        writer.WriteString("directEndpoint", record.DirectEndpoint);
        writer.WriteString("title", record.Title);
        writer.WriteString("headline", record.Headline);
        writer.WriteString("storyAuthor", record.StoryAuthor);
        writer.WriteString("storyDate", record.StoryDate);
        writer.WriteString("storyLocation", record.StoryLocation);
        writer.WriteStartArray("tags");
        foreach (var tag in record.Tags) writer.WriteStringValue(tag);
        writer.WriteEndArray();
        writer.WriteNumber("updatedUtc", record.UpdatedUtc);
        writer.WriteString("contentHash", record.ContentHash);
        writer.WriteEndObject();
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };
}

public static class RegistryMirror
{
    /// <summary>
    /// Verify and accept one snapshot. Order matters: signature over the received bytes,
    /// then parse, then monotonic version, then Merkle root, then pins.
    /// </summary>
    public static SnapshotResult Apply(MirrorState state, byte[] payload, string signature,
        IRegistrySnapshotVerifier? verifier, long? nowUnix = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(payload);

        if (verifier is null)
            return Failure(SnapshotStatus.NoVerifier,
                "No registry verifier is configured; unsigned snapshots are refused.");

        if (!verifier.Verify(payload, signature))
            return Failure(SnapshotStatus.BadSignature, "The registry snapshot signature did not verify.");

        RegistrySnapshot snapshot;
        byte[] root;
        try
        {
            snapshot = RegistrySnapshot.Parse(payload);
            root = snapshot.ComputeRoot();
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return Failure(SnapshotStatus.Malformed, ex.Message);
        }

        if (snapshot.Version < state.Version)
            return Failure(SnapshotStatus.Stale,
                $"Snapshot v{snapshot.Version} is older than the accepted v{state.Version}.");

        var claimed = DecodeHex(snapshot.RootHash);
        if (claimed.Length != root.Length ||
            !CryptographicOperations.FixedTimeEquals(claimed, root))
            return Failure(SnapshotStatus.BadRoot,
                "The records do not match the snapshot's Merkle root.");

        return AcceptVerified(state, snapshot.Version, snapshot.GeneratedAt,
            Hashing.Hex(root), snapshot.Records, nowUnix);
    }

    /// <summary>
    /// Apply a snapshot whose signature and Merkle root have already been verified by the caller.
    /// This is the delta path: the client merges records locally, recomputes the root, and then
    /// asks the mirror to enforce version monotonicity, equivocation and pins.
    /// </summary>
    public static SnapshotResult AcceptVerified(MirrorState state, int version, long generatedAt,
        string rootHex, IReadOnlyList<RegistryRecord> records, long? nowUnix = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(records);

        if (version < state.Version)
            return Failure(SnapshotStatus.Stale,
                $"Snapshot v{version} is older than the accepted v{state.Version}.");

        if (version == state.Version)
        {
            if (string.Equals(rootHex, state.RootHash, StringComparison.Ordinal))
                return new SnapshotResult(SnapshotStatus.AlreadyCurrent,
                    "This snapshot is already applied.", []);
            return Failure(SnapshotStatus.Equivocation,
                $"Two different snapshots claim v{version}; trust is frozen until this is resolved.");
        }

        var changes = EvaluatePins(state, records,
            nowUnix ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        state.Version = version;
        state.RootHash = rootHex;
        state.GeneratedAt = generatedAt;
        return new SnapshotResult(SnapshotStatus.Accepted, "", changes);
    }

    /// <summary>Accept a pending owner-key change after the user explicitly approves it.</summary>
    public static bool ApproveChange(MirrorState state, string name, long? nowUnix = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!state.Pins.TryGetValue(name, out var pin) || pin.PendingFingerprint is null)
            return false;

        var now = nowUnix ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        pin.OwnerFingerprint = pin.PendingFingerprint;
        pin.PendingFingerprint = null;
        pin.PinnedUtc = now;
        pin.ChangedUtc = null;
        return true;
    }

    /// <summary>The first-seen owner key fingerprint for a record, or empty when the name is unowned.</summary>
    public static string FingerprintOf(RegistryRecord record)
    {
        var material = !string.IsNullOrWhiteSpace(record.OwnerId)
            ? "owner:" + record.OwnerId
            : !string.IsNullOrWhiteSpace(record.DirectPublicKey) ? "direct:" + record.DirectPublicKey : "";
        return material.Length == 0 ? "" : Hashing.Hex(SHA256.HashData(Utf8.B(material)));
    }

    private static List<PinChange> EvaluatePins(MirrorState state, IEnumerable<RegistryRecord> records,
        long now)
    {
        var changes = new List<PinChange>();
        foreach (var record in records)
        {
            var fingerprint = FingerprintOf(record);
            if (fingerprint.Length == 0)
            {
                changes.Add(new PinChange(record.Name, PinVerdict.Unpinned, null));
                continue;
            }

            if (!state.Pins.TryGetValue(record.Name, out var pin))
            {
                state.Pins[record.Name] = new RegistryPin
                {
                    Name = record.Name,
                    OwnerFingerprint = fingerprint,
                    FirstSeenUtc = now,
                    PinnedUtc = now
                };
                changes.Add(new PinChange(record.Name, PinVerdict.FirstSeen, fingerprint));
                continue;
            }

            if (string.Equals(pin.OwnerFingerprint, fingerprint, StringComparison.Ordinal))
            {
                pin.PendingFingerprint = null;
                pin.ChangedUtc = null;
                changes.Add(new PinChange(record.Name, PinVerdict.Unchanged, null));
                continue;
            }

            pin.PendingFingerprint = fingerprint;
            pin.ChangedUtc ??= now;
            changes.Add(new PinChange(record.Name, PinVerdict.Changed, fingerprint));
        }
        return changes;
    }

    private static SnapshotResult Failure(SnapshotStatus status, string detail) =>
        new(status, detail, []);

    private static byte[] DecodeHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex) || hex.Length != 64) return [];
        try { return Convert.FromHexString(hex); }
        catch (FormatException) { return []; }
    }
}

public static class RegistryStateStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static MirrorState Load(DataPaths paths)
    {
        if (!File.Exists(paths.RegistryState)) return new MirrorState();
        var state = JsonSerializer.Deserialize<MirrorState>(File.ReadAllText(paths.RegistryState))
                    ?? new MirrorState();
        state.Pins = new Dictionary<string, RegistryPin>(state.Pins,
            StringComparer.OrdinalIgnoreCase);
        return state;
    }

    public static void Save(DataPaths paths, MirrorState state)
    {
        paths.Ensure();
        File.WriteAllText(paths.RegistryState, JsonSerializer.Serialize(state, Json));
    }
}
