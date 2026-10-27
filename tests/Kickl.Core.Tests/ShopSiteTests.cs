using System.Security.Cryptography;
using Kickl.Core.Documents;
using Kickl.Core.Markup;
using Kickl.Core.Session;
using Kickl.Core.Util;
using Xunit;

namespace Kickl.Core.Tests;

/// <summary>The example shop at jttp://btcshop.com, and the shop syntax in the site editor.</summary>
public class ShopSiteTests
{
    private const string Host = "btcshop.com";
    private const string DemoKey = "demo-paid";
    private static string SiteDir => Path.Combine(Fixture.RepoRoot, "sites", Host);

    private static BrowserSession PublishAndOpen(string root)
    {
        var owner = new DataPaths(root, "seller");
        Crypto.Keystore.Create(owner, "pin-s");
        var master = Crypto.Keystore.Unlock(owner, "pin-s");
        DocumentStore.Publish(owner, SiteDir, Host, master, null, DemoKey);
        CryptographicOperations.ZeroMemory(master);

        var reader = new BrowserSession(root, "buyer");
        reader.CreateProfile("pin-b");
        reader.Open("jttp://" + Host + "/", "pin-b");
        return reader;
    }

    [Fact]
    public void TheExampleShopPublishesAndListsItsProducts()
    {
        using var temp = new TempDir();
        using var reader = PublishAndOpen(temp.Path);
        var page = reader.Open("jttp://" + Host + "/");

        var shop = Assert.Single(page.Blocks, b => b.Kind == BlockKind.Shop);
        Assert.True(shop.Testnet);                                    // a demo must never ask for real money
        Assert.True(BitcoinAddress.IsValid(shop.Address));
        Assert.Equal(["pathforge-pro", "surviving-day", "ad-upload", "site-claim"], shop.Products.Select(p => p.Id));
        // the same prices as the BTC shop catalogue, in satoshis
        Assert.Equal([2418L, 12090L, 3000L, 10000L], shop.Products.Select(p => p.PriceSats));
    }

    [Fact]
    public void TheProductFileIsSealedUntilTheSellersKeyIsGiven()
    {
        using var temp = new TempDir();
        using var reader = PublishAndOpen(temp.Path);
        var page = reader.Open("jttp://" + Host + "/");
        var blob = Assert.Single(page.Blocks, b => b.Kind == BlockKind.Blob);
        Assert.Equal("pathforge-pro", blob.Id);
        Assert.True(blob.Protected);

        Assert.Throws<AccessKeyRequiredException>(() => reader.UnlockBlob("pathforge-pro"));
        Assert.ThrowsAny<CryptographicException>(() => reader.UnlockBlob("pathforge-pro", accessKey: "not-the-key"));
        Assert.Contains("PATHFORGE PRO", reader.UnlockBlob("pathforge-pro", accessKey: DemoKey));
    }

    [Fact]
    public void TheTextViewShowsTheShopAndItsTestnetWarning()
    {
        using var temp = new TempDir();
        using var reader = PublishAndOpen(temp.Path);
        var page = reader.Open("jttp://" + Host + "/");
        var text = page.Render("jttp://" + Host + "/", true, new HashSet<string>(), new Dictionary<string, string>());
        Assert.Contains("TESTNET, no real money", text);
        Assert.Contains("2,418 sats (0.00002418 BTC)", text);
    }

    // ---------------------------------------------------------------- the site editor

    private static StoryDraft ShopDraft(string html) => new()
    {
        Host = "mystore.com", Headline = "My store", Author = "me", Date = "2026-10-27", Html = html, Tags = ["shop", "demo"]
    };

    [Fact]
    public void TheEditorsStarterShopPublishesAsAShop()
    {
        using var temp = new TempDir();
        using var session = new BrowserSession(temp.Path, "seller");
        session.CreateProfile("pin-s");
        session.Unlock("pin-s");

        session.CreateSite(ShopDraft(SiteStarter.ShopExample("mystore.com")));
        var page = session.Open("jttp://mystore.com/");
        var shop = Assert.Single(page.Blocks, b => b.Kind == BlockKind.Shop);
        Assert.Equal(2, shop.Products.Count);
        Assert.True(shop.Testnet);
    }

    [Fact]
    public void TheEditorRefusesAMistypedAddress_BeforeAnythingIsPublished()
    {
        using var temp = new TempDir();
        using var session = new BrowserSession(temp.Path, "seller");
        session.CreateProfile("pin-s");
        session.Unlock("pin-s");

        var typo = SiteStarter.ShopExample("mystore.com").Replace("kxpjzsx", "kxpjzsy");
        var ex = Assert.Throws<KmlException>(() => session.CreateSite(ShopDraft(typo)));
        Assert.Contains("valid Bitcoin address", ex.Message);
    }

    [Fact]
    public void TheEditorKeepsInlineHtmlWorking_AndRefusesFileProductsAndScripts()
    {
        using var temp = new TempDir();
        using var session = new BrowserSession(temp.Path, "seller");
        session.CreateProfile("pin-s");
        session.Unlock("pin-s");

        // an ordinary inline page is untouched by the shop path
        session.CreateSite(ShopDraft("<div><p>just a page</p></div>"));
        Assert.DoesNotContain(session.Open("jttp://mystore.com/").Blocks, b => b.Kind == BlockKind.Shop);

        // a file needs a site folder; the editor says so instead of publishing a product whose file does not exist
        var withFile = SiteStarter.ShopExample("mystore.com").Replace("price=\"1500\"", "price=\"1500\" blob=\"nothing\"");
        Assert.Throws<KmlException>(() => session.CreateSite(ShopDraft(withFile)));

        // a script smuggled into the box is not a valid shop
        Assert.Throws<KmlException>(() => session.CreateSite(ShopDraft(
            SiteStarter.ShopExample("mystore.com").Replace("<product", "<script>x</script><product"))));
    }
}
