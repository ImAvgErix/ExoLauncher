using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>
/// Pause and hold. Pause stops the running download and keeps it as a paused
/// download that Resume continues (Legendary, gogdl and nile pick up partial
/// files). A hold (a game is running, or it is outside the download hours)
/// pauses the running download Exo drives itself and keeps every queued one
/// waiting; when the last hold lifts, what it paused resumes, oldest first,
/// and the queue moves again. Steam downloads are the Steam client's: its own
/// setting decides whether they continue while you play.
/// </summary>
public sealed partial class LaunchOrchestrator
{
    private readonly Dictionary<string, string> _holds = new(StringComparer.Ordinal);
    private readonly List<string> _heldTokens = [];

    /// <summary>Why downloads wait right now (the newest hold's reason), or null.</summary>
    public string? HoldReason
    {
        get { lock (_gate) return _holds.Count == 0 ? null : _holds.Values.Last(); }
    }

    internal bool IsBusy
    {
        get { lock (_gate) return _activeJob is not null; }
    }

    /// <summary>Jobs waiting to run. Paused downloads are not counted: they wait for the user.</summary>
    internal bool HasQueuedJobs
    {
        get { lock (_gate) return _pendingJobs.Count > 0; }
    }

    internal bool AnyGameRunning
    {
        get { lock (_gate) return _activeOrLaunchingGames.Count > 0; }
    }

    /// <summary>The downloaders Exo runs itself, which Pause can stop and Resume continue.</summary>
    internal static bool ExoDownloads(StoreKind store) => store is StoreKind.Epic or StoreKind.Gog or StoreKind.Amazon;

    /// <summary>The user's Pause on the running download.</summary>
    public object Pause(string gameId)
    {
        JobState paused;
        lock (_gate)
        {
            if (_activeJob is not { } job || !string.Equals(job.GameId, gameId, StringComparison.OrdinalIgnoreCase))
                return new { ok = false, message = "That download is not running." };
            if (job.DownloadIntent is null || !ExoDownloads(job.Store))
                return new { ok = false, message = "This store's own app runs this download; pause it there." };
            paused = PauseActiveLocked("Paused.");
        }
        CancelJob(paused);
        return new { ok = true, message = "Paused." };
    }

    /// <summary>Downloads wait for <paramref name="reason"/> under <paramref name="key"/> until <see cref="Release"/> with the same key.</summary>
    internal void Hold(string key, string reason)
    {
        JobState? paused = null;
        lock (_gate)
        {
            var first = _holds.Count == 0;
            _holds[key] = reason;
            if (_activeJob is { DownloadIntent: { } intent } job && ExoDownloads(job.Store) && job.PauseReason is null)
            {
                _heldTokens.Add(intent.Token);
                paused = PauseActiveLocked(reason, held: true);
            }
            if (first) AppLog.Info("Downloads held: " + reason);
            PublishDownloadQueueChangedLocked();
        }
        if (paused is not null) CancelJob(paused);
    }

    internal void Release(string key)
    {
        List<string> resume;
        lock (_gate)
        {
            if (!_holds.Remove(key)) return;
            PublishDownloadQueueChangedLocked();
            if (_holds.Count > 0) return;
            resume = _heldTokens
                .Select(token => _downloadIntents.TryGetValue(token, out var intent) && _pausedDownloadTokens.Contains(token) ? intent.GameId : null)
                .OfType<string>().ToList();
            _heldTokens.Clear();
            AppLog.Info($"Downloads released; resuming {resume.Count} paused download(s).");
        }
        // Off the caller's thread, each resume claims its place before the queue
        // moves, so what the hold paused goes first, oldest first.
        _ = Task.Run(() =>
        {
            foreach (var gameId in resume)
                _ = ResumeDownloadAsync(gameId).ContinueWith(task =>
                {
                    if (task.IsFaulted) AppLog.Info("Resume after hold failed: " + task.Exception?.GetBaseException().Message);
                    else if (!task.Result.Ok && !task.Result.Queued) AppLog.Info($"Resume after hold: gameId={gameId}; {task.Result.Message}");
                }, TaskScheduler.Default);
            DrainQueue();
        });
    }

    /// <summary>
    /// Marks the running job paused with <paramref name="reason"/> (under _gate)
    /// and returns it; the caller cancels it with <see cref="CancelJob"/> after
    /// leaving the lock.
    /// </summary>
    private JobState PauseActiveLocked(string reason, bool held = false)
    {
        var job = _activeJob!;
        job.PauseReason = reason;
        job.CancelRequested = true;
        FinishDownloadIntentLocked(job.DownloadIntent, pause: true, reason, held: held);
        AppLog.Info($"Download paused: gameId={job.GameId}; {reason}");
        PublishProgressLocked(new InstallProgress
        {
            GameId = job.GameId,
            Phase = _lastProgress.IsActive ? _lastProgress.Phase : InstallPhase.Preparing,
            Percent = _lastProgress.Percent,
            Status = "Pausing…",
            CanCancel = false,
        });
        return job;
    }
}
