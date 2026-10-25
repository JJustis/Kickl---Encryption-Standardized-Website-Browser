using Kickl.Core.Crypto;
using Kickl.Core.Util;

namespace Kickl.Core.Session;

/// <summary>Discovers local encrypted profiles and converts a display name to a safe directory id.</summary>
public static class ProfileCatalog
{
    public static IReadOnlyList<string> List(string dataRoot)
    {
        var root = Path.Combine(Path.GetFullPath(dataRoot), "profiles");
        if (!Directory.Exists(root)) return [];

        var profiles = new List<string>();
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var id = Path.GetFileName(directory);
            try
            {
                if (Keystore.Exists(new DataPaths(dataRoot, id))) profiles.Add(id);
            }
            catch (ArgumentException) { /* ignore directories that are not valid profile ids */ }
        }
        return profiles.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static string CreateId(string displayName)
    {
        var value = (displayName ?? "").Trim();
        if (value.Length is < 1 or > 32 || value.Any(c =>
                !(char.IsAsciiLetterOrDigit(c) || c is ' ' or '-' or '_')))
            throw new ArgumentException("Profile name must be 1–32 letters or numbers; spaces, hyphens, and underscores are allowed.", nameof(displayName));

        var id = string.Join('-', value.Split([' ', '_', '-'], StringSplitOptions.RemoveEmptyEntries))
            .ToLowerInvariant();
        if (id.Length is < 1 or > 32)
            throw new ArgumentException("Profile name must produce an id of at most 32 characters.", nameof(displayName));
        return id;
    }

    public static string DisplayName(string profileId) => profileId.Replace('-', ' ');
}
