using System.Security.Cryptography;
using System.Text.Json;
using Kickl.Core.Crypto;
using Kickl.Core.Registry;
using Kickl.Core.Trust;
using Kickl.Core.Util;
using Xunit;

namespace Kickl.Core.Tests;

public class KeyProtectionTests
{
    private static readonly JsonSerializerOptions Camel = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    // ------------------------------------------------------------------ file permissions

    [Fact]
    public void RestrictLeavesOnlyTheOwnerSystemAndAdministrators()
    {
        if (!OperatingSystem.IsWindows()) return;   // Unix modes are covered below

        using var temp = new TempDir();
        var file = Path.Combine(temp.Path, "secret.key");
        File.WriteAllText(file, "x");
        FileProtection.Restrict(file);
        var after = FileProtection.AllowedPrincipals(file)!;

        Assert.Equal(3, after.Count);
        Assert.DoesNotContain("S-1-1-0", after);                // Everyone
        Assert.DoesNotContain("S-1-5-32-545", after);           // BUILTIN\\Users
        Assert.DoesNotContain("S-1-5-11", after);               // Authenticated Users
        Assert.Contains("S-1-5-18", after);                     // SYSTEM
        Assert.Contains("S-1-5-32-544", after);                 // Administrators

        // the owner can still use the file
        Assert.Equal("x", File.ReadAllText(file));
        File.WriteAllText(file, "y");
    }

    [Fact]
    public void RestrictOnAFolderCoversFilesCreatedLater()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var temp = new TempDir();
        var dir = Path.Combine(temp.Path, "keys");
        Directory.CreateDirectory(dir);
        FileProtection.Restrict(dir);
        var later = Path.Combine(dir, "later.key");
        File.WriteAllText(later, "z");

        var allowed = FileProtection.AllowedPrincipals(later)!;
        Assert.DoesNotContain("S-1-1-0", allowed);
        Assert.DoesNotContain("S-1-5-32-545", allowed);
    }

    [Fact]
    public void RestrictIgnoresAPathThatDoesNotExist()
    {
        FileProtection.Restrict(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public void TheKeystoreAndItsFolderAreLockedDownWhenAProfileIsCreated()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var temp = new TempDir();
        var paths = new DataPaths(temp.Path, "locked");
        Keystore.Create(paths, "pin-pin");

        Assert.DoesNotContain("S-1-5-32-545", FileProtection.AllowedPrincipals(paths.Keystore)!);
        Assert.DoesNotContain("S-1-5-32-545", FileProtection.AllowedPrincipals(Path.GetDirectoryName(paths.Keystore)!)!);
        Assert.NotNull(Keystore.Unlock(paths, "pin-pin"));                                   // and it still opens
    }

    [Fact]
    public void ShredRemovesTheFile()
    {
        using var temp = new TempDir();
        var file = Path.Combine(temp.Path, "gone");
        File.WriteAllBytes(file, new byte[4096]);
        FileProtection.Shred(file);
        Assert.False(File.Exists(file));
        FileProtection.Shred(file);                                                           // already gone: fine
    }

    // ------------------------------------------------------------------ wrapped signing keys

    [Fact]
    public void AWrappedKeyRoundTripsAndNeverLeavesPlaintextBeside()
    {
        using var temp = new TempDir();
        using var key = HybridSigner.Generate();
        var applied = key.SaveTo(temp.Path, KeyProtection.Machine);

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(KeyProtection.Machine, applied);
            Assert.True(File.Exists(Path.Combine(temp.Path, "ed25519.seed.dpapi")));
            Assert.False(File.Exists(Path.Combine(temp.Path, "ed25519.seed")));
            Assert.False(File.Exists(Path.Combine(temp.Path, "mldsa65.pkcs8")));
            // the wrapped file does not contain the seed in any readable form
            Assert.DoesNotContain(Hashing.Hex(key.Seed), System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(temp.Path, "ed25519.seed.dpapi"))));
            Assert.Equal(KeyProtection.Machine, HybridSigner.ProtectionOf(temp.Path));
        }

        using var loaded = HybridSigner.LoadFrom(temp.Path);
        Assert.Equal(key.Public.Fingerprint, loaded.Public.Fingerprint);

        // it signs, and the signature verifies against the original public key
        var sig = loaded.Sign("test/domain", "hello");
        using var verifier = new HybridVerifier(key.Public);
        Assert.True(verifier.Verify("test/domain", "hello", sig));
    }

    [Fact]
    public void ARootKeyStaysPortable()
    {
        using var temp = new TempDir();
        using var root = HybridSigner.Generate();
        root.SaveTo(temp.Path);                                                               // None: it has to travel on a USB stick
        Assert.Equal(KeyProtection.None, HybridSigner.ProtectionOf(temp.Path));
        Assert.True(File.Exists(Path.Combine(temp.Path, "ed25519.seed")));
        using var back = HybridSigner.LoadFrom(temp.Path);
        Assert.Equal(root.Public.Fingerprint, back.Public.Fingerprint);
    }

    [Fact]
    public void LoadingAPlainKeyWithUpgradeWrapsItOnWindows()
    {
        using var temp = new TempDir();
        using var key = HybridSigner.Generate();
        key.SaveTo(temp.Path);                                                                // an existing, unwrapped key
        Assert.Equal(KeyProtection.None, HybridSigner.ProtectionOf(temp.Path));

        using var loaded = HybridSigner.LoadFrom(temp.Path, upgrade: true);
        Assert.Equal(key.Public.Fingerprint, loaded.Public.Fingerprint);

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(KeyProtection.Machine, HybridSigner.ProtectionOf(temp.Path));
            Assert.False(File.Exists(Path.Combine(temp.Path, "ed25519.seed")));
        }
        using var again = HybridSigner.LoadFrom(temp.Path);
        Assert.Equal(key.Public.Fingerprint, again.Public.Fingerprint);
    }

    [Fact]
    public void ACorruptedWrappedKeyFailsWithAClearError()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var temp = new TempDir();
        using var key = HybridSigner.Generate();
        key.SaveTo(temp.Path, KeyProtection.Machine);
        var path = Path.Combine(temp.Path, "ed25519.seed.dpapi");
        var bytes = File.ReadAllBytes(path);
        bytes[bytes.Length / 2] ^= 0xFF;
        File.WriteAllBytes(path, bytes);

        var ex = Assert.Throws<CryptographicException>(() => HybridSigner.LoadFrom(temp.Path));
        Assert.Contains("different machine or has been altered", ex.Message);
    }

    // ------------------------------------------------------------------ the CA wraps its own key

    [Fact]
    public async Task AFreshlyBuiltCaHasItsIssuingKeyWrappedAndHealthSaysSo()
    {
        using var temp = new TempDir();
        using var shop = new FakeShop();
        var ca = Path.Combine(temp.Path, "ca");
        var result = CaOps.Setup(ca, new CaConfig(), Path.Combine(temp.Path, "root"), null, shop.Spki, DateTimeOffset.UtcNow);
        Assert.True(result.Complete);

        var l = new CaLayout(ca);
        Assert.Equal(OperatingSystem.IsWindows() ? KeyProtection.Machine : KeyProtection.None, HybridSigner.ProtectionOf(l.IssuerDir));
        var health = await CaOps.HealthAsync(ca, DateTimeOffset.UtcNow);
        Assert.Equal(HealthLevel.Ok, health.Items.Single(i => i.Name == "issuer-key").Level);

        // and the CA still signs with it
        using var key = HybridSigner.LoadFrom(l.IssuerDir);
        var root = JsonSerializer.Deserialize<RegistrySigningKey>(File.ReadAllText(l.RootPublic), RegistrySigningKey.Json)!;
        var cert = JsonSerializer.Deserialize<IssuerCertificate>(File.ReadAllText(l.IssuerCert), Camel)!;
        using var store = new FileCaStore(l.Data);
        var seal = new CaService(key, cert, root, [shop.Spki], store).Countersign(shop.Sign("wrapped.com"), DateTimeOffset.UtcNow);
        Assert.True(CaTrust.VerifySeal(root, seal, "wrapped.com", DateTimeOffset.UtcNow).Verified);
    }

    [Fact]
    public async Task MaintenanceWrapsAnExistingUnwrappedIssuingKey_WithoutChangingTheKey()
    {
        using var temp = new TempDir();
        using var shop = new FakeShop();
        var ca = Path.Combine(temp.Path, "ca");
        CaOps.Setup(ca, new CaConfig(), Path.Combine(temp.Path, "root"), null, shop.Spki, DateTimeOffset.UtcNow);
        var l = new CaLayout(ca);

        // simulate an installation made before this protection existed
        using (var key = HybridSigner.LoadFrom(l.IssuerDir)) key.SaveTo(l.IssuerDir, KeyProtection.None);
        var fingerprintBefore = JsonSerializer.Deserialize<IssuerCertificate>(File.ReadAllText(l.IssuerCert), Camel)!.Key.Fingerprint;
        Assert.Equal(KeyProtection.None, HybridSigner.ProtectionOf(l.IssuerDir));

        var result = await CaOps.MaintainAsync(ca, DateTimeOffset.UtcNow);

        if (OperatingSystem.IsWindows())
        {
            Assert.Contains(result.Actions, a => a.Contains("DPAPI"));
            Assert.Equal(KeyProtection.Machine, HybridSigner.ProtectionOf(l.IssuerDir));
        }
        Assert.Equal(HealthLevel.Ok, result.After.Overall);
        using var after = HybridSigner.LoadFrom(l.IssuerDir);
        Assert.Equal(fingerprintBefore, after.Public.Fingerprint);                            // the same key, now wrapped
        Assert.False(result.NeedsHuman);
    }
}
