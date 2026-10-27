using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Kickl.Core.Util;

namespace Kickl.Core.Games;

public sealed class GameCardException(string message) : Exception(message);

/// <summary>
/// A game's card: the small record the registry stores and every computer sees in its arcade. It names the game, says what its file is
/// (the SHA-256 and size), and carries the publisher's public key and an ML-DSA-65 signature over all of it. The registry never holds a
/// game file; this card is how a computer that has never seen the game knows exactly which bytes to accept from whichever computer
/// offers them. Verify every card you receive: the registry cannot forge one, and nobody can change one without the publisher's key.
/// </summary>
public sealed partial class GameCard
{
    public const int MaxGameBytes = 2 * 1024 * 1024;

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,39}$")] private static partial Regex IdPattern();
    [GeneratedRegex("^#[0-9a-fA-F]{6}$")] private static partial Regex ColorPattern();
    [GeneratedRegex("^[0-9a-f]{64}$")] private static partial Regex Sha256Pattern();

    public int V { get; set; } = 1;
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Tagline { get; set; } = "";
    public string Author { get; set; } = "";
    public string Accent { get; set; } = "#7be0ff";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
    public int Version { get; set; } = 1;
    public string PublisherKey { get; set; } = "";
    public string PublisherId { get; set; } = "";
    public long Created { get; set; }
    public string Sig { get; set; } = "";

    /// <summary>How many computers the registry currently lists as holding this game. Not signed: it is the registry's own count.</summary>
    [JsonPropertyName("sources")]
    public int Sources { get; set; }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static string PublisherIdOf(byte[] publicKey) => Convert.ToHexString(SHA256.HashData(publicKey)).ToLowerInvariant()[..16];

    private static string F(string value) => value.Length + ":" + value;

    /// <summary>The exact bytes that are signed. Every field is length-prefixed, so no two different cards can produce the same text.</summary>
    public byte[] Canonical() => Encoding.UTF8.GetBytes("kickl/v1/game-card|" + F(Id) + F(Title) + F(Tagline) + F(Author) + F(Accent)
        + F(Sha256) + F(Size.ToString()) + F(Version.ToString()) + F(PublisherId) + F(Created.ToString()));

    public static GameCard Create(MLDsa publisher, string id, string title, string tagline, string author, string accent,
        byte[] wasm, int version, DateTimeOffset now)
    {
        var pub = publisher.ExportMLDsaPublicKey();
        var card = new GameCard
        {
            Id = id, Title = title, Tagline = tagline, Author = author, Accent = accent.ToLowerInvariant(),
            Sha256 = Convert.ToHexString(SHA256.HashData(wasm)).ToLowerInvariant(),
            Size = wasm.Length, Version = version,
            PublisherKey = B64u.Encode(pub), PublisherId = PublisherIdOf(pub), Created = now.ToUnixTimeSeconds()
        };
        card.Sig = B64u.Encode(publisher.SignData(card.Canonical()));
        card.CheckShape();
        return card;
    }

    /// <summary>The same limits the registry enforces. Throws <see cref="GameCardException"/> for a card that could not be published or trusted.</summary>
    public void CheckShape()
    {
        if (V != 1) throw new GameCardException("Only game card version 1 is supported.");
        if (!IdPattern().IsMatch(Id)) throw new GameCardException("The game id must be 1-40 lowercase letters, digits or hyphens.");
        CheckText(Title, 1, 40, "title");
        CheckText(Tagline, 0, 90, "tagline");
        CheckText(Author, 1, 24, "author");
        if (!ColorPattern().IsMatch(Accent)) throw new GameCardException("The accent colour must look like #7be0ff.");
        if (!Sha256Pattern().IsMatch(Sha256)) throw new GameCardException("The card's SHA-256 is malformed.");
        if (Size is < 8 or > MaxGameBytes) throw new GameCardException("A game must be between 8 bytes and 2 MiB.");
        if (Version is < 1 or > 1_000_000) throw new GameCardException("The card's version is out of range.");
        if (PublisherKey.Length is < 100 or > 4000 || Sig.Length is < 3000 or > 6000)
            throw new GameCardException("The card's key or signature has the wrong size.");
    }

    private static void CheckText(string value, int min, int max, string name)
    {
        if (value.Length < min || value.Length > max) throw new GameCardException($"The card's {name} must be {min}-{max} characters.");
        if (value.Any(char.IsControl)) throw new GameCardException($"The card's {name} contains control characters.");
    }

    /// <summary>
    /// Is this card genuine? It must be well formed, its publisher id must be the hash of the key it carries, and its signature must verify.
    /// A card that fails is never shown, never stored and never used to accept a download.
    /// </summary>
    public bool TryVerify(out string reason)
    {
        try
        {
            CheckShape();
            var pub = B64u.Decode(PublisherKey);
            if (!string.Equals(PublisherIdOf(pub), PublisherId, StringComparison.Ordinal))
            {
                reason = "The publisher id does not match the publisher key.";
                return false;
            }
            using var key = MLDsa.ImportMLDsaPublicKey(MLDsaAlgorithm.MLDsa65, pub);
            if (!key.VerifyData(Canonical(), B64u.Decode(Sig)))
            {
                reason = "The signature does not verify.";
                return false;
            }
            reason = "";
            return true;
        }
        catch (Exception ex) when (ex is GameCardException or FormatException or CryptographicException or ArgumentException)
        {
            reason = ex is GameCardException ? ex.Message : "The card is malformed.";
            return false;
        }
    }

    /// <summary>Does this exact game file match the card?</summary>
    public bool Matches(byte[] wasm) =>
        wasm.Length == Size && string.Equals(Convert.ToHexString(SHA256.HashData(wasm)).ToLowerInvariant(), Sha256, StringComparison.Ordinal);

    /// <summary>What to call the publisher on screen: short enough to read out, long enough to tell publishers apart.</summary>
    public string PublisherShort => PublisherId.Length >= 8 ? PublisherId[..8] : PublisherId;

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static GameCard? TryParse(string json)
    {
        try { return JsonSerializer.Deserialize<GameCard>(json, Json); }
        catch (JsonException) { return null; }
    }

    public static GameCard? FromElement(JsonElement element)
    {
        try { return element.Deserialize<GameCard>(Json); }
        catch (JsonException) { return null; }
    }
}
