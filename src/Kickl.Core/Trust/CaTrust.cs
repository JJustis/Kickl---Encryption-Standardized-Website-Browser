using System.Text;
using Kickl.Core.Registry;
using Kickl.Core.Util;

namespace Kickl.Core.Trust;

public sealed record SealCheck(bool Verified, string Reason)
{
    public static SealCheck Ok => new(true, "verified by the Kickl CA");
    public static SealCheck No(string reason) => new(false, reason);
}

/// <summary>
/// What a browser does with a seal. The only thing it trusts is the root public key it pinned; everything else is
/// proven: root signed the issuer, the issuer signed the seal and the log head, and the log contains the seal.
/// </summary>
public static class CaTrust
{
    private static HybridVerifier Verifier(RegistrySigningKey key) => new(key);

    /// <summary>The issuer certificate is signed by the root, inside its dates, and not revoked.</summary>
    public static SealCheck VerifyIssuer(RegistrySigningKey root, IssuerCertificate issuer, DateTimeOffset now,
        RevocationList? revocations)
    {
        try
        {
            using var rootVerifier = Verifier(root);
            if (!rootVerifier.Verify(TrustDomains.IssuerCert, issuer.Payload(), issuer.Sig))
                return SealCheck.No("the issuer certificate is not signed by the pinned CA root");
            issuer.Key.Validate();
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            return SealCheck.No("the issuer certificate is malformed");
        }

        var t = now.ToUnixTimeSeconds();
        if (t < issuer.NotBefore) return SealCheck.No("the issuer certificate is not valid yet");
        if (t > issuer.NotAfter) return SealCheck.No("the issuer certificate has expired");
        if (revocations is not null && revocations.Revokes(issuer.Serial))
            return SealCheck.No("the CA revoked this issuing key");
        return SealCheck.Ok;
    }

    /// <summary>
    /// Is this revocation list authentic? A root-signed list may revoke issuers and seals. An issuer-signed list is
    /// only believed for seals, and only if that issuer's own certificate verifies.
    /// </summary>
    public static bool VerifyRevocations(RegistrySigningKey root, RevocationList list, IssuerCertificate? issuer,
        DateTimeOffset now)
    {
        try
        {
            if (list.SignedBy == "root")
            {
                using var v = Verifier(root);
                return v.Verify(TrustDomains.Revocations, list.Payload(), list.Sig);
            }

            if (list.SignedBy != "issuer" || issuer is null || list.RevokedIssuerSerials.Count > 0) return false;
            if (!VerifyIssuer(root, issuer, now, null).Verified) return false;
            if (!string.Equals(list.SignerFingerprint, issuer.Key.Fingerprint, StringComparison.Ordinal)) return false;
            using var iv = Verifier(issuer.Key);
            return iv.Verify(TrustDomains.Revocations, list.Payload(), list.Sig);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            return false;
        }
    }

    public static SealCheck VerifySeal(RegistrySigningKey root, CaSeal seal, string site, DateTimeOffset now,
        RevocationList? revocations = null, int? expectedSeq = null)
    {
        if (!string.Equals(seal.Site, site, StringComparison.OrdinalIgnoreCase))
            return SealCheck.No($"this seal is for jttp://{seal.Site}/, not jttp://{site}/");
        if (expectedSeq is { } seq && seal.Seq != seq)
            return SealCheck.No("this seal is for an earlier claim of the name");

        var issuer = VerifyIssuer(root, seal.Issuer, now, revocations);
        if (!issuer.Verified) return issuer;

        if (seal.ReceiptExpires <= now.ToUnixTimeSeconds()) return SealCheck.No("the ownership has expired");
        if (revocations is not null && revocations.Revokes(seal.Site, seal.Seq))
            return SealCheck.No("the CA revoked this ownership");

        try
        {
            using var iv = Verifier(seal.Issuer.Key);
            if (!iv.Verify(TrustDomains.Seal, seal.Payload(), seal.Sig))
                return SealCheck.No("the CA's signature on this seal does not verify");
            if (!iv.Verify(TrustDomains.LogHead, seal.HeadPayload(), seal.HeadSig))
                return SealCheck.No("the CA's signature on the transparency log head does not verify");

            var leaf = CaSeal.LeafHash(seal.LeafText());
            var proof = seal.Proof.Select(Convert.FromHexString).ToList();
            if (!MerkleLog.VerifyInclusion(leaf, seal.LogIndex, seal.TreeSize, proof, Convert.FromHexString(seal.TreeRoot)))
                return SealCheck.No("this seal is not in the CA's transparency log");
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            return SealCheck.No("the seal is malformed");
        }

        return SealCheck.Ok;
    }
}
