using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Kickl.Core.Ownership;
using Kickl.Core.Registry;
using Kickl.Core.Util;

namespace Kickl.Core.Trust;

/// <summary>Where this device finds the CA root it trusts, and the revocation list it last accepted.</summary>
public static class CaPins
{
    /// <summary>
    /// The one and only CA. Certificates are sealed by https://secupgrade.com/secca/ and by nothing else: this address is
    /// fixed in the build, plain http is refused, and so is any other host.
    /// </summary>
    public const string OfficialCaUrl = "https://secupgrade.com/secca/";
    private const string OfficialCaHost = "secupgrade.com";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

#if DEBUG
    // Debug builds only (see test-claims.bat): a throwaway test CA on this computer. Shipped builds have neither
    // override, so no environment variable or file can point a Release browser at a different CA.
    private static string? TestCaUrl() =>
        Environment.GetEnvironmentVariable("KICKL_CA_URL") is { Length: > 0 } u ? (u.EndsWith('/') ? u : u + "/") : null;
#endif

    public static string CaUrl()
    {
#if DEBUG
        if (TestCaUrl() is { } test) return test;
#endif
        return OfficialCaUrl;
    }

    /// <summary>Refuse to talk to anything but the official CA over https.</summary>
    public static Uri RequireOfficialCa(string url)
    {
        var uri = new Uri(url);
#if DEBUG
        if (TestCaUrl() is not null && uri.IsLoopback) return uri;   // the local test CA, Debug builds only
#endif
        if (uri.Scheme != Uri.UriSchemeHttps || !string.Equals(uri.Host, OfficialCaHost, StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort)
            throw new ClaimException($"Certificates are sealed only by {OfficialCaUrl}, not by {uri.GetLeftPart(UriPartial.Path)}.");
        return uri;
    }

    /// <summary>
    /// An HttpClient for CA traffic. It never follows a redirect: a receipt carries a secret, and a redirect is the easy way
    /// to send one to a host that is not the CA.
    /// </summary>
    public static HttpClient NewClient(TimeSpan? timeout = null) =>
        new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = timeout ?? TimeSpan.FromSeconds(30) }.Limit(HttpLimits.CaAndShop);

    private static string Dir(DataPaths paths) => Path.Combine(paths.Root, "shared", "ownership");

    /// <summary>Tests only (InternalsVisibleTo): stand in for the root a real build embeds.</summary>
    internal static readonly AsyncLocal<RegistrySigningKey?> RootOverride = new();

    /// <summary>True while a Debug build trusts a throwaway test CA instead of the root built into the browser.</summary>
    public static bool TestRootInUse =>
#if DEBUG
        TestRootFile() is not null;
#else
        false;
#endif

#if DEBUG
    private static RegistrySigningKey? TestRootFile()
    {
        var path = Environment.GetEnvironmentVariable("KICKL_TEST_CA_ROOT");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        var key = JsonSerializer.Deserialize<RegistrySigningKey>(File.ReadAllText(path), RegistrySigningKey.Json);
        key?.Validate();
        return key;
    }
#endif

    /// <summary>
    /// The CA root key, built into the browser. There is no fallback: a build without a root trusts no CA at all, and nothing
    /// in the data folder can supply one.
    /// </summary>
    public static RegistrySigningKey? Root(DataPaths paths)
    {
        if (RootOverride.Value is { } forTest) return forTest;
#if DEBUG
        if (TestRootFile() is { } testRoot) return testRoot;
#endif
        using var stream = typeof(CaPins).Assembly.GetManifestResourceStream("ca-root.json");
        if (stream is null) return null;
        var embedded = JsonSerializer.Deserialize<RegistrySigningKey>(stream, RegistrySigningKey.Json);
        embedded?.Validate();
        return embedded;
    }

    private static string RevocationsPath(DataPaths paths) => Path.Combine(Dir(paths), "ca-revocations.json");

    public static RevocationList? CachedRevocations(DataPaths paths)
    {
        var path = RevocationsPath(paths);
        return File.Exists(path) ? JsonSerializer.Deserialize<RevocationList>(File.ReadAllText(path), Json) : null;
    }

    private sealed class RootWire
    {
        public RegistrySigningKey Root { get; set; } = new();
        public IssuerCertificate Issuer { get; set; } = new();
    }

    /// <summary>
    /// Fetch the CA's revocation list and keep it if it is authentic and newer than the one already held.
    /// A list that fails verification, or goes backwards, is ignored; the old one stays in force.
    /// </summary>
    public static async Task<RevocationList?> RefreshRevocationsAsync(DataPaths paths, HttpClient http,
        DateTimeOffset now, CancellationToken ct = default)
    {
        var root = Root(paths);
        if (root is null) return CachedRevocations(paths);

        var baseUri = RequireOfficialCa(CaUrl());
        var list = await http.GetFromJsonAsync<RevocationList>(new Uri(baseUri, "v1/ca/revocations"), Json, ct);
        if (list is null || list.Number == 0) return CachedRevocations(paths);

        var cached = CachedRevocations(paths);
        if (cached is not null && list.Number <= cached.Number) return cached;

        IssuerCertificate? issuer = null;
        if (list.SignedBy == "issuer")
            issuer = (await http.GetFromJsonAsync<RootWire>(new Uri(baseUri, "v1/ca/root"), Json, ct))?.Issuer;

        if (!CaTrust.VerifyRevocations(root, list, issuer, now)) return cached;

        Directory.CreateDirectory(Dir(paths));
        File.WriteAllText(RevocationsPath(paths), JsonSerializer.Serialize(list, Json));
        return list;
    }
}

/// <summary>Talks to the CA service: submit a shop receipt, get back the countersigned seal.</summary>
public static class CaClient
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private sealed class Answer
    {
        public CaSeal? Seal { get; set; }
        public string? Error { get; set; }
        public string? Message { get; set; }
    }

    public static async Task<CaSeal> CountersignAsync(HttpClient http, string caUrl, OwnershipCert receipt,
        CancellationToken ct = default)
    {
        var official = CaPins.RequireOfficialCa(caUrl);   // the receipt holds a secret: only ever send it to the one CA
        // Send only what the shop signed. Any earlier seal stays here.
        var bare = OwnershipCert.Parse(receipt.ToBytes());
        bare.Seal = null;

        using var content = new ByteArrayContent(bare.ToBytes());
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        using var response = await http.PostAsync(new Uri(official, "v1/ca/countersign"), content, ct);

        Answer? answer = null;
        try { answer = await response.Content.ReadFromJsonAsync<Answer>(Json, ct); }
        catch (JsonException) { }

        if (response.IsSuccessStatusCode && answer?.Seal is { } seal) return seal;
        throw new ClaimException(answer?.Message is { Length: > 0 } m
            ? "The CA refused: " + m
            : $"The CA did not answer ({(int)response.StatusCode}).");
    }
}
