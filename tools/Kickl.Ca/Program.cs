using System.Text;
using System.Text.Json;
using Kickl.Core.Ownership;
using Kickl.Core.Registry;
using Kickl.Core.Trust;
using Kickl.Core.Util;

namespace Kickl.Ca;

/// <summary>
/// The operator's CA tool. Key ceremonies (<c>root-keygen</c>, <c>issue-issuer</c>) are meant for an offline machine.
/// The online service only ever runs <c>countersign</c>, which holds the issuing key but never the root.
/// </summary>
internal static class Program
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "help" or "--help") return Help();
            var opts = Options.Parse(args[1..]);
            return args[0] switch
            {
                "root-keygen" => RootKeygen(opts),
                "issuer-keygen" => IssuerKeygen(opts),
                "issue-issuer" => IssueIssuer(opts),
                "countersign" => Countersign(opts),
                "revoke" => Revoke(opts),
                "audit" => Audit(opts),
                "setup" => BuildCa(opts),
                "health" => Health(opts),
                "maintain" => Maintain(opts),
                "renew-request" => RenewRequest(opts),
                "sign-renewal" => SignRenewal(opts),
                "install-renewal" => InstallRenewal(opts),
                "trust-shop" => TrustShop(opts),
                "protect-keys" => ProtectKeys(opts),
                "shop-keygen" => ShopKeygen(opts),
                "shop-import" => ShopImport(opts),
                "shop-sign" => ShopSign(opts),
                "shop-pubkey" => ShopPubkey(opts),
                _ => Fail($"unknown command '{args[0]}'")
            };
        }
        catch (CaRejectedException ex)
        {
            // The web wrapper relays this verbatim; the code is stable.
            Console.WriteLine(JsonSerializer.Serialize(new { error = ex.Code, message = ex.Message }, Json));
            return 2;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException
                                       or FormatException or JsonException or InvalidDataException)
        {
            return Fail(ex.Message);
        }
    }

    // ------------------------------------------------------------------ ceremonies

    private static int RootKeygen(Options o)
    {
        var dir = o.Require("dir");
        if (File.Exists(Path.Combine(dir, "root-public.json")) && !o.Has("force"))
            throw new InvalidOperationException("A root key already exists there; refusing to overwrite it without --force.");

        using var root = HybridSigner.Generate();
        root.SaveTo(dir);
        File.WriteAllText(Path.Combine(dir, "root-public.json"), JsonSerializer.Serialize(root.Public, RegistrySigningKey.Json));
        Console.WriteLine($"CA root created in {Path.GetFullPath(dir)}");
        Console.WriteLine($"  fingerprint: {root.Public.Fingerprint}");
        Console.WriteLine("  private files: ed25519.seed, mldsa65.pkcs8  <- keep OFFLINE (USB / safe). They are unencrypted.");
        Console.WriteLine("  publish root-public.json and ship it inside the browser build; clients pin this fingerprint");
        return 0;
    }

    private static int IssuerKeygen(Options o)
    {
        var dir = o.Require("dir");
        using var issuer = HybridSigner.Generate();
        issuer.SaveTo(dir);
        File.WriteAllText(Path.Combine(dir, "issuer-public.json"), JsonSerializer.Serialize(issuer.Public, RegistrySigningKey.Json));
        Console.WriteLine($"issuing key created in {Path.GetFullPath(dir)}");
        Console.WriteLine($"  fingerprint: {issuer.Public.Fingerprint}");
        Console.WriteLine("  next: carry issuer-public.json to the offline root machine and run issue-issuer");
        return 0;
    }

    private static int IssueIssuer(Options o)
    {
        using var root = HybridSigner.LoadFrom(o.Require("root-dir"));
        var issuerPublic = JsonSerializer.Deserialize<RegistrySigningKey>(File.ReadAllText(o.Require("issuer-public")), RegistrySigningKey.Json)
                           ?? throw new FormatException("The issuer public key file is unreadable.");
        issuerPublic.Validate();
        var days = int.Parse(o.Get("days") ?? "90");
        var cert = CaService.IssueIssuer(root, issuerPublic, o.Get("name") ?? "Kickl CA issuing key",
            long.Parse(o.Get("serial") ?? "1"), DateTimeOffset.UtcNow.AddMinutes(-5), TimeSpan.FromDays(days));
        File.WriteAllText(o.Require("out"), JsonSerializer.Serialize(cert, Json));
        Console.WriteLine($"issuer certificate #{cert.Serial} signed by root {root.Public.Fingerprint[..16]}…");
        Console.WriteLine($"  valid until {DateTimeOffset.FromUnixTimeSeconds(cert.NotAfter):u} ({days} days); renew before then");
        return 0;
    }

    // ------------------------------------------------------------------ the online service

    private sealed class Setup(string[] args) : IDisposable
    {
        public readonly Options Opts = Options.Parse(args);
        public HybridSigner IssuerKey = null!;
        public IssuerCertificate IssuerCert = null!;
        public RegistrySigningKey Root = null!;
        public FileCaStore Store = null!;
        public CaService Service = null!;
        public RevocationList? Revocations;

        public static Setup Open(string[] args)
        {
            var s = new Setup(args);
            var o = s.Opts;
            s.IssuerKey = HybridSigner.LoadFrom(o.Require("issuer-dir"));
            s.IssuerCert = JsonSerializer.Deserialize<IssuerCertificate>(File.ReadAllText(o.Require("issuer-cert")), Json)
                           ?? throw new FormatException("The issuer certificate is unreadable.");
            s.Root = JsonSerializer.Deserialize<RegistrySigningKey>(File.ReadAllText(o.Require("root-public")), RegistrySigningKey.Json)
                     ?? throw new FormatException("The root public key is unreadable.");
            var shopKeys = File.ReadAllLines(o.Require("shop-keys")).Select(l => l.Trim()).Where(l => l.Length > 0);
            s.Store = new FileCaStore(o.Require("data"));
            s.Service = new CaService(s.IssuerKey, s.IssuerCert, s.Root, shopKeys, s.Store);
            if (o.Get("revocations") is { } path && File.Exists(path))
                s.Revocations = JsonSerializer.Deserialize<RevocationList>(File.ReadAllText(path), Json);
            return s;
        }

        public void Dispose()
        {
            Store?.Dispose();
            IssuerKey?.Dispose();
        }
    }

    private static int Countersign(Options o)
    {
        using var s = Setup.Open(RebuildArgs(o));
        var json = Console.In.ReadToEnd();
        var receipt = OwnershipCert.Parse(System.Text.Encoding.UTF8.GetBytes(json));
        var seal = s.Service.Countersign(receipt, DateTimeOffset.UtcNow, s.Revocations);
        Console.WriteLine(JsonSerializer.Serialize(seal, Json));
        return 0;
    }

    private static int Audit(Options o)
    {
        using var s = Setup.Open(RebuildArgs(o));
        var problems = s.Service.Audit();
        var head = s.Service.Head(DateTimeOffset.UtcNow);
        Console.WriteLine($"log size {head.Size}, root {head.Root}");
        foreach (var p in problems) Console.WriteLine("PROBLEM: " + p);
        Console.WriteLine(problems.Count == 0 ? "log is consistent" : $"{problems.Count} problem(s)");
        return problems.Count == 0 ? 0 : 3;
    }

    private static int Revoke(Options o)
    {
        var previousPath = o.Get("previous");
        RevocationList? previous = previousPath is not null && File.Exists(previousPath)
            ? JsonSerializer.Deserialize<RevocationList>(File.ReadAllText(previousPath), Json) : null;

        var seals = (o.Get("seal") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x =>
            {
                var parts = x.Split(':', 3);
                return new RevokedSeal { Site = parts[0], Seq = int.Parse(parts[1]), Reason = parts.Length > 2 ? parts[2] : "" };
            }).ToList();
        var issuers = (o.Get("issuer-serial") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(long.Parse).ToList();

        RevocationList list;
        if (o.Has("root-dir"))
        {
            using var root = HybridSigner.LoadFrom(o.Require("root-dir"));
            list = CaService.RevokeAsRoot(root, previous, issuers, seals, DateTimeOffset.UtcNow);
        }
        else
        {
            if (issuers.Count > 0) throw new ArgumentException("Only the root can revoke an issuing key; pass --root-dir.");
            using var s = Setup.Open(RebuildArgs(o));
            list = s.Service.Revoke(previous, seals, DateTimeOffset.UtcNow);
        }

        File.WriteAllText(o.Require("out"), JsonSerializer.Serialize(list, Json));
        Console.WriteLine($"revocation list #{list.Number} signed by {list.SignedBy}: {list.RevokedSeals.Count} seal(s), {list.RevokedIssuerSerials.Count} issuer(s)");
        return 0;
    }

    private static string[] RebuildArgs(Options o) => o.ToArgs();

    // ------------------------------------------------------------------ build and keep a CA healthy

    private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) }.Limit(HttpLimits.CaAndShop);

    /// <summary>
    /// The operator names their own shop, so unlike the browser this is not pinned to secupgrade.com. Plain http is allowed only
    /// for this computer (a local test shop); anything else must be https.
    /// </summary>
    private static async Task<string?> FetchShopKey(string shopUrl, CancellationToken ct)
    {
        var uri = new Uri(shopUrl.EndsWith('/') ? shopUrl : shopUrl + "/");
        if (uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback)
            throw new ArgumentException("A shop key must be fetched over https (http is only accepted for this computer).");
        using var response = await Http.PostAsync(new Uri(uri, "api.php"),
            new StringContent("{\"action\":\"pubkey\"}", Encoding.UTF8, "application/json"), ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("spki").GetString();
    }

    private static async Task<string?> FetchPublicRoot(string publicUrl, CancellationToken ct)
    {
        var url = new Uri(new Uri(publicUrl.EndsWith('/') ? publicUrl : publicUrl + "/"), "v1/ca/root");
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url, ct));
        return doc.RootElement.GetProperty("root").GetProperty("fingerprint").GetString();
    }

    private static int BuildCa(Options o)
    {
        var caDir = o.Require("ca-dir");
        var config = new CaConfig
        {
            ShopUrl = o.Get("shop-url"),
            PublicUrl = o.Get("public-url"),
            IssuerDays = int.Parse(o.Get("days") ?? "90"),
            RenewBeforeDays = int.Parse(o.Get("renew-before") ?? "21"),
            IssuerName = o.Get("name") ?? "Kickl CA issuing key"
        };

        string? shopKey = null;
        if (o.Get("shop-key-file") is { } keyFile) shopKey = File.ReadAllText(keyFile).Trim();
        else if (o.Get("shop-url") is { } shopUrl)
        {
            shopKey = FetchShopKey(shopUrl, CancellationToken.None).GetAwaiter().GetResult();
            Console.WriteLine($"shop key fetched from {shopUrl}");
            Console.WriteLine($"  fingerprint: {SiteClaims.Fingerprint(shopKey!)}");
            Console.WriteLine("  check it against your shop before relying on this CA");
        }

        var result = CaOps.Setup(caDir, config, o.Get("root-dir"), o.Get("root-public"), shopKey, DateTimeOffset.UtcNow);
        Console.WriteLine(result.Complete ? "CA is ready" : "CA is partly built");
        Console.WriteLine($"  root:   {result.RootFingerprint}");
        if (result.Complete) Console.WriteLine($"  issuer: #{result.IssuerSerial}");
        Console.WriteLine("  " + result.Message.Replace("\n", "\n  "));
        if (config.RootDir is not null)
            Console.WriteLine("  NOTE: the root private key is on this machine. That is fine for development; for production keep it offline.");
        Console.WriteLine($"  to embed this root in the browser: copy {Path.Combine(caDir, "root-public.json")} to src/Kickl.Core/Trust/ca-root.json and rebuild");
        return result.Complete ? 0 : 2;
    }

    private static int Health(Options o)
    {
        var report = CaOps.HealthAsync(o.Require("ca-dir"), DateTimeOffset.UtcNow, FetchShopKey, FetchPublicRoot).GetAwaiter().GetResult();
        if (o.Has("json"))
            Console.WriteLine(JsonSerializer.Serialize(new { overall = report.Overall.ToString(), items = report.Items.Select(i => new { i.Name, level = i.Level.ToString(), i.Detail }) }, Json));
        else
            Console.WriteLine(report.ToText());
        return (int)report.Overall;
    }

    private static int Maintain(Options o)
    {
        var result = CaOps.MaintainAsync(o.Require("ca-dir"), DateTimeOffset.UtcNow, o.Has("dry-run"), FetchShopKey, FetchPublicRoot)
            .GetAwaiter().GetResult();
        Console.WriteLine(result.After.ToText());
        foreach (var a in result.Actions) Console.WriteLine("  action: " + a);
        if (result.Actions.Count == 0) Console.WriteLine("  nothing to do");
        if (result.NeedsHuman) Console.WriteLine("  A PERSON IS NEEDED: see the lines above.");
        return result.NeedsHuman ? 2 : (int)result.After.Overall;
    }

    private static int RenewRequest(Options o)
    {
        var request = CaOps.CreateRenewalRequest(o.Require("ca-dir"), DateTimeOffset.UtcNow);
        var layout = new CaLayout(o.Require("ca-dir"));
        Console.WriteLine($"renewal request for issuing key #{request.Serial} is at {layout.PendingRequest}");
        Console.WriteLine("  carry it to the offline root machine and run sign-renewal, then install-renewal here");
        return 0;
    }

    private static int SignRenewal(Options o)
    {
        var cert = CaOps.SignRenewal(o.Require("root-dir"), o.Require("request"), DateTimeOffset.UtcNow);
        File.WriteAllText(o.Require("out"), JsonSerializer.Serialize(cert, Json));
        Console.WriteLine($"issuing key #{cert.Serial} certified until {DateTimeOffset.FromUnixTimeSeconds(cert.NotAfter):u}");
        Console.WriteLine($"  certificate: {Path.GetFullPath(o.Require("out"))}  (carry it back to the CA machine)");
        return 0;
    }

    private static int InstallRenewal(Options o)
    {
        var cert = JsonSerializer.Deserialize<IssuerCertificate>(File.ReadAllText(o.Require("cert")), Json)
                   ?? throw new FormatException("The certificate is unreadable.");
        CaOps.InstallRenewal(o.Require("ca-dir"), cert, DateTimeOffset.UtcNow);
        Console.WriteLine($"issuing key #{cert.Serial} installed; any previous private key was destroyed");
        return 0;
    }

    // ------------------------------------------------------------------ the shop's signing key

    private static int ShopKeygen(Options o)
    {
        var dir = o.Require("dir");
        if (ShopSigningKey.Exists(dir) && !o.Has("force")) throw new InvalidOperationException("A shop key already exists there; refusing to replace it without --force.");
        var spki = ShopSigningKey.Generate(dir);
        Console.WriteLine("shop signing key created (RSA-3072, wrapped with Windows DPAPI, folder restricted to its owner)");
        Console.WriteLine("  fingerprint: " + SiteClaims.Fingerprint(spki));
        Console.WriteLine("  the CA must be told to trust it: kickl-ca trust-shop --ca-dir D --key-file " + Path.Combine(dir, "shop-key.spki"));
        return 0;
    }

    /// <summary>Move an existing PEM into the protected store, keeping the same public key, and destroy the PEM.</summary>
    private static int ShopImport(Options o)
    {
        var pem = o.Require("pem");
        var dir = o.Require("dir");
        var spki = ShopSigningKey.ImportPem(dir, File.ReadAllText(pem));
        Console.WriteLine("imported; the public key is unchanged");
        Console.WriteLine("  fingerprint: " + SiteClaims.Fingerprint(spki));
        if (o.Has("shred-pem"))
        {
            // prove the protected copy signs before the original is destroyed
            var probe = ShopSigningKey.Sign(dir, Encoding.UTF8.GetBytes("import-check"));
            using var rsa = System.Security.Cryptography.RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(spki), out _);
            if (!rsa.VerifyData(Encoding.UTF8.GetBytes("import-check"), Convert.FromBase64String(probe),
                    System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1))
                throw new InvalidOperationException("The imported key did not sign correctly; the PEM was NOT destroyed.");
            FileProtection.Shred(pem);
            Console.WriteLine("  the PEM file was overwritten and deleted");
        }
        return 0;
    }

    /// <summary>Reads the text to sign from stdin and prints the base64 signature. Used by the shop's PHP.</summary>
    private static int ShopSign(Options o)
    {
        using var stdin = Console.OpenStandardInput();
        using var buffer = new MemoryStream();
        stdin.CopyTo(buffer);
        if (buffer.Length is 0 or > 4096) throw new ArgumentException("Send 1 to 4096 bytes to sign on stdin.");
        Console.Write(ShopSigningKey.Sign(o.Require("dir"), buffer.ToArray()));
        return 0;
    }

    private static int ShopPubkey(Options o)
    {
        Console.Write(ShopSigningKey.PublicKey(o.Require("dir")));
        return 0;
    }

    private static int ProtectKeys(Options o)
    {
        var applied = CaOps.ProtectIssuerKey(o.Require("ca-dir"), DateTimeOffset.UtcNow);
        Console.WriteLine(applied == KeyProtection.Machine
            ? "the issuing private key is wrapped with Windows DPAPI and restricted to its owner"
            : "the issuing private key is restricted to its owner (DPAPI is Windows-only)");
        return 0;
    }

    /// <summary>Adding a shop to the CA's trust is a deliberate human act, never something maintenance does.</summary>
    private static int TrustShop(Options o)
    {
        var layout = new CaLayout(o.Require("ca-dir"));
        var key = o.Get("key-file") is { } f ? File.ReadAllText(f).Trim()
            : FetchShopKey(o.Require("from-url"), CancellationToken.None).GetAwaiter().GetResult()
              ?? throw new InvalidOperationException("The shop returned no key.");
        Console.WriteLine($"shop key fingerprint: {SiteClaims.Fingerprint(key)}");
        var existing = File.Exists(layout.ShopKeys) ? File.ReadAllLines(layout.ShopKeys).Select(l => l.Trim()).ToList() : [];
        if (existing.Contains(key)) { Console.WriteLine("already trusted"); return 0; }
        File.AppendAllText(layout.ShopKeys, key + "\n");
        Console.WriteLine($"added. The CA now trusts {existing.Count(l => l.Length > 0) + 1} shop key(s). Remove an old key by editing {layout.ShopKeys}.");
        return 0;
    }

    private static int Help()
    {
        Console.WriteLine("""
            Kickl CA tool

              root-keygen   --dir D [--force]                                   (offline) create the root key
              issuer-keygen --dir D                                             create an issuing key (public half goes to the root)
              issue-issuer  --root-dir R --issuer-public F --out CERT [--name N --serial S --days 90]   (offline)
              countersign   --issuer-dir D --issuer-cert CERT --root-public F --shop-keys F --data DIR [--revocations F]
                            reads a shop receipt (.kcert JSON) on stdin, writes the seal on stdout
              audit         (same options as countersign) re-checks the whole transparency log

              setup         --ca-dir D (--root-dir R | --root-public F) [--shop-url URL | --shop-key-file F]
                            [--public-url URL --days 90 --renew-before 21 --name N]   build a working CA
                            --root-dir creates/uses a root on THIS machine (development); --root-public uses an offline root
              health        --ca-dir D [--json]     check everything; exit 0 ok, 1 warning, 2 bad
              maintain      --ca-dir D [--dry-run]  health check + fix what is safe (renews the issuing key); exit 2 = a person is needed
              renew-request --ca-dir D              new issuing key + a request for the root to certify
              sign-renewal  --root-dir R --request F --out CERT      (offline root machine)
              install-renewal --ca-dir D --cert CERT                  install the certified key, destroy the old private key
              protect-keys  --ca-dir D                wrap the issuing private key (Windows DPAPI) and lock its folder
              shop-keygen   --dir D [--force]         the BTC shop's signing key, DPAPI-wrapped (instead of a PEM the web server can read)
              shop-import   --pem F --dir D [--shred-pem]   move an existing PEM in, same public key, then destroy the PEM
              shop-sign     --dir D                    (stdin -> base64 signature; what the shop's PHP calls)
              shop-pubkey   --dir D                    print the shop's public key
              trust-shop    --ca-dir D (--key-file F | --from-url URL)  deliberately trust one more shop key
              revoke        --out F [--previous F] [--seal site:seq[:reason],...]
                            issuer-signed: add --issuer-dir/--issuer-cert/--root-public/--shop-keys/--data
                            root-signed:   add --root-dir [--issuer-serial N,...]
            """);
        return 0;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine("error: " + message);
        return 1;
    }

    private sealed class Options
    {
        private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);

        public static Options Parse(string[] args)
        {
            var options = new Options();
            for (var i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException($"Unexpected argument '{args[i]}'.");
                var name = args[i][2..];
                if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    options._flags.Add(name);
                    continue;
                }
                options._values[name] = args[++i];
            }
            return options;
        }

        public bool Has(string name) => _flags.Contains(name) || _values.ContainsKey(name);
        public string? Get(string name) => _values.GetValueOrDefault(name);
        public string Require(string name) => Get(name) ?? throw new ArgumentException($"--{name} is required.");

        public string[] ToArgs() => _values.SelectMany(kv => new[] { "--" + kv.Key, kv.Value! })
            .Concat(_flags.Select(f => "--" + f)).ToArray();
    }
}
