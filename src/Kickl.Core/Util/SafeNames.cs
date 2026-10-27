using System.Text.RegularExpressions;

namespace Kickl.Core.Util;

/// <summary>
/// Names that come from a site, a registry record, a certificate or the command line must be proven harmless before they
/// become part of a file path. <see cref="Path.Combine(string, string)"/> is not a safeguard: it silently throws away the first
/// part when the second is rooted, and it follows <c>..</c>.
/// </summary>
public static partial class SafeNames
{
    // exactly name.tld, lower-case letters, digits and inner hyphens: the same rule jttp addresses use
    [GeneratedRegex(@"^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$")]
    private static partial Regex HostShape();

    public static bool IsHost(string? name) => name is { Length: <= 125 } && HostShape().IsMatch(name);

    /// <summary>Returns the host unchanged, or throws if it could not be a jttp host (and so could be a path trick).</summary>
    public static string RequireHost(string? name) =>
        IsHost(name) ? name! : throw new ArgumentException("That is not a valid site name.", nameof(name));

    /// <summary>
    /// Resolve <paramref name="relative"/> under <paramref name="root"/> and return the full path, or null if it is rooted,
    /// contains a parent reference, a drive, a stream or control characters, or would land outside the root.
    /// </summary>
    public static string? ResolveUnder(string root, string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Length > 400) return null;
        if (relative.Any(c => c < ' ' || c is '<' or '>' or '"' or '|' or '?' or '*' or ':')) return null;   // ':' covers drive letters and NTFS streams

        var normalized = relative.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.StartsWith("//", StringComparison.Ordinal)) return null;
        if (normalized.Split('/').Any(part => part is ".." or "." || part.Length == 0 || part.EndsWith(' ') || part.EndsWith('.')))
            return null;

        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(fullRoot, normalized.Replace('/', Path.DirectorySeparatorChar)));
        return full.StartsWith(fullRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            ? full
            : null;
    }
}
