using System.Text.Json;
using Kickl.Core.Ownership;
using Kickl.Core.Registry;
using Kickl.Core.Trust;
using Kickl.Core.Util;
using Xunit;

namespace Kickl.Core.Tests;

internal sealed class CaFixture : IDisposable
{
    public readonly HybridSigner Root = HybridSigner.Generate();
    public readonly HybridSigner IssuerKey = HybridSigner.Generate();
    public readonly FakeShop Shop = new();
    public readonly MemoryCaStore Store = new();
    public readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    public readonly IssuerCertificate Issuer;
    public readonly CaService Ca;

    public CaFixture()
    {
        Issuer = CaService.IssueIssuer(Root, IssuerKey.Public, "Kickl CA issuing key 1", 1, Now.AddHours(-1), TimeSpan.FromDays(90));
        Ca = new CaService(IssuerKey, Issuer, Root.Public, [Shop.Spki], Store);
    }

    public RegistrySigningKey RootPublic => Root.Public;

    public CaSeal Seal(string site, int seq = 1, long order = 1) => Ca.Countersign(Shop.Sign(site, seq, order), Now);

    public void Dispose()
    {
        Root.Dispose();
        IssuerKey.Dispose();
        Shop.Dispose();
    }
}

public class CaTests
{
    [Fact]
    public void AReceiptTheBrowserWritesIsReadableByTheCasWebFront()
    {
        // The web front is PHP and reads lower-case keys, exactly as the shop writes them. A receipt the
        // browser saves or submits must use the same names (this once shipped capitalised and was refused).
        using var f = new CaFixture();
        var json = JsonDocument.Parse(f.Shop.Sign("owned.com").ToBytes()).RootElement;
        Assert.Equal("ownership-cert", json.GetProperty("kickl").GetString());
        Assert.Equal("owned.com", json.GetProperty("site").GetString());
        Assert.True(json.TryGetProperty("token", out _));
        Assert.True(json.TryGetProperty("expires", out _));
    }

    [Fact]
    public void ASealedReceiptVerifiesAgainstThePinnedRoot_AndLeaksNoSecret()
    {
        using var f = new CaFixture();
        var receipt = f.Shop.Sign("owned.com");
        var seal = f.Ca.Countersign(receipt, f.Now);

        Assert.True(CaTrust.VerifySeal(f.RootPublic, seal, "owned.com", f.Now).Verified);
        Assert.Equal(1, seal.TreeSize);
        Assert.DoesNotContain(receipt.Token, JsonSerializer.Serialize(seal));
        Assert.Equal(TrustJsonSha(receipt.Token), seal.TokenCommitment);
    }

    private static string TrustJsonSha(string s) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    [Fact]
    public void ASealFromAnotherRootOrForAnotherSiteIsNotVerified()
    {
        using var f = new CaFixture();
        using var otherRoot = HybridSigner.Generate();
        var seal = f.Seal("owned.com");

        Assert.False(CaTrust.VerifySeal(otherRoot.Public, seal, "owned.com", f.Now).Verified);
        Assert.False(CaTrust.VerifySeal(f.RootPublic, seal, "other.com", f.Now).Verified);
        Assert.False(CaTrust.VerifySeal(f.RootPublic, seal, "owned.com", f.Now, expectedSeq: 2).Verified);
    }

    [Theory]
    [InlineData("site")]
    [InlineData("seq")]
    [InlineData("commitment")]
    [InlineData("root")]
    [InlineData("index")]
    [InlineData("proof")]
    [InlineData("expires")]
    public void AnyEditedSealFieldBreaksVerification(string field)
    {
        using var f = new CaFixture();
        f.Seal("filler.com", order: 1);
        f.Seal("filler2.com", order: 2);
        var seal = f.Seal("owned.com", order: 3);
        Assert.True(CaTrust.VerifySeal(f.RootPublic, seal, "owned.com", f.Now).Verified);

        switch (field)
        {
            case "site": seal.Site = "stolen.com"; break;
            case "seq": seal.Seq = 9; break;
            case "commitment": seal.TokenCommitment = new string('a', 64); break;
            case "root": seal.TreeRoot = new string('b', 64); break;
            case "index": seal.LogIndex = 0; break;
            case "proof": seal.Proof[0] = new string('c', 64); break;
            case "expires": seal.ReceiptExpires += 100000; break;
        }

        var site = field == "site" ? "stolen.com" : "owned.com";
        Assert.False(CaTrust.VerifySeal(f.RootPublic, seal, site, f.Now).Verified);
    }

    [Fact]
    public void ForgingAnIssuerCertificateOrSwappingTheIssuerKeyFails()
    {
        using var f = new CaFixture();
        using var attacker = HybridSigner.Generate();
        var seal = f.Seal("owned.com");

        // an attacker signs a perfectly good seal with their own key and pastes in their own issuer certificate
        var fakeIssuer = CaService.IssueIssuer(attacker, attacker.Public, "Kickl CA issuing key 1", 1, f.Now.AddHours(-1), TimeSpan.FromDays(90));
        var fake = new CaService(attacker, fakeIssuer, attacker.Public, [f.Shop.Spki], new MemoryCaStore());
        var forged = fake.Countersign(f.Shop.Sign("owned.com"), f.Now);
        Assert.True(CaTrust.VerifySeal(attacker.Public, forged, "owned.com", f.Now).Verified);   // valid under *their* root
        Assert.False(CaTrust.VerifySeal(f.RootPublic, forged, "owned.com", f.Now).Verified);     // not under ours

        // real seal with a swapped issuer key
        seal.Issuer.Key = attacker.Public;
        Assert.False(CaTrust.VerifySeal(f.RootPublic, seal, "owned.com", f.Now).Verified);
    }

    [Fact]
    public void IssuerCertificatesExpireAndAreNotValidBeforeTheirDate()
    {
        using var f = new CaFixture();
        var seal = f.Seal("owned.com");
        Assert.False(CaTrust.VerifySeal(f.RootPublic, seal, "owned.com", f.Now.AddDays(91)).Verified);
        Assert.False(CaTrust.VerifySeal(f.RootPublic, seal, "owned.com", f.Now.AddDays(-1)).Verified);
    }

    [Fact]
    public void ClassicalOnlySignaturesAreRefused()
    {
        using var f = new CaFixture();
        using var verifier = new HybridVerifier(f.IssuerKey.Public);
        var sig = f.IssuerKey.Sign("test/domain", "hello");
        Assert.True(verifier.Verify("test/domain", "hello", sig));
        Assert.False(verifier.Verify("another/domain", "hello", sig));   // domain separation
        Assert.False(verifier.Verify("test/domain", "hellp", sig));

        var raw = B64u.Decode(sig);
        Array.Clear(raw, 64, raw.Length - 64);                            // keep the Ed25519 half, drop ML-DSA
        Assert.False(verifier.Verify("test/domain", "hello", B64u.Encode(raw)));
    }

    // ------------------------------------------------------------------ the CA watching

    [Fact]
    public void TheCaRefusesReceiptsItDoesNotTrust()
    {
        using var f = new CaFixture();
        using var rogueShop = new FakeShop();
        var ex = Assert.Throws<CaRejectedException>(() => f.Ca.Countersign(rogueShop.Sign("owned.com"), f.Now));
        Assert.Equal("bad-receipt", ex.Code);

        var tampered = f.Shop.Sign("owned.com");
        tampered.Seq = 5;
        Assert.Equal("bad-receipt", Assert.Throws<CaRejectedException>(() => f.Ca.Countersign(tampered, f.Now)).Code);

        var expired = f.Shop.Sign("old.com", lasts: TimeSpan.FromSeconds(-100));
        Assert.Equal("expired", Assert.Throws<CaRejectedException>(() => f.Ca.Countersign(expired, f.Now)).Code);
        Assert.Empty(f.Store.Entries);
    }

    [Fact]
    public void ResubmittingTheSameReceiptReturnsTheSameSeal_ADifferentOneForTheSameClaimIsAConflict()
    {
        using var f = new CaFixture();
        var receipt = f.Shop.Sign("owned.com", order: 7);
        var first = f.Ca.Countersign(receipt, f.Now);
        var again = f.Ca.Countersign(receipt, f.Now.AddMinutes(5));
        Assert.Same(first, again);
        Assert.Single(f.Store.Entries);

        // the shop (or someone holding its key) signs a second receipt for the same claim number
        var clash = f.Shop.Sign("owned.com", seq: 1, order: 8);
        var ex = Assert.Throws<CaRejectedException>(() => f.Ca.Countersign(clash, f.Now));
        Assert.Equal("conflict", ex.Code);
        Assert.Contains(f.Store.Alerts, a => a.Kind == "conflict" && a.Detail.Contains("owned.com"));
        Assert.Single(f.Store.Entries);
    }

    [Fact]
    public void AReclaimSupersedesAndAnOldReceiptIsStale()
    {
        using var f = new CaFixture();
        var old = f.Shop.Sign("flip.com", seq: 1, order: 1);
        f.Ca.Countersign(old, f.Now);
        var next = f.Ca.Countersign(f.Shop.Sign("flip.com", seq: 2, order: 2), f.Now);
        Assert.Equal(2, next.Seq);
        Assert.True(CaTrust.VerifySeal(f.RootPublic, next, "flip.com", f.Now, expectedSeq: 2).Verified);

        var fresh = f.Shop.Sign("flip.com", seq: 1, order: 3);
        Assert.Equal("stale", Assert.Throws<CaRejectedException>(() => f.Ca.Countersign(fresh, f.Now)).Code);
    }

    // ------------------------------------------------------------------ transparency

    [Fact]
    public void EveryEarlierSealStillVerifiesAsTheLogGrows_AndTheAuditIsClean()
    {
        using var f = new CaFixture();
        var seals = Enumerable.Range(0, 9).Select(i => f.Seal($"site{i}.com", order: i + 1)).ToList();

        for (var i = 0; i < seals.Count; i++)
        {
            Assert.Equal(i, seals[i].LogIndex);
            Assert.True(CaTrust.VerifySeal(f.RootPublic, seals[i], $"site{i}.com", f.Now).Verified, $"seal {i}");
        }
        Assert.Empty(f.Ca.Audit());

        var head = f.Ca.Head(f.Now);
        Assert.Equal(9, head.Size);
        using var iv = new HybridVerifier(f.IssuerKey.Public);
        Assert.True(iv.Verify(TrustDomains.LogHead, CaSeal.HeadPayloadFor(head.Size, head.Root, head.Time), head.Sig));
    }

    [Fact]
    public void RewritingTheLedgerIsDetectedByTheAudit()
    {
        using var f = new CaFixture();
        for (var i = 0; i < 4; i++) f.Seal($"site{i}.com", order: i + 1);
        Assert.Empty(f.Ca.Audit());

        f.Store.Entries[1].LeafText = "kickl/v1/ca-leaf|site1.com|1|rewritten";
        Assert.NotEmpty(f.Ca.Audit());
    }

    [Fact]
    public void TheFileLedgerSurvivesARestartAndKeepsTheLogConsistent()
    {
        using var temp = new TempDir();
        using var f = new CaFixture();
        CaSeal first;
        using (var store = new FileCaStore(temp.Path))
        {
            var ca = new CaService(f.IssuerKey, f.Issuer, f.RootPublic, [f.Shop.Spki], store);
            first = ca.Countersign(f.Shop.Sign("a.com", order: 1), f.Now);
        }

        using var reopened = new FileCaStore(temp.Path);
        var ca2 = new CaService(f.IssuerKey, f.Issuer, f.RootPublic, [f.Shop.Spki], reopened);
        var second = ca2.Countersign(f.Shop.Sign("b.com", order: 2), f.Now);

        Assert.Equal(1, second.LogIndex);
        Assert.Equal(2, second.TreeSize);
        Assert.True(CaTrust.VerifySeal(f.RootPublic, first, "a.com", f.Now).Verified);
        Assert.True(CaTrust.VerifySeal(f.RootPublic, second, "b.com", f.Now).Verified);
        Assert.Empty(ca2.Audit());
    }

    // ------------------------------------------------------------------ revocation

    [Fact]
    public void TheIssuerCanRevokeASeal_AndTheRootCanRevokeAnIssuer()
    {
        using var f = new CaFixture();
        var seal = f.Seal("owned.com");
        var other = f.Seal("kept.com", order: 2);

        var sealList = f.Ca.Revoke(null, [new RevokedSeal { Site = "owned.com", Seq = 1, Reason = "fraud" }], f.Now);
        Assert.True(CaTrust.VerifyRevocations(f.RootPublic, sealList, f.Issuer, f.Now));
        Assert.False(CaTrust.VerifySeal(f.RootPublic, seal, "owned.com", f.Now, sealList).Verified);
        Assert.True(CaTrust.VerifySeal(f.RootPublic, other, "kept.com", f.Now, sealList).Verified);

        var rootList = CaService.RevokeAsRoot(f.Root, sealList, [f.Issuer.Serial], [], f.Now);
        Assert.Equal(2, rootList.Number);
        Assert.True(CaTrust.VerifyRevocations(f.RootPublic, rootList, null, f.Now));
        Assert.False(CaTrust.VerifySeal(f.RootPublic, other, "kept.com", f.Now, rootList).Verified);   // issuer revoked
    }

    [Fact]
    public void AStolenIssuerKeyCannotRevokeItsOwnRevocationOrOtherIssuers()
    {
        using var f = new CaFixture();
        var asIssuer = f.Ca.Revoke(null, [], f.Now);
        asIssuer.RevokedIssuerSerials.Add(99);                               // an issuer list that tries to revoke an issuer
        asIssuer.Sig = f.IssuerKey.Sign(TrustDomains.Revocations, asIssuer.Payload());
        Assert.False(CaTrust.VerifyRevocations(f.RootPublic, asIssuer, f.Issuer, f.Now));

        var claimsRoot = f.Ca.Revoke(null, [], f.Now);
        claimsRoot.SignedBy = "root";                                        // issuer signature dressed up as the root's
        Assert.False(CaTrust.VerifyRevocations(f.RootPublic, claimsRoot, f.Issuer, f.Now));
    }
}
