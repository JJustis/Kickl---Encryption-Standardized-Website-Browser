using System.Text.Json;
using System.Text.RegularExpressions;

namespace Kickl.Core.Games;

/// <summary>One game found in a games folder.</summary>
public sealed record GameEntry(
    string Id, string Title, string Tagline, string Author, string Accent, int Order,
    string WasmPath, long Bytes, string Folder, string? Error, GameCard? Card = null);

/// <summary>
/// The games folders. A game is a folder named for its id, holding <c>game.wasm</c> and, optionally, <c>game.json</c>:
/// <code>games/crystal-caverns/game.wasm
/// games/crystal-caverns/game.json   {"title":"Crystal Caverns","tagline":"a 3D maze","author":"kickl","accent":"#7be0ff","order":1}</code>
/// The browser lists everything it finds here on its home page, so publishing a game to the arcade is putting a folder in the right place.
/// Scanning reads only the small json files and file sizes; a game's module is read when it is played, and is then loaded with the same
/// limits and the same sandbox as a game on a page. Nothing in a games folder can run, or even be opened, any other way.
/// </summary>
public static partial class GameLibrary
{
    public const string FolderName = "games";
    public const string WasmFile = "game.wasm";
    public const string MetaFile = "game.json";
    private const int MaxMetaBytes = 16 * 1024;   // a few hundred bytes of text, plus a signed card (about 8 KB) when the game came from the registry
    private const int MaxGames = 200;

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,39}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex ColorPattern();

    private static readonly string[] Palette = ["#7be0ff", "#ff7a64", "#f2cc60", "#7ee787", "#d2a8ff", "#ffa657", "#79c0ff", "#ff9bce"];

    /// <summary>
    /// Where games are looked for, most personal first: the reader's own data folder, a folder beside the program, and the
    /// repository's folder when running from a source checkout. A game id found in an earlier folder hides the same id in a later one.
    /// </summary>
    public static IReadOnlyList<string> DefaultRoots(string dataRoot)
    {
        var roots = new List<string> { Path.Combine(dataRoot, FolderName), Path.Combine(AppContext.BaseDirectory, FolderName) };
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Kickl.slnx"))) dir = dir.Parent;
        if (dir is not null) roots.Add(Path.Combine(dir.FullName, FolderName));
        return roots.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Every game in <paramref name="roots"/>. A folder that is not a game (no module, a bad id) is skipped; a game that cannot run is listed with its reason.</summary>
    public static List<GameEntry> Scan(IEnumerable<string> roots, GameLimits? limits = null)
    {
        limits ??= GameLimits.Unclaimed;
        var found = new Dictionary<string, GameEntry>(StringComparer.Ordinal);
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> folders;
            try { folders = Directory.EnumerateDirectories(root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            foreach (var folder in folders)
            {
                if (found.Count >= MaxGames) break;
                var id = Path.GetFileName(folder).ToLowerInvariant();
                if (!IdPattern().IsMatch(id) || found.ContainsKey(id)) continue;
                var wasm = Path.Combine(folder, WasmFile);
                if (!File.Exists(wasm)) continue;
                // A folder that is a link to somewhere else is not followed: a games folder holds games, not pointers
                if (new DirectoryInfo(folder).LinkTarget is not null || new FileInfo(wasm).LinkTarget is not null) continue;
                found[id] = Describe(id, folder, wasm, limits);
            }
        }
        return found.Values.OrderBy(g => g.Order).ThenBy(g => g.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static GameEntry Describe(string id, string folder, string wasm, GameLimits limits)
    {
        var size = new FileInfo(wasm).Length;
        string title = Titleize(id), tagline = "a WebAssembly game", author = "local", accent = Palette[id.Sum(c => (int) c) % Palette.Length];   // a stable pick: string.GetHashCode changes on every run
        var order = 100;
        GameCard? card = null;

        var metaPath = Path.Combine(folder, MetaFile);
        if (File.Exists(metaPath) && new FileInfo(metaPath).Length <= MaxMetaBytes)
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    title = Text(doc.RootElement, "title", 40) ?? title;
                    tagline = Text(doc.RootElement, "tagline", 90) ?? tagline;
                    author = Text(doc.RootElement, "author", 24) ?? author;
                    if (Text(doc.RootElement, "accent", 7) is { } color && ColorPattern().IsMatch(color)) accent = color;
                    // A game that came from the registry keeps its signed card beside it (see Install); it is unverified until used
                    if (doc.RootElement.TryGetProperty("card", out var c) && c.ValueKind == JsonValueKind.Object) card = GameCard.FromElement(c);
                    if (doc.RootElement.TryGetProperty("order", out var o) && o.ValueKind == JsonValueKind.Number && o.TryGetInt32(out var n))
                        order = Math.Clamp(n, -1000, 1000);
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // a broken game.json only loses the nice title; the game is still listed
            }
        }

        string? error = null;
        if (size == 0) error = "game.wasm is empty.";
        else if (size > limits.MaxModuleBytes)
            error = $"game.wasm is {size / 1024:N0} KB; the limit here is {limits.MaxModuleBytes / 1024:N0} KB.";
        return new GameEntry(id, title, tagline, author, accent, order, wasm, size, folder, error, card);
    }

    private static string? Text(JsonElement obj, string name, int max)
    {
        if (!obj.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String) return null;
        var clean = new string((v.GetString() ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length == 0) return null;
        return clean.Length > max ? clean[..max] : clean;
    }

    private static string Titleize(string id) =>
        string.Join(' ', id.Split('-', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));

    private static readonly Dictionary<string, (long Size, long Ticks, string Sha)> HashCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The SHA-256 of a game's file, remembered until the file changes (the home page asks for it often).</summary>
    public static string Sha256Of(GameEntry game)
    {
        var info = new FileInfo(game.WasmPath);
        lock (HashCache)
        {
            if (HashCache.TryGetValue(game.WasmPath, out var hit) && hit.Size == info.Length && hit.Ticks == info.LastWriteTimeUtc.Ticks) return hit.Sha;
            using var stream = info.OpenRead();
            var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
            HashCache[game.WasmPath] = (info.Length, info.LastWriteTimeUtc.Ticks, sha);
            return sha;
        }
    }

    /// <summary>Which of these games has exactly this file? Games with an error are never offered.</summary>
    public static GameEntry? FindBySha(IEnumerable<GameEntry> games, string sha) =>
        games.Where(g => g.Error is null && g.Bytes <= GameCard.MaxGameBytes)
             .FirstOrDefault(g => string.Equals(Sha256Of(g), sha, StringComparison.Ordinal));

    /// <summary>
    /// Put a downloaded game in the reader's own games folder. The card must be genuine and the bytes must be exactly what it names, and the
    /// module must load in the sandbox (no imports, valid), before anything is written. A game that is already there from the same publisher is
    /// replaced (an update); a different game with the same id gets a folder name made from the id and the publisher.
    /// </summary>
    public static GameEntry Install(string gamesRoot, GameCard card, byte[] wasm, GameLimits? limits = null)
    {
        limits ??= GameLimits.Unclaimed;
        if (!card.TryVerify(out var reason)) throw new GameCardException("This card is not genuine: " + reason);
        if (!card.Matches(wasm)) throw new GameCardException("The downloaded file is not the one the card names.");
        if (wasm.Length > limits.MaxModuleBytes)
            throw new GameRejectedException($"The game is {wasm.Length / 1024:N0} KB; the limit here is {limits.MaxModuleBytes / 1024:N0} KB.");
        using (WasmGame.Load(wasm, limits)) { }   // refuses a module with imports or that is not valid WebAssembly

        Directory.CreateDirectory(gamesRoot);
        var folder = ChooseFolder(gamesRoot, card);
        var target = Path.Combine(gamesRoot, folder);
        Directory.CreateDirectory(target);

        var temp = Path.Combine(target, WasmFile + ".part");
        File.WriteAllBytes(temp, wasm);
        File.Move(temp, Path.Combine(target, WasmFile), overwrite: true);

        var kept = GameCard.TryParse(card.ToJson())!;
        kept.Sources = 0;
        var meta = new Dictionary<string, object?>
        {
            ["title"] = card.Title, ["tagline"] = card.Tagline, ["author"] = card.Author, ["accent"] = card.Accent, ["order"] = 200,
            ["card"] = JsonSerializer.Deserialize<JsonElement>(kept.ToJson())
        };
        File.WriteAllText(Path.Combine(target, MetaFile), JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }));
        return Scan([gamesRoot], limits).First(g => g.Id == folder);
    }

    private static string ChooseFolder(string gamesRoot, GameCard card)
    {
        // the same publisher's game of that id: reuse its folder, so a new version replaces the old
        foreach (var existing in Scan([gamesRoot]))
            if (existing.Card is { } c && c.PublisherId == card.PublisherId && c.Id == card.Id) return existing.Id;
        var plain = Path.Combine(gamesRoot, card.Id);
        if (!Directory.Exists(plain)) return card.Id;
        var suffixed = (card.Id.Length > 31 ? card.Id[..31] : card.Id).TrimEnd('-') + "-" + card.PublisherId[..8];
        return suffixed;
    }

    /// <summary>Read a listed game's module for play. Refuses a game that was listed with an error, or that changed size since.</summary>
    public static byte[] ReadModule(GameEntry game, GameLimits? limits = null)
    {
        limits ??= GameLimits.Unclaimed;
        if (game.Error is not null) throw new GameRejectedException(game.Error);
        var info = new FileInfo(game.WasmPath);
        if (!info.Exists) throw new GameRejectedException("The game's file has gone.");
        if (info.Length > limits.MaxModuleBytes)
            throw new GameRejectedException($"game.wasm is {info.Length / 1024:N0} KB; the limit here is {limits.MaxModuleBytes / 1024:N0} KB.");
        return File.ReadAllBytes(game.WasmPath);
    }
}
