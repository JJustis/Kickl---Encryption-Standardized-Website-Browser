using System.Net;
using System.Text;
using System.Text.Json;
using Kickl.Core.Documents;
using Kickl.Core.Ownership;
using Kickl.Core.Session;
using Kickl.Core.Trust;
using Kickl.Core.Util;
using Xunit;

namespace Kickl.Core.Tests;

/// <summary>A stand-in for the CA web service that runs the real CA logic behind the same three endpoints.</summary>
internal sealed class FakeCaServer
{
    private static readonly JsonSerializerOptions Camel = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public readonly CaService Ca;
    public readonly IssuerCertificate Issuer;
    public RevocationList? Revocations;
    public int Requests;

    public FakeCaServer(CaFixture f)
    {
        Ca = f.Ca;
        Issuer = f.Issuer;
    }

    public HttpClient Client() => new(new Handler(this));

    private sealed class Handler(FakeCaServer server) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            server.Requests++;
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/v1/ca/root"))
                return Json(HttpStatusCode.OK, new { root = new { }, issuer = server.Issuer });
            if (path.EndsWith("/v1/ca/revocations"))
                return Json(HttpStatusCode.OK, server.Revocations ?? new RevocationList());
            if (path.EndsWith("/v1/ca/countersign"))
            {
                var body = await request.Content!.ReadAsByteArrayAsync(ct);
                try
                {
                    var receipt = OwnershipCert.Parse(body);
                    Assert.Null(receipt.Seal);   // the client never sends an earlier seal
                    return Json(HttpStatusCode.OK, new { seal = server.Ca.Countersign(receipt, DateTimeOffset.UtcNow, server.Revocations) });
                }
                catch (CaRejectedException ex)
                {
                    return Json(HttpStatusCode.Conflict, new { error = ex.Code, message = ex.Message });
                }
            }
            return Json(HttpStatusCode.NotFound, new { error = "unknown" });
        }

        private static HttpResponseMessage Json(HttpStatusCode code, object value) => new(code)
        {
            Content = new StringContent(JsonSerializer.Serialize(value, Camel), Encoding.UTF8, "application/json")
        };
    }
}

public class SessionCaTests
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
    public async Task ASealedOwnerShowsAsVerifiedToEveryVisitor_NotJustTheOwner()
    {
        using var temp = new TempDir();
        using var f = new CaFixture();
        CaPins.RootOverride.Value = f.RootPublic;
        var ca = new FakeCaServer(f);

        using var owner = Profile(temp.Path, "owner", "pin-o");
        owner.CreateSite(Draft("verified.com", "v1"));
        await owner.ImportCertAsync("verified.com", f.Shop.Sign("verified.com").ToBytes(), f.Shop.Client());
        Assert.False(owner.OwnershipOf("verified.com").Verified);                     // paid, but not yet sealed

        var check = await owner.SubmitToCaAsync("verified.com", ca.Client());
        Assert.True(check.Verified, check.Reason);
        Assert.True(owner.OwnershipOf("verified.com").Verified);

        // a different login that holds no certificate at all opens the page and sees the badge
        using var visitor = Profile(temp.Path, "visitor", "pin-v");
        visitor.Open("jttp://verified.com/");
        Assert.True(visitor.OwnershipOf("verified.com").Verified);
        Assert.False(visitor.HoldsCert("verified.com"));

        // editing keeps the seal and never changes what the registry hash covers
        var before = owner.Document!.Integrity;
        owner.CreateSite(Draft("verified.com", "v2 after sealing"));
        Assert.True(owner.OwnershipOf("verified.com").Verified);
        visitor.Open("jttp://verified.com/");
        Assert.True(visitor.OwnershipOf("verified.com").Verified);
        Assert.NotEqual(before, visitor.Document!.Integrity);                          // the content did change
    }

    [Fact]
    public async Task ASealedCertificateDownloadedFromTheCaPublishesItsSealWhenImported()
    {
        using var temp = new TempDir();
        using var f = new CaFixture();
        CaPins.RootOverride.Value = f.RootPublic;
        var ca = new FakeCaServer(f);

        using var owner = Profile(temp.Path, "owner", "pin-o");
        owner.CreateSite(Draft("carried.com", "v1"));
        var receipt = f.Shop.Sign("carried.com");

        // the owner sealed it on the CA's website: the file they downloaded is the receipt plus the seal
        var seal = f.Ca.Countersign(receipt, DateTimeOffset.UtcNow);
        receipt.Seal = seal;

        // importing that single file is enough; no second trip to the CA
        await owner.ImportCertAsync("carried.com", receipt.ToBytes(), f.Shop.Client());
        Assert.True(owner.OwnershipOf("carried.com").Verified);

        using var visitor = Profile(temp.Path, "visitor", "pin-v");
        visitor.Open("jttp://carried.com/");
        Assert.True(visitor.OwnershipOf("carried.com").Verified);
        Assert.Equal(0, ca.Requests);
    }

    [Fact]
    public async Task ASealedCertificateWithABadSealIsImportedButNeverPublishedAsVerified()
    {
        using var temp = new TempDir();
        using var f = new CaFixture();
        using var rogue = new CaFixture();
        CaPins.RootOverride.Value = f.RootPublic;

        using var owner = Profile(temp.Path, "owner", "pin-o");
        owner.CreateSite(Draft("forged.com", "v1"));
        var receipt = f.Shop.Sign("forged.com");
        receipt.Seal = rogue.Ca.Countersign(rogue.Shop.Sign("forged.com"), DateTimeOffset.UtcNow);   // sealed by a CA nobody trusts

        await owner.ImportCertAsync("forged.com", receipt.ToBytes(), f.Shop.Client());
        Assert.False(owner.OwnershipOf("forged.com").Verified);
        using var visitor = Profile(temp.Path, "visitor", "pin-v");
        visitor.Open("jttp://forged.com/");
        Assert.False(visitor.OwnershipOf("forged.com").Verified);
    }

    [Fact]
    public async Task ASealFromAnImpostorCaIsDiscardedAndNeverShowsABadge()
    {
        using var temp = new TempDir();
        using var real = new CaFixture();
        using var fake = new CaFixture();                    // a second CA with its own root, sharing nothing
        CaPins.RootOverride.Value = real.RootPublic;         // this browser trusts only the real root

        using var owner = Profile(temp.Path, "owner", "pin-o");
        owner.CreateSite(Draft("imp.com", "v1"));
        var receipt = fake.Shop.Sign("imp.com");
        // the impostor CA trusts the impostor shop; to import we use the same shop on the browser side
        await owner.ImportCertAsync("imp.com", receipt.ToBytes(), fake.Shop.Client());

        var ex = await Assert.ThrowsAsync<ClaimException>(() =>
            owner.SubmitToCaAsync("imp.com", new FakeCaServer(fake).Client()));
        Assert.Contains("did not verify", ex.Message);
        Assert.False(owner.OwnershipOf("imp.com").Verified);
    }

    [Fact]
    public async Task TheCaRefusesAConflictingReceipt_AndTheOwnerIsToldWhy()
    {
        using var temp = new TempDir();
        using var f = new CaFixture();
        CaPins.RootOverride.Value = f.RootPublic;
        var ca = new FakeCaServer(f);

        using var first = Profile(temp.Path, "first", "p1");
        using var second = Profile(temp.Path, "second", "p2");
        await first.ImportCertAsync("clash.com", f.Shop.Sign("clash.com", order: 1).ToBytes(), f.Shop.Client());
        await second.ImportCertAsync("clash.com", f.Shop.Sign("clash.com", order: 2).ToBytes(), f.Shop.Client());

        Assert.True((await first.SubmitToCaAsync("clash.com", ca.Client())).Verified);
        var ex = await Assert.ThrowsAsync<ClaimException>(() => second.SubmitToCaAsync("clash.com", ca.Client()));
        Assert.Contains("refused", ex.Message);
        Assert.Contains(f.Store.Alerts, a => a.Kind == "conflict");
        Assert.False(second.OwnershipOf("clash.com").Verified);
    }

    [Fact]
    public async Task ARevokedSealStopsShowingTheBadgeOnceTheRevocationListIsFetched()
    {
        using var temp = new TempDir();
        using var f = new CaFixture();
        CaPins.RootOverride.Value = f.RootPublic;
        var ca = new FakeCaServer(f);

        using var owner = Profile(temp.Path, "owner", "pin-o");
        owner.CreateSite(Draft("revoke.com", "v1"));
        await owner.ImportCertAsync("revoke.com", f.Shop.Sign("revoke.com").ToBytes(), f.Shop.Client());
        await owner.SubmitToCaAsync("revoke.com", ca.Client());

        using var visitor = Profile(temp.Path, "visitor", "pin-v");
        visitor.Open("jttp://revoke.com/");
        Assert.True(visitor.OwnershipOf("revoke.com").Verified);

        ca.Revocations = f.Ca.Revoke(null, [new RevokedSeal { Site = "revoke.com", Seq = 1, Reason = "abuse" }], DateTimeOffset.UtcNow);
        await visitor.RefreshCaRevocationsAsync(ca.Client());
        Assert.False(visitor.OwnershipOf("revoke.com").Verified);
        Assert.Contains("revoked", visitor.OwnershipOf("revoke.com").VerifiedDetail);

        // a replayed OLDER list cannot un-revoke it
        var older = new RevocationList { Number = 0 };
        ca.Revocations = older;
        await visitor.RefreshCaRevocationsAsync(ca.Client());
        Assert.False(visitor.OwnershipOf("revoke.com").Verified);
    }

    [Fact]
    public async Task ASealPastedOntoAnotherSiteDoesNotVerifyThere()
    {
        using var temp = new TempDir();
        using var f = new CaFixture();
        CaPins.RootOverride.Value = f.RootPublic;
        var ca = new FakeCaServer(f);

        using var owner = Profile(temp.Path, "owner", "pin-o");
        owner.CreateSite(Draft("real.com", "mine"));
        await owner.ImportCertAsync("real.com", f.Shop.Sign("real.com").ToBytes(), f.Shop.Client());
        await owner.SubmitToCaAsync("real.com", ca.Client());
        var seal = owner.Document!.Seal!;

        using var attacker = Profile(temp.Path, "attacker", "pin-a");
        attacker.CreateSite(Draft("copycat.com", "I want a badge"));
        DocumentStore.AttachSeal(new DataPaths(temp.Path, "attacker"), "copycat.com", seal);
        attacker.Open("jttp://copycat.com/");

        Assert.NotNull(attacker.Document!.Seal);
        Assert.False(attacker.OwnershipOf("copycat.com").Verified);
        Assert.Contains("real.com", attacker.OwnershipOf("copycat.com").VerifiedDetail);
    }
}
