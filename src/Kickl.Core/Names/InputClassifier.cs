using System.Text.RegularExpressions;

namespace Kickl.Core.Names;

public enum InputLane
{
    Sealed,
    Clearnet,
    Search,
    Invalid
}

public sealed record NavigationTarget(InputLane Lane, string? Url, string? Reason);

/// <summary>
/// Sorts omnibox input into the two lanes. jttp is never a substitute for http:
/// explicit jttp:// wins, http(s):// stays clearnet, a bare *.custom name resolves in our
/// namespace, and a bare real-TLD name goes to the open web.
/// </summary>
public static class InputClassifier
{
    private static readonly Regex HostLike = new(
        "^(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\\.)+[a-z]{2,24}$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static NavigationTarget Classify(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new NavigationTarget(InputLane.Invalid, null, "Empty input.");

        var input = raw.Trim();

        if (input.StartsWith("jttp://", StringComparison.OrdinalIgnoreCase))
        {
            return JttpUrl.TryParse(input, out var url, out var error)
                ? new NavigationTarget(InputLane.Sealed, url!.ToString(), "Explicit sealed address.")
                : new NavigationTarget(InputLane.Invalid, null, error);
        }

        if (input.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            input.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return Uri.TryCreate(input, UriKind.Absolute, out var uri)
                ? new NavigationTarget(InputLane.Clearnet, uri.ToString(), "Open web lane.")
                : new NavigationTarget(InputLane.Invalid, null, "Malformed http(s) URL.");
        }

        if (input.Any(char.IsWhiteSpace) || input.StartsWith('?') || input.StartsWith('!'))
            return new NavigationTarget(InputLane.Search, input, "Search query.");

        var authority = input.Split('/', 2)[0].ToLowerInvariant();

        if (authority.EndsWith(".custom", StringComparison.Ordinal))
        {
            var candidate = "jttp://" + input;
            return JttpUrl.TryParse(candidate, out var url, out var error)
                ? new NavigationTarget(InputLane.Sealed, url!.ToString(), ".custom is a Kickl namespace.")
                : new NavigationTarget(InputLane.Invalid, null, error);
        }

        if (HostLike.IsMatch(authority))
            return new NavigationTarget(InputLane.Clearnet, "https://" + input, "Real TLD → open web lane.");

        return new NavigationTarget(InputLane.Search, input, "No scheme and no real TLD → search.");
    }
}
