using System.Security.Cryptography;
using System.Text;
using Kickl.Core.Ownership;
using Kickl.Core.Trust;
using Kickl.Core.Util;
using Xunit;

namespace Kickl.Core.Tests;

public class ShopSigningKeyTests
{
    [Fact]
    public void AGeneratedKeySignsReceiptsTheBrowserAndCaAccept()
    {
        using var temp = new TempDir();
        var spki = ShopSigningKey.Generate(temp.Path);
        Assert.Equal(spki, ShopSigningKey.PublicKey(temp.Path));

        var receipt = new OwnershipCert
        {
            Site = "shop.example.com", Order = 5, Seq = 1,
            PaidAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Expires = DateTimeOffset.UtcNow.AddDays(365).ToUnixTimeSeconds(),
            Token = new string('a', 64)
        };
        receipt.Sig = ShopSigningKey.Sign(temp.Path, Encoding.UTF8.GetBytes(receipt.Payload()));

        Assert.True(SiteClaims.Verify(receipt, spki));                       // what every browser runs
        receipt.Seq = 2;
        Assert.False(SiteClaims.Verify(receipt, spki));                      // and a changed receipt does not verify
    }

    [Fact]
    public void ImportingAnExistingPemKeepsThePublicKeyAndGivesIdenticalSignatures()
    {
        using var temp = new TempDir();
        using var original = RSA.Create(3072);                               // stands in for the shop's current PEM key
        var pem = original.ExportPkcs8PrivateKeyPem();
        var originalSpki = Convert.ToBase64String(original.ExportSubjectPublicKeyInfo());

        var importedSpki = ShopSigningKey.ImportPem(temp.Path, pem);
        Assert.Equal(originalSpki, importedSpki);                            // nothing already pinned breaks

        var payload = Encoding.UTF8.GetBytes("kickl/v1/site-claim|x.com|9|1|1700000000|1731536000|" + new string('b', 64));
        var viaTool = ShopSigningKey.Sign(temp.Path, payload);
        var viaOriginal = Convert.ToBase64String(original.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        Assert.Equal(viaOriginal, viaTool);                                  // PKCS#1 v1.5 is deterministic: same bytes as openssl_sign made
    }

    [Fact]
    public void TheKeyIsWrappedAtRestAndItsFolderLockedDown()
    {
        using var temp = new TempDir();
        using var original = RSA.Create(3072);
        ShopSigningKey.ImportPem(temp.Path, original.ExportPkcs8PrivateKeyPem());

        if (OperatingSystem.IsWindows())
        {
            Assert.True(File.Exists(Path.Combine(temp.Path, "shop-key.pkcs8.dpapi")));
            Assert.False(File.Exists(Path.Combine(temp.Path, "shop-key.pkcs8")));
            var allowed = FileProtection.AllowedPrincipals(temp.Path)!;
            Assert.DoesNotContain("S-1-5-32-545", allowed);                  // BUILTIN\Users
            Assert.DoesNotContain("S-1-1-0", allowed);                       // Everyone

            // and the wrapped file does not contain the private key in the clear
            var pkcs8 = original.ExportPkcs8PrivateKey();
            var wrapped = File.ReadAllBytes(Path.Combine(temp.Path, "shop-key.pkcs8.dpapi"));
            Assert.False(wrapped.AsSpan().IndexOf(pkcs8.AsSpan(0, 64)) >= 0);
        }
    }

    [Fact]
    public void ACorruptedWrappedKeyFailsClearly()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var temp = new TempDir();
        ShopSigningKey.Generate(temp.Path);
        var path = Path.Combine(temp.Path, "shop-key.pkcs8.dpapi");
        var bytes = File.ReadAllBytes(path);
        bytes[bytes.Length / 2] ^= 0xFF;
        File.WriteAllBytes(path, bytes);

        var ex = Assert.Throws<CryptographicException>(() => ShopSigningKey.Sign(temp.Path, [1, 2, 3]));
        Assert.Contains("different machine or has been altered", ex.Message);
    }

    [Fact]
    public void ATooShortKeyIsRefused()
    {
        using var temp = new TempDir();
        using var weak = RSA.Create(1024);
        Assert.Throws<CryptographicException>(() => ShopSigningKey.ImportPem(temp.Path, weak.ExportPkcs8PrivateKeyPem()));
    }

    [Fact]
    public void WithNoKeyThereIsAClearError()
    {
        using var temp = new TempDir();
        Assert.False(ShopSigningKey.Exists(temp.Path));
        Assert.Throws<FileNotFoundException>(() => ShopSigningKey.Sign(temp.Path, [1]));
    }
}
