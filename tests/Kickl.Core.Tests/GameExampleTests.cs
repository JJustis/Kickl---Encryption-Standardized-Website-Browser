using System.Security.Cryptography;
using Kickl.Core.Documents;
using Kickl.Core.Games;
using Kickl.Core.Markup;
using Kickl.Core.Session;
using Kickl.Core.Util;
using Xunit;

namespace Kickl.Core.Tests;

/// <summary>
/// The tutorial games (examples/games) and the Crystal Caverns showcase. The compiled modules are committed, so these tests need no
/// toolchain: they check that every example loads, stays inside the instruction budget, never allocates while it runs, is deterministic,
/// responds to input, and survives hostile input.
/// </summary>
public class GameExampleTests
{
    private static string Build(string name) => Path.Combine(Fixture.RepoRoot, "examples", "games", "build", name + ".wasm");
    private static string CavernsSite => Path.Combine(Fixture.RepoRoot, "sites", "caverns.custom");

    public static TheoryData<string> Games => new() { "01-hello", "02-mover", "03-plasma", "caverns" };

    private static List<(GameInput, int)> Walk(string script = "none*4,start*3,none*4,up*40,right*12,up*30,left*9,a*8,up*30,b*8,down*10,up*30") =>
        GameCheck.ParseScript(script);

    [Theory]
    [MemberData(nameof(Games))]
    public void EveryExample_RunsInsideTheUnclaimedBudget_WithoutAllocating(string name)
    {
        var report = GameCheck.Run(File.ReadAllBytes(Build(name)), GameLimits.Unclaimed, 7, Walk());
        Assert.False(report.Halted, report.HaltReason);
        Assert.True(report.InitFuel < report.InitBudget / 2, $"init used {report.InitFuel:N0} of {report.InitBudget:N0}");
        Assert.True(report.MaxFrameFuel < report.FrameBudget * 8 / 10, $"a frame used {report.MaxFrameFuel:N0} of {report.FrameBudget:N0}");
        // A game has no garbage collector: memory that grows while it runs means it allocates every frame and will eventually halt
        Assert.Equal(0, report.MemoryGrowth);
        Assert.True(report.ModuleBytes < 64 * 1024);
    }

    [Fact]
    public void TheLeakDetector_CatchesAGameThatAllocatesEveryFrame()
    {
        const string leaky = """
            (module
              (memory (export "memory") 3 200)
              (func (export "kickl_abi") (result i32) i32.const 1)
              (func (export "init") (param i32))
              (func (export "frame") (param i32 i32 i32 i32 i32) i32.const 1 memory.grow drop)
              (func (export "framebuffer") (result i32) i32.const 0))
            """;
        using var game = WasmGame.LoadText(leaky, GameLimits.Unclaimed);
        game.Start(1);
        var before = game.MemoryBytes;
        for (var i = 0; i < 20; i++) game.Frame(33, default);
        Assert.True(game.MemoryBytes > before);
    }

    [Theory]
    [MemberData(nameof(Games))]
    public void EveryExample_IsDeterministic_AndTheSeedMattersWhereItShould(string name)
    {
        var wasm = File.ReadAllBytes(Build(name));
        byte[] Run(int seed) => GameCheck.Run(wasm, GameLimits.Unclaimed, seed, Walk()).LastFrameRgba;
        var a = SHA256.HashData(Run(11));
        Assert.Equal(a, SHA256.HashData(Run(11)));                    // same seed, same keys: the same picture
        if (name is "02-mover" or "caverns")
            Assert.NotEqual(a, SHA256.HashData(Run(12)));             // a different seed: a different game
    }

    [Fact]
    public void Caverns_RespondsToTheKeys_AndStartLeavesTheTitleScreen()
    {
        var wasm = File.ReadAllBytes(Build("caverns"));
        byte[] Pic(string script) => SHA256.HashData(GameCheck.Run(wasm, GameLimits.Unclaimed, 3, GameCheck.ParseScript(script)).LastFrameRgba);

        var title = Pic("none*20");
        Assert.NotEqual(title, Pic("none*4,start*3,none*40"));         // pressing Start begins the game
        var begun = Pic("none*4,start*3,none*6");
        Assert.NotEqual(begun, Pic("none*4,start*3,none*6,right*20"));  // turning changes what you see
        Assert.NotEqual(Pic("none*4,start*3,none*6,up*25"), Pic("none*4,start*3,none*6,down*25"));
    }

    [Fact]
    public void Caverns_KeepsYourBestTime_AcrossVisits()
    {
        var wasm = File.ReadAllBytes(Build("caverns"));
        // a previous visit's save: the magic number, a best time of 73,000 ms, two caves escaped
        var saved = new byte[12];
        BitConverter.GetBytes(0x3156434bu).CopyTo(saved, 0);
        BitConverter.GetBytes(73_000u).CopyTo(saved, 4);
        BitConverter.GetBytes(2u).CopyTo(saved, 8);

        var report = GameCheck.Run(wasm, GameLimits.Unclaimed, 5, Walk("none*10"), saved);
        Assert.Equal(saved, report.Save);                               // the game read it and kept it
        var fresh = GameCheck.Run(wasm, GameLimits.Unclaimed, 5, Walk("none*10"));
        Assert.Equal(0u, BitConverter.ToUInt32(fresh.Save, 4));          // no save: no best time yet
    }

    [Fact]
    public void TheMoverKeepsItsBestScore()
    {
        var wasm = File.ReadAllBytes(Build("02-mover"));
        var report = GameCheck.Run(wasm, GameLimits.Unclaimed, 5, Walk("none*5"), [9, 0, 0, 0]);
        Assert.Equal(new byte[] { 9, 0, 0, 0 }, report.Save);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Caverns_SurvivesHostileInput(int seed)
    {
        // pressing every combination of keys and jabbing the pointer all over the picture, for a long time
        var rng = new Random(seed);
        var steps = new List<(GameInput, int)> { (new GameInput(0), 3), (new GameInput(GameButtons.Start), 3) };
        for (var i = 0; i < 150; i++)
            steps.Add((new GameInput(rng.Next(0, 128), rng.Next(-50, 300), rng.Next(-50, 300), rng.Next(4) == 0), rng.Next(1, 9)));
        var report = GameCheck.Run(File.ReadAllBytes(Build("caverns")), GameLimits.Unclaimed, seed * 977, steps);
        Assert.False(report.Halted, report.HaltReason);
        Assert.True(report.MaxFrameFuel < report.FrameBudget * 9 / 10, $"worst frame {report.MaxFrameFuel:N0}");
        Assert.Equal(0, report.MemoryGrowth);
    }

    // ---------------------------------------------------------------- the showcase site

    [Fact]
    public void TheShowcaseSite_CarriesTheSameModuleThatTheTutorialBuilds()
    {
        Assert.Equal(File.ReadAllBytes(Build("caverns")), File.ReadAllBytes(Path.Combine(CavernsSite, "blobs", "caverns.wasm")));
    }

    [Fact]
    public void TheShowcaseSite_PublishesAndDeclaresItsGame()
    {
        using var temp = new TempDir();
        var owner = new DataPaths(temp.Path, "author");
        Crypto.Keystore.Create(owner, "pin-a");
        var master = Crypto.Keystore.Unlock(owner, "pin-a");
        DocumentStore.Publish(owner, CavernsSite, "caverns.custom", master);
        CryptographicOperations.ZeroMemory(master);

        using var reader = new BrowserSession(temp.Path, "reader");
        reader.CreateProfile("pin-r");
        var page = reader.Open("jttp://caverns.custom/", "pin-r");
        var game = Assert.Single(page.Blocks, b => b.Kind == BlockKind.Game);
        Assert.Equal("caverns", game.Id);
        Assert.Equal("application/wasm", reader.Document!.Blobs["caverns"].Mime);

        // the browser opens it with the limits of an unclaimed name and it runs
        using var played = reader.LoadGame("caverns");
        played.Start(1);
        for (var i = 0; i < 5; i++) Assert.True(played.Frame(33, default));
    }
}
