using System.Security.Cryptography;
using System.Text;
using Kickl.Core.Registry;
using Kickl.Core.Util;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Kickl.Core.Trust;

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

    /// <summary>Private key files for an offline signer: <c>ed25519.seed</c> (hex) and <c>mldsa65.pkcs8</c> (b64url).</summary>
    public void SaveTo(string dir)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "ed25519.seed"), Hashing.Hex(Seed));
        File.WriteAllText(Path.Combine(dir, "mldsa65.pkcs8"), B64u.Encode(MlDsaPkcs8));
    }

    public static HybridSigner LoadFrom(string dir) => Load(
        Convert.FromHexString(File.ReadAllText(Path.Combine(dir, "ed25519.seed")).Trim()),
        B64u.Decode(File.ReadAllText(Path.Combine(dir, "mldsa65.pkcs8")).Trim()));

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
