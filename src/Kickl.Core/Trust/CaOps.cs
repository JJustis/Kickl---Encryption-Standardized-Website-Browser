using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kickl.Core.Ownership;
using Kickl.Core.Registry;
using Kickl.Core.Util;

namespace Kickl.Core.Trust;

public enum HealthLevel { Ok = 0, Warn = 1, Bad = 2 }

public sealed record HealthItem(string Name, HealthLevel Level, string Detail);

public sealed class HealthReport
{
    public DateTimeOffset CheckedAt { get; init; }
    public List<HealthItem> Items { get; } = [];
    public HealthLevel Overall => Items.Count == 0 ? HealthLevel.Ok : Items.Max(i => i.Level);

    public void Add(string name, HealthLevel level, string detail) => Items.Add(new HealthItem(name, level, detail));

    public string ToText()
    {
        var sb = new StringBuilder();
        foreach (var i in Items)
            sb.AppendLine($"  {(i.Level == HealthLevel.Ok ? "ok  " : i.Level == HealthLevel.Warn ? "WARN" : "BAD ")}  {i.Name,-14} {i.Detail}");
        sb.Append($"overall: {Overall}");
        return sb.ToString();
    }
}

/// <summary>How a CA installation is laid out and tuned. Lives in <c>ca.config.json</c> beside the keys.</summary>
public sealed class CaConfig
{
    /// <summary>Where the shop's public signing key can be fetched, e.g. https://secupgrade.com/btcshop/ (optional).</summary>
    public string? ShopUrl { get; set; }
    /// <summary>The public URL of this CA, to check the website really serves this CA (optional).</summary>
    public string? PublicUrl { get; set; }
    /// <summary>
    /// A root key folder reachable from THIS machine. Only for development or a deliberately single-box CA: with it, the
    /// issuing key renews itself. Leave empty in production and sign renewals offline.
    /// </summary>
    public string? RootDir { get; set; }
    public int IssuerDays { get; set; } = 90;
    public int RenewBeforeDays { get; set; } = 21;
    public string IssuerName { get; set; } = "Kickl CA issuing key";
}

/// <summary>Everything that lives in a CA folder. The names match what the web gateway already reads.</summary>
public sealed class CaLayout(string dir)
{
    public string Dir { get; } = Path.GetFullPath(dir);
    public string IssuerDir => Path.Combine(Dir, "issuer");
    public string IssuerCert => Path.Combine(Dir, "issuer-cert.json");
    public string RootPublic => Path.Combine(Dir, "root-public.json");
    public string ShopKeys => Path.Combine(Dir, "shop-keys.txt");
    public string Data => Path.Combine(Dir, "data");
    public string Ledger => Path.Combine(Data, "ledger.jsonl");
    public string Alerts => Path.Combine(Data, "alerts.log");
    public string Revocations => Path.Combine(Dir, "revocations.json");
    public string Config => Path.Combine(Dir, "ca.config.json");
    public string PendingKeys => Path.Combine(Dir, "issuer.pending");
    public string PendingRequest => Path.Combine(Dir, "pending", "renewal-request.json");
    public string Retired => Path.Combine(Dir, "retired");
    public string Status => Path.Combine(Dir, "status.json");
    public string Log => Path.Combine(Dir, "maintain.log");
}

/// <summary>An issuing key waiting to be certified by the root. Carries only public material.</summary>
public sealed class RenewalRequest
{
    public int V { get; set; } = 1;
    public string Name { get; set; } = "";
    public long Serial { get; set; }
    public int Days { get; set; }
    public RegistrySigningKey Key { get; set; } = new();
    public long RequestedAt { get; set; }
}

public sealed record MaintainResult(HealthReport Before, HealthReport After, IReadOnlyList<string> Actions, bool NeedsHuman);

/// <summary>
/// Builds a CA from nothing, checks that it is healthy, and repairs what can be repaired without weakening it.
///
/// What it WILL fix on its own: a missing, mismatched, expired, soon-to-expire or revoked issuing key (rotating it,
/// which needs the root: automatic only when <see cref="CaConfig.RootDir"/> is set, otherwise it writes a renewal
/// request to sign offline). What it will NOT touch: a ledger that fails its audit (that is evidence, not damage), the
/// root key, or the set of shops the CA trusts. A new shop key is only ever added by a person.
/// </summary>
public static class CaOps
{
    private static readonly JsonSerializerOptions Json = TrustJson.Options;

    // ------------------------------------------------------------------ setup

    public sealed record SetupResult(bool Complete, string RootFingerprint, long IssuerSerial, string Message);

    /// <summary>
    /// Create a working CA folder. Safe to run again: it never replaces a root key or an existing, valid issuer.
    /// Give <paramref name="rootDir"/> to create (or reuse) a root on this machine, or <paramref name="rootPublicFile"/>
    /// to use a root that lives offline, in which case the first issuing certificate must be signed offline.
    /// </summary>
    public static SetupResult Setup(string caDir, CaConfig config, string? rootDir, string? rootPublicFile,
        string? shopKeySpkiB64, DateTimeOffset now)
    {
        if (config.IssuerDays < 7 || config.IssuerDays > 400)
            throw new ArgumentException("The issuing key must last between 7 and 400 days.");
        if (config.RenewBeforeDays < 1 || config.RenewBeforeDays >= config.IssuerDays)
            throw new ArgumentException(
                $"Renew-before ({config.RenewBeforeDays} days) must be shorter than the key's life ({config.IssuerDays} days), " +
                "or maintenance would renew the key on every run.");

        var l = new CaLayout(caDir);
        Directory.CreateDirectory(l.Dir);
        Directory.CreateDirectory(l.Data);

        // ---- the root
        RegistrySigningKey rootPublic;
        HybridSigner? root = null;
        try
        {
            if (rootPublicFile is not null)
            {
                rootPublic = ReadKey(rootPublicFile);
            }
            else if (rootDir is not null)
            {
                if (File.Exists(Path.Combine(rootDir, "root-public.json")) && File.Exists(Path.Combine(rootDir, "ed25519.seed")))
                {
                    root = HybridSigner.LoadFrom(rootDir);
                    rootPublic = ReadKey(Path.Combine(rootDir, "root-public.json"));
                    if (root.Public.Fingerprint != rootPublic.Fingerprint)
                        throw new InvalidOperationException("The root's private and public files do not belong together.");
                }
                else
                {
                    root = HybridSigner.Generate();
                    root.SaveTo(rootDir);
                    rootPublic = root.Public;
                    File.WriteAllText(Path.Combine(rootDir, "root-public.json"), JsonSerializer.Serialize(rootPublic, RegistrySigningKey.Json));
                }
                config.RootDir = rootDir;
            }
            else if (File.Exists(l.RootPublic))
            {
                rootPublic = ReadKey(l.RootPublic);
            }
            else
            {
                throw new ArgumentException("Give a root: --root-dir (create or use one here) or --root-public (an offline root).");
            }

            if (File.Exists(l.RootPublic) && ReadKey(l.RootPublic).Fingerprint != rootPublic.Fingerprint)
                throw new InvalidOperationException("This CA folder already trusts a different root. Refusing to replace it.");
            File.WriteAllText(l.RootPublic, JsonSerializer.Serialize(rootPublic, RegistrySigningKey.Json));

            // ---- shop keys
            if (shopKeySpkiB64 is not null && !(File.Exists(l.ShopKeys) && File.ReadAllLines(l.ShopKeys).Contains(shopKeySpkiB64)))
                File.AppendAllText(l.ShopKeys, shopKeySpkiB64 + "\n");
            if (!File.Exists(l.ShopKeys)) File.WriteAllText(l.ShopKeys, "");

            File.WriteAllText(l.Config, JsonSerializer.Serialize(config, Json));

            // ---- the issuing key
            if (File.Exists(l.IssuerCert) && Directory.Exists(l.IssuerDir) && IssuerUsable(l, rootPublic, now, out var existing))
                return new SetupResult(true, rootPublic.Fingerprint, existing!.Serial, "The CA is already set up; its issuing key is valid.");

            var request = CreateRenewalRequest(caDir, now, firstTime: true);
            if (root is not null)
            {
                var cert = SignRenewal(rootDir!, l.PendingRequest, now);
                InstallRenewal(caDir, cert, now);
                return new SetupResult(true, rootPublic.Fingerprint, cert.Serial, $"CA built. Issuing key #{cert.Serial} is valid for {request.Days} days.");
            }

            return new SetupResult(false, rootPublic.Fingerprint, 0,
                "Almost there: the issuing key needs a signature from the offline root. Run on the root machine:\n" +
                $"  kickl-ca sign-renewal --root-dir <root> --request \"{l.PendingRequest}\" --out issuer-cert.json\n" +
                $"then here:  kickl-ca install-renewal --ca-dir \"{l.Dir}\" --cert issuer-cert.json");
        }
        finally
        {
            root?.Dispose();
        }
    }

    // ------------------------------------------------------------------ renewal (the three steps)

    /// <summary>Step 1, on the CA server: make a new issuing key and a request for the root to certify. Idempotent.</summary>
    public static RenewalRequest CreateRenewalRequest(string caDir, DateTimeOffset now, bool firstTime = false)
    {
        var l = new CaLayout(caDir);
        var cfg = ReadConfig(l);
        if (File.Exists(l.PendingRequest) && Directory.Exists(l.PendingKeys))
        {
            var existing = JsonSerializer.Deserialize<RenewalRequest>(File.ReadAllText(l.PendingRequest), Json);
            if (existing is not null) return existing;   // a request is already waiting; do not pile up keys
        }

        using var key = HybridSigner.Generate();
        if (Directory.Exists(l.PendingKeys)) Directory.Delete(l.PendingKeys, true);
        key.SaveTo(l.PendingKeys, KeyProtection.Machine);
        File.WriteAllText(Path.Combine(l.PendingKeys, "issuer-public.json"), JsonSerializer.Serialize(key.Public, RegistrySigningKey.Json));

        var request = new RenewalRequest
        {
            Name = cfg.IssuerName,
            Serial = NextSerial(l),
            Days = cfg.IssuerDays,
            Key = key.Public,
            RequestedAt = now.ToUnixTimeSeconds()
        };
        Directory.CreateDirectory(Path.GetDirectoryName(l.PendingRequest)!);
        File.WriteAllText(l.PendingRequest, JsonSerializer.Serialize(request, Json));
        Log(l, now, firstTime ? $"first issuing key requested (serial {request.Serial})" : $"renewal requested (serial {request.Serial})");
        return request;
    }

    /// <summary>Step 2, on the OFFLINE root machine: certify the requested key.</summary>
    public static IssuerCertificate SignRenewal(string rootDir, string requestPath, DateTimeOffset now)
    {
        using var root = HybridSigner.LoadFrom(rootDir);
        var request = JsonSerializer.Deserialize<RenewalRequest>(File.ReadAllText(requestPath), Json)
                      ?? throw new FormatException("The renewal request is unreadable.");
        request.Key.Validate();
        if (request.Days is < 1 or > 400) throw new ArgumentException("Refusing an issuing certificate outside 1 to 400 days.");
        return CaService.IssueIssuer(root, request.Key, request.Name, request.Serial, now.AddMinutes(-5), TimeSpan.FromDays(request.Days));
    }

    /// <summary>
    /// Step 3, on the CA server: install the certified key. The certificate must chain to the pinned root and be for the
    /// pending key. The old key's private files are destroyed; only its public certificate is kept, for the record.
    /// </summary>
    public static void InstallRenewal(string caDir, IssuerCertificate cert, DateTimeOffset now)
    {
        var l = new CaLayout(caDir);
        var root = ReadKey(l.RootPublic);
        if (!Directory.Exists(l.PendingKeys)) throw new InvalidOperationException("There is no pending issuing key. Create a renewal request first.");

        using (var pending = HybridSigner.LoadFrom(l.PendingKeys))
        {
            if (pending.Public.Fingerprint != cert.Key.Fingerprint)
                throw new InvalidOperationException("That certificate is for a different key than the pending one.");
        }

        var check = CaTrust.VerifyIssuer(root, cert, now, null);
        if (!check.Verified) throw new InvalidOperationException("The certificate is not acceptable: " + check.Reason);

        var backup = l.IssuerDir + ".swap-" + now.ToUnixTimeSeconds();
        var hadOld = Directory.Exists(l.IssuerDir);
        var oldCert = File.Exists(l.IssuerCert) ? File.ReadAllText(l.IssuerCert) : null;
        try
        {
            if (hadOld) Directory.Move(l.IssuerDir, backup);
            Directory.Move(l.PendingKeys, l.IssuerDir);
            File.WriteAllText(l.IssuerCert, JsonSerializer.Serialize(cert, Json));
        }
        catch
        {
            // put everything back exactly as it was
            if (Directory.Exists(l.IssuerDir) && hadOld) Directory.Move(l.IssuerDir, l.PendingKeys);
            if (hadOld && Directory.Exists(backup)) Directory.Move(backup, l.IssuerDir);
            if (oldCert is not null) File.WriteAllText(l.IssuerCert, oldCert);
            throw;
        }

        if (hadOld)
        {
            // keep the old certificate (public) for the record, then destroy the old private key
            Directory.CreateDirectory(l.Retired);
            if (oldCert is not null) File.WriteAllText(Path.Combine(l.Retired, $"issuer-cert-{Serial(oldCert)}.json"), oldCert);
            DestroyKeys(backup);
        }
        if (File.Exists(l.PendingRequest)) File.Delete(l.PendingRequest);
        Log(l, now, $"issuing key #{cert.Serial} installed, valid until {DateTimeOffset.FromUnixTimeSeconds(cert.NotAfter):u}");
    }

    /// <summary>
    /// Wrap the issuing private key with Windows DPAPI (a no-op if it already is) and lock its folder to the owner.
    /// It is read back and used to sign before the plaintext copy is destroyed, so a failure never loses the key.
    /// </summary>
    public static KeyProtection ProtectIssuerKey(string caDir, DateTimeOffset now)
    {
        var l = new CaLayout(caDir);
        if (!Directory.Exists(l.IssuerDir)) throw new InvalidOperationException("There is no issuing key to protect.");
        if (HybridSigner.ProtectionOf(l.IssuerDir) == KeyProtection.Machine)
        {
            FileProtection.RestrictTree(l.IssuerDir);
            return KeyProtection.Machine;
        }

        // Remember the plaintext in memory. If the wrapped copy does not read back as the same key, put the original
        // files back exactly as they were: protecting the key must never be able to lose it.
        var seedPath = Path.Combine(l.IssuerDir, "ed25519.seed");
        var mlPath = Path.Combine(l.IssuerDir, "mldsa65.pkcs8");
        var seedBytes = File.ReadAllBytes(seedPath);
        var mlBytes = File.ReadAllBytes(mlPath);
        try
        {
            string fingerprint;
            using (var original = HybridSigner.LoadFrom(l.IssuerDir)) fingerprint = original.Public.Fingerprint;
            KeyProtection applied;
            try
            {
                applied = HybridSigner.LoadFrom(l.IssuerDir).SaveTo(l.IssuerDir, KeyProtection.Machine);
                using var check = HybridSigner.LoadFrom(l.IssuerDir);
                if (check.Public.Fingerprint != fingerprint)
                    throw new CryptographicException("The wrapped key did not read back as the same key.");
            }
            catch
            {
                File.WriteAllBytes(seedPath, seedBytes);
                File.WriteAllBytes(mlPath, mlBytes);
                FileProtection.Shred(seedPath + ".dpapi");
                FileProtection.Shred(mlPath + ".dpapi");
                throw;
            }
            Log(l, now, applied == KeyProtection.Machine ? "issuing private key wrapped with DPAPI" : "issuing private key restricted to its owner");
            return applied;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seedBytes);
            CryptographicOperations.ZeroMemory(mlBytes);
        }
    }

    // ------------------------------------------------------------------ health

    public static async Task<HealthReport> HealthAsync(string caDir, DateTimeOffset now,
        Func<string, CancellationToken, Task<string?>>? fetchShopKey = null,
        Func<string, CancellationToken, Task<string?>>? fetchPublicRoot = null,
        CancellationToken ct = default)
    {
        var l = new CaLayout(caDir);
        var r = new HealthReport { CheckedAt = now };
        var cfg = File.Exists(l.Config) ? ReadConfig(l) : new CaConfig();

        // ---- settings that would make maintenance misbehave
        if (cfg.RenewBeforeDays >= cfg.IssuerDays || cfg.RenewBeforeDays < 1)
            r.Add("config", HealthLevel.Bad, $"renew-before ({cfg.RenewBeforeDays} days) must be shorter than the key's life ({cfg.IssuerDays} days), or the key would be renewed on every run");

        // ---- trust anchors present
        RegistrySigningKey? root = null;
        try { root = ReadKey(l.RootPublic); r.Add("root", HealthLevel.Ok, "pinned root " + root.Fingerprint[..16] + "…"); }
        catch (Exception ex) when (ex is IOException or JsonException or FormatException or ArgumentException)
        { r.Add("root", HealthLevel.Bad, "root-public.json is missing or unreadable"); }

        // ---- the issuing key and its certificate
        IssuerCertificate? cert = null;
        if (!File.Exists(l.IssuerCert) || !Directory.Exists(l.IssuerDir))
        {
            r.Add("issuer", HealthLevel.Bad, "no issuing key installed");
        }
        else if (root is not null)
        {
            try
            {
                cert = JsonSerializer.Deserialize<IssuerCertificate>(File.ReadAllText(l.IssuerCert), Json)!;
                using var key = HybridSigner.LoadFrom(l.IssuerDir);
                if (key.Public.Fingerprint != cert.Key.Fingerprint)
                {
                    r.Add("issuer", HealthLevel.Bad, "the issuing key does not match its certificate");
                }
                else
                {
                    var revoked = LoadRevocations(l, root, cert, now);
                    var check = CaTrust.VerifyIssuer(root, cert, now, revoked);
                    var daysLeft = (DateTimeOffset.FromUnixTimeSeconds(cert.NotAfter) - now).TotalDays;
                    if (!check.Verified) r.Add("issuer", HealthLevel.Bad, $"#{cert.Serial}: {check.Reason}");
                    else if (daysLeft <= cfg.RenewBeforeDays) r.Add("issuer", HealthLevel.Warn, $"#{cert.Serial} expires in {daysLeft:0} day(s); due for renewal");
                    else r.Add("issuer", HealthLevel.Ok, $"#{cert.Serial} valid, {daysLeft:0} days left");
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or FormatException or CryptographicException or ArgumentException)
            {
                r.Add("issuer", HealthLevel.Bad, "the issuing key or its certificate is unreadable: " + ex.Message);
            }
        }

        // ---- how the issuing private key is stored at rest
        if (Directory.Exists(l.IssuerDir))
        {
            var protection = HybridSigner.ProtectionOf(l.IssuerDir);
            if (protection == KeyProtection.Machine)
                r.Add("issuer-key", HealthLevel.Ok, "private key is wrapped with Windows DPAPI and restricted to its owner");
            else if (OperatingSystem.IsWindows())
                r.Add("issuer-key", HealthLevel.Warn, "private key is stored unwrapped; maintenance will wrap it");
            else
                r.Add("issuer-key", HealthLevel.Ok, "private key file is mode 0600 (DPAPI is Windows-only)");
        }

        // ---- a renewal waiting for the root
        if (File.Exists(l.PendingRequest))
            r.Add("renewal", HealthLevel.Warn, "a renewal request is waiting to be signed by the offline root");

        // ---- the ledger is evidence: audit it, never repair it
        var entries = ReadLedger(l, out var ledgerError);
        if (ledgerError is not null) r.Add("ledger", HealthLevel.Bad, ledgerError);
        else
        {
            var problems = CaService.AuditEntries(entries);
            r.Add("ledger", problems.Count == 0 ? HealthLevel.Ok : HealthLevel.Bad,
                problems.Count == 0 ? $"{entries.Count} entr{(entries.Count == 1 ? "y" : "ies")}, consistent" : $"AUDIT FAILED: {problems[0]} (+{problems.Count - 1} more)");
        }

        // ---- conflicts the CA has logged since the last check
        var lastCheck = File.Exists(l.Status) ? File.GetLastWriteTimeUtc(l.Status) : DateTime.MinValue;
        if (File.Exists(l.Alerts))
        {
            var fresh = File.ReadLines(l.Alerts).Count(line => DateTimeOffset.TryParse(line.Split('\t')[0], out var t) && t.UtcDateTime > lastCheck);
            r.Add("alerts", fresh > 0 ? HealthLevel.Warn : HealthLevel.Ok, fresh > 0 ? $"{fresh} new conflict alert(s) in alerts.log" : "no new alerts");
        }

        // ---- shop trust
        var trusted = File.Exists(l.ShopKeys) ? File.ReadAllLines(l.ShopKeys).Select(x => x.Trim()).Where(x => x.Length > 0).ToList() : [];
        if (trusted.Count == 0) r.Add("shop-key", HealthLevel.Bad, "the CA trusts no shop, so it will refuse every receipt");
        else if (cfg.ShopUrl is not null && fetchShopKey is not null)
        {
            string? live = null;
            try { live = await fetchShopKey(cfg.ShopUrl, ct); } catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }
            if (live is null) r.Add("shop-key", HealthLevel.Warn, "could not reach the shop to compare its key");
            else if (!trusted.Contains(live)) r.Add("shop-key", HealthLevel.Warn, "the shop is using a key this CA does not trust. If you rotated it on purpose, add it with trust-shop");
            else r.Add("shop-key", HealthLevel.Ok, $"the shop's current key is trusted ({trusted.Count} key(s) listed)");
        }
        else r.Add("shop-key", HealthLevel.Ok, $"{trusted.Count} trusted shop key(s)");

        // ---- the public website serves this CA
        if (cfg.PublicUrl is not null && fetchPublicRoot is not null && root is not null)
        {
            string? served = null;
            try { served = await fetchPublicRoot(cfg.PublicUrl, ct); } catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }
            if (served is null) r.Add("website", HealthLevel.Warn, "the CA website did not answer");
            else if (served != root.Fingerprint) r.Add("website", HealthLevel.Bad, "the CA website serves a different root than this folder pins");
            else r.Add("website", HealthLevel.Ok, "the website serves this CA");
        }

        return r;
    }

    // ------------------------------------------------------------------ maintain

    /// <summary>Check, then fix what can safely be fixed. Records the result in <c>status.json</c> and <c>maintain.log</c>.</summary>
    public static async Task<MaintainResult> MaintainAsync(string caDir, DateTimeOffset now, bool dryRun = false,
        Func<string, CancellationToken, Task<string?>>? fetchShopKey = null,
        Func<string, CancellationToken, Task<string?>>? fetchPublicRoot = null,
        CancellationToken ct = default)
    {
        var l = new CaLayout(caDir);
        var before = await HealthAsync(caDir, now, fetchShopKey, fetchPublicRoot, ct);
        var actions = new List<string>();
        var needsHuman = false;
        var cfg = File.Exists(l.Config) ? ReadConfig(l) : new CaConfig();

        var issuer = before.Items.FirstOrDefault(i => i.Name == "issuer");
        var rotate = issuer is { Level: not HealthLevel.Ok };

        if (!dryRun && before.Items.Any(i => i.Name == "issuer-key" && i.Level == HealthLevel.Warn) && !rotate)
        {
            try
            {
                ProtectIssuerKey(caDir, now);
                actions.Add("wrapped the issuing private key with Windows DPAPI and restricted it to its owner");
            }
            catch (Exception ex) when (ex is IOException or CryptographicException or UnauthorizedAccessException)
            {
                needsHuman = true;
                actions.Add("could not protect the issuing key: " + ex.Message);
            }
        }

        if (rotate && !dryRun)
        {
            try
            {
                var request = CreateRenewalRequest(caDir, now);
                actions.Add($"prepared a new issuing key (serial {request.Serial})");

                if (cfg.RootDir is not null && File.Exists(Path.Combine(cfg.RootDir, "ed25519.seed")))
                {
                    var cert = SignRenewal(cfg.RootDir, l.PendingRequest, now);
                    InstallRenewal(caDir, cert, now);
                    actions.Add($"root on this machine certified it; issuing key #{cert.Serial} installed, the old private key destroyed");
                }
                else
                {
                    needsHuman = true;
                    actions.Add($"a renewal request is waiting at {l.PendingRequest}; sign it on the offline root machine, then run install-renewal");
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or FormatException or ArgumentException or CryptographicException)
            {
                needsHuman = true;
                actions.Add("could not renew the issuing key: " + ex.Message);
            }
        }
        else if (rotate)
        {
            actions.Add("would renew the issuing key (dry run)");
        }

        var after = actions.Count == 0 ? before : await HealthAsync(caDir, now, fetchShopKey, fetchPublicRoot, ct);

        // things only a person can resolve
        foreach (var item in after.Items.Where(i => i.Level == HealthLevel.Bad && i.Name != "issuer"))
        {
            needsHuman = true;
            actions.Add($"needs a person: {item.Name}: {item.Detail}");
        }
        if (after.Items.Any(i => i.Name == "shop-key" && i.Level == HealthLevel.Warn && i.Detail.Contains("does not trust")))
            needsHuman = true;
        // we tried to renew and the key is still not healthy, and no request is waiting for the root: a person must look
        if (!dryRun && rotate && after.Items.Any(i => i.Name == "issuer" && i.Level != HealthLevel.Ok) && !File.Exists(l.PendingRequest))
            needsHuman = true;

        if (!dryRun)
        {
            File.WriteAllText(l.Status, JsonSerializer.Serialize(new
            {
                checkedAt = now.ToUnixTimeSeconds(),
                overall = after.Overall.ToString(),
                needsHuman,
                items = after.Items.Select(i => new { i.Name, level = i.Level.ToString(), i.Detail }),
                actions
            }, Json));
            Log(l, now, $"maintain: {before.Overall} -> {after.Overall}; {(actions.Count == 0 ? "nothing to do" : string.Join("; ", actions))}");
        }
        return new MaintainResult(before, after, actions, needsHuman);
    }

    // ------------------------------------------------------------------ helpers

    private static RegistrySigningKey ReadKey(string path)
    {
        var key = JsonSerializer.Deserialize<RegistrySigningKey>(File.ReadAllText(path), RegistrySigningKey.Json)
                  ?? throw new FormatException($"'{path}' is unreadable.");
        key.Validate();
        return key;
    }

    private static CaConfig ReadConfig(CaLayout l) =>
        File.Exists(l.Config) ? JsonSerializer.Deserialize<CaConfig>(File.ReadAllText(l.Config), Json) ?? new CaConfig() : new CaConfig();

    private static long Serial(string certJson) => JsonSerializer.Deserialize<IssuerCertificate>(certJson, Json)?.Serial ?? 0;

    private static long NextSerial(CaLayout l)
    {
        long max = 0;
        if (File.Exists(l.IssuerCert)) max = Math.Max(max, Serial(File.ReadAllText(l.IssuerCert)));
        if (Directory.Exists(l.Retired))
            foreach (var f in Directory.EnumerateFiles(l.Retired, "issuer-cert-*.json"))
                max = Math.Max(max, Serial(File.ReadAllText(f)));
        return max + 1;
    }

    private static bool IssuerUsable(CaLayout l, RegistrySigningKey root, DateTimeOffset now, out IssuerCertificate? cert)
    {
        cert = null;
        try
        {
            cert = JsonSerializer.Deserialize<IssuerCertificate>(File.ReadAllText(l.IssuerCert), Json);
            if (cert is null) return false;
            using var key = HybridSigner.LoadFrom(l.IssuerDir);
            return key.Public.Fingerprint == cert.Key.Fingerprint && CaTrust.VerifyIssuer(root, cert, now, null).Verified;
        }
        catch (Exception ex) when (ex is IOException or JsonException or FormatException or CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    private static RevocationList? LoadRevocations(CaLayout l, RegistrySigningKey root, IssuerCertificate issuer, DateTimeOffset now)
    {
        if (!File.Exists(l.Revocations)) return null;
        try
        {
            var list = JsonSerializer.Deserialize<RevocationList>(File.ReadAllText(l.Revocations), Json);
            return list is not null && CaTrust.VerifyRevocations(root, list, issuer, now) ? list : null;
        }
        catch (JsonException) { return null; }
    }

    private static List<LedgerEntry> ReadLedger(CaLayout l, out string? error)
    {
        error = null;
        var entries = new List<LedgerEntry>();
        if (!File.Exists(l.Ledger)) return entries;
        try
        {
            foreach (var line in File.ReadLines(l.Ledger).Where(x => x.Length > 0))
                entries.Add(JsonSerializer.Deserialize<LedgerEntry>(line, Json) ?? throw new JsonException("empty entry"));
        }
        catch (JsonException ex)
        {
            error = "the ledger cannot be parsed: " + ex.Message;
        }
        return entries;
    }

    /// <summary>Overwrite, then delete. Best effort on a journaling disk, but it stops a casual recovery.</summary>
    private static void DestroyKeys(string dir)
    {
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            try
            {
                var length = new FileInfo(file).Length;
                File.WriteAllBytes(file, RandomNumberGenerator.GetBytes((int)Math.Min(length, int.MaxValue)));
            }
            catch (IOException) { }
        }
        Directory.Delete(dir, true);
    }

    private static void Log(CaLayout l, DateTimeOffset now, string line) =>
        File.AppendAllText(l.Log, $"{now:u}  {line}\n");
}
