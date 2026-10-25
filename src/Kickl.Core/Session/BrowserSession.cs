using System.Security.Cryptography;
using System.Text;
using Kickl.Core.Ads;
using Kickl.Core.Crypto;
using Kickl.Core.Documents;
using Kickl.Core.Markup;
using Kickl.Core.Names;
using Kickl.Core.Ownership;
using Kickl.Core.Privacy;
using Kickl.Core.Registry;
using Kickl.Core.Relay;
using Kickl.Core.Util;

namespace Kickl.Core.Session;

public sealed record OwnershipStatus(bool Owned, string? OwnerId, string? BadgeId, string Detail);

public sealed record PfsLookupResult(
    LookupResponse Response,
    string SessionId,
    int ClientKeyBytes,
    int ServerKeyBytes,
    int SaltBytes,
    int SealedQueryBytes,
    int SealedResponseBytes,
    string KeyFingerprint);

/// <summary>
/// One browser session: unlock gate, registry lookup, document load, blob unlock, lock.
/// Mirrors the production split: the session owns keys; the UI only receives fragments.
/// </summary>
public sealed class BrowserSession : IDisposable
{
    private readonly DataPaths _paths;
    private byte[]? _master;
    private SessionSecret? _session;
    private byte[]? _docKey;
    private SealedDocument? _document;
    private JttpUrl? _current;

    public BrowserSession(string dataRoot, string profileId = "local")
    {
        _paths = new DataPaths(dataRoot, profileId);
        _paths.Ensure();
    }

    public DataPaths Paths => _paths;
    public SealedDocument? Document => _document;
    public JttpUrl? Current => _current;
    public bool IsUnlocked => _session?.IsAlive == true;
    public string? SessionId => _session?.IdHex;
    /// <summary>How the last package arrived: local package or direct origin-device tunnel.</summary>
    public string? LastTransport { get; private set; }
    public string? LastTransportFingerprint { get; private set; }

    /// <summary>
    /// The live session secret. Callers must not retain it or copy it into a cache;
    /// it is exposed so the cache/region layers can derive subkeys.
    /// </summary>
    public SessionSecret SessionSecret =>
        _session ?? throw new InvalidOperationException("The session is locked.");

    /// <summary>P-256 session identity, regenerated on every unlock and destroyed on Lock().</summary>
    public byte[] SessionPublicKeySpki => SessionSecret.SessionPublicKeySpki;

    public bool HasProfile => Keystore.Exists(_paths);

    /// <summary>Encrypt private profile data with a purpose-separated key derived from the master key.</summary>
    public byte[] ProtectProfileData(string purpose, ReadOnlySpan<byte> plaintext)
    {
        if (_master is null) throw new InvalidOperationException("Unlock the profile before accessing private data.");
        return ProfileDataCipher.Protect(_master, purpose, plaintext);
    }

    /// <summary>Authenticate and decrypt private profile data previously protected for this purpose.</summary>
    public byte[] OpenProfileData(string purpose, ReadOnlySpan<byte> envelopeBytes)
    {
        if (_master is null) throw new InvalidOperationException("Unlock the profile before accessing private data.");
        return ProfileDataCipher.Open(_master, purpose, envelopeBytes);
    }

    public void CreateProfile(string pin)
    {
        if (HasProfile) throw new InvalidOperationException("A profile already exists.");
        Keystore.Create(_paths, pin);
    }

    public void Unlock(string pin)
    {
        if (IsUnlocked) return;
        _master = Keystore.Unlock(_paths, pin);
        _session = SessionSecret.Create(_master);
    }

    public PageView Open(string address, string? pin = null)
    {
        if (!JttpUrl.TryParse(address, out var url, out var error))
            throw new ArgumentException(error ?? "Invalid jttp address.", nameof(address));

        if (pin is not null) Unlock(pin);

        var lookup = Lookup(url!.Host);
        if (!lookup.Response.Found) throw new SiteNotFoundException(url.Host);
        if (string.Equals(lookup.Response.Status, "closed", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"jttp://{url.Host}/ was closed by its owner.");

        var doc = LoadPackage(lookup.Response, url.Host, out var transport, out var fingerprint);
        VerifyContentHash(url.Host, lookup.Response.ContentHash, doc.Integrity);
        _document = doc;
        _current = url;
        LastTransport = transport;
        LastTransportFingerprint = fingerprint;

        var page = Kml.Parse(_document.Markup, url.Host);
        Kml.Enrich(page, _document);
        return page;
    }

    /// <summary>
    /// Resolve a name over the PFS lookup channel: fresh P-256 ECDH, HKDF-SHA384,
    /// AES-256-GCM, one session key, destroyed when the lookup returns.
    /// </summary>
    public PfsLookupResult Lookup(string host)
    {
        var registry = LocalRegistry.LoadOrEmpty(_paths);
        using var server = new PfsRegistryServer(registry);
        using var client = new PfsLookupClient();

        var clientKey = client.ClientPublicKeySpki;
        var hello = server.Initiate(clientKey);
        client.Establish(hello);
        var fingerprint = client.KeyFingerprint();

        var sealedQuery = client.Query(host);
        var sealedResponse = server.HandleQuery(hello.SessionId, sealedQuery);
        var response = client.ReadResponse(sealedResponse);

        server.Invalidate(hello.SessionId);

        return new PfsLookupResult(
            response,
            hello.SessionId,
            clientKey.Length,
            hello.ServerPublicKeySpki.Length,
            hello.Salt.Length,
            sealedQuery.Nonce.Length + sealedQuery.Ciphertext.Length,
            sealedResponse.Nonce.Length + sealedResponse.Ciphertext.Length,
            fingerprint);
    }

    /// <summary>
    /// Search the public live index over the same PFS channel. The tag/query and the
    /// result list are inside AES-256-GCM; the observer sees only lengths and timing.
    /// Search never opens anything: a hit must be confirmed by the user before Open().
    /// </summary>
    public SearchResult Search(string query)
    {
        var registry = LocalRegistry.LoadOrEmpty(_paths);
        using var server = new PfsRegistryServer(registry);
        using var client = new PfsLookupClient();

        var hello = server.Initiate(client.ClientPublicKeySpki);
        client.Establish(hello);
        var sealedQuery = client.Search(query);
        var sealedResponse = server.HandleQuery(hello.SessionId, sealedQuery);
        var result = client.ReadSearch(sealedResponse);
        server.Invalidate(hello.SessionId);
        return result;
    }

    /// <summary>
    /// A site can be published by the browser itself: the template is turned into KML, sealed,
    /// and registered. A jttp://name.rss document is the next story in that feed.
    /// </summary>
    public PageView CreateSite(StoryDraft draft)
    {
        if (_master is null) throw new InvalidOperationException("Unlock the profile before publishing.");
        SiteComposer.Publish(_paths, draft, _master);
        var host = draft.Host.Trim().ToLowerInvariant();
        return Open("jttp://" + host + "/");
    }

    public IReadOnlyList<NicklesPublisher> NicklesPublishers() =>
        Nickles.Publishers(_paths, _master ?? throw new InvalidOperationException("Unlock the profile first."));

    /// <summary>Mint (or recover) the NCKL placement for one site and ad space.</summary>
    public NicklesPublisher CreateNicklesPublisher(string site, string space)
    {
        if (_master is null) throw new InvalidOperationException("Unlock the profile before minting a publisher.");
        return Nickles.CreatePublisher(_paths, _master, site, space);
    }

    /// <summary>Resolve the current signed NCKL creative for a placement in the open document.</summary>
    public NicklesSlot NicklesSlot(NicklesPlacement placement)
    {
        if (_master is null) throw new InvalidOperationException("Unlock the profile before serving an ad.");
        return Nickles.ResolveSlot(_paths, _master, placement);
    }

    public NicklesSlot NicklesHomeSlot()
    {
        if (_master is null) throw new InvalidOperationException("Unlock the profile before serving an ad.");
        return Nickles.ResolveHomeSlot(_master);
    }

    /// <summary>
    /// Fetch the package from the origin device rather than reading the local mirror. The
    /// registry published the origin's RSA key; the tunnel wraps a fresh AES key with RSA-OAEP,
    /// splits request/response keys with HKDF, and authenticates both frames with AES-256-GCM.
    /// A locked session has no private key, so it falls back to the local package.
    /// </summary>
    private SealedDocument LoadPackage(LookupResponse lookup, string host,
        out string transport, out string fingerprint)
    {
        var siblingOrigin = OriginIsAnotherLocalProfile(lookup.OriginDeviceId);
        if (_master is not null
            && !siblingOrigin
            && !string.IsNullOrEmpty(lookup.OriginDeviceId)
            && !string.IsNullOrEmpty(lookup.DirectPublicKey))
        {
            try
            {
                using var publisher = DirectPublisher.Open(_paths, _master);
                var peer = new DirectPeer(lookup.OriginDeviceId, B64u.Decode(lookup.DirectPublicKey));
                using var exchange = DirectFetchClient.Begin(peer, host);
                var response = publisher.Respond(exchange.Request);
                var result = exchange.Complete(response);
                transport = result.Transport;
                fingerprint = result.Fingerprint;
                return result.Document;
            }
            catch (Exception ex)
            {
                throw new DirectFetchException(
                    "The origin device did not deliver the article over the direct tunnel.", ex);
            }
        }

        transport = siblingOrigin
            ? "local sealed package · origin is another profile on this device"
            : "local sealed package";
        fingerprint = "";
        return DocumentStore.Load(_paths, host);
    }

    /// <summary>
    /// True when the name was published by a different profile on this same device (the stock
    /// demo sites are published once, before any new profile exists). That profile's relay key is
    /// wrapped under its own master key, so the tunnel cannot be opened from here; the sealed
    /// package is already on this disk and is read locally after the registry hash check.
    /// </summary>
    private bool OriginIsAnotherLocalProfile(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId)) return false;
        foreach (var profileId in ProfileCatalog.List(_paths.Root))
        {
            if (string.Equals(profileId, _paths.ProfileId, StringComparison.OrdinalIgnoreCase)) continue;
            if (DeviceRelayKey.TryReadDeviceId(new DataPaths(_paths.Root, profileId), out var local)
                && string.Equals(local, deviceId, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Check the package against the registry's SHA-256 before parsing or rendering it.
    /// A mismatch counts as an attack report: increment the record's counter and fail closed.
    /// </summary>
    private void VerifyContentHash(string host, string expected, string actual)
    {
        if (string.IsNullOrEmpty(expected)) return; // legacy record, nothing to compare

        byte[] expectedBytes = [];
        byte[] actualBytes = [];
        try
        {
            expectedBytes = Convert.FromHexString(expected);
            actualBytes = Convert.FromHexString(actual);
        }
        catch (FormatException)
        {
            // fall through to the mismatch path
        }

        if (expectedBytes.Length == 32 && actualBytes.Length == 32 &&
            CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes))
            return;

        var count = LocalRegistry.NoteContentMismatch(_paths, host);
        throw new ContentIntegrityException(host, expected, actual, count);
    }

    public bool IsNameVerified(string host)
    {
        var record = LocalRegistry.Resolve(LocalRegistry.LoadOrEmpty(_paths), host);
        return record is not null && LocalRegistry.IsVerified(record);
    }

    public OwnershipStatus OwnershipOf(string host)
    {
        var record = LocalRegistry.Resolve(LocalRegistry.LoadOrEmpty(_paths), host);
        if (record is null || !LocalRegistry.IsOwned(record))
            return new OwnershipStatus(false, null, null, "no paid title");

        var title = OwnershipAuthority.LoadTitle(_paths, host);
        if (title is null || title.BadgeId != record.BadgeId)
            return new OwnershipStatus(false, record.OwnerId, null, "badge record missing");
        if (title.Status is "closed" or "transferred")
            return new OwnershipStatus(false, title.OwnerId, null, title.Status);
        if (DateTimeOffset.TryParse(title.ExpiresUtc, out var expires) && expires < DateTimeOffset.UtcNow)
            return new OwnershipStatus(false, title.OwnerId, title.BadgeId, "title lapsed (renew at secupgrade.com)");

        return new OwnershipStatus(true, title.OwnerId, title.BadgeId, "badge active");
    }

    public string UnlockBlob(string blobId, string? pin = null)
    {
        var plain = UnlockBlobBytes(blobId, pin);
        try
        {
            var text = Encoding.UTF8.GetString(plain);
            if (text.Contains('\uFFFD'))
                throw new InvalidOperationException("Binary blob; use UnlockBlobBytes for images.");
            return text;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    /// <summary>Decrypt a blob and return its bytes. The caller owns and must zero the array.</summary>
    public byte[] UnlockBlobBytes(string blobId, string? pin = null)
    {
        if (!IsUnlocked)
        {
            if (pin is null) throw new InvalidOperationException("The session is locked. Supply the user key.");
            Unlock(pin);
        }

        var doc = _document ?? throw new InvalidOperationException("Open a document first.");
        if (!doc.Blobs.TryGetValue(blobId, out var blob))
            throw new KeyNotFoundException($"Blob '{blobId}' is not part of this document.");

        _docKey ??= UnwrapDocKey(doc);

        var aad = Encoding.UTF8.GetBytes($"kickl/v1/blob|{doc.Site}|{blobId}");
        try
        {
            return Aead.Open(_docKey, blob.Nonce, blob.Ciphertext, aad);
        }
        catch (CryptographicException)
        {
            throw new CryptographicException("The blob failed authentication (wrong key, wrong session, or tampered).");
        }
    }

    private byte[] UnwrapDocKey(SealedDocument doc)
    {
        if (_master is null) throw new InvalidOperationException("The session is locked.");
        var aad = Encoding.UTF8.GetBytes($"kickl/v1/doc-key|{doc.Site}");
        return Aead.Open(_master, doc.DocKey.Nonce, doc.DocKey.Ciphertext, aad);
    }

    public void Lock()
    {
        _session?.Dispose();
        _session = null;
        if (_master is not null) CryptographicOperations.ZeroMemory(_master);
        _master = null;
        if (_docKey is not null) CryptographicOperations.ZeroMemory(_docKey);
        _docKey = null;
    }

    public void Dispose() => Lock();

    /// <summary>Slice keystroke demo: type into a shielded field and reveal once.</summary>
    public static string ShieldRoundTrip(SessionSecret session, string fieldId, string text)
    {
        using var shield = new KeystrokeShield(session, fieldId);
        shield.Append(text);
        var chars = shield.Reveal();
        try { return new string(chars); }
        finally { Array.Clear(chars); }
    }
}
