namespace Kickl.Core.Games;

/// <summary>What <see cref="GameCheck.Run"/> found out about a game.</summary>
public sealed record GameCheckReport(
    int ModuleBytes, int FramesRun, ulong InitFuel, ulong AverageFrameFuel, ulong MaxFrameFuel,
    ulong InitBudget, ulong FrameBudget, bool Halted, string? HaltReason, byte[] LastFrameRgba, byte[] Save,
    long MemoryAfterInit = 0, long MemoryAtEnd = 0)
{
    /// <summary>How much the game's memory grew while it ran. Anything above zero over a long run means it allocates per frame.</summary>
    public long MemoryGrowth => MemoryAtEnd - MemoryAfterInit;
}

/// <summary>
/// Runs a game the way the browser would, without a window, so an author can see whether it loads, how much of its instruction budget a
/// frame uses, and what it draws. Used by <c>kickl game-check</c>.
/// </summary>
public static class GameCheck
{
    /// <summary>
    /// A script is comma-separated steps <c>keys*frames</c>, where keys are names joined by <c>+</c>
    /// (<c>left right up down a b start</c>, or <c>none</c>) and may end in <c>@x:y</c> or <c>@x:y:down</c> for the pointer.
    /// Example: <c>none*10,up*30,up+right*20,a@120:80:down*5</c>.
    /// </summary>
    public static List<(GameInput Input, int Frames)> ParseScript(string script)
    {
        var steps = new List<(GameInput, int)>();
        foreach (var raw in script.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var starAt = raw.LastIndexOf('*');
            var frames = 1;
            var head = raw;
            if (starAt >= 0)
            {
                if (!int.TryParse(raw[(starAt + 1)..], out frames) || frames is < 1 or > 100_000)
                    throw new FormatException($"Bad frame count in \"{raw}\".");
                head = raw[..starAt];
            }

            int px = 0, py = 0;
            var down = false;
            var atAt = head.IndexOf('@');
            if (atAt >= 0)
            {
                var parts = head[(atAt + 1)..].Split(':');
                if (parts.Length is < 2 or > 3 || !int.TryParse(parts[0], out px) || !int.TryParse(parts[1], out py))
                    throw new FormatException($"Bad pointer in \"{raw}\"; use @x:y or @x:y:down.");
                down = parts.Length == 3 && parts[2].Equals("down", StringComparison.OrdinalIgnoreCase);
                head = head[..atAt];
            }

            var buttons = 0;
            foreach (var key in head.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                buttons |= key.ToLowerInvariant() switch
                {
                    "none" => 0,
                    "left" => GameButtons.Left,
                    "right" => GameButtons.Right,
                    "up" => GameButtons.Up,
                    "down" => GameButtons.Down,
                    "a" => GameButtons.A,
                    "b" => GameButtons.B,
                    "start" => GameButtons.Start,
                    _ => throw new FormatException($"Unknown key \"{key}\" in \"{raw}\".")
                };
            }
            steps.Add((new GameInput(buttons, px, py, down), frames));
        }
        return steps;
    }

    public static GameCheckReport Run(byte[] wasm, GameLimits limits, int seed, IReadOnlyList<(GameInput Input, int Frames)> script,
        byte[]? previousSave = null)
    {
        using var game = WasmGame.Load(wasm, limits);
        if (previousSave is { Length: > 0 }) game.RestoreSave(previousSave);
        game.Start(seed);
        var memoryAfterInit = game.MemoryBytes;

        ulong total = 0, max = 0;
        var frames = 0;
        var rgba = new byte[WasmGame.FrameBytes];
        foreach (var (input, count) in script)
        {
            for (var i = 0; i < count && !game.Halted; i++)
            {
                game.Frame(33, input);
                frames++;
                total += game.LastFrameFuel;
                max = Math.Max(max, game.LastFrameFuel);
            }
        }
        game.CopyFrame(rgba);
        return new GameCheckReport(wasm.Length, frames, game.LastInitFuel, frames == 0 ? 0 : total / (ulong) frames, max,
            limits.InitFuel, limits.FrameFuel, game.Halted, game.HaltReason, rgba, game.ReadSave(), memoryAfterInit, game.MemoryBytes);
    }
}
