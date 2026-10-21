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
                Helpers.AppLog.Crash("Unhandled UI exception", e.Exception);
            }
            catch { /* best-effort */ }
            // An unhandled UI exception may leave native window state inconsistent.
            // Record it, then let WinUI terminate cleanly instead of claiming every
            // unknown failure was recovered.
        };
        // A faulted task nobody awaited: logged with its type and stack instead of vanishing.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            try { Helpers.AppLog.Warn("Unobserved task exception: " + e.Exception.GetBaseException()); }
            catch { /* best-effort */ }
            e.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try
            {
                Helpers.AppLog.Crash("Unhandled domain exception", e.ExceptionObject as Exception);
            }
            catch { /* best-effort */ }
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        MainAppWindow = _window;
        _window.Activate();
        Program.NotifyWindowReady();
        if (Helpers.PackagedSmoke.Enabled) return;
        // Clean leftover Steam CEF surfaces from older hide/restore (blank taskbar spam).
        // Process and window enumeration stays off the UI thread and after first frame.
        _ = Task.Run(() =>
        {
            try { Adapters.StoreWindowHider.CollapseOrphanSurfaces(); } catch { /* */ }
        });
        // New DLSS, FSR and XeSS releases, looked for as Exo opens (DlssSwapService.VendorWatch.cs).
        ExoLauncher.Services.DlssSwapService.WatchUpscalerReleases();
    }

    public static MainWindow? MainAppWindow { get; set; }
}
