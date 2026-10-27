using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kickl.Core.Mail;
using Kickl.Core.Session;
using Kickl.Core.Util;

namespace Kickl.Core.Games;

/// <summary>A game in the registry that this computer does not have (or has an older version of).</summary>
public sealed record RemoteGame(GameCard Card, bool IsUpdate);

/// <summary>
/// Sharing games between computers. The registry stores <b>cards</b> (signed descriptions) and a list of computers that can serve each game;
/// the game files themselves travel directly between computers and are checked against the card's SHA-256. This class is what a profile
/// does with that: refresh the cards, publish its own games, download a game it does not have, and serve (and announce) the games it holds,
/// so every computer that downloads a game becomes another place to get it.
/// </summary>
public sealed class GameSharing : IDisposable
{
    private readonly BrowserSession _session;
    private readonly GameRegistryClient _registry;
    private readonly object _gate = new();
    private readonly Dictionary<string, GameCard> _cards = new(StringComparer.Ordinal);   // publisherId/id -> newest verified card
    private long _cursor;
    private GameSeedServer? _seed;
    private X509Certificate2? _seedCert;
    private string? _seedEndpoint;
    private Func<IReadOnlyList<GameEntry>>? _library;

    public GameSharing(BrowserSession session, GameRegistryClient registry)
    {
        _session = session;
        _registry = registry;
        LoadCardCache();
    }

    /// <summary>Downloaded games go in the reader's own games folder, inside their data folder.</summary>
    public string GamesRoot => Path.Combine(_session.Paths.Root, GameLibrary.FolderName);

    public IReadOnlyList<GameCard> Cards
    {
        get { lock (_gate) return _cards.Values.OrderBy(c => c.Title, StringComparer.OrdinalIgnoreCase).ToList(); }
    }

    public string? SeedEndpoint => _seedEndpoint;
    public bool IsSeeding => _seed is { IsRunning: true };

    // ---------------------------------------------------------------- the cards

    private string CachePath => Path.Combine(_session.Paths.Root, "shared", "game-cards.json");

    private void LoadCardCache()
    {
        try
        {
            if (!File.Exists(CachePath)) return;
            var root = JsonNode.Parse(File.ReadAllText(CachePath));
            _cursor = root?["next"]?.GetValue<long>() ?? 0;
            foreach (var node in root?["cards"]?.AsArray() ?? [])
            {
                // the cache is only a convenience: everything in it is verified again, so a tampered file shows nothing
                if (node is not null && GameCard.TryParse(node.ToJsonString()) is { } card && card.TryVerify(out _)) Remember(card);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException) { }
    }

    private void SaveCardCache()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            JsonArray array;
            lock (_gate) array = new JsonArray(_cards.Values.Select(c => (JsonNode) JsonNode.Parse(c.ToJson())!).ToArray());
            File.WriteAllText(CachePath, new JsonObject { ["next"] = _cursor, ["cards"] = array }.ToJsonString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private bool Remember(GameCard card)
    {
        lock (_gate)
        {
            var key = card.PublisherId + "/" + card.Id;
            if (_cards.TryGetValue(key, out var have) && have.Version >= card.Version && have.Sha256 == card.Sha256)
            {
                have.Sources = card.Sources;   // the registry's count moves even when the card does not
                return false;
            }
            if (have is not null && have.Version > card.Version) return false;
            _cards[key] = card;
            return true;
        }
    }

    /// <summary>Ask the registry for cards that are new since last time. Returns how many cards were added or changed.</summary>
    public async Task<int> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var changed = 0;
        for (var page = 0; page < 10; page++)
        {
            var (cards, next, more, _) = await _registry.ListAsync(_cursor, 100, cancellationToken).ConfigureAwait(false);
            foreach (var card in cards) if (Remember(card)) changed++;
            _cursor = Math.Max(_cursor, next);
            if (!more) break;
        }
        SaveCardCache();
        return changed;
    }

    /// <summary>Games the registry lists that this computer does not hold, or holds an older version of.</summary>
    public List<RemoteGame> Missing(IReadOnlyList<GameEntry> local)
    {
        var held = new HashSet<string>(local.Where(g => g.Error is null).Select(GameLibrary.Sha256Of), StringComparer.Ordinal);
        var result = new List<RemoteGame>();
        foreach (var card in Cards)
        {
            if (held.Contains(card.Sha256)) continue;
            var mine = local.FirstOrDefault(g => g.Card is { } c && c.PublisherId == card.PublisherId && c.Id == card.Id);
            if (mine?.Card is { } have && have.Version >= card.Version) continue;
            result.Add(new RemoteGame(card, mine is not null));
        }
        return result;
    }

    // ---------------------------------------------------------------- publishing

    private string PublisherPath => Path.Combine(_session.Paths.ProfileRoot, "private", "game-publisher.kpf");

    /// <summary>This profile's publisher key, made on first use. It signs the cards of games this profile publishes; keep the profile to keep the games.</summary>
    public (MLDsa Key, string Token) Publisher()
    {
        if (File.Exists(PublisherPath))
        {
            var plain = _session.OpenProfileData("game-publisher", File.ReadAllBytes(PublisherPath));
            try
            {
                var node = JsonNode.Parse(plain)!;
                return (MLDsa.ImportPkcs8PrivateKey(Convert.FromBase64String((string) node["pkcs8"]!)), (string) node["token"]!);
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }

        var key = MLDsa.GenerateKey(MLDsaAlgorithm.MLDsa65);
        var token = B64u.Encode(RandomNumberGenerator.GetBytes(32));
        var pkcs8 = key.ExportPkcs8PrivateKey();
        var json = System.Text.Encoding.UTF8.GetBytes(new JsonObject { ["pkcs8"] = Convert.ToBase64String(pkcs8), ["token"] = token }.ToJsonString());
        Directory.CreateDirectory(Path.GetDirectoryName(PublisherPath)!);
        File.WriteAllBytes(PublisherPath, _session.ProtectProfileData("game-publisher", json));
        FileProtection.Restrict(PublisherPath);
        CryptographicOperations.ZeroMemory(json);
        CryptographicOperations.ZeroMemory(pkcs8);
        return (key, token);
    }

    /// <summary>The id that identifies this profile as a publisher (a short hash of its public key). Games published by it are "yours".</summary>
    public string MyPublisherId()
    {
        var (key, _) = Publisher();
        using (key) return GameCard.PublisherIdOf(key.ExportMLDsaPublicKey());
    }

    /// <summary>Did this profile publish this game? (Its signed card was made with this profile's publisher key.)</summary>
    public bool IsMine(GameEntry game, string myPublisherId) => game.Card is { } card && card.PublisherId == myPublisherId;

    /// <summary>
    /// Stop sharing a game you published: remove its card from the registry (only the publisher can) and from the game's folder. The game stays on this
    /// computer, and on any computer that already downloaded it; it just stops being offered to new ones. Publishing it again makes a new card.
    /// </summary>
    public async Task UnpublishAsync(GameEntry game, CancellationToken cancellationToken = default)
    {
        var (key, token) = Publisher();
        using (key)
        {
            var publisherId = GameCard.PublisherIdOf(key.ExportMLDsaPublicKey());
            if (game.Card is null || game.Card.PublisherId != publisherId) throw new GameCardException("This game was not published by this profile.");
            await _registry.RemoveAsync(publisherId, game.Card.Id, TokenHash(token), cancellationToken).ConfigureAwait(false);
            lock (_gate) _cards.Remove(publisherId + "/" + game.Card.Id);
            SaveCardCache();
            var path = Path.Combine(game.Folder, GameLibrary.MetaFile);
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject meta)
            {
                meta.Remove("card");
                File.WriteAllText(path, meta.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
        }
    }

    public static string TokenHash(string token) => Hashing.Hex(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));

    /// <summary>
    /// Publish a game from the games folder: sign a card for its file and send it to the registry, keep the card beside the game, and announce
    /// this computer as a place to get it. Publishing again after the file changed raises the card's version.
    /// </summary>
    public async Task<GameCard> PublishAsync(GameEntry entry, CancellationToken cancellationToken = default)
    {
        var wasm = GameLibrary.ReadModule(entry);
        var (key, token) = Publisher();
        using (key)
        {
            var publisherId = GameCard.PublisherIdOf(key.ExportMLDsaPublicKey());
            var sha = Convert.ToHexString(SHA256.HashData(wasm)).ToLowerInvariant();
            GameCard? existing;
            lock (_gate) existing = _cards.GetValueOrDefault(publisherId + "/" + entry.Id);
            existing ??= entry.Card is { } c && c.PublisherId == publisherId ? c : null;

            var card = existing is not null && existing.Sha256 == sha
                ? existing
                : GameCard.Create(key, entry.Id, entry.Title, entry.Tagline, entry.Author, entry.Accent, wasm, (existing?.Version ?? 0) + 1, DateTimeOffset.UtcNow);

            await _registry.PublishAsync(card, TokenHash(token), cancellationToken).ConfigureAwait(false);
            Remember(card);
            SaveCardCache();
            KeepCardBesideGame(entry, card);
            if (_seedEndpoint is not null)
                await _registry.AnnounceAsync(card.Sha256, _seedEndpoint, cancellationToken).ConfigureAwait(false);
            return card;
        }
    }

    private static void KeepCardBesideGame(GameEntry entry, GameCard card)
    {
        var path = Path.Combine(entry.Folder, GameLibrary.MetaFile);
        var node = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject() : new JsonObject();
        var clean = GameCard.TryParse(card.ToJson())!;
        clean.Sources = 0;
        node["card"] = JsonNode.Parse(clean.ToJson());
        File.WriteAllText(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    // ---------------------------------------------------------------- downloading

    /// <summary>
    /// Get a game this computer does not have: ask the registry who can serve it, fetch it from the first that can (checking it against the
    /// signed card), install it into the games folder, and announce this computer as another place to get it.
    /// </summary>
    public async Task<GameEntry> DownloadAsync(GameCard card, Action<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!card.TryVerify(out var reason)) throw new GameCardException("This card is not genuine: " + reason);
        progress?.Invoke("looking for computers that have it …");
        var sources = await _registry.SourcesAsync(card.Sha256, cancellationToken).ConfigureAwait(false);
        // never fetch from ourselves
        sources.RemoveAll(s => s == _seedEndpoint);
        var wasm = await GameDownloader.FetchAsync(card, sources, progress, cancellationToken).ConfigureAwait(false);
        progress?.Invoke("checking and installing …");
        var entry = GameLibrary.Install(GamesRoot, card, wasm);
        try
        {
            if (_seedEndpoint is not null) await _registry.AnnounceAsync(card.Sha256, _seedEndpoint, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is GameRegistryException or HttpRequestException or TaskCanceledException) { /* the game is installed; announcing can wait */ }
        return entry;
    }

    // ---------------------------------------------------------------- serving what this computer holds

    private string SeedPath => Path.Combine(_session.Paths.ProfileRoot, "private", "game-seed.kpf");

    /// <summary>
    /// Start serving the games this computer holds. <paramref name="library"/> says what the games are. The key and port are made once and kept in
    /// the profile, so this computer's address stays the same between runs. Returns false when it could not start (for example, no free port).
    /// </summary>
    public bool StartSeeding(Func<IReadOnlyList<GameEntry>> library)
    {
        StopSeeding();
        _library = library;
        try
        {
            int port;
            X509Certificate2 cert;
            if (File.Exists(SeedPath))
            {
                var plain = _session.OpenProfileData("game-seed", File.ReadAllBytes(SeedPath));
                try
                {
                    var node = JsonNode.Parse(plain)!;
                    port = (int) node["port"]!;
                    cert = MailTls.Import(Convert.FromBase64String((string) node["pkcs12"]!));
                }
                finally { CryptographicOperations.ZeroMemory(plain); }
            }
            else
            {
                port = MailEndpoint.PickFreePort();
                cert = MailTls.CreateSelfSigned();
                Save(port, cert);
            }

            GameSeedServer server;
            try
            {
                server = new GameSeedServer(cert, port, ServeByHash);
                server.Start();
            }
            catch (System.Net.Sockets.SocketException)
            {
                // the saved port is taken by something else: pick another and keep it
                port = MailEndpoint.PickFreePort();
                Save(port, cert);
                server = new GameSeedServer(cert, port, ServeByHash);
                server.Start();
            }

            _seedCert = cert;
            _seed = server;
            _seedEndpoint = MailEndpoint.WithPin(MailEndpoint.LocalBaseOn(server.Port), MailTls.PinOf(cert));
            return true;
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or CryptographicException or IOException or JsonException or FormatException)
        {
            return false;
        }

        void Save(int p, X509Certificate2 c)
        {
            var json = System.Text.Encoding.UTF8.GetBytes(new JsonObject { ["port"] = p, ["pkcs12"] = Convert.ToBase64String(MailTls.Export(c)) }.ToJsonString());
            Directory.CreateDirectory(Path.GetDirectoryName(SeedPath)!);
            File.WriteAllBytes(SeedPath, _session.ProtectProfileData("game-seed", json));
            FileProtection.Restrict(SeedPath);
            CryptographicOperations.ZeroMemory(json);
        }
    }

    private byte[]? ServeByHash(string sha)
    {
        var entries = _library?.Invoke();
        if (entries is null) return null;
        var game = GameLibrary.FindBySha(entries, sha);
        if (game is null) return null;
        try { return GameLibrary.ReadModule(game); }
        catch (Exception ex) when (ex is IOException or GameRejectedException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Tell the registry that this computer can serve each game it holds that has a card. Safe to repeat; do it now and then.</summary>
    public async Task<int> AnnounceAllAsync(CancellationToken cancellationToken = default)
    {
        if (_seedEndpoint is null || _library is null) return 0;
        var known = Cards.Select(c => c.Sha256).ToHashSet(StringComparer.Ordinal);
        var announced = 0;
        foreach (var game in _library().Where(g => g.Error is null))
        {
            var sha = GameLibrary.Sha256Of(game);
            if (!known.Contains(sha)) continue;
            try
            {
                await _registry.AnnounceAsync(sha, _seedEndpoint, cancellationToken).ConfigureAwait(false);
                announced++;
            }
            catch (Exception ex) when (ex is GameRegistryException or HttpRequestException or TaskCanceledException) { }
        }
        return announced;
    }

    public void StopSeeding()
    {
        _seed?.Dispose();
        _seed = null;
        _seedCert?.Dispose();
        _seedCert = null;
        _seedEndpoint = null;
    }

    public void Dispose() => StopSeeding();
}
