using System.Security.Cryptography;
using System.Text.Json;
using Kickl.Core.Registry;
using Kickl.Core.Util;

namespace Kickl.RegistrySigner;

/// <summary>
/// Offline publisher for the Pinwheel registry. It owns the hybrid private key, builds the
/// canonical snapshot/delta bytes, and signs them; the PHP API only stores and serves the result.
/// </summary>
internal static class Program
{
    private static readonly JsonSerializerOptions RecordJson = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "help" or "--help" or "-h") return Help();

            return args[0].ToLowerInvariant() switch
            {
                "keygen" => Keygen(args[1..]),
                "publish" => Publish(args[1..]),
                _ => Fail($"unknown command '{args[0]}'")
            };
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or IOException
                                       or JsonException or CryptographicException
                                       or InvalidOperationException)
        {
            return Fail(ex.Message);
        }
    }

    private static int Keygen(string[] args)
    {
        var opts = Options.Parse(args);
        var dir = opts.Get("dir") ?? throw new ArgumentException("--dir is required.");
        Directory.CreateDirectory(dir);

        var publicPath = Path.Combine(dir, "registry-key.json");
        if (File.Exists(publicPath) && !opts.Has("force"))
            throw new InvalidOperationException(
                $"'{publicPath}' already exists; refusing to overwrite a registry key without --force.");

        using var key = RegistrySigningKeyPair.Generate();
        File.WriteAllText(Path.Combine(dir, "ed25519.seed"), Hashing.Hex(key.Seed));
        File.WriteAllText(Path.Combine(dir, "mldsa65.pkcs8"), B64u.Encode(key.MlDsaPkcs8));
        File.WriteAllText(publicPath, JsonSerializer.Serialize(key.Public, RegistrySigningKey.Json));

        Ok($"registry key created in {Path.GetFullPath(dir)}");
        Console.WriteLine($"  fingerprint: {key.Public.Fingerprint}");
        Console.WriteLine("  private files: ed25519.seed, mldsa65.pkcs8 (unencrypted; dev signer only)");
        Console.WriteLine("  publish registry-key.json so clients can pin the fingerprint");
        return 0;
    }

    private static int Publish(string[] args)
    {
        var opts = Options.Parse(args);
        var recordsPath = opts.Get("records") ?? throw new ArgumentException("--records is required.");
        var keyDir = opts.Get("key-dir") ?? throw new ArgumentException("--key-dir is required.");
        var outPath = opts.Get("out") ?? throw new ArgumentException("--out is required.");
        var version = opts.GetInt("version") ?? throw new ArgumentException("--version is required.");
        var generatedAt = opts.GetLong("generated-at")
                          ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        if (version < 1) throw new ArgumentException("Snapshot versions start at 1.");

        var records = JsonSerializer.Deserialize<List<RegistryRecord>>(
                          File.ReadAllText(recordsPath), RecordJson)
                      ?? throw new FormatException("The records file is empty.");

        using var key = LoadKey(keyDir);
        var snapshot = new RegistrySnapshot
        {
            Version = version,
            GeneratedAt = generatedAt,
            Records = records
        };
        snapshot.RootHash = snapshot.ComputeRootHex();
        snapshot.Sig = key.Sign(snapshot.CanonicalPayload());
        File.WriteAllText(outPath, JsonSerializer.Serialize(snapshot, RegistrySigningKey.Json));

        var deltaPath = opts.Get("out-delta");
        if (opts.Get("previous") is { } previousPath)
        {
            if (deltaPath is null) throw new ArgumentException("--previous also needs --out-delta.");
            var previous = RegistrySnapshot.Parse(File.ReadAllBytes(previousPath));
            if (previous.Version >= version)
                throw new ArgumentException(
                    $"New snapshot v{version} must be newer than previous v{previous.Version}.");

            var delta = BuildDelta(previous, snapshot);
            delta.Sig = key.Sign(delta.CanonicalPayload());
            File.WriteAllText(deltaPath, JsonSerializer.Serialize(delta, RegistrySigningKey.Json));
        }

        Ok($"snapshot v{version} signed with {key.Public.Fingerprint}");
        Console.WriteLine($"  root:     {snapshot.RootHash}");
        Console.WriteLine($"  records:  {snapshot.Records.Count}");
        Console.WriteLine($"  snapshot: {Path.GetFullPath(outPath)}");
        if (deltaPath is not null) Console.WriteLine($"  delta:    {Path.GetFullPath(deltaPath)}");
        return 0;
    }

    private static RegistryDelta BuildDelta(RegistrySnapshot previous, RegistrySnapshot current)
    {
        var before = previous.Records.ToDictionary(r => r.Name, StringComparer.Ordinal);
        var after = current.Records.ToDictionary(r => r.Name, StringComparer.Ordinal);

        foreach (var name in before.Keys)
        {
            if (!after.ContainsKey(name))
                throw new InvalidOperationException(
                    $"Record '{name}' disappeared; registry names are closed, never removed.");
        }

        var changed = new List<RegistryRecord>();
        foreach (var record in current.Records.OrderBy(r => r.Name, StringComparer.Ordinal))
        {
            if (!before.TryGetValue(record.Name, out var old) ||
                !RegistrySnapshot.RecordBytes(old).AsSpan()
                    .SequenceEqual(RegistrySnapshot.RecordBytes(record)))
                changed.Add(record);
        }

        return new RegistryDelta
        {
            From = previous.Version,
            To = current.Version,
            RootHash = current.RootHash,
            GeneratedAt = current.GeneratedAt,
            Records = changed
        };
    }

    private static RegistrySigningKeyPair LoadKey(string dir)
    {
        var seedPath = Path.Combine(dir, "ed25519.seed");
        var mlPath = Path.Combine(dir, "mldsa65.pkcs8");
        if (!File.Exists(seedPath) || !File.Exists(mlPath))
            throw new FileNotFoundException(
                $"No signing key in '{Path.GetFullPath(dir)}'; run keygen first.");

        var seed = Convert.FromHexString(File.ReadAllText(seedPath).Trim());
        var mlDsa = B64u.Decode(File.ReadAllText(mlPath).Trim());
        var pair = RegistrySigningKeyPair.Load(seed, mlDsa);

        var publicPath = Path.Combine(dir, "registry-key.json");
        if (File.Exists(publicPath))
        {
            var published = JsonSerializer.Deserialize<RegistrySigningKey>(
                                File.ReadAllText(publicPath), RegistrySigningKey.Json)
                            ?? throw new FormatException("The public registry key file is unreadable.");
            if (!string.Equals(published.Fingerprint, pair.Public.Fingerprint, StringComparison.Ordinal))
                throw new FormatException(
                    "The public registry key does not match the private key on disk.");
        }
        return pair;
    }

    private static int Help()
    {
        Console.WriteLine("""
            Kickl.RegistrySigner — offline Pinwheel registry publisher

              keygen  --dir DIR [--force]
              publish --records FILE --version N --key-dir DIR --out FILE
                      [--generated-at UNIX] [--previous SNAPSHOT --out-delta FILE]

            publish reads a JSON array of registry records, computes the Merkle root, builds the
            canonical snapshot, and signs it with Ed25519 + ML-DSA-65. With --previous it also
            signs a delta for clients that already accepted that version.
            """);
        return 0;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine("error: " + message);
        return 1;
    }

    private static void Ok(string message) => Console.WriteLine(message);

    private sealed class Options
    {
        private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);

        public static Options Parse(string[] args)
        {
            var options = new Options();
            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (!arg.StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException($"Unexpected argument '{arg}'.");
                var name = arg[2..];
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

        public int? GetInt(string name) =>
            int.TryParse(Get(name), out var value) ? value : null;

        public long? GetLong(string name) =>
            long.TryParse(Get(name), out var value) ? value : null;
    }
}
