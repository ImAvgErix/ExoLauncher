using System.Text.Json;
using System.Text.Json.Nodes;
using ExoLauncher.Helpers;

namespace ExoLauncher.Adapters;

/// <summary>
/// The new Epic Games Launcher's own switches for its Windows notifications
/// and its What's New page on start (settings\app-settings.json in its data
/// folder: "enableNotifications", "showWhatsNewOnStartup"). Turned off while
/// Exo starts Epic in your place (its start with Windows handed to Exo), with
/// the values they had kept beside Exo's settings. Written only while Epic is closed: it keeps
/// its settings in memory while it runs.
/// </summary>
internal static class EpicQuietSettings
{
    internal static readonly string[] Keys = ["enableNotifications", "showWhatsNewOnStartup"];

    private static string BeforePath => Path.Combine(PathHelper.AppDataDir, "epic-quiet.json");
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>The settings with every key off, and what was on before; null when nothing changes.</summary>
    internal static (string Json, Dictionary<string, bool> Before)? Quiet(string json)
    {
        if (JsonNode.Parse(json) is not JsonObject settings) return null;
        var before = new Dictionary<string, bool>();
        foreach (var key in Keys)
        {
            if (settings[key] is not JsonValue value || !value.TryGetValue<bool>(out var on) || !on) continue;
            before[key] = true;
            settings[key] = false;
        }
        return before.Count == 0 ? null : (settings.ToJsonString(Indented), before);
    }

    /// <summary>Epic's start is Exo's now: quiet it now if Epic is closed, else the next time Exo starts it.</summary>
    internal static void HandToExo()
    {
        try { if (!File.Exists(BeforePath)) WriteAtomic(BeforePath, "{}"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        ApplyIfHanded();
    }

    /// <summary>Before Exo starts Epic: its switches off, if Epic's start was handed to Exo.</summary>
    internal static void ApplyIfHanded()
    {
        if (!File.Exists(BeforePath) || SettingsPath() is not { } path || EpicRunning()) return;
        try
        {
            if (Quiet(File.ReadAllText(path)) is not { } quiet) return;
            var before = JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(BeforePath)) ?? [];
            foreach (var (key, on) in quiet.Before) before.TryAdd(key, on);
            WriteAtomic(BeforePath, JsonSerializer.Serialize(before));
            WriteAtomic(path, quiet.Json);
            AppLog.Info("Epic: its notifications and What's New page are off while Exo starts it.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Warn("Epic: settings not changed: " + ex.Message);
        }
    }

    private static string? SettingsPath()
    {
        var path = EpicLauncherV2.DataDir() is { } dir ? Path.Combine(dir, "settings", "app-settings.json") : null;
        return path is not null && File.Exists(path) ? path : null;
    }

    private static bool EpicRunning() => StoreWindowHider.EpicProcessNames.Any(ProcessHelper.IsProcessRunning);

    private static void WriteAtomic(string path, string json)
    {
        var staged = path + ".exo-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(staged, json);
        File.Move(staged, path, overwrite: true);
    }
}
