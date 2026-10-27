using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kickl.Core.Documents;
using Kickl.Core.Ownership;
using Kickl.Core.Session;
using Kickl.Core.Util;
using Xunit;

namespace Kickl.Core.Tests;

/// <summary>Stands in for the BTC shop: signs ownership certificates with a key the test controls.</summary>
internal sealed class FakeShop : IDisposable
{
    private readonly RSA _rsa = RSA.Create(2048);
    public string Spki => Convert.ToBase64String(_rsa.ExportSubjectPublicKeyInfo());

    public OwnershipCert Sign(string site, int seq = 1, long order = 7, TimeSpan? lasts = null, string? ownerPassphrase = null)
    {
        var paid = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds();
        var cert = new OwnershipCert
        {
            Site = site, Order = order, Seq = seq, PaidAt = paid,
            Expires = paid + (long)(lasts ?? TimeSpan.FromDays(365)).TotalSeconds,
            Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
            Txid = "tx" + order
        };
        if (ownerPassphrase is not null)
            (cert.BoundKdf, cert.BoundSalt, cert.BoundBinding) = OwnerBinding.CreateForShop(ownerPassphrase, site);
        cert.Sig = Convert.ToBase64String(_rsa.SignData(Utf8.B(cert.Payload()),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        return cert;
    }

    /// <summary>An HttpClient that answers the shop's pubkey call.</summary>
    public HttpClient Client() => new(new Handler(this));

    public void Dispose() => _rsa.Dispose();

    private sealed class Handler(FakeShop shop) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { spki = shop.Spki }), Encoding.UTF8, "application/json")
            });
    }
}

public class ClaimTests
{
    private static BrowserSession Profile(string root, string id, string pin)
    {
        var s = new BrowserSession(root, id);
        s.CreateProfile(pin);
        s.Unlock(pin);
        return s;
    }

    private static StoryDraft Draft(string host, string headline) => new()
    {
        Host = host, Headline = headline, Author = "t", Date = "2026-10-26",
        Html = "<div><p>" + headline + "</p></div>", Tags = ["demo", "claim"]
    };

    [Fact]
    public async Task UnclaimedNamesAreFreeForEveryone_ClaimedOnesNeedTheCertificate()
    {
        using var temp = new TempDir();
        using var shop = new FakeShop();
        using var buyer = Profile(temp.Path, "buyer", "pin-1");
        using var other = Profile(temp.Path, "other", "pin-2");

        other.CreateSite(Draft("claimme.com", "Other made it"));   // free while unclaimed
        buyer.CreateSite(Draft("claimme.com", "Buyer edits it"));  // anyone may
        Assert.Equal((false, true), other.EditAccess("claimme.com"));

        var cert = shop.Sign("claimme.com");
        await buyer.ImportCertAsync("claimme.com", cert.ToBytes(), shop.Client());

        Assert.True(buyer.OwnershipOf("claimme.com").Owned);
        buyer.CreateSite(Draft("claimme.com", "Owner update"));

        var ex = Assert.Throws<ClaimException>(() => other.CreateSite(Draft("claimme.com", "Hijack")));
        Assert.Contains("/edit", ex.Message);
        Assert.Equal((true, false), other.EditAccess("claimme.com"));
        Assert.Equal((true, true), buyer.EditAccess("claimme.com"));
        Assert.Equal((false, true), buyer.EditAccess("somethingelse.com"));
    }

    [Fact]
    public async Task TheCertificateIsPortable_AnyLoginHoldingTheFileCanEdit_AndItIsKeptEncrypted()
    {
        using var temp = new TempDir();
        using var shop = new FakeShop();
        using var owner = Profile(temp.Path, "owner", "pin-o");
        using var guest = Profile(temp.Path, "guest", "pin-g");

        var cert = shop.Sign("mine.com");
        var file = cert.ToBytes();
        await owner.ImportCertAsync("mine.com", file, shop.Client());
        owner.CreateSite(Draft("mine.com", "v1"));

        Assert.Throws<ClaimException>(() => guest.CreateSite(Draft("mine.com", "nope")));
        await guest.ImportCertAsync("mine.com", file, shop.Client());   // same file, other login
        guest.CreateSite(Draft("mine.com", "edited with the certificate"));

        // remembered across lock and unlock, but only in this profile's encrypted store
        guest.Lock();
        Assert.Equal((true, false), guest.EditAccess("mine.com"));
        guest.Unlock("pin-g");
        Assert.Equal((true, true), guest.EditAccess("mine.com"));

        var stored = File.ReadAllText(Path.Combine(temp.Path, "profiles", "guest", "private", "certs.kpf"));
        Assert.DoesNotContain(cert.Token, stored);

        var copy = owner.ExportCert("mine.com", "a long passphrase");
        Assert.True(SealedCert.IsSealed(copy));
        Assert.DoesNotContain(cert.Token, System.Text.Encoding.UTF8.GetString(copy));
        Assert.Equal(cert.Token, OwnershipCert.Parse(SealedCert.Open(copy, "a long passphrase")).Token);
        Assert.Throws<ClaimException>(() => SealedCert.Open(copy, "wrong passphrase"));
        Assert.Throws<ClaimException>(() => SealedCert.Open(copy, ""));
        Assert.Throws<ClaimException>(() => owner.ExportCert("mine.com", "short"));
        Assert.Throws<ClaimException>(() => guest.ExportCert("unrelated.com", "a long passphrase"));
    }

    [Fact]
    public void SealedCopy_IsBoundToItsSite_AndDetectsTampering()
    {
        var cert = new OwnershipCert { Site = "a.com", Token = new string('a', 64), Sig = "x" };
        var copy = SealedCert.Seal(cert, "a long passphrase");

        var retargeted = System.Text.Encoding.UTF8.GetString(copy).Replace("\"site\": \"a.com\"", "\"site\": \"b.com\"");
        Assert.Throws<ClaimException>(() => SealedCert.Open(System.Text.Encoding.UTF8.GetBytes(retargeted), "a long passphrase"));

        var bad = copy.ToArray();
        bad[^30] ^= 1;
        Assert.Throws<ClaimException>(() => SealedCert.Open(bad, "a long passphrase"));
        Assert.False(SealedCert.IsSealed(cert.ToBytes()));
    }

    [Fact]
    public async Task ABadCertificateIsRefused()
    {
        using var temp = new TempDir();
        using var shop = new FakeShop();
        using var imposter = new FakeShop();
        using var me = Profile(temp.Path, "me", "pin");

        await Assert.ThrowsAsync<ClaimException>(() =>
            me.ImportCertAsync("x.com", Encoding.UTF8.GetBytes("not a certificate"), shop.Client()));

        // for another name
        await Assert.ThrowsAsync<ClaimException>(() =>
            me.ImportCertAsync("x.com", shop.Sign("y.com").ToBytes(), shop.Client()));

        // altered after signing
        var tampered = shop.Sign("x.com");
        tampered.Seq = 9;
        await Assert.ThrowsAsync<ClaimException>(() => me.ImportCertAsync("x.com", tampered.ToBytes(), shop.Client()));

        // expired
        await Assert.ThrowsAsync<ClaimException>(() =>
            me.ImportCertAsync("x.com", shop.Sign("x.com", lasts: TimeSpan.FromSeconds(-1000)).ToBytes(), shop.Client()));

        Assert.False(me.OwnershipOf("x.com").Owned);

        // the first genuine certificate pins the shop; a look-alike shop is then refused
        await me.ImportCertAsync("x.com", shop.Sign("x.com").ToBytes(), shop.Client());
        await Assert.ThrowsAsync<ClaimException>(() =>
            me.ImportCertAsync("z.com", imposter.Sign("z.com").ToBytes(), imposter.Client()));
        Assert.False(me.OwnershipOf("z.com").Owned);
    }

    [Fact]
    public async Task AnOutOfDateCertificateIsRefusedOnceTheNameIsClaimedAgain()
    {
        using var temp = new TempDir();
        using var shop = new FakeShop();
        using var first = Profile(temp.Path, "first", "p1");
        using var second = Profile(temp.Path, "second", "p2");

        var old = shop.Sign("flip.com", seq: 1);
        await first.ImportCertAsync("flip.com", old.ToBytes(), shop.Client());

        // the name lapses and someone else buys it: seq 2
        await second.ImportCertAsync("flip.com", shop.Sign("flip.com", seq: 2, order: 8).ToBytes(), shop.Client());

        Assert.Equal((true, false), first.EditAccess("flip.com"));   // seq 1 no longer matches the claim
        await Assert.ThrowsAsync<ClaimException>(() => first.ImportCertAsync("flip.com", old.ToBytes(), shop.Client()));
        Assert.Throws<ClaimException>(() => first.CreateSite(Draft("flip.com", "stale")));
        second.CreateSite(Draft("flip.com", "current owner"));
    }
}
