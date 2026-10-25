using System.Net;
using System.Text;
using System.Text.Json;
using Kickl.Core.Registry;
using Kickl.Core.Util;

namespace Kickl.Core.Tests;

public class RegistryClientTests
{
    private const long Now = 1_790_000_000;

    [Fact]
    public async Task Sync_FetchesAVerifiedSnapshotOnFirstRun_AndPinsTheKey()
    {
        using var temp = new TempDir();
        var paths = new DataPaths(temp.Path);
        var handler = new StubHandler();
        using var http = new HttpClient(handler);
        var client = new RegistryClient(http, Base, new SignatureVerifier(), "keyA");

        var records = new[] { Record("nyx.custom", "kb1nyx"), Record("ari.custom", "kb1ari") };
        var snapshot = Snapshot(1, records);
        handler.Routes["/v1/registry/head"] = () => Json(new RegistryHead
        {
            Version = 1, RootHash = snapshot.RootHash, GeneratedAt = Now
        });
        handler.Routes["/v1/registry/snapshot"] = () => Json(snapshot);

        var state = new MirrorState();
        var registry = new RegistryFile();
        var result = await client.SyncAsync(paths, state, registry);

        Assert.Equal(SnapshotStatus.Accepted, result.Status);
        Assert.Equal(1, state.Version);
        Assert.Equal(snapshot.RootHash, state.RootHash);
        Assert.Equal("keyA", state.RegistryKeyFingerprint);
        Assert.Equal(2, registry.Records.Count);
        Assert.Equal(2, LocalRegistry.LoadOrEmpty(paths).Records.Count);
        Assert.Equal(2, state.Pins.Count);
        Assert.Contains("/v1/registry/snapshot", handler.Calls);
    }

    [Fact]
    public async Task Sync_IsIdempotentAndDoesNotRefetchAtTheSameVersion()
    {
        using var temp = new TempDir();
        var paths = new DataPaths(temp.Path);
        var handler = new StubHandler();
        using var http = new HttpClient(handler);
        var client = new RegistryClient(http, Base, new SignatureVerifier(), "keyA");

        var snapshot = Snapshot(3, [Record("nyx.custom", "kb1nyx")]);
        handler.Routes["/v1/registry/head"] = () => Json(new RegistryHead
        {
            Version = 3, RootHash = snapshot.RootHash, GeneratedAt = Now
        });
        handler.Routes["/v1/registry/snapshot"] = () => Json(snapshot);

        var state = new MirrorState();
        var registry = new RegistryFile();
        Assert.Equal(SnapshotStatus.Accepted, (await client.SyncAsync(paths, state, registry)).Status);

        handler.Calls.Clear();
        var again = await client.SyncAsync(paths, state, registry);

        Assert.Equal(SnapshotStatus.AlreadyCurrent, again.Status);
        Assert.Equal(["/v1/registry/head"], handler.Calls);
        Assert.Equal(3, state.Version);
    }

    [Fact]
    public async Task Sync_AppliesADeltaOntoTheHeldRecordSet()
    {
        using var temp = new TempDir();
        var paths = new DataPaths(temp.Path);
        var handler = new StubHandler();
        using var http = new HttpClient(handler);
        var client = new RegistryClient(http, Base, new SignatureVerifier(), "keyA");

        var baseline = new[]
        {
            Record("nyx.custom", "kb1nyx"), Record("ari.custom", "kb1ari")
        };
        var state = new MirrorState
        {
            Version = 1,
            RootHash = RootHex(baseline),
            GeneratedAt = Now,
            RegistryKeyFingerprint = "keyA"
        };
        var registry = new RegistryFile { Version = 1, Records = [.. baseline] };

        var changed = new[]
        {
            Record("ari.custom", "kb1ari", "updated headline"), Record("dev.custom", "kb1dev")
        };
        var merged = baseline.Concat(changed)
            .GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last());

        var delta = new RegistryDelta
        {
            From = 1,
            To = 2,
            RootHash = RootHex(merged),
            GeneratedAt = Now + 60,
            Records = [.. changed],
            Sig = "good"
        };

        handler.Routes["/v1/registry/head"] = () => Json(new RegistryHead
        {
            Version = 2, RootHash = delta.RootHash, GeneratedAt = Now + 60
        });
        handler.Routes["/v1/registry/delta?from=1"] = () => Json(delta);

        var result = await client.SyncAsync(paths, state, registry);

        Assert.Equal(SnapshotStatus.Accepted, result.Status);
        Assert.Equal(2, state.Version);
        Assert.Equal(delta.RootHash, state.RootHash);
        Assert.Equal(3, registry.Records.Count);
        Assert.Equal("updated headline",
            registry.Records.Single(r => r.Name == "ari.custom").Headline);
        Assert.Equal(3, LocalRegistry.LoadOrEmpty(paths).Records.Count);
        Assert.DoesNotContain("/v1/registry/snapshot", handler.Calls);
    }

    [Fact]
    public async Task Sync_RejectsAStaleHead()
    {
        using var temp = new TempDir();
        var handler = new StubHandler();
        using var http = new HttpClient(handler);
        var client = new RegistryClient(http, Base, new SignatureVerifier(), "keyA");

        handler.Routes["/v1/registry/head"] = () => Json(new RegistryHead
        {
            Version = 4, RootHash = new string('a', 64), GeneratedAt = Now
        });

        var state = new MirrorState { Version = 5, RootHash = new string('b', 64) };
        var result = await client.SyncAsync(new DataPaths(temp.Path), state, new RegistryFile());

        Assert.Equal(SnapshotStatus.Stale, result.Status);
        Assert.Equal(5, state.Version);
        Assert.DoesNotContain("/v1/registry/snapshot", handler.Calls);
    }

    [Fact]
    public async Task Sync_RejectsABadSnapshotSignatureBeforeWritingAnything()
    {
        using var temp = new TempDir();
        var paths = new DataPaths(temp.Path);
        var handler = new StubHandler();
        using var http = new HttpClient(handler);
        var client = new RegistryClient(http, Base, new SignatureVerifier(), "keyA");

        var snapshot = Snapshot(1, [Record("nyx.custom", "kb1nyx")]);
        snapshot.Sig = "bad";
        handler.Routes["/v1/registry/head"] = () => Json(new RegistryHead
        {
            Version = 1, RootHash = snapshot.RootHash, GeneratedAt = Now
        });
        handler.Routes["/v1/registry/snapshot"] = () => Json(snapshot);

        var state = new MirrorState();
        var result = await client.SyncAsync(paths, state, new RegistryFile());

        Assert.Equal(SnapshotStatus.BadSignature, result.Status);
        Assert.Equal(0, state.Version);
        Assert.Empty(state.Pins);
        Assert.False(File.Exists(paths.Registry));
    }

    [Fact]
    public async Task Sync_RejectsRecordsThatDoNotMatchTheClaimedRoot()
    {
        using var temp = new TempDir();
        var handler = new StubHandler();
        using var http = new HttpClient(handler);
        var client = new RegistryClient(http, Base, new SignatureVerifier(), "keyA");

        var snapshot = Snapshot(1, [Record("nyx.custom", "kb1nyx")]);
        snapshot.RootHash = new string('0', 64);
        handler.Routes["/v1/registry/head"] = () => Json(new RegistryHead
        {
            Version = 1, RootHash = snapshot.RootHash, GeneratedAt = Now
        });
        handler.Routes["/v1/registry/snapshot"] = () => Json(snapshot);

        var state = new MirrorState();
        var result = await client.SyncAsync(new DataPaths(temp.Path), state, new RegistryFile());

        Assert.Equal(SnapshotStatus.BadRoot, result.Status);
        Assert.Equal(0, state.Version);
    }

    [Fact]
    public async Task Sync_RejectsAChangedSigningKey()
    {
        using var temp = new TempDir();
        var handler = new StubHandler();
        using var http = new HttpClient(handler);
        var client = new RegistryClient(http, Base, new SignatureVerifier(), "keyA");

        var snapshot = Snapshot(2, [Record("nyx.custom", "kb1nyx")]);
        handler.Routes["/v1/registry/head"] = () => Json(new RegistryHead
        {
            Version = 2, RootHash = snapshot.RootHash, GeneratedAt = Now
        });
        handler.Routes["/v1/registry/snapshot"] = () => Json(snapshot);

        var state = new MirrorState { RegistryKeyFingerprint = "keyB" };
        var result = await client.SyncAsync(new DataPaths(temp.Path), state, new RegistryFile());

        Assert.Equal(SnapshotStatus.SigningKeyChanged, result.Status);
        Assert.Equal(0, state.Version);
    }

    [Fact]
    public async Task Sync_RejectsADeltaForTheWrongBaseVersion()
    {
        using var temp = new TempDir();
        var handler = new StubHandler();
        using var http = new HttpClient(handler);
        var client = new RegistryClient(http, Base, new SignatureVerifier(), "keyA");

        var delta = new RegistryDelta
        {
            From = 2,
            To = 3,
            RootHash = RootHex([Record("nyx.custom", "kb1nyx")]),
            GeneratedAt = Now,
            Records = [Record("nyx.custom", "kb1nyx")],
            Sig = "good"
        };
        handler.Routes["/v1/registry/head"] = () => Json(new RegistryHead
        {
            Version = 3, RootHash = delta.RootHash, GeneratedAt = Now
        });
        handler.Routes["/v1/registry/delta?from=1"] = () => Json(delta);

        var state = new MirrorState { Version = 1 };
        var result = await client.SyncAsync(new DataPaths(temp.Path), state, new RegistryFile());

        Assert.Equal(SnapshotStatus.Malformed, result.Status);
        Assert.Equal(1, state.Version);
    }

    [Fact]
    public async Task Sync_ReportsNetworkFailuresWithoutTouchingState()
    {
        using var temp = new TempDir();
        var handler = new StubHandler();
        using var http = new HttpClient(handler);
        var client = new RegistryClient(http, Base, new SignatureVerifier(), "keyA");

        handler.Routes["/v1/registry/head"] = () =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError);

        var state = new MirrorState();
        var result = await client.SyncAsync(new DataPaths(temp.Path), state, new RegistryFile());

        Assert.Equal(SnapshotStatus.NetworkError, result.Status);
        Assert.Equal(0, state.Version);
    }

    private static readonly Uri Base = new("https://registry.test/");

    private static RegistrySnapshot Snapshot(int version, IEnumerable<RegistryRecord> records)
    {
        var snapshot = new RegistrySnapshot
        {
            Version = version,
            GeneratedAt = Now,
            Records = [.. records]
        };
        snapshot.RootHash = snapshot.ComputeRootHex();
        snapshot.Sig = "good";
        return snapshot;
    }

    private static string RootHex(IEnumerable<RegistryRecord> records) =>
        new RegistrySnapshot { Records = [.. records] }.ComputeRootHex();

    private static RegistryRecord Record(string name, string ownerId, string headline = "test record") => new()
    {
        Name = name,
        OwnerId = ownerId,
        Title = name,
        Headline = headline,
        Tags = ["test"],
        UpdatedUtc = Now,
        ContentHash = new string('c', 64)
    };

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
    };

    private sealed class SignatureVerifier : IRegistrySnapshotVerifier
    {
        public bool Verify(ReadOnlySpan<byte> payload, string signature) => signature == "good";
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public Dictionary<string, Func<HttpResponseMessage>> Routes { get; } =
            new(StringComparer.Ordinal);

        public List<string> Calls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var key = request.RequestUri!.PathAndQuery;
            Calls.Add(key);
            return Task.FromResult(Routes.TryGetValue(key, out var factory)
                ? factory()
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
