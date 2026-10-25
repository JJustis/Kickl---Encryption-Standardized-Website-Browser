using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Kickl.Core.Mail;

/// <summary>
/// A mail image is a normal PNG with the sealed message bytes stored in a private
/// <c>kMLm</c> chunk. Nothing in the pixels is readable, and the payload is not a
/// base64 text field: open the image, read the encrypted blob, decrypt it.
/// </summary>
public static class MailImage
{
    public const int MaxPayloadBytes = 4 * 1024 * 1024;
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly byte[] ChunkType = "kMLm"u8.ToArray();
    private static readonly byte[] Iend = "IEND"u8.ToArray();

    /// <summary>A dark, generic cover for a newly composed message.</summary>
    public static byte[] CreateCover(int width = 640, int height = 200)
    {
        if (width < 1 || height < 1 || width > 4096 || height > 4096)
            throw new ArgumentOutOfRangeException(nameof(width));

        var stride = 1 + width * 4;
        var raw = new byte[height * stride];
        for (var y = 0; y < height; y++)
        {
            var row = y * stride;
            raw[row] = 0; // no filter
            for (var x = 0; x < width; x++)
            {
                var p = row + 1 + x * 4;
                var shade = (byte) (8 + (y * 18 / height));
                raw[p] = shade;
                raw[p + 1] = (byte) (14 + (y * 22 / height));
                raw[p + 2] = (byte) (24 + (y * 30 / height));
                raw[p + 3] = 255;
            }
        }

        // A thin accent rule and a centred seal-like square, drawn in pixels.
        var lineY = height * 2 / 3;
        for (var x = 28; x < width - 28; x++) SetPixel(raw, stride, x, lineY, 88, 166, 255, 210);
        var cx = width / 2;
        var cy = height / 2 - 8;
        for (var y = -26; y <= 26; y++)
        for (var x = -26; x <= 26; x++)
            if (Math.Abs(x) + Math.Abs(y) <= 30)
                SetPixel(raw, stride, cx + x, cy + y, 242, 204, 96, 235);

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(raw, 0, raw.Length);

        using var png = new MemoryStream();
        png.Write(Signature);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, 4), height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 6;  // RGBA
        ihdr[10] = 0;
        ihdr[11] = 0;
        ihdr[12] = 0;
        WriteChunk(png, "IHDR"u8, ihdr);
        WriteChunk(png, "IDAT"u8, compressed.ToArray());
        WriteChunk(png, Iend, []);
        return png.ToArray();
    }

    /// <summary>Insert the sealed payload before IEND. The cover pixels are preserved.</summary>
    public static byte[] Attach(ReadOnlySpan<byte> coverPng, ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0) throw new ArgumentException("The mail payload is empty.", nameof(payload));
        if (payload.Length > MaxPayloadBytes)
            throw new ArgumentException("The mail payload is too large for an image.", nameof(payload));
        ValidatePng(coverPng);

        using var output = new MemoryStream(coverPng.Length + payload.Length + 32);
        output.Write(Signature);
        var offset = Signature.Length;
        while (offset < coverPng.Length)
        {
            if (offset + 12 > coverPng.Length) throw new FormatException("The cover PNG is truncated.");
            var length = BinaryPrimitives.ReadInt32BigEndian(coverPng.Slice(offset, 4));
            if (length < 0 || offset + 12 + length > coverPng.Length)
                throw new FormatException("The cover PNG has an invalid chunk length.");
            var type = coverPng.Slice(offset + 4, 4);
            var total = 12 + length;
            if (type.SequenceEqual(Iend))
                WriteChunk(output, ChunkType, payload);
            output.Write(coverPng.Slice(offset, total));
            offset += total;
        }
        return output.ToArray();
    }

    /// <summary>Read the sealed payload from a mail image.</summary>
    public static byte[] Extract(ReadOnlySpan<byte> png)
    {
        ValidatePng(png);
        var result = new List<byte>();
        var offset = Signature.Length;
        while (offset < png.Length)
        {
            if (offset + 12 > png.Length) throw new FormatException("The mail image is truncated.");
            var length = BinaryPrimitives.ReadInt32BigEndian(png.Slice(offset, 4));
            if (length < 0 || offset + 12 + length > png.Length)
                throw new FormatException("The mail image has an invalid chunk length.");
            var type = png.Slice(offset + 4, 4);
            if (type.SequenceEqual(ChunkType))
                result.AddRange(png.Slice(offset + 8, length).ToArray());
            offset += 12 + length;
        }
        if (result.Count == 0) throw new FormatException("This image does not hold a sealed mail message.");
        if (result.Count > MaxPayloadBytes) throw new FormatException("The sealed mail payload is too large.");
        return [.. result];
    }

    public static bool HasPayload(ReadOnlySpan<byte> png)
    {
        try { return Extract(png).Length > 0; }
        catch (FormatException) { return false; }
    }

    private static void ValidatePng(ReadOnlySpan<byte> png)
    {
        if (png.Length < Signature.Length + 12 || !png[..Signature.Length].SequenceEqual(Signature))
            throw new FormatException("The cover is not a PNG image.");
    }

    private static void SetPixel(byte[] raw, int stride, int x, int y, byte r, byte g, byte b, byte a)
    {
        if (x < 0 || y < 0) return;
        var width = (stride - 1) / 4;
        if (x >= width || y >= raw.Length / stride) return;
        var p = y * stride + 1 + x * 4;
        raw[p] = r;
        raw[p + 1] = g;
        raw[p + 2] = b;
        raw[p + 3] = a;
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);
        output.Write(type);
        output.Write(data);

        var crc = Crc32(type, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        output.Write(crcBytes);
    }

    private static uint Crc32(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in type) crc = _table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        foreach (var b in data) crc = _table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }

    private static readonly uint[] _table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (var n = 0u; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}
