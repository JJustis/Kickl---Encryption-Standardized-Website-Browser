using System.Security.Cryptography;
using System.Text;

namespace Kickl.Core.Identity;

/// <summary>
/// The public part of an identity. Private keys never appear here.
/// The canonical byte form is versioned and length-prefixed so the digest is stable.
/// </summary>
public sealed record IdentityBundle(
    byte[] SignPublicKey,
    byte[] KemPublicKey,
    string Handle,
    byte[] Salt)
{
    public const byte Version = 1;
    public const int SaltSize = 16;
    public const int MaxHandleBytes = 32;
    public const int MinKeyBytes = 32;
    public const int MaxKeyBytes = 4096;

    public static IdentityBundle Create(byte[] signPublicKey, byte[] kemPublicKey, string handle)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        return new IdentityBundle(signPublicKey, kemPublicKey, handle, salt);
    }

    public byte[] ToCanonicalBytes()
    {
        Validate();
        var handle = Encoding.UTF8.GetBytes(Handle);
        var bytes = new byte[1 + 2 + SignPublicKey.Length + 2 + KemPublicKey.Length + 2 + handle.Length + SaltSize];
        var offset = 0;

        bytes[offset++] = Version;
        offset = WriteField(bytes, offset, SignPublicKey);
        offset = WriteField(bytes, offset, KemPublicKey);
        offset = WriteField(bytes, offset, handle);
        Salt.CopyTo(bytes, offset);
        return bytes;
    }

    public static IdentityBundle FromCanonicalBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 1 || bytes[0] != Version)
            throw new FormatException("Unsupported identity bundle version.");

        var offset = 1;
        var sign = ReadField(bytes, ref offset);
        var kem = ReadField(bytes, ref offset);
        var handle = ReadField(bytes, ref offset);

        if (bytes.Length - offset != SaltSize)
            throw new FormatException("The identity bundle salt is malformed.");

        var bundle = new IdentityBundle(sign, kem, Encoding.UTF8.GetString(handle),
            bytes[offset..].ToArray());
        bundle.Validate();
        return bundle;
    }

    /// <summary>The stable, checksummed public address: <c>kb1…</c>.</summary>
    public string Address => IdentityAddress.FromBundle(this);

    private void Validate()
    {
        ValidateKey(SignPublicKey, "signing");
        ValidateKey(KemPublicKey, "KEM");
        if (Salt.Length != SaltSize)
            throw new FormatException($"The identity salt must be {SaltSize} bytes.");

        var handleBytes = Encoding.UTF8.GetByteCount(Handle ?? "");
        if (handleBytes > MaxHandleBytes)
            throw new FormatException($"The handle is limited to {MaxHandleBytes} UTF-8 bytes.");
        if (Handle is not null && Handle.Any(char.IsControl))
            throw new FormatException("The handle cannot contain control characters.");
    }

    private static void ValidateKey(byte[] key, string kind)
    {
        if (key.Length < MinKeyBytes || key.Length > MaxKeyBytes)
            throw new FormatException(
                $"The {kind} public key must be {MinKeyBytes}..{MaxKeyBytes} bytes (got {key.Length}).");
    }

    private static int WriteField(byte[] buffer, int offset, byte[] value)
    {
        if (value.Length > ushort.MaxValue)
            throw new FormatException("Identity bundle field is too large.");
        buffer[offset++] = (byte)(value.Length >> 8);
        buffer[offset++] = (byte)value.Length;
        value.CopyTo(buffer, offset);
        return offset + value.Length;
    }

    private static byte[] ReadField(ReadOnlySpan<byte> bytes, ref int offset)
    {
        if (offset + 2 > bytes.Length)
            throw new FormatException("The identity bundle is truncated.");
        var length = (bytes[offset] << 8) | bytes[offset + 1];
        offset += 2;
        if (offset + length > bytes.Length)
            throw new FormatException("The identity bundle is truncated.");
        var value = bytes.Slice(offset, length).ToArray();
        offset += length;
        return value;
    }
}

/// <summary>
/// <c>kb1…</c> address: a bech32m encoding of <c>version ‖ SHA-256("kickl/identity/v1" ‖ bundle)[0..20]</c>.
/// It is a hash, not a key: publishing it reveals nothing the public keys do not.
/// </summary>
public static class IdentityAddress
{
    public const string HumanReadablePart = "kb";
    public const byte Version = 1;
    public const int DigestSize = 20;
    public const int DataSize = 1 + DigestSize;

    private const string Domain = "kickl/identity/v1";

    public static string FromBundle(IdentityBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        return Encode(Digest(bundle.ToCanonicalBytes()));
    }

    public static string Encode(ReadOnlySpan<byte> digest)
    {
        if (digest.Length != DigestSize)
            throw new ArgumentException($"The address digest must be {DigestSize} bytes.", nameof(digest));

        var data = new byte[DataSize];
        data[0] = Version;
        digest.CopyTo(data.AsSpan(1));
        return Bech32m.Encode(HumanReadablePart, data);
    }

    public static bool TryDecode(string? address, out byte[] digest, out string error)
    {
        digest = [];
        if (!Bech32m.TryDecode(address, out var hrp, out var data, out error))
            return false;

        if (!string.Equals(hrp, HumanReadablePart, StringComparison.Ordinal))
        {
            error = $"Expected a '{HumanReadablePart}1…' address.";
            return false;
        }
        if (data.Length != DataSize || data[0] != Version)
        {
            error = "Unsupported or malformed identity address.";
            return false;
        }

        digest = data[1..];
        return true;
    }

    public static bool IsValid(string? address) => TryDecode(address, out _, out _);

    public static byte[] Digest(ReadOnlySpan<byte> canonicalBundle)
    {
        var domain = Encoding.UTF8.GetBytes(Domain);
        var input = new byte[domain.Length + canonicalBundle.Length];
        domain.CopyTo(input, 0);
        canonicalBundle.CopyTo(input.AsSpan(domain.Length));
        try
        {
            return SHA256.HashData(input)[..DigestSize];
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
        }
    }
}
