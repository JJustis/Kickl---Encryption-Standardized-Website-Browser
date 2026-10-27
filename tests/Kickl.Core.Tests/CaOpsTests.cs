using System.Text.Json;
using Kickl.Core.Registry;
using Kickl.Core.Trust;
using Xunit;

namespace Kickl.Core.Tests;

public class CaOpsTests
{
    private static readonly JsonSerializerOptions Camel = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    private sealed class Rig : IDisposable
    {
        public readonly TempDir Temp = new();
        public readonly FakeShop Shop = new();
        public readonly DateTimeOffset T0 = DateTimeOffset.UtcNow;
        public string CaDir => Path.Combine(Temp.Path, "ca");
        public string RootDir => Path.Combine(Temp.Path, "root");
        public CaLayout L => new(CaDir);

        public CaOps.SetupResult SetupLocalRoot(CaConfig? cfg = null) =>
            CaOps.Setup(CaDir, cfg ?? new CaConfig(), RootDir, null, Shop.Spki, T0);

        /// <summary>The CA as the web gateway would run it today: the issuing key and certificate on disk.</summary>
        public CaService Service(out FileCaStore store, out HybridSigner key)
        {
            key = HybridSigner.LoadFrom(L.IssuerDir);
            var cert = JsonSerializer.Deserialize<IssuerCertificate>(File.ReadAllText(L.IssuerCert), Camel)!;
            var root = JsonSerializer.Deserialize<RegistrySigningKey>(File.ReadAllText(L.RootPublic), RegistrySigningKey.Json)!;
            store = new FileCaStore(L.Data);
            return new CaService(key, cert, root, [Shop.Spki], store);
        }

        public RegistrySigningKey Root => JsonSerializer.Deserialize<RegistrySigningKey>(File.ReadAllText(L.RootPublic), RegistrySigningKey.Json)!;

        public CaSeal SealSomething(string site, DateTimeOffset at, long order = 1)
        {
            var ca = Service(out var store, out var key);
            try { return ca.Countersign(Shop.Sign(site, order: order), at); }
            finally { store.Dispose(); key.Dispose(); }
        }

        public long IssuerSerial => JsonSerializer.Deserialize<IssuerCertificate>(File.ReadAllText(L.IssuerCert), Camel)!.Serial;

        public Task<HealthReport> Health(DateTimeOffset at, Func<string, CancellationToken, Task<string?>>? shop = null) =>
            CaOps.HealthAsync(CaDir, at, shop);

        public void Dispose()
        {
            Shop.Dispose();
            Temp.Dispose();
        }
    }

    [Fact]
    public async Task SetupBuildsAWorkingCaFromNothing_AndIsSafeToRunAgain()
    {
        using var rig = new Rig();
        var result = rig.SetupLocalRoot();

        Assert.True(result.Complete, result.Message);
        Assert.Equal(1, result.IssuerSerial);
        foreach (var f in new[] { rig.L.RootPublic, rig.L.IssuerCert, rig.L.ShopKeys, rig.L.Config })
            Assert.True(File.Exists(f), f);
        Assert.True(Directory.Exists(rig.L.IssuerDir));
        Assert.True(File.Exists(Path.Combine(rig.RootDir, "ed25519.seed")));   // the root was created where asked, not in the CA folder
        Assert.False(File.Exists(Path.Combine(rig.CaDir, "ed25519.seed")));

        Assert.Equal(HealthLevel.Ok, (await rig.Health(rig.T0)).Overall);

        // it really can seal
        Assert.True(CaTrust.VerifySeal(rig.Root, rig.SealSomething("fresh.com", rig.T0), "fresh.com", rig.T0).Verified);

        // running setup again changes nothing: same root, same issuing key
        var rootBefore = File.ReadAllText(rig.L.RootPublic);
        var certBefore = File.ReadAllText(rig.L.IssuerCert);
        var again = rig.SetupLocalRoot();
        Assert.True(again.Complete);
        Assert.Equal(rootBefore, File.ReadAllText(rig.L.RootPublic));
        Assert.Equal(certBefore, File.ReadAllText(rig.L.IssuerCert));
    }

    [Fact]
    public void SetupNeverSwapsTheRootOfAnExistingCa()
    {
        using var rig = new Rig();
        rig.SetupLocalRoot();
        using var other = HybridSigner.Generate();
        var otherPublic = Path.Combine(rig.Temp.Path, "other-root.json");
        File.WriteAllText(otherPublic, JsonSerializer.Serialize(other.Public, RegistrySigningKey.Json));

        Assert.Throws<InvalidOperationException>(() => CaOps.Setup(rig.CaDir, new CaConfig(), null, otherPublic, null, rig.T0));
    }

    [Fact]
    public async Task AnExpiringIssuingKeyIsRenewedAutomatically_AndOldSealsKeepWorking()
    {
        using var rig = new Rig();
        rig.SetupLocalRoot();
        var oldSeal = rig.SealSomething("kept.com", rig.T0);
        Assert.Equal(1, rig.IssuerSerial);

        var soon = rig.T0.AddDays(75);                               // 15 days left: inside the 21-day renewal window
        Assert.Equal(HealthLevel.Warn, (await rig.Health(soon)).Overall);

        var result = await CaOps.MaintainAsync(rig.CaDir, soon);
        Assert.Contains(result.Actions, a => a.Contains("issuing key #2"));
        Assert.False(result.NeedsHuman);
        Assert.Equal(HealthLevel.Ok, result.After.Overall);
        Assert.Equal(2, rig.IssuerSerial);

        // the old private key is gone; only its public certificate is kept for the record
        Assert.False(Directory.Exists(rig.L.IssuerDir + ".swap-" + soon.ToUnixTimeSeconds()));
        Assert.True(File.Exists(Path.Combine(rig.L.Retired, "issuer-cert-1.json")));
        Assert.Empty(Directory.EnumerateFiles(rig.L.Retired, "*.seed", SearchOption.AllDirectories));

        // a seal issued under key #1 still verifies long after #1 expired, and the new key seals new claims
        Assert.True(CaTrust.VerifySeal(rig.Root, oldSeal, "kept.com", rig.T0.AddDays(300)).Verified);
        var newSeal = rig.SealSomething("later.com", soon.AddDays(1), order: 2);
        Assert.Equal(2, newSeal.Issuer.Serial);
        Assert.True(CaTrust.VerifySeal(rig.Root, newSeal, "later.com", soon.AddDays(2)).Verified);

        // and it stays quiet afterwards
        var quiet = await CaOps.MaintainAsync(rig.CaDir, soon.AddDays(2));
        Assert.Empty(quiet.Actions);
    }

    [Fact]
    public async Task AnExpiredIssuingKeyIsReplaced()
    {
        using var rig = new Rig();
        rig.SetupLocalRoot();
        var late = rig.T0.AddDays(120);
        Assert.Equal(HealthLevel.Bad, (await rig.Health(late)).Overall);

        var result = await CaOps.MaintainAsync(rig.CaDir, late);
        Assert.Equal(HealthLevel.Ok, result.After.Overall);
        Assert.Equal(2, rig.IssuerSerial);
    }

    [Fact]
    public async Task WithoutTheRootOnTheMachineItPreparesARequestAndTellsAHumanExactlyWhat()
    {
        using var rig = new Rig();
        // the root is made "offline": only its public half is given to the CA machine
        using var offlineRoot = HybridSigner.Generate();
        offlineRoot.SaveTo(rig.RootDir);
        var rootPub = Path.Combine(rig.Temp.Path, "root-public.json");
        File.WriteAllText(rootPub, JsonSerializer.Serialize(offlineRoot.Public, RegistrySigningKey.Json));

        var setup = CaOps.Setup(rig.CaDir, new CaConfig(), null, rootPub, rig.Shop.Spki, rig.T0);
        Assert.False(setup.Complete);
        Assert.Contains("sign-renewal", setup.Message);
        Assert.True(File.Exists(rig.L.PendingRequest));

        // offline: the root certifies the request; online: it is installed
        var cert = CaOps.SignRenewal(rig.RootDir, rig.L.PendingRequest, rig.T0);
        CaOps.InstallRenewal(rig.CaDir, cert, rig.T0);
        Assert.Equal(HealthLevel.Ok, (await rig.Health(rig.T0)).Overall);
        Assert.False(File.Exists(rig.L.PendingRequest));

        // later the key is due: automatic maintenance cannot sign (no root here), so it asks for a human, once
        var soon = rig.T0.AddDays(75);
        var first = await CaOps.MaintainAsync(rig.CaDir, soon);
        Assert.True(first.NeedsHuman);
        Assert.Contains(first.Actions, a => a.Contains("offline root"));
        var requestBytes = File.ReadAllBytes(rig.L.PendingRequest);

        var second = await CaOps.MaintainAsync(rig.CaDir, soon.AddHours(6));
        Assert.True(second.NeedsHuman);
        Assert.Equal(requestBytes, File.ReadAllBytes(rig.L.PendingRequest));                 // no pile of keys
        Assert.Single(Directory.GetDirectories(rig.CaDir, "issuer*"), d => d.EndsWith("issuer.pending"));

        // the old key keeps working in the meantime
        Assert.True(CaTrust.VerifySeal(offlineRoot.Public, rig.SealSomething("still.com", soon), "still.com", soon).Verified);

        // the human signs offline and installs; everything is healthy again
        var renewal = CaOps.SignRenewal(rig.RootDir, rig.L.PendingRequest, soon);
        CaOps.InstallRenewal(rig.CaDir, renewal, soon);
        Assert.Equal(2, rig.IssuerSerial);
        Assert.Equal(HealthLevel.Ok, (await rig.Health(soon)).Overall);
    }

    [Fact]
    public void ACertificateForTheWrongKeyOrSignedByTheWrongRootIsNotInstalled()
    {
        using var rig = new Rig();
        rig.SetupLocalRoot();
        CaOps.CreateRenewalRequest(rig.CaDir, rig.T0.AddDays(75));
        var request = JsonSerializer.Deserialize<RenewalRequest>(File.ReadAllText(rig.L.PendingRequest), Camel)!;

        using var rogueRoot = HybridSigner.Generate();
        using var otherKey = HybridSigner.Generate();
        var wrongKey = CaService.IssueIssuer(rogueRoot, otherKey.Public, "x", request.Serial, rig.T0, TimeSpan.FromDays(90));
        Assert.Throws<InvalidOperationException>(() => CaOps.InstallRenewal(rig.CaDir, wrongKey, rig.T0));

        var wrongSigner = CaService.IssueIssuer(rogueRoot, request.Key, "x", request.Serial, rig.T0, TimeSpan.FromDays(90));
        Assert.Throws<InvalidOperationException>(() => CaOps.InstallRenewal(rig.CaDir, wrongSigner, rig.T0));

        Assert.Equal(1, rig.IssuerSerial);                                                   // nothing changed
    }

    [Fact]
    public async Task ARevokedIssuingKeyIsReplaced()
    {
        using var rig = new Rig();
        rig.SetupLocalRoot();
        rig.SealSomething("victim.com", rig.T0);

        using var root = HybridSigner.LoadFrom(rig.RootDir);
        var revocation = CaService.RevokeAsRoot(root, null, [1L], [], rig.T0);
        File.WriteAllText(rig.L.Revocations, JsonSerializer.Serialize(revocation, Camel));

        Assert.Equal(HealthLevel.Bad, (await rig.Health(rig.T0.AddMinutes(1))).Overall);
        var result = await CaOps.MaintainAsync(rig.CaDir, rig.T0.AddMinutes(1));
        Assert.Equal(2, rig.IssuerSerial);
        Assert.Equal(HealthLevel.Ok, result.After.Overall);
    }

    [Fact]
    public async Task ATamperedLedgerIsReportedAndLeftExactlyAsFound()
    {
        using var rig = new Rig();
        rig.SetupLocalRoot();
        rig.SealSomething("a.com", rig.T0, order: 1);
        rig.SealSomething("b.com", rig.T0, order: 2);

        // rewrite the first entry's leaf text, as a tamperer would
        var lines = File.ReadAllLines(rig.L.Ledger);
        lines[0] = lines[0].Replace("a.com", "z.com");
        File.WriteAllLines(rig.L.Ledger, lines);
        var tampered = File.ReadAllBytes(rig.L.Ledger);

        var health = await rig.Health(rig.T0);
        Assert.Equal(HealthLevel.Bad, health.Items.Single(i => i.Name == "ledger").Level);

        var result = await CaOps.MaintainAsync(rig.CaDir, rig.T0);
        Assert.True(result.NeedsHuman);
        Assert.Equal(tampered, File.ReadAllBytes(rig.L.Ledger));                             // evidence is never "repaired"
        Assert.Equal(1, rig.IssuerSerial);                                                   // and a good key is not rotated over it
    }

    [Fact]
    public async Task AChangedShopKeyIsFlaggedButNeverTrustedAutomatically()
    {
        using var rig = new Rig();
        rig.SetupLocalRoot(new CaConfig { ShopUrl = "https://shop.example/" });
        var before = File.ReadAllText(rig.L.ShopKeys);

        using var attacker = new FakeShop();
        var health = await rig.Health(rig.T0, (_, _) => Task.FromResult<string?>(attacker.Spki));
        var item = health.Items.Single(i => i.Name == "shop-key");
        Assert.Equal(HealthLevel.Warn, item.Level);

        var result = await CaOps.MaintainAsync(rig.CaDir, rig.T0, fetchShopKey: (_, _) => Task.FromResult<string?>(attacker.Spki));
        Assert.True(result.NeedsHuman);
        Assert.Equal(before, File.ReadAllText(rig.L.ShopKeys));                              // not added

        var same = await rig.Health(rig.T0, (_, _) => Task.FromResult<string?>(rig.Shop.Spki));
        Assert.Equal(HealthLevel.Ok, same.Items.Single(i => i.Name == "shop-key").Level);
    }

    [Fact]
    public async Task ConflictAlertsAreReportedOnceThenQuiet()
    {
        using var rig = new Rig();
        rig.SetupLocalRoot();
        File.WriteAllText(rig.L.Status, "{}");                                               // a previous check happened
        File.SetLastWriteTimeUtc(rig.L.Status, DateTime.UtcNow.AddMinutes(-5));
        File.AppendAllText(rig.L.Alerts, $"{DateTimeOffset.UtcNow:O}\tconflict\tx.com seq 1 clash\n");

        var first = await CaOps.MaintainAsync(rig.CaDir, DateTimeOffset.UtcNow);
        Assert.Equal(HealthLevel.Warn, first.Before.Items.Single(i => i.Name == "alerts").Level);

        var second = await CaOps.MaintainAsync(rig.CaDir, DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.Equal(HealthLevel.Ok, second.Before.Items.Single(i => i.Name == "alerts").Level);
    }

    [Fact]
    public async Task ABadRenewalWindowIsFlaggedInsteadOfRenewingForever()
    {
        using var rig = new Rig();
        rig.SetupLocalRoot();
        var cfg = JsonSerializer.Deserialize<CaConfig>(File.ReadAllText(rig.L.Config), Camel)!;
        cfg.RenewBeforeDays = cfg.IssuerDays + 5;
        File.WriteAllText(rig.L.Config, JsonSerializer.Serialize(cfg, Camel));

        var health = await rig.Health(rig.T0);
        Assert.Equal(HealthLevel.Bad, health.Items.Single(i => i.Name == "config").Level);

        // a configuration mistake is something only a person can fix, and maintenance says so instead of renewing forever
        var dry = await CaOps.MaintainAsync(rig.CaDir, rig.T0, dryRun: true);
        Assert.True(dry.NeedsHuman);
        Assert.Contains(dry.Actions, a => a.Contains("config"));
        Assert.Equal(1, rig.IssuerSerial);

        Assert.Throws<ArgumentException>(() => CaOps.Setup(Path.Combine(rig.Temp.Path, "other"), new CaConfig { IssuerDays = 30, RenewBeforeDays = 30 },
            Path.Combine(rig.Temp.Path, "other-root"), null, null, rig.T0));
    }

    [Fact]
    public async Task ADryRunChangesNothing()
    {
        using var rig = new Rig();
        rig.SetupLocalRoot();
        var cert = File.ReadAllText(rig.L.IssuerCert);

        var result = await CaOps.MaintainAsync(rig.CaDir, rig.T0.AddDays(120), dryRun: true);
        Assert.Contains(result.Actions, a => a.Contains("dry run"));
        Assert.Equal(cert, File.ReadAllText(rig.L.IssuerCert));
        Assert.False(File.Exists(rig.L.PendingRequest));
        Assert.False(File.Exists(rig.L.Status));
    }

    [Fact]
    public async Task AMissingIssuingKeyIsRebuilt()
    {
        using var rig = new Rig();
        rig.SetupLocalRoot();
        Directory.Delete(rig.L.IssuerDir, true);

        Assert.Equal(HealthLevel.Bad, (await rig.Health(rig.T0)).Overall);
        var result = await CaOps.MaintainAsync(rig.CaDir, rig.T0);
        Assert.Equal(HealthLevel.Ok, result.After.Overall);
        Assert.Equal(2, rig.IssuerSerial);
    }
}
