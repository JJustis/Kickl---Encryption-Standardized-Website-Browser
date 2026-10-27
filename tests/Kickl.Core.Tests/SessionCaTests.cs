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

public class OwnerBindingTests
{
    private static BrowserSession Profile(string root, string id, string pin)
    {
        var s = new BrowserSession(root, id);
        s.CreateProfile(pin);
        s.Unlock(pin);
        return s;
    }

    private static StoryDraft Draft(string host) => new()
    {
        Host = host, Headline = "h", Author = "t", Date = "2026-10-26", Html = "<div><p>x</p></div>", Tags = ["demo", "claim"]
    };

    [Fact]
    public void Binding_RoundTrips_AndRejectsOtherPassphrases()
    {
        var commitment = new string('a', 64);
        var (salt, mac) = OwnerBinding.Create("correct horse battery", "a.com", 3, commitment);
        var seal = new CaSeal { Site = "a.com", Seq = 3, TokenCommitment = commitment, OwnerSalt = salt, OwnerBinding = mac };
        Assert.True(OwnerBinding.Verify(seal, "correct horse battery"));
        Assert.False(OwnerBinding.Verify(seal, "wrong horse battery!"));
        Assert.False(OwnerBinding.Verify(seal, ""));
        Assert.True(OwnerBinding.Verify(new CaSeal(), ""));   // nothing bound: nothing to prove
        Assert.Throws<ClaimException>(() => OwnerBinding.Create("short", "a.com", 3, commitment));

        // a binding made for one claim means nothing on another
        var other = new CaSeal { Site = "b.com", Seq = 3, TokenCommitment = commitment, OwnerSalt = salt, OwnerBinding = mac };
        Assert.False(OwnerBinding.Verify(other, "correct horse battery"));
    }

    [Fact]
    public void TheCaSignsTheBinding_AndAnOldSealKeepsItsOldText()
    {
        using var f = new CaFixture();
        var receipt = f.Shop.Sign("bound.com");
        var (salt, mac) = OwnerBinding.Create("correct horse battery", "bound.com", receipt.Seq,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Utf8.B(receipt.Token))).ToLowerInvariant());
        receipt.OwnerSalt = salt;
        receipt.OwnerBinding = mac;
        var seal = f.Ca.Countersign(receipt, DateTimeOffset.UtcNow);
        Assert.Equal(mac, seal.OwnerBinding);
        Assert.True(CaTrust.VerifySeal(f.RootPublic, seal, "bound.com", DateTimeOffset.UtcNow, null, receipt.Seq).Verified);

        // swapping the binding after the fact breaks the CA's signature
        var forged = System.Text.Json.JsonSerializer.Deserialize<CaSeal>(System.Text.Json.JsonSerializer.Serialize(seal))!;
        forged.OwnerBinding = new string('0', 64);
        Assert.False(CaTrust.VerifySeal(f.RootPublic, forged, "bound.com", DateTimeOffset.UtcNow, null, receipt.Seq).Verified);

        // resubmitting (for instance by someone who stole the file) returns the same bound seal, never a new unbound one
        var stolen = f.Shop.Sign("bound.com");
        Assert.Throws<CaRejectedException>(() => f.Ca.Countersign(stolen, DateTimeOffset.UtcNow));   // a different receipt for that seq is a conflict

        // a seal made without a binding has no binding text at all
        var plain = f.Ca.Countersign(f.Shop.Sign("plain.com"), DateTimeOffset.UtcNow);
        Assert.Equal("", plain.OwnerBinding);
        Assert.DoesNotContain("owner:", plain.Payload());

        var bad = f.Shop.Sign("bad.com");
        bad.OwnerSalt = "x"; bad.OwnerBinding = "nothex";
        Assert.Throws<CaRejectedException>(() => f.Ca.Countersign(bad, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task ABoundName_NeedsThePassphrase_NotJustTheCertificateFile()
    {
        using var temp = new TempDir();
        using var f = new CaFixture();
        CaPins.RootOverride.Value = f.RootPublic;
        var ca = new FakeCaServer(f);

        using var owner = Profile(temp.Path, "owner", "pin-o");
        owner.CreateSite(Draft("locked.com"));
        await owner.ImportCertAsync("locked.com", f.Shop.Sign("locked.com").ToBytes(), f.Shop.Client());
        Assert.True(owner.HoldsCert("locked.com"));

        var check = await owner.SubmitToCaAsync("locked.com", ca.Client(), ownerPassphrase: "correct horse battery");
        Assert.True(check.Verified, check.Reason);
        Assert.True(owner.HoldsCert("locked.com"));   // proven during sealing

        // the same certificate file (sealed, as the owner exports/downloads it) on a thief's profile: held, but locked
        var file = owner.ExportCert("locked.com", "export passphrase");
        using var thief = Profile(temp.Path, "thief", "pin-t");
        await thief.ImportCertAsync("locked.com", file, f.Shop.Client(), passphrase: "export passphrase");
        Assert.False(thief.HoldsCert("locked.com"));
        Assert.True(thief.NeedsOwnerPassphrase("locked.com"));
        Assert.Throws<ClaimException>(() => thief.ProveOwner("locked.com", "guess guess guess"));
        Assert.False(thief.HoldsCert("locked.com"));
        Assert.Throws<ClaimException>(() => thief.CreateSite(Draft("locked.com")));   // publishing is refused

        thief.ProveOwner("locked.com", "correct horse battery");
        Assert.True(thief.HoldsCert("locked.com"));
        thief.Lock();
        thief.Unlock("pin-t");
        Assert.False(thief.HoldsCert("locked.com"));   // unlock again next session
    }
}

public class WebOwnerBindingTests
{
    // Computed independently with Node's crypto, the same primitives the CA page's WebCrypto code calls
    private const string Salt = "AAECAwQFBgcICQoLDA0ODw";
    private const string Mac = "351037e1e067efebf8a9019a64d2ff2c53ef1afdf761f1905ed319b142d14bbd";

    [Fact]
    public void WebPageBinding_VerifiesInTheBrowser()
    {
        var seal = new CaSeal
        {
            Site = "a.com", Seq = 3, TokenCommitment = new string('a', 64),
            OwnerSalt = Salt, OwnerBinding = Mac, OwnerKdf = OwnerBinding.WebKdf
        };
        Assert.True(OwnerBinding.Verify(seal, "correct horse battery"));
        Assert.False(OwnerBinding.Verify(seal, "correct horse batterY"));
        Assert.Contains("owner2:pbkdf2-sha256-600k:", seal.Payload());
    }

    [Fact]
    public void TheCaSealsAWebBinding_AndRefusesUnknownKdfs()
    {
        using var f = new CaFixture();
        var receipt = f.Shop.Sign("web.com");
        receipt.OwnerSalt = Salt;
        receipt.OwnerBinding = Mac;
        receipt.OwnerKdf = OwnerBinding.WebKdf;
        var seal = f.Ca.Countersign(receipt, DateTimeOffset.UtcNow);
        Assert.Equal(OwnerBinding.WebKdf, seal.OwnerKdf);
        Assert.True(CaTrust.VerifySeal(f.RootPublic, seal, "web.com", DateTimeOffset.UtcNow, null, receipt.Seq).Verified);

        var odd = f.Shop.Sign("odd.com");
        odd.OwnerSalt = Salt; odd.OwnerBinding = Mac; odd.OwnerKdf = "md5-lol";
        Assert.Throws<CaRejectedException>(() => f.Ca.Countersign(odd, DateTimeOffset.UtcNow));
    }
}

public class ShopBoundTests
{
    private const string Pass = "correct horse battery";

    private static BrowserSession Profile(string root, string id, string pin)
    {
        var s = new BrowserSession(root, id);
        s.CreateProfile(pin);
        s.Unlock(pin);
        return s;
    }

    private static StoryDraft Draft(string host) => new()
    {
        Host = host, Headline = "h", Author = "t", Date = "2026-10-26", Html = "<div><p>x</p></div>", Tags = ["demo", "claim"]
    };

    [Fact]
    public void TheShopSignsTheBinding_SoItCannotBeStrippedOrReplaced()
    {
        using var f = new CaFixture();
        var cert = f.Shop.Sign("bound.com", ownerPassphrase: Pass);
        Assert.True(cert.IsShopBound);
        Assert.True(SiteClaims.Verify(cert, f.Shop.Spki));
        Assert.Contains("|bound:pbkdf2-sha256-600k-shop:", cert.Payload());

        var stripped = OwnershipCert.Parse(cert.ToBytes());
        stripped.BoundKdf = stripped.BoundSalt = stripped.BoundBinding = null;
        Assert.False(SiteClaims.Verify(stripped, f.Shop.Spki));        // the shop's signature no longer matches

        var other = OwnerBinding.CreateForShop("a different passphrase", "bound.com");
        var swapped = OwnershipCert.Parse(cert.ToBytes());
        (swapped.BoundKdf, swapped.BoundSalt, swapped.BoundBinding) = other;
        Assert.False(SiteClaims.Verify(swapped, f.Shop.Spki));

        Assert.True(OwnerBinding.Verify(cert, Pass));
        Assert.False(OwnerBinding.Verify(cert, "wrong passphrase!"));
        Assert.False(OwnerBinding.Verify(cert, ""));

        // an unbound receipt is unaffected: same payload text as before
        Assert.DoesNotContain("bound:", f.Shop.Sign("plain.com").Payload());
    }

    [Fact]
    public void TheCaSealsTheShopsBinding_AndIgnoresWhatASealerAdds()
    {
        using var f = new CaFixture();
        var cert = f.Shop.Sign("sealed.com", ownerPassphrase: Pass);
        var (salt, mac) = OwnerBinding.Create("attackers own passphrase", "sealed.com", cert.Seq,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Utf8.B(cert.Token))).ToLowerInvariant());
        cert.OwnerSalt = salt; cert.OwnerBinding = mac; cert.OwnerKdf = null;   // a thief trying to bind first
        var seal = f.Ca.Countersign(cert, DateTimeOffset.UtcNow);
        Assert.Equal(cert.BoundBinding, seal.OwnerBinding);
        Assert.Equal(OwnerBinding.WebKdfShop, seal.OwnerKdf);
        Assert.True(OwnerBinding.Verify(seal, Pass));
        Assert.False(OwnerBinding.Verify(seal, "attackers own passphrase"));
        Assert.True(CaTrust.VerifySeal(f.RootPublic, seal, "sealed.com", DateTimeOffset.UtcNow, null, cert.Seq).Verified);
    }

    [Fact]
    public async Task AStolenShopBoundFile_IsLocked_SealedOrNot_EvenWithTheSealRemoved()
    {
        using var temp = new TempDir();
        using var f = new CaFixture();
        CaPins.RootOverride.Value = f.RootPublic;
        var ca = new FakeCaServer(f);
        var cert = f.Shop.Sign("theft.com", ownerPassphrase: Pass);

        using var thief = Profile(temp.Path, "thief", "pin-t");
        thief.CreateSite(Draft("theft.com"));
        await thief.ImportCertAsync("theft.com", cert.ToBytes(), f.Shop.Client());   // never sealed, no seal field at all
        Assert.False(thief.HoldsCert("theft.com"));
        Assert.True(thief.NeedsOwnerPassphrase("theft.com"));
        Assert.Throws<ClaimException>(() => thief.CreateSite(Draft("theft.com")));
        Assert.Throws<ClaimException>(() => thief.ProveOwner("theft.com", "guess guess guess"));

        using var owner = Profile(temp.Path, "owner", "pin-o");
        await owner.ImportCertAsync("theft.com", cert.ToBytes(), f.Shop.Client());
        Assert.False(owner.HoldsCert("theft.com"));
        owner.ProveOwner("theft.com", Pass);
        Assert.True(owner.HoldsCert("theft.com"));
        var check = await owner.SubmitToCaAsync("theft.com", ca.Client(), ownerPassphrase: Pass);
        Assert.True(check.Verified, check.Reason);
        Assert.True(owner.OwnershipOf("theft.com").Verified);
    }
}

public class ShopPayloadParityTests
{
    // The strings below are what the live shop's claim_payload() printed for these inputs (PHP), so the two sides cannot drift apart
    [Fact]
    public void PhpShopAndBrowser_SignTheSameText()
    {
        var bound = new OwnershipCert
        {
            Site = "a.com", Order = 7, Seq = 2, PaidAt = 100, Expires = 200, Token = new string('b', 64),
            BoundKdf = "pbkdf2-sha256-600k-shop", BoundSalt = "AAECAwQFBgcICQoLDA0ODw", BoundBinding = new string('c', 64)
        };
        Assert.Equal("kickl/v1/site-claim|a.com|7|2|100|200|" + new string('b', 64) +
                     "|bound:pbkdf2-sha256-600k-shop:AAECAwQFBgcICQoLDA0ODw:" + new string('c', 64), bound.Payload());

        var plain = new OwnershipCert { Site = "a.com", Order = 7, Seq = 2, PaidAt = 100, Expires = 200, Token = new string('b', 64) };
        Assert.Equal("kickl/v1/site-claim|a.com|7|2|100|200|" + new string('b', 64), plain.Payload());
    }

    [Fact]
    public void ShopBindingFromTheWebPage_VerifiesInTheBrowser()
    {
        // Node's crypto, same primitives as the shop page's WebCrypto: PBKDF2-SHA256 600k, HMAC over "...-shop|a.com"
        var cert = new OwnershipCert
        {
            Site = "a.com", Token = new string('b', 64), BoundKdf = "pbkdf2-sha256-600k-shop",
            BoundSalt = "AAECAwQFBgcICQoLDA0ODw", BoundBinding = "3dd0a3b47315a0616d0e1dad65000237206e23da2f814b2dc37cd161a449d952"
        };
        Assert.True(OwnerBinding.Verify(cert, "correct horse battery"));
        Assert.False(OwnerBinding.Verify(cert, "correct horse batterY"));
    }
}
