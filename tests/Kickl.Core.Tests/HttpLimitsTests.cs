using System.Net;
using Kickl.Core.Trust;
using Kickl.Core.Util;
using Xunit;

namespace Kickl.Core.Tests;

public class HttpLimitsTests
{
    private sealed class Big(int bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('x', bytes)) });
    }

    [Fact]
    public async Task AnOversizedReplyIsRefusedInsteadOfBuffered()
    {
        using var http = new HttpClient(new Big(5_000)).Limit(1_024);
        await Assert.ThrowsAsync<HttpRequestException>(() => http.GetStringAsync("https://secupgrade.com/x"));

        using var fine = new HttpClient(new Big(500)).Limit(1_024);
        Assert.Equal(500, (await fine.GetStringAsync("https://secupgrade.com/x")).Length);
    }

    [Fact]
    public void TheCeilingsAreSetOnEveryClientThatTalksToAServerWeDoNotControl()
    {
        using var ca = CaPins.NewClient();
        Assert.Equal(HttpLimits.CaAndShop, ca.MaxResponseContentBufferSize);
        Assert.True(HttpLimits.CaAndShop < 2_000_000_000L);

        using var chat = new Kickl.Core.FreeChat.FreeChatClient();
        using var mail = new Kickl.Core.Mail.MailClient();
        using var registryClient = new HttpClient().Limit(HttpLimits.Registry);
        Assert.Equal(HttpLimits.Registry, registryClient.MaxResponseContentBufferSize);
    }

    [Fact]
    public void LimitNeverRaisesACeilingAndToleratesAClientAlreadyInUse()
    {
        using var http = new HttpClient { MaxResponseContentBufferSize = 100 };
        http.Limit(5_000);
        Assert.Equal(100, http.MaxResponseContentBufferSize);   // a lower cap stays
    }
}
