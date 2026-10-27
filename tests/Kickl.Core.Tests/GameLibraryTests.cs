using Kickl.Core.Games;
using Kickl.Core.Session;
using Kickl.Core.Util;
using Xunit;

namespace Kickl.Core.Tests;

/// <summary>The games folder: a game is a folder with a game.wasm, and the home page lists whatever is there.</summary>
public class GameLibraryTests
{
    private static string RepoGames => Path.Combine(Fixture.RepoRoot, "games");
    private static byte[] Wasm(string name) => File.ReadAllBytes(Path.Combine(Fixture.RepoRoot, "examples", "games", "build", name + ".wasm"));

    private static string Add(string root, string id, byte[]? wasm = null, string? json = null)
    {
        var dir = Path.Combine(root, id);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "game.wasm"), wasm ?? Wasm("01-hello"));
        if (json is not null) File.WriteAllText(Path.Combine(dir, "game.json"), json);
        return dir;
    }

    [Fact]
    public void AFolderWithAModuleIsAGame_AndEverythingElseIsIgnored()
    {
        using var temp = new TempDir();
        Add(temp.Path, "good-game");
        Directory.CreateDirectory(Path.Combine(temp.Path, "no-module"));                       // no game.wasm
        File.WriteAllText(Path.Combine(temp.Path, "stray-file.txt"), "x");                     // not a folder
        Add(temp.Path, "Bad Name!");                                                           // not a valid id
        Add(temp.Path, "UPPER");                                                               // ids are lower case: this one is "upper"

        var games = GameLibrary.Scan([temp.Path]);
        Assert.Equal(["good-game", "upper"], games.Select(g => g.Id).Order());
        Assert.All(games, g => Assert.Null(g.Error));
    }

    [Fact]
    public void WithoutAMetadataFile_TheFolderNameBecomesTheTitle_AndColoursAreStable()
    {
        using var temp = new TempDir();
        Add(temp.Path, "my-first-game");
        var a = Assert.Single(GameLibrary.Scan([temp.Path]));
        Assert.Equal("My First Game", a.Title);
        Assert.Equal("local", a.Author);
        Assert.Matches("^#[0-9a-f]{6}$", a.Accent);
        Assert.Equal(a.Accent, Assert.Single(GameLibrary.Scan([temp.Path])).Accent);          // the same colour every run
    }

    [Fact]
    public void Metadata_IsReadCleaned_AndCapped()
    {
        using var temp = new TempDir();
        Add(temp.Path, "meta", json: $$"""
            {"title":"  Fancy\u0007 Title  ","tagline":"{{new string('x', 500)}}","author":"me","accent":"#12ab34","order":7,"build":"ignored","extra":[1,2]}
            """);
        Add(temp.Path, "badcolor", json: """{"title":"B","accent":"red; background:url(x)"}""");
        Add(temp.Path, "broken-json", json: "{ not json");
        Add(temp.Path, "wrong-types", json: """{"title":42,"tagline":["a"],"order":"first"}""");

        var games = GameLibrary.Scan([temp.Path]).ToDictionary(g => g.Id);
        var meta = games["meta"];
        Assert.Equal("Fancy Title", meta.Title);                      // control characters removed, spaces trimmed
        Assert.Equal(90, meta.Tagline.Length);                         // capped
        Assert.Equal("#12ab34", meta.Accent);
        Assert.Equal(7, meta.Order);
        Assert.Matches("^#[0-9a-f]{6}$", games["badcolor"].Accent);    // a colour that is not #rrggbb is never used
        Assert.Equal("Broken Json", games["broken-json"].Title);       // bad json loses the nice title, not the game
        Assert.Null(games["broken-json"].Error);
        Assert.Equal("Wrong Types", games["wrong-types"].Title);
        Assert.Equal(100, games["wrong-types"].Order);
    }

    [Fact]
    public void ListOrder_FollowsTheOrderField_ThenTheTitle()
    {
        using var temp = new TempDir();
        Add(temp.Path, "zeta", json: """{"title":"Zeta","order":1}""");
        Add(temp.Path, "alpha", json: """{"title":"Alpha","order":5}""");
        Add(temp.Path, "beta", json: """{"title":"Beta","order":5}""");
        Assert.Equal(["zeta", "alpha", "beta"], GameLibrary.Scan([temp.Path]).Select(g => g.Id));
    }

    [Fact]
    public void AnEarlierFolderHidesTheSameIdInALaterOne()
    {
        using var temp = new TempDir();
        var mine = Path.Combine(temp.Path, "mine");
        var shipped = Path.Combine(temp.Path, "shipped");
        Add(mine, "caverns", json: """{"title":"My Caverns"}""");
        Add(shipped, "caverns", json: """{"title":"Shipped Caverns"}""");
        Add(shipped, "other");
        var games = GameLibrary.Scan([mine, shipped, Path.Combine(temp.Path, "does-not-exist")]);
        Assert.Equal("My Caverns", games.Single(g => g.Id == "caverns").Title);
        Assert.Contains(games, g => g.Id == "other");
    }

    [Fact]
    public void AGameThatCannotRun_IsListedWithItsReason_AndRefusedAtPlay()
    {
        using var temp = new TempDir();
        Add(temp.Path, "huge", wasm: new byte[GameLimits.Unclaimed.MaxModuleBytes + 1]);
        Add(temp.Path, "empty", wasm: []);
        var games = GameLibrary.Scan([temp.Path]).ToDictionary(g => g.Id);
        Assert.Contains("limit", games["huge"].Error);
        Assert.Contains("empty", games["empty"].Error);
        Assert.Throws<GameRejectedException>(() => GameLibrary.ReadModule(games["huge"]));
    }

    [Fact]
    public void AModuleThatIsNotAGame_ListsFine_ButTheSandboxRefusesIt()
    {
        using var temp = new TempDir();
        using var session = new BrowserSession(temp.Path, "p");
        session.CreateProfile("pin");
        session.Unlock("pin");
        Add(Path.Combine(temp.Path, "games"), "junk", wasm: [1, 2, 3, 4, 5, 6, 7, 8]);
        var entry = Assert.Single(GameLibrary.Scan([Path.Combine(temp.Path, "games")]));
        Assert.Null(entry.Error);                                      // scanning only looks at size, never runs anything
        Assert.Throws<GameRejectedException>(() => session.LoadLibraryGame(entry));
    }

    [Fact]
    public void AGameFromTheFolderRuns_AndKeepsItsOwnSaveInTheProfile()
    {
        using var temp = new TempDir();
        using var session = new BrowserSession(temp.Path, "p");
        session.CreateProfile("pin");
        session.Unlock("pin");
        var root = Path.Combine(temp.Path, "games");
        Add(root, "coin-dash", wasm: Wasm("02-mover"));
        var entry = Assert.Single(GameLibrary.Scan([root]));

        using (var game = session.LoadLibraryGame(entry))
        {
            game.Start(1);
            for (var i = 0; i < 10; i++) Assert.True(game.Frame(33, default));
            Assert.Equal(4, game.ReadSave().Length);
        }

        session.SaveGameData(BrowserSession.LibrarySite, "coin-dash", [11, 0, 0, 0]);
        using var again = session.LoadLibraryGame(entry);              // the save is handed back before init
        again.Start(1);
        Assert.Equal(new byte[] { 11, 0, 0, 0 }, again.ReadSave());
    }

    [Fact]
    public void TheDefaultRoots_IncludeTheReadersOwnFolderFirst()
    {
        using var temp = new TempDir();
        var roots = GameLibrary.DefaultRoots(temp.Path);
        Assert.Equal(Path.GetFullPath(Path.Combine(temp.Path, "games")), roots[0]);
        Assert.Contains(Path.GetFullPath(RepoGames), roots);           // a source checkout finds the repository's folder
    }

    // ---------------------------------------------------------------- the folder in the repository

    [Fact]
    public void EveryGameInTheRepositoryFolder_IsValid_AndFitsTheBudget()
    {
        var games = GameLibrary.Scan([RepoGames]);
        Assert.NotEmpty(games);
        foreach (var game in games)
        {
            Assert.True(game.Error is null, $"{game.Id}: {game.Error}");
            var report = GameCheck.Run(GameLibrary.ReadModule(game), GameLimits.Unclaimed, 3, GameCheck.ParseScript("none*4,start*3,up*30,right*10,up*20"));
            Assert.False(report.Halted, $"{game.Id} halted: {report.HaltReason}");
            Assert.True(report.MaxFrameFuel < report.FrameBudget * 8 / 10, $"{game.Id} used {report.MaxFrameFuel:N0} instructions in a frame");
            Assert.Equal(0, report.MemoryGrowth);
        }
    }

    [Fact]
    public void TheRepositoryFolder_ListsTheExampleGames_AndMatchesTheirBuilds()
    {
        var games = GameLibrary.Scan([RepoGames]).ToDictionary(g => g.Id);
        Assert.Equal("Crystal Caverns", games["crystal-caverns"].Title);
        Assert.Equal(1, games["crystal-caverns"].Order);                // the showpiece is first
        Assert.Equal(Wasm("caverns"), File.ReadAllBytes(games["crystal-caverns"].WasmPath));
        Assert.Equal(Wasm("02-mover"), File.ReadAllBytes(games["coin-dash"].WasmPath));
        Assert.Equal(Wasm("03-plasma"), File.ReadAllBytes(games["plasma"].WasmPath));
        Assert.Equal(Wasm("01-hello"), File.ReadAllBytes(games["hello"].WasmPath));
    }
}
