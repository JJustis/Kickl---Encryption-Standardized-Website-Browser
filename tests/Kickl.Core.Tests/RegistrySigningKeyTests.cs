using System.Text;
using System.Text.Json;
using Kickl.Core.Registry;

namespace Kickl.Core.Tests;

public class RegistrySigningKeyTests
{
    [Fact]
    public void SigningKey_PublicVerifierAcceptsItsSignatures()
    {
        using var key = RegistrySigningKeyPair.Generate();
        using var verifier = key.Public.CreateVerifier();
        var payload = Encoding.UTF8.GetBytes("{\"version\":7}");

        var signature = key.Sign(payload);

        Assert.True(verifier.Verify(payload, signature));
        Assert.False(verifier.Verify(Encoding.UTF8.GetBytes("{\"version\":8}"), signature));
    }

    [Fact]
    public void SigningKey_FingerprintIsPublishedAndStable()
    {
        using var key = RegistrySigningKeyPair.Generate();

        Assert.Equal(key.Public.Fingerprint, key.Public.Fingerprint);
        Assert.Equal(64, key.Public.Fingerprint.Length);
        Assert.Equal("Ed25519+ML-DSA-65", key.Public.Algorithm);
        key.Public.Validate();
    }

    [Fact]
    public void SigningKey_RoundTripsThroughSeedAndPkcs8()
    {
        using var key = RegistrySigningKeyPair.Generate();
        using var loaded = RegistrySigningKeyPair.Load(key.Seed, key.MlDsaPkcs8);

        Assert.Equal(key.Public.Fingerprint, loaded.Public.Fingerprint);
        using var verifier = key.Public.CreateVerifier();
        var payload = Encoding.UTF8.GetBytes("payload");
        Assert.True(verifier.Verify(payload, loaded.Sign(payload)));
    }

    [Fact]
    public void SigningKey_PublicJsonRoundTrips()
    {
        using var key = RegistrySigningKeyPair.Generate();
        var json = JsonSerializer.Serialize(key.Public, RegistrySigningKey.Json);
        var parsed = JsonSerializer.Deserialize<RegistrySigningKey>(json, RegistrySigningKey.Json)!;

        parsed.Validate();
        using var verifier = parsed.CreateVerifier();
        Assert.True(verifier.Verify(Encoding.UTF8.GetBytes("x"), key.Sign(Encoding.UTF8.GetBytes("x"))));
    }

    [Fact]
    public void SignedSnapshot_IsAcceptedByTheMirror()
    {
        using var key = RegistrySigningKeyPair.Generate();
        using var verifier = key.Public.CreateVerifier();
        var snapshot = new RegistrySnapshot
        {
            Version = 1,
            GeneratedAt = 1_800_000_000,
            Records =
            [
                new RegistryRecord
                {
                    Name = "a.custom", Headline = "A", Tags = ["a"], OwnerId = "owner-a"
                },
                new RegistryRecord
                {
                    Name = "b.custom", Headline = "B", Tags = ["b"], OwnerId = "owner-b"
                }
            ]
        };
        snapshot.RootHash = snapshot.ComputeRootHex();
        snapshot.Sig = key.Sign(snapshot.CanonicalPayload());

        var state = new MirrorState();
        var result = RegistryMirror.Apply(state, snapshot.CanonicalPayload(), snapshot.Sig,
            verifier, nowUnix: 1_800_000_100);

        Assert.Equal(SnapshotStatus.Accepted, result.Status);
        Assert.Equal(1, state.Version);
        Assert.Equal(snapshot.RootHash, state.RootHash);
        Assert.Equal(2, state.Pins.Count);
    }

    [Fact]
    public void SigningKey_ValidateRejectsAMismatchedFingerprint()
    {
        using var key = RegistrySigningKeyPair.Generate();
        var published = key.Public;
        published.Fingerprint = new string('0', 64);

        Assert.Throws<FormatException>(published.Validate);
    }

    [Fact]
    public void SigningKey_ValidateRejectsTruncatedKeys()
    {
        using var key = RegistrySigningKeyPair.Generate();
        var publicValue = key.Public;
        publicValue.Ed25519PublicKey = publicValue.Ed25519PublicKey[..8];

        Assert.Throws<FormatException>(publicValue.Validate);
    }
}
