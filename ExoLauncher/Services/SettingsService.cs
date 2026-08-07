using System.Text.Json;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public AppSettings Current { get; private set; } = new();

    public void Load()
    {
        try
        {
            if (!File.Exists(PathHelper.SettingsPath)) return;
            var json = File.ReadAllText(PathHelper.SettingsPath);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts);
            if (loaded is not null)
            {
                // Anti-cheat safe mode is always on — never honor a false from disk.
                loaded.AntiCheatSafeMode = true;
                Current = loaded;
            }
        }
        catch { /* defaults */ }
    }

    public void Save()
    {
        try
        {
            Current.AntiCheatSafeMode = true;
            File.WriteAllText(PathHelper.SettingsPath, JsonSerializer.Serialize(Current, JsonOpts));
        }
        catch { /* best-effort */ }
    }

    public void Flush() => Save();

    public void ApplyPatch(bool? closeStore, bool? autoRedist, bool? minimizeWhilePlaying)
    {
        if (closeStore is not null) Current.CloseStoreClientsAfterLaunch = closeStore.Value;
        if (autoRedist is not null) Current.AutoInstallRedistributables = autoRedist.Value;
        if (minimizeWhilePlaying is not null) Current.MinimizeWhilePlaying = minimizeWhilePlaying.Value;
        Current.AntiCheatSafeMode = true;
        Save();
    }
}
