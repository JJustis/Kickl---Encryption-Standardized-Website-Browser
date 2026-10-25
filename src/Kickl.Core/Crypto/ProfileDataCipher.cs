using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Kickl.Core.Crypto;

/// <summary>Purpose-separated AES-GCM envelopes for profile-owned private files.</summary>
public static class ProfileDataCipher
{
    private sealed class Envelope
    {
        public int Version { get; set; }
        public string Purpose { get; set; } = "";
        public string Nonce { get; set; } = "";
        public string Ciphertext { get; set; } = "";
    }

    public static byte[] Protect(ReadOnlySpan<byte> masterKey, string purpose, ReadOnlySpan<byte> plaintext)
    {
        Validate(masterKey, purpose);
        var key = Hkdf.Derive(masterKey, ReadOnlySpan<byte>.Empty, "kickl/v1/profile-data/" + purpose);
        try
        {
            var aad = Encoding.UTF8.GetBytes("kickl/v1/profile-data-file|" + purpose + "|1");
            var sealedData = Aead.Seal(key, plaintext, aad);
            return JsonSerializer.SerializeToUtf8Bytes(new Envelope
            {
                Version = 1,
                Purpose = purpose,
                Nonce = Convert.ToBase64String(sealedData.Nonce),
                Ciphertext = Convert.ToBase64String(sealedData.Ciphertext)
            });
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public static byte[] Open(ReadOnlySpan<byte> masterKey, string purpose, ReadOnlySpan<byte> envelopeBytes)
    {
        Validate(masterKey, purpose);
        var envelope = JsonSerializer.Deserialize<Envelope>(envelopeBytes)
            ?? throw new CryptographicException("Private profile data is invalid.");
        if (envelope.Version != 1 || !string.Equals(envelope.Purpose, purpose, StringComparison.Ordinal))
            throw new CryptographicException("Private profile data purpose or version does not match.");

        byte[] nonce;
        byte[] ciphertext;
        try
        {
            nonce = Convert.FromBase64String(envelope.Nonce);
            ciphertext = Convert.FromBase64String(envelope.Ciphertext);
        }
        catch (FormatException ex) { throw new CryptographicException("Private profile data is invalid.", ex); }

        var key = Hkdf.Derive(masterKey, ReadOnlySpan<byte>.Empty, "kickl/v1/profile-data/" + purpose);
        try
        {
            var aad = Encoding.UTF8.GetBytes("kickl/v1/profile-data-file|" + purpose + "|1");
            return Aead.Open(key, nonce, ciphertext, aad);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static void Validate(ReadOnlySpan<byte> masterKey, string purpose)
    {
        if (masterKey.Length != Aead.KeySize)
            throw new ArgumentException("Profile master key must be 32 bytes.", nameof(masterKey));
        if (string.IsNullOrWhiteSpace(purpose) || purpose.Length > 64 ||
            purpose.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            throw new ArgumentException("Profile data purpose must be a short ASCII identifier.", nameof(purpose));
    }
}
