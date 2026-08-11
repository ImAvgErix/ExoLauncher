using ExoLauncher.Services;
using Microsoft.UI.Xaml;

namespace ExoLauncher;

public partial class App : Application
{
    public static AppServices Services { get; } = new();
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
        Services.Initialize();
        UnhandledException += (_, e) =>
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(Helpers.PathHelper.LogsDir, "unhandled.log"),
                    $"[{DateTime.UtcNow:O}] {e.Exception}{Environment.NewLine}");
            }
            catch { /* best-effort */ }
            // An unhandled UI exception may leave native/WebView state inconsistent.
            // Record it, then let WinUI terminate cleanly instead of claiming every
            // unknown failure was recovered.
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(Helpers.PathHelper.LogsDir, "unhandled.log"),
                    $"[{DateTime.UtcNow:O}] DOMAIN {e.ExceptionObject}{Environment.NewLine}");
            }
            catch { /* best-effort */ }
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Clean leftover Steam CEF surfaces from older hide/restore (blank taskbar spam).
        try { Adapters.StoreWindowHider.CollapseOrphanSurfaces(); } catch { /* */ }
        _window = new MainWindow();
        MainAppWindow = _window;
        _window.Activate();
        Program.NotifyWindowReady();
    }

    public static MainWindow? MainAppWindow { get; set; }
}
