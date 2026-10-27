using System.Security.Cryptography;
using System.Text;
using Kickl.Core.Crypto;
using Kickl.Core.Documents;
using Kickl.Core.Markup;
using Kickl.Core.Mesh;
using Kickl.Core.Ownership;
using Kickl.Core.Privacy;
using Kickl.Core.Registry;
using Kickl.Core.Session;
using Kickl.Core.Stego;
using Kickl.Core.Util;

namespace Kickl.Core.Tests;

public class PfsLookupTests
{
    private static RegistryFile Registry() => new()
    {
        Records =
        [
            new RegistryRecord
            {
                Name = "pinwheelexplained.com",
                DocFile = "profiles/local/sites/pinwheelexplained.com/doc.json",
                Status = "active",
                Version = 7,
                OwnerId = "kb1deadbeef",
                BadgeId = "kb1bdeadbeef"
            }
        ]
    };

    [Fact]
    public void HandshakeResolvesName_AndSessionKeysDiffer()
    {
        using var server = new PfsRegistryServer(Registry());
        using var a = new PfsLookupClient();
        using var b = new PfsLookupClient();

        var ha = server.Initiate(a.ClientPublicKeySpki);
        var hb = server.Initiate(b.ClientPublicKeySpki);
        a.Establish(ha);
        b.Establish(hb);

        Assert.NotEqual(a.KeyFingerprint(), b.KeyFingerprint());
        Assert.NotEqual(ha.SessionId, hb.SessionId);
        Assert.Equal(16, ha.Salt.Length);

        var result = a.ReadResponse(server.HandleQuery(ha.SessionId, a.Query("pinwheelexplained.com")));
        Assert.True(result.Found);
        Assert.Equal(7, result.Version);
        Assert.Equal("kb1deadbeef", result.OwnerId);
    }

    [Fact]
    public void UnknownName_IsEncryptedAsNotFound()
    {
        using var server = new PfsRegistryServer(Registry());
        using var client = new PfsLookupClient();
        var hello = server.Initiate(client.ClientPublicKeySpki);
        client.Establish(hello);
        var result = client.ReadResponse(server.HandleQuery(hello.SessionId, client.Query("absent.custom")));
        Assert.False(result.Found);
        Assert.Equal("absent.custom", result.Host);
    }

    [Fact]
    public void TamperedReply_FailsClosed()
    {
        using var server = new PfsRegistryServer(Registry());
        using var client = new PfsLookupClient();
        var hello = server.Initiate(client.ClientPublicKeySpki);
        client.Establish(hello);
        var reply = server.HandleQuery(hello.SessionId, client.Query("pinwheelexplained.com"));
        reply.Ciphertext[0] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => client.ReadResponse(reply));
    }

    [Fact]
    public void ReplayAndDestroyedSessions_AreRefused()
    {
        using var server = new PfsRegistryServer(Registry());
        using var client = new PfsLookupClient();
        var hello = server.Initiate(client.ClientPublicKeySpki);
        client.Establish(hello);

        var query = client.Query("pinwheelexplained.com");
        server.HandleQuery(hello.SessionId, query); // counter 1 consumed
        Assert.ThrowsAny<CryptographicException>(() => server.HandleQuery(hello.SessionId, query));

        server.Invalidate(hello.SessionId);
        Assert.Throws<InvalidOperationException>(() => server.HandleQuery(hello.SessionId, query));

        client.Dispose();
        Assert.Throws<InvalidOperationException>(() => client.Query("pinwheelexplained.com"));
    }
}

public class OwnershipTests
{
    private const string Site = "pinwheelexplained.com";

    [Fact]
    public void CreateProveIssue_ShowsBadge()
    {
        using var temp = new TempDir();
        var paths = new DataPaths(temp.Path);
        var usb = Path.Combine(temp.Path, "usb");

        var pub = OwnershipAuthority.Create(paths, Site, usb, "test passphrase 123");
        var secret = OwnershipAuthority.LoadSecret(usb, Site, "test passphrase 123");
        Assert.NotEmpty(secret.TokenCiphertext);
        Assert.NotEmpty(secret.WrappedKey);
        var secretJson = File.ReadAllText(OwnershipAuthority.SecretPath(usb, Site));
        Assert.DoesNotContain("\"Token\":", secretJson);
        Assert.DoesNotContain("\"ControlKey\":", secretJson);
        Assert.DoesNotContain("\"PrivateKey\"", secretJson);

        var challenge = OwnershipAuthority.NewChallenge();
        var proof = OwnershipAuthority.Prove(secret, challenge);
        Assert.True(OwnershipAuthority.VerifyProof(pub, proof));
        Assert.Equal(pub.TokenCommitment, Convert.ToHexString(
            SHA256.HashData(Utf8.B("kickl/v1/token|" + proof.Token))).ToLowerInvariant());

        var title = OwnershipAuthority.Issue(paths, pub, proof);
        Assert.Equal(pub.OwnerId, title.OwnerId);
        Assert.StartsWith("kb1b", title.BadgeId);
        Assert.True(File.Exists(OwnershipAuthority.TitlePath(paths, Site)));

        var record = LocalRegistry.Resolve(LocalRegistry.LoadOrEmpty(paths), Site)!;
        Assert.Equal(title.BadgeId, record.BadgeId);
        Assert.True(LocalRegistry.IsOwned(record));
    }

    [Fact]
    public void WrongChallenge_OrWrongKey_IsRejected()
    {
        using var temp = new TempDir();
        var paths = new DataPaths(temp.Path);
        var usb = Path.Combine(temp.Path, "usb");

        var pub = OwnershipAuthority.Create(paths, Site, usb, "test passphrase 123");
        var secret = OwnershipAuthority.LoadSecret(usb, Site, "test passphrase 123");
        var proof = OwnershipAuthority.Prove(secret, OwnershipAuthority.NewChallenge());
        proof.Challenge = OwnershipAuthority.NewChallenge(); // valid signature, wrong challenge
        Assert.False(OwnershipAuthority.VerifyProof(pub, proof));
        Assert.ThrowsAny<CryptographicException>(() => OwnershipAuthority.Issue(paths, pub, proof));
    }

    [Fact]
    public void TransferMovesTheTitle_AndOldKeyCannotSignAgain()
    {
        using var root = new TempDir();
        var paths = new DataPaths(root.Path);
        var sellerUsb = Path.Combine(root.Path, "seller-usb");
        var buyerUsb = Path.Combine(root.Path, "buyer-usb");

        var sellerPub = OwnershipAuthority.Create(paths, Site, sellerUsb, "seller passphrase 123");
        var seller = OwnershipAuthority.LoadSecret(sellerUsb, Site, "seller passphrase 123");
        var sellerTitle = OwnershipAuthority.Issue(paths, sellerPub,
            OwnershipAuthority.Prove(seller, OwnershipAuthority.NewChallenge()));

        var buyerPub = OwnershipAuthority.Create(paths, Site, buyerUsb, "buyer passphrase 123");
        var buyer = OwnershipAuthority.LoadSecret(buyerUsb, Site, "buyer passphrase 123");

        var statement = OwnershipAuthority.CreateTransfer(
            paths, sellerTitle, seller, buyerPub, "12 NKL");
        var accepted = OwnershipAuthority.AcceptTransfer(paths, statement, buyerPub,
            OwnershipAuthority.Prove(buyer, OwnershipAuthority.NewChallenge()));

        Assert.Equal(buyerPub.OwnerId, accepted.OwnerId);
        Assert.Equal(2, accepted.Seq);

        Assert.Throws<InvalidOperationException>(() =>
            OwnershipAuthority.CreateTransfer(paths, sellerTitle, seller, buyerPub, "1 NKL"));
    }

    [Fact]
    public void TamperedTransfer_IsRejected()
    {
        using var root = new TempDir();
        var paths = new DataPaths(root.Path);
        var sellerUsb = Path.Combine(root.Path, "seller-usb");
        var buyerUsb = Path.Combine(root.Path, "buyer-usb");

        var sellerPub = OwnershipAuthority.Create(paths, Site, sellerUsb, "seller passphrase 123");
        var seller = OwnershipAuthority.LoadSecret(sellerUsb, Site, "seller passphrase 123");
        var title = OwnershipAuthority.Issue(paths, sellerPub,
            OwnershipAuthority.Prove(seller, OwnershipAuthority.NewChallenge()));
        var buyerPub = OwnershipAuthority.Create(paths, Site, buyerUsb, "buyer passphrase 123");
        var buyer = OwnershipAuthority.LoadSecret(buyerUsb, Site, "buyer passphrase 123");

        var statement = OwnershipAuthority.CreateTransfer(paths, title, seller, buyerPub, "1");
        statement.Price = "999";
        Assert.ThrowsAny<CryptographicException>(() => OwnershipAuthority.AcceptTransfer(
            paths, statement, buyerPub, OwnershipAuthority.Prove(buyer, OwnershipAuthority.NewChallenge())));
    }
}

public class StegoCacheTests
{
    [Fact]
    public void StoreReadAndPolymorphicRefresh()
    {
        using var temp = new TempDir();
        using var secret = SessionSecret.Create(RandomNumberGenerator.GetBytes(32));
        var cache = new StegoCache(Path.Combine(temp.Path, "stego"));
        var envelope = new CacheEnvelope
        {
            Site = "pinwheelexplained.com",
            Path = "/",
            Title = "Pinwheel, explained",
            Content = "a page body that must never appear on disk",
            CreatedUtc = DateTimeOffset.UtcNow.ToString("O")
        };

        var path = cache.Store(secret, envelope);
        var first = File.ReadAllBytes(path);
        Assert.False(first.AsSpan().IndexOf(Encoding.UTF8.GetBytes(envelope.Site)) >= 0);
        Assert.False(first.AsSpan().IndexOf(Encoding.UTF8.GetBytes(envelope.Content)) >= 0);

        var read = cache.Read(secret, path);
        Assert.Equal(envelope.Content, read.Content);

        var refreshed = cache.Load(secret, path, refresh: true);
        var second = File.ReadAllBytes(path);
        Assert.Equal(envelope.Content, refreshed.Content);
        Assert.False(first.AsSpan().SequenceEqual(second));
    }

    [Fact]
    public void WrongSession_CannotReadCarrier()
    {
        using var temp = new TempDir();
        using var secret = SessionSecret.Create(RandomNumberGenerator.GetBytes(32));
        using var other = SessionSecret.Create(RandomNumberGenerator.GetBytes(32));
        var cache = new StegoCache(temp.Path);
        var path = cache.Store(secret, new CacheEnvelope { Site = "a.custom", Content = "secret" });
        Assert.Throws<StegoException>(() => cache.Read(other, path));
    }

    [Fact]
    public void PngRoundTrip_AndKeyedEmbedding()
    {
        var rgb = new byte[64 * 48 * 3];
        for (var i = 0; i < rgb.Length; i++) rgb[i] = (byte)(i * 37);
        var png = PngCarrier.Write(64, 48, rgb, new Dictionary<string, string> { ["kickl-seed"] = "00ff" });

        var image = PngCarrier.Read(png);
        Assert.Equal(64, image.Width);
        Assert.Equal(rgb, image.Rgb);
        Assert.Equal("00ff", image.Text["kickl-seed"]);

        var key = RandomNumberGenerator.GetBytes(32);
        var embedded = PngCarrier.Embed(png, "hello stego"u8.ToArray(), key);
        Assert.Equal("hello stego", Encoding.UTF8.GetString(PngCarrier.Extract(embedded, key)));
        Assert.Throws<StegoException>(() => PngCarrier.Extract(embedded, RandomNumberGenerator.GetBytes(32)));
    }
}

public class SealedPageTests
{
    [Fact]
    public void RegionsRevealIndividually_AndCiphertextHasNoText()
    {
        using var temp = new TempDir();
        using var secret = SessionSecret.Create(RandomNumberGenerator.GetBytes(32));
        var page = Kml.Parse(File.ReadAllText(Fixture.KmlPath), "pinwheelexplained.com");
        using var sealedPage = SealedPage.Seal(page, secret);

        Assert.True(sealedPage.RegionCount >= 4);
        var first = sealedPage.Regions[1];
        var expected = page.Blocks[first.BlockIndex].Text!;
        Assert.DoesNotContain(expected[..Math.Min(8, expected.Length)],
            Encoding.UTF8.GetString(first.Ciphertext));

        var rendered = sealedPage.Render();
        Assert.Contains("sealed paragraph", rendered);
        Assert.DoesNotContain(expected, rendered);

        var text = sealedPage.Reveal(first.Index, secret);
        Assert.Equal(expected, text);
        Assert.Contains(expected, sealedPage.Render());
        Assert.NotEqual(text, sealedPage.Reveal(0, secret));

        sealedPage.Dispose();
        Assert.Throws<ObjectDisposedException>(() => sealedPage.Reveal(0, secret));
    }
}

public class ContentHashTests
{
    [Fact]
    public void RegistryHashMatchesTheDeliveredPackage()
    {
        using var temp = new TempDir();
        var paths = new DataPaths(temp.Path);
        Keystore.Create(paths, "pin");
        var master = Keystore.Unlock(paths, "pin");
        var doc = DocumentStore.Publish(paths, Fixture.SiteDir, "pinwheelexplained.com", master);
        CryptographicOperations.ZeroMemory(master);

        using var session = new BrowserSession(temp.Path);
        var lookup = session.Lookup("pinwheelexplained.com");
        Assert.Equal(doc.Integrity, lookup.Response.ContentHash);
        session.Open("jttp://pinwheelexplained.com/");
    }

    [Fact]
    public void WrongHashIsCountedAsAnAttackAndNothingRenders()
    {
        using var temp = new TempDir();
        var paths = new DataPaths(temp.Path);
        Keystore.Create(paths, "pin");
        var master = Keystore.Unlock(paths, "pin");
        DocumentStore.Publish(paths, Fixture.SiteDir, "pinwheelexplained.com", master);
        CryptographicOperations.ZeroMemory(master);

        var registry = LocalRegistry.LoadOrEmpty(paths);
        LocalRegistry.Resolve(registry, "pinwheelexplained.com")!.ContentHash = new string('0', 64);
        LocalRegistry.Save(paths, registry);

        using var session = new BrowserSession(temp.Path);
        var first = Assert.Throws<ContentIntegrityException>(() =>
            session.Open("jttp://pinwheelexplained.com/"));
        Assert.Equal(1, first.Count);
        Assert.Null(session.Document);

        var second = Assert.Throws<ContentIntegrityException>(() =>
            session.Open("jttp://pinwheelexplained.com/"));
        Assert.Equal(2, second.Count);

        var after = LocalRegistry.Resolve(LocalRegistry.LoadOrEmpty(paths), "pinwheelexplained.com")!;
        Assert.Equal(2, after.ContentMismatchCount);
        Assert.True(after.LastContentMismatchUtc > 0);
    }
}

public class CrossProfileTests
{
    private const string Protected = """
        <?xml version="1.0" encoding="utf-8"?>
        <kml v="1" site="vaultsite.com" epoch="1">
          <head><title>Vault</title>
            <meta name="headline" content="A page with a public part and a protected part."/>
            <meta name="tags" content="demo, protected"/></head>
          <body><page><h1>Vault</h1>
            <blob id="open" mime="text/plain"/>
            <blob id="secret" mime="text/plain" access="protected"/>
          </page></body>
        </kml>
        """;

    private static SealedDocument PublishAs(string root, string profile, string pin, string host,
        string markup, string? accessKey = null)
    {
        var paths = new DataPaths(root, profile);
        Keystore.Create(paths, pin);
        var master = Keystore.Unlock(paths, pin);
        try
        {
            return DocumentStore.PublishMarkup(paths, host, markup, master,
                new Dictionary<string, PendingBlob>
                {
                    ["open"] = new("text/plain", Encoding.UTF8.GetBytes("PUBLIC-NOTE")),
                    ["secret"] = new("text/plain", Encoding.UTF8.GetBytes("SECRET-NOTE"))
                }, accessKey);
        }
        finally { CryptographicOperations.ZeroMemory(master); }
    }

    [Fact]
    public void PublicContentOpensForAnyProfile_AndTheRegistryHashStillMatches()
    {
        using var temp = new TempDir();
        var owner = new DataPaths(temp.Path, "local");
        Keystore.Create(owner, "1234");
        var master = Keystore.Unlock(owner, "1234");
        var doc = DocumentStore.Publish(owner, Fixture.SiteDir, "pinwheelexplained.com", master);
        CryptographicOperations.ZeroMemory(master);

        using var other = new BrowserSession(temp.Path, "josh");
        other.CreateProfile("00000000");
        other.Open("jttp://pinwheelexplained.com/", "00000000");
        Assert.Contains("PINWHEEL-CANARY-7f3a", other.UnlockBlob("b1"));

        // Going to another document and back must not reuse a stale document key.
        other.Open("jttp://pinwheelexplained.com/");
        Assert.Contains("PINWHEEL-CANARY-7f3a", other.UnlockBlob("b1"));

        using var home = new BrowserSession(temp.Path, "local");
        home.Open("jttp://pinwheelexplained.com/", "1234");
        Assert.Equal(doc.Integrity, home.Document!.Integrity);
        Assert.Contains("PINWHEEL-CANARY-7f3a", home.UnlockBlob("b1"));
    }

    [Fact]
    public void PageMarkupIsCiphertextOnDisk()
    {
        using var temp = new TempDir();
        PublishAs(temp.Path, "local", "pw", "vaultsite.com", Protected);
        var json = File.ReadAllText(new DataPaths(temp.Path, "local").SiteDoc("vaultsite.com"));
        Assert.DoesNotContain("A page with a public part", json);
        Assert.DoesNotContain("<h1>", json);
    }

    [Fact]
    public void ProtectedBlobNeedsTheAccessKey_PublicBlobDoesNot()
    {
        using var temp = new TempDir();
        var doc = PublishAs(temp.Path, "local", "pw", "vaultsite.com", Protected);
        var key = doc.IssuedAccessKey!;
        Assert.Equal("protected", doc.Blobs["secret"].Access);
        Assert.Equal("public", doc.Blobs["open"].Access);

        using var reader = new BrowserSession(temp.Path, "reader");
        reader.CreateProfile("reader-pin");
        reader.Open("jttp://vaultsite.com/", "reader-pin");

        Assert.Equal("PUBLIC-NOTE", reader.UnlockBlob("open"));
        Assert.Throws<AccessKeyRequiredException>(() => reader.UnlockBlob("secret"));
        Assert.Throws<CryptographicException>(() => reader.UnlockBlob("secret", null, "WRONG-KEY"));
        Assert.Equal("SECRET-NOTE", reader.UnlockBlob("secret", null, key.Replace("-", " ")));
    }

    [Fact]
    public void AuthorChosenAccessKeyWorks()
    {
        using var temp = new TempDir();
        PublishAs(temp.Path, "local", "pw", "vaultsite.com", Protected, accessKey: "correct horse");
        using var reader = new BrowserSession(temp.Path, "reader");
        reader.CreateProfile("reader-pin");
        reader.Open("jttp://vaultsite.com/", "reader-pin");
        Assert.Equal("SECRET-NOTE", reader.UnlockBlob("secret", null, "correct horse"));
    }

    [Fact]
    public void OlderAuthorOnlyPackageOpensToEveryoneOnceItsAuthorOpensIt()
    {
        using var temp = new TempDir();
        var owner = new DataPaths(temp.Path, "vikerus");
        Keystore.Create(owner, "owner-pin");
        var master = Keystore.Unlock(owner, "owner-pin");
        var published = DocumentStore.Publish(owner, Fixture.SiteDir, "pinwheelexplained.com", master);
        CryptographicOperations.ZeroMemory(master);

        // Turn it back into an older package: plaintext markup, no public wrap.
        var path = owner.SiteDoc("pinwheelexplained.com");
        var old = System.Text.Json.JsonSerializer.Deserialize<SealedDocument>(File.ReadAllText(path))!;
        old.Markup = DocumentStore.Load(owner, "pinwheelexplained.com").Markup;
        old.SealedMarkup = null;
        old.PublicWrap = new WrappedKey();
        old.PublicSalt = [];
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(old));

        using var stranger = new BrowserSession(temp.Path, "stranger");
        stranger.CreateProfile("stranger-pin");
        stranger.Open("jttp://pinwheelexplained.com/", "stranger-pin");
        var ex = Assert.Throws<CryptographicException>(() => stranger.UnlockBlob("b1"));
        Assert.Contains("author", ex.Message);

        using var author = new BrowserSession(temp.Path, "vikerus");
        author.Open("jttp://pinwheelexplained.com/", "owner-pin");
        Assert.Contains("PINWHEEL-CANARY-7f3a", author.UnlockBlob("b1"));
        Assert.DoesNotContain("<h1>", File.ReadAllText(path));

        stranger.Open("jttp://pinwheelexplained.com/");
        Assert.Contains("PINWHEEL-CANARY-7f3a", stranger.UnlockBlob("b1"));
        Assert.Equal(published.Integrity, stranger.Document!.Integrity);
    }

    [Fact]
    public void RepublishedNameOpensForAProfileHoldingAnOlderCopy_WithoutAnAttackReport()
    {
        using var temp = new TempDir();
        string Markup(string headline) => $"""
            <?xml version="1.0" encoding="utf-8"?>
            <kml v="1" site="feedexample.com" epoch="1">
              <head><title>{headline}</title>
                <meta name="headline" content="{headline}"/>
                <meta name="tags" content="rss, news"/></head>
              <body><page><h1>{headline}</h1><p>Story text for {headline}.</p></page></body>
            </kml>
            """;
        void Publish(string profile, string pin, string headline)
        {
            var paths = new DataPaths(temp.Path, profile);
            if (!Keystore.Exists(paths)) Keystore.Create(paths, pin);
            var m = Keystore.Unlock(paths, pin);
            DocumentStore.PublishMarkup(paths, "feedexample.com", Markup(headline), m);
            CryptographicOperations.ZeroMemory(m);
        }

        Publish("joshua", "j-pin", "First story");
        Publish("josh", "x-pin", "Second story"); // another profile republishes the unclaimed name

        using var joshua = new BrowserSession(temp.Path, "joshua");
        var page = joshua.Open("jttp://feedexample.com/", "j-pin"); // holds a stale copy of its own
        Assert.Equal("Second story", page.Title);
        var record = LocalRegistry.Resolve(LocalRegistry.LoadOrEmpty(new DataPaths(temp.Path, "joshua")), "feedexample.com")!;
        Assert.Equal(0, record.ContentMismatchCount);
    }

    [Fact]
    public void PlaintextHashIsIndependentOfTheSealingKey()
    {
        using var a = new TempDir();
        using var b = new TempDir();
        string Seal(string dir, string pin)
        {
            var p = new DataPaths(dir);
            Keystore.Create(p, pin);
            var m = Keystore.Unlock(p, pin);
            var d = DocumentStore.Publish(p, Fixture.SiteDir, "pinwheelexplained.com", m);
            CryptographicOperations.ZeroMemory(m);
            return d.Integrity;
        }
        Assert.Equal(Seal(a.Path, "one"), Seal(b.Path, "two"));
    }
}

public class PhonebookTests
{
    private static RegistryFile Index() => new()
    {
        Records =
        [
            new RegistryRecord
            {
                Name = "pinwheelexplained.com",
                Title = "Pinwheel, explained",
                Headline = "A sealed site on Kickl's own network.",
                Tags = ["kickl", "privacy", "sealed-pages"],
                UpdatedUtc = 1_790_000_000,
                Status = "active",
                OwnerId = "kb1deadbeef",
                BadgeId = "kb1bdeadbeef",
                Version = 4
            },
            new RegistryRecord
            {
                Name = "closed.custom",
                Title = "Closed",
                Headline = "Gone",
                Tags = ["privacy"],
                Status = "closed"
            }
        ]
    };

    [Fact]
    public void SearchOverPfsReturnsLiveMetadataAndHidesTheQuery()
    {
        using var server = new PfsRegistryServer(Index());
        using var client = new PfsLookupClient();

        var hello = server.Initiate(client.ClientPublicKeySpki);
        client.Establish(hello);
        var sealedQuery = client.Search("privacy");
        Assert.DoesNotContain("privacy", Encoding.UTF8.GetString(sealedQuery.Ciphertext));

        var result = client.ReadSearch(server.HandleQuery(hello.SessionId, sealedQuery));

        Assert.Equal("privacy", result.Query);
        Assert.Single(result.Results);
        Assert.Equal("pinwheelexplained.com", result.Results[0].Host);
        Assert.Equal("Pinwheel, explained", result.Results[0].Title);
        Assert.Contains("sealed-pages", result.Results[0].Tags);
        Assert.True(result.Results[0].UpdatedUtc > 0);
    }

    [Fact]
    public void ClosedSitesNeverAppearInSearch()
    {
        var result = Phonebook.Search(Index(), "privacy");
        Assert.DoesNotContain(result.Results, r => r.Host == "closed.custom");
    }

    [Fact]
    public void PublishingRequiresHeadlineAndTags()
    {
        using var temp = new TempDir();
        var site = Path.Combine(temp.Path, "site");
        Directory.CreateDirectory(site);
        File.WriteAllText(Path.Combine(site, "site.kml"),
            "<?xml version=\"1.0\"?><kml v=\"1\" site=\"bare.custom\"><head><title>Bare</title></head>" +
            "<body><page><p>no tags here</p></page></body></kml>");

        var paths = new DataPaths(Path.Combine(temp.Path, "data"));
        paths.Ensure();
        var master = RandomNumberGenerator.GetBytes(32);

        Assert.Throws<KmlException>(() =>
            DocumentStore.Publish(paths, site, "bare.custom", master));
    }
}

public class MeshLinkTests
{
    private static (MeshDevice A, MeshDevice B, MeshLink LinkAB, MeshLink LinkBA) Pair()
    {
        var a = MeshDevice.Create();
        var b = MeshDevice.Create();
        var ab = MeshLink.Offer(a, b, out var offer);
        var ba = MeshLink.Accept(b, offer, out var accept);
        ab.Complete(accept, b.PublicKeySpki);
        return (a, b, ab, ba);
    }

    [Fact]
    public void DevicesExchangeFramesInBothDirections()
    {
        var (a, b, ab, ba) = Pair();
        using (a)
        using (b)
        using (ab)
        using (ba)
        {
            var frame = ab.Send(Utf8.B("registry lookup"));
            Assert.Equal("registry lookup", Encoding.UTF8.GetString(ba.Receive(frame)));

            var reply = ba.Send(Utf8.B("not found"));
            Assert.Equal("not found", Encoding.UTF8.GetString(ab.Receive(reply)));
            Assert.Equal(b.DeviceId, ab.PeerId);
            Assert.Equal(a.DeviceId, ba.PeerId);
        }
    }

    [Fact]
    public void TamperedOfferIsRejected()
    {
        using var a = MeshDevice.Create();
        using var b = MeshDevice.Create();
        using var offerLink = MeshLink.Offer(a, b, out var offer);
        offer.EphemeralPublicKey[0] ^= 0x40;

        Assert.Throws<MeshException>(() => MeshLink.Accept(b, offer, out _));
    }

    [Fact]
    public void OfferForAnotherDeviceIsRejected()
    {
        using var a = MeshDevice.Create();
        using var b = MeshDevice.Create();
        using var c = MeshDevice.Create();
        using var offerLink = MeshLink.Offer(a, b, out var offer);

        Assert.Throws<MeshException>(() => MeshLink.Accept(c, offer, out _));
    }

    [Fact]
    public void ReplayedFrameIsRejected()
    {
        var (a, b, ab, ba) = Pair();
        using (a)
        using (b)
        using (ab)
        using (ba)
        {
            var frame = ab.Send(Utf8.B("one"));
            Assert.Equal("one", Encoding.UTF8.GetString(ba.Receive(frame)));
            Assert.Throws<MeshException>(() => ba.Receive(frame));
        }
    }

    [Fact]
    public void EachLinkUsesFreshEphemeralKeys()
    {
        using var a = MeshDevice.Create();
        using var b = MeshDevice.Create();
        using var first = MeshLink.Offer(a, b, out var o1);
        using var second = MeshLink.Offer(a, b, out var o2);

        Assert.NotEqual(o1.EphemeralPublicKey, o2.EphemeralPublicKey);
        Assert.NotEqual(o1.HandshakeNonce, o2.HandshakeNonce);
    }

    [Fact]
    public void OfferFromAnUnpinnedDevice_IsRefused()
    {
        using var a = MeshDevice.Create();
        using var b = MeshDevice.Create();
        using var stranger = MeshDevice.Create();
        using var offerLink = MeshLink.Offer(stranger, b, out var offer);   // validly signed, but not by who b pinned
        Assert.Throws<MeshException>(() => MeshLink.Accept(b, offer, out _, expectedPeerId: a.DeviceId));
        using var ok = MeshLink.Accept(b, offer, out _, expectedPeerId: stranger.DeviceId);
    }

    [Fact]
    public void HandshakeKeys_AreRefusedWhenNotP256_OrNotKeys()
    {
        using var a = MeshDevice.Create();
        using var b = MeshDevice.Create();

        using var link1 = MeshLink.Offer(a, b, out var wrongCurve);
        using (var p384 = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP384))
            wrongCurve.EphemeralPublicKey = p384.ExportSubjectPublicKeyInfo();
        wrongCurve.Signature = a.Sign($"kickl/v1/mesh-offer|{wrongCurve.FromDeviceId}|{wrongCurve.ToDeviceId}|" +
            $"{Convert.ToBase64String(wrongCurve.EphemeralPublicKey)}|{Convert.ToBase64String(wrongCurve.HandshakeNonce)}");
        Assert.Throws<MeshException>(() => MeshLink.Accept(b, wrongCurve, out _));

        using var link2 = MeshLink.Offer(a, b, out var garbage);
        garbage.EphemeralPublicKey = [1, 2, 3];
        garbage.Signature = a.Sign($"kickl/v1/mesh-offer|{garbage.FromDeviceId}|{garbage.ToDeviceId}|" +
            $"{Convert.ToBase64String(garbage.EphemeralPublicKey)}|{Convert.ToBase64String(garbage.HandshakeNonce)}");
        Assert.Throws<MeshException>(() => MeshLink.Accept(b, garbage, out _));
    }

    [Fact]
    public void FrameWithAForeignNonce_IsRefused()
    {
        var (a, b, ab, ba) = Pair();
        using (a)
        using (b)
        using (ab)
        using (ba)
        {
            var frame = ab.Send(Utf8.B("hello"));
            frame.Nonce = new byte[12];
            Assert.Throws<MeshException>(() => ba.Receive(frame));
        }
    }

    [Fact]
    public void TamperedFrameIsRejected()
    {
        var (a, b, ab, ba) = Pair();
        using (a)
        using (b)
        using (ab)
        using (ba)
        {
            var frame = ab.Send(Utf8.B("sealed"));
            frame.Ciphertext[0] ^= 1;
            Assert.Throws<MeshException>(() => ba.Receive(frame));
        }
    }
}
