using System.Text.Json;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>
/// New versions of what this PC runs, found without opening the page that has them: the graphics driver (NVIDIA's Game
/// Ready, Studio or hotfix on the branch the driver row last showed, AMD's or Intel's), the chipset driver when Exo
/// doesn't install it by itself, and Lucid. Each check is the one its own row uses, with its sources and cache; this only
/// asks on Exo's clock (<see cref="WebHostBridge"/>'s version watch). Each version is told once: version-watch.json keeps
/// the last one told per item, so a restart doesn't tell it again and the next one does.
/// </summary>
internal static class VersionWatch
{
    /// <summary>A newer version than the one installed. Page is where its update is: graphics, overview (PC) or devices.</summary>
    internal sealed record Found(string Id, string Name, string Version, string? Installed, string Page);

    private static readonly Lock Gate = new();
    private static Dictionary<string, string>? _told;
    private static string ToldPath => Path.Combine(PathHelper.AppDataDir, "version-watch.json");

    /// <summary>Everything with a newer version out now; a check that can't tell (offline, no such hardware) adds nothing.</summary>
    internal static async Task<List<Found>> FindAsync(AppSettings settings, CancellationToken ct)
    {
        var found = new List<Found>();
        if (await DriverAsync(settings, ct).ConfigureAwait(false) is { } driver) found.Add(driver);
        // A chipset driver Exo installs while you're away (the default) needs no word from it.
        if (!settings.AutoUpdateChipset && ChipsetUpdate.ThisPc is { } chipset && await chipset.LatestAsync().WaitAsync(ct).ConfigureAwait(false) is { } package
            && PcHealth.ChipsetBehind(chipset.Installed(), package.Version))
            found.Add(new Found("chipset", PcHealth.ChipsetVendor(PcHealth.CpuName()) == "intel" ? "Intel chipset driver" : "AMD chipset driver", package.Version, chipset.Installed(), "overview"));
        if (LucidCompanion.Locate(settings.LucidPath) is { } lucid && await LucidCompanion.AvailableUpdateAsync(lucid).WaitAsync(ct).ConfigureAwait(false) is { } version)
            found.Add(new Found("lucid", "Lucid", version.ToString(), LucidCompanion.FileVersion(lucid)?.ToString(), "devices"));
        return found;
    }

    private static async Task<Found?> DriverAsync(AppSettings settings, CancellationToken ct)
    {
        var vendor = GraphicsService.Vendor() ?? GraphicsService.Vendors(GpuCapability.Adapters()).FirstOrDefault();
        if (vendor is "amd" or "intel")
        {
            if (GpuDriverUpdate.Card(vendor) is not { } card || await GpuDriverUpdate.LatestAsync(vendor, ct).ConfigureAwait(false) is not { } newest) return null;
            var have = GpuDriverUpdate.Installed(vendor, card);
            return have is not null && GpuDriverUpdate.Newer(newest.Version, have)
                ? new Found("driver." + vendor, vendor == "amd" ? "AMD Software: Adrenalin Edition" : "Intel graphics driver", newest.Version, have, "graphics")
                : null;
        }
        if (vendor != "nvidia") return null;
        var latest = await NvidiaDriverService.LatestAsync(settings.NvidiaDriverBranch == "studio" ? "studio" : "game", ct).ConfigureAwait(false);
        var installed = NvidiaDriverService.Installed();
        if (latest is null || installed is null || NvidiaDriverService.Compare(latest.Version, installed) <= 0) return null;
        var name = latest.Kind switch
        {
            "hotfix" => "NVIDIA hotfix driver",
            "studio" => "NVIDIA Studio driver",
            "security" => "NVIDIA security update",
            "workstation" => "NVIDIA RTX Enterprise driver",
            _ => "NVIDIA Game Ready driver",
        };
        return new Found("driver.nvidia", name, latest.Version, installed, "graphics");
    }

    /// <summary>True the first time a version is found for its item (and remembers it); false once it was told.</summary>
    internal static bool FirstTime(Found found)
    {
        lock (Gate)
        {
            var told = Told();
            if (told.TryGetValue(found.Id, out var before) && before == found.Version) return false;
            told[found.Id] = found.Version;
            try
            {
                var temp = ToldPath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(told));
                File.Move(temp, ToldPath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Debug("Versions: what was told wasn't saved: " + ex.Message); }
            return true;
        }
    }

    private static Dictionary<string, string> Told()
    {
        if (_told is not null) return _told;
        try
        {
            if (File.Exists(ToldPath) && JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(ToldPath)) is { } saved) return _told = saved;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { AppLog.Debug("Versions: what was told is unreadable: " + ex.Message); }
        return _told = [];
    }
}
