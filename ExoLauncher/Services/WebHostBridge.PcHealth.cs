using System.Diagnostics;
using System.Text.Json;

namespace ExoLauncher.Services;

/// <summary>
/// system.health: System > This PC (<see cref="PcHealth"/>). No action reads the checks; apply and undo
/// switch to Exo's power plan for this PC's processor and back (<see cref="PowerPlan"/>); chipset starts the AMD or
/// Intel chipset update now (<see cref="ChipsetUpdate"/>); gamemode and gpusched open Windows' Game Mode and Graphics settings. Each answer
/// carries the checks as they are after it. system.about: PC › Overview's About card (<see cref="PcAbout"/>), read only.
/// </summary>
public sealed partial class WebHostBridge
{
    private static async Task<object> SystemAboutAsync(CancellationToken ct) => await Task.Run(PcAbout.Read, ct).ConfigureAwait(false);

    private async Task<object> SystemHealthAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        var action = ReadString(p, hasParams, "action");
        var message = action switch
        {
            "apply" => await Task.Run(() => PowerPlan.Shared.Apply(PcHealth.ThisPcProfile()), ct).ConfigureAwait(false),
            "undo" => await Task.Run(PowerPlan.Shared.Undo, ct).ConfigureAwait(false),
            "chipset" => await Task.Run(StartChipsetUpdateAsync, ct).ConfigureAwait(false),
            "gpupref" => await Task.Run(UseFastGpuForGames, ct).ConfigureAwait(false),
            "gpupref-undo" => await Task.Run(UndoFastGpuForGames, ct).ConfigureAwait(false),
            "gamemode" => OpenWindowsSettings("ms-settings:gaming-gamemode", "Game Mode"),
            "gpusched" => OpenWindowsSettings("ms-settings:display-advancedgraphics", "Graphics"),
            _ => null,
        };
        var reading = await Task.Run(() => PcHealth.ReadAsync(PowerPlan.Shared, _services.Settings.Current.AutoUpdateChipset, _services.Settings.Current.FastGpuForGames, ct), ct).ConfigureAwait(false);
        return new
        {
            ok = message is null,
            message,
            items = PcHealth.Decide(reading).Select(item => new { key = item.Key, label = item.Label, detail = item.Detail, url = item.Url, applied = item.Applied, canUndo = item.CanUndo, busy = item.Busy, cpu = item.Cpu, current = item.Current }).ToList(),
        };
    }

    /// <summary>Graphics for games on: the setting, and every installed game's programs asked for the fast chip now (a game started later is set as it starts).</summary>
    private string? UseFastGpuForGames()
    {
        _services.Settings.UpdateProfile(s => s.FastGpuForGames = true);
        var changed = GpuPreference.Shared.Use(_services.Library.PeekCachedLibrary().Where(game => game.Installed).SelectMany(GpuPreference.ProgramsOf));
        Helpers.AppLog.Info($"GPU preference: the fast graphics chip asked for {changed} program(s).");
        return null;
    }

    private string? UndoFastGpuForGames()
    {
        _services.Settings.UpdateProfile(s => s.FastGpuForGames = false);
        var restored = GpuPreference.Shared.Undo();
        Helpers.AppLog.Info($"GPU preference: {restored} program(s) put back.");
        return null;
    }

    private static async Task<string?> StartChipsetUpdateAsync()
    {
        if (ChipsetUpdate.ThisPc is not { } chipset) return "Exo doesn't update this PC's chipset driver.";
        if (chipset.Installed() is not { } installed) return $"{chipset.Maker}'s chipset driver isn't installed on this PC.";
        return await chipset.LatestAsync().ConfigureAwait(false) is { } latest
            ? chipset.Start(installed, latest)
            : $"{chipset.Maker}'s page couldn't be read. Try again later.";
    }

    /// <summary>One of Windows' own Settings pages, by a fixed address (never one the page names); null when it opened.</summary>
    private static string? OpenWindowsSettings(string uri, string what)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            return null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Helpers.AppLog.Warn($"PC health: {what} settings didn't open: " + ex.Message);
            return "Windows Settings didn't open.";
        }
    }
}
