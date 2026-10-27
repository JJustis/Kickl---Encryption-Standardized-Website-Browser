using System.Net;
using Kickl.Core.Ownership;
using Kickl.Core.Session;
using Kickl.Core.Util;
using Xunit;

namespace Kickl.Core.Tests;

public class ShopTrustTests
{
    private static BrowserSession Profile(string root, string id)
    {
        var s = new BrowserSession(root, id);
        s.CreateProfile("pin");
        s.Unlock("pin");
        return s;
    }

    [Fact]
    public void TheShopKeyIsOnlyEverLearnedOverHttpsFromSecupgradeCom()
    {
        Assert.Equal("secupgrade.com", SiteClaims.RequireOfficialShop(SiteClaims.DefaultShopUrl).Host);
        foreach (var bad in new[] { "http://secupgrade.com/btcshop/", "https://evil.example/btcshop/", "https://secupgrade.com.evil.example/", "https://secupgrade.com:8443/btcshop/" })
            Assert.Throws<ClaimException>(() => SiteClaims.RequireOfficialShop(bad));
    }

    [Fact]
    public async Task LearningTheKeyFromAnotherAddressIsRefusedBeforeAnyRequestIsMade()
    {
        var counter = new CountingHandler();
        using var http = new HttpClient(counter);
        await Assert.ThrowsAsync<ClaimException>(() => SiteClaims.FetchShopKeyAsync(http, "http://secupgrade.com/btcshop/"));
        await Assert.ThrowsAsync<ClaimException>(() => SiteClaims.FetchShopKeyAsync(http, "https://shop.evil.example/"));
        Assert.Equal(0, counter.Requests);
    }

    [Fact]
    public async Task WithBuiltInKeysNothingIsLearned_NoNetworkIsUsed_AndAnotherShopIsRefused()
    {
        using var temp = new TempDir();
        using var realShop = new FakeShop();
        using var otherShop = new FakeShop();
        SiteClaims.EmbeddedKeysOverride.Value = [realShop.Spki];            // a build that lists the real shop's key
        var counter = new CountingHandler();
        using var http = new HttpClient(counter);

        using var me = Profile(temp.Path, "me");
        await me.ImportCertAsync("good.com", realShop.Sign("good.com").ToBytes(), http);
        Assert.True(me.OwnershipOf("good.com").Owned);
        Assert.Equal(0, counter.Requests);                                   // the key was already known: nothing to fetch
        Assert.Null(SiteClaims.PinnedShopKey(new DataPaths(temp.Path, "me")));   // and nothing was pinned from the network

        // an attacker's shop, even on first contact, is refused
        await Assert.ThrowsAsync<ClaimException>(() =>
            me.ImportCertAsync("bad.com", otherShop.Sign("bad.com").ToBytes(), http));
        Assert.False(me.OwnershipOf("bad.com").Owned);
        Assert.Equal(0, counter.Requests);
    }

    [Fact]
    public async Task WithBuiltInKeysARotatedShopKeyWorksOnceItsListed()
    {
        using var temp = new TempDir();
        using var oldShop = new FakeShop();
        using var newShop = new FakeShop();
        using var http = new HttpClient(new CountingHandler());
        using var me = Profile(temp.Path, "me");

        SiteClaims.EmbeddedKeysOverride.Value = [oldShop.Spki];
        await Assert.ThrowsAsync<ClaimException>(() => me.ImportCertAsync("rot.com", newShop.Sign("rot.com").ToBytes(), http));

        SiteClaims.EmbeddedKeysOverride.Value = [oldShop.Spki, newShop.Spki];   // the next build lists both keys
        await me.ImportCertAsync("rot.com", newShop.Sign("rot.com").ToBytes(), http);
        await me.ImportCertAsync("old.com", oldShop.Sign("old.com").ToBytes(), http);
        Assert.True(me.OwnershipOf("rot.com").Owned);
        Assert.True(me.OwnershipOf("old.com").Owned);
    }

    [Fact]
    public void ABuildThatShipsNoKeysFallsBackToLearningThem()
    {
        SiteClaims.EmbeddedKeysOverride.Value = null;
        Assert.Empty(SiteClaims.EmbeddedShopKeys());                         // the shipped list is empty: TLS to secupgrade.com vouches for the key
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
