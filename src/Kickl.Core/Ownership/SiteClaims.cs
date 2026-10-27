using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
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
        if (cert is not { Kickl: "ownership-cert", V: 1 } || cert.Site.Length == 0 || cert.Token.Length != 64
            || cert.Sig.Length == 0)
            throw new ClaimException("That is not a Kickl ownership certificate.");
        return cert;
    }

    /// <summary>The exact text the shop signed.</summary>
    public string Payload() =>
        string.Join('|', "kickl/v1/site-claim", Site, Order, Seq, PaidAt, Expires, Token);
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

    public static string ShopUrl() =>
        Environment.GetEnvironmentVariable("KICKL_SHOP_URL") is { Length: > 0 } url
            ? (url.EndsWith('/') ? url : url + "/")
            : DefaultShopUrl;

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
        using var response = await http.PostAsJsonAsync(new Uri(new Uri(shopUrl), "api.php"), new { action = "pubkey" }, ct);
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
    public static TitleRecord Accept(DataPaths paths, OwnershipCert cert, string shopSpkiB64, string? expectedHost = null)
    {
        if (expectedHost is not null && !string.Equals(cert.Site, expectedHost, StringComparison.OrdinalIgnoreCase))
            throw new ClaimException($"This certificate is for jttp://{cert.Site}/, not jttp://{expectedHost}/.");
        if (!Verify(cert, PinShopKey(paths, shopSpkiB64)))
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
