using System.Security.Cryptography;
using System.Text;
using Kickl.Core.Util;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Kickl.Core.Registry;

/// <summary>
/// Hybrid Ed25519 + ML-DSA-65 verification for registry snapshots. Both signatures
/// must verify over the same domain-separated message; there is no downgraded mode.
/// Wire form: <c>b64url(Ed25519(64) || ML-DSA-65(3309))</c>.
/// </summary>
public sealed class HybridSnapshotVerifier : IRegistrySnapshotVerifier, IDisposable
{
    public const int Ed25519PublicKeySize = 32;
    public const int MlDsa65PublicKeySize = 1952;
    public const int Ed25519SignatureSize = 64;
    public const int MlDsa65SignatureSize = 3309;
    public const int HybridSignatureSize = Ed25519SignatureSize + MlDsa65SignatureSize;
    public const string Domain = "kickl/v1/registry-snapshot";

    private readonly Ed25519PublicKeyParameters _ed25519;
    private readonly byte[] _mlDsaPublicKey;
    private readonly MLDsa _mlDsa;

    public HybridSnapshotVerifier(ReadOnlySpan<byte> ed25519PublicKey,
        ReadOnlySpan<byte> mlDsa65PublicKey)
    {
        if (ed25519PublicKey.Length != Ed25519PublicKeySize)
            throw new ArgumentException($"Ed25519 public keys are {Ed25519PublicKeySize} bytes.",
                nameof(ed25519PublicKey));
        if (mlDsa65PublicKey.Length != MlDsa65PublicKeySize)
            throw new ArgumentException($"ML-DSA-65 public keys are {MlDsa65PublicKeySize} bytes.",
                nameof(mlDsa65PublicKey));
        if (!MLDsa.IsSupported)
            throw new PlatformNotSupportedException(
                "ML-DSA-65 is unavailable on this platform; refusing to verify a classical-only signature.");

        _ed25519 = new Ed25519PublicKeyParameters(ed25519PublicKey.ToArray(), 0);
        _mlDsaPublicKey = mlDsa65PublicKey.ToArray();
        _mlDsa = MLDsa.ImportMLDsaPublicKey(MLDsaAlgorithm.MLDsa65, _mlDsaPublicKey);
    }

    public bool Verify(ReadOnlySpan<byte> payload, string signature)
    {
        byte[] raw;
        try
        {
            raw = B64u.Decode(signature);
        }
        catch (FormatException)
        {
            return false;
        }

        if (raw.Length != HybridSignatureSize) return false;

        var message = SignedMessage(payload);
        try
        {
            var ed25519 = new Ed25519Signer();
            ed25519.Init(false, _ed25519);
            ed25519.BlockUpdate(message, 0, message.Length);
            var ed25519Ok = ed25519.VerifySignature(raw.AsSpan(0, Ed25519SignatureSize).ToArray());

            var mlDsaOk = _mlDsa.VerifyData(message,
                raw.AsSpan(Ed25519SignatureSize, MlDsa65SignatureSize));

            return ed25519Ok & mlDsaOk;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(message);
        }
    }

    /// <summary>The message both halves sign: a fixed domain label, a separator, then the snapshot bytes.</summary>
    public static byte[] SignedMessage(ReadOnlySpan<byte> payload)
    {
        var domain = Encoding.UTF8.GetBytes(Domain);
        var message = new byte[domain.Length + 1 + payload.Length];
        domain.CopyTo(message, 0);
        message[domain.Length] = 0x00;
        payload.CopyTo(message.AsSpan(domain.Length + 1));
        return message;
    }

    /// <summary>A stable public identifier for the pinned registry key (not a secret).</summary>
    public string Fingerprint => FingerprintOf(_ed25519.GetEncoded(), _mlDsaPublicKey);

    /// <summary>
    /// The fingerprint of a registry signing key. Signers compute it with this same method,
    /// so a published key file and a verifier agree without a second algorithm.
    /// </summary>
    public static string FingerprintOf(ReadOnlySpan<byte> ed25519PublicKey,
        ReadOnlySpan<byte> mlDsa65PublicKey)
    {
        if (ed25519PublicKey.Length != Ed25519PublicKeySize)
            throw new ArgumentException($"Ed25519 public keys are {Ed25519PublicKeySize} bytes.",
                nameof(ed25519PublicKey));
        if (mlDsa65PublicKey.Length != MlDsa65PublicKeySize)
            throw new ArgumentException($"ML-DSA-65 public keys are {MlDsa65PublicKeySize} bytes.",
                nameof(mlDsa65PublicKey));

        var material = new byte[4 + Ed25519PublicKeySize + MlDsa65PublicKeySize];
        Encoding.UTF8.GetBytes("key:").CopyTo(material, 0);
        ed25519PublicKey.CopyTo(material.AsSpan(4));
        mlDsa65PublicKey.CopyTo(material.AsSpan(4 + Ed25519PublicKeySize));
        try
        {
            return Hashing.Hex(SHA256.HashData(material));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(material);
        }
    }

    public void Dispose() => _mlDsa.Dispose();
}
