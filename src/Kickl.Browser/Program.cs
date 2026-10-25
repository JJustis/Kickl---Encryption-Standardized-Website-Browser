using Avalonia;

namespace Kickl.Browser;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var data = Arg(args, "--data")
                   ?? Path.Combine(
                       Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                       "KicklBrowser");
        App.DataRoot = Path.GetFullPath(data);
        App.ScreenshotPath = Arg(args, "--screenshot");
        App.ExitAfterScreenshot = App.ScreenshotPath is not null;
        App.DemoMode = HasFlag(args, "--demo");
        App.StartUrl = Arg(args, "--open") ?? "";
        App.StartSearch = Arg(args, "--search");

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception exception)
                CrashLog.Write(App.DataRoot, "appdomain", exception);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLog.Write(App.DataRoot, "task", e.Exception);
            e.SetObserved();
        };
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

    private static string? Arg(string[] args, string name)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                return args[i + 1];
            if (args[i].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                return args[i][(name.Length + 1)..];
        }
        return null;
    }

    private static bool HasFlag(string[] args, string name) =>
        args.Any(arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));
}
