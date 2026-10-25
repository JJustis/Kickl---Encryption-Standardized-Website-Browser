using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace Kickl.Browser;

public sealed class App : Application
{
    internal static string DataRoot { get; set; } = ".";
    internal static string? ScreenshotPath { get; set; }
    internal static bool ExitAfterScreenshot { get; set; }
    internal static bool DemoMode { get; set; }
    internal static string StartUrl { get; set; } = "";
    internal static string? StartSearch { get; set; }

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            CrashLog.Write(DataRoot, "ui", e.Exception);
            // Keep the window alive; the action failed, the app should not vanish.
            e.Handled = true;
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new BrowserWindow(DataRoot, StartUrl, ScreenshotPath, ExitAfterScreenshot, DemoMode);

        base.OnFrameworkInitializationCompleted();
    }
}
