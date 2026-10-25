using System.Security.Cryptography;
using System.Text;
using Kickl.Core.Registry;
using Kickl.Core.Util;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace Kickl.Core.Tests;

public class HybridSignatureTests
{
    [Fact]
    public void HybridSignature_VerifiesOnlyWhenBothHalvesAreValid()
    {
        using var signer = new TestSigner();
        var payload = Encoding.UTF8.GetBytes("{\"version\":1}");

        Assert.True(signer.Verifier.Verify(payload, signer.Sign(payload)));

        Assert.False(signer.Verifier.Verify(Encoding.UTF8.GetBytes("{\"version\":2}"), signer.Sign(payload)));

        var edOnly = signer.Sign(payload, mlDsa: false);
        Assert.False(signer.Verifier.Verify(payload, edOnly));

        var mlOnly = signer.Sign(payload, ed25519: false);
        Assert.False(signer.Verifier.Verify(payload, mlOnly));
    }

    [Fact]
    public void HybridSignature_RejectsEitherHalfBeingTampered()
    {
        using var signer = new TestSigner();
        var payload = Encoding.UTF8.GetBytes("snapshot");

        Assert.False(signer.Verifier.Verify(payload, signer.Sign(payload, tamperEd25519: true)));
        Assert.False(signer.Verifier.Verify(payload, signer.Sign(payload, tamperMlDsa: true)));
    }

    [Fact]
    public void HybridSignature_RequiresTheDomainSeparatedMessage()
    {
        using var signer = new TestSigner();
        var payload = Encoding.UTF8.GetBytes("snapshot");

        Assert.False(signer.Verifier.Verify(payload, signer.SignRaw(payload)));
    }

    [Fact]
    public void HybridSignature_RejectsMalformedEncodings()
    {
        using var signer = new TestSigner();
        var payload = Encoding.UTF8.GetBytes("snapshot");
        var valid = B64u.Decode(signer.Sign(payload));

        Assert.False(signer.Verifier.Verify(payload, B64u.Encode(valid[..^1])));
        Assert.False(signer.Verifier.Verify(payload, "not base64url %%%"));
        Assert.False(signer.Verifier.Verify(payload, ""));
    }

    [Fact]
    public void HybridSignature_RejectsTheWrongKey()
    {
        using var signer = new TestSigner();
        using var other = new TestSigner();
        var payload = Encoding.UTF8.GetBytes("snapshot");

        Assert.False(other.Verifier.Verify(payload, signer.Sign(payload)));
    }

    [Fact]
    public void HybridSignature_KeyFingerprintIsStableAndDistinct()
    {
        using var signer = new TestSigner();
        using var other = new TestSigner();

        Assert.Equal(signer.Verifier.Fingerprint, signer.Verifier.Fingerprint);
        Assert.NotEqual(signer.Verifier.Fingerprint, other.Verifier.Fingerprint);
        Assert.Equal(64, signer.Verifier.Fingerprint.Length);
    }

    [Fact]
    public void HybridSignature_RejectsWrongPublicKeySizes()
    {
        Assert.Throws<ArgumentException>(() => new HybridSnapshotVerifier(new byte[31], new byte[1952]));
        Assert.Throws<ArgumentException>(() => new HybridSnapshotVerifier(new byte[32], new byte[1951]));
    }

    [Fact]
    public void HybridSignature_SignedMessageBindsTheDomain()
    {
        var payload = Encoding.UTF8.GetBytes("x");
        var message = HybridSnapshotVerifier.SignedMessage(payload);
        var domain = Encoding.UTF8.GetBytes(HybridSnapshotVerifier.Domain);

        Assert.Equal(domain.Length + 1 + payload.Length, message.Length);
        Assert.Equal(domain, message[..domain.Length]);
        Assert.Equal((byte)0, message[domain.Length]);
    }

    private sealed class TestSigner : IDisposable
    {
        private readonly Ed25519PrivateKeyParameters _ed25519;
        private readonly MLDsa _mlDsa;

        public TestSigner()
        {
            var generator = new Ed25519KeyPairGenerator();
            generator.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
            var pair = generator.GenerateKeyPair();
            _ed25519 = (Ed25519PrivateKeyParameters)pair.Private;
            var edPublic = ((Ed25519PublicKeyParameters)pair.Public).GetEncoded();

            _mlDsa = MLDsa.GenerateKey(MLDsaAlgorithm.MLDsa65);
            Verifier = new HybridSnapshotVerifier(edPublic, _mlDsa.ExportMLDsaPublicKey());
        }

        public HybridSnapshotVerifier Verifier { get; }

        public string Sign(byte[] payload, bool ed25519 = true, bool mlDsa = true,
            bool tamperEd25519 = false, bool tamperMlDsa = false) =>
            SignMessage(HybridSnapshotVerifier.SignedMessage(payload), ed25519, mlDsa,
                tamperEd25519, tamperMlDsa);

        public string SignRaw(byte[] payload) => SignMessage(payload, true, true, false, false);

        private string SignMessage(byte[] message, bool ed25519, bool mlDsa,
            bool tamperEd25519, bool tamperMlDsa)
        {
            var edSignature = new byte[HybridSnapshotVerifier.Ed25519SignatureSize];
            if (ed25519)
            {
                var signer = new Ed25519Signer();
                signer.Init(true, _ed25519);
                signer.BlockUpdate(message, 0, message.Length);
                edSignature = signer.GenerateSignature();
            }
            if (tamperEd25519) edSignature[0] ^= 0xff;

            var mlSignature = new byte[HybridSnapshotVerifier.MlDsa65SignatureSize];
            if (mlDsa) mlSignature = _mlDsa.SignData(message);
            if (tamperMlDsa) mlSignature[0] ^= 0xff;

            var hybrid = new byte[HybridSnapshotVerifier.HybridSignatureSize];
            edSignature.CopyTo(hybrid, 0);
            mlSignature.CopyTo(hybrid, HybridSnapshotVerifier.Ed25519SignatureSize);
            try
            {
                return B64u.Encode(hybrid);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(hybrid);
            }
        }

        public void Dispose() => _mlDsa.Dispose();
    }
}
