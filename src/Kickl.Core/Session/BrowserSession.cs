using System.Security.Cryptography;
using System.Text;
using Kickl.Core.Ads;
using Kickl.Core.Crypto;
using Kickl.Core.Documents;
using Kickl.Core.Games;
using Kickl.Core.Markup;
using Kickl.Core.Names;
using Kickl.Core.Ownership;
using Kickl.Core.Privacy;
using Kickl.Core.Registry;
using Kickl.Core.Relay;
using Kickl.Core.Trust;
using Kickl.Core.Util;

namespace Kickl.Core.Session;

public sealed record OwnershipStatus(bool Owned, string? OwnerId, string? BadgeId, string Detail,
    bool Verified = false, string VerifiedDetail = "");

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
    private byte[]? _protectedKey;
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
        if (_docKey is not null) CryptographicOperations.ZeroMemory(_docKey);
        _docKey = null; // the keys belong to the previous document
        if (_protectedKey is not null) CryptographicOperations.ZeroMemory(_protectedKey);
        _protectedKey = null;
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
        var host = draft.Host.Trim().ToLowerInvariant();
        SiteClaims.EnsureMayPublish(_paths, host, HoldsCert(host));
        SiteComposer.Publish(_paths, draft, _master, HeldCert(host)?.Seal);
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
        var status = OwnershipCore(host);
        var check = OwnershipVerification(host);
        return status with { Verified = check.Verified, VerifiedDetail = check.Reason };
    }

    /// <summary>
    /// Is this name's ownership sealed by the Kickl CA? The seal comes from the page itself (so every visitor can
    /// check it, not only the owner) or from the owner's own certificate. It must chain to the CA root built into
    /// this browser, be in the CA's transparency log, and not be revoked.
    /// </summary>
    public SealCheck OwnershipVerification(string host)
    {
        host = host.ToLowerInvariant();
        var seal = _document is { } d && string.Equals(d.Site, host, StringComparison.OrdinalIgnoreCase) ? d.Seal : null;
        seal ??= HeldCert(host)?.Seal;
        if (seal is null) return SealCheck.No("not sealed by the CA");

        var root = CaPins.Root(_paths);
        if (root is null) return SealCheck.No("this build has no CA root to check against");

        var title = SiteClaims.ActiveTitle(_paths, host);
        var check = CaTrust.VerifySeal(root, seal, host, DateTimeOffset.UtcNow, CaPins.CachedRevocations(_paths), title?.Seq);
        return check.Verified && CaPins.TestRootInUse ? new SealCheck(true, "TEST CA: sealed by a throwaway test CA, not the real Kickl CA") : check;
    }

    /// <summary>Refresh the CA's revocation list in the background; a failure leaves the one already held in force.</summary>
    public async Task RefreshCaRevocationsAsync(HttpClient? http = null, CancellationToken ct = default)
    {
        var own = http is null;
        http ??= new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        try { await CaPins.RefreshRevocationsAsync(_paths, http, DateTimeOffset.UtcNow, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { }
        finally { if (own) http.Dispose(); }
    }

    /// <summary>
    /// Send this profile's shop receipt for <paramref name="host"/> to the CA. The CA checks it, logs it, and returns
    /// a countersigned seal. The seal is checked here against the pinned root before it is kept, then published with
    /// the site so every visitor sees the VERIFIED OWNER badge.
    /// </summary>
    public async Task<SealCheck> SubmitToCaAsync(string host, HttpClient? http = null, CancellationToken ct = default)
    {
        host = host.ToLowerInvariant();
        var cert = HeldCert(host) ?? throw new ClaimException(
            $"Import the ownership certificate for jttp://{host}/ first; the CA seals receipts, not names.");
        var root = CaPins.Root(_paths) ?? throw new ClaimException("This build has no CA root, so it cannot check the CA's answer.");

        var own = http is null;
        http ??= new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        try
        {
            await CaPins.RefreshRevocationsAsync(_paths, http, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
            var seal = await CaClient.CountersignAsync(http, CaPins.CaUrl(), cert, ct);

            var check = CaTrust.VerifySeal(root, seal, host, DateTimeOffset.UtcNow, CaPins.CachedRevocations(_paths), cert.Seq);
            if (!check.Verified) throw new ClaimException("The CA's answer did not verify, so it was discarded: " + check.Reason);

            cert.Seal = seal;
            SaveCert(cert);
            DocumentStore.AttachSeal(_paths, host, seal);
            if (_document is { } d && string.Equals(d.Site, host, StringComparison.OrdinalIgnoreCase)) d.Seal = seal;
            return check;
        }
        catch (HttpRequestException ex)
        {
            throw new ClaimException("Could not reach the CA: " + ex.Message);
        }
        finally
        {
            if (own) http.Dispose();
        }
    }

    private OwnershipStatus OwnershipCore(string host)
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

    // ------------------------------------------------------------------ site claims

    /// <summary>The BTC shop page that sells the claim, prefilled with this name.</summary>
    public string ClaimShopLink(string host) => SiteClaims.ClaimLink(SiteClaims.ShopUrl(), host.ToLowerInvariant());

    private string CertsPath => Path.Combine(_paths.ProfileRoot, "private", "certs.kpf");

    private List<OwnershipCert> LoadCerts()
    {
        if (!File.Exists(CertsPath)) return [];
        var plain = OpenProfileData("ownership-certs", File.ReadAllBytes(CertsPath));
        try { return System.Text.Json.JsonSerializer.Deserialize<List<OwnershipCert>>(plain) ?? []; }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private void SaveCert(OwnershipCert cert)
    {
        var list = LoadCerts();
        list.RemoveAll(c => c.Site == cert.Site && c.Seq <= cert.Seq);
        list.Add(cert);
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(list);
        Directory.CreateDirectory(Path.GetDirectoryName(CertsPath)!);
        File.WriteAllBytes(CertsPath, ProtectProfileData("ownership-certs", bytes));
        CryptographicOperations.ZeroMemory(bytes);
    }

    /// <summary>The newest certificate this profile has for a name, if it is still the current claim.</summary>
    private OwnershipCert? HeldCert(string host)
    {
        if (!IsUnlocked) return null;
        var title = SiteClaims.ActiveTitle(_paths, host);
        return title is null ? null
            : LoadCerts().FirstOrDefault(c => string.Equals(c.Site, host, StringComparison.OrdinalIgnoreCase)
                                              && c.Seq == title.Seq && !c.IsExpired);
    }

    public bool HoldsCert(string host) => HeldCert(host) is not null;

    /// <summary>Is the name claimed, and may this profile edit it (does it hold the current certificate)?</summary>
    public (bool Claimed, bool MayEdit) EditAccess(string host)
    {
        if (SiteClaims.ActiveTitle(_paths, host) is null) return (false, true);
        return (true, HoldsCert(host));
    }

    /// <summary>A copy of the certificate this profile holds, to save as a file.</summary>
    public byte[] ExportCert(string host) =>
        HeldCert(host)?.ToBytes() ?? throw new ClaimException($"This profile holds no certificate for jttp://{host}/.");

    /// <summary>
    /// A site asked for proof of ownership at /edit. The certificate is checked against the pinned shop key
    /// (fetched once if this device has never met the shop) and remembered, encrypted, in this profile.
    /// </summary>
    public async Task<OwnershipCert> ImportCertAsync(string host, byte[] file, HttpClient? http = null, CancellationToken ct = default)
    {
        if (!IsUnlocked) throw new InvalidOperationException("Unlock the profile first.");
        host = host.ToLowerInvariant();
        var cert = OwnershipCert.Parse(file);
        if (!string.Equals(cert.Site, host, StringComparison.OrdinalIgnoreCase))
            throw new ClaimException($"This certificate is for jttp://{cert.Site}/, not jttp://{host}/.");

        var spki = SiteClaims.PinnedShopKey(_paths);
        if (spki is null)
        {
            var own = http is null;
            http ??= new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            try { spki = await SiteClaims.FetchShopKeyAsync(http, SiteClaims.ShopUrl(), ct); }
            catch (HttpRequestException ex)
            {
                throw new ClaimException("This device has not met the BTC shop yet and cannot reach it to learn its signing key: " + ex.Message);
            }
            finally { if (own) http.Dispose(); }
        }

        SiteClaims.Accept(_paths, cert, spki, host);
        SaveCert(cert);

        // A certificate that comes back from the CA already sealed: publish the seal with the site, but only if it
        // checks out against the root built into this browser.
        if (cert.Seal is { } seal && CaPins.Root(_paths) is { } root
            && CaTrust.VerifySeal(root, seal, host, DateTimeOffset.UtcNow, CaPins.CachedRevocations(_paths), cert.Seq).Verified)
        {
            DocumentStore.AttachSeal(_paths, host, seal);
            if (_document is { } d && string.Equals(d.Site, host, StringComparison.OrdinalIgnoreCase)) d.Seal = seal;
        }
        return cert;
    }

    public string UnlockBlob(string blobId, string? pin = null, string? accessKey = null)
    {
        var plain = UnlockBlobBytes(blobId, pin, accessKey);
        try
        {
            var text = Encoding.UTF8.GetString(plain);
            if (text.Contains('�'))
                throw new InvalidOperationException("Binary blob; use UnlockBlobBytes for images.");
            return text;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    /// <summary>
    /// Decrypt a blob and return its bytes. The caller owns and must zero the array. Public blobs
    /// open for any reader with an unlocked session; protected blobs also need the site's access key.
    /// </summary>
    public byte[] UnlockBlobBytes(string blobId, string? pin = null, string? accessKey = null)
    {
        if (!IsUnlocked)
        {
            if (pin is null) throw new InvalidOperationException("The session is locked. Supply the user key.");
            Unlock(pin);
        }

        var doc = _document ?? throw new InvalidOperationException("Open a document first.");
        if (!doc.Blobs.TryGetValue(blobId, out var blob))
            throw new KeyNotFoundException($"Blob '{blobId}' is not part of this document.");

        byte[] key;
        if (blob.Access == "protected")
        {
            if (_protectedKey is null)
            {
                if (string.IsNullOrWhiteSpace(accessKey))
                    throw new AccessKeyRequiredException(doc.Site);
                _protectedKey = DocumentStore.OpenAccessKey(doc, accessKey);
            }
            key = _protectedKey;
        }
        else
        {
            key = _docKey ??= PublicKeyOf(doc);
        }

        var aad = Encoding.UTF8.GetBytes($"kickl/v1/blob|{doc.Site}|{blobId}");
        byte[] plain;
        try
        {
            plain = Aead.Open(key, blob.Nonce, blob.Ciphertext, aad);
        }
        catch (CryptographicException)
        {
            throw new CryptographicException("The blob failed authentication (wrong key, wrong session, or tampered).");
        }

        if (!DocumentStore.BlobPlaintextMatches(doc, blob, plain))
        {
            CryptographicOperations.ZeroMemory(plain);
            throw new CryptographicException("The blob does not match the content hash the registry published.");
        }
        return plain;
    }

    // ------------------------------------------------------------------ games

    /// <summary>
    /// Unclaimed names are free, so their games get tight ceilings. A claimed name has paid, so it may run
    /// bigger games. Neither limit can be removed by the game.
    /// </summary>
    public GameLimits GameLimitsFor(string host) => SiteClaims.ActiveTitle(_paths, host) is null
        ? new GameLimits(MaxModuleBytes: 512 * 1024, MaxMemoryBytes: 8L << 20, InitFuel: 100_000_000, FrameFuel: 10_000_000)
        : new GameLimits(MaxModuleBytes: 2 * 1024 * 1024, MaxMemoryBytes: 32L << 20, InitFuel: 400_000_000, FrameFuel: 40_000_000);

    /// <summary>
    /// Open the sealed wasm blob <paramref name="blobId"/> of the current page as a game, restore this reader's
    /// save for it, and return it ready for Start(). The caller disposes the game.
    /// </summary>
    public WasmGame LoadGame(string blobId, string? accessKey = null)
    {
        var doc = _document ?? throw new InvalidOperationException("Open a document first.");
        if (!doc.Blobs.TryGetValue(blobId, out var blob))
            throw new KeyNotFoundException($"Blob '{blobId}' is not part of this document.");
        if (blob.Mime != "application/wasm")
            throw new GameRejectedException($"Blob '{blobId}' is {blob.Mime}, not a WebAssembly game.");

        var bytes = UnlockBlobBytes(blobId, null, accessKey);
        try
        {
            var game = WasmGame.Load(bytes, GameLimitsFor(doc.Site));
            try
            {
                game.RestoreSave(LoadGameSave(doc.Site, blobId));
                return game;
            }
            catch
            {
                game.Dispose();
                throw;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private string GameSavesPath => Path.Combine(_paths.ProfileRoot, "private", "game-saves.kpf");

    private Dictionary<string, byte[]> LoadGameSaves()
    {
        if (!File.Exists(GameSavesPath)) return new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var plain = OpenProfileData("game-saves", File.ReadAllBytes(GameSavesPath));
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, byte[]>>(plain)
                   ?? new Dictionary<string, byte[]>(StringComparer.Ordinal);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public byte[] LoadGameSave(string site, string blobId) =>
        LoadGameSaves().TryGetValue($"{site}|{blobId}", out var data) ? data : [];

    /// <summary>Keep a game's save bytes, encrypted, in this profile only. At most 64 saves of 4 KiB each.</summary>
    public void SaveGameData(string site, string blobId, byte[] data)
    {
        if (data.Length > 4096) data = data[..4096];
        var all = LoadGameSaves();
        var key = $"{site}|{blobId}";
        if (data.Length == 0) all.Remove(key);
        else if (all.ContainsKey(key) || all.Count < 64) all[key] = data;
        else return;

        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(all);
        Directory.CreateDirectory(Path.GetDirectoryName(GameSavesPath)!);
        File.WriteAllBytes(GameSavesPath, ProtectProfileData("game-saves", bytes));
        CryptographicOperations.ZeroMemory(bytes);
    }

    private byte[] PublicKeyOf(SealedDocument doc)
    {
        var shared = DocumentStore.PublicKeyOf(doc);
        if (shared is not null) return shared;

        // Older package, sealed to its author alone. If this profile is that author, open it up
        // for everyone; otherwise it stays closed until the author's profile opens it once.
        if (_master is null) throw new InvalidOperationException("The session is locked.");
        var aad = Encoding.UTF8.GetBytes($"kickl/v1/doc-key|{doc.Site}");
        byte[] key;
        try
        {
            key = Aead.Open(_master, doc.DocKey.Nonce, doc.DocKey.Ciphertext, aad);
        }
        catch (CryptographicException)
        {
            throw new CryptographicException(
                $"jttp://{doc.Site}/ is an older package that only its author's profile can open. " +
                "Open it once from the profile that created it and it becomes readable by everyone.");
        }
        DocumentStore.OpenToPublic(_paths, doc, key);
        return key;
    }

    public void Lock()
    {
        _session?.Dispose();
        _session = null;
        if (_master is not null) CryptographicOperations.ZeroMemory(_master);
        _master = null;
        if (_docKey is not null) CryptographicOperations.ZeroMemory(_docKey);
        _docKey = null;
        if (_protectedKey is not null) CryptographicOperations.ZeroMemory(_protectedKey);
        _protectedKey = null;
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
