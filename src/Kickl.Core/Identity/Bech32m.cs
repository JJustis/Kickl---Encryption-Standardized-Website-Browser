using System.Text;

namespace Kickl.Core.Identity;

/// <summary>
/// Bech32m (BIP-350) encoding. Used for human-checkable Kickl addresses: the
/// checksum catches any single-character edit before the registry is contacted.
/// </summary>
public static class Bech32m
{
    private const string Charset = "qpzry9x8gf2tvdw0s3jn54khce6mua7l";
    private const uint Bech32mConstant = 0x2bc830a3;
    private const int MaxLength = 90;

    private static readonly uint[] Generator =
        [0x3b6a57b2, 0x26508e6d, 0x1ea119fa, 0x3d4233dd, 0x2a1462b3];

    public static string Encode(string humanReadablePart, ReadOnlySpan<byte> data)
    {
        if (!IsValidHrp(humanReadablePart))
            throw new ArgumentException("Invalid bech32m human-readable part.", nameof(humanReadablePart));

        var words = ConvertBits(data, 8, 5, pad: true);
        var encoded = humanReadablePart + "1" + WordsToString(words) + Checksum(humanReadablePart, words);
        if (encoded.Length > MaxLength)
            throw new ArgumentException($"Bech32m strings are limited to {MaxLength} characters.", nameof(data));
        return encoded;
    }

    public static bool TryDecode(string? text, out string humanReadablePart, out byte[] data,
        out string error)
    {
        humanReadablePart = "";
        data = [];
        error = "";

        if (string.IsNullOrEmpty(text))
        {
            error = "The address is empty.";
            return false;
        }
        if (text.Length > MaxLength)
        {
            error = $"Bech32m strings are limited to {MaxLength} characters.";
            return false;
        }

        var hasLower = false;
        var hasUpper = false;
        foreach (var c in text)
        {
            if (c < 33 || c > 126)
            {
                error = "The address contains an invalid character.";
                return false;
            }
            if (c >= 'a' && c <= 'z') hasLower = true;
            if (c >= 'A' && c <= 'Z') hasUpper = true;
        }
        if (hasLower && hasUpper)
        {
            error = "Mixed-case addresses are not valid bech32m.";
            return false;
        }

        var lowered = text.ToLowerInvariant();
        var separator = lowered.LastIndexOf('1');
        if (separator < 1 || separator + 7 > lowered.Length)
        {
            error = "The address has no valid separator.";
            return false;
        }

        var hrp = lowered[..separator];
        if (hrp.Length + 7 > MaxLength)
        {
            error = "The address is too long.";
            return false;
        }

        var words = new byte[lowered.Length - separator - 1];
        for (var i = 0; i < words.Length; i++)
        {
            var index = Charset.IndexOf(lowered[separator + 1 + i]);
            if (index < 0)
            {
                error = "The address contains a character outside the bech32m alphabet.";
                return false;
            }
            words[i] = (byte)index;
        }

        if (Polymod(HrpExpand(hrp), words) != Bech32mConstant)
        {
            error = "The address checksum is invalid.";
            return false;
        }

        var payload = words[..^6];
        if (!TryConvertBits(payload, 5, 8, out var bytes))
        {
            error = "The address payload is malformed.";
            return false;
        }

        humanReadablePart = hrp;
        data = bytes;
        return true;
    }

    private static string Checksum(string hrp, ReadOnlySpan<byte> words)
    {
        var values = new byte[hrp.Length * 2 + 1 + words.Length + 6];
        var expand = HrpExpand(hrp);
        expand.CopyTo(values, 0);
        words.CopyTo(values.AsSpan(expand.Length));

        var polymod = Polymod(values) ^ Bech32mConstant;
        var checksum = new char[6];
        for (var i = 0; i < 6; i++)
            checksum[i] = Charset[(int)((polymod >> (5 * (5 - i))) & 31)];
        return new string(checksum);
    }

    private static byte[] HrpExpand(string hrp)
    {
        var expanded = new byte[hrp.Length * 2 + 1];
        for (var i = 0; i < hrp.Length; i++)
        {
            expanded[i] = (byte)(hrp[i] >> 5);
            expanded[hrp.Length + 1 + i] = (byte)(hrp[i] & 31);
        }
        return expanded;
    }

    private static uint Polymod(ReadOnlySpan<byte> hrpExpanded, ReadOnlySpan<byte> words)
    {
        var values = new byte[hrpExpanded.Length + words.Length];
        hrpExpanded.CopyTo(values);
        words.CopyTo(values.AsSpan(hrpExpanded.Length));
        return Polymod(values);
    }

    private static uint Polymod(ReadOnlySpan<byte> values)
    {
        uint checksum = 1;
        foreach (var value in values)
        {
            var top = checksum >> 25;
            checksum = ((checksum & 0x1ffffff) << 5) ^ value;
            for (var i = 0; i < 5; i++)
                if (((top >> i) & 1) != 0)
                    checksum ^= Generator[i];
        }
        return checksum;
    }

    private static string WordsToString(ReadOnlySpan<byte> words)
    {
        var chars = new char[words.Length];
        for (var i = 0; i < words.Length; i++) chars[i] = Charset[words[i]];
        return new string(chars);
    }

    private static byte[] ConvertBits(ReadOnlySpan<byte> data, int fromBits, int toBits, bool pad)
    {
        if (!TryConvertBits(data, fromBits, toBits, pad, out var result))
            throw new ArgumentException("The value cannot be converted to bech32m groups.");
        return result;
    }

    private static bool TryConvertBits(ReadOnlySpan<byte> data, int fromBits, int toBits, out byte[] result) =>
        TryConvertBits(data, fromBits, toBits, pad: false, out result);

    private static bool TryConvertBits(ReadOnlySpan<byte> data, int fromBits, int toBits, bool pad,
        out byte[] result)
    {
        var buffer = new List<byte>(data.Length * 8 / toBits + 1);
        var accumulator = 0;
        var bits = 0;
        var maxValue = (1 << toBits) - 1;
        var maxAccumulator = (1 << (fromBits + toBits - 1)) - 1;

        foreach (var value in data)
        {
            if (value >> fromBits != 0) { result = []; return false; }
            accumulator = ((accumulator << fromBits) | value) & maxAccumulator;
            bits += fromBits;
            while (bits >= toBits)
            {
                bits -= toBits;
                buffer.Add((byte)((accumulator >> bits) & maxValue));
            }
        }

        if (pad)
        {
            if (bits > 0) buffer.Add((byte)((accumulator << (toBits - bits)) & maxValue));
        }
        else if (bits >= fromBits || ((accumulator << (toBits - bits)) & maxValue) != 0)
        {
            result = [];
            return false;
        }

        result = [.. buffer];
        return true;
    }

    private static bool IsValidHrp(string hrp)
    {
        if (string.IsNullOrEmpty(hrp) || hrp.Length + 7 > MaxLength) return false;
        foreach (var c in hrp)
            if (c < 33 || c > 126 || (c >= 'A' && c <= 'Z'))
                return false;
        return true;
    }
}
