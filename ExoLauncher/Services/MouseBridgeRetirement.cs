using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// Earlier Exo answered the OpenMouse web panel with a bridge of its own, and
/// Settings > Devices > Use Exo's bridge stopped the OpenMouse Bridge app and
/// removed its Windows sign-in entry. Devices is now Exo Mouse, which talks
/// to the mouse itself over HID and never uses a bridge, so Exo's bridge is
/// gone. Once, on the first start after that: when Exo had taken over from the
/// app (settings "mouseBridge": "exo"), Exo's saved profiles go into the
/// app's config.json while the app is not running, the way Undo did. Its
/// sign-in entry is not put back: Exo Mouse talks to the mouse now, and
/// two programs driving one mouse conflict. Then the three settings are
/// forgotten. The app is never started here.
/// </summary>
internal static class MouseBridgeRetirement
{
    private const int MaxBytes = 4 * 1024 * 1024;

    private static string StorePath => Path.Combine(PathHelper.AppDataDir, "openmouse-bridge.json");

    private static string AppConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenMouse", "OpenMouse Bridge", "config", "config.json");

    internal static void Run(SettingsService settings)
    {
        var current = settings.Current;
        if (current.MouseBridge is null && current.MouseBridgeAppAutostart is null && current.MouseBridgeAppPath is null) return;
        if (current.MouseBridge == "exo")
        {
            // A file the app holds open is tried again on the next start.
            if (!WriteProfilesBack()) return;
        }
        settings.ForgetMouseBridge();
        AppLog.Info("OpenMouse: Exo's bridge is retired; Devices is Exo Mouse. The OpenMouse Bridge app was not started.");
    }

    /// <summary>
    /// Exo's profiles and default profile into the app's config.json (every
    /// other field as the app wrote it, replaced atomically), then Exo's copy
    /// is deleted. Nothing is written while the app runs (it keeps its own in
    /// memory) or when it has no config; Exo's copy then stays. False only
    /// when the write failed and should be tried again.
    /// </summary>
    private static bool WriteProfilesBack()
    {
        var store = StorePath;
        var config = AppConfigPath;
        try
        {
            if (!File.Exists(store)) return true;
            if (AppRunning())
            {
                AppLog.Info("OpenMouse Bridge app is running; its own profiles stay, and Exo's copy stays in openmouse-bridge.json.");
                return true;
            }
            if (!File.Exists(config))
            {
                AppLog.Info("OpenMouse Bridge app has no config to take Exo's profiles; Exo's copy stays in openmouse-bridge.json.");
                return true;
            }
            if (new FileInfo(store).Length > MaxBytes || new FileInfo(config).Length > MaxBytes ||
                JsonNode.Parse(File.ReadAllBytes(store)) is not JsonObject saved ||
                JsonNode.Parse(File.ReadAllBytes(config)) is not JsonObject app)
            {
                AppLog.Warn("OpenMouse: Exo's profiles or the app's config did not read; both left as they are.");
                return true;
            }
            var profiles = saved["profiles"] as JsonArray ?? [];
            app["profiles"] = profiles.DeepClone();
            app["defaultProfile"] = saved["defaultProfile"]?.DeepClone();
            var temporary = config + ".exo.tmp";
            File.WriteAllText(temporary, app.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, config, overwrite: true);
            File.Delete(store);
            AppLog.Info($"OpenMouse Bridge app: Exo's {profiles.Count} profile(s) written back to its config.");
            return true;
        }
        catch (JsonException)
        {
            AppLog.Warn("OpenMouse: Exo's profiles or the app's config is not valid JSON; both left as they are.");
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("OpenMouse: Exo's profiles were not written back yet (" + error.GetType().Name + "); trying again next start.");
            return false;
        }
    }

    private static bool AppRunning()
    {
        var running = false;
        foreach (var process in Process.GetProcessesByName("openmouse-bridge"))
            using (process) running = true;
        return running;
    }
}
