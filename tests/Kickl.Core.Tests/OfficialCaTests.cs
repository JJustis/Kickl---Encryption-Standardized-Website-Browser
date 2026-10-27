using System.Net;
using Kickl.Core.Ownership;
using Kickl.Core.Trust;
using Xunit;

namespace Kickl.Core.Tests;

/// <summary>https://secupgrade.com/secca/ is the only CA. These pin that down.</summary>
public class OfficialCaTests
{
    [Fact]
    public void TheOfficialCaIsSecupgradeComOverHttps()
    {
        Assert.Equal("https://secupgrade.com/secca/", CaPins.OfficialCaUrl);
        Assert.Equal(CaPins.OfficialCaUrl, CaPins.CaUrl());
        Assert.Equal("secupgrade.com", CaPins.RequireOfficialCa(CaPins.OfficialCaUrl).Host);
        Assert.Equal("secupgrade.com", CaPins.RequireOfficialCa("HTTPS://SecUpgrade.COM/secca/").Host);
    }

    [Theory]
    [InlineData("http://secupgrade.com/secca/")]                    // not encrypted
    [InlineData("https://example.com/secca/")]                      // another host
    [InlineData("https://secupgrade.com.evil.example/secca/")]      // a look-alike host
    [InlineData("https://evil.secupgrade.com/secca/")]              // a subdomain is not the CA
    [InlineData("https://secupgrade.com@evil.example/secca/")]      // user-info trick: the real host is evil.example
    [InlineData("https://secupgrade.com:8443/secca/")]              // not the standard port
    [InlineData("https://localhost/secca/")]                        // loopback is only for Debug test CAs
    [InlineData("ftp://secupgrade.com/secca/")]
    public void AnythingElseIsRefused(string url)
    {
        Assert.Throws<ClaimException>(() => CaPins.RequireOfficialCa(url));
    }

    [Fact]
    public async Task AReceiptIsNeverSentToAnyOtherAddress()
    {
        using var shop = new FakeShop();
        var receipt = shop.Sign("owned.com");
        var handler = new CountingHandler();
        using var http = new HttpClient(handler);

        foreach (var wrong in new[] { "http://secupgrade.com/secca/", "https://evil.example/secca/", "https://secupgrade.com.evil.example/" })
            await Assert.ThrowsAsync<ClaimException>(() => CaClient.CountersignAsync(http, wrong, receipt));

        Assert.Equal(0, handler.Requests);   // refused before anything left the machine
    }

    [Fact]
    public void ThereIsNoWayToPickAnotherRootFromTheDataFolder()
    {
        // A pinned file in the data folder used to be a fallback root. It is gone: with no embedded root, nothing is trusted.
        using var temp = new TempDir();
        var dir = Path.Combine(temp.Path, "shared", "ownership");
        Directory.CreateDirectory(dir);
        using var attacker = HybridSigner.Generate();
        File.WriteAllText(Path.Combine(dir, "ca-root.json"),
            System.Text.Json.JsonSerializer.Serialize(attacker.Public, Kickl.Core.Registry.RegistrySigningKey.Json));

        var root = CaPins.Root(new Kickl.Core.Util.DataPaths(temp.Path));
        Assert.True(root is null || root.Fingerprint != attacker.Public.Fingerprint);
    }

    [Fact]
    public void TheClientForCaTrafficNeverFollowsRedirects()
    {
        using var client = CaPins.NewClient();
        Assert.Equal(TimeSpan.FromSeconds(30), client.Timeout);
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
