using Kickl.Core.Util;
using Xunit;

namespace Kickl.Core.Tests;

public class LogScrubberTests
{
    [Fact]
    public void AddressesAreRemoved()
    {
        var s = LogScrubber.Scrub("could not open jttp://secret-site.com/page?q=1 or https://example.org/a/b via http://localhost:80/x");
        Assert.DoesNotContain("secret-site", s);
        Assert.DoesNotContain("example.org", s);
        Assert.DoesNotContain("localhost", s);
        Assert.Contains("<url>", s);
    }

    [Fact]
    public void FilePathsKeepTheFileNameButLoseTheAccountAndFolders()
    {
        var s = LogScrubber.Scrub(@"at Kickl.Core.Session.BrowserSession.Open() in C:\Users\someone-private\Desktop\omnibrowser\src\BrowserSession.cs:line 99");
        Assert.DoesNotContain("someone-private", s);
        Assert.DoesNotContain("Desktop", s);
        Assert.Contains("BrowserSession.cs:line 99", s);                     // the part a developer needs

        var unix = LogScrubber.Scrub("at X.Y() in /home/alice/projects/kickl/src/Y.cs:line 7");
        Assert.DoesNotContain("alice", unix);
        Assert.Contains("Y.cs:line 7", unix);
    }

    [Fact]
    public void ThingsThatLookLikeKeysAndIdentifiersAreRedacted()
    {
        var hex = new string('a', 64);
        var b64 = "MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEAtGhnxOrOL";
        var s = LogScrubber.Scrub($"token {hex} key {b64} id KG-ABCDE-FGHJK-LMNPQ-RSTUV-WXYZ2 blob kickl-blob:image1 mail me@example.com");
        Assert.DoesNotContain(hex, s);
        Assert.DoesNotContain("MIIBojAN", s);
        Assert.DoesNotContain("ABCDE-FGHJK", s);
        Assert.DoesNotContain("image1", s);
        Assert.DoesNotContain("me@example.com", s);
        Assert.Contains("token", s);                                          // ordinary words survive
    }

    [Fact]
    public void OrdinaryErrorTextIsLeftReadable()
    {
        var s = LogScrubber.Scrub("System.InvalidOperationException: The session is locked.\n   at Kickl.Core.Session.BrowserSession.get_SessionSecret()");
        Assert.Contains("The session is locked.", s);
        Assert.Contains("BrowserSession.get_SessionSecret", s);
    }

    [Fact]
    public void ANullOrEmptyEntryIsFine_AndAHugeOneIsCapped()
    {
        Assert.Equal("", LogScrubber.Scrub(null));
        var big = LogScrubber.Scrub(new string('x', 50_000).Replace("x", "ab "));
        Assert.True(big.Length < LogScrubber.MaxEntryChars + 40);
        Assert.EndsWith("<truncated>", big);
    }

    [Fact]
    public void TheLogNeverGrowsPastItsCap_AndKeepsOneOlderFile()
    {
        using var temp = new TempDir();
        var path = Path.Combine(temp.Path, "crash.log");
        var entry = new string('e', 4_000) + "\n";
        for (var i = 0; i < 200; i++) LogScrubber.AppendBounded(path, entry);   // 800 KB offered

        Assert.True(new FileInfo(path).Length <= LogScrubber.MaxFileBytes);
        Assert.True(File.Exists(path + ".1"));
        Assert.True(new FileInfo(path + ".1").Length <= LogScrubber.MaxFileBytes);
        Assert.False(File.Exists(path + ".2"));                                 // nothing else piles up
    }

    [Fact]
    public void TheLogFileIsLockedToItsOwner()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var temp = new TempDir();
        var path = Path.Combine(temp.Path, "crash.log");
        LogScrubber.AppendBounded(path, "entry\n");
        var allowed = FileProtection.AllowedPrincipals(path)!;
        Assert.DoesNotContain("S-1-5-32-545", allowed);
        Assert.DoesNotContain("S-1-1-0", allowed);
    }
}
