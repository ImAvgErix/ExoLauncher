using System.Runtime.InteropServices;
using ExoLauncher.Helpers;
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
        StartupTiming.Begin();
        try
        {
            Helpers.NativeProcessSecurity.HardenDllSearch();
            // Exo's uninstaller, before it deletes Exo: what Exo changed goes back, Windows' records of it go (no window).
            if (args.Contains("--uninstall-cleanup", StringComparer.OrdinalIgnoreCase))
            {
                Services.ExoUninstall.Run();
                return;
            }
            // Exo's setup, once Exo is installed: Exo's parts fetched and verified, with progress for its window (no window here).
            if (args.Contains("--fetch-components", StringComparer.OrdinalIgnoreCase))
            {
                Environment.ExitCode = Services.ExoComponents.RunFromSetup(args);
                return;
            }
            // Every program Exo starts (a store's launcher, a game) inherits this folder.
            // Inside the app folder it would hold it open after Exo exits, and the next
            // update could not swap the folder; Exo's data folder is never replaced.
            try { Environment.CurrentDirectory = Helpers.PathHelper.AppDataDir; } catch { }
            Helpers.WebViewRuntimeLocator.ClearStaleBrowserFolderPin();
            AppDomain.CurrentDomain.SetData("REGEX_DEFAULT_MATCH_TIMEOUT", TimeSpan.FromSeconds(5));
            PackagedSmoke.Configure(args);

            try { SetCurrentProcessExplicitAppUserModelID(AppUserModelId); } catch { }

            // Custom-main WinUI apps must initialize WinRT wrappers before the
            // first Windows App SDK API, including AppInstance.
            ComWrappersSupport.InitializeComWrappers();
            var currentInstance = AppInstance.GetCurrent();
            var mainInstance = AppInstance.FindOrRegisterForKey(PackagedSmoke.InstanceKey);
            if (!mainInstance.IsCurrent)
            {
                mainInstance
                    .RedirectActivationToAsync(currentInstance.GetActivatedEventArgs())
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();
                return;
            }
            // A text file Windows opens with Exo (Notes' --note "%1"): in Notes once the page asks, Exo starting with it.
            if (Services.NoteTextFiles.NoteArgument(args) is { } note) Services.WebHostBridge.QueueNoteFromWindows(note);
            mainInstance.Activated += (_, activated) =>
            {
                // Exo already open: the file opens in Notes now.
                if (activated.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch
                    && Services.NoteTextFiles.NoteArgument(Services.NoteTextFiles.Split(launch.Arguments)) is { } opened)
                    _ = Task.Run(() => App.MainAppWindow?.OpenNoteFromWindows(opened));
                Interlocked.Exchange(ref _restoreRequested, 1);
                TryDeliverRestore();
            };

            StartupTiming.MarkWinUiStart();
            XamlCheckProcessRequirements();
            Application.Start(p =>
            {
                var context = new DispatcherQueueSynchronizationContext(
                    DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                new App();
            });
            AppLog.Info("Shutdown: WinUI application loop completed.");
        }
        catch (Exception ex)
        {
            try
            {
                AppLog.Crash("Fatal startup exception", ex);
            }
            catch { /* best-effort */ }
            throw;
        }
    }

    internal static void NotifyWindowReady()
    {
        StartupTiming.LogWindowReady();
        TryDeliverRestore();
    }

    private static void TryDeliverRestore()
    {
        var window = App.MainAppWindow;
        if (window is null || Volatile.Read(ref _restoreRequested) == 0) return;
        _ = window.DispatcherQueue.TryEnqueue(static () =>
        {
            var current = App.MainAppWindow;
            if (current is not null && Interlocked.Exchange(ref _restoreRequested, 0) == 1)
                current.RestoreAndActivate();
        });
    }
}
