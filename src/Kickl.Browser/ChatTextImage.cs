using SkiaSharp;

namespace Kickl.Browser;

/// <summary>
/// FreeChat messages are PNGs, not plain text. Match the web widget's 240 px / 14 px layout so
/// native and browser members read the same room.
/// </summary>
internal static class ChatTextImage
{
    private const int MaxTextWidth = 220;
    private const int LineHeight = 19;
    private const int Padding = 10;
    private const int Width = 240;

    public static byte[] Render(string text)
    {
        using var typeface = SKTypeface.FromFamilyName("Segoe UI") ?? SKTypeface.Default;
        using var font = new SKFont(typeface, 14);
        using var paint = new SKPaint
        {
            Color = SKColor.Parse("#eaf6ff"),
            IsAntialias = true
        };

        var lines = Wrap(text, font);
        var height = lines.Count * LineHeight + 16;
        using var bitmap = new SKBitmap(Width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColor.Parse("#12141f"));

        for (var i = 0; i < lines.Count; i++)
            canvas.DrawText(lines[i], Padding, 20 + i * LineHeight, SKTextAlign.Left, font, paint);

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static List<string> Wrap(string text, SKFont font)
    {
        var lines = new List<string>();
        var current = "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = current.Length == 0 ? word : current + " " + word;
            if (font.MeasureText(candidate) > MaxTextWidth && current.Length > 0)
            {
                lines.Add(current);
                current = word;
            }
            else
            {
                current = candidate;
            }
        }
        if (current.Length > 0) lines.Add(current);
        if (lines.Count == 0) lines.Add(" ");
        return lines;
    }
}
