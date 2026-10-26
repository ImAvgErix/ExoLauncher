using ExoLauncher.Helpers;
using ExoLauncher.Services;

namespace ExoLauncher;

public sealed partial class MainWindow
{
    /// <summary>
    /// With Graphics for games on (System › This PC, a PC with two graphics chips), a game Exo starts gets the fast chip first: its
    /// programs are set in Windows' graphics preferences before the store launches it. Cheap and on this thread, so it is done
    /// before the game exists.
    /// </summary>
    private void StartGpuPreference() => App.Services.Launcher.GameLaunching += OnGpuLaunching;

    private static void OnGpuLaunching(Models.GameEntry game)
    {
        if (!App.Services.Settings.Current.FastGpuForGames) return;
        try
        {
            if (GpuPreference.Shared.Use(GpuPreference.ProgramsOf(game)) is > 0 and var changed)
                AppLog.Info($"GPU preference: {changed} program(s) of {game.Title} set to the fast graphics chip.");
        }
        catch (Exception ex) { AppLog.Warn("GPU preference for a starting game: " + ex.GetType().Name + ": " + ex.Message); }
    }
}
