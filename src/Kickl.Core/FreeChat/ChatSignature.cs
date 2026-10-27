using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Kickl.Core.FreeChat;

/// <summary>How far a received message can be trusted to come from the member it claims.</summary>
public enum SenderTrust
{
    /// <summary>No signature (a web-client or older message). Delivered, but the sender name is only the server's word.</summary>
    Unsigned,
    /// <summary>Signed with the key this profile pinned for that member, and bound to the member's room key.</summary>
    Verified,
    /// <summary>Signed, and this is the first time that member's signing key was seen, so it is now pinned.</summary>
    FirstSeen,
    /// <summary>Signed, but with a different key than the one pinned for that member.</summary>
    KeyChanged,
    /// <summary>The signature does not verify, or it names a different sender, room key or position than the server reported.</summary>
    Invalid
}

/// <summary>
/// Sender authentication that needs no change to the chat service. Each member has an ML-DSA-65 signing key. The signed trailer is
/// appended after the PNG's end marker, where every PNG reader ignores it, so older clients still show the picture. The signature
/// covers the room, epoch, position, sender, the image bytes and a hash of the sender's own ML-KEM room key; verifying it against a
/// pinned signing key tells a receiver the message really came from that member, and the key hash shows when the server is listing
/// a different room key for them than the one they use.
/// </summary>
public static class ChatSignature
{
    private static readonly byte[] End = "KSG1"u8.ToArray();
    private const int MaxTrailer = 16 * 1024;

    public sealed record Parsed(byte[] Png, byte[] PublicKey, byte[] Signature, byte[] KemHash);

    private static byte[] Message(int session, int epoch, int index, int userId, string username, byte[] png, byte[] kemHash)
    {
        using var ms = new MemoryStream();
        void Add(byte[] b) { ms.Write(b); }
        Add(Encoding.UTF8.GetBytes($"kickl/v1/chat-message|{session}|{epoch}|{index}|{userId}|{username.ToLowerInvariant()}|"));
        Add(SHA256.HashData(png));
        Add(kemHash);
        return ms.ToArray();
    }

    public static byte[] KemHash(byte[] kemPublicKey) => SHA256.HashData(kemPublicKey);

    /// <summary>The PNG with the signed trailer appended.</summary>
    public static byte[] Sign(MLDsa key, byte[] png, int session, int epoch, int index, int userId, string username, byte[] kemHash)
    {
        var sig = key.SignData(Message(session, epoch, index, userId, username, png, kemHash));
        var pub = key.ExportMLDsaPublicKey();
        using var ms = new MemoryStream();
        ms.Write(png);
        var start = ms.Position;
        ms.Write(kemHash);
        var lens = new byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(lens, (ushort) pub.Length);
        BinaryPrimitives.WriteUInt16BigEndian(lens.AsSpan(2), (ushort) sig.Length);
        ms.Write(lens);
        ms.Write(pub);
        ms.Write(sig);
        var body = (int) (ms.Position - start);
        var footer = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(footer, body);
        ms.Write(footer);
        ms.Write(End);
        return ms.ToArray();
    }

    /// <summary>Split a received payload into the picture and its trailer, or null if it carries none (or a malformed one).</summary>
    public static Parsed? TryParse(byte[] payload)
    {
        if (payload.Length < 12 || !payload.AsSpan(^4).SequenceEqual(End)) return null;
        var body = BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(payload.Length - 8, 4));
        if (body < 36 || body > MaxTrailer || body + 8 > payload.Length) return null;
        var start = payload.Length - 8 - body;
        var span = payload.AsSpan(start, body);
        var kemHash = span[..32].ToArray();
        var pubLen = BinaryPrimitives.ReadUInt16BigEndian(span[32..]);
        var sigLen = BinaryPrimitives.ReadUInt16BigEndian(span[34..]);
        if (36 + pubLen + sigLen != body) return null;
        return new Parsed(payload[..start], span.Slice(36, pubLen).ToArray(), span.Slice(36 + pubLen, sigLen).ToArray(), kemHash);
    }

    public static bool Verify(Parsed parsed, int session, int epoch, int index, int userId, string username)
    {
        try
        {
            using var key = MLDsa.ImportMLDsaPublicKey(MLDsaAlgorithm.MLDsa65, parsed.PublicKey);
            return key.VerifyData(Message(session, epoch, index, userId, username, parsed.Png, parsed.KemHash), parsed.Signature);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    public static string Fingerprint(byte[] signingPublicKey) =>
        Convert.ToHexString(SHA256.HashData(signingPublicKey)).ToLowerInvariant();
}
