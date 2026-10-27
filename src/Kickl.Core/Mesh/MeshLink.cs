using System.Security.Cryptography;
using Kickl.Core.Crypto;
using Kickl.Core.Util;

namespace Kickl.Core.Mesh;

public sealed class MeshException(string message) : Exception(message);

public sealed class MeshLinkOffer
{
    public string FromDeviceId { get; set; } = "";
    public string ToDeviceId { get; set; } = "";
    public byte[] FromRsaSpki { get; set; } = [];
    /// <summary>Fresh P-256 key for this link only. Discarded after the handshake.</summary>
    public byte[] EphemeralPublicKey { get; set; } = [];
    public byte[] HandshakeNonce { get; set; } = [];
    public byte[] Signature { get; set; } = [];
}

public sealed class MeshLinkAccept
{
    public string DeviceId { get; set; } = "";
    public byte[] EphemeralPublicKey { get; set; } = [];
    public byte[] Signature { get; set; } = [];
}

public sealed class MeshFrame
{
    public uint Counter { get; set; }
    public byte[] Nonce { get; set; } = [];
    public byte[] Ciphertext { get; set; } = [];
}

/// <summary>
/// A long-term device identity for the mesh. RSA-3072 authenticates the device; it never
/// transports the link key. The key is pre-negotiated out of band (QR/USB) and is stable,
/// because rotating it would break every peer's pin and every ownership claim.
/// </summary>
public sealed class MeshDevice : IDisposable
{
    private readonly RSA _rsa;

    private MeshDevice(RSA rsa) => _rsa = rsa;

    public static MeshDevice Create()
    {
        var rsa = RSA.Create(3072);
        return new MeshDevice(rsa);
    }

    public static MeshDevice FromPrivateKey(byte[] pkcs8)
    {
        var rsa = RSA.Create();
        rsa.ImportPkcs8PrivateKey(pkcs8, out _);
        return new MeshDevice(rsa);
    }

    public byte[] PublicKeySpki => _rsa.ExportSubjectPublicKeyInfo();
    public byte[] ExportPrivateKey() => _rsa.ExportPkcs8PrivateKey();

    public string DeviceId => "kb1" +
        Convert.ToHexString(SHA256.HashData(PublicKeySpki)).ToLowerInvariant()[..20];

    internal byte[] Sign(string payload) =>
        _rsa.SignData(Utf8.B(payload), HashAlgorithmName.SHA256, RSASignaturePadding.Pss);

    internal static bool Verify(byte[] rsaSpki, string payload, byte[] signature)
    {
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(rsaSpki, out _);
        return rsa.VerifyData(Utf8.B(payload), signature,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
    }

    internal static string IdFor(byte[] rsaSpki) =>
        "kb1" + Convert.ToHexString(SHA256.HashData(rsaSpki)).ToLowerInvariant()[..20];

    public void Dispose() => _rsa.Dispose();
}

/// <summary>
/// One authenticated, encrypted link between two devices.
///
/// Key agreement is ephemeral P-256 ECDH, one fresh key pair per link, so recorded traffic
/// cannot be decrypted later even if both long-term RSA keys leak (forward secrecy). RSA-3072
/// only signs the offers, binding the ephemeral keys to the pinned device identities.
/// The derived AES-256-GCM keys are split per direction and die with the link.
/// </summary>
public sealed class MeshLink : IDisposable
{
    private const string OfferInfo = "kickl/v1/mesh-link";
    private const string OfferDomain = "kickl/v1/mesh-offer";
    private const string AcceptDomain = "kickl/v1/mesh-accept";

    private byte[]? _sendKey;
    private byte[]? _recvKey;
    private ECDiffieHellman? _pendingEcdh;
    private byte[]? _offerEphemeralPublicKey;
    private byte[]? _handshakeNonce;

    private readonly string _selfId;
    private readonly string _peerId;
    private readonly string _side;
    private readonly string _peerSide;
    private uint _sendCounter;
    private uint _recvCounter;
    private bool _disposed;

    private MeshLink(string selfId, string peerId, string side)
    {
        _selfId = selfId;
        _peerId = peerId;
        _side = side;
        _peerSide = side == "initiator" ? "responder" : "initiator";
    }

    private MeshLink(byte[] sendKey, byte[] recvKey, string selfId, string peerId, string side)
        : this(selfId, peerId, side)
    {
        _sendKey = sendKey;
        _recvKey = recvKey;
    }

    public string PeerId => _peerId;
    public bool IsEstablished => _sendKey is not null;

    private static string OfferPayload(MeshLinkOffer offer) =>
        $"{OfferDomain}|{offer.FromDeviceId}|{offer.ToDeviceId}|" +
        $"{Convert.ToBase64String(offer.EphemeralPublicKey)}|" +
        $"{Convert.ToBase64String(offer.HandshakeNonce)}";

    private static string AcceptPayload(string initiatorId, string responderId,
        byte[] initiatorEphemeral, byte[] responderEphemeral, byte[] nonce) =>
        $"{AcceptDomain}|{initiatorId}|{responderId}|" +
        $"{Convert.ToBase64String(initiatorEphemeral)}|" +
        $"{Convert.ToBase64String(responderEphemeral)}|" +
        $"{Convert.ToBase64String(nonce)}";

    /// <summary>
    /// The link keys are bound to the whole handshake (both device ids, both ephemeral keys, the nonce), not just the shared secret,
    /// so two sessions can never end up with the same keys, and a key from one transcript means nothing in another.
    /// </summary>
    private static (byte[] Send, byte[] Recv) DeriveKeys(string selfId, string peerId, string initiatorId, string responderId,
        byte[] initiatorEphemeral, byte[] responderEphemeral, byte[] rawSecret, byte[] nonce)
    {
        var salt = SHA256.HashData(Utf8.B($"{OfferInfo}|{initiatorId}|{responderId}|" +
            $"{Convert.ToBase64String(initiatorEphemeral)}|{Convert.ToBase64String(responderEphemeral)}|{Convert.ToBase64String(nonce)}"));
        var both = Hkdf.Derive(rawSecret, salt, OfferInfo, 64);
        var first = both[..32];
        var second = both[32..];
        CryptographicOperations.ZeroMemory(both);

        var selfIsA = string.CompareOrdinal(selfId, peerId) < 0;
        return selfIsA ? (first, second) : (second, first);
    }

    /// <summary>Only a P-256 key is accepted; anything else (another curve, garbage) is a refused handshake, not a crash.</summary>
    private static ECDiffieHellman ImportP256(byte[] spki)
    {
        var key = ECDiffieHellman.Create();
        try
        {
            key.ImportSubjectPublicKeyInfo(spki, out _);
            if (key.ExportParameters(false).Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
                throw new MeshException("The link uses a key on an unsupported curve.");
            return key;
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            key.Dispose();
            throw new MeshException("The link key is not valid.");
        }
        catch (MeshException)
        {
            key.Dispose();
            throw;
        }
    }

    /// <summary>Initiator: create a one-time ECDH key and a signed offer. Nothing is sent yet.</summary>
    public static MeshLink Offer(MeshDevice self, MeshDevice peer, out MeshLinkOffer offer)
    {
        var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var nonce = RandomNumberGenerator.GetBytes(16);

        offer = new MeshLinkOffer
        {
            FromDeviceId = self.DeviceId,
            ToDeviceId = peer.DeviceId,
            FromRsaSpki = self.PublicKeySpki,
            EphemeralPublicKey = ecdh.ExportSubjectPublicKeyInfo(),
            HandshakeNonce = nonce
        };
        offer.Signature = self.Sign(OfferPayload(offer));

        var link = new MeshLink(self.DeviceId, peer.DeviceId, "initiator")
        {
            _pendingEcdh = ecdh,
            _offerEphemeralPublicKey = offer.EphemeralPublicKey,
            _handshakeNonce = nonce
        };
        return link;
    }

    /// <summary>
    /// Responder: verify the pinned RSA signature, create its own one-time ECDH key, derive
    /// the link keys immediately, and return a signed accept. The ephemeral private keys are
    /// discarded here; no long-term key can reconstruct them.
    /// </summary>
    /// <param name="expectedPeerId">
    /// The device this link must come from (the pinned id). An offer signed by any other key is refused: a valid signature only
    /// proves the sender holds <i>some</i> key, and the id is what ties it to a device this one agreed to talk to.
    /// </param>
    public static MeshLink Accept(MeshDevice self, MeshLinkOffer offer, out MeshLinkAccept accept, string? expectedPeerId = null)
    {
        if (expectedPeerId is not null && !string.Equals(offer.FromDeviceId, expectedPeerId, StringComparison.Ordinal))
            throw new MeshException("This offer is from a device that was not expected.");
        if (!string.Equals(offer.ToDeviceId, self.DeviceId, StringComparison.Ordinal))
            throw new MeshException("This offer is addressed to a different device.");

        if (!string.Equals(MeshDevice.IdFor(offer.FromRsaSpki), offer.FromDeviceId, StringComparison.Ordinal))
            throw new MeshException("The offer's device id does not match its key.");

        if (!MeshDevice.Verify(offer.FromRsaSpki, OfferPayload(offer), offer.Signature))
            throw new MeshException("The link offer signature is invalid.");

        if (offer.HandshakeNonce.Length != 16)
            throw new MeshException("The link offer has a malformed nonce.");
        using var initiator = ImportP256(offer.EphemeralPublicKey);

        using var responder = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var responderEphemeral = responder.ExportSubjectPublicKeyInfo();
        var raw = responder.DeriveRawSecretAgreement(initiator.PublicKey);
        byte[] send;
        byte[] recv;
        try
        {
            (send, recv) = DeriveKeys(self.DeviceId, offer.FromDeviceId, offer.FromDeviceId, self.DeviceId,
                offer.EphemeralPublicKey, responderEphemeral, raw, offer.HandshakeNonce);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(raw);
        }

        accept = new MeshLinkAccept
        {
            DeviceId = self.DeviceId,
            EphemeralPublicKey = responderEphemeral
        };
        accept.Signature = self.Sign(AcceptPayload(offer.FromDeviceId, self.DeviceId,
            offer.EphemeralPublicKey, accept.EphemeralPublicKey, offer.HandshakeNonce));

        return new MeshLink(send, recv, self.DeviceId, offer.FromDeviceId, "responder");
    }

    /// <summary>Initiator: verify the accept, then derive the same one-time link keys.</summary>
    public void Complete(MeshLinkAccept accept, byte[] peerRsaSpki)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pendingEcdh is null || _offerEphemeralPublicKey is null || _handshakeNonce is null)
            throw new MeshException("The link is already established or was not offered.");

        if (!string.Equals(accept.DeviceId, _peerId, StringComparison.Ordinal))
            throw new MeshException("The accept came from the wrong device.");

        if (!string.Equals(MeshDevice.IdFor(peerRsaSpki), _peerId, StringComparison.Ordinal))
            throw new MeshException("The peer key does not match the expected device.");

        if (!MeshDevice.Verify(peerRsaSpki,
                AcceptPayload(_selfId, accept.DeviceId, _offerEphemeralPublicKey,
                    accept.EphemeralPublicKey, _handshakeNonce),
                accept.Signature))
            throw new MeshException("The link accept signature is invalid.");

        using var responder = ImportP256(accept.EphemeralPublicKey);

        var raw = _pendingEcdh.DeriveRawSecretAgreement(responder.PublicKey);
        try
        {
            var (send, recv) = DeriveKeys(_selfId, _peerId, _selfId, _peerId,
                _offerEphemeralPublicKey, accept.EphemeralPublicKey, raw, _handshakeNonce);
            _sendKey = send;
            _recvKey = recv;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(raw);
            _pendingEcdh.Dispose();
            _pendingEcdh = null;
        }
    }

    private static byte[] Aad(uint counter, string senderSide) =>
        Utf8.B($"kickl/v1/mesh-frame|{senderSide}|{counter}");

    public MeshFrame Send(ReadOnlySpan<byte> plaintext)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_sendKey is null) throw new MeshException("Complete the handshake before sending.");
        if (_sendCounter == uint.MaxValue)
            throw new MeshException("This link has sent the most frames it safely can. Open a new link.");
        var counter = ++_sendCounter;
        var nonce = Aead.CounterNonce(counter);
        var ciphertext = Aead.SealWithNonce(_sendKey, nonce, plaintext, Aad(counter, _side));
        return new MeshFrame { Counter = counter, Nonce = nonce, Ciphertext = ciphertext };
    }

    public byte[] Receive(MeshFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_recvKey is null) throw new MeshException("Complete the handshake before receiving.");
        if (frame.Counter != _recvCounter + 1)
            throw new MeshException($"Frame counter {frame.Counter} is not the next expected value.");

        // The nonce is a function of the counter; a frame that carries any other nonce is not from an honest peer
        if (!CryptographicOperations.FixedTimeEquals(frame.Nonce, Aead.CounterNonce(frame.Counter)))
            throw new MeshException("The frame's nonce does not match its counter.");
        byte[] plaintext;
        try { plaintext = Aead.Open(_recvKey, frame.Nonce, frame.Ciphertext, Aad(frame.Counter, _peerSide)); }
        catch (CryptographicException) { throw new MeshException("The frame failed authentication."); }
        _recvCounter++;
        return plaintext;
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (_sendKey is not null) CryptographicOperations.ZeroMemory(_sendKey);
        if (_recvKey is not null) CryptographicOperations.ZeroMemory(_recvKey);
        _pendingEcdh?.Dispose();
        _pendingEcdh = null;
        _sendKey = null;
        _recvKey = null;
        _disposed = true;
    }
}
