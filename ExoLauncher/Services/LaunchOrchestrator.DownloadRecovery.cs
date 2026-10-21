using ExoLauncher.Adapters;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

public sealed partial class LaunchOrchestrator
{
    private PendingDownloadStore? _downloadIntentStore;
    private readonly Dictionary<string, DownloadIntent> _downloadIntents = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pausedDownloadTokens = new(StringComparer.Ordinal);
    private Func<GameEntry, bool> _isVendorDownloadRunning = _ => false;
    private string? _downloadRecoveryError;
    public event Action? DownloadQueueChanged;

    private void PublishDownloadQueueChangedLocked()
    {
        try { DownloadQueueChanged?.Invoke(); } catch { /* Queue persistence must not depend on a view. */ }
    }

    /// <summary>Saved state of a download a hold paused: it carries on by itself, after the hold or a restart.</summary>
    internal const string HeldState = "held";

    private const string RestartReason = "Paused after restart. Resume checks the store account, ownership, and current download state.";

    /// <summary>Downloads that were running or queued when Exo last closed, oldest first.</summary>
    private readonly List<string> _restartTokens = new();

    private void InitializeDownloadRecovery(PendingDownloadStore? store, Func<GameEntry, bool>? isVendorDownloadRunning)
    {
        _downloadIntentStore = store;
        // Steam keeps its own download queue and Exo attaches to a download it
        // already runs, so Steam never blocks a resume. Its downloading folder
        // also keeps any cancelled partial download, which blocked every resume.
        // Exo's own Legendary, gogdl and nile runs (the scan the resume itself
        // starts, the owned-list refresh) are not someone else's download.
        _isVendorDownloadRunning = isVendorDownloadRunning ?? (game =>
            game.Store != StoreKind.Steam &&
            (Find(game.Store)?.GetDownloadProgress(game.Id).IsActive == true ||
             StoreClientActivity.IsKnownTransferProcessRunning(game.Store, Adapters.Cli.CliRunner.IsRunningOutsideExo)));
        foreach (var intent in store?.Load() ?? [])
        {
            // Saved as running or queued, paused by a hold (a game running, the download hours), or
            // paused only because Exo restarted. One the user paused or that needs review keeps its reason.
            var restart = intent.State is "pending" or "running" or HeldState || intent.ReviewReason == RestartReason;
            _downloadIntents[intent.Token] = intent with
            {
                State = "review",
                ReviewReason = restart ? RestartReason : intent.ReviewReason,
            };
            _pausedDownloadTokens.Add(intent.Token);
            if (restart) _restartTokens.Add(intent.Token);
        }
    }

    /// <summary>
    /// Picks the downloads that were running or queued when Exo closed back up,
    /// in their original order. Each one still passes every check a manual
    /// Resume makes (same store account, still owned, not already installed);
    /// one that fails stays paused with its reason and the rest carry on.
    /// Downloads paused for review before the restart are left for the user.
    /// </summary>
    public int ResumeAfterRestart()
    {
        string[] ids;
        lock (_gate)
        {
            ids = _restartTokens
                .Where(token => _pausedDownloadTokens.Contains(token) && _downloadIntents.ContainsKey(token))
                .Select(token => _downloadIntents[token])
                .Where(CanResumeAccount)
                .OrderBy(intent => intent.CreatedAtUtc)
                .Select(intent => intent.GameId)
                .ToArray();
            _restartTokens.Clear();
        }
        // Each call claims its place synchronously, so the order holds: the
        // first starts and the rest queue behind it.
        foreach (var gameId in ids)
        {
            _ = ResumeDownloadAsync(gameId).ContinueWith(task =>
            {
                if (task.IsFaulted) AppLog.Warn($"Resume after restart failed: gameId={gameId}; {task.Exception?.GetBaseException().Message}");
                else if (!task.Result.Ok && !task.Result.Queued) AppLog.Info($"Resume after restart: gameId={gameId}; {task.Result.Message}");
            }, TaskScheduler.Default);
        }
        if (ids.Length > 0) AppLog.Info($"Resumed {ids.Length} saved download(s) after restart.");
        return ids.Length;
    }

    public IReadOnlyList<PausedDownload> PausedDownloads
    {
        get
        {
            lock (_gate)
                return _downloadIntents.Values.Where(intent => _pausedDownloadTokens.Contains(intent.Token))
                    .OrderBy(intent => intent.CreatedAtUtc).Select(intent => new PausedDownload(
                        intent.GameId, intent.Title, intent.Store.ToString().ToLowerInvariant(), intent.Action,
                        CanResumeAccount(intent),
                        IsOfficialCatalogLinkIntent(intent)
                            ? "Open the game's download again to retry the official download link, or remove this saved request."
                            : string.IsNullOrWhiteSpace(intent.AccountScope)
                            ? "Paused for review. Check this title in its store, then remove this saved request before starting a fresh action."
                            : !CanResumeAccount(intent)
                            ? "Reconnect the original account to resume, or remove this request and start a fresh action."
                            : intent.ReviewReason ?? "Paused. Review before resuming.")).ToArray();
        }
    }

    private bool CanResumeAccount(DownloadIntent intent)
    {
        if (string.IsNullOrWhiteSpace(intent.AccountScope) || Find(intent.Store) is not IStoreAccountScope scoped)
            return false;
        try { return string.Equals(scoped.GetActiveAccountScope(), intent.AccountScope, StringComparison.Ordinal); }
        catch { return false; }
    }

    public async Task<InstallResult> ResumeDownloadAsync(string gameId, CancellationToken ct = default)
    {
        DownloadIntent? intent;
        lock (_gate)
            intent = _downloadIntents.Values.FirstOrDefault(value => _pausedDownloadTokens.Contains(value.Token) &&
                string.Equals(value.GameId, gameId, StringComparison.OrdinalIgnoreCase));
        if (intent is null) return new InstallResult { Ok = false, Message = "That saved download is no longer paused." };
        var game = new GameEntry
        {
            Id = intent.GameId, Title = intent.Title, Store = intent.Store,
            LaunchTarget = intent.LaunchTarget, Path = intent.GamePath,
        };
        return await RunJobAsync(game, (current, adapter, progress, token) => intent.Action switch
            {
                "install" => adapter.InstallAsync(current, intent.InstallRoot, progress, token),
                "update" => adapter.UpdateAsync(current, progress, token),
                _ => Task.FromResult(new InstallResult { Ok = false, Message = "That saved action is unsupported." }),
            }, ct, downloadIntent: intent, resumeRecoveredIntent: true).ConfigureAwait(false);
    }

    private DownloadIntent? CreateDownloadIntent(GameEntry game, string action, string? installRoot, string? accountScope)
    {
        if (game.Store == StoreKind.Local || Find(game.Store) is not { } adapter) return null;
        return new DownloadIntent
        {
            GameId = game.Id, Title = game.Title, Store = game.Store, SourceId = adapter.Id,
            LaunchTarget = game.LaunchTarget, GamePath = game.Path, Action = action,
            InstallRoot = installRoot, AccountScope = accountScope,
        };
    }

    private static bool IsOfficialCatalogLinkIntent(DownloadIntent intent)
    {
        // These exact catalog targets open a public download page. A saved
        // client update, account-bound install, or filesystem target still
        // requires the normal ownership/recovery checks.
        if (intent.Action != "install" || !string.IsNullOrWhiteSpace(intent.AccountScope) ||
            !string.IsNullOrWhiteSpace(intent.GamePath)) return false;
        var catalog = OfficialClientCatalog.TrySynthesizeOfficialClientInstall(intent.GameId, null);
        return catalog is not null && catalog.Store == intent.Store &&
            string.Equals(catalog.Id, intent.GameId, StringComparison.Ordinal) &&
            string.Equals(catalog.LaunchTarget, intent.LaunchTarget, StringComparison.Ordinal) &&
            string.Equals(intent.SourceId, catalog.Store == StoreKind.Minecraft ? "minecraft" : "roblox", StringComparison.Ordinal);
    }

    private bool PrepareDownloadIntentLocked(DownloadIntent? intent, bool resume, bool queued, out string? error)
    {
        error = null;
        if (intent is null) return true;
        var existing = _downloadIntents.Values.FirstOrDefault(value => _pausedDownloadTokens.Contains(value.Token) &&
            string.Equals(value.GameId, intent.GameId, StringComparison.OrdinalIgnoreCase));
        // A fresh authorized click may replace a legacy paused public-link
        // handoff. Never replay it at startup or relax resume authorization.
        // The same request clicked again (same game, store, action and store
        // account) is the user authorizing it afresh: it replaces the stale
        // paused copy instead of refusing with no way to clear it from the sheet.
        var replaceCatalogLink = !resume && existing is not null &&
            ((IsOfficialCatalogLinkIntent(existing) && IsOfficialCatalogLinkIntent(intent)) ||
             (existing.Store == intent.Store &&
              string.Equals(existing.Action, intent.Action, StringComparison.Ordinal) &&
              string.Equals(existing.AccountScope, intent.AccountScope, StringComparison.Ordinal)));
        if (existing is not null && (!resume || existing.Token != intent.Token) && !replaceCatalogLink)
        {
            error = "This title has a paused download. Resume it or remove the saved request first.";
            return false;
        }
        if (resume && existing is null &&
            (!_downloadIntents.TryGetValue(intent.Token, out var accepted) || accepted.State != "pending"))
        {
            error = "That saved download is no longer paused.";
            return false;
        }
        var next = intent with { State = queued ? "pending" : "running", ReviewReason = null };
        var replacedToken = replaceCatalogLink ? existing!.Token : null;
        var snapshot = _downloadIntents.Values.Where(value => value.Token != intent.Token && value.Token != replacedToken).Append(next).ToArray();
        if (_downloadIntentStore is not null && !_downloadIntentStore.TrySave(snapshot, out error)) return false;
        if (replacedToken is not null)
        {
            _downloadIntents.Remove(replacedToken);
            _pausedDownloadTokens.Remove(replacedToken);
        }
        _downloadIntents[intent.Token] = next;
        _pausedDownloadTokens.Remove(intent.Token);
        PublishDownloadQueueChangedLocked();
        return true;
    }

    private void FinishDownloadIntentLocked(DownloadIntent? intent, bool pause, string? reason, bool haltQueue = false,
        bool held = false)
    {
        if (intent is null) return;
        if (pause)
        {
            _downloadIntents[intent.Token] = intent with { State = held ? HeldState : "review", ReviewReason = TrimReviewReason(reason) };
            _pausedDownloadTokens.Add(intent.Token);
            // An unconfirmed vendor handoff cannot safely release the next
            // queued mutation. Preserve downloads for explicit review and
            // settle nonserializable repair/uninstall waiters without replay.
            while (haltQueue && _pendingJobs.TryDequeue(out var pending))
            {
                if (pending.DownloadIntent is { } saved)
                {
                    _downloadIntents[saved.Token] = saved with
                    {
                        State = "review", ReviewReason = "Paused because the previous store action needs review.",
                    };
                    _pausedDownloadTokens.Add(saved.Token);
                }
                pending.Completion?.TrySetResult(new InstallResult
                {
                    Ok = false, Message = "The queue paused because the previous store action needs review.",
                });
            }
        }
        else
        {
            _downloadIntents.Remove(intent.Token);
            _pausedDownloadTokens.Remove(intent.Token);
        }
        SaveDownloadIntentsLocked();
        PublishDownloadQueueChangedLocked();
    }

    private void SaveDownloadIntentsLocked()
    {
        if (_downloadIntentStore is not null && !_downloadIntentStore.TrySave(_downloadIntents.Values.ToArray(), out _downloadRecoveryError))
            AppLog.Warn(_downloadRecoveryError ?? "Could not persist the download queue.");
    }

    private static string? TrimReviewReason(string? reason) => reason is { Length: > 2048 } ? reason[..2048] : reason;

    private async Task<(GameEntry Game, InstallResult? Outcome)> RevalidateRecoveredDownloadAsync(
        DownloadIntent intent, GameEntry placeholder, IStoreAdapter adapter, CancellationToken ct)
    {
        InstallResult Blocked(string message) => new() { Ok = false, Message = message };
        if (adapter.Id != intent.SourceId || adapter.Store != intent.Store)
            return (placeholder, Blocked("The saved store source changed. Remove this request and choose the current source."));
        if (adapter is not IStoreAccountScope scoped || string.IsNullOrWhiteSpace(intent.AccountScope) ||
            scoped.GetActiveAccountScope() != intent.AccountScope)
            return (placeholder, Blocked("The saved store account cannot be verified. Reconnect the original account, or remove this request and start a fresh action."));
        if (_revalidateQueuedGame is null)
            return (placeholder, Blocked("Current ownership could not be revalidated. Refresh the store and retry."));
        var current = await _revalidateQueuedGame(intent.GameId, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (current is null || current.Id != intent.GameId || current.Store != intent.Store ||
            current.LaunchTarget != intent.LaunchTarget ||
            intent.Action == "update" && !string.Equals(current.Path, intent.GamePath, StringComparison.OrdinalIgnoreCase))
            return (placeholder, Blocked("The saved game's source or installation changed. Remove this request and choose the current source."));
        if (scoped.GetActiveAccountScope() != intent.AccountScope)
            return (current, Blocked("The store account changed while this request was checked. Retry after reconnecting the original account."));
        if (!current.Owned || current.EntitlementState is EntitlementState.NotOwned or EntitlementState.Unverified)
            return (current, Blocked("Current ownership could not be verified for this saved download."));
        if (_isVendorDownloadRunning(current))
            return (current, Blocked("The store is already running a download. Wait for it to finish before resuming this saved request."));
        if (intent.Action == "install" && current.Installed ||
            intent.Action == "update" && current.Installed && !current.UpdateAvailable)
            return (current, new InstallResult { Ok = true, Message = "The store already completed this request. The saved download was removed.", Path = current.Path });
        if (intent.Action == "install" && !current.CanInstall || intent.Action == "update" && !current.Installed)
            return (current, Blocked("This saved action is no longer available for the current installation."));
        return (current, null);
    }
}
