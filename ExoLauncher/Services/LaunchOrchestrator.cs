using ExoLauncher.Adapters;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>
/// detect ownership → resolve deps → install/update with progress →
/// launch backend minimized → launch game → optional store-UI cleanup.
/// </summary>
public sealed partial class LaunchOrchestrator
{
    private static readonly TimeSpan HandoffSettle = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan BoundStopTargetRecheck = TimeSpan.FromSeconds(2);
    private readonly IReadOnlyList<IStoreAdapter> _adapters;
    private readonly SettingsService _settings;
    private readonly DependencyService _deps;
    private readonly Func<GameEntry, IReadOnlyList<DependencyInfo>> _missingDependencies;
    private readonly AchievementService _achievements;
    private readonly GameProcessRegistry _runningGames = new();
    private readonly Func<GameEntry, CancellationToken, Task<GameStopResult>> _stopGame;
    private readonly Func<StoreKind, IDisposable> _beginQuietGameSession;
    private readonly Func<StoreKind, Task> _closeUnusedStoreClients;
    private readonly Func<string, CancellationToken, Task<GameEntry?>>? _revalidateQueuedGame;
    private readonly object _gate = new();
    private JobState? _activeJob;
    private readonly Queue<PendingJob> _pendingJobs = new();
    // Steam transfers Steam started itself, followed for their live progress (game ids).
    private readonly HashSet<string> _following = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _activeOrLaunchingGames = new(StringComparer.OrdinalIgnoreCase);
    // A launch becomes a session as soon as Exo reserves the game id, not only
    // once the delayed watcher begins. That lets Stop cancel a just-started
    // handoff before the watcher has observed its first game process.
    private readonly Dictionary<string, GameSessionState> _gameSessions =
        new(StringComparer.OrdinalIgnoreCase);
    private InstallProgress _lastProgress = new();

    private sealed class JobState(CancellationTokenSource cts, string gameId, StoreKind store, bool publishProgress)
    {
        public CancellationTokenSource Cts { get; } = cts;
        public string GameId { get; } = gameId;
        public StoreKind Store { get; } = store;
        public bool PublishProgress { get; } = publishProgress;
        public bool CancelRequested { get; set; }
        /// <summary>Set when the download was paused (by the user or a hold), not cancelled: its intent stays paused.</summary>
        public string? PauseReason { get; set; }
        public DownloadIntent? DownloadIntent { get; init; }
    }

    private sealed class PendingJob
    {
        public required GameEntry Game { get; init; }
        public required Func<GameEntry, IStoreAdapter, IProgress<InstallProgress>, CancellationToken, Task<InstallResult>> Work { get; init; }
        public required CancellationToken Outer { get; init; }
        public required bool PublishProgress { get; init; }
        public EntitlementMutation? EntitlementMutation { get; init; }
        public string? AccountScope { get; init; }
        public TaskCompletionSource<InstallResult>? Completion { get; init; }
        public DownloadIntent? DownloadIntent { get; init; }
        public bool ResumeRecoveredIntent { get; init; }
    }

    private enum EntitlementMutation
    {
        Install,
        Update,
    }

    private sealed class GameSessionState(GameEntry game)
    {
        private readonly object _stateGate = new();
        private IDisposable? _quietGameSession;
        private int _watching;
        private bool _completed;
        private int _launchSucceeded;
        private bool _gameStarted;

        public GameEntry Game { get; } = game;
        public CancellationTokenSource StopCts { get; } = new();
        public TaskCompletionSource CleanupCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsStopRequested => StopCts.IsCancellationRequested;
        public bool IsWatching => Volatile.Read(ref _watching) != 0;
        public bool LaunchSucceeded => Volatile.Read(ref _launchSucceeded) != 0;

        public void MarkLaunchSucceeded() => Volatile.Write(ref _launchSucceeded, 1);

        public bool TryMarkGameStarted()
        {
            lock (_stateGate)
            {
                if (_completed || _gameStarted || IsStopRequested) return false;
                _gameStarted = true;
                return true;
            }
        }

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
                // The session is terminal; late stop requests only need the
                // ObjectDisposedException guard in RequestStop.
                try { StopCts.Dispose(); } catch { }
                return true;
            }
        }
    }

    public event Action<InstallProgress>? ProgressChanged;
    public event Action<GameEntry>? GameSessionCompleted;
    public event Action<GameEntry>? GameLaunched;
    /// <summary>Just before the store adapter starts a game, on the pool: driver settings that apply from process start go here.</summary>
    internal event Action<GameEntry>? GameLaunching;
    /// <summary>An exact game id gained or lost its verified process, whether Exo launched it or not.</summary>
    internal event Action<string, GameRunState>? GameRunStateChanged
    {
        add => _runningGames.StateChanged += value;
        remove => _runningGames.StateChanged -= value;
    }

    public LaunchOrchestrator(
        IReadOnlyList<IStoreAdapter> adapters,
        SettingsService settings,
        DependencyService deps,
        AchievementService? achievements = null,
        Func<string, CancellationToken, Task<GameEntry?>>? revalidateQueuedGame = null)
        : this(adapters, settings, deps, achievements, stopGame: null,
            revalidateQueuedGame: revalidateQueuedGame,
            downloadIntentStore: new PendingDownloadStore(Path.Combine(PathHelper.AppDataDir, "download-intents.json")))
    {
    }

    internal LaunchOrchestrator(
        IReadOnlyList<IStoreAdapter> adapters,
        SettingsService settings,
        DependencyService deps,
        AchievementService? achievements,
        Func<GameEntry, CancellationToken, Task<GameStopResult>>? stopGame,
        Func<StoreKind, IDisposable>? beginQuietGameSession = null,
        Func<GameEntry, IReadOnlyList<DependencyInfo>>? missingDependencies = null,
        Func<string, CancellationToken, Task<GameEntry?>>? revalidateQueuedGame = null,
        Func<StoreKind, Task>? closeUnusedStoreClients = null,
        PendingDownloadStore? downloadIntentStore = null,
        Func<GameEntry, bool>? isVendorDownloadRunning = null)
    {
        _adapters = adapters;
        _settings = settings;
        _deps = deps;
        _achievements = achievements ?? new AchievementService();
        _stopGame = stopGame ?? _runningGames.StopAsync;
        _beginQuietGameSession = beginQuietGameSession ?? Adapters.HiddenStoreRuntime.GameSession;
        _closeUnusedStoreClients = closeUnusedStoreClients ?? CloseUnusedStoreClientsAsync;
        _missingDependencies = missingDependencies ?? _deps.GetMissingRequired;
        _revalidateQueuedGame = revalidateQueuedGame;
        InitializeDownloadRecovery(downloadIntentStore, isVendorDownloadRunning);
    }

    public InstallProgress CurrentProgress
    {
        get
        {
            lock (_gate) return _lastProgress;
        }
    }

    public IReadOnlyList<string> QueuedGameIds
    {
        get
        {
            lock (_gate) return _pendingJobs.Select(job => job.Game.Id)
                .Concat(_downloadIntents.Values.Where(intent => _pausedDownloadTokens.Contains(intent.Token)).Select(intent => intent.GameId))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    /// <summary>True while Exo or the game's store is installing or updating it (files may be half-written).</summary>
    public bool IsGameBusy(GameEntry game)
    {
        lock (_gate)
        {
            if (string.Equals(_activeJob?.GameId, game.Id, StringComparison.OrdinalIgnoreCase) ||
                _pendingJobs.Any(job => string.Equals(job.Game.Id, game.Id, StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        try { return Find(game.Store)?.GetDownloadProgress(game.Id).IsActive == true; }
        catch { return false; }
    }

    public async Task<LaunchResult> LaunchAsync(GameEntry game, bool skipDeps = false, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested)
            return new LaunchResult { Ok = false, Message = "Launch canceled." };

        if (EntitlementBlockReason(game) is { } entitlementError)
            return new LaunchResult { Ok = false, Message = entitlementError };

        AppLog.Info($"Launch: gameId={game.Id}; store={game.Store}.");
        var adapter = Find(game.Store);
        if (adapter is null)
            return new LaunchResult { Ok = false, Message = "Exo does not support this store yet." };

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
            if (_activeJob is not null && (_activeJob.Store == game.Store ||
                string.Equals(_activeJob.GameId, game.Id, StringComparison.OrdinalIgnoreCase)))
                return new LaunchResult { Ok = false, Message = "This store is busy installing, updating, or removing a game. Other stores remain available." };
            if (!_activeOrLaunchingGames.Add(game.Id))
                return new LaunchResult { Ok = false, Message = $"{game.Title} is already running or starting." };
            _gameSessions[game.Id] = session;
        }

        var keepSessionRegistration = false;
        try
        {
            if (!skipDeps && _settings.Current.AutoInstallRedistributables)
            {
                var missing = _missingDependencies(game);
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

            var options = BuildOptions(game);
            IDisposable? quietGameSession = null;
            using var launchCts = CancellationTokenSource.CreateLinkedTokenSource(ct, session.StopCts.Token);
            try
            {
                if (launchCts.IsCancellationRequested)
                    return new LaunchResult { Ok = false, Message = "Launch stopped." };

                // Exo is driving: suppression is active for the duration of the launch.
                using var driving = Adapters.HiddenStoreRuntime.Operation();
                // Register the provider before sibling cleanup starts. The cleanup
                // gate uses this registration to ensure an active or concurrently
                // launching provider is never closed or terminated.
                quietGameSession = _beginQuietGameSession(game.Store);
                // Off the UI thread: Steam's Play runs process scans and starts
                // the window hider before its first await, which froze the window.
                var launchToken = launchCts.Token;
                var result = await Task.Run(() =>
                {
                    try { GameLaunching?.Invoke(game); }
                    catch (Exception ex) { AppLog.Warn("Before-launch listener failed: " + ex.Message); }
                    return adapter.LaunchAsync(game, options, launchToken);
                }).ConfigureAwait(false);
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
                    // Keep the game's provider protected from sibling cleanup.
                    // Window hiding ends with the handoff so a user can reopen it.
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
                    if (!result.HandoffOnly)
                        NotifyGameStarted(session);
                    _ = ScheduleCleanupAsync(
                        adapter,
                        game,
                        options,
                        result.ProcessId,
                        session);
                }
                AppLog.Info(
                    $"Launch result: gameId={game.Id}; ok={result.Ok}; pid={result.ProcessId?.ToString() ?? "—"}; {result.Message}");
                return result;
            }
            catch (OperationCanceledException) when (launchCts.IsCancellationRequested)
            {
                return new LaunchResult { Ok = false, Message = "Launch stopped." };
            }
            catch (Exception ex)
            {
                return new LaunchResult { Ok = false, Message = Adapters.Cli.CliFailure.Launch(game.Title, ex) };
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

    private void NotifyGameStarted(GameSessionState session)
    {
        if (!session.TryMarkGameStarted()) return;
        var game = session.Game;
        try { _settings.RecordLaunch(game.Id); } catch { /* */ }
        try { PlaytimeService.BeginSession(game.Id); } catch { /* */ }
        if (_settings.Current.CloseStoreClientsAfterLaunch)
            _ = _closeUnusedStoreClients(game.Store);
        try { GameLaunched?.Invoke(game); }
        catch { /* observers must not prevent process tracking */ }
    }

    /// <summary>Keep client cleanup and its window enumeration off the calling thread.</summary>
    private static Task CloseUnusedStoreClientsAsync(StoreKind keep) => Task.Run(async () =>
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
    });

    private async Task ScheduleCleanupAsync(
        IStoreAdapter adapter,
        GameEntry game,
        LaunchOptions options,
        int? processId,
        GameSessionState session)
    {
        if (!session.TryBeginWatching())
            return;

        var credited = false;
        var stopped = false;
        try
        {
            // Brief settle so a protocol handoff can name its first process.
            // This used to be 2.5s, which is 2.5s where Stop had nothing to bind
            // to; the rebind loop below already tolerates an empty first scan.
            await Task.Delay(HandoffSettle, session.StopCts.Token).ConfigureAwait(false);

            // Re-bind Stop to the real game process. Steam/Epic often return a
            // bootstrap PID (or nothing) that ObserveLaunch correctly rejects —
            // without this re-scan canStop never lights and Stop always fails.
            // Keep rebinding for the whole session: late handoffs (EAC → game)
            // used to leave Stop stuck on a dead bootstrap identity.
            var ignoredForBind = BootstrapProcessNames(game.Store);
            using var rebindCts = CancellationTokenSource.CreateLinkedTokenSource(session.StopCts.Token);
            var rebindTask = RebindStopTargetLoopAsync(session, ignoredForBind, rebindCts.Token);

            // Steam playtime comes from localconfig.vdf; Epic/Riot/Local rely on
            // Exo session minutes. Never cancel just because a bootstrap PID died —
            // keep watching install path / known process names through handoff.
            var processNames = game.Store == StoreKind.Riot &&
                               !string.IsNullOrWhiteSpace(game.LaunchTarget)
                ? RiotAdapter.GameProcessNames(game.LaunchTarget)
                : null;
            var isLeagueHandoff = game.Store == StoreKind.Riot &&
                                  game.LaunchTarget?.Trim().ToLowerInvariant() is "league_of_legends" or "lion";
            var protocolLaunch = game.Store is StoreKind.Ea or StoreKind.Ubisoft or StoreKind.BattleNet;
            var handoffNames = isLeagueHandoff
                ? RiotAdapter.LaunchReadyProcessNames(game.LaunchTarget!)
                : null;
            var ignored = BootstrapProcessNames(game.Store);
            var hasTrackableSession = processId is > 0 ||
                                      !string.IsNullOrWhiteSpace(game.Path) ||
                                      processNames is { Length: > 0 };
            try
            {
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
                        // EA / Ubisoft / Battle.net fire a protocol with no PID;
                        // the vendor client can patch for minutes before a game
                        // exe appears under the install path.
                        appearTimeout: isLeagueHandoff
                            ? TimeSpan.FromHours(4)
                            : protocolLaunch
                                ? TimeSpan.FromMinutes(30)
                                : TimeSpan.FromSeconds(90),
                        goneDebounce: TimeSpan.FromSeconds(12),
                        handoffProcessNames: handoffNames,
                        handoffAppearTimeout: isLeagueHandoff ? TimeSpan.FromSeconds(90) : null,
                        observedSeedGoneGrace: game.Store is StoreKind.Gog or StoreKind.Local
                            ? TimeSpan.FromSeconds(12)
                            : null,
                        ct: session.StopCts.Token)
                        .ConfigureAwait(false);
            }
            finally
            {
                try { rebindCts.Cancel(); } catch { /* */ }
                try { await rebindTask.ConfigureAwait(false); } catch { /* best-effort */ }
            }
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

                // Refresh achievement counts in the background; neither a normal
                // exit nor Stop waits on a provider read.
                _ = RefreshAchievementsAfterSessionAsync(game);

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

    private async Task RefreshAchievementsAfterSessionAsync(GameEntry game)
    {
        try
        {
            await _achievements.RefreshAfterSessionAsync(game).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Debug($"Achievement refresh after session failed for '{game.Title}': {ex.Message}");
        }
    }

    /// <summary>
    /// Continuously re-attach Stop to the live install-root process for this
    /// title. One-shot rebind after handoff misses EAC/bootstrap chains that
    /// only spawn the real game many seconds later.
    /// </summary>
    private async Task RebindStopTargetLoopAsync(
        GameSessionState session,
        IEnumerable<string> ignoredNames,
        CancellationToken cancellationToken)
    {
        var game = session.Game;
        // First pass is aggressive so canStop lights quickly after launch.
        var first = true;
        while (!cancellationToken.IsCancellationRequested)
        {
            // A live binding cannot be improved by another scan. Verifying the
            // bound identity is one process handle; the scan below opens one for
            // every process on the machine, six times per rebind window, for the
            // whole session.
            if (!first && _runningGames.GetState(game).IsRunning)
            {
                NotifyGameStarted(session);
                try
                {
                    await Task.Delay(BoundStopTargetRecheck, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
            }

            try
            {
                var boundPid = await ProcessHelper.WaitForProcessUnderPathAsync(
                        game.Path,
                        first ? TimeSpan.FromSeconds(12) : TimeSpan.FromSeconds(2),
                        cancellationToken,
                        ignoredNames: ignoredNames,
                        confirmationDelay: first ? TimeSpan.FromMilliseconds(150) : TimeSpan.Zero)
                    .ConfigureAwait(false);
                if (boundPid is int livePid && livePid > 0)
                {
                    _runningGames.ObserveLaunch(game, livePid);
                    if (_runningGames.GetState(game).IsRunning)
                        NotifyGameStarted(session);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                AppLog.Debug($"Stop-target rebind failed for '{game.Title}': {ex.Message}");
            }

            first = false;
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
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
        // Stopping a game is logically complete once its exact process is gone,
        // playtime/achievement cleanup has crossed the watcher finally block,
        // and launch ownership has been released. A WinEvent hook can take time
        // to leave its native message pump, so never keep the game.stop RPC (and
        // the UI's Closing state) hostage to disposal of the quiet-client guard.
        // Ref-counted suppression makes overlap with an immediate replay safe.
        try
        {
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
        finally
        {
            // Stop callers must observe the logical session-completion events
            // before they resume. Native hook disposal remains asynchronous.
            session.CleanupCompleted.TrySetResult();
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

    // A canceled install's half-downloaded folder, removed off the job thread. The store tool may still be letting
    // go of its files for a moment after the cancel, so it tries a few times.
    private static void DiscardPartial(string folder, string gameTitle) => _ = Task.Run(async () =>
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
                AppLog.Info($"Canceled install of {gameTitle}: removed its unfinished download.");
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
        }
        AppLog.Warn($"Canceled install of {gameTitle}: its unfinished download could not be removed.");
    });

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
        StoreKind.Ea =>
        [
            "EADesktop", "Origin", "EasyAntiCheat", "EasyAntiCheat_EOS",
        ],
        StoreKind.Ubisoft =>
        [
            "UbisoftConnect", "upc", "UplayWebCore", "EasyAntiCheat", "EasyAntiCheat_EOS",
        ],
        StoreKind.BattleNet =>
        [
            "Battle.net", "Agent",
        ],
        _ => [],
    };

    public async Task<InstallResult> InstallAsync(
        GameEntry game,
        string? path = null,
        bool skipDeps = false,
        CancellationToken outer = default)
    {
        var accountScopeAtAuthorization = ActiveAccountScope(game.Store);
        // local:add is a real portable-install entry (not mock:*).
        if (game.Id.StartsWith("mock:", StringComparison.OrdinalIgnoreCase))
        {
            return new InstallResult
            {
                Ok = false,
                Message = "This is a demo entry. Install the real game, then refresh. For games outside a store, use Add a folder.",
            };
        }

        if (EntitlementBlockReason(game) is { } entitlementError)
            return new InstallResult { Ok = false, Message = entitlementError };

        // Install is an entitlement-bearing operation. Keep the check in the
        // orchestrator as well as the bridge so future native callers cannot
        // turn a stale capability hint into a store install.
        if (!game.Owned && !OfficialClientCatalog.IsOfficialClientCatalogInstall(game))
        {
            return new InstallResult
            {
                Ok = false,
                Message = "This title is not owned by the active store account. Buy it from the store first.",
            };
        }
        if (game.Installed)
        {
            return new InstallResult { Ok = false, Message = "This title is already installed." };
        }
        if (!game.CanInstall)
        {
            return new InstallResult
            {
                Ok = false,
                Message = "Exo cannot install this game yet. Install it from its store app.",
            };
        }

        // A store install is itself the authoritative dependency workflow.
        // Never let Exo's generic runtime detector intercept the user's exact
        // title request before Steam/Epic/GOG/Riot receives it. The retained
        // skipDeps parameter is part of the stable bridge contract.
        _ = skipDeps;

        path ??= PathHelper.GamesRoot;

        return await RunJobAsync(game, async (current, adapter, progress, ct) =>
                await adapter.InstallAsync(current, path, progress, ct).ConfigureAwait(false),
            outer,
            entitlementMutation: EntitlementMutation.Install,
            authorizationAccountScope: accountScopeAtAuthorization,
            downloadIntent: CreateDownloadIntent(game, "install", path, accountScopeAtAuthorization)).ConfigureAwait(false);
    }

    public async Task<InstallResult> UpdateAsync(GameEntry game, bool skipDeps = false, CancellationToken outer = default)
    {
        var accountScopeAtAuthorization = ActiveAccountScope(game.Store);
        if (EntitlementBlockReason(game) is { } entitlementError)
            return new InstallResult { Ok = false, Message = entitlementError };

        // Updates must target the selected vendor title directly. Store clients
        // own their redistributable/update prerequisites and can adjudicate them
        // without Exo opening an unrelated dependency installer first.
        _ = skipDeps;

        return await RunJobAsync(game, async (current, adapter, progress, ct) =>
                await adapter.UpdateAsync(current, progress, ct).ConfigureAwait(false),
            outer,
            entitlementMutation: EntitlementMutation.Update,
            authorizationAccountScope: accountScopeAtAuthorization,
            downloadIntent: CreateDownloadIntent(game, "update", null, accountScopeAtAuthorization)).ConfigureAwait(false);
    }

    private static string? EntitlementBlockReason(GameEntry game) => game.EntitlementState switch
    {
        EntitlementState.NotOwned =>
            "This title is not owned by the active store account. Buy it from the store first.",
        EntitlementState.Unverified when !game.Installed =>
            "Ownership could not be verified for the active store account. Refresh the store connection and try again.",
        _ => null,
    };

    public Task<InstallResult> UninstallAsync(GameEntry game, CancellationToken outer = default) =>
        RunJobAsync(
            game,
            (current, adapter, _, ct) => adapter.UninstallAsync(current, ct),
            outer,
            publishProgress: false,
            waitIfQueued: true);

    public Task<InstallResult> RepairAsync(GameEntry game, CancellationToken outer = default)
    {
        var adapter = Find(game.Store);
        if (adapter is not IStoreRepair repair || !repair.CanRepair(game))
        {
            return Task.FromResult(new InstallResult
            {
                Ok = false,
                Message = $"{game.Title} cannot be verified from Exo.",
            });
        }

        return RunJobAsync(
            game,
            (current, _, progress, ct) => repair.RepairAsync(current, progress, ct),
            outer,
            waitIfQueued: true);
    }

    /// <summary>
    /// Cancels the running job. With <paramref name="gameId"/>, only when that game's job is the one
    /// running: a press that lands just after its download finished must not cancel the next job.
    /// </summary>
    public object Cancel(string? gameId = null)
    {
        JobState job;
        lock (_gate)
        {
            if (_activeJob is null)
                return new { ok = false, message = "Nothing is running." };
            if (!string.IsNullOrWhiteSpace(gameId) && !string.Equals(_activeJob.GameId, gameId, StringComparison.OrdinalIgnoreCase))
                return new { ok = false, message = "That download is not running." };
            job = _activeJob;
            job.CancelRequested = true;
            FinishDownloadIntentLocked(job.DownloadIntent, pause: false, reason: null);
            AppLog.Info($"Cancel requested: gameId={job.GameId}.");
            // A job without a progress line (uninstall) never clears one at its end, so it gets none here.
            if (job.PublishProgress)
            {
                PublishProgressLocked(new InstallProgress
                {
                    GameId = job.GameId,
                    // Keep the progress surface active until the backend really
                    // returns. Cancellation is cooperative and may take time.
                    Phase = _lastProgress.IsActive ? _lastProgress.Phase : InstallPhase.Preparing,
                    Percent = _lastProgress.Percent,
                    Status = "Canceling…",
                    CanCancel = false,
                });
            }
        }
        CancelJob(job);
        return new { ok = true, message = _downloadRecoveryError is null ? "Cancel requested." :
            "Cancel requested. The saved queue could not be updated and may reappear paused after restart." };
    }

    /// <summary>
    /// Outside <see cref="_gate"/>: Cancel runs the job's continuations inline,
    /// and a job that finishes at once clears <see cref="_activeJob"/> and
    /// starts the next queued one before Cancel returns.
    /// </summary>
    private static void CancelJob(JobState job)
    {
        try { job.Cts.Cancel(); }
        catch (ObjectDisposedException) { /* the job already returned */ }
        catch (AggregateException ex) { AppLog.Debug("Cancel callback failed: " + ex.InnerException?.Message); }
    }

    /// <summary>
    /// Drop a job that has not started yet. The active job is untouched; that
    /// is <see cref="Cancel"/>. A waiter on the dropped job gets a clean
    /// not-ok result rather than hanging.
    /// </summary>
    public object Dequeue(string gameId)
    {
        PendingJob? removed = null;
        DownloadIntent? removedPaused = null;
        string[] remaining;
        lock (_gate)
        {
            var kept = new Queue<PendingJob>();
            while (_pendingJobs.TryDequeue(out var job))
            {
                if (removed is null && string.Equals(job.Game.Id, gameId, StringComparison.OrdinalIgnoreCase)) removed = job;
                else kept.Enqueue(job);
            }
            foreach (var job in kept) _pendingJobs.Enqueue(job);
            removedPaused = _downloadIntents.Values.FirstOrDefault(intent => _pausedDownloadTokens.Contains(intent.Token) &&
                string.Equals(intent.GameId, gameId, StringComparison.OrdinalIgnoreCase));
            FinishDownloadIntentLocked(removed?.DownloadIntent ?? removedPaused, pause: false, reason: null);
            remaining = QueuedGameIds.ToArray();
        }
        if (removed is null && removedPaused is null) return new { ok = false, message = "That title is not queued.", queuedGameIds = remaining, pausedDownloads = PausedDownloads };
        AppLog.Info($"Dequeued: gameId={gameId}.");
        removed?.Completion?.TrySetResult(new InstallResult { Ok = false, Message = "Removed from the queue." });
        return new { ok = true, message = _downloadRecoveryError is null
            ? $"{removed?.Game.Title ?? removedPaused!.Title} removed from the queue."
            : "Removed from the queue. The saved queue could not be updated and may reappear paused after restart.",
            queuedGameIds = remaining, pausedDownloads = PausedDownloads };
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

    internal GameRunState GetGameRunState(GameEntry game, bool discoverExternal = false) =>
        _runningGames.GetState(game, discoverExternal);

    internal IReadOnlyDictionary<string, GameRunState> GetGameRunStates(IReadOnlyList<GameEntry> games, bool underRootOnly = false) =>
        _runningGames.GetStates(games, underRootOnly);

    internal async Task<GameStopResult> StopGameAsync(GameEntry game, CancellationToken ct = default)
    {
        var result = await _stopGame(game, ct).ConfigureAwait(false);
        AppLog.Info($"Stop: gameId={game.Id}; ok={result.Ok}; {result.Message}");
        // "Nothing running" still ends Exo's session: a protocol launch (EA,
        // Ubisoft, Battle.net) that never produced a game otherwise held the
        // game as starting for its 30-minute wait, and Stop could not clear it.
        if (!result.Ok && !result.NothingRunning)
            return result;

        GameSessionState? session;
        lock (_gate)
            _gameSessions.TryGetValue(game.Id, out session);
        if (session is null)
            return result;
        if (!result.Ok)
            result = new GameStopResult(true, "Stopped.");

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
        Func<GameEntry, IStoreAdapter, IProgress<InstallProgress>, CancellationToken, Task<InstallResult>> work,
        CancellationToken outer,
        bool publishProgress = true,
        bool waitIfQueued = false,
        EntitlementMutation? entitlementMutation = null,
        bool revalidateQueuedExecution = false,
        string? queuedAccountScope = null,
        string? authorizationAccountScope = null,
        DownloadIntent? downloadIntent = null,
        bool resumeRecoveredIntent = false,
        JobState? claimedJob = null)
    {
        // A claimed job comes from DrainQueue, which already owns _activeJob
        // for it. It passed the adapter and demo checks below when it was queued.
        var adapter = Find(game.Store);
        if (adapter is null)
            return new InstallResult { Ok = false, Message = "Exo does not support this store yet." };

        if (game.Id.StartsWith("mock:", StringComparison.OrdinalIgnoreCase))
        {
            return new InstallResult
            {
                Ok = false,
                Message = "This is a demo entry. Install the real game, then refresh. For games outside a store, use Add a folder.",
            };
        }

        JobState? job = claimedJob;
        TaskCompletionSource<InstallResult>? queuedWaiter = null;
        if (claimedJob is null)
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
            // the original task has returned. A Resume of a download that is
            // still pausing waits behind it in the queue instead.
            if (_activeJob is not null && !(resumeRecoveredIntent && _activeJob.PauseReason is not null) &&
                string.Equals(_activeJob.GameId, game.Id, StringComparison.OrdinalIgnoreCase))
            {
                return new InstallResult
                {
                    Ok = false,
                    Message = "Another install, update, or uninstall is already running.",
                };
            }

            if (_pendingJobs.Any(pending =>
                    string.Equals(pending.Game.Id, game.Id, StringComparison.OrdinalIgnoreCase)))
            {
                return new InstallResult
                {
                    Ok = false,
                    Message = "This title is already queued.",
                };
            }

            // A hold (a game running, outside the download hours) keeps a new download waiting in the queue.
            var waits = _activeJob is not null || (_holds.Count > 0 && downloadIntent is not null);
            if (!PrepareDownloadIntentLocked(downloadIntent, resumeRecoveredIntent, waits, out var persistenceError))
                return new InstallResult { Ok = false, Message = persistenceError ?? "The download queue could not be saved." };

            if (waits)
            {
                queuedWaiter = waitIfQueued
                    ? new TaskCompletionSource<InstallResult>(TaskCreationOptions.RunContinuationsAsynchronously)
                    : null;
                _pendingJobs.Enqueue(new PendingJob
                {
                    Game = game,
                    Work = work,
                    Outer = outer,
                    PublishProgress = publishProgress,
                    EntitlementMutation = entitlementMutation,
                    AccountScope = adapter is IStoreAccountScope
                        ? authorizationAccountScope ?? queuedAccountScope
                        : null,
                    Completion = queuedWaiter,
                    DownloadIntent = downloadIntent,
                    ResumeRecoveredIntent = resumeRecoveredIntent,
                });
                PublishDownloadQueueChangedLocked();
                if (queuedWaiter is null)
                {
                    return new InstallResult
                    {
                        Ok = true,
                        Queued = true,
                        Message = _activeJob is null && _holds.Count > 0
                            ? $"{game.Title} is queued. {_holds.Values.Last()}"
                            : $"{game.Title} is queued. It starts after the downloads ahead of it.",
                    };
                }
            }
            else
            {
                job = StartJobLocked(game, outer, publishProgress, downloadIntent);
            }
        }

        if (queuedWaiter is not null)
            return await queuedWaiter.Task.WaitAsync(outer).ConfigureAwait(false);

        if (job is null)
            return new InstallResult { Ok = false, Message = "Install did not start." };

        var progress = new OrderedProgress(p =>
        {
            lock (_gate)
            {
                // Delivery is synchronous. Only the job that still owns the token
                // may publish; resampled snapshots keep their original sequence.
                if (!ReferenceEquals(_activeJob, job) || !job.PublishProgress)
                    return;
                // Some vendor clients ignore cancellation and emit one more active
                // progress sample. Never let that resurrect a cancelled Exo job.
                if (job.CancelRequested && p.IsActive)
                    return;
                // Adapter resample can return the last "Starting…" snapshot
                // (no percent) while a live watcher sample already has bytes.
                // Never let that empty tick wipe a visible bar.
                if (p.Sequence <= _lastProgress.Sequence || IsWeakerProgressSample(_lastProgress, p))
                    return;
                PublishProgressLocked(MergeProgressSample(_lastProgress, p));
            }
        });

        var leaveIntentPaused = false;
        var haltQueue = false;
        string? reviewReason = null;
        try
        {
            var currentGame = game;
            string? authorizationError = null;
            InstallResult? recoveredOutcome = null;
            if (resumeRecoveredIntent && downloadIntent is not null)
            {
                (currentGame, recoveredOutcome) = await RevalidateRecoveredDownloadAsync(
                    downloadIntent, game, adapter, job.Cts.Token).ConfigureAwait(false);
            }
            else if (revalidateQueuedExecution && entitlementMutation is not null && adapter is IStoreAccountScope)
            {
                (currentGame, authorizationError) = await RevalidateQueuedEntitlementAsync(
                        game,
                        adapter,
                        entitlementMutation.Value,
                        queuedAccountScope,
                        job.Cts.Token)
                    .ConfigureAwait(false);
            }

            InstallResult result;
            if (recoveredOutcome is not null)
            {
                result = recoveredOutcome;
            }
            else if (authorizationError is not null)
            {
                result = new InstallResult { Ok = false, Message = authorizationError };
            }
            else
            {
                // Install/update/uninstall are vendor-client operations too. Keep
                // their client windows and audio scoped to the actual backend work.
                using var driving = HiddenStoreRuntime.Operation();
                // A portable folder (Local) has no vendor client to keep in scope: adding or
                // removing one must not quit Steam, Epic or the other store apps.
                if (_settings.Current.CloseStoreClientsAfterLaunch && currentGame.Store != StoreKind.Local)
                    _ = _closeUnusedStoreClients(currentGame.Store);
                using var sampleCts = CancellationTokenSource.CreateLinkedTokenSource(job.Cts.Token);
                var sampler = Task.Run(async () =>
                {
                    InstallProgress? lastLive = null;
                    while (!sampleCts.IsCancellationRequested)
                    {
                        try
                        {
                            await Task.Delay(400, sampleCts.Token).ConfigureAwait(false);
                            var live = adapter.GetDownloadProgress(currentGame.Id);
                            // Adapters rebuild the same snapshot every tick. A repeat
                            // would carry a fresh sequence and overwrite newer
                            // metrics from the backend's own reports.
                            if (!live.IsActive || lastLive is not null && SameProgress(lastLive, live)) continue;
                            lastLive = live;
                            ((IProgress<InstallProgress>)progress).Report(live);
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }
                        catch
                        {
                            /* keep the job alive */
                        }
                    }
                }, sampleCts.Token);
                try
                {
                    // Adapters do blocking work before their first await (a GOG
                    // folder delete, process scans). The bridge calls this on the
                    // UI thread, so run the job on the pool.
                    var token = job.Cts.Token;
                    result = await Task.Run(() => work(currentGame, adapter, progress, token)).ConfigureAwait(false);
                }
                finally
                {
                    try { sampleCts.Cancel(); } catch { }
                    try { await sampler.ConfigureAwait(false); } catch { }
                }
            }
            // Opening a known public client download completes that link
            // request. It must not become an account-bound paused download.
            var completedCatalogLink = result.Ok && result.HandoffOnly && downloadIntent is not null &&
                IsOfficialCatalogLinkIntent(downloadIntent) && OfficialClientCatalog.IsOfficialClientCatalogInstall(currentGame);
            leaveIntentPaused = result.HandoffOnly && !completedCatalogLink || resumeRecoveredIntent && !result.Ok;
            // Only an unconfirmed store handoff holds back the jobs behind it.
            // A saved download that failed its own checks pauses alone.
            haltQueue = result.HandoffOnly && !completedCatalogLink;
            reviewReason = result.Message;
            var partial = result.PartialPath;
            string? discard = null;
            lock (_gate)
            {
                var cancelled = job.CancelRequested || job.Cts.IsCancellationRequested;
                // Cancel throws the half-downloaded game away; Pause keeps it to resume.
                if (cancelled && job.PauseReason is null && !result.Ok) discard = partial;
                if (cancelled)
                {
                    result = new InstallResult
                    {
                        Ok = false,
                        // A store tool reports its own "Canceled." when Pause stopped it too.
                        Message = result.Ok
                            ? "Cancel requested, but the store may have finished anyway."
                            : job.PauseReason is not null ? "Paused."
                            : (string.IsNullOrWhiteSpace(result.Message) ? "Canceled." : result.Message),
                        Path = result.Path,
                        HandoffOnly = result.HandoffOnly,
                    };
                }

                // Only the current owner may publish or clear shared state.
                if (!ReferenceEquals(_activeJob, job))
                    return result;

                if (cancelled)
                {
                    if (job.PublishProgress)
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
                }
                // Official-store Open is a handoff, not a certified Update complete.
                else if (job.PublishProgress && result.HandoffOnly)
                {
                    PublishProgressLocked(new InstallProgress
                    {
                        GameId = game.Id,
                        Phase = InstallPhase.Idle,
                        Percent = null,
                        Status = result.Message,
                        CanCancel = false,
                    });
                }
                // Finalize when the job ended but adapters left progress stuck in an active phase.
                else if (job.PublishProgress && _lastProgress.IsActive)
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
            }
            if (discard is not null) DiscardPartial(discard, game.Title);
            return result;
        }
        catch (OperationCanceledException)
        {
            var p = new InstallProgress
            {
                GameId = game.Id,
                Phase = InstallPhase.Cancelled,
                Status = job.PauseReason is null ? "Canceled." : "Paused.",
                CanCancel = false,
            };
            lock (_gate)
            {
                if (ReferenceEquals(_activeJob, job))
                {
                    if (job.PublishProgress)
                        PublishProgressLocked(p);
                }
            }
            return new InstallResult { Ok = false, Message = job.PauseReason is null ? "Canceled." : "Paused." };
        }
        catch (Exception ex)
        {
            leaveIntentPaused = resumeRecoveredIntent;
            AppLog.Warn($"Store job for \"{game.Title}\" threw: {ex.GetType().Name}: {ex.Message}");
            var failedMessage = $"The store action for {game.Title} did not finish. Try again.";
            reviewReason = failedMessage;
            InstallResult result;
            lock (_gate)
            {
                var cancelled = job.CancelRequested || job.Cts.IsCancellationRequested;
                result = new InstallResult
                {
                    Ok = false,
                    Message = cancelled ? (job.PauseReason is null ? "Canceled." : "Paused.") : failedMessage,
                };

                if (ReferenceEquals(_activeJob, job))
                {
                    if (job.PublishProgress)
                    {
                        PublishProgressLocked(new InstallProgress
                        {
                            GameId = game.Id,
                            Phase = cancelled ? InstallPhase.Cancelled : InstallPhase.Failed,
                            Percent = _lastProgress.Percent,
                            Status = result.Message,
                            CanCancel = false,
                        });
                    }
                }
            }
            return result;
        }
        finally
        {
            lock (_gate)
            {
                // A paused download already left its intent paused (PauseActiveLocked).
                if (job.PauseReason is null)
                    FinishDownloadIntentLocked(downloadIntent,
                    leaveIntentPaused && !job.CancelRequested && !job.Cts.IsCancellationRequested, reviewReason, haltQueue);
                if (ReferenceEquals(_activeJob, job))
                    _activeJob = null;
            }
            job.Cts.Dispose();
            DrainQueue();
        }
    }

    private JobState StartJobLocked(GameEntry game, CancellationToken outer, bool publishProgress,
        DownloadIntent? downloadIntent)
    {
        var job = new JobState(
            CancellationTokenSource.CreateLinkedTokenSource(outer),
            game.Id,
            game.Store,
            publishProgress) { DownloadIntent = downloadIntent };
        _activeJob = job;
        PublishDownloadQueueChangedLocked();
        if (job.PublishProgress)
        {
            PublishProgressLocked(new InstallProgress
            {
                GameId = game.Id,
                Phase = InstallPhase.Preparing,
                Percent = null,
                Status = "Starting…",
                CanCancel = true,
            });
        }
        return job;
    }

    private void DrainQueue()
    {
        PendingJob? next;
        JobState? claimed = null;
        lock (_gate)
        {
            // Claim the slot in the same lock as the dequeue. Otherwise a new
            // request can start first, and this job would be queued again
            // without its waiter and report a false "queued" success.
            next = DequeueNextRunnableLocked();
            if (next is not null)
                claimed = StartJobLocked(next.Game, next.Outer, next.PublishProgress, next.DownloadIntent);
        }

        if (next is null || claimed is null) return;

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await RunJobAsync(
                        next.Game,
                        next.Work,
                        next.Outer,
                        next.PublishProgress,
                        entitlementMutation: next.EntitlementMutation,
                        revalidateQueuedExecution: true,
                        queuedAccountScope: next.AccountScope,
                        downloadIntent: next.DownloadIntent,
                        resumeRecoveredIntent: next.ResumeRecoveredIntent,
                        claimedJob: claimed)
                    .ConfigureAwait(false);
                next.Completion?.TrySetResult(result);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Queued install failed: " + ex.Message);
                next.Completion?.TrySetResult(new InstallResult { Ok = false, Message = $"{next.Game.Title} could not start. Try again." });
            }
        });
    }

    /// <summary>
    /// A caller that cancelled its wait on a queued job leaves the PendingJob
    /// behind with a dead Outer token. Skip those instead of running surprise
    /// installs, and settle their completion source so nothing stays pending.
    /// </summary>
    private PendingJob? DequeueNextRunnableLocked()
    {
        if (_activeJob is not null) return null;
        // A hold keeps downloads waiting, as at admission; a queued uninstall or repair still runs.
        var held = _holds.Count > 0;
        var waiting = new List<PendingJob>();
        try
        {
            while (_pendingJobs.TryDequeue(out var candidate))
            {
                if (held && candidate.DownloadIntent is not null)
                {
                    waiting.Add(candidate);
                    continue;
                }
                if (AdmitQueuedLocked(candidate))
                    return candidate;
            }
            return null;
        }
        finally
        {
            // Waiting downloads keep their place ahead of the rest.
            if (waiting.Count > 0)
            {
                var rest = _pendingJobs.ToArray();
                _pendingJobs.Clear();
                foreach (var job in waiting.Concat(rest)) _pendingJobs.Enqueue(job);
            }
        }
    }

    /// <summary>True when a dequeued job may run; otherwise its waiter and saved request are settled.</summary>
    private bool AdmitQueuedLocked(PendingJob candidate)
    {
        // The admission checks RunJobAsync makes for a fresh request, made
        // here because a dequeued job skips them.
        string? rejection = null;
        var running = !candidate.Outer.IsCancellationRequested && _activeOrLaunchingGames.Contains(candidate.Game.Id);
        if (candidate.Outer.IsCancellationRequested)
            rejection = $"{candidate.Game.Title} was removed from the queue.";
        else if (running)
            rejection = $"Close {candidate.Game.Title} before installing, updating, or uninstalling it.";
        else if (!PrepareDownloadIntentLocked(candidate.DownloadIntent, candidate.ResumeRecoveredIntent,
                     queued: false, out var persistenceError))
            rejection = persistenceError ?? "The download queue could not be saved.";
        if (rejection is null)
            return true;
        candidate.Completion?.TrySetResult(new InstallResult
        {
            Ok = false,
            Message = rejection,
        });
        // A download that reached the front while its game runs waits as paused instead of vanishing.
        FinishDownloadIntentLocked(candidate.DownloadIntent, pause: running, reason: running ? $"Paused because {candidate.Game.Title} was running. Resume it when you are done playing." : null);
        return false;
    }

    private async Task<(GameEntry Game, string? Error)> RevalidateQueuedEntitlementAsync(
        GameEntry queuedGame,
        IStoreAdapter adapter,
        EntitlementMutation mutation,
        string? expectedAccountScope,
        CancellationToken ct)
    {
        var scoped = (IStoreAccountScope)adapter;
        if (string.IsNullOrWhiteSpace(expectedAccountScope) ||
            !string.Equals(scoped.GetActiveAccountScope(), expectedAccountScope, StringComparison.Ordinal))
        {
            return (queuedGame,
                "Queued action canceled because the active store account changed. Refresh the library and try again.");
        }

        if (_revalidateQueuedGame is null)
        {
            return (queuedGame,
                "Queued action canceled because current ownership could not be revalidated. Refresh the library and try again.");
        }

        var current = await _revalidateQueuedGame(queuedGame.Id, ct).ConfigureAwait(false);
        if (current is null ||
            current.Store != queuedGame.Store ||
            !string.Equals(current.Id, queuedGame.Id, StringComparison.OrdinalIgnoreCase))
        {
            return (queuedGame,
                "Queued action canceled because this title is no longer available to the active store account.");
        }

        if (!string.Equals(scoped.GetActiveAccountScope(), expectedAccountScope, StringComparison.Ordinal))
        {
            return (queuedGame,
                "Queued action canceled because the active store account changed. Refresh the library and try again.");
        }

        if (EntitlementBlockReason(current) is { } entitlementError)
            return (current, entitlementError);
        if (!current.Owned)
            return (current, "Queued action canceled because current ownership could not be verified.");

        if (mutation == EntitlementMutation.Install && (current.Installed || !current.CanInstall))
        {
            return (current,
                current.Installed
                    ? "Queued install canceled because this title is already installed."
                    : "Queued install canceled because installation is no longer available.");
        }
        if (mutation == EntitlementMutation.Update && (!current.Installed || !current.UpdateAvailable))
        {
            return (current,
                "Queued update canceled because an update is no longer available for the current installation.");
        }

        return (current, null);
    }

    /// <summary>
    /// Steam downloads Steam started itself (its auto-update, or an install or update started in Steam) show live progress
    /// like Exo's own:
    /// each is followed with the same reader until Steam stops moving its files. Exo's own job, while one runs, owns
    /// the progress line; the followed one has nothing to cancel.
    /// </summary>
    public void FollowSteamTransfers(IEnumerable<GameEntry> games)
    {
        foreach (var game in games)
        {
            if (game.Store != StoreKind.Steam || game.LaunchTarget is not { Length: > 0 } appId) continue;
            if (game.Installed ? !game.UpdateAvailable : game.EntitlementState != EntitlementState.Owned) continue;
            lock (_gate)
            {
                if (_following.Contains(game.Id) || string.Equals(_activeJob?.GameId, game.Id, StringComparison.OrdinalIgnoreCase) ||
                    _pendingJobs.Any(pending => string.Equals(pending.Game.Id, game.Id, StringComparison.OrdinalIgnoreCase)))
                    continue;
            }
            if (!SteamAdapter.TransferRunning(appId)) continue;
            lock (_gate) if (!_following.Add(game.Id)) continue;
            _ = FollowAsync(game);
        }
    }

    private async Task FollowAsync(GameEntry game)
    {
        AppLog.Info($"Following Steam's own download of {game.Title}.");
        try { await SteamAdapter.FollowAsync(game, new FollowedProgress(this), CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { AppLog.Debug($"Following {game.Title}'s Steam download failed: {ex.Message}"); }
        finally
        {
            AppLog.Info($"Steam finished moving {game.Title}'s files.");
            lock (_gate)
            {
                _following.Remove(game.Id);
                if (_activeJob is null && string.Equals(_lastProgress.GameId, game.Id, StringComparison.OrdinalIgnoreCase) && _lastProgress.IsActive)
                    PublishProgressLocked(new InstallProgress { GameId = game.Id, Phase = InstallPhase.Idle, CanCancel = false });
            }
        }
    }

    private sealed class FollowedProgress(LaunchOrchestrator owner) : IProgress<InstallProgress>
    {
        public void Report(InstallProgress value)
        {
            lock (owner._gate)
            {
                if (owner._activeJob is not null) return;
                owner.PublishProgressLocked(new InstallProgress
                {
                    GameId = value.GameId, Phase = value.Phase, Percent = value.Percent, BytesDownloaded = value.BytesDownloaded,
                    BytesToDownload = value.BytesToDownload, BytesPerSecond = value.BytesPerSecond, Status = value.Status, CanCancel = false,
                });
            }
        }
    }

    /// <summary>Publishes while <see cref="_gate"/> is held so job events cannot
    /// be reordered across a cancel/replacement boundary.</summary>
    private void PublishProgressLocked(InstallProgress progress)
    {
        progress = WithRate(progress);
        var unchanged = SameProgress(_lastProgress, progress);
        _lastProgress = progress;
        if (unchanged) return;
        try { ProgressChanged?.Invoke(progress); }
        catch (Exception ex) { AppLog.Debug($"Progress observer failed: {ex.Message}"); }
    }

    // A download's speed, from its byte count, for stores that report bytes but no speed (Steam): smoothed over
    // samples at least half a second apart, and started over for another game or a count that went back.
    private (string Game, long Bytes, long At, double? Rate) _rate;

    private InstallProgress WithRate(InstallProgress p)
    {
        // A speed equal to the last one worked out here was carried forward by the merge, not reported by the store.
        if (p.BytesPerSecond is { } given && given != _rate.Rate || p.Phase != InstallPhase.Downloading || p.BytesDownloaded is not { } bytes)
            return p;
        var now = Environment.TickCount64;
        if (!string.Equals(_rate.Game, p.GameId, StringComparison.OrdinalIgnoreCase) || bytes < _rate.Bytes)
            _rate = (p.GameId, bytes, now, null);
        else if (now - _rate.At >= 500)
        {
            var sample = (bytes - _rate.Bytes) * 1000.0 / (now - _rate.At);
            _rate = (p.GameId, bytes, now, _rate.Rate is { } rate ? rate * 0.7 + sample * 0.3 : sample);
        }
        if (_rate.Rate is not { } speed) return p;
        return new InstallProgress
        {
            Sequence = p.Sequence, GameId = p.GameId, Phase = p.Phase, Percent = p.Percent, BytesDownloaded = p.BytesDownloaded,
            BytesToDownload = p.BytesToDownload, BytesPerSecond = speed, Status = p.Status, CanCancel = p.CanCancel,
        };
    }

    private static bool SameProgress(InstallProgress left, InstallProgress right) =>
        string.Equals(left.GameId, right.GameId, StringComparison.OrdinalIgnoreCase) &&
        left.Phase == right.Phase &&
        left.Percent == right.Percent &&
        left.BytesDownloaded == right.BytesDownloaded &&
        left.BytesToDownload == right.BytesToDownload &&
        left.BytesPerSecond == right.BytesPerSecond &&
        string.Equals(left.Status, right.Status, StringComparison.Ordinal) &&
        left.CanCancel == right.CanCancel;

    /// <summary>
    /// Same job, same phase, incoming has no live reading while current does.
    /// Phase changes (applying / finalizing) still publish.
    /// </summary>
    internal static bool IsWeakerProgressSample(InstallProgress current, InstallProgress incoming)
    {
        if (!string.Equals(current.GameId, incoming.GameId, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!current.IsActive || current.Phase != incoming.Phase) return false;
        if (incoming.Phase is InstallPhase.Completed or InstallPhase.Failed
            or InstallPhase.Cancelled or InstallPhase.Finalizing)
            return false;
        var incomingHas = incoming.Percent is >= 0 and <= 100 || incoming.BytesDownloaded is >= 0 ||
                          incoming.BytesToDownload is >= 0 || incoming.BytesPerSecond is >= 0;
        if (incomingHas) return false;
        return current.Percent is >= 0 and <= 100 || current.BytesDownloaded is >= 0 ||
               current.BytesToDownload is >= 0 || current.BytesPerSecond is >= 0;
    }

    internal static InstallProgress MergeProgressSample(InstallProgress current, InstallProgress incoming)
    {
        // CLI backends emit percentage and speed on separate lines. Keep the
        // newest value of each metric within one active phase, while resetting
        // counters when a backend restarts work without changing its phase.
        if (!current.IsActive || !incoming.IsActive || current.Phase != incoming.Phase ||
            !string.Equals(current.GameId, incoming.GameId, StringComparison.OrdinalIgnoreCase) ||
            incoming.Percent is { } percent && current.Percent is { } previousPercent && percent < previousPercent ||
            incoming.BytesDownloaded is { } bytes && current.BytesDownloaded is { } previousBytes && bytes < previousBytes)
            return incoming;

        return new InstallProgress
        {
            Sequence = incoming.Sequence,
            GameId = incoming.GameId,
            Phase = incoming.Phase,
            Percent = incoming.Percent ?? current.Percent,
            BytesDownloaded = incoming.BytesDownloaded ?? current.BytesDownloaded,
            BytesToDownload = incoming.BytesToDownload ?? current.BytesToDownload,
            BytesPerSecond = incoming.BytesPerSecond ?? current.BytesPerSecond,
            Status = incoming.Status,
            CanCancel = incoming.CanCancel,
        };
    }

    private sealed class OrderedProgress(Action<InstallProgress> report) : IProgress<InstallProgress>
    {
        public void Report(InstallProgress value) => report(value);
    }

    private LaunchOptions BuildOptions(GameEntry? game = null)
    {
        GameLaunchOverride? launch = null;
        if (game is not null)
            _settings.Current.LaunchOverrides.TryGetValue(game.Id, out launch);
        var antiCheat = IsAntiCheatTitle(game);
        return new LaunchOptions
        {
            CloseStoreUiAfterExit = _settings.Current.CloseStoreClientsAfterLaunch,
            MinimizeStoreUi = true,
            ExtraArgs = antiCheat ? null : launch?.ExtraArgs,
            WorkingDirectory = antiCheat ? null : launch?.WorkingDirectory,
            RunAsAdmin = !antiCheat && launch?.RunAsAdmin == true,
        };
    }

    internal static bool IsAntiCheatTitle(GameEntry? game)
    {
        if (game is null) return false;
        if (game.Store == StoreKind.Riot) return true;
        var title = game.Title ?? "";
        return title.Contains("Fortnite", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("VALORANT", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("League of Legends", StringComparison.OrdinalIgnoreCase);
    }

    private IStoreAdapter? Find(StoreKind store) =>
        _adapters.FirstOrDefault(a => a.Store == store);

    private string? ActiveAccountScope(StoreKind store) =>
        Find(store) is IStoreAccountScope scoped ? scoped.GetActiveAccountScope() : null;

    private IStoreAdapter? FindByGameId(string gameId)
    {
        var prefix = gameId.Split(':')[0];
        return _adapters.FirstOrDefault(a =>
            string.Equals(a.Id, prefix, StringComparison.OrdinalIgnoreCase));
    }
}
