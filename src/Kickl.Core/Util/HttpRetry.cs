namespace Kickl.Core.Util;

/// <summary>
/// Retry for requests that are safe to repeat (a lookup, or setting a value). Home and office networks lose the first packet of a connection, or
/// silently forget an idle connection that a program is about to reuse; the request then goes nowhere and waits for its whole timeout. Two things
/// keep that cheap: every attempt has a short deadline, and when an attempt is slow a second one is raced against it (the first answer wins), so a
/// stalled connection costs a couple of seconds, not a timeout. An answer from the server, even an error, is final and never retried.
/// </summary>
public static class HttpRetry
{
    public const int MaxAttempts = 4;

    /// <summary>How long one attempt may take before it is abandoned.</summary>
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(8);

    /// <summary>How long to wait for a running attempt before racing another against it.</summary>
    public static readonly TimeSpan HedgeAfter = TimeSpan.FromMilliseconds(2500);

    public static async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken,
        TimeSpan? attemptTimeout = null, TimeSpan? hedgeAfter = null)
    {
        var perAttempt = attemptTimeout ?? AttemptTimeout;
        var hedge = hedgeAfter ?? HedgeAfter;
        using var all = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var running = new List<Task<T>>();
        Exception? last = null;

        async Task<T> One()
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(all.Token);
            attempt.CancelAfter(perAttempt);
            try { return await operation(attempt.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!all.IsCancellationRequested)
            {
                throw new TimeoutException("The request did not complete in time.");   // this attempt ran out of time; the caller did not cancel
            }
        }

        for (var started = 0; started < MaxAttempts;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            running.Add(One());
            started++;
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var hedgeTimer = Task.Delay(hedge, wait.Token);

            // Wait for an answer; if none comes in time (and attempts remain) the loop races another attempt against the slow one
            while (running.Count > 0)
            {
                var finished = await Task.WhenAny(running.Cast<Task>().Append(hedgeTimer)).ConfigureAwait(false);
                if (ReferenceEquals(finished, hedgeTimer)) break;
                var done = (Task<T>) finished;
                running.Remove(done);
                if (done.IsCompletedSuccessfully)
                {
                    all.Cancel();
                    wait.Cancel();
                    return done.Result;
                }
                last = FailureOf(done);
                if (!IsTransient(last) || cancellationToken.IsCancellationRequested)
                {
                    all.Cancel();
                    wait.Cancel();
                    throw last;
                }
                if (running.Count == 0 && started < MaxAttempts)
                {
                    await Task.Delay(150 * started, cancellationToken).ConfigureAwait(false);   // everything running failed: try again at once, after a breath
                    break;
                }
            }
            wait.Cancel();
        }

        // every attempt is under way; take the first success, or the last failure
        while (running.Count > 0)
        {
            var done = await Task.WhenAny(running).ConfigureAwait(false);
            running.Remove(done);
            if (done.IsCompletedSuccessfully) { all.Cancel(); return done.Result; }
            last = FailureOf(done);
            if (!IsTransient(last)) { all.Cancel(); throw last; }
        }
        throw last ?? new TimeoutException("The request did not complete.");
    }

    /// <summary>
    /// Why a finished task did not succeed. A task that was cancelled (rather than faulted) has no Exception to read, so ask the task itself;
    /// this returns the real exception, an OperationCanceledException for a cancellation.
    /// </summary>
    private static Exception FailureOf<T>(Task<T> task)
    {
        try { task.GetAwaiter().GetResult(); }
        catch (Exception ex) { return ex; }
        return new InvalidOperationException("The attempt finished without a result.");
    }

    public static bool IsTransient(Exception ex) =>
        ex is HttpRequestException { StatusCode: null } || ex is TaskCanceledException { InnerException: TimeoutException } || ex is TimeoutException;
}
