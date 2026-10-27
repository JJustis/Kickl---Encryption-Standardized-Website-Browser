using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kickl.Core.Games;

/// <summary>What the browser found out by running a game file before accepting it.</summary>
public sealed record GameInspection(
    bool Ok, string? Error, long Bytes, string Sha256, double InitPercent, double FramePercent, long MemoryGrowth,
    IReadOnlyList<string> Warnings, byte[]? Picture)
{
    public static GameInspection Failed(string reason, long bytes = 0) =>
        new(false, reason, bytes, "", 0, 0, 0, [], null);
}

/// <summary>
/// Adding a game of your own: look at a .wasm file the way the browser will (does it load in the sandbox, how much of the instruction budget a frame uses,
/// does it keep allocating, what does it draw), pick an id for it, and put it in your games folder. The wizard in the arcade is a front for this; the
/// logic lives here so it can be tested.
/// </summary>
public static class GameImport
{
    // a short, varied play-through: enough input to wake up most games without needing to know what they are
    private const string Probe = "none*5,start*3,none*5,up*20,right*10,a*4,down*10,left*8,b*4,up*20";

    /// <summary>Run <paramref name="wasm"/> in the sandbox and report. Nothing is written anywhere.</summary>
    public static GameInspection Inspect(byte[] wasm, GameLimits? limits = null)
    {
        limits ??= GameLimits.Unclaimed;
        if (wasm.Length == 0) return GameInspection.Failed("That file is empty.");
        if (wasm.Length > GameCard.MaxGameBytes) return GameInspection.Failed($"That file is {wasm.Length / 1024:N0} KB; a game can be at most {GameCard.MaxGameBytes / 1024:N0} KB.", wasm.Length);
        if (wasm.Length > limits.MaxModuleBytes)
            return GameInspection.Failed($"That file is {wasm.Length / 1024:N0} KB; the limit for a game here is {limits.MaxModuleBytes / 1024:N0} KB.", wasm.Length);

        var sha = Convert.ToHexString(SHA256.HashData(wasm)).ToLowerInvariant();
        GameCheckReport report;
        try
        {
            report = GameCheck.Run(wasm, limits, 1, GameCheck.ParseScript(Probe));
        }
        catch (GameRejectedException ex)
        {
            return GameInspection.Failed(Plain(ex.Message), wasm.Length);
        }

        if (report.Halted)
            return GameInspection.Failed("The game stopped when it was run: " + Plain(report.HaltReason ?? "it ended"), wasm.Length);

        var framePct = 100.0 * report.MaxFrameFuel / report.FrameBudget;
        var initPct = 100.0 * report.InitFuel / report.InitBudget;
        var warnings = new List<string>();
        if (framePct > 80) warnings.Add($"A frame uses {framePct:0}% of the budget. A busier moment could stop the game; try to keep it under 70%.");
        else if (framePct > 70) warnings.Add($"A frame uses {framePct:0}% of the budget, which is close to the limit.");
        if (initPct > 50) warnings.Add($"Starting up uses {initPct:0}% of the start-up budget.");
        if (report.MemoryGrowth > 0)
            warnings.Add("The game keeps allocating memory while it runs. Over a long session it will run out and stop; build everything in init() instead.");
        return new GameInspection(true, null, wasm.Length, sha, initPct, framePct, report.MemoryGrowth, warnings, report.LastFrameRgba);
    }

    /// <summary>The sandbox's messages are written for developers; keep the useful part.</summary>
    private static string Plain(string message) => message.Length > 240 ? message[..240] + "…" : message;

    /// <summary>A folder name for a game: lower-case letters, digits and hyphens, at most 40, made from its title.</summary>
    public static string SuggestId(string title)
    {
        var sb = new StringBuilder();
        foreach (var c in title.Trim().ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var id = sb.ToString().Trim('-');
        if (id.Length > 40) id = id[..40].TrimEnd('-');
        return id.Length == 0 ? "game" : id;
    }

    /// <summary>
    /// Put a game in the games folder (or update the one with this id): the module, and a game.json with the title, tagline, author and colour. A signed
    /// card already kept there (from an earlier publish) is left alone, so publishing again raises its version.
    /// </summary>
    public static GameEntry Add(string gamesRoot, string id, byte[] wasm, string title, string tagline, string author, string accent)
    {
        var inspection = Inspect(wasm);
        if (!inspection.Ok) throw new GameRejectedException(inspection.Error ?? "The game cannot run.");
        // the same limits a card has, so a game that is added can always be shared
        CheckText(id, title, tagline, author, accent);

        var folder = Path.Combine(gamesRoot, id);
        Directory.CreateDirectory(folder);
        var temp = Path.Combine(folder, GameLibrary.WasmFile + ".part");
        File.WriteAllBytes(temp, wasm);
        File.Move(temp, Path.Combine(folder, GameLibrary.WasmFile), overwrite: true);

        var metaPath = Path.Combine(folder, GameLibrary.MetaFile);
        var meta = File.Exists(metaPath) && JsonNode.Parse(File.ReadAllText(metaPath)) is JsonObject existing ? existing : new JsonObject();
        meta["title"] = title;
        meta["tagline"] = tagline;
        meta["author"] = author;
        meta["accent"] = accent.ToLowerInvariant();
        if (meta["order"] is null) meta["order"] = 150;
        File.WriteAllText(metaPath, meta.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return GameLibrary.Scan([gamesRoot]).First(g => g.Id == id);
    }

    /// <summary>The checks a card would make, run before anything is written, with messages for a person.</summary>
    public static void CheckText(string id, string title, string tagline, string author, string accent)
    {
        if (id.Length is < 1 or > 40 || !id.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-') || id[0] == '-')
            throw new GameCardException("The game's id must be 1-40 lowercase letters, digits or hyphens.");
        if (title.Trim().Length is < 1 or > 40) throw new GameCardException("Give the game a title of 1-40 characters.");
        if (tagline.Length > 90) throw new GameCardException("The tagline can be at most 90 characters.");
        if (author.Trim().Length is < 1 or > 24) throw new GameCardException("Put an author name of 1-24 characters (it is shown on the card).");
        if (!(accent.Length == 7 && accent[0] == '#' && accent[1..].All(Uri.IsHexDigit))) throw new GameCardException("Pick a colour.");
        foreach (var text in new[] { title, tagline, author })
            if (text.Any(char.IsControl)) throw new GameCardException("Titles and names cannot contain control characters.");
    }
}
