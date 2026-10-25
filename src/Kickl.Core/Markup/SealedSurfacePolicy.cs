using Kickl.Core.Names;

namespace Kickl.Core.Markup;

/// <summary>Security defaults shared by every sealed-page rendering engine.</summary>
public static class SealedSurfacePolicy
{
    public const string Scheme = "jttp";
    public const bool JavaScriptEnabled = false;
    public const bool NetworkRequestsAllowed = false;
    public const string ContentSecurityPolicy =
        "default-src 'none'; script-src 'none'; object-src 'none'; base-uri 'none'; " +
        "connect-src 'none'; img-src data:; style-src 'none'; font-src 'none'; " +
        "frame-src 'none'; form-action 'none'; frame-ancestors 'none'";

    public static bool IsSecureScheme(string? scheme) =>
        string.Equals(scheme, Scheme, StringComparison.OrdinalIgnoreCase);

    public static bool AllowsTopLevelNavigation(string? address) =>
        JttpUrl.TryParse(address, out _, out _);

    /// <summary>Only renderer-local data images and sealed blob references are subresources.</summary>
    public static bool AllowsSubresource(string? reference) =>
        !string.IsNullOrWhiteSpace(reference) &&
        (InlineHtml.IsSafeImageData(reference) || InlineHtml.IsSealedReference(reference));

    public static bool AllowsNetworkRequest(Uri? _) => false;
}
