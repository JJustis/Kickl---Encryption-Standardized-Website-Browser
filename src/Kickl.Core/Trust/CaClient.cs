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
    public const string DefaultCaUrl = "https://secupgrade.com/secca/";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static string CaUrl() =>
        Environment.GetEnvironmentVariable("KICKL_CA_URL") is { Length: > 0 } url
            ? (url.EndsWith('/') ? url : url + "/")
            : DefaultCaUrl;

    private static string Dir(DataPaths paths) => Path.Combine(paths.Root, "shared", "ownership");

    /// <summary>
    /// The CA root key. The one built into the browser always wins: it cannot be changed by anything that can
    /// write to the data folder. A file pin is only a fallback for builds that ship without a root.
    /// </summary>
    /// <summary>Tests only (InternalsVisibleTo): stand in for the root a real build embeds.</summary>
    internal static readonly AsyncLocal<RegistrySigningKey?> RootOverride = new();

    /// <summary>True while this process trusts a throwaway test CA instead of the root built into the browser.</summary>
    public static bool TestRootInUse => TestRootFile() is not null;

    private static RegistrySigningKey? TestRootFile()
    {
        var path = Environment.GetEnvironmentVariable("KICKL_TEST_CA_ROOT");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        var key = JsonSerializer.Deserialize<RegistrySigningKey>(File.ReadAllText(path), RegistrySigningKey.Json);
        key?.Validate();
        return key;
    }

    public static RegistrySigningKey? Root(DataPaths paths)
    {
        if (RootOverride.Value is { } forTest) return forTest;

        // A throwaway test CA (see test-claims.bat). Never set in normal use; when it is, the badge says so.
        if (TestRootFile() is { } testRoot) return testRoot;
        using (var stream = typeof(CaPins).Assembly.GetManifestResourceStream("ca-root.json"))
        {
            if (stream is not null)
            {
                var embedded = JsonSerializer.Deserialize<RegistrySigningKey>(stream, RegistrySigningKey.Json);
                embedded?.Validate();
                return embedded;
            }
        }

        var path = Path.Combine(Dir(paths), "ca-root.json");
        if (!File.Exists(path)) return null;
        var pinned = JsonSerializer.Deserialize<RegistrySigningKey>(File.ReadAllText(path), RegistrySigningKey.Json);
        pinned?.Validate();
        return pinned;
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

        var baseUri = new Uri(CaUrl());
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
        // Send only what the shop signed. Any earlier seal stays here.
        var bare = OwnershipCert.Parse(receipt.ToBytes());
        bare.Seal = null;

        using var content = new ByteArrayContent(bare.ToBytes());
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        using var response = await http.PostAsync(new Uri(new Uri(caUrl), "v1/ca/countersign"), content, ct);

        Answer? answer = null;
        try { answer = await response.Content.ReadFromJsonAsync<Answer>(Json, ct); }
        catch (JsonException) { }

        if (response.IsSuccessStatusCode && answer?.Seal is { } seal) return seal;
        throw new ClaimException(answer?.Message is { Length: > 0 } m
            ? "The CA refused: " + m
            : $"The CA did not answer ({(int)response.StatusCode}).");
    }
}
