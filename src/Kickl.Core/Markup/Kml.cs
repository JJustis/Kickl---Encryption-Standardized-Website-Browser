using System.Text;
using System.Xml;
using System.Xml.Linq;
using Kickl.Core.Documents;

namespace Kickl.Core.Markup;

public sealed class KmlException(string message) : Exception(message);

public enum BlockKind
{
    Heading,
    Paragraph,
    Code,
    Quote,
    Divider,
    Blob,
    File,
    Button,
    Image,
    Html,
    Ad,
    Game,
    Shop
}

/// <summary>One thing for sale in a <c>&lt;shop&gt;</c>. The price is whole satoshis; there is no floating point anywhere in a price.</summary>
public sealed class ProductInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required long PriceSats { get; init; }
    public string Description { get; init; } = "";
    /// <summary>The id of a sealed blob delivered with the product, if any. The seller hands the reader its access key after payment.</summary>
    public string? BlobId { get; init; }
}

public sealed class PageBlock
{
    public BlockKind Kind { get; init; }
    public string? Text { get; set; }
    public string? Id { get; set; }
    public string? Mime { get; set; }
    public string? Html { get; set; }
    public string? Publisher { get; set; }
    public string? Site { get; set; }
    public string? Space { get; set; }
    public string? Token { get; set; }
    public int Bytes { get; set; }
    public bool Protected { get; set; }
    public int Level { get; set; }

    /// <summary>A shop's Bitcoin address, checked down to its checksum when the page is parsed.</summary>
    public string? Address { get; set; }
    public bool Testnet { get; set; }
    public IReadOnlyList<ProductInfo> Products { get; set; } = [];
}

public sealed class PageView
{
    public required string Site { get; init; }
    public required string Title { get; init; }
    public string Headline { get; init; } = "";
    public string Author { get; init; } = "";
    public string Published { get; init; } = "";
    public string Location { get; init; } = "";
    public IReadOnlyList<string> Tags { get; init; } = [];
    public required List<PageBlock> Blocks { get; init; }

    public string Render(string address, bool verified, IReadOnlySet<string> unlocked,
        IReadOnlyDictionary<string, string> texts, bool owned = false, string? ownerId = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"jttp://{Site}/");
        sb.AppendLine($"{Title}");
        sb.AppendLine(new string('─', Math.Min(72, Math.Max(24, Title.Length + 12))));
        sb.AppendLine(verified ? "name: verified (registry + TXT proof)" : "name: unverified (no _kickl TXT proof)");
        sb.AppendLine(owned
            ? $"owner: {ownerId} · paid title · badge active"
            : "owner: no paid title (name can be created and closed, not traded)");
        sb.AppendLine();

        foreach (var block in Blocks)
        {
            switch (block.Kind)
            {
                case BlockKind.Heading:
                    sb.AppendLine(new string('#', Math.Clamp(block.Level, 1, 4)) + " " + block.Text);
                    break;
                case BlockKind.Paragraph:
                    sb.AppendLine(block.Text);
                    break;
                case BlockKind.Code:
                    sb.AppendLine("    " + block.Text?.Replace("\n", "\n    "));
                    break;
                case BlockKind.Quote:
                    sb.AppendLine("  │ " + block.Text?.Replace("\n", "\n  │ "));
                    break;
                case BlockKind.Divider:
                    sb.AppendLine(new string('·', 40));
                    break;
                case BlockKind.Button:
                    sb.AppendLine($"[ {block.Text} ]  (native action)");
                    break;
                case BlockKind.File:
                    sb.AppendLine($"[ file {block.Id} · {block.Mime} · {block.Bytes} B ]  sealed attachment");
                    break;
                case BlockKind.Image:
                    sb.AppendLine($"[ image {block.Id ?? "(remote)"} · {block.Text ?? ""} ]");
                    break;
                case BlockKind.Html:
                    sb.AppendLine("[ inline html/svg visual · scripts and external requests removed ]");
                    break;
                case BlockKind.Ad:
                    sb.AppendLine($"[ AD · {block.Publisher} · {block.Space} · NCKL ]");
                    break;
                case BlockKind.Game:
                    sb.AppendLine($"[ game {block.Id} · {block.Text} · WebAssembly sandbox · {block.Bytes} B ]  press play in the browser");
                    break;
                case BlockKind.Shop:
                    sb.AppendLine($"[ shop {block.Text} · pays to {block.Address}{(block.Testnet ? " · TESTNET, no real money" : "")} ]");
                    foreach (var product in block.Products)
                    {
                        sb.AppendLine($"    {product.Name} · {BtcAmount.Sats(product.PriceSats)} sats ({BtcAmount.Btc(product.PriceSats)} BTC){(product.BlobId is null ? "" : " · sealed file " + product.BlobId)}");
                        if (product.Description.Length > 0) sb.AppendLine("        " + product.Description);
                    }
                    sb.AppendLine("    Kickl cannot see payments; the seller confirms them and hands over any access key.");
                    break;
                case BlockKind.Blob when unlocked.Contains(block.Id!):
                    sb.AppendLine($"[ {block.Id} · UNLOCKED · {block.Mime} ]");
                    if (texts.TryGetValue(block.Id!, out var text))
                        sb.AppendLine("    " + text.Replace("\n", "\n    "));
                    sb.AppendLine();
                    break;
                case BlockKind.Blob:
                    sb.AppendLine($"[ {block.Id} · sealed · {block.Mime} · {block.Bytes} B ]");
                    sb.AppendLine($"    unlock: kickl unlock {block.Id} --pin <pin>");
                    sb.AppendLine();
                    break;
            }
        }

        return sb.ToString();
    }
}

/// <summary>
/// Strict KML parser. Unknown elements or attributes are errors, not warnings. Script,
/// frames, forms, and style attributes can never enter the model.
/// </summary>
public static class Kml
{
    private static readonly HashSet<string> AllowedElements = new(StringComparer.Ordinal)
    {
        "kml", "head", "title", "meta", "styles", "body", "page", "stack", "row", "grid", "card", "game", "shop", "product",
        "h1", "h2", "h3", "h4", "p", "text", "a", "code", "quote", "hr", "img", "blob", "file",
        "button", "badge", "b", "i", "u", "s", "span", "br", "html", "visual", "ad"
    };

    private static bool IsProtected(XElement e) => (string?)e.Attribute("access") switch
    {
        null or "public" => false,
        "protected" => true,
        var other => throw new KmlException($"access=\"{other}\" is not valid; use public or protected.")
    };

    private static readonly HashSet<string> AllowedAttributes = new(StringComparer.Ordinal)
    {
        "v", "site", "epoch", "name", "content", "id", "ref", "mime", "w", "h", "label", "size",
        "href", "tone", "align", "class", "width", "cols", "gap", "pad", "src", "alt", "intent",
        "lang", "mono", "title", "publisher", "space", "token", "access", "address", "price", "blob"
    };

    public static PageView Parse(string xml, string host)
    {
        if (string.IsNullOrWhiteSpace(xml)) throw new KmlException("Empty document.");
        if (xml.Length > 512 * 1024) throw new KmlException("Document exceeds 512 KiB.");

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            MaxCharactersInDocument = 600_000
        };

        XDocument doc;
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            doc = XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException ex)
        {
            throw new KmlException("Malformed XML: " + ex.Message);
        }

        var root = doc.Root ?? throw new KmlException("Missing root element.");
        if (root.Name.LocalName != "kml") throw new KmlException("Root element must be <kml>.");
        if ((string?)root.Attribute("v") != "1") throw new KmlException("Only KML v=\"1\" is supported.");

        Validate(root);

        var title = root.Element("head")?.Element("title")?.Value.Trim() ?? "Untitled";
        var head = root.Element("head");
        var body = root.Element("body") ?? throw new KmlException("Missing <body>.");

        var blocks = new List<PageBlock>();
        Walk(body, blocks);

        if (host.EndsWith(".rss", StringComparison.OrdinalIgnoreCase)
            && blocks.Any(b => b.Kind == BlockKind.Html))
            throw new KmlException("RSS stories use the fixed story template and cannot contain inline HTML/SVG.");
        if (host.EndsWith(".rss", StringComparison.OrdinalIgnoreCase)
            && blocks.Any(b => b.Kind == BlockKind.Ad))
            throw new KmlException("RSS stories cannot carry an NCKL ad slot.");

        return new PageView
        {
            Site = host,
            Title = title,
            Headline = Meta(head, "headline"),
            Author = Meta(head, "author"),
            Published = Meta(head, "date"),
            Location = Meta(head, "location"),
            Tags = SplitTags(Meta(head, "tags")),
            Blocks = blocks
        };
    }

    public const int MaxTags = 8;

    /// <summary>A phonebook tag: 2–24 lowercase LDH characters.</summary>
    public static bool IsValidTag(string tag) =>
        tag.Length is >= 2 and <= 24
        && tag[0].IsAsciiLetterOrDigit()
        && tag[^1].IsAsciiLetterOrDigit()
        && tag.All(c => c.IsAsciiLetterOrDigit() || c == '-');

    private static bool IsAsciiLetterOrDigit(this char c) =>
        c is >= 'a' and <= 'z' or >= '0' and <= '9';

    private static string Meta(XElement? head, string name) =>
        head?.Elements("meta")
            .FirstOrDefault(m => string.Equals((string?)m.Attribute("name"), name, StringComparison.OrdinalIgnoreCase))
            ?.Attribute("content")?.Value.Trim() ?? "";

    private static List<string> SplitTags(string raw) =>
        raw.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim().ToLowerInvariant())
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static void Validate(XElement element)
    {
        foreach (var e in element.DescendantsAndSelf())
        {
            var name = e.Name.LocalName;
            if (!AllowedElements.Contains(name))
                throw new KmlException($"Element <{name}> is not allowed.");
            if (e.Name.Namespace != XNamespace.None)
                throw new KmlException($"Namespaces are not allowed (<{name}>).");

            foreach (var attr in e.Attributes())
            {
                var an = attr.Name.LocalName;
                if (an.StartsWith("on", StringComparison.OrdinalIgnoreCase) || an == "style")
                    throw new KmlException($"Attribute '{an}' is not allowed.");
                if (!AllowedAttributes.Contains(an))
                    throw new KmlException($"Attribute '{an}' on <{name}> is not allowed.");
            }
        }
    }

    private static void Walk(XElement element, List<PageBlock> blocks)
    {
        foreach (var child in element.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "page":
                case "stack":
                case "row":
                case "grid":
                case "card":
                case "body":
                    Walk(child, blocks);
                    break;
                case "h1":
                case "h2":
                case "h3":
                case "h4":
                    blocks.Add(new PageBlock
                    {
                        Kind = BlockKind.Heading,
                        Level = int.Parse(child.Name.LocalName[1..]),
                        Text = TextOf(child)
                    });
                    break;
                case "p":
                case "text":
                case "span":
                    blocks.Add(new PageBlock { Kind = BlockKind.Paragraph, Text = TextOf(child) });
                    break;
                case "code":
                    blocks.Add(new PageBlock { Kind = BlockKind.Code, Text = child.Value });
                    break;
                case "quote":
                    blocks.Add(new PageBlock { Kind = BlockKind.Quote, Text = TextOf(child) });
                    break;
                case "hr":
                    blocks.Add(new PageBlock { Kind = BlockKind.Divider });
                    break;
                case "blob":
                    blocks.Add(new PageBlock
                    {
                        Kind = BlockKind.Blob,
                        Id = (string?)child.Attribute("id") ?? throw new KmlException("<blob> needs an id."),
                        Mime = (string?)child.Attribute("mime") ?? "application/octet-stream",
                        Protected = IsProtected(child)
                    });
                    break;
                case "file":
                    blocks.Add(new PageBlock
                    {
                        Kind = BlockKind.File,
                        Id = (string?)child.Attribute("id") ?? throw new KmlException("<file> needs an id."),
                        Mime = (string?)child.Attribute("mime") ?? "application/octet-stream",
                        Protected = IsProtected(child)
                    });
                    break;
                case "game":
                    blocks.Add(new PageBlock
                    {
                        Kind = BlockKind.Game,
                        Id = (string?)child.Attribute("id") ?? throw new KmlException("<game> needs the id of its wasm blob."),
                        Text = (string?)child.Attribute("label") ?? "Game",
                        Mime = "application/wasm",
                        Protected = IsProtected(child)
                    });
                    break;
                case "shop":
                    AddShop(child, blocks);
                    break;
                case "button":
                    blocks.Add(new PageBlock { Kind = BlockKind.Button, Text = TextOf(child) });
                    break;
                case "badge":
                    blocks.Add(new PageBlock { Kind = BlockKind.Paragraph, Text = "· " + TextOf(child) + " ·" });
                    break;
                case "img":
                    blocks.Add(new PageBlock
                    {
                        Kind = BlockKind.Image,
                        Id = (string?)child.Attribute("ref") ?? (string?)child.Attribute("src"),
                        Text = (string?)child.Attribute("alt") ?? "",
                        Mime = "image"
                    });
                    break;
                case "html":
                case "visual":
                    var inline = InlineHtml.Sanitize(child.Value);
                    if (inline.Length > 0)
                        blocks.Add(new PageBlock { Kind = BlockKind.Html, Html = inline });
                    break;
                case "ad":
                    blocks.Add(new PageBlock
                    {
                        Kind = BlockKind.Ad,
                        Publisher = (string?)child.Attribute("publisher")
                                    ?? throw new KmlException("<ad> needs a publisher."),
                        Site = (string?)child.Attribute("site")
                               ?? throw new KmlException("<ad> needs a site."),
                        Space = (string?)child.Attribute("space")
                                ?? throw new KmlException("<ad> needs a space."),
                        Token = (string?)child.Attribute("token")
                                ?? throw new KmlException("<ad> needs a token.")
                    });
                    break;
                case "a":
                case "b":
                case "i":
                case "u":
                case "s":
                case "br":
                case "meta":
                case "title":
                case "styles":
                    break;
                default:
                    throw new KmlException($"Unexpected element <{child.Name.LocalName}>.");
            }
        }
    }

    public const int MaxProducts = 24;

    /// <summary>
    /// <c>&lt;shop name="…" address="…"&gt;</c> holding <c>&lt;product id="…" name="…" price="sats" [blob="id" access="protected"]&gt;description&lt;/product&gt;</c>.
    /// The address must be a complete, checksummed Bitcoin address; prices are whole satoshis. A product's <c>blob</c> is delivered as a
    /// sealed blob on the same page, so a protected one opens with the access key the seller sends after payment.
    /// </summary>
    private static void AddShop(XElement shop, List<PageBlock> blocks)
    {
        var address = ((string?)shop.Attribute("address"))?.Trim()
                      ?? throw new KmlException("<shop> needs the seller's Bitcoin address.");
        var info = BitcoinAddress.Parse(address)
                   ?? throw new KmlException("<shop> address is not a valid Bitcoin address (the checksum does not match). Check it character by character.");
        var name = (string?)shop.Attribute("name") ?? "Shop";
        if (name.Length > 80) throw new KmlException("<shop> name is longer than 80 characters.");

        var products = new List<ProductInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var child in shop.Elements())
        {
            if (child.Name.LocalName != "product")
                throw new KmlException($"A <shop> can only hold <product> elements, not <{child.Name.LocalName}>.");
            if (products.Count >= MaxProducts) throw new KmlException($"A shop can list at most {MaxProducts} products.");

            var id = (string?)child.Attribute("id") ?? throw new KmlException("<product> needs an id.");
            if (id.Length is < 1 or > 32 || !id.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'))
                throw new KmlException($"Product id \"{id}\" must be 1-32 lowercase letters, digits or hyphens.");
            if (!seen.Add(id)) throw new KmlException($"Product id \"{id}\" is used twice.");

            var productName = (string?)child.Attribute("name") ?? throw new KmlException($"Product {id} needs a name.");
            if (productName.Length is < 1 or > 80) throw new KmlException($"Product {id}: the name must be 1-80 characters.");

            var rawPrice = (string?)child.Attribute("price") ?? throw new KmlException($"Product {id} needs a price in satoshis.");
            if (!long.TryParse(rawPrice, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var price)
                || price < 1 || price > BtcAmount.MaxSats)
                throw new KmlException($"Product {id}: the price must be a whole number of satoshis between 1 and {BtcAmount.MaxSats:N0}.");

            var description = TextOf(child);
            if (description.Length > 500) throw new KmlException($"Product {id}: the description is longer than 500 characters.");

            var blob = (string?)child.Attribute("blob");
            if (blob is not null && (blob.Length is < 1 or > 64)) throw new KmlException($"Product {id}: the blob id is not valid.");
            products.Add(new ProductInfo { Id = id, Name = productName, PriceSats = price, Description = description, BlobId = blob });
        }
        if (products.Count == 0) throw new KmlException("A <shop> needs at least one <product>.");

        blocks.Add(new PageBlock
        {
            Kind = BlockKind.Shop,
            Text = name,
            Address = address,
            Testnet = info.Network == BitcoinNetwork.Testnet,
            Products = products
        });

        // Each product's file is an ordinary sealed blob on the page; the product's access setting decides whether it needs a key
        foreach (var product in shop.Elements().Where(p => p.Attribute("blob") is not null))
        {
            blocks.Add(new PageBlock
            {
                Kind = BlockKind.Blob,
                Id = (string)product.Attribute("blob")!,
                Mime = (string?)product.Attribute("mime") ?? "application/octet-stream",
                Protected = IsProtected(product)
            });
        }
    }

    private static string TextOf(XElement element) =>
        string.Join(' ', string.Concat(element.DescendantNodes().OfType<XText>().Select(t => t.Value))
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Attach sealed sizes after parsing (the parser does not see the document).</summary>
    public static void Enrich(PageView page, SealedDocument doc)
    {
        foreach (var block in page.Blocks.Where(b => b.Id is not null))
        {
            if (doc.Blobs.TryGetValue(block.Id!, out var blob))
            {
                block.Bytes = blob.PlainLength;
                if (block.Mime is null) block.Mime = blob.Mime;
            }
        }
    }
}
