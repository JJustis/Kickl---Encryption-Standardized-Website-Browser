using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Kickl.Core.Identity;

/// <summary>
/// A short human-comparable fingerprint for a contact pair. Both sides compute the same
/// value from the two addresses and KEM public keys, sorted so order does not matter.
/// It is the only defence against a registry that lies about a public key: users compare
/// it out of band.
/// </summary>
public static class SafetyNumber
{
    private const string Domain = "kickl/safety/v1";
    private const int Digits = 60;
    private const int Groups = 12;
    private const int GroupSize = 5;

    public static string Compute(string addressA, string addressB, byte[] kemA, byte[] kemB)
    {
        ArgumentNullException.ThrowIfNull(kemA);
        ArgumentNullException.ThrowIfNull(kemB);

        if (!IdentityAddress.TryDecode(addressA, out var digestA, out var error))
            throw new ArgumentException(error, nameof(addressA));
        if (!IdentityAddress.TryDecode(addressB, out var digestB, out error))
            throw new ArgumentException(error, nameof(addressB));
        if (kemA.Length == 0 || kemB.Length == 0)
            throw new ArgumentException("Both KEM public keys are required.");

        var leftKey = CompareKeys(kemA, kemB) <= 0 ? kemA : kemB;
        var rightKey = ReferenceEquals(leftKey, kemA) ? kemB : kemA;
        var leftDigest = Compare(digestA, digestB) <= 0 ? digestA : digestB;
        var rightDigest = ReferenceEquals(leftDigest, digestA) ? digestB : digestA;

        var domain = Encoding.UTF8.GetBytes(Domain);
        var material = new byte[domain.Length + 40 + leftKey.Length + rightKey.Length];
        var offset = 0;
        domain.CopyTo(material, offset); offset += domain.Length;
        leftDigest.CopyTo(material, offset); offset += leftDigest.Length;
        rightDigest.CopyTo(material, offset); offset += rightDigest.Length;
        leftKey.CopyTo(material, offset); offset += leftKey.Length;
        rightKey.CopyTo(material, offset);

        byte[] hash;
        try
        {
            hash = SHA256.HashData(material);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(material);
        }

        // 25 bytes (~200 bits) reduced into exactly 60 decimal digits.
        var value = new BigInteger(hash.AsSpan(0, 25), isUnsigned: true, isBigEndian: true)
                    % BigInteger.Pow(10, Digits);
        var digits = value.ToString().PadLeft(Digits, '0');

        var builder = new StringBuilder(Digits + Groups - 1);
        for (var i = 0; i < Groups; i++)
        {
            if (i > 0) builder.Append(' ');
            builder.Append(digits, i * GroupSize, GroupSize);
        }
        return builder.ToString();
    }

    private static int CompareKeys(byte[] a, byte[] b) => Compare(a, b);

    private static int Compare(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var shared = Math.Min(a.Length, b.Length);
        for (var i = 0; i < shared; i++)
        {
            var diff = a[i].CompareTo(b[i]);
            if (diff != 0) return diff;
        }
        return a.Length.CompareTo(b.Length);
    }
}
