using System.Text.Json;

namespace ExoLauncher.Services;

/// <summary>
/// system.interrupts: System > This PC > Device interrupts (<see cref="InterruptMode"/>). No action reads the devices;
/// apply switches every device that can to MSI mode, undo puts every one back, revert one (by its token). Each answer
/// carries the devices as they are after it.
/// </summary>
public sealed partial class WebHostBridge
{
    private static readonly InterruptMode PcInterrupts = new(new InterruptMode.NativeDevices(), new InterruptMode.ElevatedWriter(),
        Path.Combine(Helpers.PathHelper.AppDataDir, "msi-mode-backup.json"), InterruptMode.BootTime, () => DateTimeOffset.UtcNow);

    private static async Task<object> SystemInterruptsAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        var action = ReadString(p, hasParams, "action");
        var device = ReadString(p, hasParams, "device");
        var message = await Task.Run(() => action switch
        {
            "apply" => PcInterrupts.Apply(),
            "undo" => PcInterrupts.Undo(null),
            "revert" when !string.IsNullOrEmpty(device) => PcInterrupts.Undo(device),
            _ => null,
        }, ct).ConfigureAwait(false);
        var state = await Task.Run(PcInterrupts.Read, ct).ConfigureAwait(false);
        return new
        {
            ok = message is null,
            message,
            restart = state.Restart,
            result = state.Result,
            canUndo = state.CanUndo,
            devices = state.Devices.Select(d => new
            {
                id = d.Token,
                name = d.Name,
                kind = d.Kind,
                mode = d.Mode.ToString().ToLowerInvariant(),
                plan = d.Plan.ToString().ToLowerInvariant(),
                reason = d.Reason,
                changed = d.Changed,
            }).ToList(),
        };
    }
}
