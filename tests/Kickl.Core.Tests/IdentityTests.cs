using System.Text;
using Kickl.Core.Identity;

namespace Kickl.Core.Tests;

public class IdentityTests
{
    [Theory]
    [InlineData("A1LQFN3A", "a1lqfn3a")]
    [InlineData("a1lqfn3a", "a1lqfn3a")]
    [InlineData("?1v759aa", "?1v759aa")]
    [InlineData("abcdef1l7aum6echk45nj3s0wdvt2fg8x9yrzpqzd3ryx",
        "abcdef1l7aum6echk45nj3s0wdvt2fg8x9yrzpqzd3ryx")]
    public void Bech32m_DecodesBip350Vectors_AndReencodesCanonically(string input, string expected)
    {
        Assert.True(Bech32m.TryDecode(input, out _, out var data, out var error), error);
        var reencoded = Bech32m.Encode(input.Split('1')[0].ToLowerInvariant(), data);
        Assert.Equal(expected, reencoded);
    }

    [Fact]
    public void Bech32m_RejectsBech32ChecksumConstant()
    {
        // Valid bech32 (BIP-173) but not bech32m: the checksum constants differ.
        Assert.False(Bech32m.TryDecode("split1checkupstagehandshakeupstreamerranterredcaperred2y9e3w",
            out _, out _, out var error));
        Assert.Contains("checksum", error);
    }

    [Fact]
    public void Bech32m_RejectsMixedCaseAndBadAlphabet()
    {
        Assert.False(Bech32m.TryDecode("aB1qqqqqq", out _, out _, out var mixed));
        Assert.Contains("Mixed-case", mixed);
        Assert.False(Bech32m.TryDecode("kb1bbbbbb", out _, out _, out var bad));
        Assert.NotEqual("", bad);
    }

    [Fact]
    public void IdentityAddress_RoundTrips_AndCarriesTheDigest()
    {
        var bundle = Bundle(0x11, 0x22, "Nyx");
        var address = IdentityAddress.FromBundle(bundle);

        Assert.StartsWith("kb1", address);
        Assert.Equal(43, address.Length);
        Assert.True(IdentityAddress.TryDecode(address, out var digest, out var error), error);
        Assert.Equal(IdentityAddress.Digest(bundle.ToCanonicalBytes()), digest);
        Assert.True(IdentityAddress.IsValid(address.ToUpperInvariant()));
    }

    [Fact]
    public void IdentityAddress_DetectsASingleCharacterEdit()
    {
        var address = IdentityAddress.FromBundle(Bundle(0x33, 0x44, "Ari"));
        var index = 6;
        var replacement = address[index] == 'q' ? 'p' : 'q';
        var tampered = address[..index] + replacement + address[(index + 1)..];

        Assert.False(IdentityAddress.TryDecode(tampered, out _, out var error));
        Assert.NotEqual("", error);
    }

    [Fact]
    public void IdentityAddress_RejectsAForeignNamespace()
    {
        var digest = new byte[IdentityAddress.DigestSize];
        var foreign = Bech32m.Encode("nk", [IdentityAddress.Version, .. digest]);

        Assert.False(IdentityAddress.TryDecode(foreign, out _, out var error));
        Assert.Contains("kb1", error);
    }

    [Fact]
    public void IdentityBundle_CanonicalBytesRoundTrip()
    {
        var bundle = Bundle(0x55, 0x66, "Rae");
        var restored = IdentityBundle.FromCanonicalBytes(bundle.ToCanonicalBytes());

        Assert.Equal(bundle.SignPublicKey, restored.SignPublicKey);
        Assert.Equal(bundle.KemPublicKey, restored.KemPublicKey);
        Assert.Equal(bundle.Handle, restored.Handle);
        Assert.Equal(bundle.Salt, restored.Salt);
        Assert.Equal(bundle.Address, restored.Address);
    }

    [Fact]
    public void IdentityBundle_RejectsShortKeysAndLongHandles()
    {
        Assert.Throws<FormatException>(() =>
            new IdentityBundle(new byte[8], new byte[32], "", new byte[16]).ToCanonicalBytes());
        Assert.Throws<FormatException>(() =>
            new IdentityBundle(new byte[32], new byte[32], new string('x', 33), new byte[16]).ToCanonicalBytes());
        Assert.Throws<FormatException>(() =>
            new IdentityBundle(new byte[32], new byte[32], "ok", new byte[15]).ToCanonicalBytes());
    }

    [Fact]
    public void IdentityBundle_RejectsTruncatedOrUnknownVersions()
    {
        var bytes = Bundle(0x77, 0x88, "Dev").ToCanonicalBytes();

        Assert.Throws<FormatException>(() => IdentityBundle.FromCanonicalBytes(bytes[..^4]));
        var wrongVersion = (byte[])bytes.Clone();
        wrongVersion[0] = 9;
        Assert.Throws<FormatException>(() => IdentityBundle.FromCanonicalBytes(wrongVersion));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(482173)]
    [InlineData(999999)]
    public void ShortCode_CarriesValidMod97CheckDigits(int seed)
    {
        var code = ShortCode.Create(seed);

        Assert.Equal(8, code.Length);
        Assert.True(ShortCode.TryParse(code, out var parsed, out var error), error);
        Assert.Equal(code, parsed);
        Assert.Equal(1, long.Parse(code) % 97);
    }

    [Fact]
    public void ShortCode_AcceptsSpacingAndRejectsAnAlteredDigit()
    {
        var code = ShortCode.Create(482173);
        Assert.True(ShortCode.TryParse(ShortCode.Format(code), out var parsed, out _));
        Assert.Equal(code, parsed);
        Assert.Equal("4821 7390".Length, ShortCode.Format("48217390").Length);

        var last = code[^1] == '9' ? '0' : (char)(code[^1] + 1);
        var tampered = code[..^1] + last;
        Assert.False(ShortCode.TryParse(tampered, out _, out var error));
        Assert.Contains("check digits", error);

        Assert.False(ShortCode.TryParse("1234567", out _, out _));
        Assert.False(ShortCode.TryParse("1234567a", out _, out _));
    }

    [Fact]
    public void ShortCode_RejectsOutOfRangeSeeds()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ShortCode.Create(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ShortCode.Create(1_000_000));
    }

    [Fact]
    public void SafetyNumber_IsOrderIndependent_AndKeySensitive()
    {
        var a = Bundle(0x91, 0xa1, "Nyx");
        var b = Bundle(0x92, 0xa2, "Ari");

        var forward = SafetyNumber.Compute(a.Address, b.Address, a.KemPublicKey, b.KemPublicKey);
        var reverse = SafetyNumber.Compute(b.Address, a.Address, b.KemPublicKey, a.KemPublicKey);

        Assert.Equal(forward, reverse);
        Assert.Equal(12, forward.Split(' ').Length);
        Assert.All(forward.Split(' '), group => Assert.Equal(5, group.Length));

        var changedKey = Bundle(0x93, 0xa2, "Ari");
        var changed = SafetyNumber.Compute(a.Address, changedKey.Address, a.KemPublicKey,
            changedKey.KemPublicKey);
        Assert.NotEqual(forward, changed);
    }

    [Fact]
    public void SafetyNumber_RejectsNonKicklAddresses()
    {
        var bundle = Bundle(0x94, 0xa4, "Nyx");
        Assert.Throws<ArgumentException>(() =>
            SafetyNumber.Compute("https://example.com", bundle.Address, bundle.KemPublicKey,
                bundle.KemPublicKey));
    }

    private static IdentityBundle Bundle(byte sign, byte kem, string handle) =>
        new(Enumerable.Repeat(sign, 32).ToArray(),
            Enumerable.Repeat(kem, 1216).ToArray(),
            handle,
            Encoding.UTF8.GetBytes("0123456789abcdef"));
}
