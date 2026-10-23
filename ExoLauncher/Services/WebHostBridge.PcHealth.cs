using System.Diagnostics;
using System.Text.Json;

namespace ExoLauncher.Services;

/// <summary>
/// system.health: System > This PC (<see cref="PcHealth"/>). No action reads the checks; apply and undo
/// switch to Exo's power plan for this PC's processor and back (<see cref="PowerPlan"/>); chipset starts the AMD or
/// Intel chipset update now (<see cref="ChipsetUpdate"/>); gamemode opens Windows' Game Mode settings. Each answer
/// carries the checks as they are after it.
/// </summary>
public sealed partial class WebHostBridge
{
    private async Task<object> SystemHealthAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        var action = ReadString(p, hasParams, "action");
        var message = action switch
        {
            "apply" => await Task.Run(() => PowerPlan.Shared.Apply(PcHealth.ThisPcProfile()), ct).ConfigureAwait(false),
            "undo" => await Task.Run(PowerPlan.Shared.Undo, ct).ConfigureAwait(false),
            "chipset" => await Task.Run(StartChipsetUpdateAsync, ct).ConfigureAwait(false),
            "gamemode" => OpenGameModeSettings(),
            _ => null,
        };
        var reading = await Task.Run(() => PcHealth.ReadAsync(PowerPlan.Shared, _services.Settings.Current.AutoUpdateChipset, ct), ct).ConfigureAwait(false);
        return new
        {
            ok = message is null,
            message,
            items = PcHealth.Decide(reading).Select(item => new { key = item.Key, label = item.Label, detail = item.Detail, url = item.Url, applied = item.Applied, canUndo = item.CanUndo, busy = item.Busy, cpu = item.Cpu, current = item.Current }).ToList(),
        };
    }

    private static async Task<string?> StartChipsetUpdateAsync()
    {
        if (ChipsetUpdate.ThisPc is not { } chipset) return "Exo doesn't update this PC's chipset driver.";
        if (chipset.Installed() is not { } installed) return $"{chipset.Maker}'s chipset driver isn't installed on this PC.";
        return await chipset.LatestAsync().ConfigureAwait(false) is { } latest
            ? chipset.Start(installed, latest)
            : $"{chipset.Maker}'s page couldn't be read. Try again later.";
    }

    private static string? OpenGameModeSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:gaming-gamemode") { UseShellExecute = true });
            return null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Helpers.AppLog.Warn("PC health: Game Mode settings didn't open: " + ex.Message);
            return "Windows Settings didn't open.";
        }
    }
}
