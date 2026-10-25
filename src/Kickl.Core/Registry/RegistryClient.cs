using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kickl.Core.Util;

namespace Kickl.Core.Registry;

/// <summary>The signed head: advisory metadata used only to decide whether to fetch.</summary>
public sealed class RegistryHead
{
    public int Version { get; set; }
    public string RootHash { get; set; } = "";
    public long GeneratedAt { get; set; }
    public string Sig { get; set; } = "";

    internal static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static RegistryHead? Parse(ReadOnlySpan<byte> payload)
    {
        try
        {
            return JsonSerializer.Deserialize<RegistryHead>(payload, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// A set of changed records between two snapshot versions. The signature covers
/// <see cref="CanonicalPayload"/>; the consistency proof is carried for the append-only log
/// and is not required by a client that already holds the full record set.
/// </summary>
public sealed class RegistryDelta
{
    public int From { get; set; }
    public int To { get; set; }
    public string RootHash { get; set; } = "";
    public long GeneratedAt { get; set; }
    public List<RegistryRecord> Records { get; set; } = [];
    public string[] ConsistencyProof { get; set; } = [];
    public string Sig { get; set; } = "";

    public byte[] CanonicalPayload()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("from", From);
            writer.WriteNumber("to", To);
            writer.WriteString("rootHash", RootHash);
            writer.WriteNumber("generatedAt", GeneratedAt);
            writer.WriteStartArray("records");
            foreach (var record in Records.OrderBy(r => r.Name, StringComparer.Ordinal))
                RegistrySnapshot.WriteRecord(writer, record);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    public static RegistryDelta? Parse(ReadOnlySpan<byte> payload)
    {
        try
        {
            return JsonSerializer.Deserialize<RegistryDelta>(payload, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
}

public sealed record RegistrySyncResult(SnapshotStatus Status, string Detail, int Version,
    IReadOnlyList<PinChange> PinChanges)
{
    public bool Ok => Status is SnapshotStatus.Accepted or SnapshotStatus.AlreadyCurrent;
}

/// <summary>
/// Pulls signed registry state. Full snapshots and deltas are verified before the local
/// mirror or record file changes; a failed fetch leaves both untouched.
/// The signing key is pinned by fingerprint in <see cref="MirrorState"/>.
/// </summary>
public sealed class RegistryClient
{
    private readonly HttpClient _http;
    private readonly Uri _baseUri;
    private readonly IRegistrySnapshotVerifier _verifier;
    private readonly string? _keyFingerprint;

    public RegistryClient(HttpClient http, Uri baseUri, IRegistrySnapshotVerifier verifier,
        string? keyFingerprint = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(baseUri);
        ArgumentNullException.ThrowIfNull(verifier);
        if (!baseUri.IsAbsoluteUri) throw new ArgumentException("The base URI must be absolute.", nameof(baseUri));

        _http = http;
        _baseUri = baseUri.AbsoluteUri.EndsWith('/') ? baseUri : new Uri(baseUri.AbsoluteUri + "/");
        _verifier = verifier;
        _keyFingerprint = keyFingerprint;
    }

    /// <summary>
    /// Bring the local mirror and record file up to date. A full snapshot is fetched on the
    /// first sync; afterwards a delta from the accepted version is used.
    /// </summary>
    public async Task<RegistrySyncResult> SyncAsync(DataPaths paths, MirrorState state,
        RegistryFile registry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(registry);

        RegistryHead? head;
        try
        {
            var bytes = await GetBytesAsync("v1/registry/head", cancellationToken)
                .ConfigureAwait(false);
            if (bytes is null)
                return new RegistrySyncResult(SnapshotStatus.NetworkError,
                    "The registry head could not be fetched.", state.Version, []);
            head = RegistryHead.Parse(bytes);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return new RegistrySyncResult(SnapshotStatus.NetworkError, ex.Message, state.Version, []);
        }

        if (head is null)
            return new RegistrySyncResult(SnapshotStatus.Malformed,
                "The registry head is unreadable.", state.Version, []);

        if (head.Version < state.Version)
            return new RegistrySyncResult(SnapshotStatus.Stale,
                $"The head v{head.Version} is older than the accepted v{state.Version}.",
                state.Version, []);
        if (head.Version == state.Version)
            return new RegistrySyncResult(SnapshotStatus.AlreadyCurrent, "", state.Version, []);

        try
        {
            return state.Version == 0
                ? await FetchSnapshotAsync(paths, state, registry, cancellationToken).ConfigureAwait(false)
                : await FetchDeltaAsync(paths, state, registry, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return new RegistrySyncResult(SnapshotStatus.NetworkError, ex.Message, state.Version, []);
        }
    }

    private async Task<RegistrySyncResult> FetchSnapshotAsync(DataPaths paths, MirrorState state,
        RegistryFile registry, CancellationToken cancellationToken)
    {
        var bytes = await GetBytesAsync("v1/registry/snapshot", cancellationToken).ConfigureAwait(false);
        if (bytes is null)
            return Failure(SnapshotStatus.NetworkError, "The snapshot could not be fetched.", state);

        var snapshot = RegistrySnapshot.Parse(bytes);
        if (snapshot.Version <= state.Version)
            return Failure(SnapshotStatus.Stale, "The snapshot is not newer than the accepted state.", state);

        var canonical = snapshot.CanonicalPayload();
        if (!_verifier.Verify(canonical, snapshot.Sig))
            return Failure(SnapshotStatus.BadSignature, "The snapshot signature did not verify.", state);

        var key = CheckKeyPin(state);
        if (key is not null) return key;

        var root = snapshot.ComputeRootHex();
        if (!Matches(root, snapshot.RootHash))
            return Failure(SnapshotStatus.BadRoot,
                "The snapshot records do not match its Merkle root.", state);

        var result = RegistryMirror.AcceptVerified(state, snapshot.Version, snapshot.GeneratedAt,
            root, snapshot.Records);
        if (result.Status == SnapshotStatus.Accepted)
        {
            registry.Records = [.. snapshot.Records];
            registry.Version = snapshot.Version;
            Persist(paths, state, registry);
        }
        return new RegistrySyncResult(result.Status, result.Detail, state.Version, result.PinChanges);
    }

    private async Task<RegistrySyncResult> FetchDeltaAsync(DataPaths paths, MirrorState state,
        RegistryFile registry, CancellationToken cancellationToken)
    {
        var bytes = await GetBytesAsync($"v1/registry/delta?from={state.Version}", cancellationToken)
            .ConfigureAwait(false);
        if (bytes is null)
            return Failure(SnapshotStatus.NetworkError, "The delta could not be fetched.", state);

        var delta = RegistryDelta.Parse(bytes);
        if (delta is null)
            return Failure(SnapshotStatus.Malformed, "The delta is unreadable.", state);
        if (delta.From != state.Version)
            return Failure(SnapshotStatus.Malformed,
                $"The delta starts at v{delta.From}, not the accepted v{state.Version}.", state);

        if (!_verifier.Verify(delta.CanonicalPayload(), delta.Sig))
            return Failure(SnapshotStatus.BadSignature, "The delta signature did not verify.", state);

        var key = CheckKeyPin(state);
        if (key is not null) return key;

        var merged = Merge(registry.Records, delta.Records);
        var root = new RegistrySnapshot { Records = merged }.ComputeRootHex();
        if (!Matches(root, delta.RootHash))
            return Failure(SnapshotStatus.BadRoot,
                "The applied delta does not match its Merkle root.", state);

        var result = RegistryMirror.AcceptVerified(state, delta.To, delta.GeneratedAt, root, merged);
        if (result.Status == SnapshotStatus.Accepted)
        {
            registry.Records = merged;
            registry.Version = delta.To;
            Persist(paths, state, registry);
        }
        return new RegistrySyncResult(result.Status, result.Detail, state.Version, result.PinChanges);
    }

    /// <summary>Returns a failure only when a pinned key is missing or has changed.</summary>
    private RegistrySyncResult? CheckKeyPin(MirrorState state)
    {
        if (_keyFingerprint is null) return null;

        if (string.IsNullOrEmpty(state.RegistryKeyFingerprint))
        {
            state.RegistryKeyFingerprint = _keyFingerprint;
            return null;
        }

        return string.Equals(state.RegistryKeyFingerprint, _keyFingerprint, StringComparison.Ordinal)
            ? null
            : Failure(SnapshotStatus.SigningKeyChanged,
                "The registry signing key does not match the pinned fingerprint; refusing the update.", state);
    }

    private static List<RegistryRecord> Merge(IEnumerable<RegistryRecord> current,
        IEnumerable<RegistryRecord> changed)
    {
        var byName = new Dictionary<string, RegistryRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in current) byName[record.Name] = record;
        foreach (var record in changed) byName[record.Name] = record;
        return [.. byName.Values.OrderBy(r => r.Name, StringComparer.Ordinal)];
    }

    private static bool Matches(string actual, string claimed) =>
        string.Equals(actual, claimed, StringComparison.OrdinalIgnoreCase);

    private static void Persist(DataPaths paths, MirrorState state, RegistryFile registry)
    {
        LocalRegistry.Save(paths, registry);
        RegistryStateStore.Save(paths, state);
    }

    private static RegistrySyncResult Failure(SnapshotStatus status, string detail, MirrorState state) =>
        new(status, detail, state.Version, []);

    private async Task<byte[]?> GetBytesAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(new Uri(_baseUri, path), cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NotModified
            or HttpStatusCode.ServiceUnavailable)
            return null;
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"The registry returned {(int)response.StatusCode}.");
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }
}
