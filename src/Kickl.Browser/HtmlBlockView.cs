using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Svg.Skia;
using Kickl.Core.Markup;
using HtmlAgilityPack;

namespace Kickl.Browser;

/// <summary>
/// Renders the sanitized inline HTML fragment that a sealed page may carry. The subset is
/// intentionally spatial rather than a browser engine: containers, text, tables, lists, data
/// images, and inline SVG. There is no CSS engine, no script engine, and no network loader.
/// </summary>
internal static class HtmlBlockView
{
    private const string BlobPrefix = "kickl-blob:";
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex BlobReference = new(@"kickl-blob:([A-Za-z0-9_-]{1,64})", RegexOptions.Compiled);
    private static readonly IReadOnlyDictionary<string, UnlockedVisual> NoVisuals =
        new Dictionary<string, UnlockedVisual>(StringComparer.Ordinal);

    // A 1x1 transparent PNG keeps the SVG layout intact while the real sealed image is locked.
    private const string PlaceholderData =
        "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAALSURBVBhXY2AAAgAABQABqtXIUQAAAABJRU5ErkJggg==";

    private sealed record Ctx(
        IBrush Text,
        IBrush Muted,
        IBrush Accent,
        FontFamily Body,
        FontFamily Mono,
        double Size,
        bool Center,
        bool Pre,
        IReadOnlyDictionary<string, UnlockedVisual> Visuals,
        Action<IReadOnlyList<string>>? Unlock);

    private sealed record InlineStyle(bool Bold = false, bool Italic = false,
        bool Underline = false, bool Strike = false, bool Code = false, bool Link = false);

    /// <summary>Bytes decrypted for one inline visual. The browser tab owns and zeroes them.</summary>
    public sealed record UnlockedVisual(string Mime, byte[] Bytes);

    public static Control Build(string html, IBrush text, IBrush muted, IBrush accent,
        FontFamily body, FontFamily mono, double size,
        IReadOnlyDictionary<string, UnlockedVisual>? visuals = null,
        Action<IReadOnlyList<string>>? unlock = null)
    {
        try
        {
            html = InlineHtml.Sanitize(html);
            var doc = new HtmlDocument
            {
                OptionOutputOriginalCase = true,
                OptionFixNestedTags = true,
                OptionAutoCloseOnEnd = true
            };
            doc.LoadHtml(html);
            var root = doc.DocumentNode.SelectSingleNode("//body") ?? doc.DocumentNode;
            var ctx = new Ctx(text, muted, accent, body, mono, size, false, false,
                visuals ?? NoVisuals, unlock);

            var panel = new StackPanel { Spacing = 8 };
            foreach (var child in root.ChildNodes)
            {
                var view = BuildBlock(child, ctx);
                if (view is not null) panel.Children.Add(view);
            }

            if (panel.Children.Count == 0)
                return new TextBlock { Text = "(empty inline visual)", Foreground = muted, FontSize = size };
            return panel;
        }
        catch (Exception ex)
        {
            return new TextBlock
            {
                Text = "inline html/svg could not be rendered · " + ex.Message,
                Foreground = muted,
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap
            };
        }
    }

    private static Control? BuildBlock(HtmlNode node, Ctx ctx)
    {
        if (node.NodeType == HtmlNodeType.Comment) return null;
        if (node.NodeType == HtmlNodeType.Text)
        {
            var text = Normalize(node.InnerText, ctx.Pre);
            return text.Length == 0 ? null : new TextBlock
            {
                Text = text,
                Foreground = ctx.Text,
                FontFamily = ctx.Body,
                FontSize = ctx.Size,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = ctx.Center ? TextAlignment.Center : TextAlignment.Left
            };
        }
        if (node.NodeType != HtmlNodeType.Element) return null;

        var name = node.Name.ToLowerInvariant();
        switch (name)
        {
            case "svg":
                return BuildSvg(node, ctx);
            case "img":
                return BuildImage(node, ctx);
            case "br":
                return new TextBlock { Text = " " };
            case "hr":
                return new Border
                {
                    Height = 1,
                    Background = ctx.Muted,
                    Opacity = 0.35,
                    Margin = new Thickness(0, 4)
                };
            case "h1":
            case "h2":
            case "h3":
            case "h4":
                return TextBlockFor(node, ctx, name switch
                {
                    "h1" => ctx.Size + 13,
                    "h2" => ctx.Size + 7,
                    "h3" => ctx.Size + 3,
                    _ => ctx.Size + 1
                }, bold: true);
            case "p":
            case "div":
            case "center":
            case "body":
            case "html":
            case "section":
            case "article":
            case "header":
            case "footer":
            case "main":
            case "nav":
            case "aside":
            case "figure":
            case "figcaption":
            case "blockquote":
                if (name == "blockquote")
                    return new Border
                    {
                        BorderBrush = ctx.Accent,
                        BorderThickness = new Thickness(2, 0, 0, 0),
                        Padding = new Thickness(10, 0, 0, 0),
                        Child = Container(node, ctx with { Center = false })
                    };
                return Container(node, ctx with { Center = ctx.Center || name == "center" });
            case "pre":
                return TextBlockFor(node, ctx with { Pre = true }, ctx.Size - 0.5, mono: true);
            case "ul":
            case "ol":
                return BuildList(node, ctx, ordered: name == "ol");
            case "table":
                return BuildTable(node, ctx);
            case "tr":
                return BuildTableRow(node, ctx);
            case "a":
            case "b":
            case "strong":
            case "i":
            case "em":
            case "u":
            case "s":
            case "strike":
            case "del":
            case "span":
            case "code":
            case "small":
            case "big":
            case "sub":
            case "sup":
                return TextBlockFor(node, ctx, ctx.Size, mono: name == "code");
            default:
                return Container(node, ctx);
        }
    }

    private static Control Container(HtmlNode node, Ctx ctx)
    {
        var panel = new StackPanel { Spacing = 6 };
        if (ctx.Center) panel.HorizontalAlignment = HorizontalAlignment.Center;
        foreach (var child in node.ChildNodes)
        {
            var view = BuildBlock(child, ctx);
            if (view is not null) panel.Children.Add(view);
        }
        return panel;
    }

    private static TextBlock TextBlockFor(HtmlNode node, Ctx ctx, double size,
        bool bold = false, bool mono = false)
    {
        var block = new TextBlock
        {
            Foreground = ctx.Text,
            FontFamily = mono ? ctx.Mono : ctx.Body,
            FontSize = size,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = ctx.Center ? TextAlignment.Center : TextAlignment.Left,
            LineHeight = Math.Round(size * 1.45, 1)
        };
        AddInlines(block.Inlines!, node, ctx, new InlineStyle(Bold: bold, Code: mono), ctx.Pre);
        return block;
    }

    private static void AddInlines(InlineCollection inlines, HtmlNode node, Ctx ctx,
        InlineStyle style, bool preserveWhitespace)
    {
        foreach (var child in node.ChildNodes)
        {
            if (child.NodeType == HtmlNodeType.Text)
            {
                var text = preserveWhitespace ? child.InnerText : Normalize(child.InnerText, false);
                if (text.Length == 0) continue;
                var run = new Run
                {
                    Text = text,
                    FontFamily = style.Code ? ctx.Mono : ctx.Body,
                    FontSize = ctx.Size,
                    FontWeight = style.Bold ? FontWeight.Bold : FontWeight.Normal,
                    FontStyle = style.Italic ? FontStyle.Italic : FontStyle.Normal,
                    Foreground = style.Link ? ctx.Accent : ctx.Text
                };
                if (style.Underline || style.Link)
                    run.TextDecorations = TextDecorations.Underline;
                else if (style.Strike)
                    run.TextDecorations = TextDecorations.Strikethrough;
                inlines.Add(run);
                continue;
            }
            if (child.NodeType != HtmlNodeType.Element) continue;

            var name = child.Name.ToLowerInvariant();
            if (name == "br")
            {
                inlines.Add(new LineBreak());
                continue;
            }
            if (name is "svg" or "img" or "table" or "ul" or "ol" or "div")
                continue;

            var next = name switch
            {
                "b" or "strong" => style with { Bold = true },
                "i" or "em" => style with { Italic = true },
                "u" => style with { Underline = true },
                "s" or "strike" or "del" => style with { Strike = true },
                "code" => style with { Code = true },
                "a" => style with { Link = true, Underline = true },
                _ => style
            };
            AddInlines(inlines, child, ctx, next, preserveWhitespace || name == "pre");
        }
    }

    private static Control BuildList(HtmlNode node, Ctx ctx, bool ordered)
    {
        var panel = new StackPanel { Spacing = 4 };
        var items = node.ChildNodes
            .Where(n => n.NodeType == HtmlNodeType.Element && n.Name.Equals("li", StringComparison.OrdinalIgnoreCase))
            .ToList();
        for (var i = 0; i < items.Count; i++)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("22,*") };
            row.Children.Add(new TextBlock
            {
                Text = ordered ? $"{i + 1}." : "•",
                Foreground = ctx.Accent,
                FontFamily = ctx.Mono,
                FontSize = ctx.Size,
                Margin = new Thickness(0, 0, 4, 0)
            });
            var content = BuildBlock(items[i], ctx with { Center = false })
                          ?? new TextBlock { Text = "" };
            row.Children.Add(content);
            Grid.SetColumn(content, 1);
            panel.Children.Add(row);
        }
        return panel;
    }

    private static Control BuildTable(HtmlNode node, Ctx ctx)
    {
        var rows = node.Descendants()
            .Where(n => n.NodeType == HtmlNodeType.Element && n.Name.Equals("tr", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (rows.Count == 0) return Container(node, ctx);

        var columns = Math.Max(1, rows.Max(r => Cells(r).Count));
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(string.Join(",", Enumerable.Repeat("*", columns)))
        };
        foreach (var row in rows)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        }

        for (var r = 0; r < rows.Count; r++)
        {
            var cells = Cells(rows[r]);
            for (var c = 0; c < cells.Count && c < columns; c++)
            {
                var cell = BuildBlock(cells[c], ctx with { Center = false })
                           ?? new TextBlock { Text = "" };
                var border = new Border
                {
                    Padding = new Thickness(6, 4),
                    BorderBrush = ctx.Muted,
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Child = cell
                };
                grid.Children.Add(border);
                Grid.SetRow(border, r);
                Grid.SetColumn(border, c);
            }
        }
        return grid;
    }

    private static Control BuildTableRow(HtmlNode node, Ctx ctx)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        foreach (var cell in Cells(node))
        {
            var view = BuildBlock(cell, ctx) ?? new TextBlock { Text = "" };
            panel.Children.Add(view);
        }
        return panel;
    }

    private static List<HtmlNode> Cells(HtmlNode row) =>
        row.ChildNodes
            .Where(n => n.NodeType == HtmlNodeType.Element
                        && (n.Name.Equals("td", StringComparison.OrdinalIgnoreCase)
                            || n.Name.Equals("th", StringComparison.OrdinalIgnoreCase)))
            .ToList();

    private static Control BuildSvg(HtmlNode node, Ctx ctx)
    {
        try
        {
            var raw = node.OuterHtml;
            var sealedIds = SealedIds(raw, ctx.Visuals);
            var markup = ResolveBlobReferences(raw, ctx.Visuals);
            if (!markup.Contains("xmlns=", StringComparison.Ordinal))
                markup = markup.Replace("<svg", "<svg xmlns=\"http://www.w3.org/2000/svg\"", StringComparison.OrdinalIgnoreCase);

            var source = SvgSource.LoadFromSvg(markup);
            var image = new Image
            {
                Source = new SvgImage { Source = source },
                Stretch = Stretch.Uniform,
                MaxWidth = 760,
                MaxHeight = 560,
                HorizontalAlignment = HorizontalAlignment.Left
            };

            if (sealedIds.Count == 0) return image;
            return new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    image,
                    SealedImageCard(sealedIds, ctx)
                }
            };
        }
        catch (Exception ex)
        {
            return new TextBlock
            {
                Text = "inline svg could not be rendered · " + ex.Message,
                Foreground = ctx.Muted,
                FontSize = 9,
                TextWrapping = TextWrapping.Wrap
            };
        }
    }

    private static Control? BuildImage(HtmlNode node, Ctx ctx)
    {
        var src = node.GetAttributeValue("src", "");
        if (!SealedSurfacePolicy.AllowsSubresource(src)) return null;
        if (src.StartsWith(BlobPrefix, StringComparison.Ordinal))
        {
            var id = src[BlobPrefix.Length..];
            if (!IsBlobId(id)) return null;
            if (ctx.Visuals.TryGetValue(id, out var unlocked))
                return BuildUnlockedImage(unlocked, ctx);
            return SealedImageCard([id], ctx);
        }

        if (!src.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)) return null;

        try
        {
            var comma = src.IndexOf(',');
            if (comma < 0) return null;
            var header = src[..comma];
            var payload = src[(comma + 1)..];
            var bytes = header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase)
                ? Convert.FromBase64String(payload)
                : Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));

            return BuildImageFromBytes(header, bytes, ctx);
        }
        catch (Exception ex)
        {
            return new TextBlock
            {
                Text = "inline image could not be rendered · " + ex.Message,
                Foreground = ctx.Muted,
                FontSize = 9
            };
        }
    }

    private static Control BuildUnlockedImage(UnlockedVisual visual, Ctx ctx)
    {
        try
        {
            return BuildImageFromBytes("data:" + visual.Mime, visual.Bytes, ctx);
        }
        catch (Exception ex)
        {
            return new TextBlock
            {
                Text = "sealed image could not be rendered · " + ex.Message,
                Foreground = ctx.Muted,
                FontSize = 9
            };
        }
    }

    private static Control BuildImageFromBytes(string header, byte[] bytes, Ctx ctx)
    {
        if (header.Contains("svg+xml", StringComparison.OrdinalIgnoreCase))
        {
            // never hand raw SVG bytes to the renderer: a sealed blob's type is chosen by the page's author
            var source = SvgSource.LoadFromSvg(Kickl.Core.Markup.InlineHtml.SanitizeSvgFile(Encoding.UTF8.GetString(bytes)));
            return new Image
            {
                Source = new SvgImage { Source = source },
                Stretch = Stretch.Uniform,
                MaxWidth = 760,
                MaxHeight = 560,
                HorizontalAlignment = HorizontalAlignment.Left
            };
        }

        using var stream = new MemoryStream(bytes);
        return new Image
        {
            Source = new Bitmap(stream),
            Stretch = Stretch.Uniform,
            MaxWidth = 760,
            MaxHeight = 560,
            HorizontalAlignment = HorizontalAlignment.Left
        };
    }

    private static Control SealedImageCard(IReadOnlyList<string> ids, Ctx ctx)
    {
        var plural = ids.Count > 1;
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock
        {
            Text = "SEALED IMAGE" + (plural ? "S" : "") + " · " + string.Join(", ", ids),
            Foreground = ctx.Muted,
            FontFamily = ctx.Mono,
            FontSize = 9.5,
            TextWrapping = TextWrapping.Wrap
        });
        var button = new Button
        {
            Content = "UNLOCK SEALED IMAGE" + (plural ? "S" : ""),
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(12, 5),
            FontSize = 10,
            FontWeight = FontWeight.Bold,
            Background = ctx.Accent,
            Foreground = Brushes.Black,
            IsEnabled = ctx.Unlock is not null
        };
        button.Click += (_, _) => ctx.Unlock?.Invoke(ids);
        panel.Children.Add(button);
        return panel;
    }

    private static List<string> SealedIds(string markup, IReadOnlyDictionary<string, UnlockedVisual> visuals) =>
        BlobReference.Matches(markup)
            .Select(m => m.Groups[1].Value)
            .Where(id => !visuals.ContainsKey(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static string ResolveBlobReferences(string markup,
        IReadOnlyDictionary<string, UnlockedVisual> visuals) =>
        BlobReference.Replace(markup, match =>
        {
            if (!visuals.TryGetValue(match.Groups[1].Value, out var visual)) return PlaceholderData;
            return $"data:{visual.Mime};base64,{Convert.ToBase64String(visual.Bytes)}";
        });

    private static bool IsBlobId(string id) =>
        id.Length is >= 1 and <= 64
        && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static string Normalize(string value, bool preserve)
    {
        var text = WebUtility.HtmlDecode(value);
        if (preserve) return text;
        return Whitespace.Replace(text, " ");
    }
}
