using System.Text;
using System.Text.RegularExpressions;

namespace Kickl.Core.Util;

/// <summary>
/// Makes text safe to keep in a plaintext log. A crash report is written before anything can be encrypted, so what goes
/// into it must not name the sites a person visits, their account name, or anything that looks like a key.
/// </summary>
public static partial class LogScrubber
{
    public const int MaxEntryChars = 8_000;
    public const long MaxFileBytes = 256 * 1024;

    [GeneratedRegex(@"\b(?:jttp|https?|ftp|file)://[^\s""'<>)\]]+", RegexOptions.IgnoreCase)]
    private static partial Regex Urls();

    [GeneratedRegex(@"[\w.+-]+@[\w-]+(?:\.[\w-]+)+")]
    private static partial Regex Emails();

    // C:\Users\someone\...\File.cs  ->  <path>\File.cs     /home/someone/x/y.cs  ->  <path>/y.cs
    [GeneratedRegex(@"(?:[A-Za-z]:\\|\\\\)(?:[^\s:""'<>|?*\\]+\\)+(?=[^\s\\:""'<>|?*]+)")]
    private static partial Regex WindowsPaths();

    [GeneratedRegex(@"(?<![\w.])/(?:[^\s:""'<>/]+/)+(?=[^\s/:""'<>]+)")]
    private static partial Regex UnixPaths();

    // 32+ characters of hex or base64-ish text: a key, a token, a hash, a certificate body
    [GeneratedRegex(@"(?<![A-Za-z0-9+/_=-])[A-Za-z0-9+/_-]{32,}={0,2}(?![A-Za-z0-9+/_=-])")]
    private static partial Regex Secretish();

    // a KG- style id, an access key, "kickl-blob:..." references
    [GeneratedRegex(@"\b(?:kickl-blob:[\w-]+|[A-Z2-9]{5}(?:-[A-Z2-9]{5}){4})\b")]
    private static partial Regex Identifiers();

    public static string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var s = text;
        s = Urls().Replace(s, "<url>");
        s = Emails().Replace(s, "<email>");
        s = WindowsPaths().Replace(s, "<path>\\");
        s = UnixPaths().Replace(s, "<path>/");
        s = Identifiers().Replace(s, "<id>");
        s = Secretish().Replace(s, "<redacted>");
        s = Environment.UserName.Length >= 3 ? s.Replace(Environment.UserName, "<user>", StringComparison.OrdinalIgnoreCase) : s;
        return s.Length > MaxEntryChars ? s[..MaxEntryChars] + "\n<truncated>" : s;
    }

    /// <summary>
    /// Append one entry to a log that can never grow past <see cref="MaxFileBytes"/> (the previous file is kept once as
    /// <c>.1</c>), and lock the file to its owner.
    /// </summary>
    public static void AppendBounded(string path, string entry)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if (File.Exists(path) && new FileInfo(path).Length + entry.Length > MaxFileBytes)
            File.Move(path, path + ".1", overwrite: true);
        File.AppendAllText(path, entry, Encoding.UTF8);
        FileProtection.Restrict(path);
        if (File.Exists(path + ".1")) FileProtection.Restrict(path + ".1");
    }
}
