using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kickl.Core.Ownership;
using Kickl.Core.Registry;
using Kickl.Core.Util;

namespace Kickl.Core.Trust;

/// <summary>The CA refused a receipt. <see cref="Code"/> is stable so callers can react to it.</summary>
public sealed class CaRejectedException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class LedgerEntry
{
    public string Site { get; set; } = "";
    public int Seq { get; set; }
    public string ReceiptHash { get; set; } = "";
    public string TokenCommitment { get; set; } = "";
    public string LeafText { get; set; } = "";
    public CaSeal Seal { get; set; } = new();
}

public interface ICaStore
{
    IReadOnlyList<LedgerEntry> Entries { get; }
    void Append(LedgerEntry entry);
    void Alert(string kind, string detail);
}

public sealed class MemoryCaStore : ICaStore
{
    private readonly List<LedgerEntry> _entries = [];
    public List<(string Kind, string Detail)> Alerts { get; } = [];
    public IReadOnlyList<LedgerEntry> Entries => _entries;
    public void Append(LedgerEntry entry) => _entries.Add(entry);
    public void Alert(string kind, string detail) => Alerts.Add((kind, detail));
}

/// <summary>
/// An append-only ledger on disk: one JSON object per line, never rewritten, plus an alert log.
/// A lock file serialises concurrent countersign requests so two never claim the same log position.
/// </summary>
public sealed class FileCaStore : ICaStore, IDisposable
{
    private readonly string _ledger;
    private readonly string _alerts;
    private readonly FileStream _lock;
    private readonly List<LedgerEntry> _entries = [];

    public FileCaStore(string dir)
    {
        Directory.CreateDirectory(dir);
        _ledger = Path.Combine(dir, "ledger.jsonl");
        _alerts = Path.Combine(dir, "alerts.log");
        _lock = new FileStream(Path.Combine(dir, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (File.Exists(_ledger))
            foreach (var line in File.ReadLines(_ledger).Where(l => l.Length > 0))
                _entries.Add(JsonSerializer.Deserialize<LedgerEntry>(line, TrustJson.Options)
                             ?? throw new InvalidDataException("The CA ledger is corrupt."));
    }

    public IReadOnlyList<LedgerEntry> Entries => _entries;

    public void Append(LedgerEntry entry)
    {
        File.AppendAllText(_ledger, JsonSerializer.Serialize(entry, new JsonSerializerOptions(TrustJson.Options) { WriteIndented = false }) + "\n");
        _entries.Add(entry);
    }

    public void Alert(string kind, string detail) =>
        File.AppendAllText(_alerts, $"{DateTimeOffset.UtcNow:O}\t{kind}\t{detail}\n");

    public void Dispose() => _lock.Dispose();
}

/// <summary>
/// The Certificate Authority. It is the party that watches ownership certificates: it checks each shop receipt,
/// refuses anything that conflicts with what it has already sealed, writes every seal to an append-only
/// transparency log, and returns a countersigned seal the owner publishes with the site.
/// </summary>
public sealed class CaService
{
    private readonly HybridSigner _issuerKey;
    private readonly IssuerCertificate _issuer;
    private readonly ICaStore _store;
    private readonly IReadOnlyList<string> _shopKeys;
    private readonly RegistrySigningKey _root;

    public CaService(HybridSigner issuerKey, IssuerCertificate issuer, RegistrySigningKey root,
        IEnumerable<string> trustedShopKeysSpki, ICaStore store)
    {
        _issuerKey = issuerKey;
        _issuer = issuer;
        _root = root;
        _store = store;
        _shopKeys = trustedShopKeysSpki.ToList();
        if (_shopKeys.Count == 0) throw new ArgumentException("The CA needs at least one shop key to trust.", nameof(trustedShopKeysSpki));
        if (!string.Equals(issuer.Key.Fingerprint, issuerKey.Public.Fingerprint, StringComparison.Ordinal))
            throw new ArgumentException("The issuer certificate is for a different key.", nameof(issuer));
    }

    public CaSeal Countersign(OwnershipCert receipt, DateTimeOffset now, RevocationList? revocations = null)
    {
        var check = CaTrust.VerifyIssuer(_root, _issuer, now, revocations);
        if (!check.Verified) throw new CaRejectedException("issuer-invalid", "The CA's own issuing key is not valid: " + check.Reason);

        if (!_shopKeys.Any(k => SiteClaims.Verify(receipt, k)))
            throw new CaRejectedException("bad-receipt", "The receipt is not signed by a shop this CA trusts.");
        if (receipt.Expires <= now.ToUnixTimeSeconds())
            throw new CaRejectedException("expired", "The receipt has expired.");
        if (receipt.Seq < 1 || string.IsNullOrWhiteSpace(receipt.Site))
            throw new CaRejectedException("bad-receipt", "The receipt does not name a site and claim number.");
        if (revocations is not null && revocations.Revokes(receipt.Site, receipt.Seq))
            throw new CaRejectedException("revoked", $"The claim on jttp://{receipt.Site}/ was revoked.");

        var receiptHash = TrustJson.Sha256Hex(receipt.Payload());
        var commitment = TrustJson.Sha256Hex(receipt.Token);

        var existing = _store.Entries.Where(e => e.Site == receipt.Site).ToList();
        if (existing.Any(e => e.Seq > receipt.Seq))   // judged against the newest claim, whatever order they were logged in
            throw new CaRejectedException("stale", $"jttp://{receipt.Site}/ has been claimed again since this receipt.");

        foreach (var e in existing)
        {
            if (e.Seq == receipt.Seq)
            {
                if (e.ReceiptHash == receiptHash) return e.Seal;   // resubmitting the same receipt returns the same seal
                _store.Alert("conflict", $"{receipt.Site} seq {receipt.Seq}: a second, different receipt (commitment {commitment[..16]}) was presented");
                throw new CaRejectedException("conflict",
                    $"jttp://{receipt.Site}/ already has a different, sealed claim with this number. The CA has logged the attempt.");
            }
        }

        var seal = new CaSeal
        {
            Issuer = _issuer,
            Site = receipt.Site,
            Seq = receipt.Seq,
            ReceiptHash = receiptHash,
            TokenCommitment = commitment,
            ReceiptExpires = receipt.Expires,
            IssuedAt = now.ToUnixTimeSeconds()
        };

        var leafText = seal.LeafText();
        var leaves = _store.Entries.Select(e => CaSeal.LeafHash(e.LeafText)).ToList();
        leaves.Add(CaSeal.LeafHash(leafText));
        var index = leaves.Count - 1;
        seal.LogIndex = index;
        seal.TreeSize = leaves.Count;
        seal.TreeRoot = TrustJson.Hex(MerkleLog.Root(leaves));
        seal.Proof = MerkleLog.InclusionProof(leaves, index).Select(TrustJson.Hex).ToList();
        seal.HeadTime = seal.IssuedAt;
        seal.HeadSig = _issuerKey.Sign(TrustDomains.LogHead, seal.HeadPayload());
        seal.Sig = _issuerKey.Sign(TrustDomains.Seal, seal.Payload());

        _store.Append(new LedgerEntry
        {
            Site = seal.Site, Seq = seal.Seq, ReceiptHash = receiptHash, TokenCommitment = commitment,
            LeafText = leafText, Seal = seal
        });
        return seal;
    }

    /// <summary>The current signed head of the log, for monitors and for checking that it only ever grows.</summary>
    public (int Size, string Root, long Time, string Sig) Head(DateTimeOffset now)
    {
        var leaves = _store.Entries.Select(e => CaSeal.LeafHash(e.LeafText)).ToList();
        var root = TrustJson.Hex(MerkleLog.Root(leaves));
        var time = now.ToUnixTimeSeconds();
        return (leaves.Count, root, time,
            _issuerKey.Sign(TrustDomains.LogHead, CaSeal.HeadPayloadFor(leaves.Count, root, time)));
    }

    /// <summary>Re-derive every seal's inclusion in the current log. Returns problems found; empty means the log is sound.</summary>
    public IReadOnlyList<string> Audit()
    {
        var problems = new List<string>();
        var leaves = _store.Entries.Select(e => CaSeal.LeafHash(e.LeafText)).ToList();
        for (var i = 0; i < _store.Entries.Count; i++)
        {
            var e = _store.Entries[i];
            if (e.Seal.LogIndex != i) problems.Add($"entry {i}: seal says position {e.Seal.LogIndex}");
            if (CaSeal.LeafHash(e.Seal.LeafText()).AsSpan().SequenceEqual(leaves[i]) is false)
                problems.Add($"entry {i}: leaf does not match its seal");
            var root = MerkleLog.Root(leaves.Take(e.Seal.TreeSize).ToList());
            if (TrustJson.Hex(root) != e.Seal.TreeRoot) problems.Add($"entry {i}: the root it was sealed under is not a prefix of today's log");
        }
        return problems;
    }

    /// <summary>Sign a new revocation list. <paramref name="previous"/> numbers it so clients can refuse rollbacks.</summary>
    public RevocationList Revoke(RevocationList? previous, IEnumerable<RevokedSeal> seals, DateTimeOffset now)
    {
        var list = new RevocationList
        {
            Number = (previous?.Number ?? 0) + 1,
            GeneratedAt = now.ToUnixTimeSeconds(),
            RevokedIssuerSerials = previous?.RevokedIssuerSerials.ToList() ?? [],
            RevokedSeals = previous?.RevokedSeals.ToList() ?? [],
            SignedBy = "issuer",
            SignerFingerprint = _issuer.Key.Fingerprint
        };
        // An issuer-signed list cannot carry issuer revocations; those only come from the root.
        list.RevokedIssuerSerials.Clear();
        foreach (var s in seals)
            if (!list.Revokes(s.Site, s.Seq)) list.RevokedSeals.Add(s);
        list.Sig = _issuerKey.Sign(TrustDomains.Revocations, list.Payload());
        return list;
    }

    /// <summary>The root signs a list that may also revoke issuing keys. Used offline, rarely.</summary>
    public static RevocationList RevokeAsRoot(HybridSigner root, RevocationList? previous,
        IEnumerable<long> issuerSerials, IEnumerable<RevokedSeal> seals, DateTimeOffset now)
    {
        var list = new RevocationList
        {
            Number = (previous?.Number ?? 0) + 1,
            GeneratedAt = now.ToUnixTimeSeconds(),
            RevokedIssuerSerials = (previous?.RevokedIssuerSerials ?? []).Union(issuerSerials).Distinct().ToList(),
            RevokedSeals = previous?.RevokedSeals.ToList() ?? [],
            SignedBy = "root",
            SignerFingerprint = root.Public.Fingerprint
        };
        foreach (var s in seals)
            if (!list.Revokes(s.Site, s.Seq)) list.RevokedSeals.Add(s);
        list.Sig = root.Sign(TrustDomains.Revocations, list.Payload());
        return list;
    }

    /// <summary>The root certifies an issuing key. Run offline.</summary>
    public static IssuerCertificate IssueIssuer(HybridSigner root, RegistrySigningKey issuerPublic, string name,
        long serial, DateTimeOffset notBefore, TimeSpan lifetime)
    {
        var cert = new IssuerCertificate
        {
            Serial = serial,
            Name = name,
            Key = issuerPublic,
            NotBefore = notBefore.ToUnixTimeSeconds(),
            NotAfter = notBefore.Add(lifetime).ToUnixTimeSeconds()
        };
        cert.Sig = root.Sign(TrustDomains.IssuerCert, cert.Payload());
        return cert;
    }
}
