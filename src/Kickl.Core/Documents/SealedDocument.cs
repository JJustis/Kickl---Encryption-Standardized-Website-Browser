namespace Kickl.Core.Documents;

public sealed class SealedBlob
{
    public string Mime { get; set; } = "application/octet-stream";
    public string Label { get; set; } = "";
    public byte[] Nonce { get; set; } = [];
    public byte[] Ciphertext { get; set; } = [];
    public int PlainLength { get; set; }
    /// <summary>SHA-256 (hex) of the plaintext. Lets the registry hash cover content, not one profile's ciphertext.</summary>
    public string PlainSha256 { get; set; } = "";
    /// <summary>"public": any reader's session opens it. "protected": needs the site's access key.</summary>
    public string Access { get; set; } = "public";
}

/// <summary>The protected-blob key, wrapped under a key derived (Argon2id) from the access key.</summary>
public sealed class AccessKeyWrap
{
    public string Kdf { get; set; } = "";
    public byte[] Salt { get; set; } = [];
    public byte[] Nonce { get; set; } = [];
    public byte[] Ciphertext { get; set; } = [];
}

public sealed class WrappedKey
{
    public byte[] Nonce { get; set; } = [];
    public byte[] Ciphertext { get; set; } = [];
}

/// <summary>
/// A KML document plus its sealed blobs. The markup is public structure; every blob is
/// AES-256-GCM ciphertext under a document key that is itself wrapped by the profile master key.
/// In production the wrap is HPKE to each reader; the slice models the local reader.
/// </summary>
public sealed class SealedDocument
{
    public int V { get; set; } = 3;
    public string Site { get; set; } = "";
    public string Path { get; set; } = "/";
    public string Title { get; set; } = "";
    public int Version { get; set; } = 1;
    public long Created { get; set; }
    public string Markup { get; set; } = "";
    public Dictionary<string, SealedBlob> Blobs { get; set; } = [];
    /// <summary>The author's own wrap of the document key (legacy readers and key recovery).</summary>
    public WrappedKey DocKey { get; set; } = new();
    /// <summary>
    /// The public-content key, wrapped under a key derived from the site name and <see cref="PublicSalt"/>.
    /// Public pages are meant to open for any Kickl reader through their own session, so anyone who
    /// knows the name can derive the wrapping key; what it stops is reading the page off a disk,
    /// backup or file listing. Empty on older packages until their author's profile opens them once.
    /// </summary>
    public WrappedKey PublicWrap { get; set; } = new();
    public byte[] PublicSalt { get; set; } = [];
    /// <summary>The page markup, AES-256-GCM under the public-content key. When present, <see cref="Markup"/> is empty on disk.</summary>
    public WrappedKey? SealedMarkup { get; set; }
    /// <summary>Present only when the site has protected blobs.</summary>
    public AccessKeyWrap? Protected { get; set; }
    /// <summary>Set on publish when an access key was generated. Never written to disk.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? IssuedAccessKey { get; set; }
    /// <summary>V2: SHA-256 over site, markup and each blob's plaintext hash. Identical in every profile that seals the same content. V1: over ciphertext.</summary>
    public string Integrity { get; set; } = "";

    /// <summary>
    /// The CA's seal for this name, published with the site so every visitor can verify it. It holds no secret and is
    /// not covered by <see cref="Integrity"/>, so sealing a site never changes its registry content hash.
    /// </summary>
    public Trust.CaSeal? Seal { get; set; }
}
