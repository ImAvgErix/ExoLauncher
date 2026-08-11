using ExoLauncher.Adapters;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>
/// detect ownership → resolve deps → install/update with progress →
/// launch backend minimized → launch game → optional store-UI cleanup.
/// </summary>
public sealed class LaunchOrchestrator
{
    private readonly IReadOnlyList<IStoreAdapter> _adapters;
    private readonly SettingsService _settings;
    private readonly DependencyService _deps;
    private readonly AchievementService _achievements;
    private readonly GameProcessRegistry _runningGames = new();
    private readonly Func<GameEntry, CancellationToken, Task<GameStopResult>> _stopGame;
    private readonly Func<StoreKind, IDisposable> _beginQuietGameSession;
    private readonly object _gate = new();
    private JobState? _activeJob;
    private readonly HashSet<string> _activeOrLaunchingGames = new(StringComparer.OrdinalIgnoreCase);
    // A launch becomes a session as soon as Exo reserves the game id, not only
    // once the delayed watcher begins. That lets Stop cancel a just-started
    // handoff before the watcher has observed its first game process.
    private readonly Dictionary<string, GameSessionState> _gameSessions =
        new(StringComparer.OrdinalIgnoreCase);
    private InstallProgress _lastProgress = new();

    private sealed class JobState(CancellationTokenSource cts, string gameId)
    {
        public CancellationTokenSource Cts { get; } = cts;
        public string GameId { get; } = gameId;
        public bool CancelRequested { get; set; }
    }

    private sealed class GameSessionState(GameEntry game)
    {
        private readonly object _stateGate = new();
        private IDisposable? _quietGameSession;
        private int _watching;
        private bool _completed;
        private int _launchSucceeded;

        public GameEntry Game { get; } = game;
        public CancellationTokenSource StopCts { get; } = new();
        public TaskCompletionSource CleanupCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsStopRequested => StopCts.IsCancellationRequested;
        public bool IsWatching => Volatile.Read(ref _watching) != 0;
        public bool LaunchSucceeded => Volatile.Read(ref _launchSucceeded) != 0;

        public void MarkLaunchSucceeded() => Volatile.Write(ref _launchSucceeded, 1);

        public bool TryAttachQuietSession(IDisposable quietGameSession)
        {
            ArgumentNullException.ThrowIfNull(quietGameSession);
            lock (_stateGate)
            {
                if (_completed || _quietGameSession is not null)
                    return false;
                _quietGameSession = quietGameSession;
                return true;
            }
        }

        public bool TryBeginWatching()
        {
            lock (_stateGate)
            {
                if (_completed)
                    return false;
                Volatile.Write(ref _watching, 1);
                return true;
            }
        }

        public void RequestStop()
        {
            try { StopCts.Cancel(); } catch (ObjectDisposedException) { }
        }

        public bool TryComplete(out IDisposable? quietGameSession)
        {
            lock (_stateGate)
            {
                if (_completed)
                {
                    quietGameSession = null;
                    return false;
                }
                _completed = true;
                quietGameSession = _quietGameSession;
                _quietGameSession = null;
                return true;
            }
        }
    }

    public event Action<InstallProgress>? ProgressChanged;
    public event Action<GameEntry>? GameSessionCompleted;

    public LaunchOrchestrator(
        IReadOnlyList<IStoreAdapter> adapters,
        SettingsService settings,
        DependencyService deps,
        AchievementService? achievements = null)
        : this(adapters, settings, deps, achievements, stopGame: null)
    {
    }

    internal LaunchOrchestrator(
        IReadOnlyList<IStoreAdapter> adapters,
        SettingsService settings,
        DependencyService deps,
        AchievementService? achievements,
        Func<GameEntry, CancellationToken, Task<GameStopResult>>? stopGame,
        Func<StoreKind, IDisposable>? beginQuietGameSession = null)
    {
        _adapters = adapters;
        _settings = settings;
        _deps = deps;
        _achievements = achievements ?? new AchievementService();
        _stopGame = stopGame ?? _runningGames.StopAsync;
        _beginQuietGameSession = beginQuietGameSession ?? Adapters.HiddenStoreRuntime.GameSession;
    }

    public InstallProgress CurrentProgress
    {
        get
        {
            lock (_gate) return _lastProgress;
        }
    }

    public bool IsBusy
    {
        get
        {
            // Cancellation is only a request. A vendor backend may ignore its
            // token while it finishes touching manifests/files, so the job
            // remains busy until that task has actually unwound.
            lock (_gate) return _activeJob is not null;
        }
    }

    public async Task<LaunchResult> LaunchAsync(GameEntry game, bool skipDeps = false, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested)
            return new LaunchResult { Ok = false, Message = "Launch cancelled." };

        var adapter = Find(game.Store);
        if (adapter is null)
            return new LaunchResult { Ok = false, Message = $"No adapter for store {game.Store}." };

        if (game.Id.StartsWith("mock:", StringComparison.OrdinalIgnoreCase))
        {
            return new LaunchResult
            {
                Ok = false,
                Message = "This is a demo library entry. Install the real title, then refresh.",
            };
        }

        var session = new GameSessionState(game);
        lock (_gate)
        {
            if (_activeJob is not null)
                return new LaunchResult { Ok = false, Message = "Another install, update, or uninstall is already running." };
            if (!_activeOrLaunchingGames.Add(game.Id))
                return new LaunchResult { Ok = false, Message = $"{game.Title} is already running or starting." };
            _gameSessions[game.Id] = session;
        }

        var keepSessionRegistration = false;
        try
        {
            if (!skipDeps && _settings.Current.AutoInstallRedistributables)
            {
                var missing = _deps.GetMissingRequired(game);
                if (missing.Count > 0)
                {
                    return new LaunchResult
                    {
                        Ok = false,
                        NeedsDependencies = true,
                        MissingDependencies = missing,
                        Message = "Install required: " + string.Join(", ", missing.Select(d => d.Name)),
                    };
                }
            }

            var options = BuildOptions();
            IDisposable? quietGameSession = null;
            using var launchCts = CancellationTokenSource.CreateLinkedTokenSource(ct, session.StopCts.Token);
            try
            {
                // Exo is driving: suppression is active for the duration of the launch.
                using var driving = Adapters.HiddenStoreRuntime.Operation();
                // Register the provider before sibling cleanup starts. The cleanup
                // gate uses this registration to ensure an active or concurrently
                // launching provider is never closed or terminated.
                quietGameSession = _beginQuietGameSession(game.Store);
                // Shut down the store clients this game does not need.
                _ = CloseUnusedStoreClientsAsync(game.Store);

                var result = await adapter.LaunchAsync(game, options, launchCts.Token).ConfigureAwait(false);
                if (result.Ok)
                {
                    // Stop may have closed a process while a vendor adapter was
                    // still returning from its handoff. Never resurrect that
                    // stopped session or re-enter Quiet Game Mode afterwards.
                    if (session.IsStopRequested)
                    {
                        return new LaunchResult { Ok = false, Message = "Launch stopped." };
                    }
                    _runningGames.ObserveLaunch(game, result.ProcessId);
                    try { _settings.RecordLaunch(game.Id); } catch { /* */ }
                    try { PlaytimeService.BeginSession(game.Id); } catch { /* */ }
                    var achievementSession = _achievements.BeginSessionAsync(game, CancellationToken.None);
                    // Keep every vendor window suppressed for the complete game
                    // session, not only the initial handoff. This catches delayed
                    // store popups and keeps Exo as the sole visible launcher.
                    var ownedGameSession = quietGameSession;
                    quietGameSession = null;
                    // Clean up store UI after handoff / game exit; also ends
                    // playtime and releases the full-session suppression scope.
                    if (!session.TryAttachQuietSession(ownedGameSession))
                    {
                        ownedGameSession.Dispose();
                        return new LaunchResult { Ok = false, Message = "Launch stopped." };
                    }
                    session.MarkLaunchSucceeded();
                    keepSessionRegistration = true;
                    _ = ScheduleCleanupAsync(
                        adapter,
                        game,
                        options,
                        result.ProcessId,
                        achievementSession,
                        session);
                }
                return result;
            }
            catch (Exception ex)
            {
                return new LaunchResult { Ok = false, Message = ex.Message };
            }
            finally
            {
                // Failed/cancelled handoffs never leak an active-provider guard.
                quietGameSession?.Dispose();
            }
        }
        finally
        {
            if (!keepSessionRegistration)
            {
                CompleteGameSession(session);
            }
        }
    }

    private static async Task CloseUnusedStoreClientsAsync(StoreKind keep)
    {
        try
        {
            // Hide first so graceful shutdown cannot flash store chrome.
            StoreClientCleanup.HideUnused(keep);
            var report = await StoreClientCleanup.ExitUnusedAsync(keep).ConfigureAwait(false);
            if (report.GracefulStoreRequests > 0)
            {
                Helpers.AppLog.Info(
                    $"Quiet Game Mode exited {report.GracefulStoreRequests} unused store clients " +
                    $"(kept {keep}; hidden clients still running: {report.RemainingStoreClients}).");
            }
        }
        catch
        {
            /* best-effort */
        }
    }

    private async Task ScheduleCleanupAsync(
        IStoreAdapter adapter,
        GameEntry game,
        LaunchOptions options,
        int? processId,
        Task<AchievementSnapshot> achievementSession,
        GameSessionState session)
    {
        if (!session.TryBeginWatching())
            return;

        var credited = false;
        var stopped = false;
        try
        {
            // Brief settle so protocol handoff can complete before we hide store chrome.
            await Task.Delay(2500, session.StopCts.Token).ConfigureAwait(false);

            // Steam playtime comes from localconfig.vdf; Epic/Riot/Local rely on
            // Exo session minutes. Never cancel just because a bootstrap PID died —
            // keep watching install path / known process names through handoff.
            var processNames = game.Store == StoreKind.Riot &&
                               !string.IsNullOrWhiteSpace(game.LaunchTarget)
                ? RiotAdapter.GameProcessNames(game.LaunchTarget)
                : null;
            var isLeagueHandoff = game.Store == StoreKind.Riot &&
                                  game.LaunchTarget?.Trim().ToLowerInvariant() is "league_of_legends" or "lion";
            var handoffNames = isLeagueHandoff
                ? RiotAdapter.LaunchReadyProcessNames(game.LaunchTarget!)
                : null;
            var ignored = BootstrapProcessNames(game.Store);
            var hasTrackableSession = processId is > 0 ||
                                      !string.IsNullOrWhiteSpace(game.Path) ||
                                      processNames is { Length: > 0 };
            if (hasTrackableSession)
                credited = await ProcessHelper.TrackGameSessionAsync(
                    processId,
                    game.Path,
                    processNames,
                    ignored,
                    // League opens a persistent lobby before a match. Keep
                    // waiting while that handoff is alive instead of
                    // dropping playtime and Quiet Game Mode after 90s. A
                    // cold launch still has to produce that handoff soon,
                    // otherwise the user must be able to retry.
                    appearTimeout: isLeagueHandoff ? TimeSpan.FromHours(4) : TimeSpan.FromSeconds(90),
                    goneDebounce: TimeSpan.FromSeconds(12),
                    handoffProcessNames: handoffNames,
                    handoffAppearTimeout: isLeagueHandoff ? TimeSpan.FromSeconds(90) : null,
                    observedSeedGoneGrace: game.Store is StoreKind.Gog or StoreKind.Local
                        ? TimeSpan.FromSeconds(12)
                        : null,
                    ct: session.StopCts.Token)
                    .ConfigureAwait(false);
            session.StopCts.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (session.IsStopRequested)
        {
            // A successful Stop must not wait for the normal 2.5s handoff,
            // 12s exit debounce, or 90s appearance timeout before it releases
            // launch ownership and vendor-client suppression.
            stopped = true;
        }
        catch (Exception ex)
        {
            AppLog.Debug($"Playtime session watch failed for '{game.Title}': {ex.Message}");
        }
        finally
        {
            try
            {
                try
                {
                    if (credited) PlaytimeService.EndSession(game.Id);
                    else PlaytimeService.CancelSession(game.Id);
                }
                catch { /* */ }
                // Steam updates localconfig after play — pick it up on the next library scan.
                try { SteamPlaytime.Invalidate(); } catch { /* */ }

                try
                {
                    if (stopped || session.IsStopRequested)
                    {
                        // Do not make Stop wait on a provider refresh. It is safe to
                        // reconcile it in the background after the foreground
                        // session has already released its ownership.
                        _ = FinalizeStoppedAchievementSessionAsync(game.Id, achievementSession);
                    }
                    else
                    {
                        _ = await achievementSession.ConfigureAwait(false);
                        _ = await _achievements.EndSessionAsync(game.Id, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Debug($"Achievement session finalization failed for '{game.Title}': {ex.Message}");
                }

                if (!stopped && !session.IsStopRequested && options.CloseStoreUiAfterExit)
                    await adapter.CleanupAfterExitAsync(game, options).ConfigureAwait(false);
            }
            catch
            {
                /* best-effort cleanup */
            }
            finally
            {
                CompleteGameSession(session);
            }
        }
    }

    private async Task FinalizeStoppedAchievementSessionAsync(
        string gameId,
        Task<AchievementSnapshot> achievementSession)
    {
        try
        {
            _ = await achievementSession.ConfigureAwait(false);
            _ = await _achievements.EndSessionAsync(gameId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Debug($"Achievement session finalization failed after stop for '{gameId}': {ex.Message}");
        }
    }

    private void CompleteGameSession(GameSessionState session)
    {
        if (!session.TryComplete(out var quietGameSession))
            return;

        lock (_gate)
        {
            if (_gameSessions.TryGetValue(session.Game.Id, out var current) &&
                ReferenceEquals(current, session))
            {
                _gameSessions.Remove(session.Game.Id);
                _activeOrLaunchingGames.Remove(session.Game.Id);
            }
        }
        session.CleanupCompleted.TrySetResult();

        // Stopping a game is logically complete once its exact process is gone,
        // playtime/achievement cleanup has crossed the watcher finally block,
        // and launch ownership has been released. A WinEvent hook can take time
        // to leave its native message pump, so never keep the game.stop RPC (and
        // the UI's Closing state) hostage to disposal of the quiet-client guard.
        // Ref-counted suppression makes overlap with an immediate replay safe.
        if (quietGameSession is not null)
            _ = DisposeQuietGameSessionAsync(quietGameSession, session.Game.Title);

        if (!session.LaunchSucceeded)
            return;
        var handlers = GameSessionCompleted?.GetInvocationList();
        if (handlers is null)
            return;
        foreach (var handler in handlers)
        {
            try { ((Action<GameEntry>)handler)(session.Game); }
            catch (Exception ex) { AppLog.Debug("Game session listener failed: " + ex.Message); }
        }
    }

    private static async Task DisposeQuietGameSessionAsync(IDisposable quietGameSession, string gameTitle)
    {
        try
        {
            await Task.Run(quietGameSession.Dispose).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Debug($"Quiet Game Mode cleanup failed for '{gameTitle}': {ex.Message}");
        }
    }

    internal static string[] BootstrapProcessNames(StoreKind store) => store switch
    {
        StoreKind.Epic =>
        [
            "EpicGamesLauncher", "EpicWebHelper", "CrashReportClient",
            "Launcher", "EasyAntiCheat", "EasyAntiCheat_EOS",
            "EpicOnlineServices", "EOSOverlayRenderer-Win64-Shipping",
        ],
        StoreKind.Steam =>
        [
            "steam", "steamwebhelper", "steamservice", "GameOverlayUI",
            "SteamErrorReporter",
        ],
        StoreKind.Riot =>
        [
            "RiotClientServices", "Riot Client", "RiotClientCrashHandler",
            "RiotClientUx", "RiotClientUxRender", "LeagueClient",
            "LeagueClientUx", "LeagueClientUxRender",
        ],
        StoreKind.Gog =>
        [
            "GalaxyClient", "GalaxyClient Service", "GOG Galaxy Notifications",
        ],
        _ => [],
    };

    public async Task<InstallResult> InstallAsync(
        GameEntry game,
        string? path = null,
        bool skipDeps = false,
        CancellationToken outer = default)
    {
        // local:add is a real portable-install entry (not mock:*).
        if (game.Id.StartsWith("mock:", StringComparison.OrdinalIgnoreCase))
        {
            return new InstallResult
            {
                Ok = false,
                Message = "Demo entry — install uses the real store backend when the title is discovered. For portable games use “Add portable game”.",
            };
        }

        if (!skipDeps && _settings.Current.AutoInstallRedistributables)
        {
            var missing = _deps.GetMissingRequired(game);
            if (missing.Count > 0)
            {
                return new InstallResult
                {
                    Ok = false,
                    NeedsDependencies = true,
                    MissingDependencies = missing,
                    Message = "Install required: " + string.Join(", ", missing.Select(d => d.Name)),
                };
            }
        }

        path ??= PathHelper.GamesRoot;

        return await RunJobAsync(game, async (adapter, progress, ct) =>
            await adapter.InstallAsync(game, path, progress, ct).ConfigureAwait(false), outer).ConfigureAwait(false);
    }

    public async Task<InstallResult> UpdateAsync(GameEntry game, bool skipDeps = false, CancellationToken outer = default)
    {
        if (!skipDeps && _settings.Current.AutoInstallRedistributables)
        {
            var missing = _deps.GetMissingRequired(game);
            if (missing.Count > 0)
            {
                return new InstallResult
                {
                    Ok = false,
                    NeedsDependencies = true,
                    MissingDependencies = missing,
                    Message = "Install required: " + string.Join(", ", missing.Select(d => d.Name)),
                };
            }
        }

        return await RunJobAsync(game, async (adapter, progress, ct) =>
            await adapter.UpdateAsync(game, progress, ct).ConfigureAwait(false), outer).ConfigureAwait(false);
    }

    public Task<InstallResult> UninstallAsync(GameEntry game, CancellationToken outer = default) =>
        RunJobAsync(
            game,
            (adapter, _, ct) => adapter.UninstallAsync(game, ct),
            outer);

    public object Cancel()
    {
        lock (_gate)
        {
            if (_activeJob is null)
                return new { ok = false, message = "Nothing is running." };
            _activeJob.CancelRequested = true;
            try { _activeJob.Cts.Cancel(); } catch { }
            var cancellingPhase = _lastProgress.IsActive
                ? _lastProgress.Phase
                : InstallPhase.Preparing;
            PublishProgressLocked(new InstallProgress
            {
                GameId = _activeJob.GameId,
                // Keep the progress surface active until the backend really
                // returns. Cancellation is cooperative and may take time.
                Phase = cancellingPhase,
                Percent = _lastProgress.Percent,
                Status = "Cancelling…",
                CanCancel = false,
            });
        }
        return new { ok = true, message = "Cancel requested." };
    }

    public InstallProgress GetProgress(string? gameId = null)
    {
        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(gameId) &&
                !string.Equals(_activeJob?.GameId, gameId, StringComparison.OrdinalIgnoreCase) &&
                FindByGameId(gameId) is { } adapter)
            {
                return adapter.GetDownloadProgress(gameId);
            }
            return _lastProgress;
        }
    }

    public async Task CleanupAfterExitAsync(GameEntry game)
    {
        var adapter = Find(game.Store);
        if (adapter is null) return;
        await adapter.CleanupAfterExitAsync(game, BuildOptions()).ConfigureAwait(false);
    }

    internal GameRunState GetGameRunState(GameEntry game, bool discoverExternal = false) =>
        _runningGames.GetState(game, discoverExternal);

    internal async Task<GameStopResult> StopGameAsync(GameEntry game, CancellationToken ct = default)
    {
        var result = await _stopGame(game, ct).ConfigureAwait(false);
        if (!result.Ok)
            return result;

        GameSessionState? session;
        lock (_gate)
            _gameSessions.TryGetValue(game.Id, out session);
        if (session is null)
            return result;

        session.RequestStop();
        // If Stop wins before the delayed watcher starts, release launch
        // ownership now. LaunchAsync observes the cancelled state before it can
        // attach Quiet Game Mode, so a late vendor handoff cannot resurrect it.
        if (!session.IsWatching)
            CompleteGameSession(session);

        await session.CleanupCompleted.Task.WaitAsync(ct).ConfigureAwait(false);
        return result;
    }

    private async Task<InstallResult> RunJobAsync(
        GameEntry game,
        Func<IStoreAdapter, IProgress<InstallProgress>, CancellationToken, Task<InstallResult>> work,
        CancellationToken outer)
    {
        var adapter = Find(game.Store);
        if (adapter is null)
            return new InstallResult { Ok = false, Message = $"No adapter for store {game.Store}." };

        if (game.Id.StartsWith("mock:", StringComparison.OrdinalIgnoreCase))
        {
            return new InstallResult
            {
                Ok = false,
                Message = "Demo entry — install uses the real store backend when the title is discovered. For portable games use “Add portable game”.",
            };
        }

        JobState job;
        lock (_gate)
        {
            if (_activeOrLaunchingGames.Contains(game.Id))
                return new InstallResult
                {
                    Ok = false,
                    Message = $"Close {game.Title} before installing, updating, or uninstalling it.",
                };
            // Never overlap store mutations. Cancellation does not release
            // ownership: some vendor backends cannot stop immediately (or at
            // all), and a replacement install/update/uninstall must wait until
            // the original task has returned.
            if (_activeJob is not null)
                return new InstallResult { Ok = false, Message = "Another install, update, or uninstall is already running." };

            job = new JobState(CancellationTokenSource.CreateLinkedTokenSource(outer), game.Id);
            _activeJob = job;
            PublishProgressLocked(new InstallProgress
            {
                GameId = game.Id,
                Phase = InstallPhase.Preparing,
                Percent = 0,
                Status = "Starting…",
                CanCancel = true,
            });
        }

        var progress = new Progress<InstallProgress>(p =>
        {
            lock (_gate)
            {
                // Progress<T> delivers on the thread pool, so a sample reported
                // just before the job ended can arrive after the terminal phase
                // was published. Only the job that still owns the token may write.
                if (!ReferenceEquals(_activeJob, job))
                    return;
                // Some vendor clients ignore cancellation and emit one more active
                // progress sample. Never let that resurrect a cancelled Exo job.
                if (job.CancelRequested && p.IsActive)
                    return;
                PublishProgressLocked(p);
            }
        });

        try
        {
            // Install/update/uninstall are vendor-client operations too. Keep
            // their client windows and audio scoped to the actual backend work.
            using var driving = HiddenStoreRuntime.Operation();
            var result = await work(adapter, progress, job.Cts.Token).ConfigureAwait(false);
            lock (_gate)
            {
                var cancelled = job.CancelRequested || job.Cts.IsCancellationRequested;
                if (cancelled)
                {
                    result = new InstallResult
                    {
                        Ok = false,
                        Message = result.Ok
                            ? "Cancel requested; the backend may still be finishing."
                            : (string.IsNullOrWhiteSpace(result.Message) ? "Cancelled." : result.Message),
                        Path = result.Path,
                        HandoffOnly = result.HandoffOnly,
                    };
                }

                // Only the current owner may publish or clear shared state.
                if (!ReferenceEquals(_activeJob, job))
                    return result;

                if (cancelled)
                {
                    PublishProgressLocked(new InstallProgress
                    {
                        GameId = game.Id,
                        Phase = InstallPhase.Cancelled,
                        Percent = _lastProgress.Percent,
                        Status = result.Message,
                        CanCancel = false,
                    });
                }
                // Finalize when the job ended but adapters left progress stuck in an active phase.
                else if (_lastProgress.IsActive)
                {
                    PublishProgressLocked(new InstallProgress
                    {
                        GameId = game.Id,
                        Phase = result.Ok ? InstallPhase.Completed : InstallPhase.Failed,
                        Percent = result.Ok ? 100 : _lastProgress.Percent,
                        Status = result.Message,
                        CanCancel = false,
                    });
                }
                _activeJob = null;
            }
            return result;
        }
        catch (OperationCanceledException)
        {
            var p = new InstallProgress
            {
                GameId = game.Id,
                Phase = InstallPhase.Cancelled,
                Status = "Cancelled.",
                CanCancel = false,
            };
            lock (_gate)
            {
                if (ReferenceEquals(_activeJob, job))
                {
                    PublishProgressLocked(p);
                    _activeJob = null;
                }
            }
            return new InstallResult { Ok = false, Message = "Cancelled." };
        }
        catch (Exception ex)
        {
            InstallResult result;
            lock (_gate)
            {
                var cancelled = job.CancelRequested || job.Cts.IsCancellationRequested;
                result = new InstallResult
                {
                    Ok = false,
                    Message = cancelled ? "Cancelled." : ex.Message,
                };

                if (ReferenceEquals(_activeJob, job))
                {
                    PublishProgressLocked(new InstallProgress
                    {
                        GameId = game.Id,
                        Phase = cancelled ? InstallPhase.Cancelled : InstallPhase.Failed,
                        Percent = _lastProgress.Percent,
                        Status = result.Message,
                        CanCancel = false,
                    });
                    _activeJob = null;
                }
            }
            return result;
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_activeJob, job))
                    _activeJob = null;
            }
            job.Cts.Dispose();
        }
    }

    /// <summary>Publishes while <see cref="_gate"/> is held so job events cannot
    /// be reordered across a cancel/replacement boundary.</summary>
    private void PublishProgressLocked(InstallProgress progress)
    {
        _lastProgress = progress;
        try { ProgressChanged?.Invoke(progress); }
        catch (Exception ex) { AppLog.Debug($"Progress observer failed: {ex.Message}"); }
    }

    private LaunchOptions BuildOptions() => new()
    {
        CloseStoreUiAfterExit = _settings.Current.CloseStoreClientsAfterLaunch,
        MinimizeStoreUi = true,
        AntiCheatSafeMode = true,
    };

    private IStoreAdapter? Find(StoreKind store) =>
        _adapters.FirstOrDefault(a => a.Store == store);

    private IStoreAdapter? FindByGameId(string gameId)
    {
        var prefix = gameId.Split(':')[0];
        return _adapters.FirstOrDefault(a =>
            string.Equals(a.Id, prefix, StringComparison.OrdinalIgnoreCase));
    }
}
