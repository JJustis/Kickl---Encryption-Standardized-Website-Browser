using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Kickl.Core.Registry;
using Kickl.Core.Util;

namespace Kickl.Core.News;

/// <summary>
/// One item on the second screen. Site additions come from the Pinwheel registry; RSS items
/// come from the built-in local SecUpgrade publications file and other configured feeds. Nothing
/// here opens automatically: every item is a card.
/// </summary>
public sealed class NewsItem
{
    public string Id { get; set; } = "";
    public string Source { get; set; } = "";
    public string Kind { get; set; } = "site"; // site | rss
    public string Host { get; set; } = "";
    public string Title { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Link { get; set; } = "";
    public string Author { get; set; } = "";
    public string StoryDate { get; set; } = "";
    public string Location { get; set; } = "";
    public string[] Tags { get; set; } = [];
    public long PublishedUtc { get; set; }

    public DateTimeOffset Published =>
        PublishedUtc > 0 ? DateTimeOffset.FromUnixTimeSeconds(PublishedUtc).ToLocalTime() : DateTimeOffset.MinValue;

    public bool IsSiteAddition => Kind is "site" or "story";
    public bool IsStory => string.Equals(Kind, "story", StringComparison.Ordinal);
}

/// <summary>
/// The news service behind screen two. It deliberately has no ranking, no tracking and no
/// recommendation model: newest first, save what you want, everything else falls away.
/// </summary>
public static class NewsFeed
{
    public const int MaxItems = 200;
    public const int MaxSummary = 320;

    private static readonly Regex Markup = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

    /// <summary>Every active site addition in the registry, newest first.</summary>
    public static List<NewsItem> FromRegistry(RegistryFile registry, int max = MaxItems)
    {
        return registry.Records
            .Where(r => !LocalRegistry.IsClosed(r))
            .Select(r => new NewsItem
            {
                Id = r.Name.EndsWith(".rss", StringComparison.OrdinalIgnoreCase)
                    ? $"story:{r.Name.ToLowerInvariant()}:{r.Version}"
                    : "site:" + r.Name.ToLowerInvariant(),
                Source = r.Name.EndsWith(".rss", StringComparison.OrdinalIgnoreCase)
                    ? "Pinwheel feed"
                    : "Pinwheel registry",
                Kind = r.Name.EndsWith(".rss", StringComparison.OrdinalIgnoreCase) ? "story" : "site",
                Host = r.Name,
                Title = string.IsNullOrWhiteSpace(r.Title) ? r.Name : r.Title,
                Summary = r.Headline,
                Link = "jttp://" + r.Name + "/",
                Author = r.StoryAuthor,
                StoryDate = r.StoryDate,
                Location = r.StoryLocation,
                Tags = r.Tags,
                PublishedUtc = r.UpdatedUtc
            })
            .OrderByDescending(i => i.PublishedUtc)
            .ThenBy(i => i.Host, StringComparer.Ordinal)
            .Take(max)
            .ToList();
    }

    /// <summary>
    /// Parse RSS 2.0 or Atom into inert cards. The parser refuses DTDs and never resolves
    /// external entities; feed HTML is stripped to plain text before it reaches the UI.
    /// </summary>
    public static List<NewsItem> ParseRss(string xml, string sourceName)
    {
        if (string.IsNullOrWhiteSpace(xml)) throw new FormatException("The feed is empty.");
        if (xml.Length > 2_000_000) throw new FormatException("The feed is larger than 2 MB.");

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            MaxCharactersInDocument = 2_100_000
        };

        XDocument doc;
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            doc = XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException ex)
        {
            throw new FormatException("Malformed RSS/Atom XML: " + ex.Message, ex);
        }

        var root = doc.Root ?? throw new FormatException("The feed has no root element.");
        var isAtom = string.Equals(root.Name.LocalName, "feed", StringComparison.OrdinalIgnoreCase);
        if (!isAtom && !string.Equals(root.Name.LocalName, "rss", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("Only RSS 2.0 and Atom feeds are supported.");

        var entries = isAtom
            ? root.Elements().Where(e => e.Name.LocalName == "entry")
            : doc.Descendants().Where(e => e.Name.LocalName == "item");
        var feedAuthor = AuthorValue(root);

        var items = new Dictionary<string, NewsItem>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var title = Clean(Value(entry, "title"), 180);
            if (title.Length == 0) continue;

            var link = isAtom ? AtomLink(entry) : Clean(Value(entry, "link"), 2048);
            var entryId = Clean(FirstValue(entry, "id", "guid"), 2048);
            if (link.Length == 0) link = entryId;
            var summary = Clean(FirstValue(entry, "description", "summary", "content", "encoded"), MaxSummary);
            var published = FirstValue(entry, "pubDate", "published", "updated", "date");
            var tags = entry.Elements()
                .Where(e => e.Name.LocalName is "category" or "subject")
                .Select(e => (string?)e.Attribute("term") ?? e.Value)
                .Select(CleanTag)
                .Where(t => t.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .Take(8)
                .ToArray();

            var host = HostOf(link);
            var stable = entryId.Length > 0 ? entryId : link.Length > 0 ? link : title;
            var id = "rss:" + Hex(SHA256.HashData(Encoding.UTF8.GetBytes(sourceName + "|" + stable)))[..24];
            var timestamp = ParseDate(published);
            var author = AuthorValue(entry);
            if (author.Length == 0) author = feedAuthor;

            items[id] = new NewsItem
            {
                Id = id,
                Source = string.IsNullOrWhiteSpace(sourceName) ? host : sourceName,
                Kind = "rss",
                Host = host,
                Title = title,
                Summary = summary,
                Link = link,
                Author = Clean(author, 120),
                Tags = tags,
                PublishedUtc = timestamp
            };
        }

        return items.Values
            .OrderByDescending(i => i.PublishedUtc)
            .ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase)
            .Take(MaxItems)
            .ToList();
    }

    /// <summary>Merge lists by stable id, newest first. Newer copies replace older ones.</summary>
    public static List<NewsItem> Merge(IEnumerable<NewsItem> first, IEnumerable<NewsItem> second,
        int max = MaxItems)
    {
        var byId = new Dictionary<string, NewsItem>(StringComparer.Ordinal);
        foreach (var item in first.Concat(second))
        {
            if (item is null || item.Id.Length == 0) continue;
            byId[item.Id] = item;
        }

        return byId.Values
            .OrderByDescending(i => i.PublishedUtc)
            .ThenBy(i => i.Source, StringComparer.Ordinal)
            .ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .ToList();
    }

    private static string AtomLink(XElement entry)
    {
        var links = entry.Elements().Where(e => e.Name.LocalName == "link").ToList();
        var alternate = links.FirstOrDefault(e =>
            (string?)e.Attribute("rel") is null or "alternate");
        var chosen = alternate ?? links.FirstOrDefault();
        return Clean((string?)chosen?.Attribute("href") ?? "", 2048);
    }

    private static string AuthorValue(XElement parent)
    {
        var author = parent.Elements().FirstOrDefault(e => e.Name.LocalName is "author" or "creator");
        if (author is null) return "";
        return author.Elements().FirstOrDefault(e => e.Name.LocalName == "name")?.Value ?? author.Value;
    }

    private static string Value(XElement parent, string localName) =>
        parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName)?.Value ?? "";

    private static string FirstValue(XElement parent, params string[] names)
    {
        foreach (var name in names)
        {
            var value = Value(parent, name);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return "";
    }

    private static long ParseDate(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return DateTimeOffset.TryParse(value.Trim(), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed.ToUnixTimeSeconds()
            : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    private static string Clean(string value, int max)
    {
        var text = WebUtility.HtmlDecode(Markup.Replace(value ?? "", " "));
        text = Spaces.Replace(text, " ").Trim();
        return text.Length <= max ? text : text[..max].TrimEnd() + "…";
    }

    private static string CleanTag(string value)
    {
        var tag = Spaces.Replace((value ?? "").Trim().ToLowerInvariant(), "-");
        tag = new string(tag.Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').ToArray()).Trim('-');
        return tag.Length is >= 2 and <= 24 ? tag : "";
    }

    private static string HostOf(string link) =>
        Uri.TryCreate(link, UriKind.Absolute, out var uri) ? uri.Host.ToLowerInvariant() : "";

    private static string Hex(ReadOnlySpan<byte> data) => Convert.ToHexString(data).ToLowerInvariant();
}
