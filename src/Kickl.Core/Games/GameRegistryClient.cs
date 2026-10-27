using System.Net;
using System.Text;
using System.Text.Json;
using Kickl.Core.Util;

namespace Kickl.Core.Games;

public sealed class GameRegistryException(string message, HttpStatusCode? status = null, Exception? inner = null) : Exception(message, inner)
{
    public HttpStatusCode? Status { get; } = status;
}

/// <summary>
/// The client for the registry's game directory (<c>/v1/games</c>): list the cards, publish a card, say "this computer can serve that game",
/// and ask who can serve one. The registry holds cards and a list of sources only, never a game file. Requests that are safe to repeat are
/// retried when the network drops a connection.
/// </summary>
public sealed class GameRegistryClient : IDisposable
{
    public const string DefaultBase = "https://secupgrade.com/apicalls";

    private readonly HttpClient _http;
    private readonly Uri _baseUri;
    private readonly bool _ownsHttp;

    public GameRegistryClient(string? baseUrl = null, HttpClient? http = null)
    {
        _ownsHttp = http is null;
        _http = (http ?? new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(3),
            AllowAutoRedirect = false,
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(10),   // see MailClient: do not reuse a connection a router may have forgotten
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        }) { Timeout = TimeSpan.FromSeconds(60) }).Limit(HttpLimits.Mail);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("KicklGames/0.1");
        var value = baseUrl ?? DefaultBase;
#if DEBUG
        // Debug builds only (like the other test overrides): point at a throwaway registry. A shipped build only ever uses the real one.
        if (baseUrl is null && Environment.GetEnvironmentVariable("KICKL_GAMES_REGISTRY") is { Length: > 0 } testRegistry) value = testRegistry;
#endif
        _baseUri = new Uri(value.EndsWith('/') ? value : value + "/", UriKind.Absolute);
    }

    /// <summary>Cards the registry changed after <paramref name="since"/>. Every card is verified; one that is not genuine is dropped.</summary>
    public async Task<(List<GameCard> Cards, long Next, bool More, int Rejected)> ListAsync(long since, int limit = 100,
        CancellationToken cancellationToken = default)
    {
        using var document = await GetAsync($"v1/games?since={since}&limit={limit}", "list", cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        var cards = new List<GameCard>();
        var rejected = 0;
        if (root.TryGetProperty("cards", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in array.EnumerateArray().Take(200))
            {
                var card = GameCard.FromElement(element);
                if (card is not null && card.TryVerify(out _)) cards.Add(card);
                else rejected++;
            }
        }
        var next = root.TryGetProperty("next", out var n) && n.TryGetInt64(out var nv) ? nv : since;
        var more = root.TryGetProperty("more", out var m) && m.ValueKind == JsonValueKind.True;
        return (cards, next, more, rejected);
    }

    public async Task PublishAsync(GameCard card, string tokenHash, CancellationToken cancellationToken = default)
    {
        card.CheckShape();
        using var _ = await PostAsync("v1/games/card", new { card, tokenHash }, "publish", cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveAsync(string publisherId, string id, string tokenHash, CancellationToken cancellationToken = default)
    {
        using var _ = await PostAsync("v1/games/remove", new { publisherId, id, tokenHash }, "remove", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Tell the registry that the computer at <paramref name="endpoint"/> (https://host:port#pin=…) can serve the game with this hash.</summary>
    public async Task AnnounceAsync(string sha256, string endpoint, CancellationToken cancellationToken = default)
    {
        using var _ = await PostAsync("v1/games/source", new { sha256, endpoint }, "announce", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Where to try downloading a game from, best first: each computer's own address, then the address the registry saw it come from.</summary>
    public async Task<List<string>> SourcesAsync(string sha256, CancellationToken cancellationToken = default)
    {
        using var document = await GetAsync("v1/games/sources?sha256=" + sha256, "sources", cancellationToken).ConfigureAwait(false);
        var list = new List<string>();
        if (document.RootElement.TryGetProperty("sources", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in array.EnumerateArray().Take(12))
            {
                if (entry.TryGetProperty("endpoint", out var e) && e.GetString() is { Length: > 0 } endpoint) list.Add(endpoint);
                if (entry.TryGetProperty("alt", out var a) && a.GetString() is { Length: > 0 } alt) list.Add(alt);
            }
        }
        return list;
    }

    private Task<JsonDocument> GetAsync(string relative, string action, CancellationToken cancellationToken) =>
        HttpRetry.RunAsync(async ct =>
        {
            using var response = await _http.GetAsync(new Uri(_baseUri, relative), ct).ConfigureAwait(false);
            return await ParseAsync(response, action, ct).ConfigureAwait(false);
        }, cancellationToken);

    // Publishing, announcing and removing are all safe to repeat (they set a state), so they are retried too
    private Task<JsonDocument> PostAsync(string relative, object body, string action, CancellationToken cancellationToken) =>
        HttpRetry.RunAsync(async ct =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, relative))
            {
                Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json")
            };
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            return await ParseAsync(response, action, ct).ConfigureAwait(false);
        }, cancellationToken);

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static async Task<JsonDocument> ParseAsync(HttpResponseMessage response, string action, CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        JsonDocument document;
        try { document = JsonDocument.Parse(text); }
        catch (JsonException)
        {
            throw new GameRegistryException($"The game directory returned an unreadable answer for {action} ({(int) response.StatusCode}).", response.StatusCode);
        }
        if (response.IsSuccessStatusCode) return document;
        using (document)
        {
            var error = document.RootElement.TryGetProperty("error", out var value) ? value.GetString() : null;
            throw new GameRegistryException(error ?? $"The game directory returned {(int) response.StatusCode} for {action}.", response.StatusCode);
        }
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
