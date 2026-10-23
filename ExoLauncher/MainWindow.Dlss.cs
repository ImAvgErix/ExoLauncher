using System.Runtime.InteropServices;
using ExoLauncher.Helpers;
using ExoLauncher.Services;

namespace ExoLauncher;

/// <summary>
/// Whether Exo's DLSS override reached a game: while a library game runs, the game in front is asked
/// every 15 s (for its first 10 minutes) what the driver's override did in it (NvAPI_NGX_GetNGXOverrideState),
/// and the first answer that shows DLSS running goes to app.log once per run: overridden or the game's own,
/// the preset and frame generation it got.
/// </summary>
public sealed partial class MainWindow
{
    private static readonly TimeSpan DlssLook = TimeSpan.FromSeconds(15);
    private const int DlssLooks = 40;
    private System.Threading.Timer? _dlssWatch;
    private int _dlssLooksLeft;
    private readonly HashSet<string> _dlssLogged = new(StringComparer.OrdinalIgnoreCase);

    private void StartDlssCheck()
    {
        if (!NvidiaProfiles.Available) return;
        _dlssWatch = new System.Threading.Timer(_ => LookAtDlss(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        App.Services.Launcher.GameRunStateChanged += (gameId, state) =>
        {
            if (!state.IsRunning) { lock (_dlssLogged) _dlssLogged.Remove(gameId); return; }
            Volatile.Write(ref _dlssLooksLeft, DlssLooks);
            try { _dlssWatch?.Change(DlssLook, DlssLook); } catch (ObjectDisposedException) { }
        };
    }

    private void LookAtDlss()
    {
        try
        {
            var running = RunningLibraryGames().Keys.ToList();
            if (running.Count == 0 || Interlocked.Decrement(ref _dlssLooksLeft) < 0) { _dlssWatch?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan); return; }
            GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
            if (pid == 0 || pid == Environment.ProcessId || NvidiaProfiles.DlssState((int)pid) is not { } state || !NvidiaProfiles.RunsDlss(state)) return;
            var games = App.Services.Library.PeekCachedLibrary();
            var game = running.Count == 1 ? running[0] : null; // with several running, the one in front isn't known by id
            lock (_dlssLogged) if (!_dlssLogged.Add(game ?? pid.ToString(System.Globalization.CultureInfo.InvariantCulture))) return;
            var title = games.FirstOrDefault(g => string.Equals(g.Id, game, StringComparison.OrdinalIgnoreCase))?.Title ?? "the game in front";
            AppLog.Info($"DLSS in {title}: {NvidiaProfiles.Describe(state)}.");
        }
        catch (Exception ex) { AppLog.Debug("DLSS check failed: " + ex.GetType().Name); }
    }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
}
