using System.Net;
using System.Security.Cryptography;
using System.Text;
using HtmlAgilityPack;

namespace Kickl.Core.Markup;

/// <summary>A raster image pulled out of inline markup and sealed as a document blob at publish time.</summary>
public sealed record InlineImage(string Id, string Mime, byte[] Bytes);

/// <summary>
/// A deliberately small HTML surface for inline visuals. It exists so a page can contain a
/// div filled with an inline SVG instead of learning a CSS dialect. Scripts, forms, external
/// resources, event handlers, and stylesheet machinery are removed before the renderer sees
/// the fragment. The result is still inert markup: no navigation, no storage, no requests.
/// </summary>
public static class InlineHtml
{
    public const int MaxChars = 96 * 1024;
    public const int MaxNodes = 8_192;
    public const int MaxDepth = 48;
    public const string BlobPrefix = "kickl-blob:";

    private static readonly HashSet<string> Dropped = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "link", "meta", "base", "iframe", "frame", "frameset",
        "object", "embed", "applet", "form", "input", "button", "textarea", "select",
        "option", "audio", "video", "source", "track", "canvas", "noscript", "template",
        "head", "foreignobject"
    };

    private static readonly HashSet<string> HtmlAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "colspan", "rowspan", "align", "valign", "width", "height", "alt", "title", "src"
    };

    private static readonly HashSet<string> SvgAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "viewbox", "width", "height", "x", "y", "x1", "y1", "x2", "y2", "cx", "cy",
        "r", "rx", "ry", "d", "points", "fill", "stroke", "stroke-width", "opacity",
        "fill-opacity", "stroke-opacity", "transform", "offset", "stop-color",
        "stop-opacity", "font-size", "font-family", "font-weight", "text-anchor",
        "dominant-baseline", "gradientunits", "gradienttransform", "spreadmethod",
        "id", "href", "xlink:href", "style", "preserveaspectratio", "clip-path", "mask",
        "filter", "version", "xmlns", "xmlns:xlink", "stroke-linecap", "stroke-linejoin",
        "stroke-dasharray", "textlength", "lengthadjust", "pathlength"
    };

    private static readonly HashSet<string> SvgStyleProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "stop-color", "stop-opacity", "fill", "stroke", "stroke-width", "opacity",
        "fill-opacity", "stroke-opacity", "font-size", "font-family", "font-weight",
        "text-anchor", "color", "display", "visibility", "stroke-linecap",
        "stroke-linejoin", "stroke-dasharray"
    };

    private static readonly Dictionary<string, string> SvgNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["lineargradient"] = "linearGradient",
        ["radialgradient"] = "radialGradient",
        ["clippath"] = "clipPath",
        ["textpath"] = "textPath",
        ["preserveaspectratio"] = "preserveAspectRatio",
        ["viewbox"] = "viewBox",
        ["gradientunits"] = "gradientUnits",
        ["gradienttransform"] = "gradientTransform",
        ["spreadmethod"] = "spreadMethod",
        ["stop-color"] = "stop-color",
        ["stop-opacity"] = "stop-opacity"
    };

    public static string Sanitize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        if (raw.Length > MaxChars)
            throw new KmlException($"Inline HTML exceeds {MaxChars / 1024} KiB.");

        var doc = new HtmlDocument
        {
            OptionOutputOriginalCase = true,
            OptionFixNestedTags = true,
            OptionAutoCloseOnEnd = true
        };
        doc.LoadHtml(raw);
        ValidateTreeBudget(doc.DocumentNode);

        var root = doc.DocumentNode;
        foreach (var node in root.DescendantsAndSelf().ToList())
        {
            if (node.NodeType == HtmlNodeType.Comment)
            {
                node.Remove();
                continue;
            }
            if (node.NodeType != HtmlNodeType.Element) continue;

            var name = node.Name;
            if (Dropped.Contains(name))
            {
                node.Remove();
                continue;
            }

            var insideSvg = string.Equals(name, "svg", StringComparison.OrdinalIgnoreCase)
                            || node.Ancestors().Any(a => string.Equals(a.Name, "svg", StringComparison.OrdinalIgnoreCase));
            if (insideSvg)
                SanitizeSvgElement(node);
            else
                SanitizeHtmlElement(node);
        }

        var body = root.SelectSingleNode("//body");
        var result = (body ?? root).InnerHtml.Trim();
        if (result.Length > MaxChars)
            throw new KmlException($"Sanitized inline HTML exceeds {MaxChars / 1024} KiB.");
        return result;
    }

    private static void SanitizeHtmlElement(HtmlNode node)
    {
        var name = node.Name.ToLowerInvariant();
        if (name == "img")
        {
            var src = node.GetAttributeValue("src", "");
            if (!IsSafeImageData(src) && !IsSealedReference(src))
            {
                var alt = WebUtility.HtmlDecode(node.GetAttributeValue("alt", ""));
                node.ParentNode?.ReplaceChild(HtmlTextNode.CreateNode(alt), node);
                return;
            }
        }

        foreach (var attr in node.Attributes.ToList())
        {
            var attrName = attr.Name.ToLowerInvariant();
            if (attrName.StartsWith("on", StringComparison.Ordinal) || !HtmlAttributes.Contains(attrName))
            {
                attr.Remove();
                continue;
            }
            if (!SafeValue(attr.Value ?? ""))
            {
                attr.Remove();
            }
        }

        if (name == "a")
        {
            // Inline pages are text, not navigation surfaces. The browser confirms every
            // jttp navigation before it happens; a raw href in content would bypass that.
            node.Attributes.RemoveAll();
        }
    }

    private static void SanitizeSvgElement(HtmlNode node)
    {
        if (SvgNames.TryGetValue(node.Name, out var canonical))
            node.Name = canonical;

        foreach (var attr in node.Attributes.ToList())
        {
            var attrName = attr.Name.ToLowerInvariant();
            if (attrName.StartsWith("on", StringComparison.Ordinal))
            {
                attr.Remove();
                continue;
            }
            if (attrName == "class")
            {
                attr.Remove();
                continue;
            }
            if (!SvgAttributes.Contains(attrName))
            {
                attr.Remove();
                continue;
            }
            if (SvgNames.TryGetValue(attrName, out var attrCanonical) && attrCanonical.StartsWith("stop", StringComparison.Ordinal))
                attr.Name = attrCanonical;

            if (attrName is "href" or "xlink:href")
            {
                if (!IsSafeReference(attr.Value ?? ""))
                    attr.Remove();
                continue;
            }
            if (attrName == "style")
            {
                var style = SafeStyle(attr.Value ?? "");
                if (style.Length == 0) attr.Remove();
                else attr.Value = style;
                continue;
            }
            if (attrName is "fill" or "stroke")
            {
                if (!IsSafePaint(attr.Value ?? ""))
                {
                    attr.Remove();
                    continue;
                }
            }
            if (!SafeValue(attr.Value ?? ""))
                attr.Remove();
        }

        if (string.Equals(node.Name, "svg", StringComparison.OrdinalIgnoreCase)
            && node.Attributes["xmlns"] is null)
            node.SetAttributeValue("xmlns", "http://www.w3.org/2000/svg");
    }

    private static bool SafeValue(string value)
    {
        var lower = value.Trim().ToLowerInvariant();
        return !lower.Contains("javascript:", StringComparison.Ordinal)
               && !lower.Contains("vbscript:", StringComparison.Ordinal)
               && !lower.Contains("data:text/html", StringComparison.Ordinal)
               && !lower.Contains("expression(", StringComparison.Ordinal)
               && !lower.Contains("@import", StringComparison.Ordinal);
    }

    private static bool IsSafeReference(string value)
    {
        var v = value.Trim();
        return (v.StartsWith('#') && SafeValue(v)) || IsSafeImageData(v) || IsSealedReference(v);
    }

    /// <summary>A public reference from inline SVG/HTML to a sealed Pinwheel image blob.</summary>
    public static bool IsSealedReference(string value)
    {
        var v = value.Trim();
        return v.StartsWith(BlobPrefix, StringComparison.Ordinal) && IsBlobId(v[BlobPrefix.Length..]);
    }

    public static bool IsBlobId(string id) =>
        id.Length is >= 1 and <= 64
        && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>
    /// Pull data-URL images out of a sanitized fragment. The HTML keeps only a public
    /// kickl-blob:id reference; the decoded bytes become sealed document blobs at publish time.
    /// </summary>
    public static (string Html, IReadOnlyList<InlineImage> Images) ExtractDataImages(string? sanitized)
    {
        if (string.IsNullOrWhiteSpace(sanitized)) return ("", []);

        var doc = new HtmlDocument
        {
            OptionOutputOriginalCase = true,
            OptionFixNestedTags = true,
            OptionAutoCloseOnEnd = true
        };
        doc.LoadHtml(sanitized);

        var images = new Dictionary<string, InlineImage>(StringComparer.Ordinal);
        foreach (var node in doc.DocumentNode.DescendantsAndSelf().ToList())
        {
            if (node.NodeType != HtmlNodeType.Element) continue;
            var name = node.Name.ToLowerInvariant();
            if (name is not ("img" or "image")) continue;

            foreach (var attr in node.Attributes.ToList())
            {
                var attrName = attr.Name.ToLowerInvariant();
                if (attrName is not ("src" or "href" or "xlink:href")) continue;
                var value = (attr.Value ?? "").Trim();
                if (!value.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)) continue;

                var comma = value.IndexOf(',');
                if (comma < 0) continue;
                var header = value[..comma].ToLowerInvariant();
                if (header.Contains("svg+xml", StringComparison.Ordinal)) continue;
                var mime = header[5..];
                var semi = mime.IndexOf(';');
                if (semi >= 0) mime = mime[..semi];
                if (mime is not ("image/png" or "image/jpeg" or "image/gif" or "image/webp"))
                    continue;

                byte[] bytes;
                try
                {
                    bytes = header.EndsWith(";base64", StringComparison.Ordinal)
                        ? Convert.FromBase64String(value[(comma + 1)..])
                        : Encoding.UTF8.GetBytes(Uri.UnescapeDataString(value[(comma + 1)..]));
                }
                catch (FormatException)
                {
                    continue;
                }

                var id = "v" + Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant();
                if (!images.ContainsKey(id))
                    images[id] = new InlineImage(id, mime, bytes);
                else
                    CryptographicOperations.ZeroMemory(bytes);
                attr.Value = BlobPrefix + id;
            }
        }

        var body = doc.DocumentNode.SelectSingleNode("//body");
        return ((body ?? doc.DocumentNode).InnerHtml.Trim(), images.Values.ToList());
    }

    public static bool IsSafeImageData(string value)
    {
        var v = value.Trim();
        if (!v.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) || !SafeValue(v))
            return false;
        var comma = v.IndexOf(',');
        var header = comma < 0 ? v : v[..comma];
        // Nested SVG data can hide its own references. Inline <svg> is supported directly;
        // raster data URLs are the only image data allowed through.
        return !header.Contains("svg+xml", StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateTreeBudget(HtmlNode root)
    {
        var pending = new Stack<(HtmlNode Node, int Depth)>();
        pending.Push((root, 0));
        var count = 0;
        while (pending.Count > 0)
        {
            var (node, depth) = pending.Pop();
            if (++count > MaxNodes) throw new KmlException($"Inline HTML exceeds {MaxNodes} nodes.");
            if (depth > MaxDepth) throw new KmlException($"Inline HTML exceeds {MaxDepth} levels of nesting.");
            foreach (var child in node.ChildNodes)
                pending.Push((child, depth + 1));
        }
    }

    private static bool IsSafePaint(string value)
    {
        var v = value.Trim();
        if (!SafeValue(v)) return false;
        var lower = v.ToLowerInvariant();
        if (!lower.Contains("url(", StringComparison.Ordinal)) return true;
        var index = 0;
        while ((index = lower.IndexOf("url(", index, StringComparison.Ordinal)) >= 0)
        {
            var start = index + 4;
            var end = lower.IndexOf(')', start);
            if (end < 0) return false;
            var target = lower[start..end].Trim().Trim('"', '\'');
            if (!target.StartsWith('#')) return false;
            index = end + 1;
        }
        return true;
    }

    private static string SafeStyle(string value)
    {
        var builder = new StringBuilder();
        foreach (var declaration in value.Split(';'))
        {
            var colon = declaration.IndexOf(':');
            if (colon <= 0) continue;
            var property = declaration[..colon].Trim();
            var propertyValue = declaration[(colon + 1)..].Trim();
            if (!SvgStyleProperties.Contains(property)) continue;
            if (!SafeValue(propertyValue)) continue;
            if (propertyValue.Contains("url(", StringComparison.OrdinalIgnoreCase)
                && !IsSafePaint(propertyValue)) continue;
            if (builder.Length > 0) builder.Append("; ");
            builder.Append(property).Append(": ").Append(propertyValue);
        }
        return builder.ToString();
    }
}
