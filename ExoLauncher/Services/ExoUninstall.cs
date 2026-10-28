using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// What Exo's uninstaller asks of Exo before it deletes it (ExoLauncher.exe --uninstall-cleanup, tools/ExoLauncher.nsi):
/// what Exo changed for this user goes back, and Windows' records of Exo go, as Exo's own deep clean leaves another app.
/// Back: the store launchers and apps Exo kept from starting with Windows start with it again (StartupApps), and
/// Graphics for games' choices return to what each program held (GpuPreference). Gone: shortcuts pointing at Exo (a taskbar
/// pin included), Windows' name cache, compatibility, recent apps and UserAssist records of Exo.exe, its notification
/// settings (<see cref="Program.AppUserModelId"/>), its crash dumps, and the parts it fetched beside itself (the store tools
/// and models, <see cref="ExoComponents"/>: a reinstall fetches them again). Exo's own folder is the uninstaller's, its data
/// stays (a reinstall keeps settings); nothing machine-wide is touched without Windows' permission, so nothing is. Exo's power
/// plan is put back by the uninstaller itself first.
/// </summary>
internal static class ExoUninstall
{
    private static bool SteamOpen() => System.Diagnostics.Process.GetProcessesByName("steam").Length > 0;

    /// <summary>Asks Steam to close as its own Exit does and waits up to half a minute; never a kill.</summary>
    private static void CloseSteam(string steamRoot)
    {
        var exe = Path.Combine(steamRoot, "steam.exe");
        if (!File.Exists(exe)) return;
        try
        {
            using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "-shutdown") { UseShellExecute = false });
            for (var waited = 0; waited < 30_000 && SteamOpen(); waited += 500) Thread.Sleep(500);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { AppLog.Warn("Uninstall: Steam not asked to close: " + ex.GetType().Name); }
    }

    internal static void Run()
    {
        AppLog.Info("Uninstall: putting back what Exo changed.");
        try
        {
            var handed = Handed();
            var started = StartupApps.GiveBack(handed);
            if (started.Count > 0) AppLog.Info("Uninstall: " + string.Join(", ", started) + " start with Windows again.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or JsonException)
        {
            AppLog.Warn("Uninstall: startup apps not given back: " + ex.GetType().Name);
        }
        try
        {
            var restored = GpuPreference.Shared.Undo();
            if (restored > 0) AppLog.Info($"Uninstall: Graphics for games put {restored} programs back.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            AppLog.Warn("Uninstall: Graphics for games not undone: " + ex.GetType().Name);
        }
        try
        {
            // Steam put offline by Exo through an outage goes back as it was. Steam rewrites its file as it closes, so it's
            // written only while Steam is closed; a Steam that's open is asked to close first (its own -shutdown), since
            // once Exo is gone nothing is left to put it back and Steam would start offline from then on.
            if (Adapters.SteamLoginUsers.LoadOfflineSwitch() is not null && Adapters.SteamInstallation.ResolveRoot() is { } steam)
            {
                if (SteamOpen()) CloseSteam(steam);
                AppLog.Info(SteamOpen() ? "Uninstall: Steam didn't close; its offline mode stays until it is switched off in Steam."
                    : Adapters.SteamLoginUsers.WriteOnline(steam) ? "Uninstall: Steam starts online again." : "Uninstall: Steam's offline mode couldn't be put back.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            AppLog.Warn("Uninstall: Steam's offline mode not put back: " + ex.GetType().Name);
        }
        // Exo offered for text files (Notes): out of Open with and Default apps again.
        NoteTextFiles.Withdraw();
        ExoComponents.RemoveAll();
        var app = AppContext.BaseDirectory.TrimEnd('\\');
        var scan = new LeftoverScan(LeftoverScan.ThisPc());
        var found = scan.Find(new Trail("Exo Launcher")
        {
            Homes = [app], Exes = ["ExoLauncher.exe"], Ids = [Program.AppUserModelId], Keep = [PathHelper.AppDataDir],
        }, deep: false).Where(item => !item.Machine && !item.Likely).ToList();
        var gone = found.Count(scan.Delete);
        AppLog.Info($"Uninstall: cleared {gone} of Windows' {found.Count} records of Exo.");
    }

    // The ids Exo turned off (settings.json’s startupHandedToExo), read without starting Exo's services.
    private static HashSet<string> Handed()
    {
        if (!File.Exists(PathHelper.SettingsPath)) return [];
        using var settings = JsonDocument.Parse(File.ReadAllText(PathHelper.SettingsPath));
        return settings.RootElement.TryGetProperty("startupHandedToExo", out var ids) && ids.ValueKind == JsonValueKind.Array
            ? ids.EnumerateArray().Where(id => id.ValueKind == JsonValueKind.String).Select(id => id.GetString()!).ToHashSet(StringComparer.Ordinal) : [];
    }
}
