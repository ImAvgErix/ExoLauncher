using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using WinRT;

namespace ExoLauncher;

public static class Program
{
    private static int _restoreRequested;

    [DllImport("Microsoft.ui.xaml.dll")]
    private static extern void XamlCheckProcessRequirements();

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appID);

    public const string AppUserModelId = "ImAvgErix.ExoLauncher";

    [STAThread]
    public static void Main(string[] args)
    {
        try
        {
            Helpers.NativeProcessSecurity.HardenDllSearch();
            AppDomain.CurrentDomain.SetData("REGEX_DEFAULT_MATCH_TIMEOUT", TimeSpan.FromSeconds(5));
            _ = args;

            try { SetCurrentProcessExplicitAppUserModelID(AppUserModelId); } catch { }

            // Custom-main WinUI apps must initialize WinRT wrappers before the
            // first Windows App SDK API, including AppInstance.
            ComWrappersSupport.InitializeComWrappers();
            var currentInstance = AppInstance.GetCurrent();
            var mainInstance = AppInstance.FindOrRegisterForKey("ExoLauncher.Main");
            if (!mainInstance.IsCurrent)
            {
                mainInstance
                    .RedirectActivationToAsync(currentInstance.GetActivatedEventArgs())
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();
                return;
            }
            mainInstance.Activated += (_, _) =>
            {
                Interlocked.Exchange(ref _restoreRequested, 1);
                TryDeliverRestore();
            };

            XamlCheckProcessRequirements();
            Application.Start(p =>
            {
                var context = new DispatcherQueueSynchronizationContext(
                    DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                new App();
            });
        }
        catch (Exception ex)
        {
            try
            {
                var log = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ExoLauncher", "logs", "fatal.log");
                Directory.CreateDirectory(Path.GetDirectoryName(log)!);
                File.AppendAllText(log, $"[{DateTime.UtcNow:O}] {ex}{Environment.NewLine}");
            }
            catch { /* best-effort */ }
            throw;
        }
    }

    internal static void NotifyWindowReady() => TryDeliverRestore();

    private static void TryDeliverRestore()
    {
        var window = App.MainAppWindow;
        if (window is null || Volatile.Read(ref _restoreRequested) == 0) return;
        _ = window.DispatcherQueue.TryEnqueue(() =>
        {
            if (Interlocked.Exchange(ref _restoreRequested, 0) == 1)
                window.RestoreAndActivate();
        });
    }
}
