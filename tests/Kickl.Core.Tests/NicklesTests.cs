using System.Security.Cryptography;
using Kickl.Core.Ads;
using Kickl.Core.Crypto;
using Kickl.Core.Documents;
using Kickl.Core.Markup;
using Kickl.Core.Util;

namespace Kickl.Core.Tests;

public class NicklesTests
{
    [Fact]
    public void PublisherSnippetIsMarriedToTheSiteAndSealsAnAdBlock()
    {
        using var temp = new TempDir();
        var paths = new DataPaths(temp.Path);
        Keystore.Create(paths, "pin");
        var master = Keystore.Unlock(paths, "pin");
        try
        {
            var publisher = Nickles.CreatePublisher(paths, master, "ad-lab.custom", Nickles.DefaultSpace);
            var snippet = Nickles.Snippet(publisher);

            Assert.Contains("adnetworking/nickles.js", snippet);
            Assert.Contains(publisher.Id, snippet);
            Assert.Contains("data-nickles-site=\"ad-lab.custom\"", snippet);

            var doc = SiteComposer.Publish(paths, new StoryDraft
            {
                Host = "ad-lab.custom",
                Headline = "Ad lab",
                Author = "demo",
                Html = "<p>Before the slot.</p>\n" + snippet + "\n<p>After the slot.</p>",
                Tags = ["demo", "ads"]
            }, master);

            Assert.DoesNotContain("<script", doc.Markup, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("kickl-ad-slot", doc.Markup, StringComparison.Ordinal);
            var page = Kml.Parse(doc.Markup, "ad-lab.custom");
            var ad = Assert.Single(page.Blocks, b => b.Kind == BlockKind.Ad);
            var adIndex = page.Blocks.IndexOf(ad);
            Assert.True(adIndex > 0);
            Assert.Equal(BlockKind.Html, page.Blocks[adIndex - 1].Kind);
            Assert.Equal(BlockKind.Html, page.Blocks[adIndex + 1].Kind);
            Assert.Equal(publisher.Id, ad.Publisher);
            Assert.Equal("ad-lab.custom", ad.Site);
            Assert.Equal(Nickles.DefaultSpace, ad.Space);
            Assert.Equal(publisher.Token, ad.Token);

            var slot = Nickles.ResolveSlot(paths, master,
                new NicklesPlacement(ad.Publisher!, ad.Site!, ad.Space!, ad.Token!),
                DateTimeOffset.FromUnixTimeSeconds(1_800_000_000));
            Assert.Equal(1_800_000_000 / 600, slot.Bucket);
            Assert.Equal(slot.Creative, Nickles.ResolveSlot(paths, master,
                new NicklesPlacement(ad.Publisher!, ad.Site!, ad.Space!, ad.Token!),
                DateTimeOffset.FromUnixTimeSeconds(1_800_000_000)).Creative);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(master);
        }
    }

    [Fact]
    public void PublisherSnippetCopiedToAnotherSiteFailsClosed()
    {
        using var temp = new TempDir();
        var paths = new DataPaths(temp.Path);
        Keystore.Create(paths, "pin");
        var master = Keystore.Unlock(paths, "pin");
        try
        {
            var publisher = Nickles.CreatePublisher(paths, master, "ad-lab.custom", Nickles.DefaultSpace);
            Assert.Throws<KmlException>(() => SiteComposer.Publish(paths, new StoryDraft
            {
                Host = "other.custom",
                Headline = "Other",
                Author = "demo",
                Html = "<p>Wrong site.</p>\n" + Nickles.Snippet(publisher),
                Tags = ["demo", "ads"]
            }, master));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(master);
        }
    }

    [Fact]
    public void LoaderFileIsAManifestNotAScript()
    {
        using var temp = new TempDir();
        var paths = new DataPaths(temp.Path);
        Keystore.Create(paths, "pin");
        var master = Keystore.Unlock(paths, "pin");
        try
        {
            var publisher = Nickles.CreatePublisher(paths, master, "ad-lab.custom", "site.inline");
            var js = Nickles.LoaderJs(publisher);

            Assert.Contains("window.KicklNickles", js);
            Assert.Contains(publisher.Id, js);
            Assert.Contains(publisher.Token, js);
            Assert.Contains("Object.freeze", js);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(master);
        }
    }

    [Fact]
    public void IncompleteAdBlocksAreRejected()
    {
        Assert.Throws<KmlException>(() => Kml.Parse(
            "<kml v=\"1\" site=\"x.custom\"><head><title>x</title></head><body><page><ad publisher=\"p\" site=\"x.custom\" space=\"site.inline\"/></page></body></kml>",
            "x.custom"));
    }
}
