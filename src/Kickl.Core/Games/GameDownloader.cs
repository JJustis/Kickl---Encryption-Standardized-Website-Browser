using System.Security.Authentication;
using Kickl.Core.Mail;

namespace Kickl.Core.Games;

/// <summary>
/// Fetch a game from another computer. The caller supplies the places to try (the registry's list of sources); each is contacted over TLS 1.3
/// pinned to the key in its address, and whatever it sends is accepted only if it is exactly the file the signed card names: right length,
/// right SHA-256. A computer that is offline, slow, lying, or pointing somewhere else is just skipped, so any one honest source is enough.
/// </summary>
public static class GameDownloader
{
    public const int MaxSourcesTried = 8;

    /// <summary>The bytes of the game named by <paramref name="card"/>, from the first source that has them. Throws with every reason if none does.</summary>
    public static async Task<byte[]> FetchAsync(GameCard card, IEnumerable<string> endpoints, Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!card.TryVerify(out var reason)) throw new GameCardException("This card is not genuine: " + reason);

        var failures = new List<string>();
        var tried = 0;
        foreach (var endpoint in endpoints.Distinct(StringComparer.Ordinal))
        {
            if (tried++ >= MaxSourcesTried) break;
            cancellationToken.ThrowIfCancellationRequested();

            Uri address;
            string pin;
            try { (address, pin) = MailEndpoint.Secure(endpoint); }
            catch (FormatException ex) { failures.Add(ex.Message); continue; }

            progress?.Invoke($"asking {address.Host}:{address.Port} …");
            try
            {
                var bytes = await FetchOneAsync(card, address, pin, cancellationToken).ConfigureAwait(false);
                progress?.Invoke($"received {bytes.Length:N0} bytes from {address.Host}");
                return bytes;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or AuthenticationException or IOException
                                           or GameCardException or InvalidOperationException)
            {
                if (cancellationToken.IsCancellationRequested) throw;
                failures.Add($"{address.Host}:{address.Port}: {Describe(ex)}");
            }
        }

        throw new GameCardException(failures.Count == 0
            ? "No computer is offering this game right now. Try again when its publisher, or someone who has downloaded it, is online."
            : "No computer could provide this game right now (" + string.Join("; ", failures.Take(3)) + ").");
    }

    private static async Task<byte[]> FetchOneAsync(GameCard card, Uri address, string pin, CancellationToken cancellationToken)
    {
        using var http = PinnedHttp.Create(pin, TimeSpan.FromSeconds(45), card.Size + 1);
        using var response = await http.GetAsync(new Uri(address, "/v1/game/" + card.Sha256), HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new GameCardException($"it answered {(int) response.StatusCode} (it does not hold this game)");
        if (response.Content.Headers.ContentLength is { } length && length != card.Size)
            throw new GameCardException($"it offered {length:N0} bytes but the card says {card.Size:N0}");

        // Never read more than the card says the file is: a peer cannot make us buffer more
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[card.Size];
        var filled = 0;
        while (filled < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(filled), cancellationToken).ConfigureAwait(false);
            if (n == 0) break;
            filled += n;
        }
        if (filled != buffer.Length || await stream.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false) != 0)
            throw new GameCardException("it sent a different number of bytes than the card names");
        if (!card.Matches(buffer)) throw new GameCardException("what it sent is not the file the card names (the SHA-256 differs)");
        return buffer;
    }

    private static string Describe(Exception ex) => ex switch
    {
        AuthenticationException => "it did not present the key its address named",
        TaskCanceledException => "no answer in time",
        HttpRequestException h when h.GetBaseException() is AuthenticationException => "it did not present the key its address named",
        HttpRequestException h => h.GetBaseException().Message,
        _ => ex.Message
    };
}
