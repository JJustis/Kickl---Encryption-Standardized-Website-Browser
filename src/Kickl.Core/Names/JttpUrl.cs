using System.Text.RegularExpressions;

namespace Kickl.Core.Names;

/// <summary>
/// A jttp:// address. This is our own DNS namespace: it is resolved by the registry mirror
/// and never touches the public DNS or HTTP stack.
/// </summary>
public sealed record JttpUrl(string Host, string Path, string? Query, string? Fragment)
{
    private static readonly Regex Label = new(
        "^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public bool IsCustomNamespace => Host.EndsWith(".custom", StringComparison.Ordinal);

    public override string ToString()
    {
        var s = $"jttp://{Host}{Path}";
        if (!string.IsNullOrEmpty(Query)) s += "?" + Query;
        if (!string.IsNullOrEmpty(Fragment)) s += "#" + Fragment;
        return s;
    }

    public static bool TryParse(string? input, out JttpUrl? url, out string? error)
    {
        url = null;
        error = null;

        if (string.IsNullOrWhiteSpace(input))
        {
            error = "Empty address.";
            return false;
        }

        var s = input.Trim();
        if (!s.StartsWith("jttp://", StringComparison.OrdinalIgnoreCase))
        {
            error = "jttp addresses must start with jttp://.";
            return false;
        }

        s = s[7..];

        string? fragment = null;
        var hash = s.IndexOf('#');
        if (hash >= 0)
        {
            fragment = s[(hash + 1)..];
            s = s[..hash];
        }

        string? query = null;
        var q = s.IndexOf('?');
        if (q >= 0)
        {
            query = s[(q + 1)..];
            s = s[..q];
        }

        var slash = s.IndexOf('/');
        var authority = slash >= 0 ? s[..slash] : s;
        var path = slash >= 0 ? s[slash..] : "/";

        if (authority.Length == 0)
        {
            error = "Missing host.";
            return false;
        }
        if (authority.Contains('@') || authority.Contains(':'))
        {
            error = "Userinfo and ports are not supported in jttp addresses.";
            return false;
        }

        var host = authority.ToLowerInvariant();
        var labels = host.Split('.');
        if (labels.Length != 2 || labels.Any(l => !Label.IsMatch(l)))
        {
            error = "A jttp host is exactly name.tld (LDH labels, no leading/trailing hyphen).";
            return false;
        }

        if (labels[1] == "custom" && labels[0] is "home" or "help" or "settings" or "registry"
            or "mail" or "chat" or "vault" or "panic" or "about" or "security" or "debug")
        {
            error = "Reserved namespace.";
            return false;
        }

        if (!path.StartsWith('/')) path = "/" + path;
        if (path.Contains("/../", StringComparison.Ordinal) || path.EndsWith("/..", StringComparison.Ordinal))
        {
            error = "Path traversal is not allowed.";
            return false;
        }

        url = new JttpUrl(host, path, string.IsNullOrEmpty(query) ? null : query,
            string.IsNullOrEmpty(fragment) ? null : fragment);
        return true;
    }
}
