using System.Security.Cryptography;
using System.Text;
using Kickl.Core.Registry;
using Kickl.Core.Util;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Kickl.Core.Trust;

public enum KeyProtection { None, Machine }

/// <summary>Domain labels. A signature made for one purpose can never be replayed as another.</summary>
public static class TrustDomains
{
    public const string IssuerCert = "kickl/v1/ca-issuer-cert";
    public const string Seal = "kickl/v1/ca-seal";
    public const string LogHead = "kickl/v1/ca-log-head";
    public const string Revocations = "kickl/v1/ca-revocations";
}

/// <summary>
/// Hybrid Ed25519 + ML-DSA-65 signing for the CA hierarchy. Both halves sign the same domain-separated message
/// and both must verify; there is no classical-only mode. Wire form: <c>b64url(Ed25519(64) || ML-DSA-65(3309))</c>,
/// the same as registry snapshots, but with a different domain label per purpose.
/// </summary>
public sealed class HybridSigner : IDisposable
{
    private readonly Ed25519PrivateKeyParameters _ed25519;
    private readonly MLDsa _mlDsa;

    private HybridSigner(byte[] seed, byte[] mlDsaPkcs8)
    {
        if (!MLDsa.IsSupported)
            throw new PlatformNotSupportedException("ML-DSA-65 is unavailable here; refusing to create a classical-only CA key.");
        Seed = seed;
        MlDsaPkcs8 = mlDsaPkcs8;
        _ed25519 = new Ed25519PrivateKeyParameters(seed, 0);
        _mlDsa = MLDsa.ImportPkcs8PrivateKey(mlDsaPkcs8);
    }

    public byte[] Seed { get; }
    public byte[] MlDsaPkcs8 { get; }

    public static HybridSigner Generate()
    {
        using var ml = MLDsa.GenerateKey(MLDsaAlgorithm.MLDsa65);
        return new HybridSigner(RandomNumberGenerator.GetBytes(32), ml.ExportPkcs8PrivateKey());
    }

    public static HybridSigner Load(ReadOnlySpan<byte> seed, ReadOnlySpan<byte> mlDsaPkcs8) =>
        new(seed.ToArray(), mlDsaPkcs8.ToArray());

    public byte[] Ed25519PublicKey => _ed25519.GeneratePublicKey().GetEncoded();
    public byte[] MlDsa65PublicKey => _mlDsa.ExportMLDsaPublicKey();
    public RegistrySigningKey Public => RegistrySigningKey.FromPublic(Ed25519PublicKey, MlDsa65PublicKey);

    public string Sign(string domain, ReadOnlySpan<byte> payload)
    {
        var message = HybridMessage.Build(domain, payload);
        var hybrid = new byte[HybridSnapshotVerifier.HybridSignatureSize];
        try
        {
            var ed = new Ed25519Signer();
            ed.Init(true, _ed25519);
            ed.BlockUpdate(message, 0, message.Length);
            ed.GenerateSignature().CopyTo(hybrid, 0);
            _mlDsa.SignData(message).CopyTo(hybrid, HybridSnapshotVerifier.Ed25519SignatureSize);
            return B64u.Encode(hybrid);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(message);
            CryptographicOperations.ZeroMemory(hybrid);
        }
    }

    public string Sign(string domain, string text) => Sign(domain, Encoding.UTF8.GetBytes(text));

    private const string ProtectedSuffix = ".dpapi";
    private static readonly byte[] Entropy = Utf8.B("kickl/v1/ca-key-file");

    /// <summary>Where a signer's key files stand: <c>Machine</c> (wrapped by Windows DPAPI), <c>None</c> (plain), or null if absent.</summary>
    public static KeyProtection? ProtectionOf(string dir)
    {
        if (File.Exists(Path.Combine(dir, "ed25519.seed" + ProtectedSuffix))) return KeyProtection.Machine;
        return File.Exists(Path.Combine(dir, "ed25519.seed")) ? KeyProtection.None : null;
    }

    /// <summary>
    /// Save the private key files and lock the folder to the current account.
    /// <see cref="KeyProtection.Machine"/> additionally wraps them with Windows DPAPI (machine scope) so a copied or
    /// backed-up file cannot be read anywhere else. An OFFLINE root must stay <see cref="KeyProtection.None"/>: it has to
    /// be able to move to another machine. On other platforms Machine falls back to a 0600 file. Returns what was applied.
    /// </summary>
    public KeyProtection SaveTo(string dir, KeyProtection protection = KeyProtection.None)
    {
        Directory.CreateDirectory(dir);
        var seedPath = Path.Combine(dir, "ed25519.seed");
        var mlPath = Path.Combine(dir, "mldsa65.pkcs8");
        var seed = Utf8.B(Hashing.Hex(Seed));
        var ml = Utf8.B(B64u.Encode(MlDsaPkcs8));
        var wrap = false;
        try
        {
            if (OperatingSystem.IsWindows() && protection == KeyProtection.Machine)
            {
                wrap = true;
                File.WriteAllBytes(seedPath + ProtectedSuffix, ProtectBytes(seed));
                File.WriteAllBytes(mlPath + ProtectedSuffix, ProtectBytes(ml));
                FileProtection.Shred(seedPath);      // a plaintext copy must not linger beside the wrapped one
                FileProtection.Shred(mlPath);
            }
            else
            {
                File.WriteAllBytes(seedPath, seed);
                File.WriteAllBytes(mlPath, ml);
                FileProtection.Shred(seedPath + ProtectedSuffix);
                FileProtection.Shred(mlPath + ProtectedSuffix);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
            CryptographicOperations.ZeroMemory(ml);
        }
        FileProtection.RestrictTree(dir);
        return wrap ? KeyProtection.Machine : KeyProtection.None;
    }

    /// <summary>
    /// Load a signer from its key files, wrapped or plain. With <paramref name="upgrade"/> a plain key is rewritten as a
    /// wrapped one on Windows, so existing installations pick up the protection the next time they run.
    /// </summary>
    public static HybridSigner LoadFrom(string dir, bool upgrade = false)
    {
        var wasPlain = ProtectionOf(dir) == KeyProtection.None;
        var seedText = ReadSecret(Path.Combine(dir, "ed25519.seed"));
        var mlText = ReadSecret(Path.Combine(dir, "mldsa65.pkcs8"));
        var signer = Load(Convert.FromHexString(seedText), B64u.Decode(mlText));
        if (upgrade && wasPlain && OperatingSystem.IsWindows()) signer.SaveTo(dir, KeyProtection.Machine);
        return signer;
    }

    private static string ReadSecret(string path)
    {
        byte[] bytes;
        if (File.Exists(path + ProtectedSuffix))
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("This key is wrapped with Windows DPAPI and can only be opened on the machine that wrapped it.");
            bytes = UnprotectBytes(File.ReadAllBytes(path + ProtectedSuffix));
        }
        else
        {
            bytes = File.ReadAllBytes(path);
        }

        try { return Encoding.UTF8.GetString(bytes).Trim(); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static byte[] ProtectBytes(byte[] plain) =>
        System.Security.Cryptography.ProtectedData.Protect(plain, Entropy, System.Security.Cryptography.DataProtectionScope.LocalMachine);

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static byte[] UnprotectBytes(byte[] wrapped)
    {
        try { return System.Security.Cryptography.ProtectedData.Unprotect(wrapped, Entropy, System.Security.Cryptography.DataProtectionScope.LocalMachine); }
        catch (CryptographicException ex) { throw new CryptographicException("This key file was wrapped on a different machine or has been altered.", ex); }
    }

    public void Dispose()
    {
        _mlDsa.Dispose();
        CryptographicOperations.ZeroMemory(Seed);
    }
}

/// <summary>Checks hybrid signatures from one public key under a named domain.</summary>
public sealed class HybridVerifier : IDisposable
{
    private readonly Ed25519PublicKeyParameters _ed25519;
    private readonly MLDsa _mlDsa;

    public HybridVerifier(RegistrySigningKey key)
    {
        key.Validate();
        if (!MLDsa.IsSupported)
            throw new PlatformNotSupportedException("ML-DSA-65 is unavailable here; refusing to verify a classical-only signature.");
        _ed25519 = new Ed25519PublicKeyParameters(B64u.Decode(key.Ed25519PublicKey), 0);
        _mlDsa = MLDsa.ImportMLDsaPublicKey(MLDsaAlgorithm.MLDsa65, B64u.Decode(key.MlDsa65PublicKey));
        Fingerprint = key.Fingerprint;
    }

    public string Fingerprint { get; }

    public bool Verify(string domain, ReadOnlySpan<byte> payload, string signature)
    {
        byte[] raw;
        try { raw = B64u.Decode(signature); }
        catch (FormatException) { return false; }
        if (raw.Length != HybridSnapshotVerifier.HybridSignatureSize) return false;

        var message = HybridMessage.Build(domain, payload);
        try
        {
            var ed = new Ed25519Signer();
            ed.Init(false, _ed25519);
            ed.BlockUpdate(message, 0, message.Length);
            var edOk = ed.VerifySignature(raw.AsSpan(0, HybridSnapshotVerifier.Ed25519SignatureSize).ToArray());
            var mlOk = _mlDsa.VerifyData(message, raw.AsSpan(HybridSnapshotVerifier.Ed25519SignatureSize));
            return edOk & mlOk;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(message);
        }
    }

    public bool Verify(string domain, string text, string signature) =>
        Verify(domain, Encoding.UTF8.GetBytes(text), signature);

    public void Dispose() => _mlDsa.Dispose();
}

internal static class HybridMessage
{
    /// <summary>domain, a zero byte, then the payload.</summary>
    public static byte[] Build(string domain, ReadOnlySpan<byte> payload)
    {
        var d = Encoding.UTF8.GetBytes(domain);
        var message = new byte[d.Length + 1 + payload.Length];
        d.CopyTo(message, 0);
        message[d.Length] = 0;
        payload.CopyTo(message.AsSpan(d.Length + 1));
        return message;
    }
}
