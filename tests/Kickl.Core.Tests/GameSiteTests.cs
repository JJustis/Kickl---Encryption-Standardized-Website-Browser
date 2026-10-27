using Kickl.Core.Documents;
using Kickl.Core.Games;
using Kickl.Core.Markup;
using Kickl.Core.Session;
using Kickl.Core.Util;
using Xunit;

namespace Kickl.Core.Tests;

public class GameSiteTests
{
    private const string Host = "game-lab.custom";
    private const uint Yellow = 0xff40d0ff;   // the falling block, as little-endian RGBA
    private const uint Cyan = 0xffe0c040;     // the best-score marker

    private static string SiteDir => Path.Combine(Fixture.RepoRoot, "sites", Host);

    private static BrowserSession PublishAndOpenAs(string root, string publisher, string reader)
    {
        var owner = new DataPaths(root, publisher);
        Keystore_Create(owner, "pin-p");
        var master = Crypto.Keystore.Unlock(owner, "pin-p");
        DocumentStore.Publish(owner, SiteDir, Host, master);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(master);

        var session = new BrowserSession(root, reader);
        session.CreateProfile("pin-r");
        session.Open("jttp://" + Host + "/", "pin-r");
        return session;
    }

    private static void Keystore_Create(DataPaths paths, string pin) => Crypto.Keystore.Create(paths, pin);

    private static uint Pixel(byte[] rgba, int x, int y)
    {
        var i = (y * WasmGame.Width + x) * 4;
        return BitConverter.ToUInt32(rgba, i);
    }

    /// <summary>Find the falling block's x by scanning the picture; -1 when it is not visible.</summary>
    private static int FindBlockX(byte[] rgba)
    {
        for (var y = 12; y < 140; y++)
            for (var x = 0; x < WasmGame.Width; x++)
                if (Pixel(rgba, x, y) == Yellow) return x;
        return -1;
    }

    [Fact]
    public void ThePageDeclaresItsGameAsAWasmBlob()
    {
        using var session = PublishAndOpenAs(new TempDir().Path, "author", "reader");
        var page = session.Open("jttp://" + Host + "/");
        var game = Assert.Single(page.Blocks, b => b.Kind == BlockKind.Game);
        Assert.Equal("catcher", game.Id);
        Assert.Equal("Catcher", game.Text);
        Assert.Equal("application/wasm", session.Document!.Blobs["catcher"].Mime);
    }

    [Fact]
    public void AnotherLoginCanPlayTheSampleGame_AndItsBestScoreSurvivesInThatProfileOnly()
    {
        using var temp = new TempDir();
        using var session = PublishAndOpenAs(temp.Path, "author", "reader");
        var frame = new byte[WasmGame.FrameBytes];

        using (var game = session.LoadGame("catcher"))
        {
            game.Start(12345);
            Assert.False(game.Halted);

            // play it: follow the falling block with the pointer
            for (var i = 0; i < 400 && !game.Halted; i++)
            {
                Assert.True(game.CopyFrame(frame));
                var x = FindBlockX(frame);
                game.Frame(33, new GameInput(PointerX: x < 0 ? 120 : x + 4, PointerY: 150, PointerDown: true));
            }

            Assert.False(game.Halted, game.HaltReason);
            var save = game.ReadSave();
            Assert.Equal(4, save.Length);
            Assert.True(BitConverter.ToInt32(save) >= 5, "tracking the block should catch several");
            session.SaveGameData(Host, "catcher", save);
        }

        // the save is encrypted at rest
        var onDisk = File.ReadAllBytes(Path.Combine(temp.Path, "profiles", "reader", "private", "game-saves.kpf"));
        Assert.DoesNotContain("catcher", System.Text.Encoding.UTF8.GetString(onDisk));

        // a new run starts with the best score already drawn as the cyan marker
        using (var game = session.LoadGame("catcher"))
        {
            game.Start(777);
            game.Frame(33, default);
            game.CopyFrame(frame);
            Assert.Equal(Cyan, Pixel(frame, 5, 10));
        }

        // another profile has no such save
        using var stranger = new BrowserSession(temp.Path, "stranger");
        stranger.CreateProfile("pin-s");
        stranger.Open("jttp://" + Host + "/", "pin-s");
        Assert.Empty(stranger.LoadGameSave(Host, "catcher"));
    }

    [Fact]
    public void ALoneGameThatTriesToDoNothingIsHarmless_AndNonGameBlobsAreRefused()
    {
        using var session = PublishAndOpenAs(new TempDir().Path, "author", "reader");
        using var game = session.LoadGame("catcher");
        game.Start(1);
        for (var i = 0; i < 100; i++) Assert.True(game.Frame(33, default));
        Assert.False(game.Halted);

        var tmp = new TempDir();
        var owner = new DataPaths(tmp.Path, "o");
        Crypto.Keystore.Create(owner, "p");
        var m = Crypto.Keystore.Unlock(owner, "p");
        DocumentStore.PublishMarkup(owner, "notagame.com", """
            <?xml version="1.0" encoding="utf-8"?>
            <kml v="1" site="notagame.com" epoch="1"><head><title>x</title>
              <meta name="headline" content="not a game"/><meta name="tags" content="demo, test"/></head>
              <body><page><h1>x</h1><blob id="note" mime="text/plain"/></page></body></kml>
            """, m, new Dictionary<string, PendingBlob> { ["note"] = new("text/plain", "hello"u8.ToArray()) });
        using var reader = new BrowserSession(tmp.Path, "r");
        reader.CreateProfile("pin");
        reader.Open("jttp://notagame.com/", "pin");
        Assert.Throws<GameRejectedException>(() => reader.LoadGame("note"));
    }

    [Fact]
    public void ClaimedNamesRunBiggerGamesThanFreeOnes()
    {
        using var temp = new TempDir();
        using var session = new BrowserSession(temp.Path, "p");
        session.CreateProfile("pin");
        session.Unlock("pin");
        var free = session.GameLimitsFor("freename.com");
        Assert.True(free.MaxMemoryBytes <= 8L << 20);
        Assert.True(free.MaxModuleBytes <= 512 * 1024);
    }
}
