using System.Diagnostics;
using Kickl.Core.Mail;
using Kickl.Core.Util;
using Xunit;

namespace Kickl.Core.Tests;

/// <summary>
/// A request into a connection that a router has quietly forgotten goes nowhere and waits for its whole timeout. These tests pin the behaviour that
/// keeps that cheap: a slow attempt is raced by another, every attempt has a short deadline, and an answer from the server is final.
/// </summary>
public class HttpRetryTests
{
    [Fact]
    public async Task AHungFirstAttempt_IsRacedByASecond_AndTheSecondWins_InAboutTheHedgeDelay()
    {
        var calls = 0;
        var sw = Stopwatch.StartNew();
        var result = await HttpRetry.RunAsync<string>(async ct =>
        {
            if (Interlocked.Increment(ref calls) == 1) await Task.Delay(Timeout.Infinite, ct);   // sent into a dead connection: no answer, ever
            return "answer";
        }, CancellationToken.None, attemptTimeout: TimeSpan.FromSeconds(20), hedgeAfter: TimeSpan.FromMilliseconds(300));
        Assert.Equal("answer", result);
        Assert.Equal(2, calls);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed}: it waited for the hung attempt instead of racing it");
    }

    [Fact]
    public async Task AnAttemptThatRunsOutOfTime_IsAbandoned_AndTheNextOneUsed()
    {
        var calls = 0;
        var result = await HttpRetry.RunAsync<int>(async ct =>
        {
            if (Interlocked.Increment(ref calls) <= 2) await Task.Delay(Timeout.Infinite, ct);
            return 7;
        }, CancellationToken.None, attemptTimeout: TimeSpan.FromMilliseconds(250), hedgeAfter: TimeSpan.FromSeconds(30));
        Assert.Equal(7, result);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task IfEveryAttemptHangs_ItGivesUpWithinABoundedTime()
    {
        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() => HttpRetry.RunAsync<int>(
            async ct => { await Task.Delay(Timeout.Infinite, ct); return 0; }, CancellationToken.None,
            attemptTimeout: TimeSpan.FromMilliseconds(400), hedgeAfter: TimeSpan.FromMilliseconds(100)));
        // four attempts raced 100 ms apart, each dead after 400 ms: well under a second and a half, not four timeouts in a row
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), $"took {sw.Elapsed}");   // generous: the whole suite runs in parallel
    }

    [Fact]
    public async Task AnAnswerFromTheServer_IsFinal_EvenWhileAnotherAttemptIsStillRunning()
    {
        var calls = 0;
        await Assert.ThrowsAsync<MailServiceException>(() => HttpRetry.RunAsync<int>(async ct =>
        {
            if (Interlocked.Increment(ref calls) == 1) await Task.Delay(Timeout.Infinite, ct);
            throw new MailServiceException("No mail identity has that PFS id.", System.Net.HttpStatusCode.NotFound);
        }, CancellationToken.None, attemptTimeout: TimeSpan.FromSeconds(20), hedgeAfter: TimeSpan.FromMilliseconds(100)));
        Assert.Equal(2, calls);                                   // not retried a third time
    }

    [Fact]
    public async Task ACancelledCaller_StopsEverything()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HttpRetry.RunAsync<int>(
            async ct => { await Task.Delay(Timeout.Infinite, ct); return 0; }, cts.Token,
            attemptTimeout: TimeSpan.FromSeconds(30), hedgeAfter: TimeSpan.FromSeconds(30)));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task TheMailClient_RecoversFromAHungConnection_WithoutWaitingOutTheTimeout()
    {
        using var id = MailIdentity.Generate("rt");
        var body = System.Text.Json.JsonSerializer.Serialize(new { bundle = id.Bundle }, MailJson.Options);
        var handler = new HangingFirst(body);
        using var client = new MailClient("https://example.test/api", new HttpClient(handler));
        var sw = Stopwatch.StartNew();
        var bundle = await client.LookupAsync(id.Bundle.ShortId);
        Assert.Equal(id.Bundle.ShortId, bundle.ShortId);
        Assert.Equal(2, handler.Calls);
        // the hedge delay is 2.5 s: recovery takes about that, not the 8 s of one attempt, and nothing like the 60 s client timeout
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"took {sw.Elapsed}");   // generous: the whole suite runs in parallel
    }

    private sealed class HangingFirst(string body) : HttpMessageHandler
    {
        public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref Calls) == 1) await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            };
        }
    }
}
