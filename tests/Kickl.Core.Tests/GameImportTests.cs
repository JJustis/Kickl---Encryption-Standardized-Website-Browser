using Kickl.Core.Games;
using Kickl.Core.Session;
using Kickl.Core.Util;
using Xunit;

namespace Kickl.Core.Tests;

/// <summary>The "add a game" wizard: look at a file, make an id, put it in the games folder, and own what you publish.</summary>
public class GameImportTests
{
    private static byte[] Wasm(string name) => File.ReadAllBytes(Path.Combine(Fixture.RepoRoot, "examples", "games", "build", name + ".wasm"));

    [Theory]
    [InlineData("01-hello")]
    [InlineData("02-mover")]
    [InlineData("03-plasma")]
    [InlineData("caverns")]
    public void AGoodGame_IsAcceptedWithItsPicture_AndItsBudgetUse(string name)
    {
        var wasm = Wasm(name);
        var look = GameImport.Inspect(wasm);
        Assert.True(look.Ok, look.Error);
        Assert.Equal(wasm.Length, look.Bytes);
        Assert.Equal(WasmGame.FrameBytes, look.Picture!.Length);           // a thumbnail to show the author what the game draws
        Assert.InRange(look.FramePercent, 0.1, 80);
        Assert.Equal(0, look.MemoryGrowth);
        Assert.Empty(look.Warnings);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(wasm)).ToLowerInvariant(), look.Sha256);
    }

    [Fact]
    public void FilesThatAreNotGames_AreRefusedWithAReason()
    {
        Assert.False(GameImport.Inspect([]).Ok);
        var junk = new byte[200]; new Random(3).NextBytes(junk);
        var look = GameImport.Inspect(junk);
        Assert.False(look.Ok);
        Assert.False(string.IsNullOrWhiteSpace(look.Error));
        Assert.Null(look.Picture);

        var big = GameImport.Inspect(new byte[GameLimits.Unclaimed.MaxModuleBytes + 1]);
        Assert.False(big.Ok);
        Assert.Contains("limit", big.Error);
        var huge = GameImport.Inspect(new byte[GameCard.MaxGameBytes + 1]);
        Assert.Contains("at most", huge.Error);
    }

    [Theory]
    [InlineData("Crystal Caverns", "crystal-caverns")]
    [InlineData("  My   Game!! ", "my-game")]
    [InlineData("Ünï-cödé 2", "n-c-d-2")]
    [InlineData("!!!", "game")]
    [InlineData("", "game")]
    [InlineData("---", "game")]
    public void TheSuggestedId_IsAlwaysAValidFolderName(string title, string expected)
    {
        var id = GameImport.SuggestId(title);
        Assert.Equal(expected, id);
        GameImport.CheckText(id, "T", "", "me", "#ffffff");   // and it is always acceptable to a card
    }

    [Fact]
    public void ALongTitle_GivesAnIdThatStillFits()
    {
        var id = GameImport.SuggestId(new string('a', 80) + " and more");
        Assert.True(id.Length <= 40);
        Assert.DoesNotContain("--", id);
    }

    [Fact]
    public void Add_PutsTheGameInTheFolder_AndAnUpdateKeepsWhatWasThere()
    {
        using var temp = new TempDir();
        var entry = GameImport.Add(temp.Path, "orb-garden", Wasm("01-hello"), "Orb Garden", "glowing orbs", "mira", "#FF9BCE");
        Assert.Equal("Orb Garden", entry.Title);
        Assert.Equal("#ff9bce", entry.Accent);
        Assert.Equal("mira", entry.Author);
        Assert.Null(entry.Error);
        Assert.Equal(Wasm("01-hello"), File.ReadAllBytes(entry.WasmPath));

        // a new file for the same id updates it in place; a signed card kept beside it is not lost
        var metaPath = Path.Combine(entry.Folder, "game.json");
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(metaPath))!.AsObject();
        json["card"] = new System.Text.Json.Nodes.JsonObject { ["marker"] = "keep me" };
        File.WriteAllText(metaPath, json.ToJsonString());
        var updated = GameImport.Add(temp.Path, "orb-garden", Wasm("03-plasma"), "Orb Garden 2", "", "mira", "#112233");
        Assert.Equal("Orb Garden 2", updated.Title);
        Assert.Equal(Wasm("03-plasma"), File.ReadAllBytes(updated.WasmPath));
        Assert.Contains("keep me", File.ReadAllText(metaPath));
        Assert.Single(GameLibrary.Scan([temp.Path]));
    }

    [Theory]
    [InlineData("Bad Id", "T", "", "me", "#ffffff")]
    [InlineData("-lead", "T", "", "me", "#ffffff")]
    [InlineData("ok", "", "", "me", "#ffffff")]
    [InlineData("ok", "T", "", "", "#ffffff")]
    [InlineData("ok", "T", "", "me", "red")]
    [InlineData("ok", "T\u0007", "", "me", "#ffffff")]
    public void Add_RefusesWhatACardWouldRefuse_AndWritesNothing(string id, string title, string tagline, string author, string accent)
    {
        using var temp = new TempDir();
        Assert.Throws<GameCardException>(() => GameImport.Add(temp.Path, id, Wasm("01-hello"), title, tagline, author, accent));
        Assert.Empty(GameLibrary.Scan([temp.Path]));
        Assert.Throws<GameRejectedException>(() => GameImport.Add(temp.Path, "junk", [1, 2, 3, 4, 5, 6, 7, 8], "T", "", "me", "#ffffff"));
        Assert.False(Directory.Exists(Path.Combine(temp.Path, "junk")));
    }

    [Fact]
    public async Task AGameYouPublish_IsYours_AndYouCanStopSharingIt()
    {
        using var temp = new TempDir();
        var registry = new FakeGameRegistry();
        using var session = new BrowserSession(temp.Path, "alice");
        session.CreateProfile("pin");
        session.Unlock("pin");
        using var sharing = new GameSharing(session, new GameRegistryClient("https://registry.test/api", registry.Client()));

        var mine = sharing.MyPublisherId();
        Assert.Equal(16, mine.Length);
        Assert.Equal(mine, sharing.MyPublisherId());                      // the same key every time: ownership is stable

        var entry = GameImport.Add(sharing.GamesRoot, "orb-garden", Wasm("01-hello"), "Orb Garden", "t", "mira", "#ff9bce");
        Assert.False(sharing.IsMine(entry, mine));                        // not yet published
        await sharing.PublishAsync(entry);
        entry = GameLibrary.Scan([sharing.GamesRoot]).Single();
        Assert.True(sharing.IsMine(entry, mine));
        Assert.Single(registry.Cards);

        // another profile on another computer does not own it, and cannot remove it
        using var other = new BrowserSession(Path.Combine(temp.Path, "other"), "bob");
        other.CreateProfile("pin");
        other.Unlock("pin");
        using var sharingOther = new GameSharing(other, new GameRegistryClient("https://registry.test/api", registry.Client()));
        Assert.False(sharingOther.IsMine(entry, sharingOther.MyPublisherId()));
        await Assert.ThrowsAsync<GameCardException>(() => sharingOther.UnpublishAsync(entry));

        // the owner stops sharing: the card leaves the registry and the folder, the game stays
        await sharing.UnpublishAsync(entry);
        Assert.Empty(registry.Cards);
        var after = GameLibrary.Scan([sharing.GamesRoot]).Single();
        Assert.Null(after.Card);
        Assert.Null(after.Error);
        Assert.True(File.Exists(after.WasmPath));
    }

    [Fact]
    public async Task UpdatingAGame_ThroughTheWizardPath_RaisesTheVersion()
    {
        using var temp = new TempDir();
        var registry = new FakeGameRegistry();
        using var session = new BrowserSession(temp.Path, "alice");
        session.CreateProfile("pin");
        session.Unlock("pin");
        using var sharing = new GameSharing(session, new GameRegistryClient("https://registry.test/api", registry.Client()));

        var first = GameImport.Add(sharing.GamesRoot, "orb", Wasm("01-hello"), "Orb", "", "mira", "#ffffff");
        var v1 = await sharing.PublishAsync(first);
        var second = GameImport.Add(sharing.GamesRoot, "orb", Wasm("03-plasma"), "Orb", "now with waves", "mira", "#ffffff");
        Assert.NotNull(second.Card);                                      // the earlier card is still beside it
        var v2 = await sharing.PublishAsync(second);
        Assert.Equal(1, v1.Version);
        Assert.Equal(2, v2.Version);
        Assert.Single(registry.Cards);
    }
}
