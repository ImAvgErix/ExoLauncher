using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinRT;

namespace ExoLauncher;

public static class Program
{
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

            try { SetCurrentProcessExplicitAppUserModelID(AppUserModelId); } catch { }

            XamlCheckProcessRequirements();
            ComWrappersSupport.InitializeComWrappers();
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
}
