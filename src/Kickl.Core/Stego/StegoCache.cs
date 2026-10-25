using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kickl.Core.Crypto;
using Kickl.Core.Privacy;
using Kickl.Core.Util;

namespace Kickl.Core.Stego;

/// <summary>Everything needed to restore a cached page, sealed inside the carrier.</summary>
public sealed class CacheEnvelope
{
    public int V { get; set; } = 1;
    public string Site { get; set; } = "";
    public string Path { get; set; } = "/";
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public string CreatedUtc { get; set; } = "";
}

/// <summary>
/// Session-bound steganographic cache. The carrier is a generated PNG with the encrypted
/// envelope hidden in keyed LSB order. Every save uses a fresh seed, so the ciphertext and
/// the image change even when the page is identical ("polymorphic" re-seal).
/// The permutation and payload keys are derived from the live session secret; without the
/// session the carrier is just an image.
/// </summary>
public sealed class StegoCache
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private readonly string _root;

    public StegoCache(string root)
    {
        _root = root;
        Directory.CreateDirectory(root);
    }

    public string Root => _root;

    private static byte[] Subkey(SessionSecret session, byte[] seed, string info) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, session.Key.ToArray(), 32, seed, Utf8.B(info));

    public string Store(SessionSecret session, CacheEnvelope envelope, string? path = null)
    {
        var seed = RandomNumberGenerator.GetBytes(32);
        var payloadKey = Subkey(session, seed, "kickl/v1/stego-payload");
        var permKey = Subkey(session, seed, "kickl/v1/stego-permutation");
        var plain = JsonSerializer.SerializeToUtf8Bytes(envelope, Json);
        var payload = Array.Empty<byte>();

        try
        {
            var (nonce, ciphertext) = Aead.Seal(payloadKey, plain, Utf8.B("kickl/v1/stego"));
            payload = new byte[nonce.Length + ciphertext.Length];
            nonce.CopyTo(payload, 0);
            ciphertext.CopyTo(payload, nonce.Length);

            var rgb = CarrierImage(seed, envelope.Site);
            var png = PngCarrier.Write(512, 384, rgb,
                new Dictionary<string, string> { [PngCarrier.SeedKey] = Convert.ToHexString(seed).ToLowerInvariant() });
            png = PngCarrier.Embed(png, payload, permKey);

            path ??= Path.Combine(_root, Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant() + ".png");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllBytes(path, png);
            return path;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payloadKey);
            CryptographicOperations.ZeroMemory(permKey);
            CryptographicOperations.ZeroMemory(plain);
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    /// <summary>
    /// Extract a cached page. With refresh=true (the default) the carrier is immediately
    /// re-sealed with a new seed, so the on-disk image changes on every cache request.
    /// </summary>
    public CacheEnvelope Load(SessionSecret session, string path, bool refresh = true)
    {
        var envelope = Read(session, path);
        if (refresh) Store(session, envelope, path);
        return envelope;
    }

    public CacheEnvelope Read(SessionSecret session, string path)
    {
        var png = File.ReadAllBytes(path);
        var image = PngCarrier.Read(png);
        if (!image.Text.TryGetValue(PngCarrier.SeedKey, out var seedHex))
            throw new StegoException("The carrier has no Kickl cache seed.");

        var seed = Convert.FromHexString(seedHex);
        var payloadKey = Subkey(session, seed, "kickl/v1/stego-payload");
        var permKey = Subkey(session, seed, "kickl/v1/stego-permutation");
        byte[] payload = [];

        try
        {
            payload = PngCarrier.Extract(png, permKey);
            if (payload.Length < Aead.NonceSize + Aead.TagSize)
                throw new StegoException("The cache payload is too short.");

            var nonce = payload.AsSpan(0, Aead.NonceSize);
            var ciphertext = payload.AsSpan(Aead.NonceSize);
            var plain = Aead.Open(payloadKey, nonce, ciphertext, Utf8.B("kickl/v1/stego"));
            try
            {
                return JsonSerializer.Deserialize<CacheEnvelope>(plain, Json)
                       ?? throw new StegoException("The cache envelope is unreadable.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payloadKey);
            CryptographicOperations.ZeroMemory(permKey);
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    public static string CarrierId() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <summary>Deterministic abstract page card; the real shell replaces this with a bitmap render.</summary>
    private static byte[] CarrierImage(byte[] seed, string site)
    {
        const int width = 512, height = 384;
        var input = new byte[seed.Length + Encoding.UTF8.GetByteCount(site)];
        seed.CopyTo(input, 0);
        Encoding.UTF8.GetBytes(site).CopyTo(input, seed.Length);
        var hash = SHA256.HashData(input);
        var rgb = new byte[width * height * 3];
        var band = 28 + hash[0] % 52;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 3;
                var r = (byte)((x * 255 / width + hash[1]) & 0xFF);
                var g = (byte)((y * 255 / height + hash[2]) & 0xFF);
                var b = (byte)(((x * y) / 31 + hash[3]) & 0xFF);

                if (((x / band) + (y / band) + hash[4]) % 3 == 0)
                {
                    r = (byte)(r / 3 + 32);
                    g = (byte)(g / 2 + 48);
                    b = (byte)(b / 4 + 64);
                }

                rgb[i] = r;
                rgb[i + 1] = g;
                rgb[i + 2] = b;
            }
        }

        return rgb;
    }
}
