using System.Security.Cryptography;
using System.Text;
using Kickl.Core.Util;

namespace Kickl.Core.Trust;

/// <summary>
/// The BTC shop's receipt-signing key, held by the CA tool instead of by the shop's PHP.
/// A PEM readable by the web server is exposed to every bug in that web app; here the key is an RSA-3072 key wrapped with
/// Windows DPAPI in a folder only its owner can open, and the shop asks for signatures through the tool
/// (<c>shop-sign</c>). The signature format is exactly what the shop produced before (RSA PKCS#1 v1.5 over SHA-256), so
/// existing receipts, the pinned shop key and the CA's trusted-shop list are unaffected.
/// </summary>
public static class ShopSigningKey
{
    private const string KeyFile = "shop-key.pkcs8";
    private const string Suffix = ".dpapi";
    private const string SpkiFile = "shop-key.spki";
    private static readonly byte[] Entropy = Utf8.B("kickl/v1/shop-signing-key");

    public static bool Exists(string dir) =>
        File.Exists(Path.Combine(dir, KeyFile + Suffix)) || File.Exists(Path.Combine(dir, KeyFile));

    /// <summary>Create a new key. Returns its public key (base64 SPKI), which the CA must be told to trust.</summary>
    public static string Generate(string dir)
    {
        using var rsa = RSA.Create(3072);
        return Save(dir, rsa);
    }

    /// <summary>
    /// Take over an existing key from a PEM file, keeping the same public key so nothing already pinned breaks.
    /// The caller destroys the PEM afterwards (<see cref="FileProtection.Shred"/>).
    /// </summary>
    public static string ImportPem(string dir, string pemText)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(pemText);
        if (rsa.KeySize < 2048) throw new CryptographicException("Refusing a shop key shorter than 2048 bits.");
        return Save(dir, rsa);
    }

    private static string Save(string dir, RSA rsa)
    {
        Directory.CreateDirectory(dir);
        var pkcs8 = rsa.ExportPkcs8PrivateKey();
        var spki = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
        try
        {
            var path = Path.Combine(dir, KeyFile);
            if (OperatingSystem.IsWindows())
            {
                File.WriteAllBytes(path + Suffix, Protect(pkcs8));
                FileProtection.Shred(path);
            }
            else
            {
                File.WriteAllBytes(path, pkcs8);
                FileProtection.Shred(path + Suffix);
            }
            File.WriteAllText(Path.Combine(dir, SpkiFile), spki);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }
        FileProtection.RestrictTree(dir);
        return spki;
    }

    public static string PublicKey(string dir)
    {
        var path = Path.Combine(dir, SpkiFile);
        if (File.Exists(path)) return File.ReadAllText(path).Trim();
        using var rsa = Load(dir);
        return Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
    }

    /// <summary>RSA PKCS#1 v1.5 / SHA-256, base64, as the shop's PHP produced with openssl_sign.</summary>
    public static string Sign(string dir, byte[] payload)
    {
        using var rsa = Load(dir);
        return Convert.ToBase64String(rsa.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    private static RSA Load(string dir)
    {
        var path = Path.Combine(dir, KeyFile);
        byte[] pkcs8;
        if (File.Exists(path + Suffix))
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("This shop key is wrapped with Windows DPAPI and only opens on the machine that wrapped it.");
            pkcs8 = Unprotect(File.ReadAllBytes(path + Suffix));
        }
        else if (File.Exists(path))
        {
            pkcs8 = File.ReadAllBytes(path);
        }
        else
        {
            throw new FileNotFoundException("There is no shop signing key in " + dir);
        }

        try
        {
            var rsa = RSA.Create();
            rsa.ImportPkcs8PrivateKey(pkcs8, out _);
            return rsa;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static byte[] Protect(byte[] plain) => ProtectedData.Protect(plain, Entropy, DataProtectionScope.LocalMachine);

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static byte[] Unprotect(byte[] wrapped)
    {
        try { return ProtectedData.Unprotect(wrapped, Entropy, DataProtectionScope.LocalMachine); }
        catch (CryptographicException ex) { throw new CryptographicException("This shop key file was wrapped on a different machine or has been altered.", ex); }
    }
}
