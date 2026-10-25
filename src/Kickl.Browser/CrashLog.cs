using System.Text;

namespace Kickl.Browser;

internal static class CrashLog
{
    public static string PathFor(string dataRoot) => Path.Combine(dataRoot, "crash.log");

    public static void Write(string dataRoot, string source, Exception exception)
    {
        try
        {
            Directory.CreateDirectory(dataRoot);
            var text = new StringBuilder()
                .AppendLine("---- " + DateTimeOffset.Now.ToString("O") + " · " + source)
                .AppendLine(exception.ToString())
                .AppendLine()
                .ToString();
            File.AppendAllText(PathFor(dataRoot), text, Encoding.UTF8);
        }
        catch
        {
            // Logging must never be the reason the app fails.
        }
    }
}
