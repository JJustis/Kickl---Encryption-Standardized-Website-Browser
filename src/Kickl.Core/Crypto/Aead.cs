using System.Security.Cryptography;

namespace Kickl.Core.Crypto;

/// <summary>
/// AES-256-GCM only. Nonces are either random (12 bytes) or caller-supplied counters.
/// This is a thin wrapper over the BCL primitive; no custom construction lives here.
/// </summary>
public static class Aead
{
    public const int KeySize = 32;
    public const int NonceSize = 12;
    public const int TagSize = 16;

    public static (byte[] Nonce, byte[] Ciphertext) Seal(
        ReadOnlySpan<byte> key, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> aad)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        return (nonce, SealWithNonce(key, nonce, plaintext, aad));
    }

    public static byte[] SealWithNonce(
        ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> aad)
    {
        if (key.Length != KeySize) throw new ArgumentException("AES-256 requires a 32-byte key.", nameof(key));
        if (nonce.Length != NonceSize) throw new ArgumentException("GCM nonce must be 12 bytes.", nameof(nonce));

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        using var gcm = new AesGcm(key, TagSize);
        gcm.Encrypt(nonce, plaintext, ciphertext, tag, aad);

        var result = new byte[ciphertext.Length + TagSize];
        Buffer.BlockCopy(ciphertext, 0, result, 0, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, result, ciphertext.Length, TagSize);
        CryptographicOperations.ZeroMemory(ciphertext);
        return result;
    }

    public static byte[] Open(
        ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertextAndTag, ReadOnlySpan<byte> aad)
    {
        if (key.Length != KeySize) throw new ArgumentException("AES-256 requires a 32-byte key.", nameof(key));
        if (nonce.Length != NonceSize) throw new ArgumentException("GCM nonce must be 12 bytes.", nameof(nonce));
        if (ciphertextAndTag.Length < TagSize) throw new CryptographicException("Ciphertext is too short.");

        var body = ciphertextAndTag[..^TagSize];
        var tag = ciphertextAndTag[^TagSize..];
        var plaintext = new byte[body.Length];
        using var gcm = new AesGcm(key, TagSize);
        gcm.Decrypt(nonce, body, tag, plaintext, aad);
        return plaintext;
    }

    /// <summary>Deterministic counter nonce: 8-byte big-endian counter + 4 zero bytes.</summary>
    public static byte[] CounterNonce(ulong counter)
    {
        var nonce = new byte[NonceSize];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(nonce, counter);
        return nonce;
    }
}
