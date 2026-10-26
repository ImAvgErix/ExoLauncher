namespace ExoLauncher.Services;

/// <summary>
/// devices.list: the Devices room's cards (<see cref="Peripherals"/>), read only; devices.changed (no payload) once a
/// device arrives or leaves or Windows' default speakers or microphone change, from the first list on.
/// </summary>
public sealed partial class WebHostBridge
{
    private int _devicesWatched;

    private object DevicesList()
    {
        if (Interlocked.Exchange(ref _devicesWatched, 1) == 0) Peripherals.Watch(() => PostEvent("devices.changed", new { }));
        return new { ok = true, devices = Peripherals.List(() => _services.Launcher.AnyGameRunning || MainWindow.RunningLibraryGames().Count > 0) };
    }
}
