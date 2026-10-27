using Kickl.Core.Markup;
using Xunit;

namespace Kickl.Core.Tests;

public class BitcoinAddressTests
{
    [Theory]
    [InlineData("BC1QW508D6QEJXTDG4Y5R3ZARVARY0C5XW7KV8F3T4", BitcoinNetwork.Mainnet, "P2WPKH")]            // BIP 173, upper case
    [InlineData("bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4", BitcoinNetwork.Mainnet, "P2WPKH")]
    [InlineData("tb1qw508d6qejxtdg4y5r3zarvary0c5xw7kxpjzsx", BitcoinNetwork.Testnet, "P2WPKH")]
    [InlineData("tb1qrp33g0q5c5txsp9arysrx4k6zdkfs4nce4xj0gdcccefvpysxf3q0sl5k7", BitcoinNetwork.Testnet, "P2WSH")]
    [InlineData("bc1p0xlxvlhemja6c4dqv22uapctqupfhlxm9h8z3k2e72q4k9hcz7vqzk5jj0", BitcoinNetwork.Mainnet, "P2TR")]   // BIP 350
    [InlineData("1BvBMSEYstWetqTFn5Au4m4GFg7xJaNVN2", BitcoinNetwork.Mainnet, "P2PKH")]
    [InlineData("3J98t1WpEZ73CNmQviecrnyiWrnqRhWNLy", BitcoinNetwork.Mainnet, "P2SH")]
    [InlineData("mipcBbFg9gMiCh81Kj8tqqdgoZub1ZJRfn", BitcoinNetwork.Testnet, "P2PKH")]
    [InlineData("2MzQwSSnBHWHqSAqtTVQ6v47XtaisrJa1Vc", BitcoinNetwork.Testnet, "P2SH")]
    public void RealAddresses_AreAccepted(string address, BitcoinNetwork network, string kind)
    {
        var info = BitcoinAddress.Parse(address);
        Assert.NotNull(info);
        Assert.Equal(network, info!.Value.Network);
        Assert.Equal(kind, info.Value.Kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("tc1qw508d6qejxtdg4y5r3zarvary0c5xw7kg3g4ty")]                  // unknown network prefix
    [InlineData("bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t5")]                  // one character off: bad checksum
    [InlineData("BC13W508D6QEJXTDG4Y5R3ZARVARY0C5XW7KN40WF2")]                  // witness version too high for the checksum
    [InlineData("bc1zw508d6qejxtdg4y5r3zarvaryvqyzf3du")]                       // bad padding
    [InlineData("tb1qrp33g0q5C5txsp9arysrx4k6zdkfs4nce4xj0gdcccefvpysxf3q0sl5k7")] // mixed case
    [InlineData("bc1p0xlxvlhemja6c4dqv22uapctqupfhlxm9h8z3k2e72q4k9hcz7vqh2y7hd")] // taproot with the old checksum constant
    [InlineData("bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4 ")]                 // trailing space
    [InlineData("1BvBMSEYstWetqTFn5Au4m4GFg7xJaNVN3")]                          // legacy, last character changed
    [InlineData("1BvBMSEYstWetqTFn5Au4m4GFg7xJaNVN")]                           // legacy, truncated
    [InlineData("0BvBMSEYstWetqTFn5Au4m4GFg7xJaNVN2")]                          // '0' is not in the Base58 alphabet
    public void MistypedOrForeignAddresses_AreRefused(string address) =>
        Assert.False(BitcoinAddress.IsValid(address));

    [Fact]
    public void EveryOneCharacterTypo_OfARealAddress_IsCaught()
    {
        foreach (var real in new[] { "bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4", "1BvBMSEYstWetqTFn5Au4m4GFg7xJaNVN2" })
        {
            for (var i = 0; i < real.Length; i++)
            {
                var chars = real.ToCharArray();
                chars[i] = chars[i] == 'q' ? 'p' : 'q';
                if (new string(chars) == real) continue;
                Assert.False(BitcoinAddress.IsValid(new string(chars)), $"typo at {i} was not caught");
            }
        }
    }
}

public class BtcAmountTests
{
    [Theory]
    [InlineData(1L, "0.00000001")]
    [InlineData(50_000L, "0.00050000")]
    [InlineData(100_000_000L, "1.00000000")]
    [InlineData(2_100_000_000_000_000L, "21000000.00000000")]
    public void Btc_IsExact(long sats, string expected) => Assert.Equal(expected, BtcAmount.Btc(sats));

    [Fact]
    public void Sats_HasSeparators() => Assert.Equal("1,234,567", BtcAmount.Sats(1_234_567));

    [Theory]
    [InlineData(50_000L, "bitcoin:ADDR?amount=0.0005")]
    [InlineData(100_000_000L, "bitcoin:ADDR?amount=1")]
    [InlineData(1_000_000_000L, "bitcoin:ADDR?amount=10")]
    public void PaymentUri_HasNoTrailingZeros(long sats, string expected) =>
        Assert.Equal(expected, BtcAmount.PaymentUri("ADDR", sats));

    [Fact]
    public void PaymentUri_EncodesTheLabel() =>
        Assert.Equal("bitcoin:ADDR?amount=0.0005&label=Path%20%26%20Forge&message=order%2042",
            BtcAmount.PaymentUri("ADDR", 50_000, "Path & Forge", "order 42"));
}

public class ShopMarkupTests
{
    private const string Testnet = "tb1qw508d6qejxtdg4y5r3zarvary0c5xw7kxpjzsx";
    private const string Mainnet = "bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4";

    private static string Page(string body) =>
        $"""<kml v="1" site="s.com"><head><title>T</title></head><body><page>{body}</page></body></kml>""";

    private static PageView Parse(string body) => Kml.Parse(Page(body), "s.com");

    [Fact]
    public void AShop_ParsesIntoCardsAndASealedBlobPerFile()
    {
        var page = Parse($"""
            <shop name="My shop" address="{Mainnet}">
              <product id="guide" name="Field guide" price="50000" blob="guide" access="protected">A PDF about <b>mushrooms</b>.</product>
              <product id="sticker" name="Sticker" price="1500">A sticker.</product>
            </shop>
            """);
        var shop = Assert.Single(page.Blocks, b => b.Kind == BlockKind.Shop);
        Assert.Equal("My shop", shop.Text);
        Assert.Equal(Mainnet, shop.Address);
        Assert.False(shop.Testnet);
        Assert.Equal(2, shop.Products.Count);
        Assert.Equal(50_000, shop.Products[0].PriceSats);
        Assert.Equal("A PDF about mushrooms.", shop.Products[0].Description);
        Assert.Equal("guide", shop.Products[0].BlobId);

        var blob = Assert.Single(page.Blocks, b => b.Kind == BlockKind.Blob);
        Assert.Equal("guide", blob.Id);
        Assert.True(blob.Protected);
    }

    [Fact]
    public void ATestnetAddress_IsMarkedSo()
    {
        var page = Parse($"""<shop address="{Testnet}"><product id="a" name="A" price="1">x</product></shop>""");
        Assert.True(Assert.Single(page.Blocks).Testnet);
        var text = page.Render("jttp://s.com/", false, new HashSet<string>(), new Dictionary<string, string>());
        Assert.Contains("TESTNET, no real money", text);
    }

    [Theory]
    [InlineData("""<shop><product id="a" name="A" price="1">x</product></shop>""", "address")]
    [InlineData("""<shop address="bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t5"><product id="a" name="A" price="1">x</product></shop>""", "valid Bitcoin")]
    [InlineData("""<shop address="ADDR"></shop>""", "at least one")]
    [InlineData("""<shop address="ADDR"><product id="a" name="A" price="0">x</product></shop>""", "price")]
    [InlineData("""<shop address="ADDR"><product id="a" name="A" price="1.5">x</product></shop>""", "price")]
    [InlineData("""<shop address="ADDR"><product id="a" name="A" price="-5">x</product></shop>""", "price")]
    [InlineData("""<shop address="ADDR"><product id="a" name="A" price="2100000000000001">x</product></shop>""", "price")]
    [InlineData("""<shop address="ADDR"><product id="a" name="A">x</product></shop>""", "price")]
    [InlineData("""<shop address="ADDR"><product id="A B" name="A" price="1">x</product></shop>""", "Product id")]
    [InlineData("""<shop address="ADDR"><product id="a" name="A" price="1">x</product><product id="a" name="B" price="2">y</product></shop>""", "twice")]
    [InlineData("""<shop address="ADDR"><p>not a product</p></shop>""", "only hold")]
    public void BadShops_AreRefused(string shop, string reason)
    {
        var ex = Assert.Throws<KmlException>(() => Parse(shop.Replace("ADDR", Testnet)));
        Assert.Contains(reason, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TooManyProducts_AreRefused()
    {
        var products = string.Concat(Enumerable.Range(0, Kml.MaxProducts + 1).Select(i => $"""<product id="p{i}" name="P" price="1">x</product>"""));
        Assert.Throws<KmlException>(() => Parse($"""<shop address="{Testnet}">{products}</shop>"""));
    }

    [Fact]
    public void AShop_CannotSmuggleScriptsOrStyles()
    {
        Assert.Throws<KmlException>(() => Parse($"""<shop address="{Testnet}" onclick="x()"><product id="a" name="A" price="1">x</product></shop>"""));
        Assert.Throws<KmlException>(() => Parse($"""<shop address="{Testnet}"><product id="a" name="A" price="1" style="x">x</product></shop>"""));
        Assert.Throws<KmlException>(() => Parse($"""<shop address="{Testnet}"><product id="a" name="A" price="1"><script>x</script></product></shop>"""));
    }
}
