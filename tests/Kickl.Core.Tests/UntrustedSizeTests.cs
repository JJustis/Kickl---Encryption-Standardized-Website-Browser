using Kickl.Core.Crypto;
using Kickl.Core.Documents;
using Kickl.Core.Registry;
using Kickl.Core.Relay;
using Kickl.Core.Util;
using Xunit;

namespace Kickl.Core.Tests;

/// <summary>A hostile site, relay or registry chooses how big its data is. Nothing may be held in memory before it is bounded.</summary>
public class UntrustedSizeTests
{
    private static string WritePackage(string dir, string name, Action<SealedDocument> shape)
    {
        var doc = new SealedDocument { Site = "big.example.com" };
        shape(doc);
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(doc));
        return path;
    }

    [Fact]
    public void ADocumentFileOverTheLimitIsRefusedWithoutBeingRead()
    {
        using var temp = new TempDir();
        var path = Path.Combine(temp.Path, "huge.json");
        using (var f = new FileStream(path, FileMode.Create)) f.SetLength(DocumentStore.MaxDocumentBytes + 1);   // sparse: nothing is written

        var ex = Assert.Throws<IntegrityException>(() => DocumentStore.ReadDocumentFile(path));
        Assert.Contains("larger", ex.Message);
    }

    [Fact]
    public void ADocumentClaimingTooManyOrTooBigBlobsIsRefused()
    {
        using var temp = new TempDir();

        var many = WritePackage(temp.Path, "many.json", d =>
        {
            for (var i = 0; i < DocumentStore.MaxBlobs + 1; i++)
                d.Blobs["b" + i] = new SealedBlob { Ciphertext = [1], Nonce = new byte[12] };
        });
        Assert.Throws<IntegrityException>(() => DocumentStore.ReadDocumentFile(many));

        var fat = WritePackage(temp.Path, "fat.json", d =>
            d.Blobs["x"] = new SealedBlob { Ciphertext = new byte[DocumentStore.MaxBlobBytes + 1], Nonce = new byte[12] });
        Assert.Throws<IntegrityException>(() => DocumentStore.ReadDocumentFile(fat));

        var pageText = WritePackage(temp.Path, "text.json", d =>
            d.SealedMarkup = new WrappedKey { Ciphertext = new byte[2 * 1024 * 1024], Nonce = new byte[12] });
        Assert.Throws<IntegrityException>(() => DocumentStore.ReadDocumentFile(pageText));
    }

    [Fact]
    public void AnOrdinaryDocumentStillLoads()
    {
        using var temp = new TempDir();
        var paths = new DataPaths(temp.Path);
        Keystore.Create(paths, "pin");
        var master = Keystore.Unlock(paths, "pin");
        DocumentStore.Publish(paths, Fixture.SiteDir, "pinwheelexplained.com", master);
        Assert.NotNull(DocumentStore.Load(paths, "pinwheelexplained.com"));
    }

    [Fact]
    public void ARegistrySnapshotThatIsHugeOrMalformedIsRefused()
    {
        Assert.Throws<FormatException>(() => RegistrySnapshot.Parse(new byte[33 * 1024 * 1024]));

        var longName = new string('a', 300);
        var json = $"{{\"version\":1,\"generatedAt\":1,\"rootHash\":\"\",\"sig\":\"\",\"records\":[{{\"name\":\"{longName}.com\"}}]}}";
        Assert.Throws<FormatException>(() => RegistrySnapshot.Parse(System.Text.Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void ATunnelReplyOrRequestOfAbsurdSizeIsRefusedBeforeItIsDecrypted()
    {
        using var temp = new TempDir();
        var paths = new DataPaths(temp.Path);
        Keystore.Create(paths, "pin");
        var master = Keystore.Unlock(paths, "pin");
        DocumentStore.Publish(paths, Fixture.SiteDir, "pinwheelexplained.com", master);
        var record = LocalRegistry.Resolve(LocalRegistry.LoadOrEmpty(paths), "pinwheelexplained.com")!;
        using var publisher = DirectPublisher.Open(paths, master);
        var peer = new DirectPeer(record.OriginDeviceId, B64u.Decode(record.DirectPublicKey));

        using var exchange = DirectFetchClient.Begin(peer, "pinwheelexplained.com");
        var honest = publisher.Respond(exchange.Request);
        honest.Ciphertext = new byte[(int)DocumentStore.MaxDocumentBytes + 1];      // a relay that lies about size
        var ex = Assert.Throws<DirectFetchException>(() => exchange.Complete(honest));
        Assert.Contains("larger", ex.Message);

        using var exchange2 = DirectFetchClient.Begin(peer, "pinwheelexplained.com");
        exchange2.Request.Ciphertext = new byte[128 * 1024];
        var big = Assert.Throws<DirectFetchException>(() => publisher.Respond(exchange2.Request));
        Assert.Contains("larger", big.Message);
    }
}
