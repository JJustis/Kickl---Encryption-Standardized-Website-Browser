using System.Security.Cryptography;
using Kickl.Core.Crypto;
using Kickl.Core.Documents;
using Kickl.Core.Markup;
using Kickl.Core.Registry;
using Kickl.Core.Util;

namespace Kickl.Core.Tests;

public class StarterTests
{
    [Fact]
    public void EveryNewNameGetsACompleteEditableExamplePage()
    {
        var example = SiteStarter.Example("new-lab.custom");
        var safe = InlineHtml.Sanitize(example);

        Assert.Contains("new-lab.custom", safe);
        Assert.Contains("<svg", safe);
        Assert.Contains("kickl-blob:", safe);
        Assert.Contains("Change this drawing", safe);
        Assert.Contains("<p>", safe);
    }

    [Fact]
    public void TheExamplePagePublishesAsASealedSite()
    {
        using var temp = new TempDir();
        var paths = new DataPaths(temp.Path);
        Keystore.Create(paths, "pin");
        var master = Keystore.Unlock(paths, "pin");
        try
        {
            var doc = SiteComposer.Publish(paths, new StoryDraft
            {
                Host = "starter.custom",
                Headline = "Welcome to starter.custom",
                Author = "you",
                Html = SiteStarter.Example("starter.custom"),
                Tags = ["new", "site"]
            }, master);

            var page = Kml.Parse(doc.Markup, "starter.custom");
            var html = Assert.Single(page.Blocks, b => b.Kind == BlockKind.Html);
            Assert.Contains("starter.custom", html.Html);
            Assert.DoesNotContain("src=\"data:image", doc.Markup, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("href=\"data:image", doc.Markup, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(master);
        }
    }

    [Fact]
    public void RssXmlTemplateParsesIntoTheFixedStoryFields()
    {
        var xml = RssTemplate.Example("weekly.rss", "2026-10-24");
        var draft = RssTemplate.Parse("weekly.rss", xml);

        Assert.Equal("weekly.rss", draft.Host);
        Assert.Equal("Headline goes here", draft.Headline);
        Assert.Equal("unnamed", draft.Author);
        Assert.Equal("2026-10-24", draft.Date);
        Assert.Equal("The mesh", draft.Location);
        Assert.Contains("The story", draft.Body);
        Assert.Contains("rss", draft.Tags);
        Assert.Contains("story", draft.Tags);
        Assert.Null(draft.ImagePath);
        Assert.Equal(xml, draft.RssXml);
    }

    [Fact]
    public void RssXmlPublishesThroughTheSealedRegistryPath()
    {
        using var temp = new TempDir();
        var paths = new DataPaths(temp.Path);
        Keystore.Create(paths, "pin");
        var master = Keystore.Unlock(paths, "pin");
        try
        {
            var xml = RssTemplate.Example("daily.rss", "2026-10-24");
            var doc = SiteComposer.Publish(paths, new StoryDraft
            {
                Host = "daily.rss",
                RssXml = xml
            }, master);

            var record = LocalRegistry.Resolve(LocalRegistry.LoadOrEmpty(paths), "daily.rss")!;
            Assert.Equal("Headline goes here", record.Headline);
            Assert.Contains("rss", record.Tags);

            var page = Kml.Parse(doc.Markup, "daily.rss");
            Assert.Equal("Headline goes here", page.Title);
            Assert.DoesNotContain(page.Blocks, b => b.Kind == BlockKind.Html);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(master);
        }
    }

    [Fact]
    public void RssXmlStaysXmlAndRejectsInlineVisuals()
    {
        Assert.Throws<KmlException>(() => RssTemplate.Parse("x.rss", "<rss><channel></channel></rss>"));
        Assert.Throws<KmlException>(() => RssTemplate.Parse("x.rss", "<rss><channel><item>"));
        Assert.Throws<KmlException>(() => RssTemplate.Parse("x.custom",
            RssTemplate.Example("x.rss", "2026-10-24")));

        using var temp = new TempDir();
        var paths = new DataPaths(temp.Path);
        var master = RandomNumberGenerator.GetBytes(32);
        Assert.Throws<KmlException>(() => SiteComposer.Publish(paths, new StoryDraft
        {
            Host = "x.custom",
            RssXml = RssTemplate.Example("x.rss", "2026-10-24")
        }, master));
    }
}
