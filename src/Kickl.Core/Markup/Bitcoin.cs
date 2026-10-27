using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Kickl.Core.Markup;

public enum BitcoinNetwork { Mainnet, Testnet }

public readonly record struct BitcoinAddressInfo(BitcoinNetwork Network, string Kind);

/// <summary>
/// Checks a Bitcoin address completely, checksum included, so a typo in a shop page is caught when the page is published and never
/// becomes a payment sent to nobody. Understands legacy Base58Check (P2PKH, P2SH) and Segwit Bech32 / Bech32m (P2WPKH, P2WSH, P2TR),
/// on the main network and on testnet. It only checks that an address is well formed; it can never know who owns it.
/// </summary>
public static class BitcoinAddress
{
    private const string Base58Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
    private const string Bech32Charset = "qpzry9x8gf2tvdw0s3jn54khce6mua7l";

    public const int MaxLength = 90;

    public static BitcoinAddressInfo? Parse(string? address)
    {
        if (string.IsNullOrEmpty(address) || address.Length is < 14 or > MaxLength) return null;
        if (address.Any(c => c > 126 || c < 33)) return null;
        var lower = address.ToLowerInvariant();
        if (lower.StartsWith("bc1", StringComparison.Ordinal) || lower.StartsWith("tb1", StringComparison.Ordinal))
            return ParseSegwit(address);
        return ParseBase58(address);
    }

    public static bool IsValid(string? address) => Parse(address) is not null;

    // ---------------------------------------------------------------- Base58Check

    private static BitcoinAddressInfo? ParseBase58(string address)
    {
        BigInteger value = BigInteger.Zero;
        foreach (var c in address)
        {
            var digit = Base58Alphabet.IndexOf(c);
            if (digit < 0) return null;
            value = value * 58 + digit;
        }

        var body = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        var leadingZeros = address.TakeWhile(c => c == '1').Count();
        var bytes = new byte[leadingZeros + body.Length];
        body.CopyTo(bytes, leadingZeros);
        if (value.IsZero) bytes = new byte[leadingZeros];
        if (bytes.Length != 25) return null;

        var check = SHA256.HashData(SHA256.HashData(bytes.AsSpan(0, 21)));
        if (!CryptographicOperations.FixedTimeEquals(check.AsSpan(0, 4), bytes.AsSpan(21, 4))) return null;

        return bytes[0] switch
        {
            0x00 => new BitcoinAddressInfo(BitcoinNetwork.Mainnet, "P2PKH"),
            0x05 => new BitcoinAddressInfo(BitcoinNetwork.Mainnet, "P2SH"),
            0x6F => new BitcoinAddressInfo(BitcoinNetwork.Testnet, "P2PKH"),
            0xC4 => new BitcoinAddressInfo(BitcoinNetwork.Testnet, "P2SH"),
            _ => null
        };
    }

    // ---------------------------------------------------------------- Bech32 / Bech32m (BIP 173, BIP 350)

    private static BitcoinAddressInfo? ParseSegwit(string address)
    {
        // A mix of upper and lower case is invalid; all of either is fine
        if (address.Any(char.IsUpper) && address.Any(char.IsLower)) return null;
        var s = address.ToLowerInvariant();
        var sep = s.LastIndexOf('1');
        if (sep < 1 || sep + 7 > s.Length) return null;   // at least 6 checksum characters, and something before the 1

        var hrp = s[..sep];
        if (hrp is not ("bc" or "tb")) return null;
        var data = new int[s.Length - sep - 1];
        for (var i = 0; i < data.Length; i++)
        {
            var v = Bech32Charset.IndexOf(s[sep + 1 + i]);
            if (v < 0) return null;
            data[i] = v;
        }

        var polymod = Polymod(HrpExpand(hrp).Concat(data));
        var version = data[0];
        if (version > 16) return null;
        // Version 0 uses the original Bech32 constant, every later version uses Bech32m
        var expected = version == 0 ? 1u : 0x2bc830a3u;
        if (polymod != expected) return null;

        var program = ConvertBits(data.AsSpan(1, data.Length - 7), 5, 8, pad: false);
        if (program is null) return null;
        var kind = (version, program.Length) switch
        {
            (0, 20) => "P2WPKH",
            (0, 32) => "P2WSH",
            (1, 32) => "P2TR",
            _ => null
        };
        if (kind is null) return null;
        return new BitcoinAddressInfo(hrp == "bc" ? BitcoinNetwork.Mainnet : BitcoinNetwork.Testnet, kind);
    }

    private static IEnumerable<int> HrpExpand(string hrp)
    {
        foreach (var c in hrp) yield return c >> 5;
        yield return 0;
        foreach (var c in hrp) yield return c & 31;
    }

    private static uint Polymod(IEnumerable<int> values)
    {
        uint[] generator = [0x3b6a57b2, 0x26508e6d, 0x1ea119fa, 0x3d4233dd, 0x2a1462b3];
        uint chk = 1;
        foreach (var v in values)
        {
            var top = chk >> 25;
            chk = ((chk & 0x1ffffff) << 5) ^ (uint) v;
            for (var i = 0; i < 5; i++)
                if (((top >> i) & 1) != 0) chk ^= generator[i];
        }
        return chk;
    }

    private static byte[]? ConvertBits(ReadOnlySpan<int> data, int from, int to, bool pad)
    {
        var acc = 0;
        var bits = 0;
        var maxv = (1 << to) - 1;
        var result = new List<byte>();
        foreach (var value in data)
        {
            if (value < 0 || value >> from != 0) return null;
            acc = (acc << from) | value;
            bits += from;
            while (bits >= to)
            {
                bits -= to;
                result.Add((byte) ((acc >> bits) & maxv));
            }
            acc &= (1 << bits) - 1;
        }
        if (pad)
        {
            if (bits > 0) result.Add((byte) ((acc << (to - bits)) & maxv));
        }
        else if (bits >= from || ((acc << (to - bits)) & maxv) != 0)
        {
            return null;   // leftover bits must be fewer than a whole input group and all zero
        }
        return [.. result];
    }
}

/// <summary>Amounts in satoshis, shown without floating point so nothing is ever rounded.</summary>
public static class BtcAmount
{
    public const long SatsPerBtc = 100_000_000;
    /// <summary>21 million bitcoin, the most there will ever be.</summary>
    public const long MaxSats = 21_000_000L * SatsPerBtc;

    /// <summary>0.00050000</summary>
    public static string Btc(long sats) =>
        (sats / SatsPerBtc).ToString(CultureInfo.InvariantCulture) + "." + (sats % SatsPerBtc).ToString("D8", CultureInfo.InvariantCulture);

    /// <summary>50,000</summary>
    public static string Sats(long sats) => sats.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>
    /// A BIP 21 payment link, <c>bitcoin:ADDRESS?amount=0.0005&amp;label=…</c>, which wallets open or scan. Labels are percent-encoded.
    /// </summary>
    public static string PaymentUri(string address, long sats, string? label = null, string? message = null)
    {
        // "0.00050000" -> "0.0005", "1.00000000" -> "1": no trailing zeros, no bare decimal point
        var sb = new StringBuilder("bitcoin:").Append(address).Append("?amount=").Append(Btc(sats).TrimEnd('0').TrimEnd('.'));
        if (!string.IsNullOrWhiteSpace(label)) sb.Append("&label=").Append(Uri.EscapeDataString(label));
        if (!string.IsNullOrWhiteSpace(message)) sb.Append("&message=").Append(Uri.EscapeDataString(message));
        return sb.ToString();
    }
}
