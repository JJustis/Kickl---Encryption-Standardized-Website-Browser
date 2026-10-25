using SkiaSharp;

namespace Kickl.Browser;

/// <summary>
/// Mail bodies are shown as images, not selectable text. The encrypted blob lives in
/// the stored PNG chunk; this renderer is only for the page the reader explicitly opens.
/// </summary>
internal static class MailBodyImage
{
    private const int Width = 720;
    private const int Padding = 26;
    private const int MaxTextWidth = Width - Padding * 2;
    private const int LineHeight = 18;

    public static byte[] RenderPage(string subject, string pageText, int pageIndex, int pageCount)
    {
        using var typeface = SKTypeface.FromFamilyName("Segoe UI") ?? SKTypeface.Default;
        using var font = new SKFont(typeface, 13);
        using var small = new SKFont(typeface, 10);
        using var paint = new SKPaint { Color = SKColor.Parse("#eaf6ff"), IsAntialias = true };
        using var muted = new SKPaint { Color = SKColor.Parse("#8fa8bd"), IsAntialias = true };
        using var accent = new SKPaint { Color = SKColor.Parse("#f2cc60"), IsAntialias = true };

        var lines = Wrap(pageText, font);
        var headerLines = Wrap(subject, font);
        var height = Padding + 18 + headerLines.Count * 20 + 12 + lines.Count * LineHeight + 34;
        height = Math.Clamp(height, 220, 1600);

        using var bitmap = new SKBitmap(Width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColor.Parse("#0b131c"));

        var y = Padding + 12f;
        canvas.DrawText("KICKLMAIL · EYES ONLY", Padding, y, SKTextAlign.Left, small, accent);
        y += 20;
        foreach (var line in headerLines)
        {
            canvas.DrawText(line, Padding, y, SKTextAlign.Left, font, paint);
            y += 20;
        }
        y += 10;
        canvas.DrawLine(Padding, y, Width - Padding, y, new SKPaint
        {
            Color = SKColor.Parse("#26374a"),
            StrokeWidth = 1
        });
        y += 20;
        foreach (var line in lines)
        {
            if (y > height - 30) break;
            canvas.DrawText(line, Padding, y, SKTextAlign.Left, font, paint);
            y += LineHeight;
        }

        var footer = $"page {pageIndex + 1} / {pageCount}";
        canvas.DrawText(footer, Width - Padding - small.MeasureText(footer), height - 14,
            SKTextAlign.Left, small, muted);
        canvas.DrawText("sealed image · not selectable text", Padding, height - 14,
            SKTextAlign.Left, small, muted);

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static List<string> Wrap(string text, SKFont font)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (paragraph.Length == 0)
            {
                lines.Add("");
                continue;
            }
            var current = "";
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = current.Length == 0 ? word : current + " " + word;
                if (font.MeasureText(candidate) <= MaxTextWidth || current.Length == 0)
                {
                    current = candidate;
                    continue;
                }
                lines.Add(current);
                current = word;
            }
            if (current.Length > 0) lines.Add(current);
        }
        if (lines.Count == 0) lines.Add("");
        return lines;
    }
}
