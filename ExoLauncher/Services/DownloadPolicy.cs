using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>
/// Settings > Downloads, applied: downloads wait while a game runs and
/// outside the download hours (<see cref="LaunchOrchestrator.Hold"/>), and
/// games from the stores Exo downloads itself (Epic, GOG, Amazon) update by
/// themselves once the PC is really idle (<see cref="PcIdle"/>: nobody at it,
/// nothing playing, no call, no other program at work, no game, not on battery),
/// and so does AMD's or Intel's chipset driver (<see cref="ChipsetUpdate"/>).
/// Steam updates and pauses its own downloads.
/// </summary>
internal sealed class DownloadPolicy : IDisposable
{
    private const string Playing = "playing";
    private const string Hours = "hours";

    private readonly AppServices _services;
    private readonly object _gate = new();
    private Timer? _tick;
    private DateTime _lastUpdateSweep = DateTime.MinValue;

    internal DownloadPolicy(AppServices services) => _services = services;

    internal void Start()
    {
        var launcher = _services.Launcher;
        launcher.GameLaunched += OnGameLaunched;
        launcher.GameSessionCompleted += OnGameEnded;
        lock (_gate) _tick ??= new Timer(_ => Tick(), null, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1));
        Apply();
    }

    /// <summary>The settings changed: holds follow them at once.</summary>
    internal void Apply()
    {
        var settings = _services.Settings.Current;
        if (!settings.PauseDownloadsWhilePlaying) _services.Launcher.Release(Playing);
        ApplyHours(settings);
    }

    private void OnGameLaunched(GameEntry game)
    {
        if (_services.Settings.Current.PauseDownloadsWhilePlaying)
            _services.Launcher.Hold(Playing, $"Downloads are paused while you play {game.Title}.");
    }

    private void OnGameEnded(GameEntry game)
    {
        // Another game may still run; the next tick releases once none does.
        if (!_services.Launcher.AnyGameRunning) _services.Launcher.Release(Playing);
    }

    private void Tick()
    {
        try
        {
            var settings = _services.Settings.Current;
            if (!_services.Launcher.AnyGameRunning) _services.Launcher.Release(Playing);
            ApplyHours(settings);
            if (settings.AutoUpdateGames) SweepUpdates();
            if (settings.AutoUpdateChipset) UpdateChipset();
        }
        catch (Exception ex)
        {
            AppLog.Debug("Download policy tick failed: " + ex.Message);
        }
    }

    private void ApplyHours(AppSettings settings)
    {
        if (settings.DownloadHoursStart is not { } start || settings.DownloadHoursEnd is not { } end || InHours(DateTime.Now.Hour, start, end))
        {
            _services.Launcher.Release(Hours);
            return;
        }
        _services.Launcher.Hold(Hours, $"Downloads start at {Clock(start)}, your download hours.");
    }

    /// <summary>9 to 17 is a day window; 1 to 7, or 22 to 6, wraps past midnight.</summary>
    internal static bool InHours(int hour, int start, int end) =>
        start < end ? hour >= start && hour < end : hour >= start || hour < end;

    internal static string Clock(int hour) =>
        new DateTime(2000, 1, 1, hour, 0, 0).ToString("h tt", System.Globalization.CultureInfo.CurrentCulture).Replace(" ", " ");

    /// <summary>Queues the updates Exo downloads itself, at most every 30 minutes, only while idle and nothing else downloads.</summary>
    private void SweepUpdates()
    {
        var launcher = _services.Launcher;
        if (DateTime.UtcNow - _lastUpdateSweep < TimeSpan.FromMinutes(30)) return;
        if (launcher.AnyGameRunning || launcher.HoldReason is not null || !Idle()) return;
        // A paused download waits for the user; it must not turn auto-update off for every other game.
        if (launcher.HasQueuedJobs || launcher.IsBusy) return;
        _lastUpdateSweep = DateTime.UtcNow;
        var paused = launcher.PausedDownloads.Select(p => p.GameId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var due = _services.Library.PeekCachedLibrary()
            .Where(g => g.Installed && g.UpdateAvailable && LaunchOrchestrator.ExoDownloads(g.Store) && !paused.Contains(g.Id))
            .ToList();
        if (due.Count == 0) return;
        AppLog.Info($"Auto-update while idle: {due.Count} game(s): {string.Join(", ", due.Select(g => g.Title))}");
        foreach (var game in due)
            _ = Task.Run(async () =>
            {
                try { await launcher.UpdateAsync(game).ConfigureAwait(false); }
                catch (Exception ex) { AppLog.Info($"Auto-update of {game.Title} failed: {ex.Message}"); }
            });
    }

    /// <summary>AMD's or Intel's chipset driver, in the same idle window as games (<see cref="ChipsetUpdate"/> adds its own gates).</summary>
    private void UpdateChipset()
    {
        if (!Idle() || ChipsetUpdate.ThisPc is not { } chipset || chipset.Installed() is null) return;
        // The maker's page at most every 12 hours; this tick uses the answer once it's in.
        var latest = chipset.LatestAsync();
        if (latest.IsCompletedSuccessfully) chipset.Tick(true, idle: true, latest.Result, ChipsetUpdate.BootedAt());
    }

    /// <summary>The PC is really idle (<see cref="PcIdle"/>), with Exo's own word on a game running.</summary>
    private bool Idle() => PcIdle.Now(() => _services.Launcher.AnyGameRunning);

    public void Dispose()
    {
        lock (_gate) { _tick?.Dispose(); _tick = null; }
    }
}
