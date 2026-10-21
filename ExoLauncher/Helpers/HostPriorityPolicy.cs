namespace ExoLauncher.Helpers;

/// <summary>
/// While an Exo-launched game is running and the launcher window is
/// inactive, the host yields foreground scheduling to the game, except while
/// live sound runs in it (a call): a host below normal puts the call's audio
/// threads, and noise suppression's, below every thread of the game.
/// </summary>
internal static class HostPriorityPolicy
{
    internal static bool Wanted(bool gameRunning, bool windowActive, bool liveAudio = false) => gameRunning && !windowActive && !liveAudio;
}
