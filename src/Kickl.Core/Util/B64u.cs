using System.Security.Cryptography;
using System.Text;

namespace Kickl.Core.Util;

/// <summary>Base64url without padding (RFC 4648 §5).</summary>
public static class B64u
{
    public static string Encode(ReadOnlySpan<byte> data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Decode(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        s = (s.Length % 4) switch
        {
            2 => s + "==",
            3 => s + "=",
            0 => s,
            _ => throw new FormatException("Invalid base64url length.")
        };
        return Convert.FromBase64String(s);
    }
}

public static class Utf8
{
    public static byte[] B(string value) => Encoding.UTF8.GetBytes(value);
}

public sealed class DataPaths
{
    public DataPaths(string root, string profileId = "local")
    {
        Root = Path.GetFullPath(root);
        if (string.IsNullOrWhiteSpace(profileId) || profileId.Length > 32 ||
            profileId.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-')))
            throw new ArgumentException("Profile id must contain only ASCII letters, digits, and hyphens.", nameof(profileId));
        ProfileId = profileId.ToLowerInvariant();
    }

    public string Root { get; }
    public string ProfileId { get; }
    public string ProfileRoot => Path.Combine(Root, "profiles", ProfileId);
    public string Keystore => Path.Combine(ProfileRoot, "keys", "keystore.json");
    public string Registry => Path.Combine(Root, "shared", "registry.json");
    public string RegistryState => Path.Combine(Root, "shared", "registry-state.json");
    public string SiteDoc(string host) => Path.Combine(ProfileRoot, "sites", SafeNames.RequireHost(host), "doc.json");
    public string RelayKey => Path.Combine(ProfileRoot, "keys", "relay.json");
    public string Drafts => Path.Combine(ProfileRoot, "drafts");
    public string NicklesPublishers => Path.Combine(ProfileRoot, "ads", "publishers.kpf");
    public string MailRoot => Path.Combine(ProfileRoot, "mail");
    public string MailDatabase => Path.Combine(MailRoot, "mail.db");
    public string StegoRoot => Path.Combine(ProfileRoot, "vault", "stego");
    public string CacheIndex => Path.Combine(ProfileRoot, "vault", "cache.index");

    public void Ensure()
    {
        Directory.CreateDirectory(ProfileRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(Keystore)!);
        Directory.CreateDirectory(MailRoot);
        Directory.CreateDirectory(Path.Combine(Root, "shared"));
        if (string.Equals(ProfileId, "local", StringComparison.Ordinal))
        {
            var legacyDrafts = Path.Combine(Root, "drafts");
            if (Directory.Exists(legacyDrafts) && !Directory.Exists(Drafts))
                Directory.Move(legacyDrafts, Drafts);
        }
    }
}

public static class Hashing
{
    public static byte[] Sha256(params byte[][] parts)
    {
        using var h = SHA256.Create();
        foreach (var p in parts) h.TransformBlock(p, 0, p.Length, null, 0);
        h.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return h.Hash!;
    }

    public static string Hex(ReadOnlySpan<byte> data) => Convert.ToHexString(data).ToLowerInvariant();
}
