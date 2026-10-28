using ExoLauncher.Helpers;
using ExoLauncher.Services;

namespace ExoLauncher;

public sealed partial class MainWindow
{
    /// <summary>
    /// Call of Duty asks to start in safe mode, which resets its settings (Optimize's with them), when the marker of a
    /// session that didn't quit cleanly is still there (<see cref="GameSettings.ClearLeftoverMarker"/>). Exo clears it
    /// as such a game ends, however it was started, so even a start from Steam doesn't ask; and again just before Exo
    /// starts one, for a game that ended while Exo was closed.
    /// </summary>
    private void StartSafeModeGuard()
    {
        App.Services.Launcher.GameLaunching += game => ClearSafeModeMarker(game, "starts");
        App.Services.Launcher.GameRunStateChanged += (gameId, state) =>
        {
            if (state.IsRunning || App.Services.Library.Find(gameId) is not { } game) return;
            // The game removes it itself as it quits cleanly: look once it has had the time to.
            _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ => ClearSafeModeMarker(game, "ended"), TaskScheduler.Default);
        };
    }

    private static void ClearSafeModeMarker(Models.GameEntry game, string when)
    {
        try
        {
            if (GameSettings.ClearLeftoverMarker(game))
                AppLog.Info($"Safe mode: {game.Title} {when} after a session that didn't quit cleanly; its marker is cleared, so it won't ask.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"Safe mode: {game.Title}'s marker couldn't be cleared: {ex.GetType().Name}");
        }
    }
}
