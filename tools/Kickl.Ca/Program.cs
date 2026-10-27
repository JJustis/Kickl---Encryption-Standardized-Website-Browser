using System.Text.Json;
using Kickl.Core.Ownership;
using Kickl.Core.Registry;
using Kickl.Core.Trust;

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
