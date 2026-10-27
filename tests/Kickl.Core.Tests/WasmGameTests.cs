using Kickl.Core.Games;
using Xunit;

namespace Kickl.Core.Tests;

public class WasmGameTests
{
    // 4 pages = 262,144 bytes: the 153,600-byte framebuffer at 0 and a save area at 200,000.
    private const string Counter = """
        (module
          (memory (export "memory") 4)
          (global $seed (mut i32) (i32.const 0))
          (func (export "kickl_abi") (result i32) i32.const 1)
          (func (export "init") (param i32)
            local.get 0 global.set $seed
            (i32.store (i32.const 200000) (local.get 0)))
          (func (export "framebuffer") (result i32) i32.const 0)
          (func (export "save_ptr") (result i32) i32.const 200000)
          (func (export "save_len") (result i32) i32.const 4)
          (func (export "frame") (param $dt i32) (param $b i32) (param $px i32) (param $py i32) (param $down i32)
            (i32.store (i32.const 0) (i32.or (i32.const 0xff000000) (local.get $b)))
            (i32.store (i32.const 4) (global.get $seed))
            (i32.store (i32.const 8) (i32.add (local.get $px) (i32.shl (local.get $py) (i32.const 8))))
            (i32.store (i32.const 12) (local.get $down))))
        """;

    private static string Game(string frameBody, string extra = "", string abi = "1", string fb = "0") => $$"""
        (module
          {{extra}}
          (memory (export "memory") 4)
          (func (export "kickl_abi") (result i32) i32.const {{abi}})
          (func (export "init") (param i32))
          (func (export "framebuffer") (result i32) i32.const {{fb}})
          (func (export "frame") (param i32 i32 i32 i32 i32) {{frameBody}}))
        """;

    private static byte[] Pixel(WasmGame g, int index)
    {
        var buffer = new byte[WasmGame.FrameBytes];
        Assert.True(g.CopyFrame(buffer));
        return buffer[(index * 4)..(index * 4 + 4)];
    }

    [Fact]
    public void AGameRunsAndDrawsWhatInputAndSeedTellIt()
    {
        using var game = WasmGame.LoadText(Counter);
        game.Start(0x1234);
        Assert.True(game.Frame(16, new GameInput(Buttons: GameButtons.Left | GameButtons.A, PointerX: 3, PointerY: 2, PointerDown: true)));

        Assert.Equal(new byte[] { 17, 0, 0, 0xff }, Pixel(game, 0));        // buttons in the low bits, opaque alpha
        Assert.Equal(new byte[] { 0x34, 0x12, 0, 0 }, Pixel(game, 1));      // the seed the host chose
        Assert.Equal(new byte[] { 3, 2, 0, 0 }, Pixel(game, 2));            // pointer position
        Assert.Equal(new byte[] { 1, 0, 0, 0 }, Pixel(game, 3));
        Assert.Equal(new byte[] { 0x34, 0x12, 0, 0 }, game.ReadSave());
        Assert.False(game.Halted);
    }

    [Fact]
    public void PointerInputIsClampedToTheScreen()
    {
        using var game = WasmGame.LoadText(Counter);
        game.Start(1);
        game.Frame(16, new GameInput(PointerX: 99999, PointerY: -5));
        Assert.Equal(new byte[] { 239, 0, 0, 0 }, Pixel(game, 2)); // x clamped to 239, y clamped to 0
    }

    [Fact]
    public void AnEndlessLoopRunsOutOfFuelAndHaltsTheGameNotTheBrowser()
    {
        using var game = WasmGame.LoadText(Game("(loop $l (br $l))"), new GameLimits(FrameFuel: 100_000));
        game.Start(1);
        Assert.False(game.Frame(16, default));
        Assert.True(game.Halted);
        Assert.Contains("fuel", game.HaltReason, StringComparison.OrdinalIgnoreCase);
        Assert.False(game.Frame(16, default)); // stays halted
    }

    [Fact]
    public void ARunawayInitIsStoppedToo()
    {
        const string wat = """
            (module (memory (export "memory") 4)
              (func (export "kickl_abi") (result i32) i32.const 1)
              (func (export "init") (param i32) (loop $l (br $l)))
              (func (export "framebuffer") (result i32) i32.const 0)
              (func (export "frame") (param i32 i32 i32 i32 i32)))
            """;
        using var game = WasmGame.LoadText(wat, new GameLimits(InitFuel: 100_000));
        game.Start(1);
        Assert.True(game.Halted);
    }

    [Fact]
    public void ATrapHaltsTheGameWithAReason()
    {
        using var game = WasmGame.LoadText(Game("unreachable"));
        game.Start(1);
        Assert.False(game.Frame(16, default));
        Assert.NotNull(game.HaltReason);
    }

    [Fact]
    public void AGameThatAsksTheHostForAnythingIsRefused()
    {
        var withImport = Game("", extra: """(import "env" "fetch" (func $f (param i32) (result i32)))""");
        var ex = Assert.Throws<GameRejectedException>(() => WasmGame.LoadText(withImport));
        Assert.Contains("env.fetch", ex.Message);

        var withMemoryImport = """
            (module (import "env" "memory" (memory 1))
              (func (export "kickl_abi") (result i32) i32.const 1))
            """;
        Assert.Throws<GameRejectedException>(() => WasmGame.LoadText(withMemoryImport));
    }

    [Fact]
    public void MemoryCannotGrowPastTheCap()
    {
        // tries to grow by 1000 pages (64 MB) and draws -1 or the old size into pixel 0
        var body = """
            (i32.store (i32.const 0) (memory.grow (i32.const 1000)))
            """;
        using var game = WasmGame.LoadText(Game(body), new GameLimits(MaxMemoryBytes: 8L << 20));
        game.Start(1);
        Assert.True(game.Frame(16, default));
        Assert.Equal(new byte[] { 0xff, 0xff, 0xff, 0xff }, Pixel(game, 0)); // memory.grow returned -1
    }

    [Fact]
    public void AMemoryBiggerThanTheCapCannotEvenStart()
    {
        var big = """
            (module (memory (export "memory") 600)
              (func (export "kickl_abi") (result i32) i32.const 1)
              (func (export "init") (param i32))
              (func (export "framebuffer") (result i32) i32.const 0)
              (func (export "frame") (param i32 i32 i32 i32 i32)))
            """;
        Assert.Throws<GameRejectedException>(() => WasmGame.LoadText(big, new GameLimits(MaxMemoryBytes: 16L << 20)));
    }

    [Fact]
    public void ABadFramebufferPointerHaltsTheGame()
    {
        using var game = WasmGame.LoadText(Game("", fb: "262000")); // 262,000 + 153,600 is past 262,144
        game.Start(1);
        Assert.True(game.Halted);
        Assert.Contains("framebuffer", game.HaltReason);
    }

    [Fact]
    public void AWrongAbiOrMissingExportIsRefused()
    {
        Assert.Throws<GameRejectedException>(() => WasmGame.LoadText(Game("", abi: "2")));

        const string noFrame = """
            (module (memory (export "memory") 4)
              (func (export "kickl_abi") (result i32) i32.const 1)
              (func (export "init") (param i32))
              (func (export "framebuffer") (result i32) i32.const 0))
            """;
        Assert.Throws<GameRejectedException>(() => WasmGame.LoadText(noFrame));
    }

    [Fact]
    public void GarbageAndOversizedModulesAreRefused()
    {
        Assert.Throws<GameRejectedException>(() => WasmGame.Load([]));
        Assert.Throws<GameRejectedException>(() => WasmGame.Load([1, 2, 3, 4, 5, 6, 7, 8]));
        Assert.Throws<GameRejectedException>(() => WasmGame.Load(new byte[2_000_000]));
        Assert.Throws<GameRejectedException>(() => WasmGame.Load(new byte[2_000], new GameLimits(MaxModuleBytes: 1_000)));
    }

    [Fact]
    public void DeepRecursionTrapsInsteadOfCrashingTheHost()
    {
        const string recurse = """
            (module (memory (export "memory") 4)
              (func $r (param i32) (result i32) (call $r (i32.add (local.get 0) (i32.const 1))))
              (func (export "kickl_abi") (result i32) i32.const 1)
              (func (export "init") (param i32))
              (func (export "framebuffer") (result i32) i32.const 0)
              (func (export "frame") (param i32 i32 i32 i32 i32) (drop (call $r (i32.const 0)))))
            """;
        using var game = WasmGame.LoadText(recurse);
        game.Start(1);
        Assert.False(game.Frame(16, default));
        Assert.True(game.Halted);
    }
}
