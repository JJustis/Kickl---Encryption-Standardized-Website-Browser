using System.Text;
using Kickl.Core.Util;

namespace Kickl.Browser;

internal static class CrashLog
{
    public static string PathFor(string dataRoot) => Path.Combine(dataRoot, "crash.log");

    /// <summary>
    /// A crash is logged in plaintext (nothing can be encrypted yet), so only what helps fix the bug is kept: the error type,
    /// a scrubbed message and scrubbed stack frames. Sites, addresses, file paths with an account name and anything that
    /// looks like a key are removed, each entry is capped, and the file is bounded and locked to its owner.
    /// </summary>
    public static void Write(string dataRoot, string source, Exception exception)
    {
        try
        {
            var text = new StringBuilder()
                .AppendLine("---- " + DateTimeOffset.Now.ToString("O") + " · " + source)
                .AppendLine(LogScrubber.Scrub(exception.ToString()))
                .AppendLine()
                .ToString();
            LogScrubber.AppendBounded(PathFor(dataRoot), text);
        }
        catch
        {
            // Logging must never be the reason the app fails.
        }
    }
}
