using System.Diagnostics;
using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using ExoLauncher.Models;
using ExoLauncher.Services;

namespace ExoLauncher.Helpers;

/// <summary>Opt-in release smoke using a disposable data directory and the packaged UI/native transport.</summary>
internal static class PackagedSmoke
{
    internal static bool Enabled { get; private set; }
    internal static string InstanceKey { get; private set; } = "ExoLauncher.Main";
    private static int _started;

    internal static void Configure(string[] args)
    {
        if (!args.Contains("--smoke-test", StringComparer.Ordinal)) return;
        var root = Environment.GetEnvironmentVariable(PathHelper.DataDirOverrideVariable);
        if (string.IsNullOrWhiteSpace(root) || !PathHelper.IsUnderRoot(root, Path.GetTempPath()) ||
            !Path.GetFileName(Path.TrimEndingDirectorySeparator(root)).StartsWith("exo-packaged-smoke-", StringComparison.Ordinal) ||
            !File.Exists(Path.Combine(root, ".exo-smoke-owner")))
            throw new InvalidOperationException("Packaged smoke requires an owned disposable test directory.");
        Enabled = true;
        InstanceKey = "ExoLauncher.Smoke." + Path.GetFileName(Path.TrimEndingDirectorySeparator(root));
    }

    internal static async Task RunAsync(CoreWebView2 core, MainWindow window)
    {
        if (!Enabled || Interlocked.Exchange(ref _started, 1) != 0) return;
        var watch = Stopwatch.StartNew();
        var checks = new List<string>();
        try
        {
            VerifyRecoveryFixtures(checks);
            await WaitForAsync(core, "!!document.querySelector('.exo-library-pane .exo-add-game-btn')");
            checks.Add("packaged-library");
            await core.ExecuteScriptAsync("document.querySelector('[aria-label=\"Open settings\"]').click()");
            await WaitForAsync(core, "!!document.querySelector('main.exo-settings')");
            await WaitForAsync(core, "Array.from(document.querySelectorAll('.exo-settings button')).some(b=>b.textContent.trim()==='Check for updates') && ['general','downloads','accounts','about'].every(id=>!!document.querySelector('#set-'+id+'-title'))");
            checks.Add("packaged-restored-settings");
            await WaitForAsync(core, "!!document.querySelector('[aria-label=\"itch.io API key\"]') && Array.from(document.querySelectorAll('.exo-settings button')).some(b=>b.textContent.trim()==='Support Exo')");
            checks.Add("itch-key-support-settings");
            await VerifyTaskbarCommandsAsync(window, checks);
            await core.ExecuteScriptAsync("document.querySelector('[aria-label=\"Home library\"]').click()");
            await WaitForAsync(core, "document.querySelector('[aria-label=\"Open settings\"]').getAttribute('aria-pressed')==='false'");
            checks.Add("return-to-library");
            await core.ExecuteScriptAsync("document.querySelector('.exo-add-game-btn').click()");
            await WaitForAsync(core, "Array.from(document.querySelectorAll('[role=menuitem]')).some(b=>b.textContent.includes('Activate key'))");
            await core.ExecuteScriptAsync("Array.from(document.querySelectorAll('[role=menuitem]')).find(b=>b.textContent.includes('Activate key')).click()");
            await WaitForAsync(core, "(() => { const p=document.querySelector('.exo-key-popover'); if(!p) return false; const r=p.getBoundingClientRect(); return r.left>=0 && r.top>=0 && r.right<=innerWidth && r.bottom<=innerHeight && p.parentElement===document.body; })()");
            checks.Add("activate-key-inside-window");
            File.WriteAllText(Path.Combine(PathHelper.AppDataDir, "smoke-result.json"), JsonSerializer.Serialize(new
            {
                ok = true, version = App.Services.AppVersion, elapsedMs = watch.ElapsedMilliseconds, checks,
            }));
        }
        catch (Exception ex)
        {
            AppLog.Crash("Packaged smoke failed", ex);
            File.WriteAllText(Path.Combine(PathHelper.AppDataDir, "smoke-result.json"), JsonSerializer.Serialize(new
            {
                ok = false, elapsedMs = watch.ElapsedMilliseconds, checks, message = AppLog.Redact(ex.Message),
            }));
            Environment.ExitCode = 1;
        }
        finally
        {
            window.CloseWindow();
            Microsoft.UI.Xaml.Application.Current.Exit();
        }
    }

    private static async Task WaitForAsync(CoreWebView2 core, string expression)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(30))
        {
            if (await core.ExecuteScriptAsync("Boolean(" + expression + ")") == "true") return;
            await Task.Delay(100);
        }
        throw new TimeoutException("Packaged page did not reach the expected state.");
    }

    private static async Task VerifyTaskbarCommandsAsync(MainWindow window, List<string> checks)
    {
        // Exercise the tray once so the notification-icon subclass is attached,
        // then send the same native system commands Windows uses for taskbar actions.
        window.HideForGameplay();
        window.RestoreAndActivate();
        await Task.Delay(150);
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        SendMessage(hwnd, 0x0112, 0xF020, 0); // WM_SYSCOMMAND / SC_MINIMIZE
        await Task.Delay(150);
        if (!IsWindowVisible(hwnd) || !IsIconic(hwnd))
            throw new InvalidOperationException("Native minimize was intercepted instead of remaining on the taskbar.");
        SendMessage(hwnd, 0x0112, 0xF120, 0); // SC_RESTORE
        await Task.Delay(150);
        if (!IsWindowVisible(hwnd) || IsIconic(hwnd))
            throw new InvalidOperationException("Native taskbar restore did not restore the window.");
        checks.Add("native-taskbar-minimize-and-restore-after-tray");
    }

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint hwnd);

    private static void VerifyRecoveryFixtures(List<string> checks)
    {
        // All paths are below the marker-validated disposable application data root.
        var root = Path.Combine(PathHelper.AppDataDir, "fixture-game");
        Directory.CreateDirectory(root);
        var destination = Path.Combine(root, "nvngx_dlss.dll");
        var backup = destination + DlssSwapService.BackupSuffix;
        var unrelated = Path.Combine(root, "keep.txt");
        File.WriteAllText(destination, "replacement");
        File.WriteAllText(backup, "original");
        File.WriteAllText(unrelated, "untouched");
        var game = new GameEntry { Id = "local:release-fixture", Title = "Release fixture", Store = StoreKind.Local, Installed = true, Path = root };
        using (var locked = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var blocked = new DlssSwapService().RestoreGame(game);
            if (blocked.Ok || File.ReadAllText(backup) != "original")
                throw new IOException("Locked restoration lost its recoverable backup.");
        }
        var restored = new DlssSwapService().RestoreGame(game);
        if (!restored.Ok || File.ReadAllText(destination) != "original" || File.ReadAllText(unrelated) != "untouched")
            throw new IOException("Fixture restoration could not recover after a locked destination.");
        checks.Add("locked-dll-restore-retry-preserves-unrelated-files");
    }
}
