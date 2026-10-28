using System.Runtime.InteropServices;
using ExoLauncher.Helpers;

namespace ExoLauncher;

public sealed partial class MainWindow
{
    /// <summary>
    /// While a library game runs, however it was started, the screen stays on and the PC awake: Exo's power plan turns
    /// the screen off after 30 minutes without keyboard or mouse input (an OLED's burn-in), and a game played on a
    /// controller doesn't always ask Windows to stay awake itself. One Windows power request, named for Exo in
    /// powercfg /requests, held from the first game's start to the last one's end.
    /// </summary>
    private void StartGameAwake() => App.Services.Launcher.GameRunStateChanged += (_, _) => Task.Run(SyncGameAwake);

    private static readonly Lock GameAwakeGate = new();
    private static nint _gameAwake;

    private static void SyncGameAwake()
    {
        using var _ = GameAwakeGate.EnterScope();
        try
        {
            var playing = RunningLibraryGames().Count > 0;
            if (playing == (_gameAwake != 0)) return;
            if (playing)
            {
                var reason = new ReasonContext { Version = 0, Flags = SimpleString, Reason = "Exo: a game is running" };
                var request = PowerCreateRequest(ref reason);
                if (request is 0 or -1) { AppLog.Debug("Game awake: no power request: " + Marshal.GetLastPInvokeError()); return; }
                PowerSetRequest(request, DisplayRequired);
                PowerSetRequest(request, SystemRequired);
                _gameAwake = request;
                AppLog.Info("Game awake: the screen stays on while the game runs.");
            }
            else
            {
                PowerClearRequest(_gameAwake, DisplayRequired);
                PowerClearRequest(_gameAwake, SystemRequired);
                CloseHandle(_gameAwake);
                _gameAwake = 0;
                AppLog.Info("Game awake: no game runs; the power plan decides again.");
            }
        }
        catch (Exception ex) { AppLog.Debug("Game awake: " + ex.GetType().Name + ": " + ex.Message); }
    }

    private const uint SimpleString = 0x1; // POWER_REQUEST_CONTEXT_SIMPLE_STRING
    private const int DisplayRequired = 0, SystemRequired = 1; // POWER_REQUEST_TYPE

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ReasonContext
    {
        public uint Version;
        public uint Flags;
        [MarshalAs(UnmanagedType.LPWStr)] public string Reason;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint PowerCreateRequest(ref ReasonContext context);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerSetRequest(nint request, int type);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerClearRequest(nint request, int type);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
