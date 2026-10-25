using System.Security.Cryptography;
using System.Xml.Linq;
using Kickl.Core.Ads;
using Kickl.Core.Documents;
using Kickl.Core.Markup;
using Kickl.Core.Names;
using Kickl.Core.Registry;
using Kickl.Core.Util;

namespace Kickl.Core.Documents;

/// <summary>The fields behind the jttp://name.rss story template in the browser space.</summary>
public sealed class StoryDraft
{
    public string Host { get; set; } = "";
    public string Headline { get; set; } = "";
    public string Author { get; set; } = "";
    public string Date { get; set; } = "";
    public string Location { get; set; } = "";
    public string Body { get; set; } = "";
    public string? Html { get; set; }
    /// <summary>The raw RSS XML template for a .rss story. When present it is the source of the fields.</summary>
    public string? RssXml { get; set; }
    public string[] Tags { get; set; } = [];
    public string? ImagePath { get; set; }

    public bool IsRss => Host.EndsWith(".rss", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Builds a site or the next feed story from the template fields, writes it to the local draft
/// area, seals it, and publishes it through the same registry path as every other site.
/// </summary>
public static class SiteComposer
{
    public const string ImageBlobId = "image";
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg"];

    public static SealedDocument Publish(DataPaths paths, StoryDraft draft, byte[] master)
    {
        if (!JttpUrl.TryParse("jttp://" + draft.Host + "/", out var url, out var error))
            throw new KmlException(error ?? "Invalid jttp name.");
        if (!string.IsNullOrWhiteSpace(draft.RssXml))
        {
            if (!draft.IsRss)
                throw new KmlException("RSS XML can only be used with a .rss name.");
            draft = RssTemplate.Parse(draft.Host, draft.RssXml);
        }
        if (string.IsNullOrWhiteSpace(draft.Headline))
            throw new KmlException("A headline is required.");
        if (draft.IsRss && string.IsNullOrWhiteSpace(draft.Body))
            throw new KmlException("An RSS story requires the story field.");
        if (!draft.IsRss && string.IsNullOrWhiteSpace(draft.Body) && string.IsNullOrWhiteSpace(draft.Html))
            throw new KmlException("Add inline HTML/SVG or a text body.");
        if (draft.IsRss && !string.IsNullOrWhiteSpace(draft.Html))
            throw new KmlException("RSS stories follow the story template; inline HTML/SVG is for .com and custom names.");

        var host = url!.Host;
        var tags = draft.Tags
            .Select(t => (t ?? "").Trim().ToLowerInvariant())
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (draft.IsRss && !tags.Contains("rss", StringComparer.Ordinal))
            tags.Insert(0, "rss");
        var pageTitle = draft.Headline.Trim();
        Phonebook.ValidateMetadata(pageTitle, tags);

        var pending = new Dictionary<string, PendingBlob>(StringComparer.Ordinal);
        var owned = new List<byte[]>();
        try
        {
            var ad = Nickles.ValidateSnippet(draft.Html, host, paths, master);
            string? inlineHtml = null;
            if (!string.IsNullOrWhiteSpace(draft.Html))
            {
                var source = ad is null ? draft.Html : Nickles.MarkSlot(draft.Html, ad.Space);
                var safe = InlineHtml.Sanitize(source);
                var (rewritten, images) = InlineHtml.ExtractDataImages(safe);
                inlineHtml = rewritten;
                foreach (var image in images)
                {
                    if (pending.ContainsKey(image.Id))
                    {
                        CryptographicOperations.ZeroMemory(image.Bytes);
                        continue;
                    }
                    pending[image.Id] = new PendingBlob(image.Mime, image.Bytes);
                    owned.Add(image.Bytes);
                }
            }

            var hasImage = false;
            if (!string.IsNullOrWhiteSpace(draft.ImagePath))
            {
                var source = Path.GetFullPath(draft.ImagePath);
                if (!File.Exists(source)) throw new FileNotFoundException("Story image not found.", source);
                var ext = Path.GetExtension(source).ToLowerInvariant();
                if (!ImageExtensions.Contains(ext))
                    throw new KmlException("Story image must be PNG, JPEG, GIF, WebP, or SVG.");
                var bytes = File.ReadAllBytes(source);
                pending[ImageBlobId] = new PendingBlob(DocumentStore.MimeFor(ext), bytes);
                owned.Add(bytes);
                hasImage = true;
            }

            var kml = BuildKml(host, draft, tags, hasImage, inlineHtml, ad);
            return DocumentStore.PublishMarkup(paths, host, kml, master, pending);
        }
        finally
        {
            foreach (var bytes in owned)
                CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public static string BuildKml(string host, StoryDraft draft, IReadOnlyList<string> tags,
        bool hasImage, string? inlineHtml = null, NicklesPlacement? ad = null)
    {
        var date = string.IsNullOrWhiteSpace(draft.Date)
            ? DateTimeOffset.Now.ToString("yyyy-MM-dd")
            : draft.Date.Trim();
        var author = string.IsNullOrWhiteSpace(draft.Author) ? "unnamed" : draft.Author.Trim();
        var location = draft.Location.Trim();
        var headline = draft.Headline.Trim();

        var byline = string.Join(" · ", new[] { author, date, location }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        var hasBody = !string.IsNullOrWhiteSpace(draft.Body);
        var hasInlineHtml = !string.IsNullOrWhiteSpace(inlineHtml);
        var page = new XElement("page");

        // RSS has a fixed template. A .com or custom name is free to be just its inline
        // SVG/HTML page; the browser chrome still shows the registered title and byline.
        if (draft.IsRss || !hasInlineHtml)
        {
            page.Add(new XElement("h1", headline),
                new XElement("badge", draft.IsRss
                    ? "feed story · sealed · next story"
                    : "sealed site · created in browser"),
                new XElement("p", byline));
        }

        if (hasImage)
            page.Add(new XElement("img", new XAttribute("ref", ImageBlobId), new XAttribute("alt", headline)));

        if (hasInlineHtml)
        {
            var marker = inlineHtml!.IndexOf(Nickles.SlotMarker, StringComparison.Ordinal);
            if (ad is not null && marker >= 0)
            {
                var before = inlineHtml[..marker].Trim();
                var after = inlineHtml[(marker + Nickles.SlotMarker.Length)..].Trim();
                if (before.Length > 0)
                    page.Add(new XElement("html", before));
                page.Add(new XElement("ad",
                    new XAttribute("publisher", ad.PublisherId),
                    new XAttribute("site", ad.Site),
                    new XAttribute("space", ad.Space),
                    new XAttribute("token", ad.Token)));
                if (after.Length > 0)
                    page.Add(new XElement("html", after));
            }
            else
            {
                page.Add(new XElement("html", inlineHtml.Trim()));
                if (ad is not null)
                    page.Add(new XElement("ad",
                        new XAttribute("publisher", ad.PublisherId),
                        new XAttribute("site", ad.Site),
                        new XAttribute("space", ad.Space),
                        new XAttribute("token", ad.Token)));
            }
        }
        else if (ad is not null)
        {
            page.Add(new XElement("ad",
                new XAttribute("publisher", ad.PublisherId),
                new XAttribute("site", ad.Site),
                new XAttribute("space", ad.Space),
                new XAttribute("token", ad.Token)));
        }

        if (hasBody)
            AppendStory(page, draft.Body);

        var head = new XElement("head",
            new XElement("title", headline),
            new XElement("meta", new XAttribute("name", "description"), new XAttribute("content", headline)),
            new XElement("meta", new XAttribute("name", "headline"), new XAttribute("content", headline)),
            new XElement("meta", new XAttribute("name", "author"), new XAttribute("content", author)),
            new XElement("meta", new XAttribute("name", "date"), new XAttribute("content", date)),
            new XElement("meta", new XAttribute("name", "location"), new XAttribute("content", location)),
            new XElement("meta", new XAttribute("name", "tags"), new XAttribute("content", string.Join(", ", tags))));

        var root = new XElement("kml",
            new XAttribute("v", "1"),
            new XAttribute("site", host),
            new XAttribute("epoch", "1"),
            head,
            new XElement("body", page));

        return new XDocument(new XDeclaration("1.0", "utf-8", null), root).ToString();
    }

    private static void AppendStory(XElement page, string body)
    {
        var paragraph = new List<string>();

        void Flush()
        {
            if (paragraph.Count == 0) return;
            page.Add(new XElement("p", string.Join(" ", paragraph)));
            paragraph.Clear();
        }

        foreach (var raw in body.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (string.IsNullOrWhiteSpace(line))
            {
                Flush();
                continue;
            }

            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                Flush();
                page.Add(new XElement("h3", line[4..].Trim()));
            }
            else if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                Flush();
                page.Add(new XElement("h2", line[3..].Trim()));
            }
            else if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                Flush();
                page.Add(new XElement("h1", line[2..].Trim()));
            }
            else if (line.StartsWith("> ", StringComparison.Ordinal))
            {
                Flush();
                page.Add(new XElement("quote", line[2..].Trim()));
            }
            else if (line.Trim() == "---")
            {
                Flush();
                page.Add(new XElement("hr"));
            }
            else if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                Flush();
                page.Add(new XElement("p", "• " + line[2..].Trim()));
            }
            else
            {
                paragraph.Add(line.Trim());
            }
        }

        Flush();
    }
}
