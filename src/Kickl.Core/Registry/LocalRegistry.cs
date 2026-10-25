using System.Text.Json;
using Kickl.Core.Util;

namespace Kickl.Core.Registry;

public sealed class RegistryRecord
{
    public string Name { get; set; } = "";
    public string Owner { get; set; } = "kb1dev";
    public string DocFile { get; set; } = "";
    public int Version { get; set; } = 1;
    public long NotAfter { get; set; }
    /// <summary>Optional proof-of-control: "txt:&lt;64 hex&gt;" after a _kickl TXT check.</summary>
    public string? Proof { get; set; }
    public string Sig { get; set; } = "";
    public string Status { get; set; } = "active"; // active | closed | reserved
    public string OwnerId { get; set; } = "";
    public string BadgeId { get; set; } = "";
    public int TitleSeq { get; set; }

    // Direct device fetch. The registry publishes the origin device's RSA public key;
    // the article itself is fetched from the origin over an RSA-wrapped AES-GCM tunnel.
    public string OriginDeviceId { get; set; } = "";
    public string DirectPublicKey { get; set; } = "";
    public string DirectEndpoint { get; set; } = "";

    // Live-index fields. Required for search; public by design (the phonebook is public).
    public string Title { get; set; } = "";
    public string Headline { get; set; } = "";
    public string StoryAuthor { get; set; } = "";
    public string StoryDate { get; set; } = "";
    public string StoryLocation { get; set; } = "";
    public string[] Tags { get; set; } = [];
    public long UpdatedUtc { get; set; }
    /// <summary>SHA-256 of the delivered package (markup + sealed blobs). Transport integrity, not authorship.</summary>
    public string ContentHash { get; set; } = "";
    /// <summary>How many times a received package failed the registry hash check.</summary>
    public int ContentMismatchCount { get; set; }
    public long LastContentMismatchUtc { get; set; }
}

public sealed class RegistryFile
{
    public int Version { get; set; } = 1;
    public List<RegistryRecord> Records { get; set; } = [];
}

/// <summary>
/// The local mirror of the Pinwheel phonebook. Resolution never touches the network in the
/// slice; in production it reads a signed snapshot and verifies a Merkle root.
/// </summary>
public static class LocalRegistry
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static RegistryFile LoadOrEmpty(DataPaths paths)
    {
        if (!File.Exists(paths.Registry)) return new RegistryFile();
        var file = JsonSerializer.Deserialize<RegistryFile>(File.ReadAllText(paths.Registry));
        return file ?? new RegistryFile();
    }

    public static void Save(DataPaths paths, RegistryFile file)
    {
        paths.Ensure();
        File.WriteAllText(paths.Registry, JsonSerializer.Serialize(file, Json));
    }

    public static RegistryRecord? Resolve(RegistryFile file, string host) =>
        file.Records.FirstOrDefault(r => string.Equals(r.Name, host, StringComparison.OrdinalIgnoreCase));

    public static bool IsVerified(RegistryRecord record) =>
        record.Proof is { Length: 68 } p && p.StartsWith("txt:", StringComparison.Ordinal)
        && p[4..].All(Uri.IsHexDigit);

    public static bool IsOwned(RegistryRecord record) =>
        !string.IsNullOrWhiteSpace(record.BadgeId) && record.Status != "closed";

    public static bool IsClosed(RegistryRecord record) =>
        string.Equals(record.Status, "closed", StringComparison.OrdinalIgnoreCase);

    public static void Close(DataPaths paths, string host)
    {
        var registry = LoadOrEmpty(paths);
        var record = Resolve(registry, host) ?? throw new KeyNotFoundException($"No record for '{host}'.");
        record.Status = "closed";
        Save(paths, registry);
    }

    public static void ReopenIfClosed(DataPaths paths, string host)
    {
        var registry = LoadOrEmpty(paths);
        var record = Resolve(registry, host);
        if (record is null) return;
        record.Status = "active";
        Save(paths, registry);
    }

    /// <summary>
    /// Record one failed content-hash check. The slice increments the local mirror (the same
    /// counter the central Pinwheel DB keeps); production also queues POST /registry/mismatch,
    /// which is anonymous and rate-limited. Returns the new count.
    /// </summary>
    public static int NoteContentMismatch(DataPaths paths, string host)
    {
        var registry = LoadOrEmpty(paths);
        var record = Resolve(registry, host) ?? throw new KeyNotFoundException($"No record for '{host}'.");
        record.ContentMismatchCount++;
        record.LastContentMismatchUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Save(paths, registry);
        return record.ContentMismatchCount;
    }

    public static RegistryRecord Upsert(RegistryFile file, string host, string docFile)
    {
        var record = Resolve(file, host);
        if (record is null)
        {
            record = new RegistryRecord { Name = host, DocFile = docFile, Version = 1 };
            file.Records.Add(record);
        }
        else
        {
            record.DocFile = docFile;
            record.Version++;
        }
        file.Records.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return record;
    }
}
