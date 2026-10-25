using System.Net;

namespace Kickl.Core.Documents;

/// <summary>
/// A complete, editable example page for a newly created name. It is deliberately an inline
/// SVG/HTML page: the quickest way to learn the sealed-site idiom is to change a working one.
/// </summary>
public static class SiteStarter
{
    public static string Example(string host)
    {
        var safe = WebUtility.HtmlEncode(host.Trim());
        return $$"""
            <div>
              <svg xmlns="http://www.w3.org/2000/svg" width="660" height="230" viewBox="0 0 660 230">
                <defs>
                  <linearGradient id="starter-bg" x1="0" y1="0" x2="1" y2="1">
                    <stop offset="0%" stop-color="#162635"/>
                    <stop offset="100%" stop-color="#081018"/>
                  </linearGradient>
                </defs>
                <rect width="660" height="230" rx="14" fill="url(#starter-bg)"/>
                <circle cx="566" cy="42" r="105" fill="#78c8c8" opacity="0.14"/>
                <circle cx="606" cy="190" r="58" fill="#ff7a64" opacity="0.10"/>
                <text x="36" y="52" fill="#78c8c8" font-family="monospace" font-size="14">YOUR NEW SEALED PAGE</text>
                <text x="36" y="108" fill="#e8f1f8" font-family="monospace" font-size="30">{{safe}}</text>
                <text x="36" y="150" fill="#a9bdd2" font-family="monospace" font-size="14">Change this drawing and the words below.</text>
                <text x="36" y="176" fill="#8fa8bd" font-family="monospace" font-size="12">Publish when it feels like yours.</text>
                <rect x="36" y="196" width="74" height="6" rx="3" fill="#78c8c8"/>
              </svg>
              <p>This is a working example, not a blank page. Edit the inline HTML/SVG above to learn how a sealed site is made: shapes, gradients, text, lists, tables, and raster images all live in one fragment.</p>
              <p><b>Headline</b> and <b>tags</b> are the only public parts. The page itself is sealed into the package, and every reader gets the package hash checked before it renders.</p>
              <p>To try a sealed image, paste a <code>data:image/png;base64,…</code> URL into an <code>&lt;image href="…"&gt;</code> inside the SVG. Publishing extracts it, encrypts it, and replaces it with a <code>kickl-blob:</code> reference; the reader clicks unlock before it appears.</p>
              <p><b>NCKL ads are optional.</b> Webmasters can open <code>jttp://secupgrade.com/adnetworking/publisher</code>, mint a placement for this host, and paste the generated snippet into an ad-space div. Publishing validates the signed placement and mounts one native <b>AD</b> slot; no site script is ever executed.</p>
            </div>
            """;
    }
}
