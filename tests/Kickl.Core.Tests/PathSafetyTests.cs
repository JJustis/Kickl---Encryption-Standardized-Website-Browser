using Kickl.Core.Documents;
using Kickl.Core.Ownership;
using Kickl.Core.Registry;
using Kickl.Core.Session;
using Kickl.Core.Util;
using Xunit;

namespace Kickl.Core.Tests;

public class PathSafetyTests
{
    [Theory]
    [InlineData("profiles/josh/sites/a.com/doc.json")]
    [InlineData(@"profiles\josh\sites\a.com\doc.json")]
    [InlineData("shared/registry.json")]
    public void OrdinaryRelativePathsStayInsideTheRoot(string relative)
    {
        using var temp = new TempDir();
        var resolved = SafeNames.ResolveUnder(temp.Path, relative);
        Assert.NotNull(resolved);
        Assert.StartsWith(Path.GetFullPath(temp.Path), resolved);
    }

    [Theory]
    [InlineData("../outside.json")]
    [InlineData(@"..\outside.json")]
    [InlineData("profiles/../../outside.json")]
    [InlineData("a/./b")]
    [InlineData("/etc/passwd")]
    [InlineData(@"\Windows\win.ini")]
    [InlineData(@"C:\Windows\win.ini")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData(@"\\server\share\doc.json")]
    [InlineData("//server/share/doc.json")]
    [InlineData("doc.json:hidden")]                     // an NTFS alternate data stream
    [InlineData("a\0b")]
    [InlineData("trailing-dot./x")]
    [InlineData("")]
    [InlineData("   ")]
    public void AnythingThatCouldEscapeTheRootIsRefused(string relative)
    {
        using var temp = new TempDir();
        Assert.Null(SafeNames.ResolveUnder(temp.Path, relative));
    }

    [Theory]
    [InlineData("example.com", true)]
    [InlineData("my-site.custom", true)]
    [InlineData("peanutbutter.rss", true)]
    [InlineData("../evil", false)]
    [InlineData("a/b.com", false)]
    [InlineData(@"a\b.com", false)]
    [InlineData("evil.com/..", false)]
    [InlineData("C:", false)]
    [InlineData("a.b.com", false)]                      // exactly name.tld
    [InlineData("-a.com", false)]
    [InlineData("UPPER.com", false)]
    [InlineData("a..com", false)]
    [InlineData("", false)]
    public void OnlyARealHostNameIsAHost(string name, bool valid) => Assert.Equal(valid, SafeNames.IsHost(name));

    [Fact]
    public void FilePathHelpersRefuseAHostThatIsReallyAPath()
    {
        using var temp = new TempDir();
        var paths = new DataPaths(temp.Path, "p");
        Assert.Throws<ArgumentException>(() => paths.SiteDoc("../../evil"));
        Assert.Throws<ArgumentException>(() => paths.SiteDoc(@"..\..\evil"));
        Assert.Throws<ArgumentException>(() => OwnershipAuthority.TitlePath(paths, "../x"));
        Assert.Throws<ArgumentException>(() => OwnershipAuthority.SecretPath(temp.Path, "../x"));
        Assert.EndsWith(Path.Combine("sites", "fine.com", "doc.json"), paths.SiteDoc("fine.com"));
    }

    [Fact]
    public void ARegistryRecordCannotPointTheBrowserAtAFileOutsideItsDataFolder()
    {
        using var temp = new TempDir();
        var data = Path.Combine(temp.Path, "data");
        var outside = Path.Combine(temp.Path, "outside");
        Directory.CreateDirectory(outside);

        // a real, valid sealed document, but stored where the data folder should never reach
        var owner = new DataPaths(Path.Combine(temp.Path, "elsewhere"), "o");
        Crypto.Keystore.Create(owner, "pin");
        var master = Crypto.Keystore.Unlock(owner, "pin");
        DocumentStore.Publish(owner, Fixture.SiteDir, "pinwheelexplained.com", master);
        var stolen = Path.Combine(outside, "doc.json");
        File.Copy(owner.SiteDoc("pinwheelexplained.com"), stolen);

        var victim = new DataPaths(data, "v");
        victim.Ensure();
        foreach (var docFile in new[] { "../outside/doc.json", stolen, stolen.Replace('\\', '/'), @"..\outside\doc.json" })
        {
            var registry = LocalRegistry.LoadOrEmpty(victim);
            registry.Records.RemoveAll(r => r.Name == "pinwheelexplained.com");
            registry.Records.Add(new RegistryRecord { Name = "pinwheelexplained.com", DocFile = docFile, Status = "active" });
            LocalRegistry.Save(victim, registry);

            // the file outside is never consulted, so the name simply has no document here
            Assert.Throws<SiteNotFoundException>(() => DocumentStore.Load(victim, "pinwheelexplained.com"));
        }
    }

    [Fact]
    public void ARegistryRecordPointingInsideTheDataFolderStillWorks()
    {
        using var temp = new TempDir();
        var owner = new DataPaths(temp.Path, "owner");
        Crypto.Keystore.Create(owner, "pin");
        var master = Crypto.Keystore.Unlock(owner, "pin");
        DocumentStore.Publish(owner, Fixture.SiteDir, "pinwheelexplained.com", master);

        // another profile reads it through the shared registry's relative DocFile
        var other = new DataPaths(temp.Path, "other");
        other.Ensure();
        var record = LocalRegistry.Resolve(LocalRegistry.LoadOrEmpty(other), "pinwheelexplained.com")!;
        Assert.StartsWith("profiles/owner/", record.DocFile.Replace('\\', '/'));
        Assert.NotNull(DocumentStore.Load(other, "pinwheelexplained.com"));
    }
}
