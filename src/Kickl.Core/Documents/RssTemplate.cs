using System.Xml;
using System.Xml.Linq;
using Kickl.Core.Markup;

namespace Kickl.Core.Documents;

/// <summary>
/// The XML authoring surface for a <c>jttp://name.rss</c> story. The person fills in one
/// <c>&lt;item&gt;</c>; the browser turns it into the fixed story template. RSS does not
/// accept inline SVG/HTML — the story is text plus an optional sealed image attachment.
/// </summary>
public static class RssTemplate
{
    public static string Example(string host, string date)
    {
        var safe = host.Trim().ToLowerInvariant();
        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <rss version="2.0">
              <channel>
                <title>{safe}</title>
                <link>jttp://{safe}/</link>
                <description>Next story for {safe}</description>
                <item>
                  <title>Headline goes here</title>
                  <author>unnamed</author>
                  <pubDate>{date}</pubDate>
                  <location>The mesh</location>
                  <description><![CDATA[
            ## The story

            Write the next story for this feed.

            ## Why it matters

            Add the details, sources, and context.
                  ]]></description>
                  <category>rss</category>
                  <category>story</category>
                  <!-- Optional: an absolute path to a PNG/JPEG/GIF/WebP/SVG image. -->
                  <enclosure url="" type="image/png"/>
                </item>
              </channel>
            </rss>
            """;
    }

    public static StoryDraft Parse(string host, string xml)
    {
        if (!host.Trim().EndsWith(".rss", StringComparison.OrdinalIgnoreCase))
            throw new KmlException("RSS XML is only for .rss names.");
        if (string.IsNullOrWhiteSpace(xml))
            throw new KmlException("RSS XML is empty.");

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            MaxCharactersInDocument = InlineHtml.MaxChars
        };

        XDocument doc;
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            doc = XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException ex)
        {
            throw new KmlException("RSS XML is malformed: " + ex.Message);
        }

        var root = doc.Root ?? throw new KmlException("RSS XML has no root element.");
        if (!string.Equals(root.Name.LocalName, "rss", StringComparison.OrdinalIgnoreCase))
            throw new KmlException("RSS XML must have an <rss> root.");

        var item = root.Descendants()
                       .FirstOrDefault(e => e.Name.LocalName.Equals("item", StringComparison.OrdinalIgnoreCase))
                   ?? throw new KmlException("RSS XML needs one <item>.");

        string Value(string name) =>
            item.Elements()
                .FirstOrDefault(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?.Value.Trim() ?? "";

        var tags = item.Elements()
            .Where(e => e.Name.LocalName.Equals("category", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Value.Trim().ToLowerInvariant())
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var enclosure = item.Elements()
            .FirstOrDefault(e => e.Name.LocalName.Equals("enclosure", StringComparison.OrdinalIgnoreCase))
            ?.Attribute("url")?.Value.Trim();

        return new StoryDraft
        {
            Host = host.Trim().ToLowerInvariant(),
            Headline = Value("title"),
            Author = Value("author"),
            Date = Value("pubDate"),
            Location = Value("location"),
            Body = Value("description"),
            Tags = tags,
            ImagePath = string.IsNullOrWhiteSpace(enclosure) ? null : enclosure,
            RssXml = xml
        };
    }
}
