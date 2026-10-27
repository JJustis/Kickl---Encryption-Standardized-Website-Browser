using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kickl.Core.Registry;

namespace Kickl.Core.Trust;

/// <summary>
/// An issuing key, certified by the offline root. Issuer certificates are short-lived (the tool defaults to 90 days),
/// so a leaked online key is bounded in time even before it is revoked.
/// </summary>
public sealed class IssuerCertificate
{
    public int V { get; set; } = 1;
    public long Serial { get; set; }
    public string Name { get; set; } = "";
    public RegistrySigningKey Key { get; set; } = new();
    public long NotBefore { get; set; }
    public long NotAfter { get; set; }
    /// <summary>Root's hybrid signature over <see cref="Payload"/>.</summary>
    public string Sig { get; set; } = "";

    public string Payload() =>
        string.Join('|', "kickl/v1/ca-issuer", Serial, Name, Key.Ed25519PublicKey, Key.MlDsa65PublicKey, NotBefore, NotAfter);
}

/// <summary>
/// The CA's countersignature on a shop receipt, with proof that it was written to the CA's append-only log.
/// It contains nothing secret: no token, only the token's commitment, so it can be published with the site.
/// </summary>
public sealed class CaSeal
{
    public int V { get; set; } = 1;
    public IssuerCertificate Issuer { get; set; } = new();
    public string Site { get; set; } = "";
    public int Seq { get; set; }
    /// <summary>SHA-256 (hex) of the receipt's signed text.</summary>
    public string ReceiptHash { get; set; } = "";
    /// <summary>SHA-256 (hex) of the receipt's secret token.</summary>
    public string TokenCommitment { get; set; } = "";
    public long ReceiptExpires { get; set; }
    public long IssuedAt { get; set; }

    public int LogIndex { get; set; }
    public int TreeSize { get; set; }
    public string TreeRoot { get; set; } = "";
    public List<string> Proof { get; set; } = [];
    public long HeadTime { get; set; }
    public string HeadSig { get; set; } = "";

    /// <summary>Issuer's hybrid signature over <see cref="Payload"/>.</summary>
    public string Sig { get; set; } = "";

    public string IssuerFingerprint => Issuer.Key.Fingerprint;

    /// <summary>What the transparency log stores for this seal (before it is hashed as a leaf).</summary>
    public string LeafText() =>
        string.Join('|', "kickl/v1/ca-leaf", Site, Seq, ReceiptHash, TokenCommitment, ReceiptExpires, IssuedAt, IssuerFingerprint);

    public string Payload() =>
        string.Join('|', "kickl/v1/ca-seal", Site, Seq, ReceiptHash, TokenCommitment, ReceiptExpires, IssuedAt,
            IssuerFingerprint, LogIndex, TreeSize, TreeRoot, string.Join(',', Proof));

    public string HeadPayload() => HeadPayloadFor(TreeSize, TreeRoot, HeadTime);

    public static string HeadPayloadFor(int size, string root, long time) =>
        string.Join('|', "kickl/v1/ca-log-head", size, root, time);

    public static byte[] LeafHash(string leafText) => MerkleLog.LeafHash(Encoding.UTF8.GetBytes(leafText));
}

public sealed class RevokedSeal
{
    public string Site { get; set; } = "";
    public int Seq { get; set; }
    public string Reason { get; set; } = "";
}

/// <summary>
/// A monotonic, signed revocation list. The root may revoke issuers and seals; an issuer may only revoke seals,
/// so a stolen issuing key can never un-revoke itself or revoke a sibling issuer.
/// </summary>
public sealed class RevocationList
{
    public int V { get; set; } = 1;
    public long Number { get; set; }
    public long GeneratedAt { get; set; }
    public List<long> RevokedIssuerSerials { get; set; } = [];
    public List<RevokedSeal> RevokedSeals { get; set; } = [];
    /// <summary><c>root</c> or <c>issuer</c>.</summary>
    public string SignedBy { get; set; } = "root";
    public string SignerFingerprint { get; set; } = "";
    public string Sig { get; set; } = "";

    public string Payload() => string.Join('|', "kickl/v1/ca-revocations", Number, GeneratedAt, SignedBy, SignerFingerprint,
        string.Join(',', RevokedIssuerSerials.OrderBy(s => s)),
        string.Join(',', RevokedSeals.OrderBy(r => r.Site, StringComparer.Ordinal).ThenBy(r => r.Seq).Select(r => $"{r.Site}:{r.Seq}")));

    public bool Revokes(long issuerSerial) => RevokedIssuerSerials.Contains(issuerSerial);
    public bool Revokes(string site, int seq) => RevokedSeals.Any(r => r.Site == site && r.Seq == seq);
}

internal static class TrustJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
    public static string Sha256Hex(string text) => Hex(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
