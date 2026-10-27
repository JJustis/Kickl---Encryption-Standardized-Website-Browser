namespace Kickl.Core.Util;

/// <summary>
/// Retry for requests that are safe to repeat. A failed connection or a timeout (the network is flaky, not the answer) is tried again
/// up to <see cref="MaxAttempts"/> times; anything the server actually answered, even with an error, is final.
/// </summary>
public static class HttpRetry
{
    public const int MaxAttempts = 4;

    public static async Task<T> RunAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation().ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < MaxAttempts && !cancellationToken.IsCancellationRequested && IsTransient(ex))
            {
                await Task.Delay(200 * attempt, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public static bool IsTransient(Exception ex) =>
        ex is HttpRequestException { StatusCode: null } || ex is TaskCanceledException { InnerException: TimeoutException } || ex is TimeoutException;
}
