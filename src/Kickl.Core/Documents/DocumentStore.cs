using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kickl.Core.Crypto;
using Kickl.Core.Markup;
using Kickl.Core.Registry;
using Kickl.Core.Relay;
using Kickl.Core.Util;

namespace Kickl.Core.Documents;

public sealed class SiteNotFoundException(string host) : Exception($"No record for '{host}' in the phonebook.")
{
    public string Host { get; } = host;
}

public sealed class IntegrityException(string message) : Exception(message);

/// <summary>
/// The registry's content SHA-256 did not match the package that arrived. Raised before parsing
/// or rendering. Each occurrence increments the attack counter on the registry record.
/// </summary>
public sealed class ContentIntegrityException(string host, string expected, string actual, int count)
    : Exception($"Content hash mismatch for '{host}': expected {Short(expected)}, got {Short(actual)}. " +
                $"Nothing was rendered. Registry attack counter: {count}.")
{
    public string Host { get; } = host;
    public string Expected { get; } = expected;
    public string Actual { get; } = actual;
    public int Count { get; } = count;

    private static string Short(string value) => value.Length <= 16 ? value : value[..16] + "…";
}

/// <summary>An in-memory blob that must be sealed into the package without touching disk plaintext.</summary>
public sealed record PendingBlob(string Mime, byte[] Bytes);

/// <summary>Builds and loads sealed documents from a local site directory.</summary>
public static class DocumentStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private static string BlobAad(string host, string id) => $"kickl/v1/blob|{host}|{id}";
    private static string DocKeyAad(string host) => $"kickl/v1/doc-key|{host}";

    public static SealedDocument Publish(DataPaths paths, string siteDir, string host, byte[] masterKey,
        IReadOnlyDictionary<string, PendingBlob>? extraBlobs = null)
    {
        var markupPath = Path.Combine(siteDir, "site.kml");
        if (!File.Exists(markupPath)) throw new FileNotFoundException("site.kml is required.", markupPath);
        var markup = File.ReadAllText(markupPath);
        var blobs = new Dictionary<string, PendingBlob>(StringComparer.Ordinal);
        var blobDir = Path.Combine(siteDir, "blobs");
        if (Directory.Exists(blobDir))
        {
            foreach (var file in Directory.EnumerateFiles(blobDir).OrderBy(f => f, StringComparer.Ordinal))
            {
                var id = Path.GetFileNameWithoutExtension(file);
                blobs[id] = new PendingBlob(MimeFor(Path.GetExtension(file)), File.ReadAllBytes(file));
            }
        }
        try
        {
            if (extraBlobs is not null)
                foreach (var (id, blob) in extraBlobs)
                {
                    if (!blobs.TryAdd(id, blob))
                        throw new IntegrityException($"Blob id '{id}' is duplicated.");
                }
            return PublishMarkup(paths, host, markup, masterKey, blobs);
        }
        finally
        {
            foreach (var (id, blob) in blobs)
                if (extraBlobs is null || !extraBlobs.TryGetValue(id, out var external) || !ReferenceEquals(blob.Bytes, external.Bytes))
                    CryptographicOperations.ZeroMemory(blob.Bytes);
        }
    }

    /// <summary>Publish generated markup directly from memory without writing a plaintext draft.</summary>
    public static SealedDocument PublishMarkup(DataPaths paths, string host, string markup, byte[] masterKey,
        IReadOnlyDictionary<string, PendingBlob>? blobs = null)
    {
        var page = Kml.Parse(markup, host); // validates before we seal anything
        Phonebook.ValidateMetadata(page.Headline, page.Tags);

        var docKey = RandomNumberGenerator.GetBytes(Aead.KeySize);
        try
        {
            var doc = new SealedDocument
            {
                Site = host,
                Path = "/",
                Title = page.Title,
                Version = 1,
                Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Markup = markup
            };

            if (blobs is not null)
            {
                foreach (var (id, pending) in blobs.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                {
                    if (doc.Blobs.ContainsKey(id))
                        throw new IntegrityException($"Blob id '{id}' is duplicated.");
                    var (nonce, ct) = Aead.Seal(docKey, pending.Bytes, Utf8.B(BlobAad(host, id)));
                    doc.Blobs[id] = new SealedBlob
                    {
                        Mime = pending.Mime,
                        Label = id,
                        Nonce = nonce,
                        Ciphertext = ct,
                        PlainLength = pending.Bytes.Length
                    };
                }
            }

            var (wrapNonce, wrapped) = Aead.Seal(masterKey, docKey, Utf8.B(DocKeyAad(host)));
            doc.DocKey = new WrappedKey { Nonce = wrapNonce, Ciphertext = wrapped };
            doc.Integrity = ComputeIntegrity(doc);

            var target = paths.SiteDoc(host);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, JsonSerializer.Serialize(doc, Json));

            var registry = LocalRegistry.LoadOrEmpty(paths);
            var relative = Path.GetRelativePath(paths.Root, target).Replace('\\', '/');
            var record = LocalRegistry.Upsert(registry, host, relative);
            record.Status = "active"; // publishing creates or reopens the name
            record.Title = page.Title;
            record.Headline = page.Headline;
            record.StoryAuthor = page.Author;
            record.StoryDate = page.Published;
            record.StoryLocation = page.Location;
            record.Tags = page.Tags.ToArray();
            record.UpdatedUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            record.ContentHash = doc.Integrity;

            using (var relay = DeviceRelayKey.Ensure(paths, masterKey))
            {
                record.OriginDeviceId = relay.DeviceId;
                record.DirectPublicKey = B64u.Encode(relay.PublicKeySpki);
                record.DirectEndpoint = "device://" + relay.DeviceId;
            }

            LocalRegistry.Save(paths, registry);

            return doc;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(docKey);
        }
    }

    public static SealedDocument Load(DataPaths paths, string host)
    {
        var registry = LocalRegistry.LoadOrEmpty(paths);
        var record = LocalRegistry.Resolve(registry, host) ?? throw new SiteNotFoundException(host);
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(record.DocFile))
            candidates.Add(Path.Combine(paths.Root, record.DocFile.Replace('/', Path.DirectorySeparatorChar)));
        candidates.Add(paths.SiteDoc(host));

        var path = candidates.FirstOrDefault(File.Exists)
                   ?? throw new SiteNotFoundException(host);

        var doc = JsonSerializer.Deserialize<SealedDocument>(File.ReadAllText(path))
                  ?? throw new IntegrityException("The document is unreadable.");

        if (!string.Equals(doc.Site, host, StringComparison.OrdinalIgnoreCase))
            throw new IntegrityException("Document site does not match the requested name.");

        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(doc.Integrity), Convert.FromHexString(ComputeIntegrity(doc))))
            throw new IntegrityException("Document integrity check failed.");

        return doc;
    }

    public static string ComputeIntegrity(SealedDocument doc)
    {
        using var sha = SHA256.Create();
        void Feed(byte[] data) => sha.TransformBlock(data, 0, data.Length, null, 0);

        Feed(Encoding.UTF8.GetBytes(doc.Site));
        Feed(Encoding.UTF8.GetBytes(doc.Markup));
        foreach (var (id, blob) in doc.Blobs.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            Feed(Encoding.UTF8.GetBytes(id));
            Feed(blob.Ciphertext);
        }
        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    public static string MimeFor(string extension) => extension.ToLowerInvariant() switch
    {
        ".txt" => "text/plain",
        ".md" => "text/markdown",
        ".html" => "text/html",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".svg" => "image/svg+xml",
        ".json" => "application/json",
        _ => "application/octet-stream"
    };
}
