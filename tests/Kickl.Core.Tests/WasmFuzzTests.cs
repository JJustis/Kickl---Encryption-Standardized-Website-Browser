using System.Diagnostics;
using Kickl.Core.Games;
using Xunit;

namespace Kickl.Core.Tests;

/// <summary>
/// A game module is attacker-supplied bytes. Whatever they are, the host may only refuse them with <see cref="GameRejectedException"/>
/// or run them and stop them on a limit; it must never throw anything else, crash, or hang.
/// </summary>
public class WasmFuzzTests
{
    private static readonly GameLimits Tight = new(
        MaxModuleBytes: 64 * 1024, MaxMemoryBytes: 8L << 20, InitFuel: 2_000_000, FrameFuel: 500_000, MaxStackBytes: 128 * 1024);

    private static byte[] Real() =>
        File.ReadAllBytes(Path.Combine(Fixture.RepoRoot, "sites", "game-lab.custom", "blobs", "catcher.wasm"));

    /// <summary>Load and run a module, accepting only the outcomes a hostile module is allowed to produce.</summary>
    private static string Probe(byte[] module)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var game = WasmGame.Load(module, Tight);
            game.Start(1);
            var frame = new byte[WasmGame.FrameBytes];
            for (var i = 0; i < 4 && !game.Halted; i++)
            {
                game.Frame(33, new GameInput(Buttons: i, PointerX: i * 7, PointerY: i * 3, PointerDown: i % 2 == 0));
                game.CopyFrame(frame);
            }
            game.ReadSave();
            return game.Halted ? "halted" : "ran";
        }
        catch (GameRejectedException)
        {
            return "rejected";
        }
        finally
        {
            Assert.True(sw.ElapsedMilliseconds < 5_000, $"a hostile module held the host for {sw.ElapsedMilliseconds} ms");
        }
    }

    [Fact]
    public void FlippingBytesOfARealGameNeverBreaksTheHost()
    {
        var original = Real();
        var rng = new Random(20261027);                         // fixed seed: a failure reproduces
        var outcomes = new Dictionary<string, int>();

        for (var i = 0; i < 400; i++)
        {
            var mutated = (byte[])original.Clone();
            var flips = rng.Next(1, 6);
            for (var k = 0; k < flips; k++) mutated[rng.Next(mutated.Length)] = (byte)rng.Next(256);
            var outcome = Probe(mutated);
            outcomes[outcome] = outcomes.GetValueOrDefault(outcome) + 1;
        }

        Assert.True(outcomes.Values.Sum() == 400);
        Assert.True(outcomes.GetValueOrDefault("rejected") > 0, "most corruptions should simply be refused");
    }

    [Fact]
    public void TruncatedAndExtendedModulesAreHandled()
    {
        var original = Real();
        for (var cut = 0; cut < original.Length; cut += 13)
            Probe(original[..cut]);

        var padded = original.Concat(new byte[4096]).ToArray();
        Probe(padded);
        Probe(original.Concat(Enumerable.Repeat((byte)0xFF, 2048)).ToArray());
    }

    [Fact]
    public void RandomBytesBehindAValidHeaderAreHandled()
    {
        var header = new byte[] { 0x00, 0x61, 0x73, 0x6D, 0x01, 0x00, 0x00, 0x00 };   // \0asm, version 1
        var rng = new Random(77);
        for (var i = 0; i < 300; i++)
        {
            var body = new byte[rng.Next(1, 400)];
            rng.NextBytes(body);
            Probe(header.Concat(body).ToArray());
        }
    }

    [Fact]
    public void ModulesThatClaimEnormousThingsAreRefusedNotHonoured()
    {
        // a memory section declaring 65,536 pages (4 GiB), and a data section far larger than the module itself
        var hugeMemory = new byte[]
        {
            0x00, 0x61, 0x73, 0x6D, 0x01, 0x00, 0x00, 0x00,
            0x05, 0x03, 0x01, 0x00, 0x80, 0x80, 0x04                                   // memory: min 65536 pages
        };
        Assert.Equal("rejected", Probe(hugeMemory));

        var bigLocals = new byte[]
        {
            0x00, 0x61, 0x73, 0x6D, 0x01, 0x00, 0x00, 0x00,
            0x01, 0x04, 0x01, 0x60, 0x00, 0x00,                                         // type () -> ()
            0x03, 0x02, 0x01, 0x00,                                                      // one function
            0x0A, 0x09, 0x01, 0x07, 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0x0F, 0x7F, 0x0B      // 0xFFFFFFFF i32 locals
        };
        Assert.Equal("rejected", Probe(bigLocals));
    }
}
