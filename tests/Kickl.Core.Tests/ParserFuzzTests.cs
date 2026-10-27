using System.Diagnostics;
using System.Text;
using Kickl.Core.Mail;
using Kickl.Core.Markup;
using Kickl.Core.News;
using Kickl.Core.Ownership;
using Kickl.Core.Registry;
using Xunit;

namespace Kickl.Core.Tests;

/// <summary>
/// Everything a stranger can hand the browser as text or JSON: a page, a feed, a registry answer, a mail bundle, a certificate.
/// Mutated and random inputs may be refused, but only with an exception that means "refused"; never a crash, another kind of
/// exception, or a hang.
/// </summary>
public class ParserFuzzTests
{
    private const string Page = "<kml><page title=\"t\"><h1>Hello</h1><p>text <b>bold</b></p></page></kml>";
    private const string Feed = "<?xml version=\"1.0\"?><rss version=\"2.0\"><channel><title>x</title><item><title>a</title><link>https://e.com/1</link><description>d</description></item></channel></rss>";

    private static bool Refusal(Exception ex) => ex is FormatException or System.Text.Json.JsonException or InvalidDataException
        or System.Xml.XmlException or ArgumentException or ClaimException or Markup.KmlException or System.Security.Cryptography.CryptographicException
        or InvalidOperationException or KeyNotFoundException or OverflowException or NotSupportedException;

    private static IEnumerable<byte[]> Inputs(string seed, int count, int salt)
    {
        var rng = new Random(salt);
        var original = Encoding.UTF8.GetBytes(seed);
        for (var i = 0; i < count; i++)
        {
            var b = original.ToArray();
            switch (i % 5)
            {
                case 0: for (var k = 0; k < 1 + i % 6; k++) b[rng.Next(b.Length)] = (byte) rng.Next(256); break;
                case 1: b = b[..rng.Next(b.Length)]; break;
                case 2: b = [.. b, .. Enumerable.Range(0, rng.Next(64)).Select(_ => (byte) rng.Next(256))]; break;
                case 3: { var cut = rng.Next(b.Length); b = [.. b[..cut], .. b[rng.Next(b.Length)..]]; break; }
                default: b = new byte[rng.Next(1, 300)]; rng.NextBytes(b); break;
            }
            yield return b;
        }
    }

    private static void Hammer(string seed, int salt, Action<byte[]> parse, int count = 2000)
    {
        var sw = Stopwatch.StartNew();
        foreach (var input in Inputs(seed, count, salt))
        {
            try { parse(input); }
            catch (Exception ex) when (Refusal(ex)) { }
        }
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(60), "parsing hostile input was too slow");
    }

    [Fact] public void Kml() => Hammer(Page, 1, b => Markup.Kml.Parse(Encoding.UTF8.GetString(b), "x.com"));
    [Fact] public void Rss() => Hammer(Feed, 2, b => NewsFeed.ParseRss(Encoding.UTF8.GetString(b), "src"));
    [Fact] public void RegistryHead() => Hammer("{\"version\":3,\"rootHash\":\"" + new string('a', 64) + "\",\"generatedAt\":1,\"sig\":\"abc\"}", 3, b => Registry.RegistryHead.Parse(b));
    [Fact] public void RegistryDelta() => Hammer("{\"from\":1,\"to\":2,\"records\":[{\"host\":\"a.com\"}],\"head\":{}}", 4, b => Registry.RegistryDelta.Parse(b));
    [Fact] public void RegistrySnapshot() => Hammer("{\"version\":1,\"records\":[],\"head\":{}}", 5, b => Registry.RegistrySnapshot.Parse(b));
    [Fact] public void OwnershipCertFile() => Hammer(Encoding.UTF8.GetString(new OwnershipCert { Site = "a.com", Token = new string('a', 64), Sig = "x" }.ToBytes()), 6, b => OwnershipCert.Parse(b));
    [Fact] public void SealedCertFile() => Hammer(Encoding.UTF8.GetString(SealedCert.Seal(new OwnershipCert { Site = "a.com", Token = new string('a', 64), Sig = "x" }, "a long passphrase")), 7, b => SealedCert.Open(b, "a long passphrase"), count: 30);

    [Fact]
    public void MailBundleAndEnvelope()
    {
        using var a = MailIdentity.Generate("a");
        using var b = MailIdentity.Generate("b");
        var sealedMail = MailCrypto.Seal(a, b.Bundle, "s", "body");
        Hammer(a.Bundle.ToJson(), 8, x => MailIdentityBundle.Parse(Encoding.UTF8.GetString(x)).Validate());
        Hammer(System.Text.Json.JsonSerializer.Serialize(sealedMail.Envelope, MailJson.Options), 9, x => MailEnvelope.Parse(Encoding.UTF8.GetString(x)));
    }
}

public class PngBombTests
{
    private static byte[] BombPng(int width, int height, int zeroBytes)
    {
        // A valid-looking PNG whose IDAT inflates to far more than the header's canvas
        using var idat = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(idat, System.IO.Compression.CompressionLevel.SmallestSize, true))
            z.Write(new byte[zeroBytes]);
        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        void Chunk(string type, byte[] data)
        {
            var len = new byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
            png.Write(len);
            var body = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
            png.Write(body);
            var crc = 0xFFFFFFFFu;
            foreach (var by in body)
            {
                crc ^= by;
                for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
            var crcBytes = new byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(crcBytes, ~crc);
            png.Write(crcBytes);
        }
        var ihdr = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8; ihdr[9] = 2;
        Chunk("IHDR", ihdr);
        Chunk("IDAT", idat.ToArray());
        Chunk("IEND", []);
        return png.ToArray();
    }

    [Fact]
    public void GiantCanvas_IsRefused_BeforeAnyAllocation()
    {
        var png = BombPng(60000, 60000, 100);
        Assert.Throws<Kickl.Core.Stego.StegoException>(() => Kickl.Core.Stego.PngCarrier.Read(png));
        png = BombPng(int.MaxValue, int.MaxValue, 100);
        Assert.Throws<Kickl.Core.Stego.StegoException>(() => Kickl.Core.Stego.PngCarrier.Read(png));
    }

    [Fact]
    public void InflationBeyondTheHeader_IsRefused()
    {
        var png = BombPng(10, 10, 200_000_000);   // ~200 KB compressed claim of a 10x10 picture
        Assert.True(png.Length < 1_000_000);
        Assert.Throws<Kickl.Core.Stego.StegoException>(() => Kickl.Core.Stego.PngCarrier.Read(png));
    }
}
