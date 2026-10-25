using System.Security.Cryptography;
using Kickl.Core.Crypto;
using Kickl.Core.Documents;
using Kickl.Core.News;
using Kickl.Core.Registry;
using Kickl.Core.Relay;
using Kickl.Core.Session;
using Kickl.Core.Util;

namespace Kickl.Core.Tests;

public class DirectFetchTests
{
    private const string Host = "pinwheelexplained.com";

    [Fact]
    public void OriginDeliversThePackageThroughTheRsaAesTunnel()
    {
        var temp = new TempDir();
        var paths = new DataPaths(temp.Path);
        Keystore.Create(paths, "pin");
        var master = Keystore.Unlock(paths, "pin");
        try
        {
            var doc = DocumentStore.Publish(paths, Fixture.SiteDir, Host, master);
            var record = LocalRegistry.Resolve(LocalRegistry.LoadOrEmpty(paths), Host)!;
            Assert.NotEmpty(record.OriginDeviceId);
            Assert.NotEmpty(record.DirectPublicKey);

            using var publisher = DirectPublisher.Open(paths, master);
            var peer = new DirectPeer(record.OriginDeviceId, B64u.Decode(record.DirectPublicKey));
            using var exchange = DirectFetchClient.Begin(peer, Host);
            var response = publisher.Respond(exchange.Request);
            var result = exchange.Complete(response);

            Assert.Equal(doc.Integrity, result.Document.Integrity);
            Assert.Equal(Host, result.Document.Site);
            Assert.Contains("RSA-OAEP", result.Transport);
            Assert.NotEmpty(result.Fingerprint);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(master);
        }
    }

    [Fact]
    public void TamperedTunnelResponseFailsClosed()
    {
        var temp = new TempDir();
        var paths = new DataPaths(temp.Path);
        Keystore.Create(paths, "pin");
        var master = Keystore.Unlock(paths, "pin");
        try
        {
            DocumentStore.Publish(paths, Fixture.SiteDir, Host, master);
            var record = LocalRegistry.Resolve(LocalRegistry.LoadOrEmpty(paths), Host)!;
            using var publisher = DirectPublisher.Open(paths, master);
            using var exchange = DirectFetchClient.Begin(
                new DirectPeer(record.OriginDeviceId, B64u.Decode(record.DirectPublicKey)), Host);
            var response = publisher.Respond(exchange.Request);
            response.Ciphertext[0] ^= 1;

            Assert.Throws<DirectFetchException>(() => exchange.Complete(response));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(master);
        }
    }

    [Fact]
    public void BrowserSessionReportsTheDirectTransport()
    {
        var temp = new TempDir();
        var paths = new DataPaths(temp.Path);
        Keystore.Create(paths, "pin");
        var master = Keystore.Unlock(paths, "pin");
        DocumentStore.Publish(paths, Fixture.SiteDir, Host, master);
        CryptographicOperations.ZeroMemory(master);

        using var session = new BrowserSession(temp.Path);
        session.Unlock("pin");
        var page = session.Open("jttp://" + Host + "/");

        Assert.Equal(Host, page.Site);
        Assert.StartsWith("direct origin device", session.LastTransport);
        Assert.True(session.LastTransportFingerprint?.Length >= 12);
    }
}

public class SiteComposerTests
{
    [Fact]
    public void RssTemplatePublishesAStoryWithMetadataAndAFeedId()
    {
        using var temp = new TempDir();
        var paths = new DataPaths(temp.Path);
        Keystore.Create(paths, "pin");
        var master = Keystore.Unlock(paths, "pin");
        try
        {
            var draft = new StoryDraft
            {
                Host = "weekly.rss",
                Headline = "The first weekly story",
                Author = "Ada",
                Date = "2026-10-24",
                Location = "The mesh",
                Body = "## A beginning\n\nA story that travels from the origin device.",
                Tags = ["news", "test"]
            };

            var doc = SiteComposer.Publish(paths, draft, master);
            var record = LocalRegistry.Resolve(LocalRegistry.LoadOrEmpty(paths), "weekly.rss")!;

            Assert.Equal("The first weekly story", record.Title);
            Assert.Equal("Ada", record.StoryAuthor);
            Assert.Equal("The mesh", record.StoryLocation);
            Assert.Contains("rss", record.Tags);
            Assert.Contains("news", record.Tags);
            Assert.Equal(doc.Integrity, record.ContentHash);

            var item = Assert.Single(NewsFeed.FromRegistry(LocalRegistry.LoadOrEmpty(paths)));
            Assert.True(item.IsStory);
            Assert.Equal("story:weekly.rss:1", item.Id);
            Assert.Equal("Ada", item.Author);
            Assert.Equal("The mesh", item.Location);

            var page = Kickl.Core.Markup.Kml.Parse(doc.Markup, "weekly.rss");
            Assert.Equal("Ada", page.Author);
            Assert.Equal("2026-10-24", page.Published);
            Assert.Equal("The mesh", page.Location);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(master);
        }
    }

    [Fact]
    public void NextStoryIncrementsTheFeedVersion()
    {
        using var temp = new TempDir();
        var paths = new DataPaths(temp.Path);
        Keystore.Create(paths, "pin");
        var master = Keystore.Unlock(paths, "pin");
        try
        {
            SiteComposer.Publish(paths, new StoryDraft
            {
                Host = "daily.rss", Headline = "One", Author = "A", Body = "First."
            }, master);
            SiteComposer.Publish(paths, new StoryDraft
            {
                Host = "daily.rss", Headline = "Two", Author = "A", Body = "Second."
            }, master);

            var record = LocalRegistry.Resolve(LocalRegistry.LoadOrEmpty(paths), "daily.rss")!;
            Assert.Equal(2, record.Version);
            Assert.Equal("Two", record.Title);
            var item = Assert.Single(NewsFeed.FromRegistry(LocalRegistry.LoadOrEmpty(paths)));
            Assert.Equal("story:daily.rss:2", item.Id);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(master);
        }
    }

    [Fact]
    public void HeadlineAndBodyAreRequired()
    {
        using var temp = new TempDir();
        var paths = new DataPaths(temp.Path);
        var master = RandomNumberGenerator.GetBytes(32);

        Assert.Throws<Kickl.Core.Markup.KmlException>(() =>
            SiteComposer.Publish(paths, new StoryDraft { Host = "x.rss", Body = "body" }, master));
        Assert.Throws<Kickl.Core.Markup.KmlException>(() =>
            SiteComposer.Publish(paths, new StoryDraft { Host = "x.rss", Headline = "h" }, master));
    }
}
