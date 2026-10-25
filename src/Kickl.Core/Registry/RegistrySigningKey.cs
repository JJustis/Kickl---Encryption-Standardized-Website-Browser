using System.Security.Cryptography;
using System.Text.Json;
using Kickl.Core.Util;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Kickl.Core.Registry;

/// <summary>
/// The public half of a registry signing key, as published for clients to pin.
/// Nothing here is secret.
/// </summary>
public sealed class RegistrySigningKey
{
    public string Algorithm { get; set; } = "Ed25519+ML-DSA-65";
    public string Ed25519PublicKey { get; set; } = "";
    public string MlDsa65PublicKey { get; set; } = "";
    public string Fingerprint { get; set; } = "";

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static RegistrySigningKey FromPublic(ReadOnlySpan<byte> ed25519PublicKey,
        ReadOnlySpan<byte> mlDsa65PublicKey) => new()
    {
        Ed25519PublicKey = B64u.Encode(ed25519PublicKey),
        MlDsa65PublicKey = B64u.Encode(mlDsa65PublicKey),
        Fingerprint = HybridSnapshotVerifier.FingerprintOf(ed25519PublicKey, mlDsa65PublicKey)
    };

    public void Validate()
    {
        var ed = B64u.Decode(Ed25519PublicKey);
        var ml = B64u.Decode(MlDsa65PublicKey);
        if (ed.Length != HybridSnapshotVerifier.Ed25519PublicKeySize ||
            ml.Length != HybridSnapshotVerifier.MlDsa65PublicKeySize)
            throw new FormatException("The registry signing key file has the wrong key sizes.");
        var fingerprint = HybridSnapshotVerifier.FingerprintOf(ed, ml);
        if (!string.Equals(fingerprint, Fingerprint, StringComparison.Ordinal))
            throw new FormatException("The registry signing key fingerprint does not match its keys.");
    }

    public HybridSnapshotVerifier CreateVerifier() =>
        new(B64u.Decode(Ed25519PublicKey), B64u.Decode(MlDsa65PublicKey));
}

/// <summary>
/// The private half of a registry signing key: an Ed25519 seed and an ML-DSA-65 PKCS#8 blob.
/// This type is for the offline signer and tests only; the registry API never sees it.
/// Both signatures are always produced over the domain-separated snapshot message.
/// </summary>
public sealed class RegistrySigningKeyPair : IDisposable
{
    private readonly Ed25519PrivateKeyParameters _ed25519;
    private readonly MLDsa _mlDsa;

    private RegistrySigningKeyPair(byte[] seed, byte[] mlDsaPkcs8)
    {
        if (seed.Length != 32) throw new ArgumentException("Ed25519 seeds are 32 bytes.", nameof(seed));
        if (!MLDsa.IsSupported)
            throw new PlatformNotSupportedException(
                "ML-DSA-65 is unavailable on this platform; refusing to create a classical-only registry key.");

        Seed = seed;
        MlDsaPkcs8 = mlDsaPkcs8;
        _ed25519 = new Ed25519PrivateKeyParameters(seed, 0);
        _mlDsa = MLDsa.ImportPkcs8PrivateKey(mlDsaPkcs8);
    }

    public byte[] Seed { get; }
    public byte[] MlDsaPkcs8 { get; }

    public static RegistrySigningKeyPair Generate()
    {
        if (!MLDsa.IsSupported)
            throw new PlatformNotSupportedException("ML-DSA-65 is unavailable on this platform.");

        var seed = RandomNumberGenerator.GetBytes(32);
        using var mlDsa = MLDsa.GenerateKey(MLDsaAlgorithm.MLDsa65);
        return new RegistrySigningKeyPair(seed, mlDsa.ExportPkcs8PrivateKey());
    }

    public static RegistrySigningKeyPair Load(ReadOnlySpan<byte> seed, ReadOnlySpan<byte> mlDsaPkcs8) =>
        new(seed.ToArray(), mlDsaPkcs8.ToArray());

    public byte[] Ed25519PublicKey => _ed25519.GeneratePublicKey().GetEncoded();

    public byte[] MlDsa65PublicKey => _mlDsa.ExportMLDsaPublicKey();

    public RegistrySigningKey Public => RegistrySigningKey.FromPublic(Ed25519PublicKey, MlDsa65PublicKey);

    /// <summary>Hybrid signature in wire form: b64url(Ed25519(64) || ML-DSA-65(3309)).</summary>
    public string Sign(ReadOnlySpan<byte> payload)
    {
        var message = HybridSnapshotVerifier.SignedMessage(payload);
        var hybrid = new byte[HybridSnapshotVerifier.HybridSignatureSize];
        try
        {
            var ed25519 = new Ed25519Signer();
            ed25519.Init(true, _ed25519);
            ed25519.BlockUpdate(message, 0, message.Length);
            ed25519.GenerateSignature().CopyTo(hybrid, 0);
            _mlDsa.SignData(message).CopyTo(hybrid, HybridSnapshotVerifier.Ed25519SignatureSize);
            return B64u.Encode(hybrid);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(message);
            CryptographicOperations.ZeroMemory(hybrid);
        }
    }

    public void Dispose() => _mlDsa.Dispose();
}
