namespace Kickl.Core.Documents;

public sealed class SealedBlob
{
    public string Mime { get; set; } = "application/octet-stream";
    public string Label { get; set; } = "";
    public byte[] Nonce { get; set; } = [];
    public byte[] Ciphertext { get; set; } = [];
    public int PlainLength { get; set; }
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
    public int V { get; set; } = 1;
    public string Site { get; set; } = "";
    public string Path { get; set; } = "/";
    public string Title { get; set; } = "";
    public int Version { get; set; } = 1;
    public long Created { get; set; }
    public string Markup { get; set; } = "";
    public Dictionary<string, SealedBlob> Blobs { get; set; } = [];
    public WrappedKey DocKey { get; set; } = new();
    public string Integrity { get; set; } = "";
}
