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

/// <summary>A protected blob was requested without the site's access key.</summary>
public sealed class AccessKeyRequiredException(string host)
    : Exception($"jttp://{host}/ has protected content. Enter its access key to open it.");

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

    /// <summary>The most a stored or delivered site package may weigh. The largest real one is a few MiB.</summary>
    public const long MaxDocumentBytes = 64L << 20;
    public const int MaxBlobs = 512;
    public const long MaxBlobBytes = 32L << 20;

    /// <summary>
    /// Read a package from disk without trusting its size or shape: a hostile site controls its own file, and one that expands to
    /// gigabytes, or claims thousands of blobs, must be refused before it is held in memory.
    /// </summary>
    internal static SealedDocument ReadDocumentFile(string path)
    {
        if (new FileInfo(path).Length > MaxDocumentBytes)
            throw new IntegrityException("The document is larger than any site package may be.");
        var doc = JsonSerializer.Deserialize<SealedDocument>(File.ReadAllText(path))
                  ?? throw new IntegrityException("The document is unreadable.");
        ValidateShape(doc);
        return doc;
    }

    public static void ValidateShape(SealedDocument doc)
    {
        if (doc.Blobs.Count > MaxBlobs)
            throw new IntegrityException($"The document claims {doc.Blobs.Count} blobs; the limit is {MaxBlobs}.");
        long total = 0;
        foreach (var (_, blob) in doc.Blobs)
        {
            if (blob.Ciphertext.Length > MaxBlobBytes)
                throw new IntegrityException("A blob in the document is larger than any blob may be.");
            total += blob.Ciphertext.Length;
        }
        if (total > MaxDocumentBytes) throw new IntegrityException("The document's blobs together are larger than any site package may be.");
        if (doc.SealedMarkup is { } m && m.Ciphertext.Length > 1024 * 1024)
            throw new IntegrityException("The document's page text is larger than any page may be.");
    }

    private static string BlobAad(string host, string id) => $"kickl/v1/blob|{host}|{id}";
    private static string DocKeyAad(string host) => $"kickl/v1/doc-key|{host}";

    public static SealedDocument Publish(DataPaths paths, string siteDir, string host, byte[] masterKey,
        IReadOnlyDictionary<string, PendingBlob>? extraBlobs = null, string? accessKey = null)
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
            return PublishMarkup(paths, host, markup, masterKey, blobs, accessKey);
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
        IReadOnlyDictionary<string, PendingBlob>? blobs = null, string? accessKey = null, Trust.CaSeal? seal = null)
    {
        var page = Kml.Parse(markup, host); // validates before we seal anything
        Phonebook.ValidateMetadata(page.Headline, page.Tags);

        var protectedIds = page.Blocks
            .Where(b => b.Protected && b.Id is not null).Select(b => b.Id!).ToHashSet(StringComparer.Ordinal);

        var docKey = RandomNumberGenerator.GetBytes(Aead.KeySize);
        byte[]? protectedKey = protectedIds.Count > 0 ? RandomNumberGenerator.GetBytes(Aead.KeySize) : null;
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
                    var isProtected = protectedIds.Contains(id);
                    var (nonce, ct) = Aead.Seal(isProtected ? protectedKey! : docKey, pending.Bytes,
                        Utf8.B(BlobAad(host, id)));
                    doc.Blobs[id] = new SealedBlob
                    {
                        Access = isProtected ? "protected" : "public",
                        Mime = pending.Mime,
                        Label = id,
                        Nonce = nonce,
                        Ciphertext = ct,
                        PlainLength = pending.Bytes.Length,
                        PlainSha256 = Convert.ToHexString(SHA256.HashData(pending.Bytes)).ToLowerInvariant()
                    };
                }
            }

            var (wrapNonce, wrapped) = Aead.Seal(masterKey, docKey, Utf8.B(DocKeyAad(host)));
            doc.DocKey = new WrappedKey { Nonce = wrapNonce, Ciphertext = wrapped };
            SealPublic(doc, docKey);
            doc.Seal = seal;
            if (protectedKey is not null)
            {
                doc.IssuedAccessKey = string.IsNullOrEmpty(accessKey) ? NewAccessKey() : accessKey;
                doc.Protected = WrapAccessKey(host, protectedKey, doc.IssuedAccessKey);
            }
            doc.Integrity = ComputeIntegrity(doc);

            var target = paths.SiteDoc(host);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, Serialize(doc));

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
            if (protectedKey is not null) CryptographicOperations.ZeroMemory(protectedKey);
        }
    }

    private static string AccessAad(string host) => $"kickl/v1/access-key|{host}";

    /// <summary>Random, human-passable key (25 base-32 characters in groups of five).</summary>
    public static string NewAccessKey()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var chars = RandomNumberGenerator.GetBytes(25).Select(b => alphabet[b % alphabet.Length]).ToArray();
        return string.Join('-', Enumerable.Range(0, 5).Select(i => new string(chars, i * 5, 5)));
    }

    private static string NormalizeAccessKey(string key) =>
        new(key.Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray());

    private static AccessKeyWrap WrapAccessKey(string host, byte[] protectedKey, string accessKey)
    {
        var salt = RandomNumberGenerator.GetBytes(PinKdf.SaltSize);
        var kek = PinKdf.Derive(NormalizeAccessKey(accessKey), PinKdf.Id, salt, PinKdf.KeySize);
        try
        {
            var (nonce, ct) = Aead.Seal(kek, protectedKey, Utf8.B(AccessAad(host)));
            return new AccessKeyWrap { Kdf = PinKdf.Id, Salt = salt, Nonce = nonce, Ciphertext = ct };
        }
        finally { CryptographicOperations.ZeroMemory(kek); }
    }

    /// <summary>Derive the protected-blob key from the access key. Throws on a wrong key.</summary>
    public static byte[] OpenAccessKey(SealedDocument doc, string accessKey)
    {
        var wrap = doc.Protected ?? throw new InvalidOperationException("This site has no protected content.");
        var kek = PinKdf.Derive(NormalizeAccessKey(accessKey), wrap.Kdf, wrap.Salt, PinKdf.KeySize);
        try { return Aead.Open(kek, wrap.Nonce, wrap.Ciphertext, Utf8.B(AccessAad(doc.Site))); }
        catch (CryptographicException) { throw new CryptographicException("That access key does not open this site's protected content."); }
        finally { CryptographicOperations.ZeroMemory(kek); }
    }

    /// <summary>
    /// Publish the CA's seal with a site that is already stored. The seal is public and outside the integrity hash, so
    /// the document, its blobs and the registry content hash are untouched.
    /// </summary>
    public static bool AttachSeal(DataPaths paths, string host, Trust.CaSeal seal)
    {
        var record = LocalRegistry.Resolve(LocalRegistry.LoadOrEmpty(paths), host);
        var attached = false;
        foreach (var path in DocCandidates(paths, record, host).Where(File.Exists))
        {
            var onDisk = ReadDocumentFile(path);
            if (onDisk is null || !string.Equals(onDisk.Site, host, StringComparison.OrdinalIgnoreCase)) continue;
            onDisk.Seal = seal;
            File.WriteAllText(path, Serialize(onDisk));
            attached = true;
        }
        return attached;
    }

    private static string MarkupAad(string host) => $"kickl/v1/markup|{host}";

    private static byte[] PublicKek(string host, byte[] salt) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, Utf8.B(host.ToLowerInvariant()), 32, salt,
            Utf8.B("kickl/v1/public-content"));

    /// <summary>Wrap the document key under the name-derived key and seal the markup under it.</summary>
    private static void SealPublic(SealedDocument doc, byte[] docKey)
    {
        doc.PublicSalt = RandomNumberGenerator.GetBytes(16);
        var kek = PublicKek(doc.Site, doc.PublicSalt);
        try
        {
            var (n, c) = Aead.Seal(kek, docKey, Utf8.B(DocKeyAad(doc.Site) + "|public"));
            doc.PublicWrap = new WrappedKey { Nonce = n, Ciphertext = c };
        }
        finally { CryptographicOperations.ZeroMemory(kek); }
        var (mn, mc) = Aead.Seal(docKey, Utf8.B(doc.Markup), Utf8.B(MarkupAad(doc.Site)));
        doc.SealedMarkup = new WrappedKey { Nonce = mn, Ciphertext = mc };
    }

    /// <summary>The public-content key, or null for an older package that has none yet.</summary>
    public static byte[]? PublicKeyOf(SealedDocument doc)
    {
        if (doc.PublicWrap.Ciphertext.Length == 0 || doc.PublicSalt.Length == 0) return null;
        var kek = PublicKek(doc.Site, doc.PublicSalt);
        try { return Aead.Open(kek, doc.PublicWrap.Nonce, doc.PublicWrap.Ciphertext, Utf8.B(DocKeyAad(doc.Site) + "|public")); }
        catch (CryptographicException) { return null; }
        finally { CryptographicOperations.ZeroMemory(kek); }
    }

    /// <summary>Serialize for disk: sealed markup is never written next to its ciphertext.</summary>
    public static string Serialize(SealedDocument doc)
    {
        if (doc.SealedMarkup is null) return JsonSerializer.Serialize(doc, Json);
        var copy = JsonSerializer.Deserialize<SealedDocument>(JsonSerializer.Serialize(doc, Json))!;
        copy.Markup = "";
        return JsonSerializer.Serialize(copy, Json);
    }

    private static void OpenMarkup(SealedDocument doc)
    {
        if (doc.SealedMarkup is null) return;
        var key = PublicKeyOf(doc) ?? throw new IntegrityException("The sealed page cannot be opened.");
        try
        {
            doc.Markup = Encoding.UTF8.GetString(
                Aead.Open(key, doc.SealedMarkup.Nonce, doc.SealedMarkup.Ciphertext, Utf8.B(MarkupAad(doc.Site))));
        }
        catch (CryptographicException) { throw new IntegrityException("The sealed page failed authentication."); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    /// <summary>
    /// Older packages were sealed to their author's key alone. The author's session can unwrap the
    /// document key, so the package is opened up for everyone: markup sealed, key wrapped under the
    /// public key. Blob ciphertext, plaintext hashes and the registry content hash are unchanged.
    /// </summary>
    public static void OpenToPublic(DataPaths paths, SealedDocument doc, byte[] docKey)
    {
        var record = LocalRegistry.Resolve(LocalRegistry.LoadOrEmpty(paths), doc.Site);
        foreach (var path in DocCandidates(paths, record, doc.Site).Where(File.Exists))
        {
            var onDisk = ReadDocumentFile(path);
            if (onDisk is null || !onDisk.DocKey.Ciphertext.SequenceEqual(doc.DocKey.Ciphertext)) continue;
            if (onDisk.SealedMarkup is null) SealPublic(onDisk, docKey);
            File.WriteAllText(path, Serialize(onDisk));
            return;
        }
    }

    public static SealedDocument Load(DataPaths paths, string host)
    {
        var registry = LocalRegistry.LoadOrEmpty(paths);
        var record = LocalRegistry.Resolve(registry, host) ?? throw new SiteNotFoundException(host);

        // Several profiles can hold a copy of the same name, and anyone may republish an unclaimed
        // one, so the registry decides: take the copy that matches its content hash. Only when
        // nothing matches is the first copy loaded, and the caller's hash check then reports it.
        SealedDocument? first = null;
        foreach (var path in DocCandidates(paths, record, host).Where(File.Exists))
        {
            var candidate = ReadDocumentFile(path);
            first ??= candidate;
            if (string.Equals(candidate.Integrity, record.ContentHash, StringComparison.OrdinalIgnoreCase))
            {
                first = candidate;
                break;
            }
        }

        var doc = first ?? throw new SiteNotFoundException(host);

        if (!string.Equals(doc.Site, host, StringComparison.OrdinalIgnoreCase))
            throw new IntegrityException("Document site does not match the requested name.");

        OpenMarkup(doc);

        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(doc.Integrity), Convert.FromHexString(ComputeIntegrity(doc))))
            throw new IntegrityException("Document integrity check failed.");

        return doc;
    }

    private static IEnumerable<string> DocCandidates(DataPaths paths, RegistryRecord? record, string host)
    {
        yield return paths.SiteDoc(host);
        // DocFile arrives with registry data. It may only name a file inside the data folder: a rooted path or a ".." would
        // otherwise make the browser read (and the seal/key upgrade rewrite) any file on disk.
        if (record is not null && SafeNames.ResolveUnder(paths.Root, record.DocFile) is { } inside)
            yield return inside;
    }

    public static string ComputeIntegrity(SealedDocument doc)
    {
        // V1 packages predate plaintext hashing and bound the ciphertext instead.
        var legacy = doc.V < 2;
        using var sha = SHA256.Create();
        void Feed(byte[] data) => sha.TransformBlock(data, 0, data.Length, null, 0);

        Feed(Encoding.UTF8.GetBytes(doc.Site));
        Feed(Encoding.UTF8.GetBytes(doc.Markup));
        foreach (var (id, blob) in doc.Blobs.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            Feed(Encoding.UTF8.GetBytes(id));
            Feed(legacy ? blob.Ciphertext : Encoding.UTF8.GetBytes(blob.PlainSha256));
        }
        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    /// <summary>Does the decrypted blob match the plaintext hash the registry hash committed to?</summary>
    public static bool BlobPlaintextMatches(SealedDocument doc, SealedBlob blob, byte[] plain) =>
        doc.V < 2 || CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(plain), Convert.FromHexString(blob.PlainSha256));

    public static string MimeFor(string extension) => extension.ToLowerInvariant() switch
    {
        ".txt" => "text/plain",
        ".md" => "text/markdown",
        ".html" => "text/html",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".svg" => "image/svg+xml",
        ".json" => "application/json",
        ".wasm" => "application/wasm",
        _ => "application/octet-stream"
    };
}
