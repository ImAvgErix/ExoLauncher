using ExoLauncher.Adapters;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>
/// Steam kept current without its "Updating Steam" box in the way of Play, and on its Beta Update. Steam downloads its
/// client updates while it runs but installs them only as it next starts, so the box shows the next time you start a
/// game cold. Every few minutes Exo reads Steam's update log (<see cref="SteamClientUpdate"/>); once an update waits, it
/// starts Steam again while nothing plays, out of sight (closed with Steam's own -shutdown, started -silent with its
/// windows kept hidden), at most once an hour. Exo puts Steam on its Beta Update once, the way Steam's own Client Beta
/// Participation does (package\beta), and Steam switches at that same quiet start; leave the beta in Steam and Exo
/// leaves it too.
/// </summary>
public sealed partial class WebHostBridge
{
    private static readonly TimeSpan SteamClientPoll = TimeSpan.FromMinutes(5);
    /// <summary>Steam starts again for its client at most this often, so an update that keeps failing can't loop it.</summary>
    private static readonly TimeSpan SteamClientRestartEvery = TimeSpan.FromHours(1);

    private System.Threading.Timer? _steamClientPoll;
    private int _steamClientPolling;
    private DateTimeOffset _steamClientRestartAt = DateTimeOffset.MinValue;

    private void KeepSteamClientCurrent() =>
        _steamClientPoll = new System.Threading.Timer(_ => _ = SteamClientTickAsync(), null, TimeSpan.FromMinutes(2), SteamClientPoll);

    private async Task SteamClientTickAsync()
    {
        if (_detached)
        {
            Interlocked.Exchange(ref _steamClientPoll, null)?.Dispose();
            return;
        }
        if (Interlocked.Exchange(ref _steamClientPolling, 1) == 1) return;
        try
        {
            if (SteamInstallation.ResolveRoot() is not { } root) return;
            JoinSteamBetaOnce(root);
            if (!ProcessHelper.IsProcessRunning("steam") || DateTimeOffset.UtcNow - _steamClientRestartAt < SteamClientRestartEvery) return;
            if (SteamClientUpdate.RestartFor(SteamClientUpdate.ReadLog(root), SteamClientUpdate.ReadBeta(root)) is not { } why) return;
            if (SteamBusy() is { } busy)
            {
                AppLog.Info($"Steam: {why}; it starts again once {busy}.");
                return;
            }
            await RestartSteamForClientAsync(root, why).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("Steam: its client couldn't be checked: " + ex.GetType().Name);
        }
        finally { Volatile.Write(ref _steamClientPolling, 0); }
    }

    /// <summary>Steam's Beta Update, once: a Steam on its stable client joins; one already on a beta, or left by the player since, stays.</summary>
    private void JoinSteamBetaOnce(string root)
    {
        if (_services.Settings.Current.SteamBetaJoined) return;
        if (SteamClientUpdate.ReadBeta(root) is null)
        {
            SteamClientUpdate.JoinBeta(root);
            AppLog.Info("Steam: joined its Beta Update; it switches as Steam next starts.");
        }
        _services.Settings.UpdateProfile(s => s.SteamBetaJoined = true);
    }

    /// <summary>What keeps Steam from starting again now, or null: a game, a Play just pressed, Steam open on screen, Exo's offline mode.</summary>
    private string? SteamBusy()
    {
        if (SteamLoginUsers.IsGameRunning()) return "the Steam game closes";
        if (DateTimeOffset.UtcNow - _steamPlayAt < LaunchQuiet) return "the game you just started is up";
        if (StoreWindowHider.VisibleMainStores([StoreKind.Steam]).Contains(StoreKind.Steam)) return "Steam's window is closed";
        if (SteamLoginUsers.LoadOfflineSwitch() is not null) return "Steam is back online";
        return null;
    }

    private async Task RestartSteamForClientAsync(string root, string why)
    {
        var exe = Path.Combine(root, "steam.exe");
        if (!File.Exists(exe) || !await _steamRestartGate.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            if (SteamLoginUsers.IsGameRunning()) return;
            _steamClientRestartAt = DateTimeOffset.UtcNow;
            AppLog.Info($"Steam: {why}; starting it again out of sight while nothing plays.");
            if (!await CloseSteamAsync(exe, _bridgeLifetime.Token).ConfigureAwait(false)) return;
            StartSteamHidden(exe);
        }
        finally { _steamRestartGate.Release(); }
    }
}
