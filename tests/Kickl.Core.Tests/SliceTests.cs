using System.Security.Cryptography;
using System.Text;
using Kickl.Core.Crypto;
using Kickl.Core.Documents;
using Kickl.Core.Markup;
using Kickl.Core.Names;
using Kickl.Core.Privacy;
using Kickl.Core.Session;
using Kickl.Core.Util;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;

namespace Kickl.Core.Tests;

public class CryptoAeadTests
{
    [Fact]
    public void Argon2id_MatchesRfc9106FourLaneVector()
    {
        var password = Enumerable.Repeat((byte)0x01, 32).ToArray();
        var salt = Enumerable.Repeat((byte)0x02, 16).ToArray();
        var secret = Enumerable.Repeat((byte)0x03, 8).ToArray();
        var additional = Enumerable.Repeat((byte)0x04, 12).ToArray();
        var expected = Convert.FromHexString(
            "0d640df58d78766c08c037a34a8b53c9d01ef0452d75b65eb52520e96b01e659");
        var parameters = new Argon2Parameters.Builder(Argon2Parameters.Argon2id)
            .WithVersion(Argon2Parameters.Version13)
            .WithIterations(3)
            .WithMemoryAsKB(32)
            .WithParallelism(4)
            .WithSalt(salt)
            .WithSecret(secret)
            .WithAdditional(additional)
            .Build();
        var actual = new byte[32];
        var generator = new Argon2BytesGenerator();
        generator.Init(parameters);
        generator.GenerateBytes(password, actual);

        Assert.Equal(expected, actual);
        CryptographicOperations.ZeroMemory(password);
        CryptographicOperations.ZeroMemory(secret);
        CryptographicOperations.ZeroMemory(actual);
    }

    [Fact]
    public void Aes256Gcm_MatchesNistEmptyMessageVector()
    {
        // NIST SP 800-38D, AES-256, 96-bit IV, empty plaintext and AAD.
        var key = Convert.FromHexString("0000000000000000000000000000000000000000000000000000000000000000");
        var nonce = Convert.FromHexString("000000000000000000000000");
        var expectedTag = Convert.FromHexString("530f8afbc74536b9a963b4f1c4cb738b");

        Assert.Equal(expectedTag, Aead.SealWithNonce(key, nonce, ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty));
        Assert.Empty(Aead.Open(key, nonce, expectedTag, ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void RoundTrip_AndAadBinding()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var aad = Encoding.UTF8.GetBytes("field-A");
        var (nonce, ct) = Aead.Seal(key, "hello"u8, aad);

        Assert.Equal("hello", Encoding.UTF8.GetString(Aead.Open(key, nonce, ct, aad)));
        Assert.ThrowsAny<CryptographicException>(() =>
            Aead.Open(key, nonce, ct, Encoding.UTF8.GetBytes("field-B")));
    }

    [Fact]
    public void TamperedCiphertext_FailsClosed()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var (nonce, ct) = Aead.Seal(key, "hello"u8, ReadOnlySpan<byte>.Empty);
        ct[0] ^= 0x01;
        Assert.ThrowsAny<CryptographicException>(() => Aead.Open(key, nonce, ct, ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void CounterNonces_AreDistinctAndBigEndian()
    {
        var a = Aead.CounterNonce(1);
        var b = Aead.CounterNonce(2);
        Assert.Equal(12, a.Length);
        Assert.NotEqual(a, b);
        Assert.Equal(1UL, System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(a));
    }

    [Fact]
    public void Hkdf_IsDeterministicPerSalt()
    {
        var ikm = RandomNumberGenerator.GetBytes(32);
        var salt = RandomNumberGenerator.GetBytes(32);
        Assert.Equal(Hkdf.Derive(ikm, salt, "info"), Hkdf.Derive(ikm, salt, "info"));
        Assert.NotEqual(Hkdf.Derive(ikm, salt, "info"), Hkdf.Derive(ikm, salt, "other"));
    }

    [Fact]
    public void HkdfSha512_MatchesConfiguredIndependentExpansion()
    {
        // SHA-512 HKDF is the configured project suite; fixed inputs pin its exact encoding.
        var ikm = Enumerable.Range(0, 22).Select(i => (byte)i).ToArray();
        var salt = Enumerable.Range(0, 13).Select(i => (byte)(0x20 + i)).ToArray();
        var info = "kickl/test/vector";
        var expected = Convert.FromHexString("891d490732a36f73d05c5d2b3f37d8d06e3136638f95a5209ef3413587c413bfcf43847b562eaee4abc2");
        Assert.Equal(expected, Hkdf.Derive(ikm, salt, info, 42));
    }
}

public class KeystoreTests
{
    [Fact]
    public void NewKeystore_UsesArgon2idAndRecordsParameters()
    {
        using var temp = new TempDir();
        var paths = new DataPaths(temp.Path);
        Keystore.Create(paths, "correct horse");

        var file = System.Text.Json.JsonSerializer.Deserialize<KeystoreFile>(File.ReadAllText(paths.Keystore))!;
        Assert.Equal(PinKdf.Id, file.Kdf);
        Assert.Equal(0, file.Iterations);
        Assert.Equal(PinKdf.MemoryKiB, file.MemoryKiB);
        Assert.Equal(PinKdf.Passes, file.Passes);
        Assert.Equal(PinKdf.Parallelism, file.Parallelism);
        Assert.Equal(32, Keystore.Unlock(paths, "correct horse").Length);
    }

    [Fact]
    public void LegacyPbkdf2Keystore_MigratesAfterSuccessfulUnlock()
    {
        using var temp = new TempDir();
        var paths = new DataPaths(temp.Path);
        paths.Ensure();
        const string pin = "legacy pin";
        var salt = RandomNumberGenerator.GetBytes(PinKdf.SaltSize);
        var originalMaster = RandomNumberGenerator.GetBytes(Aead.KeySize);
        var legacyKek = PinKdf.Derive(pin, PinKdf.LegacyId, salt, PinKdf.KeySize,
            legacyIterations: PinKdf.LegacyIterations);
        var (nonce, wrapped) = Aead.Seal(legacyKek, originalMaster, Encoding.UTF8.GetBytes("kickl/v1/keystore"));
        var legacyFile = new KeystoreFile
        {
            Kdf = PinKdf.LegacyId,
            Iterations = PinKdf.LegacyIterations,
            Salt = salt,
            Nonce = nonce,
            WrappedMaster = wrapped
        };
        File.WriteAllText(paths.Keystore, System.Text.Json.JsonSerializer.Serialize(legacyFile));
        CryptographicOperations.ZeroMemory(legacyKek);

        var unlocked = Keystore.Unlock(paths, pin);
        var migrated = System.Text.Json.JsonSerializer.Deserialize<KeystoreFile>(File.ReadAllText(paths.Keystore))!;
        Assert.Equal(originalMaster, unlocked);
        Assert.Equal(PinKdf.Id, migrated.Kdf);
        Assert.Equal(PinKdf.MemoryKiB, migrated.MemoryKiB);
        Assert.Equal(originalMaster, Keystore.Unlock(paths, pin));
        CryptographicOperations.ZeroMemory(originalMaster);
        CryptographicOperations.ZeroMemory(unlocked);
    }

    [Fact]
    public void EarlierArgon2idP1Keystore_MigratesToP4AfterSuccessfulUnlock()
    {
        using var temp = new TempDir();
        var paths = new DataPaths(temp.Path);
        paths.Ensure();
        const string pin = "previous argon profile";
        var salt = RandomNumberGenerator.GetBytes(PinKdf.SaltSize);
        var originalMaster = RandomNumberGenerator.GetBytes(Aead.KeySize);
        var kek = PinKdf.Derive(pin, PinKdf.LegacyArgon2Id, salt, PinKdf.KeySize,
            PinKdf.MemoryKiB, PinKdf.Passes, PinKdf.LegacyArgon2Parallelism);
        var (nonce, wrapped) = Aead.Seal(kek, originalMaster, Encoding.UTF8.GetBytes("kickl/v1/keystore"));
        var previousFile = new KeystoreFile
        {
            Kdf = PinKdf.LegacyArgon2Id,
            MemoryKiB = PinKdf.MemoryKiB,
            Passes = PinKdf.Passes,
            Parallelism = PinKdf.LegacyArgon2Parallelism,
            Salt = salt,
            Nonce = nonce,
            WrappedMaster = wrapped
        };
        File.WriteAllText(paths.Keystore, System.Text.Json.JsonSerializer.Serialize(previousFile));
        CryptographicOperations.ZeroMemory(kek);

        var unlocked = Keystore.Unlock(paths, pin);
        var migrated = System.Text.Json.JsonSerializer.Deserialize<KeystoreFile>(File.ReadAllText(paths.Keystore))!;
        Assert.Equal(originalMaster, unlocked);
        Assert.Equal(PinKdf.Id, migrated.Kdf);
        Assert.Equal(4, migrated.Parallelism);
        Assert.Equal(originalMaster, Keystore.Unlock(paths, pin));
        CryptographicOperations.ZeroMemory(originalMaster);
        CryptographicOperations.ZeroMemory(unlocked);
    }

    [Fact]
    public void Create_DoesNotOverwriteExistingKeystore()
    {
        using var temp = new TempDir();
        var paths = new DataPaths(temp.Path);
        Keystore.Create(paths, "first pin");
        var original = File.ReadAllBytes(paths.Keystore);

        Assert.Throws<IOException>(() => Keystore.Create(paths, "second pin"));
        Assert.Equal(original, File.ReadAllBytes(paths.Keystore));
        Assert.Throws<InvalidUserKeyException>(() => Keystore.Unlock(paths, "second pin"));
        var master = Keystore.Unlock(paths, "first pin");
        CryptographicOperations.ZeroMemory(master);
    }

    [Fact]
    public void Create_RejectsEmptyPinWithoutCreatingProfileKey()
    {
        using var temp = new TempDir();
        var paths = new DataPaths(temp.Path);

        Assert.Throws<ArgumentException>(() => Keystore.Create(paths, ""));
        Assert.False(Keystore.Exists(paths));
    }

    [Fact]
    public void WrongPin_Fails_AndKeystoreHasNoPinOrMaster()
    {
        using var temp = new TempDir();
        var paths = new DataPaths(temp.Path);
        Keystore.Create(paths, "correct horse");

        Assert.Throws<InvalidUserKeyException>(() => Keystore.Unlock(paths, "wrong horse"));

        var json = File.ReadAllText(paths.Keystore);
        Assert.DoesNotContain("correct horse", json);

        var master = Keystore.Unlock(paths, "correct horse");
        Assert.Equal(32, master.Length);
        Assert.DoesNotContain(Convert.ToBase64String(master), json);
        Assert.DoesNotContain(Convert.ToHexString(master), json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Kdf", "unknown-kdf")]
    [InlineData("Iterations", "1")]
    [InlineData("SaltSize", "1")]
    [InlineData("KeySize", "16")]
    public void Keystore_RejectsUnsupportedOrTamperedKdfMetadata(string property, string value)
    {
        using var temp = new TempDir();
        var paths = new DataPaths(temp.Path);
        Keystore.Create(paths, "correct horse");

        var json = File.ReadAllText(paths.Keystore);
        var metadata = System.Text.Json.JsonDocument.Parse(json);
        var fields = metadata.RootElement.EnumerateObject()
            .ToDictionary(x => x.Name, x => x.Value.GetRawText());
        fields[property] = property == "Kdf" ? System.Text.Json.JsonSerializer.Serialize(value) : value;
        File.WriteAllText(paths.Keystore, System.Text.Json.JsonSerializer.Serialize(fields));

        Assert.Throws<InvalidUserKeyException>(() => Keystore.Unlock(paths, "correct horse"));
    }
}

public class NamesTests
{
    [Theory]
    [InlineData("jttp://pinwheelexplained.com/", "pinwheelexplained.com")]
    [InlineData("JTTP://Example.custom/a?b=1#c", "example.custom")]
    [InlineData("jttp://foo.bar", "foo.bar")]
    public void ParsesValid(string input, string host)
    {
        Assert.True(JttpUrl.TryParse(input, out var url, out var error), error);
        Assert.Equal(host, url!.Host);
    }

    [Theory]
    [InlineData("http://example.com/")]
    [InlineData("jttp://localhost/")]
    [InlineData("jttp://one/")]
    [InlineData("jttp://user@example.com/")]
    [InlineData("jttp://example.com:443/")]
    [InlineData("jttp://example.com/a/../b")]
    [InlineData("jttp://home.custom/")]
    public void RejectsInvalid(string input) =>
        Assert.False(JttpUrl.TryParse(input, out _, out _));

    [Theory]
    [InlineData("jttp://pinwheelexplained.com/", InputLane.Sealed)]
    [InlineData("pinwheelexplained.custom", InputLane.Sealed)]
    [InlineData("https://example.com/x", InputLane.Clearnet)]
    [InlineData("example.com", InputLane.Clearnet)]
    [InlineData("how do batteries work", InputLane.Search)]
    [InlineData("", InputLane.Invalid)]
    public void ClassifiesLanes(string input, InputLane lane) =>
        Assert.Equal(lane, InputClassifier.Classify(input).Lane);
}

public class KmlTests
{
    private const string Good = """
        <kml v="1"><head><title>T</title></head><body><page>
        <h1>Hello</h1><p>World</p><hr/><blob id="b1" mime="text/plain"/>
        </page></body></kml>
        """;

    [Fact]
    public void ParsesBlocks()
    {
        var page = Kml.Parse(Good, "example.custom");
        Assert.Equal("T", page.Title);
        Assert.Contains(page.Blocks, b => b.Kind == BlockKind.Heading && b.Text == "Hello");
        Assert.Contains(page.Blocks, b => b.Kind == BlockKind.Blob && b.Id == "b1");
    }

    [Theory]
    [InlineData("<kml v=\"1\"><body><script>alert(1)</script></body></kml>")]
    [InlineData("<kml v=\"1\"><body><p onclick=\"x()\">hi</p></body></kml>")]
    [InlineData("<kml v=\"1\"><body><iframe src=\"https://x\"/></body></kml>")]
    [InlineData("<kml v=\"2\"><body><p>x</p></body></kml>")]
    [InlineData("<html><body>x</body></html>")]
    public void RejectsUnsafeOrInvalid(string xml) =>
        Assert.Throws<KmlException>(() => Kml.Parse(xml, "example.custom"));

    [Fact]
    public void FixtureParses()
    {
        var page = Kml.Parse(File.ReadAllText(Fixture.KmlPath), "pinwheelexplained.com");
        Assert.Equal("Pinwheel, explained", page.Title);
        Assert.Contains(page.Blocks, b => b.Id == "b1");
    }
}

public class KeystrokeShieldTests
{
    [Fact]
    public void SameCharacterTwice_ProducesDifferentCiphertext()
    {
        using var secret = SessionSecret.Create(RandomNumberGenerator.GetBytes(32));
        using var shield = new KeystrokeShield(secret, "omnibox");
        shield.Append('a');
        shield.Append('a');

        var cells = shield.Cells;
        Assert.NotEqual(cells[0].Ciphertext, cells[1].Ciphertext);
        Assert.NotEqual(cells[0].Nonce, cells[1].Nonce);

        var revealed = shield.Reveal();
        Assert.Equal("aa", new string(revealed));
        Array.Clear(revealed);
    }

    [Fact]
    public void CiphertextBuffer_ContainsNoPlaintext()
    {
        using var secret = SessionSecret.Create(RandomNumberGenerator.GetBytes(32));
        using var shield = new KeystrokeShield(secret, "omnibox");
        shield.Append("jttp://pinwheelexplained.com");

        var snapshot = shield.CiphertextSnapshot();
        Assert.DoesNotContain("jttp", Encoding.UTF8.GetString(snapshot));
        Assert.DoesNotContain("pinwheel", Encoding.UTF8.GetString(snapshot));
        CryptographicOperations.ZeroMemory(snapshot);
    }

    [Fact]
    public void CellsAreBoundToField()
    {
        using var secret = SessionSecret.Create(RandomNumberGenerator.GetBytes(32));
        using var shield = new KeystrokeShield(secret, "omnibox");
        shield.Append('x');

        var cell = shield.Cells[0];
        var wrongAad = Encoding.UTF8.GetBytes(
            $"kickl/v1/keystroke|{secret.IdHex}|pin|{cell.Counter}");
        Assert.ThrowsAny<CryptographicException>(() =>
            Aead.Open(secret.Key, cell.Nonce, cell.Ciphertext, wrongAad));
    }

    [Fact]
    public void ReorderedCells_FailClosed()
    {
        using var secret = SessionSecret.Create(RandomNumberGenerator.GetBytes(32));
        using var shield = new KeystrokeShield(secret, "omnibox");
        shield.Append("ab");

        var list = (List<KeyCell>)shield.Cells;
        (list[0], list[1]) = (list[1], list[0]);
        Assert.ThrowsAny<CryptographicException>(() => shield.Reveal());
    }

    [Fact]
    public void DisposedSession_CanNoLongerReveal()
    {
        var secret = SessionSecret.Create(RandomNumberGenerator.GetBytes(32));
        using var shield = new KeystrokeShield(secret, "omnibox");
        shield.Append("secret");
        secret.Dispose();

        Assert.Throws<ObjectDisposedException>(() => shield.Reveal());
        Assert.Throws<ObjectDisposedException>(() => secret.Key.ToArray());
    }
}

public class SessionTemporalityTests
{
    [Fact]
    public void EveryOpeningGetsAFreshSessionKeyPair()
    {
        var master = RandomNumberGenerator.GetBytes(32);
        using var first = SessionSecret.Create(master);
        using var second = SessionSecret.Create(master);

        Assert.NotEqual(first.Key.ToArray(), second.Key.ToArray());
        Assert.NotEqual(first.SessionPublicKeySpki, second.SessionPublicKeySpki);
        Assert.NotEqual(first.Salt, second.Salt);
    }

    [Fact]
    public void SessionKeyPairDiesWithTheSession()
    {
        var secret = SessionSecret.Create(RandomNumberGenerator.GetBytes(32));
        var pub = secret.SessionPublicKeySpki;
        Assert.NotEmpty(pub);

        secret.Dispose();
        Assert.Throws<ObjectDisposedException>(() => secret.SessionPublicKeySpki);
    }
}

public class SealedDocumentTests
{
    [Fact]
    public void PublishOpenUnlock_LeavesNoPlaintextOnDisk_AndFailsWhenLocked()
    {
        using var temp = new TempDir();
        var paths = new DataPaths(temp.Path);
        Keystore.Create(paths, "pinwheel-demo");
        var master = Keystore.Unlock(paths, "pinwheel-demo");

        var doc = DocumentStore.Publish(paths, Fixture.SiteDir, "pinwheelexplained.com", master);
        CryptographicOperations.ZeroMemory(master);

        Assert.Equal(3, doc.Blobs.Count);
        Assert.DoesNotContain("PINWHEEL-CANARY", File.ReadAllText(paths.SiteDoc("pinwheelexplained.com")));

        using var session = new BrowserSession(temp.Path);
        Assert.Throws<InvalidOperationException>(() => session.UnlockBlob("b1"));

        var page = session.Open("jttp://pinwheelexplained.com/", "pinwheel-demo");
        Assert.NotNull(page);
        Assert.True(session.IsUnlocked);

        var note = session.UnlockBlob("b1");
        Assert.Contains("PINWHEEL-CANARY-7f3a", note);
        Assert.False(session.IsNameVerified("pinwheelexplained.com"));

        session.Lock();
        Assert.Throws<InvalidOperationException>(() => session.UnlockBlob("b1"));
    }

    [Fact]
    public void TamperedDocument_FailsIntegrity()
    {
        using var temp = new TempDir();
        var paths = new DataPaths(temp.Path);
        Keystore.Create(paths, "pw");
        var master = Keystore.Unlock(paths, "pw");
        DocumentStore.Publish(paths, Fixture.SiteDir, "pinwheelexplained.com", master);
        CryptographicOperations.ZeroMemory(master);

        var path = paths.SiteDoc("pinwheelexplained.com");
        var json = File.ReadAllText(path);
        Assert.DoesNotContain("sealed document that this browser authenticates", json); // page text is sealed at rest
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        var ct = node["SealedMarkup"]!["Ciphertext"]!.GetValue<string>();
        node["SealedMarkup"]!["Ciphertext"] = (ct[0] == 'A' ? "B" : "A") + ct[1..];
        File.WriteAllText(path, node.ToJsonString());

        Assert.Throws<IntegrityException>(() => DocumentStore.Load(paths, "pinwheelexplained.com"));
    }

    [Fact]
    public void WrongPin_CannotOpenProfile()
    {
        using var temp = new TempDir();
        var paths = new DataPaths(temp.Path);
        Keystore.Create(paths, "right");
        Assert.Throws<InvalidUserKeyException>(() => Keystore.Unlock(paths, "wrong"));
    }
}

internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kickl-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }
}

internal static class Fixture
{
    public static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(System.IO.Path.Combine(dir.FullName, "Kickl.slnx")))
                dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("Repo root not found.");
        }
    }

    public static string SiteDir => System.IO.Path.Combine(RepoRoot, "sites", "pinwheelexplained.com");
    public static string KmlPath => System.IO.Path.Combine(SiteDir, "site.kml");
}

