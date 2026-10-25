using Kickl.Core.News;
using Kickl.Core.Registry;

namespace Kickl.Core.Tests;

public class NewsFeedTests
{
    private const string Rss = """
        <?xml version="1.0" encoding="utf-8"?>
        <rss version="2.0"><channel>
          <title>Test wire</title>
          <item>
            <title>First &amp; best</title>
            <link>https://news.example/first</link>
            <description><![CDATA[<p>Hello <b>world</b>.</p>]]></description>
            <pubDate>Mon, 20 Oct 2025 10:00:00 GMT</pubDate>
            <category>Privacy</category>
            <category>security</category>
            <guid>first-guid</guid>
          </item>
          <item>
            <title>Second</title>
            <link>https://news.example/second</link>
            <description>Plain text.</description>
            <pubDate>Tue, 21 Oct 2025 10:00:00 GMT</pubDate>
          </item>
        </channel></rss>
        """;

    private const string Atom = """
        <?xml version="1.0" encoding="utf-8"?>
        <feed xmlns="http://www.w3.org/2005/Atom">
          <title>Atom wire</title>
          <entry>
            <title>An atom entry</title>
            <link rel="alternate" href="https://atom.example/post"/>
            <summary>Atom summary</summary>
            <published>2025-10-22T10:00:00Z</published>
            <category term="Mesh"/>
          </entry>
        </feed>
        """;

    private const string SecupgradeShape = """
        <?xml version="1.0" encoding="utf-8"?>
        <rss version="2.0" xmlns:dc="http://purl.org/dc/elements/1.1/">
          <channel>
            <title>SecUpgrade Publications</title>
            <item>
              <title>First publication</title>
              <link>https://secupgrade.com/publications/first_article.html</link>
              <guid isPermaLink="true">https://secupgrade.com/publications/first_article.html</guid>
              <pubDate>Fri, 16 Oct 2026 22:18:52 +0200</pubDate>
              <dc:creator>JJustis</dc:creator>
              <description>Latest article description</description>
            </item>
            <item>
              <title>Second publication</title>
              <link>https://secupgrade.com/publications/second_article.html</link>
              <guid isPermaLink="true">https://secupgrade.com/publications/second_article.html</guid>
              <pubDate>Thu, 15 Oct 2026 22:18:52 +0200</pubDate>
              <dc:creator>JJustis</dc:creator>
              <description>Another article</description>
            </item>
          </channel>
        </rss>
        """;

    [Fact]
    public void RegistryAdditionsAreNewestFirstAndClosedSitesDisappear()
    {
        var registry = new RegistryFile
        {
            Records =
            [
                new RegistryRecord
                {
                    Name = "old.custom", Title = "Old", Headline = "Old news",
                    Tags = ["old"], UpdatedUtc = 100, Status = "active"
                },
                new RegistryRecord
                {
                    Name = "new.custom", Title = "New", Headline = "New news",
                    Tags = ["new"], UpdatedUtc = 200, Status = "active"
                },
                new RegistryRecord
                {
                    Name = "closed.custom", Title = "Closed", Headline = "Gone",
                    Tags = ["closed"], UpdatedUtc = 300, Status = "closed"
                }
            ]
        };

        var items = NewsFeed.FromRegistry(registry);

        Assert.Equal(2, items.Count);
        Assert.Equal("new.custom", items[0].Host);
        Assert.Equal("jttp://new.custom/", items[0].Link);
        Assert.Equal("site:new.custom", items[0].Id);
        Assert.DoesNotContain(items, i => i.Host == "closed.custom");
    }

    [Fact]
    public void RssIsSanitisedAndOrdered()
    {
        var items = NewsFeed.ParseRss(Rss, "Test wire");

        Assert.Equal(2, items.Count);
        Assert.Equal("Second", items[0].Title);
        Assert.Equal("First & best", items[1].Title);
        Assert.Equal("Hello world .", items[1].Summary);
        Assert.Equal("news.example", items[1].Host);
        Assert.Contains("privacy", items[1].Tags);
        Assert.Contains("security", items[1].Tags);
        Assert.Equal(2, items[1].Tags.Length);
    }

    [Fact]
    public void AtomIsSupported()
    {
        var item = Assert.Single(NewsFeed.ParseRss(Atom, "Atom wire"));

        Assert.Equal("An atom entry", item.Title);
        Assert.Equal("https://atom.example/post", item.Link);
        Assert.Equal("Atom summary", item.Summary);
        Assert.Equal("atom.example", item.Host);
        Assert.Contains("mesh", item.Tags);
    }

    [Fact]
    public void SecupgradeFeedFormatKeepsGuidAndDcCreator()
    {
        var items = NewsFeed.ParseRss(SecupgradeShape, "secupgrade.com");

        Assert.Equal(2, items.Count);
        Assert.Equal("First publication", items[0].Title);
        Assert.Equal("JJustis", items[0].Author);
        Assert.StartsWith("rss:", items[0].Id);
        Assert.Equal("secupgrade.com", items[0].Host);
    }

    [Fact]
    public void MergeDeduplicatesAndKeepsTheNewestCopy()
    {
        var first = new NewsItem { Id = "a", Title = "old", PublishedUtc = 10 };
        var second = new NewsItem { Id = "a", Title = "new", PublishedUtc = 20 };
        var other = new NewsItem { Id = "b", Title = "other", PublishedUtc = 15 };

        var merged = NewsFeed.Merge([first, other], [second]);

        Assert.Equal(2, merged.Count);
        Assert.Equal("new", merged[0].Title);
        Assert.Equal("other", merged[1].Title);
    }

    [Fact]
    public void RejectsDtds()
    {
        const string evil = """
            <?xml version="1.0"?>
            <!DOCTYPE rss [<!ENTITY xxe SYSTEM "file:///c:/windows/win.ini">]>
            <rss version="2.0"><channel><item><title>&xxe;</title></channel></rss>
            """;

        Assert.Throws<FormatException>(() => NewsFeed.ParseRss(evil, "bad"));
    }
}

public class SavedItemsStoreTests
{
    [Fact]
    public void SavedItemsPersistAndToggle()
    {
        using var temp = new TempDir();
        using var session = new Kickl.Core.Session.BrowserSession(temp.Path);
        session.CreateProfile("saved-items test key");
        session.Unlock("saved-items test key");
        var store = new SavedItemsStore(temp.Path, session);
        var item = new NewsItem
        {
            Id = "site:pinwheelexplained.com",
            Kind = "site",
            Host = "pinwheelexplained.com",
            Title = "Pinwheel, explained",
            Link = "jttp://pinwheelexplained.com/",
            PublishedUtc = 123
        };

        store.Save(item);
        Assert.True(store.Contains(item.Id));
        Assert.Single(new SavedItemsStore(temp.Path, session).Load());

        store.Toggle(item);
        Assert.False(store.Contains(item.Id));
        Assert.Empty(new SavedItemsStore(temp.Path, session).Load());
    }
}
