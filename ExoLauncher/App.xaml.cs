using ExoLauncher.Services;
using Microsoft.UI.Xaml;

namespace ExoLauncher;

public partial class App : Application
{
    public static AppServices Services { get; } = new();
    private Window? _window;

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
            e.Handled = false;
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        MainAppWindow = _window;
        _window.Activate();
    }

    public static Window? MainAppWindow { get; set; }
}
