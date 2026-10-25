using System.Text;
using Kickl.Core.Registry;
using Kickl.Core.Util;

namespace Kickl.Core.Tests;

public class RegistryVerificationTests
{
    // Known-answer values computed with an independent SHA-256 implementation
    // following RFC 6962: leaves alpha..echo.
    private const string ExpectedRoot =
        "27fb5ac1b7d728b57862f8db5ad1fdb3f6f8f9281552842c2242cfaba97f8646";

    private static readonly string[] Words = ["alpha", "bravo", "charlie", "delta", "echo"];

    [Fact]
    public void MerkleRoot_MatchesIndependentKnownAnswer()
    {
        Assert.Equal(ExpectedRoot, Hashing.Hex(MerkleLog.Root(LeafHashes())));
    }

    [Fact]
    public void InclusionProofs_MatchIndependentKnownAnswers()
    {
        var leaves = LeafHashes();
        var root = MerkleLog.Root(leaves);

        var expected = new Dictionary<int, string[]>
        {
            [0] =
            [
                "798e6a07734241cb4ee9e30a512d3ac722a5fde3cbf9340755301d2715fd7810",
                "949d44dcd632bd90fef86f33c218f61f59e9880fba34fa10bbd89cdc704d8360",
                "4a3cb744e0a2fb15b4b4c045b85e2825ee7012ae25b6c2111f341a33621fc5e7"
            ],
            [3] =
            [
                "f931962f0917c346d447293c07b687ae1609f7003f8a44a06a75c4145b1e1929",
                "fb33dff7b9f27b94d57431d3c72e3268e5dda9c4de3d2b0d34ab34146d6e6806",
                "4a3cb744e0a2fb15b4b4c045b85e2825ee7012ae25b6c2111f341a33621fc5e7"
            ],
            [4] = ["e872bf22aae12fbbdc419c9a6b42ee30943539d08c5de1297abc4f847d3c1644"]
        };

        foreach (var (index, hashes) in expected)
        {
            var proof = MerkleLog.InclusionProof(leaves, index);
            Assert.Equal(hashes, proof.Select(p => Hashing.Hex(p)).ToArray());
            Assert.True(MerkleLog.VerifyInclusion(leaves[index], index, leaves.Count, proof, root));
        }
    }

    [Fact]
    public void InclusionProof_RejectsTamperingAndBadPositions()
    {
        var leaves = LeafHashes();
        var root = MerkleLog.Root(leaves);
        var proof = MerkleLog.InclusionProof(leaves, 2);

        var tampered = proof.Select(p => (byte[])p.Clone()).ToList();
        tampered[0][0] ^= 0xff;
        Assert.False(MerkleLog.VerifyInclusion(leaves[2], 2, leaves.Count, tampered, root));
        Assert.False(MerkleLog.VerifyInclusion(leaves[2], 3, leaves.Count, proof, root));
        Assert.False(MerkleLog.VerifyInclusion(leaves[2], 2, leaves.Count, proof, new byte[32]));
        Assert.False(MerkleLog.VerifyInclusion(leaves[2], -1, leaves.Count, proof, root));
        Assert.False(MerkleLog.VerifyInclusion(leaves[2], 2, 0, proof, root));
    }

    [Fact]
    public void InclusionProof_WorksForEverySizeAndIndex()
    {
        for (var size = 1; size <= 9; size++)
        {
            var leaves = Enumerable.Range(0, size)
                .Select(i => MerkleLog.LeafHash(Encoding.UTF8.GetBytes("leaf-" + i)))
                .ToList();
            var root = MerkleLog.Root(leaves);

            for (var index = 0; index < size; index++)
            {
                var proof = MerkleLog.InclusionProof(leaves, index);
                Assert.True(MerkleLog.VerifyInclusion(leaves[index], index, size, proof, root),
                    $"size {size}, index {index}");
            }
        }
    }

    [Fact]
    public void ConsistencyProofs_MatchIndependentKnownAnswers()
    {
        var leaves = LeafHashes();
        var root = MerkleLog.Root(leaves);

        var expected = new Dictionary<int, string[]>
        {
            [1] =
            [
                "798e6a07734241cb4ee9e30a512d3ac722a5fde3cbf9340755301d2715fd7810",
                "949d44dcd632bd90fef86f33c218f61f59e9880fba34fa10bbd89cdc704d8360",
                "4a3cb744e0a2fb15b4b4c045b85e2825ee7012ae25b6c2111f341a33621fc5e7"
            ],
            [2] =
            [
                "949d44dcd632bd90fef86f33c218f61f59e9880fba34fa10bbd89cdc704d8360",
                "4a3cb744e0a2fb15b4b4c045b85e2825ee7012ae25b6c2111f341a33621fc5e7"
            ],
            [3] =
            [
                "f931962f0917c346d447293c07b687ae1609f7003f8a44a06a75c4145b1e1929",
                "5c7117fb9edb0cec387257891105da6a6616722af247083e2d6eda671529cdc5",
                "fb33dff7b9f27b94d57431d3c72e3268e5dda9c4de3d2b0d34ab34146d6e6806",
                "4a3cb744e0a2fb15b4b4c045b85e2825ee7012ae25b6c2111f341a33621fc5e7"
            ],
            [4] = ["4a3cb744e0a2fb15b4b4c045b85e2825ee7012ae25b6c2111f341a33621fc5e7"]
        };

        foreach (var (firstSize, hashes) in expected)
        {
            var proof = MerkleLog.ConsistencyProof(firstSize, leaves);
            Assert.Equal(hashes, proof.Select(h => Hashing.Hex(h)).ToArray());
            Assert.True(MerkleLog.VerifyConsistency(firstSize, leaves.Count,
                MerkleLog.Root(leaves.Take(firstSize).ToList()), root, proof));
        }
    }

    [Fact]
    public void ConsistencyProof_WorksForEveryPairOfSizes()
    {
        for (var size = 2; size <= 9; size++)
        {
            var leaves = Enumerable.Range(0, size)
                .Select(i => MerkleLog.LeafHash(Encoding.UTF8.GetBytes("leaf-" + i)))
                .ToList();
            var root = MerkleLog.Root(leaves);

            for (var first = 1; first < size; first++)
            {
                var oldRoot = MerkleLog.Root(leaves.Take(first).ToList());
                var proof = MerkleLog.ConsistencyProof(first, leaves);
                Assert.True(MerkleLog.VerifyConsistency(first, size, oldRoot, root, proof),
                    $"first {first}, size {size}");
            }
        }
    }

    [Fact]
    public void ConsistencyProof_RejectsTamperingAndWrongRoots()
    {
        var leaves = LeafHashes();
        var root = MerkleLog.Root(leaves);
        var oldRoot = MerkleLog.Root(leaves.Take(2).ToList());
        var proof = MerkleLog.ConsistencyProof(2, leaves);

        Assert.False(MerkleLog.VerifyConsistency(2, leaves.Count, oldRoot, root,
            proof.Take(proof.Count - 1).ToList()));

        var tampered = proof.Select(p => (byte[])p.Clone()).ToList();
        tampered[0][0] ^= 0xff;
        Assert.False(MerkleLog.VerifyConsistency(2, leaves.Count, oldRoot, root, tampered));

        var wrongOld = (byte[])oldRoot.Clone();
        wrongOld[0] ^= 0xff;
        Assert.False(MerkleLog.VerifyConsistency(2, leaves.Count, wrongOld, root, proof));

        var wrongNew = (byte[])root.Clone();
        wrongNew[0] ^= 0xff;
        Assert.False(MerkleLog.VerifyConsistency(2, leaves.Count, oldRoot, wrongNew, proof));

        Assert.False(MerkleLog.VerifyConsistency(3, 2, root, oldRoot, proof));
        Assert.False(MerkleLog.VerifyConsistency(0, leaves.Count, [], root, proof));
    }

    [Fact]
    public void Apply_AcceptsASignedSnapshot_AndPinsFirstSeenOwners()
    {
        var (payload, snapshot) = Build(5, Record("nyx.custom", "kb1nyx"), Record("ari.custom", "kb1ari"));
        var state = new MirrorState();

        var result = RegistryMirror.Apply(state, payload, "good", new PayloadVerifier(payload), Now);

        Assert.Equal(SnapshotStatus.Accepted, result.Status);
        Assert.Equal(5, state.Version);
        Assert.Equal(snapshot.ComputeRootHex(), state.RootHash);
        Assert.Equal(PinVerdict.FirstSeen, result.PinChanges.Single(c => c.Name == "nyx.custom").Verdict);
        Assert.Equal(2, state.Pins.Count);
    }

    [Fact]
    public void Apply_IsIdempotentForTheSameSnapshot()
    {
        var (payload, _) = Build(5, Record("nyx.custom", "kb1nyx"));
        var state = new MirrorState();
        var verifier = new PayloadVerifier(payload);

        Assert.Equal(SnapshotStatus.Accepted,
            RegistryMirror.Apply(state, payload, "good", verifier, Now).Status);
        var again = RegistryMirror.Apply(state, payload, "good", verifier, Now);

        Assert.Equal(SnapshotStatus.AlreadyCurrent, again.Status);
        Assert.Empty(again.PinChanges);
        Assert.Equal(5, state.Version);
    }

    [Fact]
    public void Apply_RejectsRollbackAndKeepsState()
    {
        var (newer, _) = Build(5, Record("nyx.custom", "kb1nyx"));
        var state = new MirrorState();
        RegistryMirror.Apply(state, newer, "good", new PayloadVerifier(newer), Now);

        var (older, _) = Build(4, Record("nyx.custom", "kb1nyx"));
        var result = RegistryMirror.Apply(state, older, "good", new PayloadVerifier(older), Now);

        Assert.Equal(SnapshotStatus.Stale, result.Status);
        Assert.Equal(5, state.Version);
    }

    [Fact]
    public void Apply_FlagsTwoDifferentSnapshotsAtTheSameVersion()
    {
        var (first, _) = Build(5, Record("nyx.custom", "kb1nyx"));
        var state = new MirrorState();
        RegistryMirror.Apply(state, first, "good", new PayloadVerifier(first), Now);
        var acceptedRoot = state.RootHash;

        var (second, _) = Build(5, Record("ari.custom", "kb1ari"));
        var result = RegistryMirror.Apply(state, second, "good", new PayloadVerifier(second), Now);

        Assert.Equal(SnapshotStatus.Equivocation, result.Status);
        Assert.Equal(acceptedRoot, state.RootHash);
    }

    [Fact]
    public void Apply_FailsClosedWithoutAVerifier()
    {
        var (payload, _) = Build(5, Record("nyx.custom", "kb1nyx"));
        var state = new MirrorState();

        var result = RegistryMirror.Apply(state, payload, "good", verifier: null, Now);

        Assert.Equal(SnapshotStatus.NoVerifier, result.Status);
        Assert.False(result.Ok);
        Assert.Equal(0, state.Version);
        Assert.Empty(state.Pins);
    }

    [Fact]
    public void Apply_RejectsAWrongSignatureBeforeParsing()
    {
        var (payload, _) = Build(5, Record("nyx.custom", "kb1nyx"));
        var (other, _) = Build(5, Record("other.custom", "kb1other"));
        var state = new MirrorState();

        var result = RegistryMirror.Apply(state, payload, "good", new PayloadVerifier(other), Now);

        Assert.Equal(SnapshotStatus.BadSignature, result.Status);
        Assert.Empty(state.Pins);
    }

    [Fact]
    public void Apply_RejectsRecordsThatDoNotMatchTheClaimedRoot()
    {
        var (_, snapshot) = Build(5, Record("nyx.custom", "kb1nyx"));
        snapshot.RootHash = new string('0', 64);
        var payload = snapshot.CanonicalPayload();
        var state = new MirrorState();

        var result = RegistryMirror.Apply(state, payload, "good", new PayloadVerifier(payload), Now);

        Assert.Equal(SnapshotStatus.BadRoot, result.Status);
        Assert.Equal(0, state.Version);
        Assert.Empty(state.Pins);
    }

    [Fact]
    public void Apply_RejectsDuplicateNames()
    {
        var snapshot = new RegistrySnapshot
        {
            Version = 5,
            GeneratedAt = Now,
            RootHash = new string('0', 64),
            Records = [Record("nyx.custom", "kb1a"), Record("NYX.CUSTOM", "kb1b")]
        };
        var payload = snapshot.CanonicalPayload();
        var state = new MirrorState();

        var result = RegistryMirror.Apply(state, payload, "good", new PayloadVerifier(payload), Now);

        Assert.Equal(SnapshotStatus.Malformed, result.Status);
        Assert.Equal(0, state.Version);
    }

    [Fact]
    public void OwnerChange_IsHeldForExplicitApproval()
    {
        var state = new MirrorState();
        var (first, _) = Build(1, Record("nyx.custom", "kb1old"));
        RegistryMirror.Apply(state, first, "good", new PayloadVerifier(first), Now);

        var oldPin = state.Pins["nyx.custom"].OwnerFingerprint;
        var (second, _) = Build(2, Record("nyx.custom", "kb1new"));
        var result = RegistryMirror.Apply(state, second, "good", new PayloadVerifier(second), Now);

        var change = Assert.Single(result.PinChanges, c => c.Name == "nyx.custom");
        Assert.Equal(PinVerdict.Changed, change.Verdict);
        Assert.NotEqual(oldPin, change.PendingFingerprint);
        Assert.Equal(oldPin, state.Pins["nyx.custom"].OwnerFingerprint);
        Assert.NotNull(state.Pins["nyx.custom"].PendingFingerprint);

        Assert.True(RegistryMirror.ApproveChange(state, "nyx.custom", Now + 60));
        Assert.Equal(change.PendingFingerprint, state.Pins["nyx.custom"].OwnerFingerprint);
        Assert.Null(state.Pins["nyx.custom"].PendingFingerprint);
    }

    [Fact]
    public void StateStore_RoundTripsPinsAndHead()
    {
        using var temp = new TempDir();
        var paths = new DataPaths(temp.Path);
        var state = new MirrorState { Version = 7, RootHash = new string('a', 64), GeneratedAt = Now };
        state.Pins["nyx.custom"] = new RegistryPin
        {
            Name = "nyx.custom",
            OwnerFingerprint = "f00d",
            FirstSeenUtc = Now,
            PinnedUtc = Now,
            PendingFingerprint = "beef",
            ChangedUtc = Now
        };

        RegistryStateStore.Save(paths, state);
        var loaded = RegistryStateStore.Load(paths);

        Assert.Equal(7, loaded.Version);
        Assert.Equal(state.RootHash, loaded.RootHash);
        Assert.Equal("beef", loaded.Pins["NYX.CUSTOM"].PendingFingerprint);
    }

    private const long Now = 1_790_000_000;

    private static List<byte[]> LeafHashes() =>
        Words.Select(w => MerkleLog.LeafHash(Encoding.UTF8.GetBytes(w))).ToList();

    private static (byte[] Payload, RegistrySnapshot Snapshot) Build(int version,
        params RegistryRecord[] records)
    {
        var snapshot = new RegistrySnapshot
        {
            Version = version,
            GeneratedAt = Now,
            Records = [.. records]
        };
        snapshot.RootHash = snapshot.ComputeRootHex();
        return (snapshot.CanonicalPayload(), snapshot);
    }

    private static RegistryRecord Record(string name, string ownerId) => new()
    {
        Name = name,
        OwnerId = ownerId,
        Title = name,
        Headline = "test record",
        Tags = ["test"],
        UpdatedUtc = Now,
        ContentHash = new string('c', 64)
    };

    private sealed class PayloadVerifier(byte[] expected) : IRegistrySnapshotVerifier
    {
        private readonly string _hash = Hashing.Hex(System.Security.Cryptography.SHA256.HashData(expected));

        public bool Verify(ReadOnlySpan<byte> payload, string signature) =>
            signature == "good" &&
            Hashing.Hex(System.Security.Cryptography.SHA256.HashData(payload)) == _hash;
    }
}
