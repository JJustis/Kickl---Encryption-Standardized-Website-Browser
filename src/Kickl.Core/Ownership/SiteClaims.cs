using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Kickl.Core.Crypto;
using Kickl.Core.Registry;
using Kickl.Core.Util;

namespace Kickl.Core.Ownership;

public sealed class ClaimException(string message) : Exception(message);

/// <summary>
/// The file the buyer downloads from the BTC shop after paying. It is the shop's signed statement that one
/// name belongs to whoever holds it; the secret token inside is what makes the holder the owner. Whoever
/// holds the file can edit the name, so it should be kept like a key.
/// </summary>
public sealed class OwnershipCert
{
    public const string Extension = ".kcert";

    public string Kickl { get; set; } = "ownership-cert";
    public int V { get; set; } = 1;
    public string Site { get; set; } = "";
    public long Order { get; set; }
    public int Seq { get; set; }
    public long PaidAt { get; set; }
    public long Expires { get; set; }
    public string Token { get; set; } = "";
    public string? Txid { get; set; }
    public string Sig { get; set; } = "";

    /// <summary>The CA's countersignature, once the owner has had this receipt sealed. Not part of what the shop signed.</summary>
    public Trust.CaSeal? Seal { get; set; }

    /// <summary>The owner's binding, sent to the CA with the receipt so it is sealed in. Not part of what the shop signed.</summary>
    public string? OwnerSalt { get; set; }
    public string? OwnerBinding { get; set; }
    public string? OwnerKdf { get; set; }

    /// <summary>
    /// An owner binding the buyer gave the SHOP when ordering. Unlike the three fields above it is part of what the shop signs, so the
    /// shop (not whoever seals first) decides it, and it travels inside the signed receipt where it cannot be stripped or replaced.
    /// </summary>
    public string? BoundKdf { get; set; }
    public string? BoundSalt { get; set; }
    public string? BoundBinding { get; set; }

    public bool IsShopBound => !string.IsNullOrEmpty(BoundBinding);

    public static string FileName(string site) => $"jttp-owner-{site}{Extension}";

    public bool IsExpired => DateTimeOffset.FromUnixTimeSeconds(Expires) <= DateTimeOffset.UtcNow;

    // camelCase, the same as the file the shop hands out, so the CA's web front reads either one.
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public byte[] ToBytes() => System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this, Json));

    public static OwnershipCert Parse(byte[] file)
    {
        OwnershipCert? cert;
        try { cert = JsonSerializer.Deserialize<OwnershipCert>(file, Json); }
        catch (JsonException) { cert = null; }
        // A hostile file can say "site": null; treat a missing text field as empty rather than crash
        if (cert is not { Kickl: "ownership-cert", V: 1 } || (cert.Site?.Length ?? 0) == 0 || cert.Token?.Length != 64
            || (cert.Sig?.Length ?? 0) == 0)
            throw new ClaimException("That is not a Kickl ownership certificate.");
        if (cert.IsShopBound && (cert.BoundKdf != global::Kickl.Core.Ownership.OwnerBinding.WebKdfShop || !global::Kickl.Core.Ownership.OwnerBinding.IsWellFormed(cert.BoundSalt, cert.BoundBinding, cert.BoundKdf)))
            throw new ClaimException("That certificate's owner binding is malformed.");
        return cert;
    }

    /// <summary>The exact text the shop signed.</summary>
    public string Payload() =>
        string.Join('|', "kickl/v1/site-claim", Site, Order, Seq, PaidAt, Expires, Token)
        + (IsShopBound ? $"|bound:{BoundKdf}:{BoundSalt}:{BoundBinding}" : "");
}

/// <summary>
/// Ties a claim to the person, not just the file. The owner picks a passphrase when the receipt is sealed; the browser sends the CA
/// <c>HMAC-SHA256(Argon2id(passphrase, salt), "site|seq|token commitment")</c> and the CA signs and logs it inside the seal. The CA
/// never sees the passphrase or the token's owner. After that, holding the .kcert is not enough to edit the name: the editor must also
/// give the passphrase, which is checked against the sealed binding. A stolen certificate file is useless without it.
/// </summary>
public static class OwnerBinding
{
    public const int MinPassphrase = 8;

    private static byte[] Message(string site, int seq, string commitment) =>
        Utf8.B($"kickl/v1/owner-binding|{site.ToLowerInvariant()}|{seq}|{commitment}");

    public static (string Salt, string Binding) Create(string passphrase, string site, int seq, string tokenCommitment)
    {
        if (passphrase.Length < MinPassphrase)
            throw new ClaimException($"The owner passphrase must be at least {MinPassphrase} characters.");
        var salt = RandomNumberGenerator.GetBytes(PinKdf.SaltSize);
        return (B64u.Encode(salt), Compute(passphrase, salt, site, seq, tokenCommitment));
    }

    /// <summary>The key derivation the CA's web page uses, because a page can only call what WebCrypto offers.</summary>
    public const string WebKdf = "pbkdf2-sha256-600k";

    /// <summary>The same derivation, but the message is just the site name, because the buyer sets it before the shop has issued a token or claim number.</summary>
    public const string WebKdfShop = "pbkdf2-sha256-600k-shop";

    /// <summary>What the shop page binds: HMAC(PBKDF2(passphrase, salt), "kickl/v1/owner-binding-shop|site"). Used by tests and tools.</summary>
    public static (string Kdf, string Salt, string Binding) CreateForShop(string passphrase, string site)
    {
        if (passphrase.Length < MinPassphrase)
            throw new ClaimException($"The owner passphrase must be at least {MinPassphrase} characters.");
        var salt = RandomNumberGenerator.GetBytes(PinKdf.SaltSize);
        return (WebKdfShop, B64u.Encode(salt), Compute(passphrase, salt, site, 0, "", WebKdfShop));
    }

    private static string Compute(string passphrase, byte[] salt, string site, int seq, string commitment, string kdf = "")
    {
        var key = kdf is WebKdf or WebKdfShop
            ? Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, 600_000, HashAlgorithmName.SHA256, 32)
            : PinKdf.Derive(passphrase, salt);
        var message = kdf == WebKdfShop ? Utf8.B("kickl/v1/owner-binding-shop|" + site.ToLowerInvariant()) : Message(site, seq, commitment);
        try { return Convert.ToHexString(HMACSHA256.HashData(key, message)).ToLowerInvariant(); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    /// <summary>Does <paramref name="passphrase"/> match the binding the CA sealed into <paramref name="seal"/>?</summary>
    public static bool Verify(Trust.CaSeal seal, string passphrase)
    {
        if (seal.OwnerBinding.Length == 0) return true;   // nothing was bound
        if (passphrase.Length == 0) return false;
        try
        {
            var actual = Compute(passphrase, B64u.Decode(seal.OwnerSalt), seal.Site, seal.Seq, seal.TokenCommitment, seal.OwnerKdf);
            return CryptographicOperations.FixedTimeEquals(Utf8.B(actual), Utf8.B(seal.OwnerBinding));
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException) { return false; }
    }

    /// <summary>
    /// Is the owner passphrase right for this certificate? The shop's signed binding decides when there is one; otherwise the seal's.
    /// A certificate with neither is not bound, and needs nothing.
    /// </summary>
    public static bool Verify(OwnershipCert cert, string passphrase)
    {
        if (cert.IsShopBound)
        {
            if (passphrase.Length == 0) return false;
            try
            {
                var commitment = Convert.ToHexString(SHA256.HashData(Utf8.B(cert.Token))).ToLowerInvariant();
                var actual = Compute(passphrase, B64u.Decode(cert.BoundSalt!), cert.Site, cert.Seq, commitment, cert.BoundKdf!);
                return CryptographicOperations.FixedTimeEquals(Utf8.B(actual), Utf8.B(cert.BoundBinding!));
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException) { return false; }
        }
        return cert.Seal is null || Verify(cert.Seal, passphrase);
    }

    public static bool IsBound(OwnershipCert cert) => cert.IsShopBound || cert.Seal is { OwnerBinding.Length: > 0 };

    /// <summary>The CA checks only that the binding is well-formed; it cannot check the passphrase.</summary>
    public static bool IsWellFormed(string? salt, string? binding, string? kdf = null) =>
        (string.IsNullOrEmpty(kdf) || kdf is WebKdf or WebKdfShop)
        && salt is { Length: > 0 and <= 64 } && binding is { Length: 64 } && binding.All(Uri.IsHexDigit)
        && salt.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}

/// <summary>
/// A passphrase-protected copy of an ownership certificate. The plain .kcert is a bearer credential: anyone who copies the file can
/// edit the name. A sealed copy is Argon2id + AES-256-GCM under a passphrase, and is bound to the site name, so a stolen backup is
/// useless without the passphrase. The shop's plain file is still accepted on import; only copies made by the browser are sealed.
/// </summary>
public static class SealedCert
{
    private const string Marker = "ownership-cert-sealed";
    private const int MinPassphrase = 8;

    private sealed class Wire
    {
        public string Kickl { get; set; } = Marker;
        public int V { get; set; } = 1;
        public string Site { get; set; } = "";
        public string Kdf { get; set; } = PinKdf.Id;
        public string Salt { get; set; } = "";
        public string Nonce { get; set; } = "";
        public string Ct { get; set; } = "";
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static byte[] Aad(string site) => Utf8.B("kickl/v1/sealed-cert|" + site.ToLowerInvariant());

    public static bool IsSealed(byte[] file)
    {
        try { return JsonSerializer.Deserialize<Wire>(file, Json) is { Kickl: Marker }; }
        catch (JsonException) { return false; }
    }

    public static byte[] Seal(OwnershipCert cert, string passphrase)
    {
        if (passphrase.Length < MinPassphrase)
            throw new ClaimException($"Choose a passphrase of at least {MinPassphrase} characters to protect the copy.");
        var salt = RandomNumberGenerator.GetBytes(PinKdf.SaltSize);
        var key = PinKdf.Derive(passphrase, salt);
        var plain = cert.ToBytes();
        try
        {
            var (nonce, ct) = Aead.Seal(key, plain, Aad(cert.Site));
            return JsonSerializer.SerializeToUtf8Bytes(new Wire
            {
                Site = cert.Site, Salt = B64u.Encode(salt), Nonce = B64u.Encode(nonce), Ct = B64u.Encode(ct)
            }, Json);
        }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(plain); }
    }

    /// <summary>The plain certificate bytes inside a sealed copy. A wrong passphrase and a damaged file look the same on purpose.</summary>
    public static byte[] Open(byte[] file, string passphrase)
    {
        Wire? wire;
        try { wire = JsonSerializer.Deserialize<Wire>(file, Json); }
        catch (JsonException) { wire = null; }
        if (wire is not { Kickl: Marker, V: 1 } || wire.Kdf != PinKdf.Id || string.IsNullOrEmpty(wire.Site)
            || wire.Salt is null || wire.Nonce is null || wire.Ct is null)
            throw new ClaimException("That is not a sealed Kickl certificate.");
        if (passphrase.Length == 0) throw new ClaimException("This certificate copy is protected. Enter its passphrase.");
        byte[]? key = null;
        try
        {
            key = PinKdf.Derive(passphrase, B64u.Decode(wire.Salt));
            return Aead.Open(key, B64u.Decode(wire.Nonce), B64u.Decode(wire.Ct), Aad(wire.Site));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            throw new ClaimException("Wrong passphrase, or the certificate copy is damaged.");
        }
        finally { if (key is not null) CryptographicOperations.ZeroMemory(key); }
    }
}

/// <summary>
/// Claiming a name costs Bitcoin and is paid at the shop. Until then every name is free for anyone to create
/// and edit. The shop signs an ownership certificate; the browser checks it against the shop key this device
/// pinned, remembers it in the profile, and from then on only a holder of the certificate can publish.
/// </summary>
public static class SiteClaims
{
    public const string DefaultShopUrl = "https://secupgrade.com/btcshop/";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static string ShopUrl()
    {
#if DEBUG
        // Debug builds only (see test-claims.bat): a throwaway test shop. A shipped build only ever uses the real one.
        if (Environment.GetEnvironmentVariable("KICKL_SHOP_URL") is { Length: > 0 } url)
            return url.EndsWith('/') ? url : url + "/";
#endif
        return DefaultShopUrl;
    }

    /// <summary>The shop's signing key is only ever learned over https from secupgrade.com (whose certificate vouches for it).</summary>
    public static Uri RequireOfficialShop(string url)
    {
        var uri = new Uri(url);
#if DEBUG
        if (Environment.GetEnvironmentVariable("KICKL_SHOP_URL") is { Length: > 0 } && uri.IsLoopback) return uri;
#endif
        if (uri.Scheme != Uri.UriSchemeHttps || !string.Equals(uri.Host, "secupgrade.com", StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort)
            throw new ClaimException($"The shop's key is only trusted from {DefaultShopUrl} over https, not from {uri.GetLeftPart(UriPartial.Path)}.");
        return uri;
    }

    /// <summary>The page the CLAIM SITE button opens. The fragment is never sent to the server.</summary>
    public static string ClaimLink(string shopUrl, string site) =>
        $"{shopUrl}#claim={Uri.EscapeDataString(site)}";

    public static bool Verify(OwnershipCert cert, string shopSpkiB64)
    {
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(shopSpkiB64), out _);
            return rsa.VerifyData(Utf8.B(cert.Payload()), Convert.FromBase64String(cert.Sig),
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ the shop keys built into the browser

    private sealed class ShopKeyList
    {
        public List<ShopKeyEntry> Keys { get; set; } = [];
    }

    private sealed class ShopKeyEntry
    {
        public string Name { get; set; } = "";
        public string Spki { get; set; } = "";
    }

    /// <summary>Tests only: the list a test run uses unless it sets <see cref="EmbeddedKeysOverride"/>. Empty means "learn on first contact".</summary>
    internal static IReadOnlyList<string>? EmbeddedDefaultOverride;
    internal static readonly AsyncLocal<IReadOnlyList<string>?> EmbeddedKeysOverride = new();

    /// <summary>
    /// The shop signing keys this browser was built to trust. When the list is not empty it is the ONLY thing trusted: a
    /// certificate signed by any other key is refused, nothing is learned on first contact, and no network is needed.
    /// Rotating the shop key means shipping a build that lists the new key (and the old one while receipts signed by it
    /// are still in use).
    /// </summary>
    public static IReadOnlyList<string> EmbeddedShopKeys()
    {
        if (EmbeddedKeysOverride.Value is { } forTest) return forTest;
        if (EmbeddedDefaultOverride is { } testDefault) return testDefault;
#if DEBUG
        // Debug builds only (see test-claims.bat): a throwaway test shop's key, from a file.
        if (Environment.GetEnvironmentVariable("KICKL_TEST_SHOP_KEYS") is { Length: > 0 } file && File.Exists(file))
            return File.ReadAllLines(file).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
#endif
        using var stream = typeof(SiteClaims).Assembly.GetManifestResourceStream("shop-keys.json");
        if (stream is null) return [];
        var list = JsonSerializer.Deserialize<ShopKeyList>(stream, Json);
        return list?.Keys.Select(k => k.Spki).Where(k => k.Length > 0).ToList() ?? [];
    }

        // ------------------------------------------------------------------ shop key pin

    private sealed class ShopKeyPin
    {
        public string Spki { get; set; } = "";
        public string Fingerprint { get; set; } = "";
    }

    private sealed class KeyWire
    {
        public string Spki { get; set; } = "";
    }

    private static string PinPath(DataPaths paths) => Path.Combine(paths.Root, "shared", "ownership", "shop-key.json");

    public static string Fingerprint(string spkiB64) =>
        Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(spkiB64))).ToLowerInvariant();

    /// <summary>The shop key this device already trusts, or null on a device that has never dealt with the shop.</summary>
    public static string? PinnedShopKey(DataPaths paths)
    {
        var path = PinPath(paths);
        return File.Exists(path) ? JsonSerializer.Deserialize<ShopKeyPin>(File.ReadAllText(path), Json)?.Spki : null;
    }

    /// <summary>
    /// Pin the shop's signing key on first contact; afterwards a different key is refused, so a
    /// look-alike shop cannot hand out ownership certificates.
    /// </summary>
    public static string PinShopKey(DataPaths paths, string spkiB64)
    {
        if (PinnedShopKey(paths) is { } pinned)
        {
            if (!string.Equals(pinned, spkiB64, StringComparison.Ordinal))
                throw new ClaimException("The shop is using a different signing key than the one this device pinned. Certificate refused.");
            return pinned;
        }

        var path = PinPath(paths);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(
            new ShopKeyPin { Spki = spkiB64, Fingerprint = Fingerprint(spkiB64) }, Json));
        return spkiB64;
    }

    public static async Task<string> FetchShopKeyAsync(HttpClient http, string shopUrl, CancellationToken ct = default)
    {
        var official = RequireOfficialShop(shopUrl);
        using var response = await http.PostAsJsonAsync(new Uri(official, "api.php"), new { action = "pubkey" }, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<KeyWire>(Json, ct))?.Spki
               ?? throw new ClaimException("The shop returned no signing key.");
    }

    // ------------------------------------------------------------------ accepting a certificate

    private static string OwnerIdFor(string commitmentHex) => "kb1c" + commitmentHex[..16];

    private static string BadgeIdFor(string site, string ownerId, int seq, DateTimeOffset issued) =>
        "kb1b" + Convert.ToHexString(SHA256.HashData(Utf8.B($"{site}|{ownerId}|{seq}|{issued:O}")))
            .ToLowerInvariant()[..16];

    /// <summary>
    /// Verify a certificate and record the claim on this device. A certificate for a name that has since been
    /// claimed again (higher sequence number) is refused. A device that has never seen the claim learns it here.
    /// </summary>
    public static TitleRecord Accept(DataPaths paths, OwnershipCert cert, string shopSpkiB64, string? expectedHost = null,
        bool keyIsBuiltIn = false)
    {
        if (expectedHost is not null && !string.Equals(cert.Site, expectedHost, StringComparison.OrdinalIgnoreCase))
            throw new ClaimException($"This certificate is for jttp://{cert.Site}/, not jttp://{expectedHost}/.");
        if (!Verify(cert, keyIsBuiltIn ? shopSpkiB64 : PinShopKey(paths, shopSpkiB64)))
            throw new ClaimException("The shop's signature on this certificate does not verify. Nothing was applied.");
        if (cert.IsExpired)
            throw new ClaimException("This certificate has expired. Renew the claim at the shop.");

        var current = OwnershipAuthority.LoadTitle(paths, cert.Site);
        if (current is not null && current.Seq > cert.Seq)
            throw new ClaimException("This certificate is out of date: the name has been claimed again since.");

        var commitment = Convert.ToHexString(SHA256.HashData(Utf8.B(cert.Token))).ToLowerInvariant();
        var issued = DateTimeOffset.FromUnixTimeSeconds(cert.PaidAt);
        var ownerId = OwnerIdFor(commitment);
        var title = new TitleRecord
        {
            Site = cert.Site,
            OwnerId = ownerId,
            TokenCommitment = commitment,
            BadgeId = BadgeIdFor(cert.Site, ownerId, cert.Seq, issued),
            Seq = cert.Seq,
            Status = "active",
            Tier = "paid-btc",
            PaymentRef = cert.Txid ?? $"order-{cert.Order}",
            IssuedUtc = issued.ToString("O"),
            ExpiresUtc = DateTimeOffset.FromUnixTimeSeconds(cert.Expires).ToString("O"),
            Issuer = "btcshop",
            IssuerSig = cert.Sig
        };
        OwnershipAuthority.SaveTitle(paths, title);
        OwnershipAuthority.ApplyToRegistry(paths, title);
        return title;
    }

    // ------------------------------------------------------------------ the rule once a name is claimed

    /// <summary>Is there an active paid title on this name?</summary>
    public static TitleRecord? ActiveTitle(DataPaths paths, string host)
    {
        var record = LocalRegistry.Resolve(LocalRegistry.LoadOrEmpty(paths), host);
        if (record is null || !LocalRegistry.IsOwned(record)) return null;
        var title = OwnershipAuthority.LoadTitle(paths, host);
        if (title is null || title.Status is "closed" or "transferred") return null;
        if (DateTimeOffset.TryParse(title.ExpiresUtc, out var expires) && expires < DateTimeOffset.UtcNow) return null;
        return title;
    }

    /// <summary>
    /// Unclaimed names are free for anyone. Once a title is active, only a holder of its current certificate may publish.
    /// </summary>
    public static void EnsureMayPublish(DataPaths paths, string host, bool holdsCertificate)
    {
        if (ActiveTitle(paths, host) is null || holdsCertificate) return;
        throw new ClaimException(
            $"jttp://{host}/ has been claimed. Open jttp://{host}/edit and present its ownership certificate to publish to it.");
    }
}
