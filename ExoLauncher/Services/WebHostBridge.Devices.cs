namespace ExoLauncher.Services;

/// <summary>
/// devices.list: the Devices room's cards (<see cref="Peripherals"/>), each with where it meets the PC and the processor's
/// free USB connectors (<see cref="UsbPorts"/>), read only; devices.changed (no payload) once a device arrives or leaves
/// or Windows' default speakers or microphone change, from the first list on.
/// </summary>
public sealed partial class WebHostBridge
{
    private int _devicesWatched;

    /// <summary>From Exo's start: devices.changed for the room, and an Edge Exo calibrated gets its calibration back.</summary>
    private void WatchDevices()
    {
        if (Interlocked.Exchange(ref _devicesWatched, 1) != 0) return;
        Peripherals.Watch(() =>
        {
            PostEvent("devices.changed", new { });
            HideControllerSoundSoon();
            ReapplyCalibrationsSoon();
        });
        HideControllerSoundSoon();
        ReapplyCalibrationsSoon();
    }

    private object DevicesList()
    {
        WatchDevices();
        var cards = Peripherals.List(() => _services.Launcher.AnyGameRunning || MainWindow.RunningLibraryGames().Count > 0);
        try
        {
            var (ports, cpu) = UsbPorts.Read(cards);
            return new { ok = true, devices = cards.Select(c => Labbed(ports.TryGetValue(c.Id, out var port) ? c with { Port = port } : c)).ToList(), cpuPorts = cpu };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The cards stand without their ports.
            Helpers.AppLog.Warn("Devices: ports not read: " + ex.Message);
            return new { ok = true, devices = cards.Select(Labbed).ToList(), cpuPorts = (UsbPorts.CpuPorts?)null };
        }
    }

    private static Peripherals.Peripheral Labbed(Peripherals.Peripheral card) => card.Kind == "controller" ? card with { Lab = ControllerLab.FamilyOf(card) is not null } : card;

    /// <summary>devices.openApp: a keyboard's own settings app, by the id its card carries (<see cref="Peripherals.OpenKeyboardApp"/>).</summary>
    private static object DevicesOpenApp(System.Text.Json.JsonElement p, bool hasParams) =>
        Peripherals.OpenKeyboardApp(ReadString(p, hasParams, "app")) is { } failed ? new { ok = false, message = failed } : new { ok = true };
}
