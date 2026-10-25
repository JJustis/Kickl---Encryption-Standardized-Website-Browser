using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Kickl.Core.Stego;

public sealed class StegoException(string message) : Exception(message);

/// <summary>
/// Minimal lossless PNG carrier (RGB8, filter 0) plus keyed LSB embedding.
/// PNG is deliberate: JPEG's lossy transform destroys LSB payloads. A JPEG carrier would
/// need DCT-coefficient embedding and is out of scope for this slice.
/// </summary>
public static class PngCarrier
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];
    public const string SeedKey = "kickl-seed";
    private static readonly byte[] Magic = "KBST"u8.ToArray();
    private const byte Version = 1;
    private const int HeaderSize = 4 + 1 + 4 + 16;

    public sealed record Image(int Width, int Height, byte[] Rgb, Dictionary<string, string> Text);

    public static byte[] Write(int width, int height, byte[] rgb, IReadOnlyDictionary<string, string>? text = null)
    {
        if (width <= 0 || height <= 0) throw new ArgumentException("Image dimensions must be positive.");
        if (rgb.Length != width * height * 3) throw new ArgumentException("RGB buffer length does not match dimensions.");

        using var output = new MemoryStream();
        output.Write(Signature);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 2;  // truecolor RGB
        ihdr[10] = 0; // deflate
        ihdr[11] = 0; // adaptive filtering (we write filter 0)
        ihdr[12] = 0; // no interlace
        WriteChunk(output, "IHDR", ihdr);

        if (text is not null)
        {
            foreach (var (key, value) in text)
            {
                var data = Encoding.UTF8.GetBytes(key + "\0" + value);
                WriteChunk(output, "tEXt", data);
            }
        }

        var raw = new byte[height * (1 + width * 3)];
        var offset = 0;
        for (var y = 0; y < height; y++)
        {
            raw[offset++] = 0; // filter None
            Buffer.BlockCopy(rgb, y * width * 3, raw, offset, width * 3);
            offset += width * 3;
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(raw);
        WriteChunk(output, "IDAT", compressed.ToArray());
        WriteChunk(output, "IEND", []);

        return output.ToArray();
    }

    public static Image Read(byte[] png)
    {
        if (png.Length < Signature.Length + 25) throw new StegoException("Not a PNG file.");
        if (!png.AsSpan(0, Signature.Length).SequenceEqual(Signature)) throw new StegoException("Bad PNG signature.");

        var pos = Signature.Length;
        var width = 0;
        var height = 0;
        var sawHeader = false;
        using var idat = new MemoryStream();
        var text = new Dictionary<string, string>(StringComparer.Ordinal);

        while (pos + 12 <= png.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(pos));
            if (length < 0 || pos + 12 + length > png.Length) throw new StegoException("Corrupt PNG chunk.");
            var type = Encoding.ASCII.GetString(png, pos + 4, 4);
            var data = png.AsSpan(pos + 8, length);
            var expectedCrc = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(pos + 8 + length));
            if (Crc32(png.AsSpan(pos + 4, 4 + length)) != expectedCrc)
                throw new StegoException("PNG chunk CRC mismatch.");

            switch (type)
            {
                case "IHDR":
                    if (length != 13) throw new StegoException("Bad IHDR.");
                    width = BinaryPrimitives.ReadInt32BigEndian(data);
                    height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                    if (data[8] != 8 || data[9] != 2 || data[12] != 0)
                        throw new StegoException("Only RGB8 non-interlaced PNG is supported.");
                    sawHeader = true;
                    break;
                case "IDAT":
                    idat.Write(data);
                    break;
                case "tEXt":
                {
                    var bytes = data.ToArray();
                    var zero = Array.IndexOf(bytes, (byte)0);
                    if (zero > 0)
                    {
                        var key = Encoding.UTF8.GetString(bytes, 0, zero);
                        var value = Encoding.UTF8.GetString(bytes, zero + 1, bytes.Length - zero - 1);
                        text[key] = value;
                    }
                    break;
                }
            }

            pos += 12 + length;
            if (type == "IEND") break;
        }

        if (!sawHeader || width <= 0 || height <= 0) throw new StegoException("Missing PNG header.");

        idat.Position = 0;
        using var inflate = new ZLibStream(idat, CompressionMode.Decompress);
        using var rawStream = new MemoryStream();
        inflate.CopyTo(rawStream);
        var raw = rawStream.ToArray();

        var stride = 1 + width * 3;
        if (raw.Length != stride * height) throw new StegoException("Unexpected PNG data length.");

        var rgb = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            if (raw[y * stride] != 0) throw new StegoException("Only filter 0 is supported.");
            Buffer.BlockCopy(raw, y * stride + 1, rgb, y * width * 3, width * 3);
        }

        return new Image(width, height, rgb, text);
    }

    /// <summary>Embed magic + length + SHA-256 prefix + payload into RGB LSBs in keyed order.</summary>
    public static byte[] Embed(byte[] png, byte[] payload, byte[] permutationKey)
    {
        var image = Read(png);
        var frame = new byte[HeaderSize + payload.Length];
        Magic.CopyTo(frame, 0);
        frame[4] = Version;
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(5), payload.Length);
        SHA256.HashData(payload).AsSpan(0, 16).CopyTo(frame.AsSpan(9));
        payload.CopyTo(frame, HeaderSize);

        var capacity = image.Rgb.Length;
        if (frame.Length * 8 > capacity)
            throw new StegoException($"Payload needs {frame.Length * 8} bits; carrier holds {capacity}.");

        var order = Permutation(capacity, permutationKey);
        for (var i = 0; i < frame.Length * 8; i++)
        {
            var bit = (frame[i >> 3] >> (7 - (i & 7))) & 1;
            var at = order[i];
            image.Rgb[at] = (byte)((image.Rgb[at] & 0xFE) | bit);
        }

        return Write(image.Width, image.Height, image.Rgb, image.Text);
    }

    public static byte[] Extract(byte[] png, byte[] permutationKey)
    {
        var image = Read(png);
        var order = Permutation(image.Rgb.Length, permutationKey);

        byte ReadByte(int index)
        {
            var value = 0;
            for (var b = 0; b < 8; b++)
                value = (value << 1) | (image.Rgb[order[index * 8 + b]] & 1);
            return (byte)value;
        }

        if (image.Rgb.Length < HeaderSize * 8) throw new StegoException("Carrier is too small.");
        var header = new byte[HeaderSize];
        for (var i = 0; i < HeaderSize; i++) header[i] = ReadByte(i);

        if (!header.AsSpan(0, 4).SequenceEqual(Magic)) throw new StegoException("No Kickl stego payload in this carrier.");
        if (header[4] != Version) throw new StegoException("Unsupported stego payload version.");

        var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(5));
        if (length < 0 || (HeaderSize + length) * 8 > image.Rgb.Length)
            throw new StegoException("Corrupt stego payload length.");

        var payload = new byte[length];
        for (var i = 0; i < length; i++) payload[i] = ReadByte(HeaderSize + i);

        if (!CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(payload).AsSpan(0, 16), header.AsSpan(9, 16)))
            throw new StegoException("Stego payload checksum failed.");

        return payload;
    }

    /// <summary>Keyed Fisher–Yates permutation driven by HMAC-SHA-256.</summary>
    private static int[] Permutation(int count, byte[] key)
    {
        var order = new int[count];
        for (var i = 0; i < count; i++) order[i] = i;

        using var hmac = new HMACSHA256(key);
        var block = new byte[32];
        var counterBytes = new byte[8];
        var available = 0;
        ulong counter = 0;

        ulong Next()
        {
            if (available == 0)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(counterBytes, counter++);
                if (!hmac.TryComputeHash(counterBytes, block, out _))
                    throw new CryptographicException("HMAC failed.");
                available = 32;
            }
            available -= 8;
            return BinaryPrimitives.ReadUInt64LittleEndian(block.AsSpan(available, 8));
        }

        for (var i = count - 1; i > 0; i--)
        {
            var j = (int)(Next() % (ulong)(i + 1));
            (order[i], order[j]) = (order[j], order[i]);
        }

        return order;
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);

        var typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);

        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typeBytes, data));
        stream.Write(crc);
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }

    private static uint Crc32(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var x in a) crc = CrcTable[(crc ^ x) & 0xFF] ^ (crc >> 8);
        foreach (var x in b) crc = CrcTable[(crc ^ x) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}
