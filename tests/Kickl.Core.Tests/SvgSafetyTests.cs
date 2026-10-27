using System.Diagnostics;
using System.Text;
using Kickl.Core.Markup;
using Xunit;

namespace Kickl.Core.Tests;

/// <summary>
/// An SVG's cost is not its size: a &lt;use&gt;, paint server or mask pulls in everything it points at. Measured against the real
/// renderer, 1.6 KB of nested &lt;use&gt; ran for over 20 seconds. These keep the sanitizer from letting that through.
/// </summary>
public class SvgSafetyTests
{
    private static string Laughs(int levels, int fan)
    {
        var sb = new StringBuilder("<svg viewBox='0 0 10 10'><defs>");
        sb.Append("<g id='g0'><rect width='1' height='1' fill='red'/></g>");
        for (var i = 1; i <= levels; i++)
        {
            sb.Append($"<g id='g{i}'>");
            for (var k = 0; k < fan; k++) sb.Append($"<use href='#g{i - 1}' x='{k}'/>");
            sb.Append("</g>");
        }
        sb.Append($"</defs><use href='#g{levels}'/></svg>");
        return sb.ToString();
    }

    [Theory]
    [InlineData(4)]    // 10^4 draws: already ~2 s in the renderer
    [InlineData(6)]    // 10^6: the measured hang
    [InlineData(12)]   // 10^12
    public void ExponentialUseChainsAreRejected_Quickly(int levels)
    {
        var sw = Stopwatch.StartNew();
        Assert.Throws<KmlException>(() => InlineHtml.Sanitize("<div>" + Laughs(levels, 10) + "</div>"));
        Assert.True(sw.ElapsedMilliseconds < 2000, $"rejecting took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void AReferenceThatPointsBackAtItsOwnAncestorIsRejected()
    {
        Assert.Throws<KmlException>(() => InlineHtml.Sanitize("<svg><g id='a'><use href='#a'/></g></svg>"));
        Assert.Throws<KmlException>(() => InlineHtml.Sanitize("<svg><g id='a'><use href='#b'/></g><g id='b'><use href='#a'/></g></svg>"));
    }

    [Fact]
    public void APaintServerThatUsesItselfIsRejected()
    {
        var cyclic = "<svg><defs><pattern id='p' width='1' height='1'><rect width='1' height='1' fill='url(#p)'/></pattern></defs><rect fill='url(#p)' width='5' height='5'/></svg>";
        Assert.Throws<KmlException>(() => InlineHtml.Sanitize(cyclic));
    }

    [Fact]
    public void ADeepReferenceChainIsRejectedWithoutExhaustingTheStack()
    {
        var sb = new StringBuilder("<svg><defs><g id='c0'><rect width='1' height='1'/></g>");
        for (var i = 1; i <= 400; i++) sb.Append($"<g id='c{i}'><use href='#c{i - 1}'/></g>");
        sb.Append("</defs><use href='#c400'/></svg>");
        var ex = Assert.Throws<KmlException>(() => InlineHtml.Sanitize(sb.ToString()));
        Assert.Contains("deep", ex.Message);
    }

    [Fact]
    public void OrdinaryReuseStillWorks()
    {
        // a symbol reused a few dozen times, gradients, a mask and a clip: what real art does
        var sb = new StringBuilder("<svg viewBox='0 0 100 100'><defs>");
        sb.Append("<linearGradient id='lg'><stop offset='0' stop-color='#fff'/><stop offset='1' stop-color='#000'/></linearGradient>");
        sb.Append("<clipPath id='cp'><circle cx='50' cy='50' r='40'/></clipPath>");
        sb.Append("<g id='star'><polygon points='0,0 5,10 10,0' fill='url(#lg)'/></g></defs>");
        for (var i = 0; i < 40; i++) sb.Append($"<use href='#star' x='{i}' y='{i}'/>");
        sb.Append("<rect width='100' height='100' clip-path='url(#cp)' fill='url(#lg)'/></svg>");

        var result = InlineHtml.Sanitize(sb.ToString());
        Assert.Matches(@"clip-path=['""]url\(#cp\)['""]", result);
        Assert.Equal(40, System.Text.RegularExpressions.Regex.Matches(result, "<use ").Count);
    }

    [Fact]
    public void AnSvgFileGetsTheSameTreatmentAsInlineSvg()
    {
        // a normal file, with an XML declaration and a DOCTYPE, survives
        var file = "<?xml version=\"1.0\"?><!DOCTYPE svg PUBLIC \"-//W3C//DTD SVG 1.1//EN\" \"http://evil.example/x.dtd\">" +
                   "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 10 10\"><circle cx=\"5\" cy=\"5\" r=\"4\" fill=\"#3df5c8\"/></svg>";
        var clean = InlineHtml.SanitizeSvgFile(file);
        Assert.StartsWith("<svg", clean);
        Assert.Contains("circle", clean);
        Assert.DoesNotContain("evil.example", clean);

        // the same hostile content that is refused inline is refused as a file
        Assert.Throws<KmlException>(() => InlineHtml.SanitizeSvgFile(Laughs(6, 10)));
        Assert.Throws<KmlException>(() => InlineHtml.SanitizeSvgFile("<svg><g id='a'><use href='#a'/></g></svg>"));
        Assert.Throws<KmlException>(() => InlineHtml.SanitizeSvgFile(new string(' ', InlineHtml.MaxChars + 10) + "<svg/>"));
        Assert.Throws<KmlException>(() => InlineHtml.SanitizeSvgFile("   "));
        Assert.Throws<KmlException>(() => InlineHtml.SanitizeSvgFile("<p>not an svg</p>"));

        // scripts and event handlers do not survive, and external references are dropped
        var hostile = InlineHtml.SanitizeSvgFile("<svg onload=\"alert(1)\"><script>alert(1)</script><image href=\"https://evil.example/x.png\" width=\"5\" height=\"5\"/><rect width=\"5\" height=\"5\" filter=\"url(https://evil.example/f#x)\"/></svg>");
        Assert.DoesNotContain("onload", hostile);
        Assert.DoesNotContain("script", hostile);
        Assert.DoesNotContain("evil.example", hostile);
    }

    [Theory]
    [InlineData("filter")]
    [InlineData("mask")]
    [InlineData("clip-path")]
    public void FilterMaskAndClipMayNotReachOutsideTheDocument(string attribute)
    {
        foreach (var external in new[] { "url(https://evil.example/x.svg#f)", "url(http://10.0.0.1/a)", "url('//evil.example/x#f')", "url(data:image/svg+xml,AAAA)" })
        {
            var result = InlineHtml.Sanitize($"<svg><rect width='5' height='5' {attribute}=\"{external}\"/></svg>");
            Assert.DoesNotContain("evil.example", result);
            Assert.DoesNotContain("10.0.0.1", result);
            Assert.DoesNotContain("data:", result);
        }

        var local = InlineHtml.Sanitize($"<svg><defs><filter id='f'/></defs><rect width='5' height='5' {attribute}='url(#f)'/></svg>");
        Assert.Matches(attribute + @"=['""]url\(#f\)['""]", local);
    }
}
