using System.Security.Cryptography;
using System.Text;
using Kickl.Core.Crypto;
using Kickl.Core.Markup;
using Kickl.Core.Privacy;
using Kickl.Core.Util;

namespace Kickl.Core.Privacy;

public sealed class SealedRegion
{
    public int Index { get; init; }
    public int BlockIndex { get; init; }
    public string Kind { get; init; } = "";
    public int Level { get; init; }
    public byte[] Nonce { get; init; } = [];
    public byte[] Ciphertext { get; init; } = [];
}

/// <summary>
/// A page whose readable text is individually encrypted per region. The renderer shows
/// placeholders until a region is clicked; clicking decrypts only that region, so the
/// screen (and a screen recorder) sees the minimum. Plaintext is held only for displayed
/// regions and is dropped on Dispose.
/// </summary>
public sealed class SealedPage : IDisposable
{
    private readonly byte[] _seed;
    private readonly Dictionary<int, string> _revealed = new();
    private bool _disposed;

    private SealedPage(string site, string title, List<SealedRegion> regions, byte[] seed)
    {
        Site = site;
        Title = title;
        Regions = regions;
        _seed = seed;
    }

    public string Site { get; }
    public string Title { get; }
    public IReadOnlyList<SealedRegion> Regions { get; }
    public int RegionCount => Regions.Count;

    public static SealedPage Seal(PageView page, SessionSecret session)
    {
        var seed = RandomNumberGenerator.GetBytes(32);
        var regionKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, session.Key.ToArray(), 32, seed,
            Utf8.B("kickl/v1/regions"));

        try
        {
            var regions = new List<SealedRegion>();
            var regionIndex = 0;

            for (var blockIndex = 0; blockIndex < page.Blocks.Count; blockIndex++)
            {
                var block = page.Blocks[blockIndex];
                if (block.Text is null) continue;
                if (block.Kind is not (BlockKind.Heading or BlockKind.Paragraph or BlockKind.Code or BlockKind.Quote))
                    continue;

                var aad = Aad(page.Site, regionIndex, block.Kind, block.Level);
                var (nonce, ciphertext) = Aead.Seal(regionKey, Utf8.B(block.Text), aad);
                regions.Add(new SealedRegion
                {
                    Index = regionIndex,
                    BlockIndex = blockIndex,
                    Kind = block.Kind.ToString().ToLowerInvariant(),
                    Level = block.Level,
                    Nonce = nonce,
                    Ciphertext = ciphertext
                });
                regionIndex++;
            }

            return new SealedPage(page.Site, page.Title, regions, seed);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(regionKey);
        }
    }

    private static byte[] Aad(string site, int index, BlockKind kind, int level) =>
        Utf8.B($"kickl/v1/region|{site}|{index}|{kind.ToString().ToLowerInvariant()}|{level}");

    public string Reveal(int regionIndex, SessionSecret session)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var region = Regions.FirstOrDefault(r => r.Index == regionIndex)
                     ?? throw new KeyNotFoundException($"No region {regionIndex}.");

        var regionKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, session.Key.ToArray(), 32, _seed,
            Utf8.B("kickl/v1/regions"));
        try
        {
            var kind = Enum.Parse<BlockKind>(region.Kind, ignoreCase: true);
            var plain = Aead.Open(regionKey, region.Nonce, region.Ciphertext,
                Aad(Site, region.Index, kind, region.Level));
            try
            {
                var text = Encoding.UTF8.GetString(plain);
                _revealed[region.Index] = text;
                return text;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(regionKey);
        }
    }

    public string Render()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"jttp://{Site}/  ·  sealed page ({Regions.Count} regions)");
        sb.AppendLine(Title);
        sb.AppendLine(new string('─', 56));
        foreach (var region in Regions)
        {
            var label = region.Kind;
            if (_revealed.TryGetValue(region.Index, out var text))
            {
                sb.AppendLine($"[ {region.Index + 1} · revealed {label} ]");
                sb.AppendLine(text);
                sb.AppendLine();
            }
            else
            {
                var bar = region.Kind == "heading" ? "████████" : "▒▒▒▒▒▒▒▒▒▒▒▒";
                sb.AppendLine($"[ {region.Index + 1} ] {bar}  sealed {label}  ·  reveal {region.Index}");
            }
        }
        sb.AppendLine();
        sb.Append("plaintext exists only for regions you revealed; the rest is ciphertext");
        return sb.ToString();
    }

    public void Dispose()
    {
        if (_disposed) return;
        CryptographicOperations.ZeroMemory(_seed);
        foreach (var region in Regions) CryptographicOperations.ZeroMemory(region.Ciphertext);
        _revealed.Clear();
        _disposed = true;
    }
}
