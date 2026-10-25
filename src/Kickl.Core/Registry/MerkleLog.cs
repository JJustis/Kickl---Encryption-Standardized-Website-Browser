using System.Security.Cryptography;

namespace Kickl.Core.Registry;

/// <summary>
/// RFC 6962 Merkle tree over registry record hashes: domain-separated leaves,
/// inclusion proofs, and a verifier that never trusts the sender's root.
/// </summary>
public static class MerkleLog
{
    private const int HashSize = 32;
    private static readonly byte[] EmptyRoot = SHA256.HashData([]);

    public static byte[] LeafHash(ReadOnlySpan<byte> leaf)
    {
        var buffer = new byte[1 + leaf.Length];
        leaf.CopyTo(buffer.AsSpan(1));
        return SHA256.HashData(buffer);
    }

    public static byte[] Root(IReadOnlyList<byte[]> leafHashes)
    {
        ArgumentNullException.ThrowIfNull(leafHashes);
        ValidateLeaves(leafHashes);
        return RootOf(leafHashes, 0, leafHashes.Count);
    }

    public static IReadOnlyList<byte[]> InclusionProof(IReadOnlyList<byte[]> leafHashes, int index)
    {
        ArgumentNullException.ThrowIfNull(leafHashes);
        ValidateLeaves(leafHashes);
        if (index < 0 || index >= leafHashes.Count)
            throw new ArgumentOutOfRangeException(nameof(index), "Leaf index is outside the tree.");

        var proof = new List<byte[]>();
        Proof(index, leafHashes, 0, leafHashes.Count, proof);
        return proof;
    }

    /// <summary>
    /// RFC 6962 consistency proof: the nodes needed to show that the first
    /// <paramref name="firstSize"/> leaves are unchanged in the larger tree.
    /// </summary>
    public static IReadOnlyList<byte[]> ConsistencyProof(int firstSize,
        IReadOnlyList<byte[]> leafHashes)
    {
        ArgumentNullException.ThrowIfNull(leafHashes);
        ValidateLeaves(leafHashes);
        if (firstSize <= 0 || firstSize >= leafHashes.Count)
            throw new ArgumentOutOfRangeException(nameof(firstSize),
                "A consistency proof needs 0 < firstSize < tree size.");

        var proof = new List<byte[]>();
        SubProof(firstSize, leafHashes, 0, leafHashes.Count, knownRoot: true, proof);
        return proof;
    }

    /// <summary>
    /// Verifies an append-only consistency proof without trusting the sender's tree.
    /// Ported from the Certificate Transparency reference verifier; both roots must match.
    /// </summary>
    public static bool VerifyConsistency(int firstSize, int secondSize, byte[] firstRoot,
        byte[] secondRoot, IReadOnlyList<byte[]> proof)
    {
        ArgumentNullException.ThrowIfNull(firstRoot);
        ArgumentNullException.ThrowIfNull(secondRoot);
        ArgumentNullException.ThrowIfNull(proof);

        if (firstRoot.Length != HashSize || secondRoot.Length != HashSize) return false;
        if (firstSize < 0 || secondSize < 0 || firstSize > secondSize) return false;

        if (firstSize == secondSize)
            return proof.Count == 0 && CryptographicOperations.FixedTimeEquals(firstRoot, secondRoot);
        if (firstSize == 0)
            return proof.Count == 0;

        var node = firstSize - 1;
        var lastNode = secondSize - 1;
        while ((node & 1) != 0)
        {
            node >>= 1;
            lastNode >>= 1;
        }

        var index = 0;
        byte[] oldHash;
        byte[] newHash;
        if (node != 0)
        {
            if (proof.Count == 0) return false;
            oldHash = newHash = proof[index++];
            if (oldHash.Length != HashSize) return false;
        }
        else
        {
            oldHash = newHash = firstRoot;
        }

        while (node != 0)
        {
            if ((node & 1) != 0)
            {
                if (index >= proof.Count) return false;
                var sibling = proof[index++];
                if (sibling.Length != HashSize) return false;
                oldHash = HashNode(sibling, oldHash);
                newHash = HashNode(sibling, newHash);
            }
            else if (node < lastNode)
            {
                if (index >= proof.Count) return false;
                var sibling = proof[index++];
                if (sibling.Length != HashSize) return false;
                newHash = HashNode(newHash, sibling);
            }

            node >>= 1;
            lastNode >>= 1;
        }

        while (lastNode != 0)
        {
            if (index >= proof.Count) return false;
            var sibling = proof[index++];
            if (sibling.Length != HashSize) return false;
            newHash = HashNode(newHash, sibling);
            lastNode >>= 1;
        }

        return CryptographicOperations.FixedTimeEquals(newHash, secondRoot)
               && CryptographicOperations.FixedTimeEquals(oldHash, firstRoot);
    }

    /// <summary>
    /// RFC 6962 proof verification. The caller supplies the leaf it is checking,
    /// the claimed position and tree size, and the trusted root.
    /// </summary>
    public static bool VerifyInclusion(byte[] leafHash, int index, int treeSize,
        IReadOnlyList<byte[]> proof, byte[] expectedRoot)
    {
        ArgumentNullException.ThrowIfNull(leafHash);
        ArgumentNullException.ThrowIfNull(proof);
        ArgumentNullException.ThrowIfNull(expectedRoot);

        if (leafHash.Length != HashSize || expectedRoot.Length != HashSize) return false;
        if (treeSize <= 0 || index < 0 || index >= treeSize) return false;

        var fn = leafHash;
        var sn = treeSize - 1;
        var r = index;

        foreach (var sibling in proof)
        {
            if (sibling.Length != HashSize || sn == 0) return false;

            if ((r & 1) != 0 || r == sn)
            {
                fn = HashNode(sibling, fn);
                if ((r & 1) == 0)
                {
                    while ((r & 1) == 0 && r != 0)
                    {
                        r >>= 1;
                        sn >>= 1;
                    }
                }
            }
            else
            {
                fn = HashNode(fn, sibling);
            }

            r >>= 1;
            sn >>= 1;
        }

        return sn == 0 && CryptographicOperations.FixedTimeEquals(fn, expectedRoot);
    }

    private static byte[] RootOf(IReadOnlyList<byte[]> leaves, int offset, int count)
    {
        if (count == 0) return (byte[])EmptyRoot.Clone();
        if (count == 1) return leaves[offset];

        var k = LargestPowerOfTwoBelow(count);
        return HashNode(
            RootOf(leaves, offset, k),
            RootOf(leaves, offset + k, count - k));
    }

    private static void SubProof(int m, IReadOnlyList<byte[]> leaves, int offset, int count,
        bool knownRoot, List<byte[]> proof)
    {
        if (m == count)
        {
            if (!knownRoot) proof.Add(RootOf(leaves, offset, count));
            return;
        }

        var k = LargestPowerOfTwoBelow(count);
        if (m <= k)
        {
            SubProof(m, leaves, offset, k, knownRoot, proof);
            proof.Add(RootOf(leaves, offset + k, count - k));
        }
        else
        {
            SubProof(m - k, leaves, offset + k, count - k, knownRoot: false, proof);
            proof.Add(RootOf(leaves, offset, k));
        }
    }

    private static void Proof(int index, IReadOnlyList<byte[]> leaves, int offset, int count,
        List<byte[]> proof)
    {
        if (count == 1) return;

        var k = LargestPowerOfTwoBelow(count);
        if (index < k)
        {
            Proof(index, leaves, offset, k, proof);
            proof.Add(RootOf(leaves, offset + k, count - k));
        }
        else
        {
            Proof(index - k, leaves, offset + k, count - k, proof);
            proof.Add(RootOf(leaves, offset, k));
        }
    }

    private static byte[] HashNode(byte[] left, byte[] right)
    {
        var buffer = new byte[1 + left.Length + right.Length];
        buffer[0] = 0x01;
        left.CopyTo(buffer, 1);
        right.CopyTo(buffer, 1 + left.Length);
        return SHA256.HashData(buffer);
    }

    private static int LargestPowerOfTwoBelow(int count)
    {
        var k = 1;
        while (k * 2 < count) k *= 2;
        return k;
    }

    private static void ValidateLeaves(IReadOnlyList<byte[]> leaves)
    {
        foreach (var leaf in leaves)
            if (leaf.Length != HashSize)
                throw new ArgumentException($"A Merkle leaf hash must be {HashSize} bytes.", nameof(leaves));
    }
}
