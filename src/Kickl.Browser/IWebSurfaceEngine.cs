using Avalonia.Controls;
using Avalonia.Media;

namespace Kickl.Browser;

/// <summary>
/// Cross-platform sealed-surface boundary. Platform adapters must preserve the policy surface;
/// the current Avalonia adapter uses native controls and therefore has no script or URL loader.
/// </summary>
internal interface IWebSurfaceEngine
{
    string Name { get; }
    bool JavaScriptEnabled { get; }
    string ContentSecurityPolicy { get; }
    bool IsSecureScheme(string? scheme);
    bool AllowsTopLevelNavigation(string? address);
    bool AllowsSubresource(string? reference);
    Control RenderInlineFragment(string html, IBrush text, IBrush muted, IBrush accent,
        FontFamily body, FontFamily mono, double size,
        IReadOnlyDictionary<string, HtmlBlockView.UnlockedVisual>? visuals = null,
        Action<IReadOnlyList<string>>? unlock = null);
}

internal sealed class AvaloniaSealedSurfaceEngine : IWebSurfaceEngine
{
    public string Name => "avalonia-static";
    public bool JavaScriptEnabled => Kickl.Core.Markup.SealedSurfacePolicy.JavaScriptEnabled;
    public string ContentSecurityPolicy => Kickl.Core.Markup.SealedSurfacePolicy.ContentSecurityPolicy;
    public bool IsSecureScheme(string? scheme) => Kickl.Core.Markup.SealedSurfacePolicy.IsSecureScheme(scheme);
    public bool AllowsTopLevelNavigation(string? address) => Kickl.Core.Markup.SealedSurfacePolicy.AllowsTopLevelNavigation(address);
    public bool AllowsSubresource(string? reference) => Kickl.Core.Markup.SealedSurfacePolicy.AllowsSubresource(reference);

    public Control RenderInlineFragment(string html, IBrush text, IBrush muted, IBrush accent,
        FontFamily body, FontFamily mono, double size,
        IReadOnlyDictionary<string, HtmlBlockView.UnlockedVisual>? visuals = null,
        Action<IReadOnlyList<string>>? unlock = null) =>
        HtmlBlockView.Build(html, text, muted, accent, body, mono, size, visuals, unlock);
}
