using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kickl.Core.Games;
using Kickl.Core.Mail;
using Kickl.Core.Session;
using Kickl.Core.Util;
using Xunit;

namespace Kickl.Core.Tests;

public class GameCardTests
{
    private static byte[] Wasm() => File.ReadAllBytes(Path.Combine(Fixture.RepoRoot, "examples", "games", "build", "01-hello.wasm"));

    private static GameCard Make(MLDsa key, string id = "my-game", int version = 1) =>
        GameCard.Create(key, id, "My Game", "a tagline", "me", "#7BE0FF", Wasm(), version, DateTimeOffset.UtcNow);

    [Fact]
    public void ACardSignedByItsPublisher_Verifies_AndRoundTripsThroughJson()
    {
        using var key = MLDsa.GenerateKey(MLDsaAlgorithm.MLDsa65);
        var card = Make(key);
        Assert.True(card.TryVerify(out var reason), reason);
        Assert.Equal("#7be0ff", card.Accent);
        var again = GameCard.TryParse(card.ToJson())!;
        Assert.True(again.TryVerify(out _));
        Assert.Equal(card.Sha256, again.Sha256);
        Assert.True(card.Matches(Wasm()));
        Assert.False(card.Matches([1, 2, 3]));
    }

    [Theory]
    [InlineData("title")]
    [InlineData("tagline")]
    [InlineData("author")]
    [InlineData("sha256")]
    [InlineData("size")]
    [InlineData("version")]
    [InlineData("accent")]
    [InlineData("id")]
    public void ChangingAnyField_BreaksTheSignature(string field)
    {
        using var key = MLDsa.GenerateKey(MLDsaAlgorithm.MLDsa65);
        var card = Make(key);
        switch (field)
        {
            case "title": card.Title = "Other"; break;
            case "tagline": card.Tagline = "other"; break;
            case "author": card.Author = "someone"; break;
            case "sha256": card.Sha256 = new string('a', 64); break;
            case "size": card.Size += 1; break;
            case "version": card.Version += 1; break;
            case "accent": card.Accent = "#000000"; break;
            case "id": card.Id = "other-game"; break;
        }
        Assert.False(card.TryVerify(out _));
    }

    [Fact]
    public void ACardCannotBeRepublishedUnderAnotherPublishersId_OrWithAnotherKey()
    {
        using var mine = MLDsa.GenerateKey(MLDsaAlgorithm.MLDsa65);
        using var thief = MLDsa.GenerateKey(MLDsaAlgorithm.MLDsa65);
        var card = Make(mine);

        var swapped = GameCard.TryParse(card.ToJson())!;
        swapped.PublisherKey = Kickl.Core.Util.B64u.Encode(thief.ExportMLDsaPublicKey());   // my signature, the thief's key
        Assert.False(swapped.TryVerify(out _));

        var forged = Make(thief);
        forged.PublisherId = card.PublisherId;                                              // the thief claims my id
        Assert.False(forged.TryVerify(out var reason));
        Assert.Contains("publisher", reason);
    }

    [Fact]
    public void FieldBoundaries_CannotBeShifted_ToReuseASignature()
    {
        // "ab" + "c" and "a" + "bc" must not sign the same: every field is length-prefixed
        using var key = MLDsa.GenerateKey(MLDsaAlgorithm.MLDsa65);
        var one = Make(key); one.Title = "ab"; one.Tagline = "c";
        var two = GameCard.TryParse(one.ToJson())!; two.Title = "a"; two.Tagline = "bc";
        Assert.NotEqual(one.Canonical(), two.Canonical());
    }

    [Theory]
    [InlineData("Bad Id")]
    [InlineData("UPPER")]
    [InlineData("-lead")]
    [InlineData("")]
    public void BadIds_AreRefused(string id)
    {
        using var key = MLDsa.GenerateKey(MLDsaAlgorithm.MLDsa65);
        Assert.Throws<GameCardException>(() => Make(key, id));
    }

    [Fact]
    public void OversizedOrControlTextOrBadSizes_AreRefused()
    {
        using var key = MLDsa.GenerateKey(MLDsaAlgorithm.MLDsa65);
        Assert.Throws<GameCardException>(() => GameCard.Create(key, "g", new string('x', 41), "", "me", "#ffffff", Wasm(), 1, DateTimeOffset.UtcNow));
        Assert.Throws<GameCardException>(() => GameCard.Create(key, "g", "t\u0007", "", "me", "#ffffff", Wasm(), 1, DateTimeOffset.UtcNow));
        Assert.Throws<GameCardException>(() => GameCard.Create(key, "g", "t", "", "me", "red", Wasm(), 1, DateTimeOffset.UtcNow));
        Assert.Throws<GameCardException>(() => GameCard.Create(key, "g", "t", "", "me", "#ffffff", new byte[GameCard.MaxGameBytes + 1], 1, DateTimeOffset.UtcNow));
        Assert.Throws<GameCardException>(() => GameCard.Create(key, "g", "t", "", "me", "#ffffff", Wasm(), 0, DateTimeOffset.UtcNow));
    }
}

public class GameTransferTests
{
    private static byte[] Wasm(string name = "01-hello") => File.ReadAllBytes(Path.Combine(Fixture.RepoRoot, "examples", "games", "build", name + ".wasm"));

    private sealed class Seed : IDisposable
    {
        public readonly System.Security.Cryptography.X509Certificates.X509Certificate2 Cert = MailTls.CreateSelfSigned();
        public readonly GameSeedServer Server;
        public string Endpoint => MailEndpoint.WithPin($"https://127.0.0.1:{Server.Port}", MailTls.PinOf(Cert));
        public Seed(Func<string, byte[]?> lookup) { Server = new GameSeedServer(Cert, MailEndpoint.PickFreePort(), lookup); Server.Start(); }
        public void Dispose() { Server.Dispose(); Cert.Dispose(); }
    }

    private static GameCard Card(byte[] wasm, string id = "hello")
    {
        using var key = MLDsa.GenerateKey(MLDsaAlgorithm.MLDsa65);
        return GameCard.Create(key, id, "Hello", "t", "me", "#7ee787", wasm, 1, DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task AGame_IsDownloadedFromAComputerThatHasIt_AndVerified()
    {
        var wasm = Wasm();
        var card = Card(wasm);
        using var seed = new Seed(sha => sha == card.Sha256 ? wasm : null);
        var got = await GameDownloader.FetchAsync(card, [seed.Endpoint]);
        Assert.Equal(wasm, got);
    }

    [Fact]
    public async Task ADeadSource_IsSkipped_AndALaterOneWorks()
    {
        var wasm = Wasm();
        var card = Card(wasm);
        using var good = new Seed(sha => sha == card.Sha256 ? wasm : null);
        var dead = MailEndpoint.WithPin($"https://127.0.0.1:{MailEndpoint.PickFreePort()}", new string('A', MailTls.PinLength));
        var notice = new List<string>();
        var got = await GameDownloader.FetchAsync(card, [dead, good.Endpoint], notice.Add);
        Assert.Equal(wasm, got);
        Assert.Contains(notice, n => n.StartsWith("received"));
    }

    [Fact]
    public async Task ALyingComputer_IsRefused_BecauseTheBytesMustMatchTheSignedCard()
    {
        var real = Wasm("01-hello");
        var card = Card(real);
        using var liar = new Seed(_ => Wasm("03-plasma"));               // serves some other game under the hash it was asked for
        var ex = await Assert.ThrowsAsync<GameCardException>(() => GameDownloader.FetchAsync(card, [liar.Endpoint]));
        Assert.Contains("No computer could provide this game", ex.Message);

        using var shortChanged = new Seed(_ => real[..(real.Length - 1)]);   // right game, one byte missing
        await Assert.ThrowsAsync<GameCardException>(() => GameDownloader.FetchAsync(card, [shortChanged.Endpoint]));
    }

    [Fact]
    public async Task AnImpostorWithTheWrongKey_IsRefused()
    {
        var wasm = Wasm();
        var card = Card(wasm);
        using var seed = new Seed(_ => wasm);
        var wrongPin = MailEndpoint.WithPin($"https://127.0.0.1:{seed.Server.Port}", new string('B', MailTls.PinLength));
        var ex = await Assert.ThrowsAsync<GameCardException>(() => GameDownloader.FetchAsync(card, [wrongPin]));
        Assert.Contains("key its address named", ex.Message);
    }

    [Fact]
    public async Task NobodyOffering_GivesAClearMessage_AndAForgedCardIsNeverTrusted()
    {
        var card = Card(Wasm());
        var ex = await Assert.ThrowsAsync<GameCardException>(() => GameDownloader.FetchAsync(card, []));
        Assert.Contains("No computer is offering", ex.Message);

        card.Title = "tampered";
        await Assert.ThrowsAsync<GameCardException>(() => GameDownloader.FetchAsync(card, ["https://127.0.0.1:1#pin=" + new string('A', 43)]));
    }

    [Fact]
    public async Task TheSeed_AnswersOnlyAGameItHolds_AndDropsGarbage()
    {
        var wasm = Wasm();
        var card = Card(wasm);
        using var seed = new Seed(sha => sha == card.Sha256 ? wasm : null);

        // a game it does not hold: a clean refusal
        var other = Card(Wasm("03-plasma"), "plasma");
        var ex = await Assert.ThrowsAsync<GameCardException>(() => GameDownloader.FetchAsync(other, [seed.Endpoint]));
        Assert.Contains("does not hold", ex.Message);

        // a plain-text request to the TLS port, and a client that never speaks: dropped, and the server keeps serving
        using (var tcp = new System.Net.Sockets.TcpClient())
        {
            await tcp.ConnectAsync(IPAddress.Loopback, seed.Server.Port);
            await tcp.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"GET /v1/game/{card.Sha256} HTTP/1.1\r\n\r\n"));
            var buf = new byte[64];
            var n = 0;
            try { n = await tcp.GetStream().ReadAsync(buf).AsTask().WaitAsync(TimeSpan.FromSeconds(10)); } catch (IOException) { }
            Assert.DoesNotContain("HTTP/1.1 200", Encoding.ASCII.GetString(buf, 0, n));
        }
        Assert.Equal(wasm, await GameDownloader.FetchAsync(card, [seed.Endpoint]));
    }

    // ---------------------------------------------------------------- installing

    [Fact]
    public void AnInstalledGame_KeepsItsCard_AndBecomesPartOfTheLibrary()
    {
        using var temp = new TempDir();
        var wasm = Wasm();
        var card = Card(wasm);
        var entry = GameLibrary.Install(temp.Path, card, wasm);
        Assert.Equal("hello", entry.Id);
        Assert.Equal("Hello", entry.Title);
        Assert.Null(entry.Error);
        Assert.NotNull(entry.Card);
        Assert.True(entry.Card!.TryVerify(out _));
        Assert.Equal(card.Sha256, GameLibrary.Sha256Of(entry));
        Assert.Equal(entry.Id, Assert.Single(GameLibrary.Scan([temp.Path])).Id);
    }

    [Fact]
    public void Install_RefusesAForgedCard_AWrongFile_AndAModuleTheSandboxWouldRefuse()
    {
        using var temp = new TempDir();
        var wasm = Wasm();
        var card = Card(wasm);

        var forged = GameCard.TryParse(card.ToJson())!;
        forged.Title = "tampered";
        Assert.Throws<GameCardException>(() => GameLibrary.Install(temp.Path, forged, wasm));
        Assert.Throws<GameCardException>(() => GameLibrary.Install(temp.Path, card, Wasm("03-plasma")));

        // genuine card, matching bytes, but the module is not a game (it imports nothing useful / is not wasm at all)
        var junk = new byte[64]; new Random(1).NextBytes(junk);
        var junkCard = Card(junk, "junk");
        Assert.Throws<GameRejectedException>(() => GameLibrary.Install(temp.Path, junkCard, junk));
        Assert.Empty(GameLibrary.Scan([temp.Path]));                                    // nothing was written
    }

    [Fact]
    public void ANewerVersionFromTheSamePublisher_ReplacesTheOldOne_ADifferentPublishersSameId_GetsItsOwnFolder()
    {
        using var temp = new TempDir();
        using var publisher = MLDsa.GenerateKey(MLDsaAlgorithm.MLDsa65);
        using var stranger = MLDsa.GenerateKey(MLDsaAlgorithm.MLDsa65);
        var v1 = Wasm("01-hello");
        var v2 = Wasm("03-plasma");

        var first = GameLibrary.Install(temp.Path, GameCard.Create(publisher, "demo", "Demo", "t", "me", "#ffffff", v1, 1, DateTimeOffset.UtcNow), v1);
        var update = GameLibrary.Install(temp.Path, GameCard.Create(publisher, "demo", "Demo", "t", "me", "#ffffff", v2, 2, DateTimeOffset.UtcNow), v2);
        Assert.Equal(first.Id, update.Id);                                                  // the same folder: an update, not a copy
        Assert.Equal(v2, File.ReadAllBytes(update.WasmPath));

        var other = GameLibrary.Install(temp.Path, GameCard.Create(stranger, "demo", "Demo", "t", "them", "#ffffff", v1, 1, DateTimeOffset.UtcNow), v1);
        Assert.NotEqual(first.Id, other.Id);                                                // never overwrites someone else's game of the same name
        Assert.StartsWith("demo-", other.Id);
        Assert.Equal(2, GameLibrary.Scan([temp.Path]).Count);
    }
}

/// <summary>A stand-in for the registry's game directory, behaving the way server/src/GameCardStore.php does.</summary>
internal sealed class FakeGameRegistry : HttpMessageHandler
{
    public readonly Dictionary<string, JsonNode> Cards = new();       // publisherId/id -> card json
    public readonly Dictionary<string, List<string>> Sources = new(); // sha -> endpoints
    public string RewriteHost = "127.0.0.1";

    public HttpClient Client() => new(this);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
        (HttpStatusCode code, object body) result;

        if (request.Method == HttpMethod.Get && path.EndsWith("/v1/games"))
        {
            var cards = Cards.Values.Select(c => JsonNode.Parse(c.ToJsonString())!).ToArray();
            foreach (var card in cards) card["sources"] = Sources.GetValueOrDefault((string) card["sha256"]!)?.Count ?? 0;
            result = (HttpStatusCode.OK, new { cards, next = 1, more = false });
        }
        else if (request.Method == HttpMethod.Get && path.EndsWith("/v1/games/sources"))
        {
            var list = Sources.GetValueOrDefault(query["sha256"] ?? "") ?? [];
            result = (HttpStatusCode.OK, new { sources = list.Select(e => new { endpoint = e }).ToArray() });
        }
        else if (request.Method == HttpMethod.Post && path.EndsWith("/v1/games/card"))
        {
            var body = JsonNode.Parse(request.Content!.ReadAsStringAsync(ct).Result)!;
            var card = body["card"]!;
            Cards[(string) card["publisherId"]! + "/" + (string) card["id"]!] = JsonNode.Parse(card.ToJsonString())!;
            result = (HttpStatusCode.Created, new { ok = true });
        }
        else if (request.Method == HttpMethod.Post && path.EndsWith("/v1/games/source"))
        {
            var body = JsonNode.Parse(request.Content!.ReadAsStringAsync(ct).Result)!;
            var sha = (string) body["sha256"]!;
            if (!Cards.Values.Any(c => (string) c["sha256"]! == sha)) result = (HttpStatusCode.UnprocessableEntity, new { error = "No game card has that SHA-256" });
            else
            {
                // the test's computers are all on loopback: the LAN address a computer announced is not reachable from here, 127.0.0.1 is
                var endpoint = (string) body["endpoint"]!;
                var uri = new Uri(endpoint.Split('#')[0]);
                var local = $"https://{RewriteHost}:{uri.Port}#{endpoint.Split('#')[1]}";
                var list = Sources.TryGetValue(sha, out var l) ? l : Sources[sha] = [];
                if (!list.Contains(local)) list.Add(local);
                result = (HttpStatusCode.OK, new { ok = true });
            }
        }
        else result = (HttpStatusCode.NotFound, new { error = "unknown" });

        return Task.FromResult(new HttpResponseMessage(result.code)
        {
            Content = new StringContent(JsonSerializer.Serialize(result.body), Encoding.UTF8, "application/json")
        });
    }
}

public class GameSharingTests
{
    private static byte[] Wasm(string name) => File.ReadAllBytes(Path.Combine(Fixture.RepoRoot, "examples", "games", "build", name + ".wasm"));

    private static BrowserSession Profile(string root, string id)
    {
        var s = new BrowserSession(root, id);
        s.CreateProfile("pin-" + id);
        s.Unlock("pin-" + id);
        return s;
    }

    [Fact]
    public async Task AGamePublishedOnOneComputer_ReachesAnotherThroughTheRegistry_AndThenSpreadsFromThere()
    {
        using var temp = new TempDir();
        var registry = new FakeGameRegistry();

        // computer A has a game in its games folder, publishes it and serves it
        using var a = Profile(Path.Combine(temp.Path, "A"), "alice");
        using var sharingA = new GameSharing(a, new GameRegistryClient("https://registry.test/api", registry.Client()));
        Directory.CreateDirectory(Path.Combine(sharingA.GamesRoot, "plasma"));
        File.WriteAllBytes(Path.Combine(sharingA.GamesRoot, "plasma", "game.wasm"), Wasm("03-plasma"));
        File.WriteAllText(Path.Combine(sharingA.GamesRoot, "plasma", "game.json"), """{"title":"Plasma","tagline":"waves","author":"alice","accent":"#d2a8ff"}""");
        IReadOnlyList<GameEntry> LibraryA() => GameLibrary.Scan([sharingA.GamesRoot]);
        Assert.True(sharingA.StartSeeding(LibraryA));
        var card = await sharingA.PublishAsync(Assert.Single(LibraryA()));
        Assert.True(card.TryVerify(out _));
        Assert.Contains(registry.Cards.Keys, k => k.EndsWith("/plasma"));
        Assert.NotEmpty(registry.Sources[card.Sha256]);                                    // A announced itself as a source
        Assert.NotNull(Assert.Single(LibraryA()).Card);                                    // and kept the signed card beside the game

        // computer B has nothing: it refreshes, sees the card as a game it does not own, and downloads it
        using var b = Profile(Path.Combine(temp.Path, "B"), "bob");
        using var sharingB = new GameSharing(b, new GameRegistryClient("https://registry.test/api", registry.Client()));
        IReadOnlyList<GameEntry> LibraryB() => GameLibrary.Scan([sharingB.GamesRoot]);
        Assert.Equal(1, await sharingB.RefreshAsync());
        var missing = Assert.Single(sharingB.Missing(LibraryB()));
        Assert.False(missing.IsUpdate);
        Assert.Equal("Plasma", missing.Card.Title);

        var progress = new List<string>();
        var installed = await sharingB.DownloadAsync(missing.Card, progress.Add);
        Assert.Equal(Wasm("03-plasma"), File.ReadAllBytes(installed.WasmPath));
        Assert.Empty(sharingB.Missing(LibraryB()));                                        // it is B's now
        Assert.Contains(progress, p => p.StartsWith("received"));

        // B plays it in the sandbox, with its own save
        using (var game = b.LoadLibraryGame(installed)) { game.Start(1); Assert.True(game.Frame(33, default)); }

        // B can now serve it too: computer C downloads it from B even though A has gone
        sharingA.StopSeeding();
        registry.Sources[card.Sha256].Clear();
        Assert.True(sharingB.StartSeeding(LibraryB));
        Assert.True(await sharingB.AnnounceAllAsync() >= 1);

        using var c = Profile(Path.Combine(temp.Path, "C"), "carol");
        using var sharingC = new GameSharing(c, new GameRegistryClient("https://registry.test/api", registry.Client()));
        await sharingC.RefreshAsync();
        var fromB = await sharingC.DownloadAsync(Assert.Single(sharingC.Missing([])).Card);
        Assert.Equal(Wasm("03-plasma"), File.ReadAllBytes(fromB.WasmPath));
    }

    [Fact]
    public async Task APublisherSeesNoDuplicate_AndAnUpdateRaisesTheVersion()
    {
        using var temp = new TempDir();
        var registry = new FakeGameRegistry();
        using var a = Profile(temp.Path, "alice");
        using var sharing = new GameSharing(a, new GameRegistryClient("https://registry.test/api", registry.Client()));
        var dir = Path.Combine(sharing.GamesRoot, "g");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "game.wasm"), Wasm("01-hello"));
        File.WriteAllText(Path.Combine(dir, "game.json"), """{"title":"G","author":"alice"}""");

        var one = await sharing.PublishAsync(GameLibrary.Scan([sharing.GamesRoot]).Single());
        var again = await sharing.PublishAsync(GameLibrary.Scan([sharing.GamesRoot]).Single());
        Assert.Equal(1, one.Version);
        Assert.Equal(1, again.Version);                                  // nothing changed: the same card, not a new version

        File.WriteAllBytes(Path.Combine(dir, "game.wasm"), Wasm("03-plasma"));
        var changed = await sharing.PublishAsync(GameLibrary.Scan([sharing.GamesRoot]).Single());
        Assert.Equal(2, changed.Version);
        Assert.NotEqual(one.Sha256, changed.Sha256);
        Assert.Single(registry.Cards);
    }

    [Fact]
    public async Task TheRegistryClient_DropsForgedCards_AndReturnsEachSourcesSecondAddress()
    {
        using var key = MLDsa.GenerateKey(MLDsaAlgorithm.MLDsa65);
        var good = GameCard.Create(key, "good", "Good", "t", "me", "#ffffff", Wasm("01-hello"), 1, DateTimeOffset.UtcNow);
        var forged = GameCard.TryParse(good.ToJson())!;
        forged.Id = "forged"; forged.Title = "Not Signed";               // changed after signing
        var body = JsonSerializer.Serialize(new { cards = new[] { good, forged }, next = 9, more = false });

        var handler = new StubHandler(req => req.RequestUri!.AbsolutePath.EndsWith("/sources")
            ? """{"sources":[{"endpoint":"https://10.0.0.5:4000#pin=A","alt":"https://203.0.113.9:4000#pin=A"},{"endpoint":"https://10.0.0.6:5000#pin=B"}]}"""
            : body);
        using var client = new GameRegistryClient("https://registry.test/api", new HttpClient(handler));
        var (cards, next, more, rejected) = await client.ListAsync(0);
        Assert.Equal("good", Assert.Single(cards).Id);
        Assert.Equal(1, rejected);
        Assert.Equal(9, next);
        Assert.False(more);
        Assert.Equal(["https://10.0.0.5:4000#pin=A", "https://203.0.113.9:4000#pin=A", "https://10.0.0.6:5000#pin=B"], await client.SourcesAsync(new string('a', 64)));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, string> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(respond(request), Encoding.UTF8, "application/json") });
    }
}
