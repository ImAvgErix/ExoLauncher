using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExoLauncher.Adapters;
using ExoLauncher.Helpers;
using ExoLauncher.Models;
using ExoLauncher.Ui;
using Microsoft.UI.Dispatching;
using Microsoft.Web.WebView2.Core;

namespace ExoLauncher.Services;

/// <summary>
/// JSON-RPC bridge between the React UI (WebView2) and native services.
/// UI owns pixels; host owns discovery, install/progress, launch, and deps.
/// </summary>
public sealed partial class WebHostBridge
{
    private readonly AppServices _services;
    private readonly DispatcherQueue _queue;
    private CoreWebView2? _web;
    private readonly DlssSwapService _dlss = new();
    private readonly CancellationTokenSource _bridgeLifetime = new();
    private readonly BridgeRequestRegistry _rpcRequests = new();
    private bool _detached;

    private CancellationTokenSource? _keyWatchCts;
    private int _libraryPushScheduled;
    private int _libraryPushQueued;
    private long _libraryPushedAtMs;
    private string? _libraryPushedJson;
    private int _riotHoursRefreshing;
    private readonly object _externalPlaytimeGate = new();
    private long _externalPlaytimeDueAtMs;
    private HashSet<string> _externalSessions = new(StringComparer.OrdinalIgnoreCase);
    private System.Threading.Timer? _externalExitTimer;
    private System.Threading.Timer? _runStatePoll;
    private int _runStatePolling;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public WebHostBridge(AppServices services, DispatcherQueue queue)
    {
        _services = services;
        _queue = queue;
        _services.Launcher.ProgressChanged += OnProgress;
        _services.Launcher.DownloadQueueChanged += OnDownloadQueueChanged;
        _services.Launcher.GameSessionCompleted += OnGameSessionCompleted;
        _services.Launcher.GameLaunched += OnGameLaunched;
        _services.Launcher.GameRunStateChanged += OnGameRunStateChanged;
        _services.Shaders.Changed += OnShadersChanged;
        _services.Store.Changed += OnStoreChanged;
        _services.Achievements.SummaryChanged += OnAchievementsChanged;
        _services.Store.SaleFound += OnStoreSale;
        _services.Library.LibraryUpdated += OnLibraryUpdated;
        _services.Library.LibraryUpdated += FollowSteamTransfers;
        _services.Library.LibraryUpdated += SteamOverlaySoon;
        InstalledSizeCache.MeasurementUpdated += OnLibraryUpdated;
        _runStatePoll = new System.Threading.Timer(_ => PollRunStates(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// A game started outside Exo (from Riot Client, Steam, a shortcut) raises no
    /// launch event, and the library-push check runs at most every 30 s and only
    /// after a library update. Every 5 s one process snapshot is matched against
    /// the installed games' folders, so a start or exit reaches game.runState
    /// (Exo Mouse's game profiles, Play/Stop) within seconds.
    /// </summary>
    private void PollRunStates()
    {
        if (_detached || Interlocked.Exchange(ref _runStatePolling, 1) == 1) return;
        try
        {
            var installed = _services.Library.PeekCachedLibrary()
                .SelectMany(g => g.Variants.Count == 0 ? [g] : g.Variants
                    .Where(v => v.Installed)
                    .Select(v => v.ToGameEntry(g)))
                .Where(g => g.Installed && !string.IsNullOrWhiteSpace(g.Path))
                .ToArray();
            if (installed.Length > 0) _services.Launcher.GetGameRunStates(installed, underRootOnly: true);
        }
        catch (Exception ex) { AppLog.Debug("Run-state poll failed: " + ex.GetType().Name); }
        finally { Interlocked.Exchange(ref _runStatePolling, 0); }
    }

    private static async Task<object> SaveWebPreferencesAsync(JsonElement patch)
    {
        var preferences = WebClientPreferences.Save(patch);
        if (App.MainAppWindow is { } window)
        {
            if (preferences.MusicService is { } service) window.SelectMusicService(service);
            if (new[] { "microphone", "camera", "clipboard", "notifications" }.Any(key => patch.TryGetProperty(key, out _)))
                await window.ResetWebPermissionGrantsAsync();
        }
        return preferences;
    }

    public void Attach(CoreWebView2 web)
    {
        ObjectDisposedException.ThrowIf(_detached, this);
        _services.GogAuth.AttachDispatcher(_queue);
        _services.EpicAuth.AttachDispatcher(_queue);
        _web = web;
        web.WebMessageReceived += OnMessage;
        web.NavigationStarting += OnBridgeNavigationStarting;
        ScheduleRiotHoursRefresh();
        ScheduleReportedHoursRefresh();
    }

    private void OnBridgeNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (e.Cancel) return;
        _rpcRequests.CancelAll();
        // A new page has none of the rows the last push carried.
        Volatile.Write(ref _libraryPushedJson, null);
        try { Interlocked.Exchange(ref _keyWatchCts, null)?.Cancel(); } catch { }
    }

    public void Detach()
    {
        if (_detached) return;
        _detached = true;
        try { _bridgeLifetime.Cancel(); } catch { }
        try { Interlocked.Exchange(ref _keyWatchCts, null)?.Cancel(); } catch { }
        if (_web is not null)
        {
            try { _web.WebMessageReceived -= OnMessage; } catch { }
            try { _web.NavigationStarting -= OnBridgeNavigationStarting; } catch { }
        }
        _web = null;
        _services.Launcher.ProgressChanged -= OnProgress;
        _services.Launcher.DownloadQueueChanged -= OnDownloadQueueChanged;
        _services.Launcher.GameSessionCompleted -= OnGameSessionCompleted;
        _services.Launcher.GameLaunched -= OnGameLaunched;
        _services.Launcher.GameRunStateChanged -= OnGameRunStateChanged;
        _services.Shaders.Changed -= OnShadersChanged;
        _services.Store.Changed -= OnStoreChanged;
        _services.Achievements.SummaryChanged -= OnAchievementsChanged;
        _services.Store.SaleFound -= OnStoreSale;
        _services.Library.LibraryUpdated -= OnLibraryUpdated;
        _services.Library.LibraryUpdated -= FollowSteamTransfers;
        _services.Library.LibraryUpdated -= SteamOverlaySoon;
        InstalledSizeCache.MeasurementUpdated -= OnLibraryUpdated;
        if (Volatile.Read(ref _driverEvents) is { } driverEvents) NvidiaDriverService.Changed -= driverEvents;
        if (Volatile.Read(ref _hidWired) == 1)
        {
            _services.Hid.Input -= OnHidInput;
            _services.Hid.Changed -= OnHidChanged;
        }
        lock (_externalPlaytimeGate)
        {
            _externalExitTimer?.Dispose();
            _externalExitTimer = null;
        }
        _runStatePoll?.Dispose();
        _runStatePoll = null;
    }

    // Store tools report a sample per output line; the UI repaints at 4 Hz.
    // Running samples of one job post at most every 250 ms (the latest wins);
    // a new job, phase, cancel state or end posts at once and drops any older
    // pending sample so events never arrive out of order.
    private const int ProgressPostIntervalMs = 250;
    private readonly object _progressGate = new();
    private InstallProgress? _progressPosted;
    private InstallProgress? _progressPending;
    private long _progressPostedAtMs;

    private void OnProgress(InstallProgress p)
    {
        lock (_progressGate)
        {
            var last = _progressPosted;
            var now = Environment.TickCount64;
            var urgent = !p.IsActive || last is null || !last.IsActive || last.Phase != p.Phase ||
                last.CanCancel != p.CanCancel ||
                !string.Equals(last.GameId, p.GameId, StringComparison.OrdinalIgnoreCase);
            if (urgent || now - _progressPostedAtMs >= ProgressPostIntervalMs)
            {
                PostProgressLocked(p, now);
                return;
            }
            var scheduled = _progressPending is not null;
            _progressPending = p;
            if (scheduled) return;
            var wait = (int)Math.Max(1, ProgressPostIntervalMs - (now - _progressPostedAtMs));
            _ = Task.Delay(wait).ContinueWith(_ =>
            {
                lock (_progressGate)
                {
                    if (_progressPending is { } pending && !_detached)
                        PostProgressLocked(pending, Environment.TickCount64);
                    _progressPending = null;
                }
            }, TaskScheduler.Default);
        }
    }

    private void PostProgressLocked(InstallProgress p, long now)
    {
        _progressPending = null;
        _progressPosted = p;
        _progressPostedAtMs = now;
        // The page reloads the library the moment it hears "completed": the library must already know it changed,
        // or that reload reuses the store's last answer and the new game shows as not installed.
        if (p.Phase == InstallPhase.Completed) _services.Library.Invalidate();
        PostEvent("install.progress", MapProgress(p));
    }

    // A Steam download Steam started itself gets live progress too (see LaunchOrchestrator.FollowSteamTransfers).
    private void FollowSteamTransfers()
    {
        try { _services.Launcher.FollowSteamTransfers(_services.Library.PeekCachedLibrary()); }
        catch (Exception ex) { AppLog.Debug("Following Steam downloads failed: " + ex.Message); }
    }

    private void OnDownloadQueueChanged() => PostEvent("install.queue", new
    {
        queuedGameIds = _services.Launcher.QueuedGameIds,
        pausedDownloads = _services.Launcher.PausedDownloads,
        held = _services.Launcher.HoldReason,
    });

    // Run state goes out before launch.status: the UI counts each run-state
    // push as a newer read, so the game.get that launch.status triggers must
    // be issued after it or it would be dropped as stale.
    private void OnGameLaunched(GameEntry game)
    {
        OnGameRunStateChanged(game.Id, _services.Launcher.GetGameRunState(game));
        PostEvent("launch.status", new { gameId = game.Id, ok = true, message = "Game started.", phase = "running" });
    }

    private void OnGameSessionCompleted(GameEntry game)
    {
        OnGameRunStateChanged(game.Id, _services.Launcher.GetGameRunState(game));
        PostEvent("launch.status", new { gameId = game.Id, ok = true, message = "Game closed.", phase = "stopped" });
        // Foreground tracking selects the next game or desktop after the window closes.
    }

    /// <summary>Same flags game.get projects for this exact id.</summary>
    private void OnGameRunStateChanged(string gameId, GameRunState state) =>
        PostEvent("game.runState", new { id = gameId, isRunning = state.IsRunning, canStop = state.CanStop });

    private void OnLibraryUpdated()
    {
        // Cover warm used to fire this once per downloaded poster, each time
        // mapping and serialising the whole library on the UI thread. Throttle
        // to one push per 80 ms; a trailing pass still lands the last cover.
        Interlocked.Exchange(ref _libraryPushQueued, 1);
        if (Interlocked.CompareExchange(ref _libraryPushScheduled, 1, 0) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                do
                {
                    Interlocked.Exchange(ref _libraryPushQueued, 0);
                    var wait = 80 - (Environment.TickCount64 - Interlocked.Read(ref _libraryPushedAtMs));
                    if (wait > 0 && wait < 1_000)
                        await Task.Delay((int)wait).ConfigureAwait(false);
                    if (_detached) return;
                    Interlocked.Exchange(ref _libraryPushedAtMs, Environment.TickCount64);
                    try
                    {
                        // LibraryService.OnWarmBatch already reapplies cache URLs
                        // before it raises LibraryUpdated. Publish that snapshot;
                        // do not regroup the entire library a second time here.
                        var games = _services.Library.PeekCachedLibrary();
                        var json = ExoBridgeProtocol.SerializeEvent("library.updated", new
                        {
                            games = games.Select(game => MapGame(game)).ToList(),
                            count = games.Count,
                        });
                        // Cover warms, size measurements and derived refreshes
                        // often settle on the rows the page already has. The
                        // page re-parses and re-merges every row it is sent, so
                        // an unchanged library is not sent again.
                        if (!string.Equals(json, _libraryPushedJson, StringComparison.Ordinal))
                        {
                            _libraryPushedJson = json;
                            PostSerialized(json);
                        }
                        SyncExternalPlaytime(games);
                    }
                    catch (Exception ex)
                    {
                        AppLog.Debug("library.updated failed: " + ex.Message);
                    }
                }
                while (!_detached && Interlocked.CompareExchange(ref _libraryPushQueued, 0, 1) == 1);
            }
            finally
            {
                Interlocked.Exchange(ref _libraryPushScheduled, 0);
                if (!_detached && Volatile.Read(ref _libraryPushQueued) != 0)
                    OnLibraryUpdated();
            }
        });
    }

    // A process snapshot per library push was the cost here. Sync at most every
    // 30 s; an external game's exit raises no push, so a 60 s poll runs only
    // while a session Exo did not launch is open.
    private void SyncExternalPlaytime(IReadOnlyList<GameEntry> games, bool force = false)
    {
        lock (_externalPlaytimeGate)
        {
            var now = Environment.TickCount64;
            if (_detached || !force && now < _externalPlaytimeDueAtMs) return;
            _externalPlaytimeDueAtMs = now + 30_000;
            var watched = games.Where(game => game.Installed &&
                (game.Store is StoreKind.Riot or StoreKind.Epic || PlaytimeService.HasActiveSession(game.Id))).ToArray();
            var states = _services.Launcher.GetGameRunStates(watched);
            var running = states.Where(row => row.Value.IsRunning).Select(row => row.Key).ToArray();
            // Play-owned sessions already exist and end with their launch; the rest are external.
            _externalSessions = running
                .Where(id => _externalSessions.Contains(id) || !PlaytimeService.HasActiveSession(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            PlaytimeService.SyncObservedRunning(running);
            if (_externalSessions.Count > 0)
            {
                _externalExitTimer ??= new System.Threading.Timer(
                    _ => PollExternalPlaytime(), null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
            }
            else
            {
                _externalExitTimer?.Dispose();
                _externalExitTimer = null;
            }
        }
    }

    private void PollExternalPlaytime()
    {
        try { SyncExternalPlaytime(_services.Library.PeekCachedLibrary(), force: true); }
        catch (Exception ex) { AppLog.Debug("External playtime poll failed: " + ex.GetType().Name); }
    }


    private void OnMessage(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_detached) return;
        if (!WebViewTrustPolicy.IsTrustedBridgeSource(e.Source))
        {
            AppLog.Warn("Blocked a privileged WebView message from an untrusted origin.");
            return;
        }

        string? raw = null;
        try { raw = e.TryGetWebMessageAsString(); } catch { }
        if (string.IsNullOrWhiteSpace(raw))
        {
            try { raw = e.WebMessageAsJson; } catch { return; }
        }
        if (string.IsNullOrWhiteSpace(raw)) return;
        _ = HandleAsync(raw);
    }

    private async Task HandleAsync(string raw)
    {
        string? id = null;
        string? method = null;
        BridgeRequestRegistry.Lease? scope = null;
        var started = Stopwatch.GetTimestamp();
        try
        {
            if (!ExoBridgeProtocol.TryParseRequest(raw, out var request))
            {
                // Well formed but too large: answer, so the page does not wait for a reply that never comes.
                if (request.Id.Length > 0 && !_detached)
                {
                    AppLog.Warn($"RPC {request.Method}: request of {raw.Length} characters is over the limit.");
                    PostResponse(request.Id, ok: false, error: "That is too large for Exo to take.");
                }
                return;
            }
            id = request.Id;
            method = request.Method;
            var paramsEl = request.Params;
            var hasParams = request.HasParams;
            if (_detached) return;
            if (method == "rpc.cancel")
            {
                var target = ReadString(paramsEl, hasParams, "requestId");
                if (target is { Length: > 0 and <= 128 }) _rpcRequests.Cancel(target);
                return;
            }
            // Any Discord request means the page wants Discord's events (calls, video), not only Home or sign-in.
            if (method.StartsWith("discord.", StringComparison.Ordinal)) DiscordEvents();
            scope = _rpcRequests.Begin(id, method, _bridgeLifetime.Token);
            var requestToken = scope?.Token ?? _bridgeLifetime.Token;
            requestToken.ThrowIfCancellationRequested();

            object? result = method switch
            {
                "library.get" => await LibraryGetAsync(paramsEl, hasParams).WaitAsync(requestToken).ConfigureAwait(false),
                "library.refresh" => await LibraryGetAsync(paramsEl, hasParams: true, force: true,
                    full: ReadBool(paramsEl, hasParams, "full") == true).WaitAsync(requestToken).ConfigureAwait(false),
                "keys.preview" => await KeysPreviewAsync().ConfigureAwait(true),
                "keys.activate" => await KeysActivateAsync(paramsEl, hasParams).ConfigureAwait(true),
                "game.get" => await GameGetAsync(paramsEl, hasParams).ConfigureAwait(true),
                "game.launch" => await GameLaunchAsync(paramsEl, hasParams).ConfigureAwait(true),
                "game.stop" => await GameStopAsync(paramsEl, hasParams).ConfigureAwait(true),
                "game.install" => await GameInstallAsync(paramsEl, hasParams).ConfigureAwait(true),
                "game.update" => await GameUpdateAsync(paramsEl, hasParams).ConfigureAwait(true),
                "game.resumeDownload" => await GameResumeDownloadAsync(paramsEl, hasParams).ConfigureAwait(true),
                "game.uninstall" => await GameUninstallAsync(paramsEl, hasParams).ConfigureAwait(true),
                "game.repair" => await GameRepairAsync(paramsEl, hasParams).ConfigureAwait(true),
                "game.extras" => await GameExtrasAsync(paramsEl, hasParams).ConfigureAwait(true),
                "game.openFolder" => GameOpenFolder(paramsEl, hasParams),
                "game.properties" => await GamePropertiesAsync(paramsEl, hasParams).ConfigureAwait(true),
                "game.properties.set" => await GamePropertiesSetAsync(paramsEl, hasParams).ConfigureAwait(true),
                "art.replace" => await ArtworkReplaceAsync(paramsEl, hasParams).ConfigureAwait(true),
                "art.reset" => await ArtworkResetAsync(paramsEl, hasParams).ConfigureAwait(true),
                "art.refetch" => await ArtworkRefetchAsync(paramsEl, hasParams).ConfigureAwait(true),
                "library.refreshAll" => await LibraryRefreshAllAsync(_bridgeLifetime.Token).ConfigureAwait(true),
                "game.cancelInstall" => _services.Launcher.Cancel(ReadString(paramsEl, hasParams, "id")),
                "game.pauseInstall" => _services.Launcher.Pause(ReadString(paramsEl, hasParams, "id") ?? ""),
                "game.dequeue" => _services.Launcher.Dequeue(ReadString(paramsEl, hasParams, "id") ?? ""),
                "game.progress" => GameProgress(paramsEl, hasParams),
                "stores.auth" => await StoresAuthAsync(paramsEl, hasParams).ConfigureAwait(true),
                "playtime.riotRefresh" => await RiotHoursRefreshAsync(paramsEl, hasParams).ConfigureAwait(true),
                "playtime.refresh" => await PlaytimeRefreshAsync(paramsEl, hasParams).ConfigureAwait(true),
                "web.preferences.get" => WebClientPreferences.Reload(),

                "web.preferences.set" => await SaveWebPreferencesAsync(paramsEl),
                "shell.painted" => App.MainAppWindow is { } paintedWindow
                    && paintedWindow.DispatcherQueue.TryEnqueue(paintedWindow.RevealPainted)
                    ? new { ok = true }
                    : new { ok = false },
                "shell.prepare" => App.MainAppWindow is { } prepareWindow
                    ? await prepareWindow.PrepareConnectedRoomsAsync(ReadString(paramsEl, hasParams, "musicService"))
                    : new { ok = false },
                "music.setBounds" => App.MainAppWindow is { } musicWindow
                    ? musicWindow.SetMusicBounds(
                        ReadString(paramsEl, hasParams, "service"),
                        ReadBool(paramsEl, hasParams, "visible") == true,
                        ReadDouble(paramsEl, hasParams, "left") ?? 0,
                        ReadDouble(paramsEl, hasParams, "top") ?? 0,
                        ReadDouble(paramsEl, hasParams, "width") ?? 0,
                        ReadDouble(paramsEl, hasParams, "height") ?? 0,
                        ReadBool(paramsEl, hasParams, "background") == true,
                        hasParams ? DesktopClientHost.ReadCutouts(paramsEl) : null)
                    : new { ok = false, message = "The app window is unavailable." },
                "music.playback" => App.MainAppWindow?.MusicPlayback() ?? new { ok = false, playback = (object?)null },
                "music.control" => App.MainAppWindow is { } controlWindow
                    ? await controlWindow.ControlMusicAsync(ReadString(paramsEl, hasParams, "action"),
                        ReadDouble(paramsEl, hasParams, "positionMs")).ConfigureAwait(true)
                    : new { ok = false, message = "The app window is unavailable." },
                "music.volume" => App.MainAppWindow is { } volumeWindow
                    ? await volumeWindow.MusicVolumeAsync(ReadDouble(paramsEl, hasParams, "level"),
                        ReadBool(paramsEl, hasParams, "muted")).ConfigureAwait(true)
                    : new { ok = false, message = "The app window is unavailable." },
                "music.install" => App.MainAppWindow is { } installMusicWindow
                    ? await installMusicWindow.InstallMusicAsync(ReadString(paramsEl, hasParams, "service")).ConfigureAwait(true)
                    : new { ok = false, message = "The app window is unavailable." },
                "music.open" => App.MainAppWindow is { } openMusicWindow
                    ? await openMusicWindow.OpenMusicAsync(ReadString(paramsEl, hasParams, "service")).ConfigureAwait(true)
                    : new { ok = false, message = "The app window is unavailable." },
                "spotify.start" => App.MainAppWindow is { } spotifyWindow
                    ? await spotifyWindow.SpotifyStartAsync().ConfigureAwait(true)
                    : new { ok = false, state = "error", message = "The app window is unavailable." },
                "spotify.leave" => App.MainAppWindow?.SpotifyLeave() ?? new { ok = false },
                "spotify.link" => await SpotifyLinks.LinkAsync(ReadString(paramsEl, hasParams, "url")).ConfigureAwait(true),
                "spotify.signIn" => App.MainAppWindow is { } spotifySignInWindow
                    ? await spotifySignInWindow.SpotifySignInAsync().ConfigureAwait(true)
                    : new { ok = false, message = "The app window is unavailable." },
                "spotify.view" => App.MainAppWindow is { } spotifyViewWindow
                    ? await spotifyViewWindow.SpotifyViewAsync(ReadString(paramsEl, hasParams, "view"),
                        ReadString(paramsEl, hasParams, "uri"), ReadString(paramsEl, hasParams, "query"),
                        (int)(ReadDouble(paramsEl, hasParams, "offset") ?? 0)).ConfigureAwait(true)
                    : new { ok = false, message = "The app window is unavailable." },
                "spotify.act" => App.MainAppWindow is { } spotifyActWindow
                    ? await spotifyActWindow.SpotifyActAsync(ReadString(paramsEl, hasParams, "action"),
                        ReadString(paramsEl, hasParams, "uri"), ReadString(paramsEl, hasParams, "context"),
                        ReadDouble(paramsEl, hasParams, "index") is { } index ? (int)index : null,
                        ReadBool(paramsEl, hasParams, "on"), ReadString(paramsEl, hasParams, "uid"),
                        ReadString(paramsEl, hasParams, "query"), ReadString(paramsEl, hasParams, "name"),
                        ReadString(paramsEl, hasParams, "description"), ReadString(paramsEl, hasParams, "image")).ConfigureAwait(true)
                    : new { ok = false, message = "The app window is unavailable." },
                "ytmusic.start" or "applemusic.start" => App.MainAppWindow is { } webMusicWindow
                    ? await webMusicWindow.WebMusicStartAsync(method == "ytmusic.start" ? "youtube" : "apple").ConfigureAwait(true)
                    : new { ok = false, state = "error", message = "The app window is unavailable." },
                "ytmusic.view" or "applemusic.view" => App.MainAppWindow is { } webMusicViewWindow
                    ? await webMusicViewWindow.WebMusicViewAsync(method == "ytmusic.view" ? "youtube" : "apple", ReadString(paramsEl, hasParams, "view"),
                        ReadString(paramsEl, hasParams, "uri"), ReadString(paramsEl, hasParams, "query"),
                        (int)(ReadDouble(paramsEl, hasParams, "offset") ?? 0)).ConfigureAwait(true)
                    : new { ok = false, message = "The app window is unavailable." },
                "ytmusic.act" or "applemusic.act" => App.MainAppWindow is { } webMusicActWindow
                    ? await webMusicActWindow.WebMusicActAsync(method == "ytmusic.act" ? "youtube" : "apple", ReadString(paramsEl, hasParams, "action"),
                        ReadString(paramsEl, hasParams, "uri"), ReadString(paramsEl, hasParams, "context"),
                        ReadDouble(paramsEl, hasParams, "index") is { } webIndex ? (int)webIndex : null,
                        ReadBool(paramsEl, hasParams, "on"), ReadString(paramsEl, hasParams, "uid"),
                        ReadString(paramsEl, hasParams, "query"), ReadString(paramsEl, hasParams, "text")).ConfigureAwait(true)
                    : new { ok = false, message = "The app window is unavailable." },
                "music.signIn" => App.MainAppWindow is { } signInWindow
                    ? await signInWindow.MusicSignInAsync(ReadString(paramsEl, hasParams, "service")).ConfigureAwait(true)
                    : new { ok = false, message = "The app window is unavailable." },
                "music.warm" => App.MainAppWindow?.WarmMusic() ?? new { ok = false },
                "music.reload" => App.MainAppWindow?.ReloadMusic()
                    ?? new { ok = false, message = "The app window is unavailable." },
                "shaders.addon" => App.MainAppWindow is { } shadersStatusWindow
                    ? await Task.Run(shadersStatusWindow.ShadersAddonStatus).ConfigureAwait(true) : new { ok = false, present = false },
                "shaders.call" => App.MainAppWindow is { } shadersWindow
                    ? await shadersWindow.ShadersAddonCallAsync(ReadString(paramsEl, hasParams, "method"),
                        hasParams && paramsEl.TryGetProperty("params", out var shadersParams) ? System.Text.Json.Nodes.JsonNode.Parse(shadersParams.GetRawText()) : null,
                        requestToken).ConfigureAwait(true)
                    : new { ok = false, message = "The app window is unavailable." },
                "capture.status" => App.MainAppWindow?.CaptureStatus() ?? new { ok = false },
                "capture.settings" => App.MainAppWindow is { } captureWindow
                    ? await captureWindow.CaptureSetSettingsAsync(paramsEl).ConfigureAwait(true) : new { ok = false },
                "capture.act" => App.MainAppWindow is { } actWindow
                    ? await actWindow.CaptureActAsync(ReadString(paramsEl, hasParams, "action")).ConfigureAwait(true) : new { ok = false },
                "capture.list" => App.MainAppWindow is { } listWindow
                    ? await Task.Run(() => listWindow.CaptureList(ReadString(paramsEl, hasParams, "gameId"), (int?)ReadDouble(paramsEl, hasParams, "limit"))).ConfigureAwait(true)
                    : new { ok = false },
                // Each finds its file by scanning the captures folder, then moves, writes or decodes it: off the UI thread.
                "capture.item" => App.MainAppWindow is { } itemWindow
                    ? await Task.Run(() => itemWindow.CaptureItemAsync(ReadString(paramsEl, hasParams, "id"), ReadString(paramsEl, hasParams, "action"))).ConfigureAwait(true) : new { ok = false },
                "capture.edit" => App.MainAppWindow is { } editWindow
                    ? await Task.Run(() => editWindow.CaptureEditAsync(ReadString(paramsEl, hasParams, "id"), ReadString(paramsEl, hasParams, "preset"),
                        ReadDouble(paramsEl, hasParams, "startMs"), ReadDouble(paramsEl, hasParams, "endMs"))).ConfigureAwait(true) : new { ok = false },
                "capture.saveImage" => App.MainAppWindow is { } imageWindow
                    ? await Task.Run(() => imageWindow.CaptureSaveImageAsync(ReadString(paramsEl, hasParams, "id"), ReadString(paramsEl, hasParams, "png"))).ConfigureAwait(true) : new { ok = false },
                "friends.badge" => App.MainAppWindow?.FriendsBadge() ?? new { count = 0 },
                "deps.list" => await Task.Run(DepsList).ConfigureAwait(true),
                "replaced.scan" => await Task.Run(() => ReplacedScan(ReadBool(paramsEl, hasParams, "clean") == true)).ConfigureAwait(true),
                "replaced.remove" => await ReplacedRemoveAsync(ReadString(paramsEl, hasParams, "id") ?? "", requestToken).ConfigureAwait(true),
                "desktopClients.install" => App.MainAppWindow is { } desktopInstallWindow
                    ? await desktopInstallWindow.InstallDesktopClientAsync(ReadString(paramsEl, hasParams, "id")).ConfigureAwait(true)
                    : new { ok = false, message = "The app window is unavailable." },
                "desktopClients.status" => App.MainAppWindow?.DesktopClientsStatus()
                    ?? new { ok = false, apps = Array.Empty<object>() },
                "deps.offerInstall" => await DepsOfferInstallAsync(paramsEl, hasParams).ConfigureAwait(true),
                "deps.cancelInstall" => _services.Dependencies.CancelInstall(ReadString(paramsEl, hasParams, "id") ?? ""),
                "stores.check" => await StoresCheckAsync().ConfigureAwait(false),
                "stores.matrix" => await Task.Run(StoreMatrixMapped).ConfigureAwait(true),
                "settings.get" => BuildSettings(),
                "settings.set" => SetSettings(paramsEl, hasParams),
                "accounts.list" => await Task.Run(AccountsList).ConfigureAwait(true),
                "accounts.switch" => await AccountsSwitchAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "accounts.add" => await AccountsAddAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "accounts.remove" => await AccountsRemoveAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "accounts.signOut" => await AccountsSignOutAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "accounts.epicImport" => await AccountsEpicImportAsync(requestToken).ConfigureAwait(true),
                "accounts.epicAdd" => await AccountsEpicAddAsync(requestToken).ConfigureAwait(true),
                "accounts.epicRemove" => await AccountsEpicRemoveAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "shell.minimize" => MinimizeWindow(),
                "shell.maximize" => ToggleMaximize(),
                "shell.windowState" => WindowState(),
                "shell.close" => CloseWindow(),
                "shell.openUrl" => OpenUrl(paramsEl, hasParams),
                "shell.showStore" => await ShowStoreAsync(paramsEl, hasParams).ConfigureAwait(true),
                "shell.pickFolder" => await PickFolderAsync().ConfigureAwait(true),
                "shell.pickProgram" => await PickProgramAsync().ConfigureAwait(true),
                "shell.setChromeLayout" => SetChrome(paramsEl, hasParams),
                "app.version" => new { version = _services.AppVersion },
                "app.log" => LogFromUi(paramsEl, hasParams),
                "app.repairCache" => await RepairCacheAsync(paramsEl, hasParams).ConfigureAwait(false),
                "app.openLogs" => OpenLogsFolder(),
                "app.checkUpdate" => await CheckUpdateAsync(ReadBool(paramsEl, hasParams, "manual") == true, requestToken).ConfigureAwait(true),
                "app.installUpdate" => await InstallUpdateAsync(requestToken).ConfigureAwait(true),
                "dlss.status" => await DlssStatusAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "dlss.updateAll" => await DlssUpdateAllAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "dlss.restore" => await DlssRestoreAsync(paramsEl, hasParams).ConfigureAwait(true),
                "dlss.consent" => await DlssConsentAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                // Windows' administrator prompt blocks the thread that asks until it is answered.
                "dlss.indicator" => await Task.Run(() => DlssIndicatorAsync(paramsEl, hasParams)).ConfigureAwait(true),
                "shaders.status" => await ShadersStatusAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "shaders.estimates" => await ShadersEstimatesAsync(requestToken).ConfigureAwait(true),
                "hdr.state" => await Task.Run(HdrState).ConfigureAwait(true),
                "stores.quiet" => await QuietStoresAsync(requestToken).ConfigureAwait(true),
                "hdr.game" => await HdrGameAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "gpu.display" => await GpuDeadlineAsync(GpuDisplayAsync(paramsEl, hasParams), TimeSpan.FromSeconds(25), requestToken).ConfigureAwait(true),
                "discord.status" => DiscordStatus(paramsEl, hasParams),
                "discord.signIn" => DiscordSignIn(paramsEl, hasParams),
                "discord.signOut" => await DiscordSignOutAsync().ConfigureAwait(true),
                "app.notify" => hasParams ? AppNotify(paramsEl) : new { ok = false },
                "discord.home" => DiscordHome(),
                "discord.channels" => DiscordChannels(paramsEl, hasParams),
                "discord.messages" => await DiscordMessagesAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "discord.send" => await DiscordSendAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "discord.edit" => await DiscordEditAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "discord.delete" => await DiscordDeleteAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "discord.ack" => await DiscordAckAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "discord.thread" => await DiscordThreadAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "discord.react" => await DiscordReactAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "discord.emojis" => DiscordEmojis(paramsEl, hasParams),
                "discord.view" => DiscordView(paramsEl, hasParams),
                "discord.upload" => DiscordUpload(paramsEl, hasParams),
                "discord.voice" => DiscordVoice(paramsEl, hasParams),
                "discord.audio" => DiscordAudio(paramsEl, hasParams),
                "discord.ring" => await DiscordRingAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "discord.friends" => DiscordFriends(),
                "discord.friend" => await DiscordFriendAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "discord.markRead" => await DiscordMarkReadAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "discord.setStatus" => await DiscordSetStatusAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "discord.members" => DiscordMembers(paramsEl, hasParams),
                "discord.mentionable" => DiscordMentionable(paramsEl, hasParams),
                "discord.colors" => DiscordColors(paramsEl, hasParams),
                "discord.pins" => await DiscordPinsAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "discord.search" => await DiscordSearchAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "discord.gifs" => await DiscordGifsAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "discord.stream" => DiscordStream(paramsEl, hasParams),
                "discord.golive" => await DiscordGoLiveAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "discord.camera" => DiscordCamera(paramsEl, hasParams),
                "discord.gifFavorites" => await DiscordGifFavoritesAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
            "discord.emojiFavorites" => await DiscordEmojiFavoritesAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "discord.server" => await DiscordServerAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "discord.admin" => await DiscordAdminAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "discord.profile" => await DiscordProfileAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "discord.threads" => await DiscordThreadsAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "discord.stickers" => await DiscordStickersAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "discord.stickerData" => await DiscordStickerDataAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "discord.poll" => await DiscordPollAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "discord.soundboard" => await DiscordSoundboardAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "discord.sticker" => await DiscordSendStickerAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "discord.mentions" => await DiscordMentionsAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "discord.forYou" => await DiscordForYouAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "discord.unreads" => DiscordUnreads(),
                "discord.reactors" => await DiscordReactorsAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "discord.fileText" => await DiscordFileTextAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "discord.image" => await DiscordImageAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "discord.channel" => await DiscordChannelActionAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "discord.apps" => await DiscordAppsAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "discord.typing" => await DiscordTypingAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "discord.userSettings" => await DiscordUserSettingsAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "discord.account" => await DiscordAccountAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "discord.shop" => await DiscordShopAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "discord.font" => await DiscordFontAsync(paramsEl, hasParams).ConfigureAwait(true),
                "discord.clientSettings" => DiscordClientSettings(paramsEl, hasParams),
                "discord.micTest" => DiscordMicTest(paramsEl, hasParams),
                "discord.voiceTune" => await DiscordVoiceTuneAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "lucid" => await Task.Run(() => LucidAsync(paramsEl, hasParams)).ConfigureAwait(true),
                "lucid.headphones" => await Task.Run(LucidHeadphones).ConfigureAwait(true),
                "lucid.update" => await Task.Run(LucidUpdateAsync).ConfigureAwait(true),
                "discord.voiceCheck" => await DiscordClient.Voice.VoiceCheck.RecordAsync(ReadInt(paramsEl, "seconds", 3, 10, 8), requestToken).ConfigureAwait(true),
                "discord.sound" => DiscordSound(paramsEl, hasParams),
                "gpu.driver" => await GpuDeadlineAsync(Task.Run(() => GpuDriverAsync(paramsEl, hasParams, requestToken), requestToken), TimeSpan.FromSeconds(55), requestToken).ConfigureAwait(true),
                "gpu.profile" => await GpuDeadlineAsync(GpuProfileAsync(paramsEl, hasParams, requestToken), TimeSpan.FromSeconds(25), requestToken).ConfigureAwait(true),
                "gpu.import" => await GpuImportAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "store.home" => await StoreHomeAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "store.search" => await StoreSearchAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "store.game" => await StoreGameAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "store.gameOffers" => await StoreGameOffersAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "store.wishlist" => await StoreWishlistAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                "store.open" => await StoreOpenAsync(paramsEl, hasParams, requestToken).ConfigureAwait(true),
                // Listing opens every device to read its reports: off the UI thread, like the calls below.
                "hid.list" => await Task.Run(() => HidList(paramsEl, hasParams)).ConfigureAwait(true),
                "mouse.profiles.get" => MouseProfilesGet(),
                "mouse.profiles.set" => MouseProfilesSet(paramsEl, hasParams),
                "hid.open" or "hid.close" or "hid.sendReport" or "hid.sendFeatureReport" or "hid.receiveFeatureReport" or "hid.receiveInputReport"
                    => await Task.Run(() => HidCallAsync(method, paramsEl, hasParams, requestToken)).ConfigureAwait(true),

                "twitch.connect" => await IgdbTwitchAuth.ConnectAsync().ConfigureAwait(true),
                _ => throw new InvalidOperationException($"Unknown method: {method}")
            };

            if (!_detached && scope?.Token.IsCancellationRequested != true)
                PostResponse(id!, ok: true, result: result);
        }
        catch (OperationCanceledException)
        {
            if (!_detached && id is not null) PostResponse(id, ok: false, error: "The request was canceled or timed out.");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"RPC {method} {id}: {ex.Message}");
            // Handlers throw InvalidOperation/InvalidData with a line meant for the user; file and
            // Windows errors carry paths, which stay in native code (AGENTS.md).
            if (!_detached && id is not null)
                PostResponse(id, ok: false, error: ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception
                    ? "Exo could not finish that. The details are in the log."
                    : ex.Message);
        }
        finally
        {
            scope?.Dispose();
            // A slow answer is what the UI waited on; installs, launches and
            // store sign-ins are long by nature and are left out.
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (elapsed >= 250 && method is not null && !IsLongRunningRpc(method))
                AppLog.Info($"PERF rpc method={method} elapsedMs={elapsed:F0}");
        }
    }

    private static bool IsLongRunningRpc(string method) =>
        method.StartsWith("game.", StringComparison.Ordinal) && method is not "game.get" ||
        method.Contains("install", StringComparison.OrdinalIgnoreCase) ||
        method.Contains("Update", StringComparison.Ordinal) && method is not "app.checkUpdate" ||
        method is "shell.pickFolder" or "shell.pickProgram" or "library.refresh" or "dlss.restore" or "library.refreshAll" or "gpu.import";
    private int _libraryTimingLogged;

    private async Task<object> LibraryGetAsync(JsonElement p, bool hasParams, bool force = false, bool full = false)
    {
        if (hasParams && p.ValueKind == JsonValueKind.Object
            && p.TryGetProperty("force", out var f) && f.ValueKind == JsonValueKind.True)
            force = true;

        var libraryStarted = Stopwatch.GetTimestamp();
        var games = await _services.Library.GetLibraryAsync(force, full).ConfigureAwait(false);
        var libraryMs = Stopwatch.GetElapsedTime(libraryStarted).TotalMilliseconds;
        var settings = _services.Settings.Current;
        var projectionStarted = Stopwatch.GetTimestamp();
        var mapped = await Task.Run(() => games.Select(game => MapGame(game)).ToList()).ConfigureAwait(false);
        var projectionMs = Stopwatch.GetElapsedTime(projectionStarted).TotalMilliseconds;
        if (Interlocked.Exchange(ref _libraryTimingLogged, 1) == 0)
            AppLog.Info($"PERF first library.get libraryMs={libraryMs:F0} projectionMs={projectionMs:F0} count={mapped.Count}");
        return new
        {
            games = mapped,
            count = games.Count,
            stores = MapStoreMatrix(
                _services.Library.PeekStoreMatrix() ?? Array.Empty<LibraryService.StoreBackendStatus>()),
            progress = MapProgress(_services.Launcher.CurrentProgress),
            queuedGameIds = _services.Launcher.QueuedGameIds,
            pausedDownloads = _services.Launcher.PausedDownloads,
            sortMode = settings.SortMode,
            recent = settings.Recent,
        };
    }

    private async Task<object> GameGetAsync(JsonElement p, bool hasParams)
    {
        var gameId = ReadString(p, hasParams, "id");
        if (string.IsNullOrWhiteSpace(gameId))
            return new { ok = false, message = "Missing game id." };

        var game = _services.Library.Find(gameId!);
        if (game is null)
        {
            await _services.Library.GetLibraryAsync().ConfigureAwait(true);
            game = _services.Library.Find(gameId!);
        }
        if (game is null)
            return new { ok = false, message = "Game not found." };

        // Finding a game started outside Exo reads every process on the PC: off the UI thread.
        var mapped = await Task.Run(() => MapGame(game, discoverExternalRunningGame: true)).ConfigureAwait(true);
        return new { ok = true, game = mapped };
    }

    private const int UiLogMaxChars = 4000;

    /// <summary>
    /// The web UI's error channel. Bounded, tagged, and written to the same
    /// rolling app.log so a render crash or swallowed RPC failure is diagnosable
    /// from the crash folder without any analytics.
    /// </summary>
    private static object LogFromUi(JsonElement p, bool hasParams)
    {
        var level = ReadString(p, hasParams, "level")?.Trim().ToLowerInvariant();
        var message = ReadString(p, hasParams, "message")?.Trim();
        if (string.IsNullOrEmpty(message)) return new { ok = false, message = "Missing message." };
        var context = ReadString(p, hasParams, "context")?.Trim();
        var stack = ReadString(p, hasParams, "stack")?.Trim();
        var line = "UI " + (string.IsNullOrEmpty(context) ? "" : "[" + context + "] ") + message;
        if (!string.IsNullOrEmpty(stack)) line += " | " + stack.ReplaceLineEndings(" ");
        if (line.Length > UiLogMaxChars) line = line[..UiLogMaxChars] + "…";
        switch (level)
        {
            case "error": AppLog.Error(line); break;
            case "warn": AppLog.Warn(line); break;
            default: AppLog.Info(line); break;
        }
        return new { ok = true };
    }

    private static object OpenLogsFolder()
    {
        try
        {
            Directory.CreateDirectory(PathHelper.LogsDir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(PathHelper.LogsDir) { UseShellExecute = true });
            return new { ok = true };
        }
        catch (Exception ex)
        {
            return new { ok = false, message = ex.Message };
        }
    }

    private async Task<object> GameLaunchAsync(JsonElement p, bool hasParams)
    {
        var gameId = ReadString(p, hasParams, "id");
        if (string.IsNullOrWhiteSpace(gameId))
            return new { ok = false, message = "Missing game id." };

        var game = _services.Library.Find(gameId!);
        if (game is null)
        {
            await _services.Library.GetLibraryAsync().ConfigureAwait(true);
            game = _services.Library.Find(gameId!);
        }
        if (game is null)
            return new { ok = false, message = "Game not found. Refresh the library." };

        if (game.Store == StoreKind.Epic)
        {
            // A disk-hydrated card may still name Legendary's old folder.
            // Refresh the manifest overlay before launch and process tracking.
            await _services.Library.GetLibraryAsync(force: true).ConfigureAwait(true);
            game = _services.Library.Find(gameId!);
            if (game is null)
                return new { ok = false, message = "Game not found. Refresh the library." };
        }

        // A Steam game with a EULA not yet accepted would stop at Steam's dialog: accepted first (Steam closes for a moment).
        if (game.Store == StoreKind.Steam && ExoLauncher.Adapters.Cli.SteamProtocol.IsValidAppId(game.LaunchTarget) && SteamEulaPending(game.LaunchTarget!))
            await ApplySteamOverlayAsync(null, userAsked: true, _bridgeLifetime.Token).ConfigureAwait(true);

        PostEvent("launch.status", new
        {
            gameId = game.Id,
            ok = true,
            message = "Preparing launch…",
            phase = "preparing",
        });

        var skipDeps = ReadBool(p, hasParams, "skipDeps") == true;
        var result = await _services.Launcher.LaunchAsync(game, skipDeps).ConfigureAwait(true);

        PostEvent("launch.status", new
        {
            gameId = game.Id,
            ok = result.Ok,
            message = result.Ok
                ? (string.IsNullOrWhiteSpace(result.Message) ? "Running" : result.Message)
                : result.Message,
            processId = result.ProcessId,
            backendStarted = result.BackendStarted,
            handoffOnly = result.HandoffOnly,
            needsDependencies = result.NeedsDependencies,
            phase = result.HandoffOnly ? "handoff" : (result.Ok ? "running" : (result.NeedsDependencies ? "needsDeps" : "failed")),
        });

        return MapDepAwareResult(result);
    }

    private async Task<object> GameStopAsync(JsonElement p, bool hasParams)
    {
        var gameId = ReadString(p, hasParams, "id");
        if (string.IsNullOrWhiteSpace(gameId))
            return new { ok = false, message = "Missing game id." };

        var game = _services.Library.Find(gameId!);
        if (game is null)
        {
            await _services.Library.GetLibraryAsync().ConfigureAwait(true);
            game = _services.Library.Find(gameId!);
        }
        if (game is null)
            return new { ok = false, message = "Game not found. Refresh the library." };

        var result = await _services.Launcher.StopGameAsync(game).ConfigureAwait(true);
        PostEvent("launch.status", new
        {
            gameId = game.Id,
            ok = result.Ok,
            message = result.Message,
            phase = result.Ok ? "stopped" : "stopFailed",
        });
        return new { ok = result.Ok, message = result.Message };
    }

    private async Task<object> KeysPreviewAsync()
    {
        string? raw = null;
        try
        {
            var content = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            if (content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
                raw = await content.GetTextAsync();
        }
        catch
        {
            /* clipboard can be empty or locked */
        }

        var key = GameKeyActivation.Normalize(raw);
        if (!GameKeyActivation.LooksLikeKey(key))
            return new { ok = true, looksLikeKey = false, store = "unknown", key = "" };

        return new
        {
            ok = true,
            looksLikeKey = true,
            store = GameKeyActivation.DetectStore(key),
            key,
        };
    }

    private async Task<object> KeysActivateAsync(JsonElement p, bool hasParams)
    {
        var key = GameKeyActivation.Normalize(ReadString(p, hasParams, "key"));
        if (key.Length == 0)
            return new { ok = false, message = "Paste a product key first." };

        var store = GameKeyActivation.ResolveStore(key, ReadString(p, hasParams, "store"));
        if (store is not ("steam" or "epic" or "gog"))
            return new { ok = false, message = "Choose Steam, Epic, or GOG." };

        var targetStore = store switch { "epic" => StoreKind.Epic, "gog" => StoreKind.Gog, _ => StoreKind.Steam };
        var ownedBefore = _services.Library.PeekCachedLibrary()
            .Where(game => game.Store == targetStore && game.Owned).Select(game => game.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var url = GameKeyActivation.RedeemUrl(store, key);
        if (store == "steam") OpenSteamProtocol(url);
        else if (!ExoUrlLaunch.TryStart(url))
            return new { ok = false, message = "Could not open the redeem page." };

        StartKeyWatch(targetStore, ownedBefore);
        return new
        {
            ok = true,
            store,
            message = store switch
            {
                "steam" => "Steam's key page opened with your key filled in. Accept Steam's agreement and press Continue.",
                "gog" => "GOG's redeem page opened in your browser with your key filled in. Press Continue, then Redeem.",
                _ => "Epic's redeem page opened in your browser. Paste your key there and press Redeem.",
            },
        };
    }

    private void StartKeyWatch(StoreKind store, HashSet<string> owned)
    {
        if (_detached) return;
        try { _keyWatchCts?.Cancel(); } catch { /* previous watch */ }
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_bridgeLifetime.Token);
        _keyWatchCts = cts;
        var ct = cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                // Three bounded, store-specific reads replace fifteen full
                // library invalidations. Keys never overwrite the clipboard.
                foreach (var delay in new[] { 5, 10, 15 })
                {
                    await Task.Delay(TimeSpan.FromSeconds(delay), ct).ConfigureAwait(false);
                    var next = await _services.Library.RefreshStoreAsync(store, ct).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    var added = next.FirstOrDefault(game =>
                        game.Store == store && game.Owned && !owned.Contains(game.Id));
                    if (added is null) continue;
                    PostEvent("keys.found", new
                    {
                        ok = true,
                        gameId = added.Id,
                        title = added.Title,
                        message = added.Title + " is in your library.",
                    });
                    return;
                }

                ct.ThrowIfCancellationRequested();
                PostEvent("keys.found", new
                {
                    ok = false,
                    message = "No new game was confirmed. Finish redeeming in the store, then refresh the library.",
                });
            }
            catch (OperationCanceledException)
            {
                /* dismissed */
            }
            catch (Exception ex)
            {
                AppLog.Debug("Key watch failed: " + ex.Message);
            }
            finally
            {
                Interlocked.CompareExchange(ref _keyWatchCts, null, cts);
                cts.Dispose();
            }
        });
    }

    private async Task<object> GameInstallAsync(JsonElement p, bool hasParams)
    {
        var gameId = ReadString(p, hasParams, "id");
        if (string.IsNullOrWhiteSpace(gameId))
            return new { ok = false, message = "Missing game id." };

        // Auto root: Settings override → %LOCALAPPDATA%\ExoLauncher\Games
        var requested = ReadString(p, hasParams, "path");
        if (string.IsNullOrWhiteSpace(requested))
            requested = _services.Settings.Current.DefaultInstallRoot;
        string path;
        if (string.IsNullOrWhiteSpace(requested))
            path = Helpers.PathHelper.GamesRoot;
        else if (NormalizeInstallRoot(requested) is { } validRoot)
            path = validRoot;
        // Add a game: the game's own .exe, its folder held to the same rules as a folder.
        else if (gameId == Adapters.LocalAdapter.AddPortableId && ProgramInAllowedFolder(requested) is { } program)
            path = program;
        else
            return new { ok = false, message = InstallRootRejected };

        var skipDeps = ReadBool(p, hasParams, "skipDeps") == true;

        await _services.Library.GetLibraryAsync().ConfigureAwait(true);
        var game = _services.Library.Find(gameId!)
                   ?? TrySynthesizeFromId(gameId!, p, hasParams);
        if (game is null)
            return new { ok = false, message = "Game not found. Refresh the library." };
        if (!game.Owned && !OfficialClientCatalog.IsOfficialClientCatalogInstall(game))
            return new { ok = false, message = "This title is not owned by the active store account. Buy it from the store first." };

        var result = await _services.Launcher.InstallAsync(game, path, skipDeps).ConfigureAwait(true);
        if (result.Ok)
            _services.Library.Invalidate();

        return new
        {
            ok = result.Ok,
            message = result.Message,
            path = result.Path,
            handoffOnly = result.HandoffOnly,
            needsDependencies = result.NeedsDependencies,
            missingDependencies = MapMissingDeps(result.MissingDependencies),
            queued = result.Queued,
            queuedGameIds = _services.Launcher.QueuedGameIds,
            pausedDownloads = _services.Launcher.PausedDownloads,
            progress = MapProgress(_services.Launcher.CurrentProgress),
        };
    }

    /// <summary>An existing .exe whose folder <see cref="NormalizeInstallRoot"/> takes; its full path, else null.</summary>
    private static string? ProgramInAllowedFolder(string? path)
    {
        try
        {
            var full = Path.GetFullPath(path!.Trim());
            return string.Equals(Path.GetExtension(full), ".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(full) &&
                NormalizeInstallRoot(Path.GetDirectoryName(full)) is not null ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or System.Security.SecurityException) { return null; }
    }

    private const string InstallRootRejected =
        "Choose a folder on a local drive, outside Windows and not Program Files itself.";

    /// <summary>
    /// Install roots from the UI reach store clients as targets. Only an
    /// absolute folder on a local fixed or removable drive is accepted: not a
    /// share, not under Windows, not a Program Files root, and either existing
    /// or creatable (no path segment is a file). Returns the full path.
    /// </summary>
    private static string? NormalizeInstallRoot(string? path)
    {
        try
        {
            var trimmed = path?.Trim();
            if (string.IsNullOrEmpty(trimmed) || !Path.IsPathFullyQualified(trimmed) ||
                trimmed.StartsWith(@"\\", StringComparison.Ordinal) || trimmed.StartsWith("//", StringComparison.Ordinal))
                return null;
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed));
            var drive = new DriveInfo(Path.GetPathRoot(full)!);
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
                return null;
            static bool Same(string left, string right) => string.Equals(left,
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!string.IsNullOrEmpty(windows) && (Same(full, windows) || ProcessHelper.IsPathUnderRoot(full, windows)))
                return null;
            foreach (var programFiles in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     })
            {
                if (!string.IsNullOrEmpty(programFiles) && Same(full, programFiles))
                    return null;
            }
            for (var probe = full; probe is not null; probe = Path.GetDirectoryName(probe))
            {
                if (Directory.Exists(probe)) return full;
                if (File.Exists(probe)) return null;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private async Task<object> GameResumeDownloadAsync(JsonElement p, bool hasParams)
    {
        var gameId = ReadString(p, hasParams, "id");
        if (string.IsNullOrWhiteSpace(gameId)) return new { ok = false, message = "Missing game id." };
        var result = await _services.Launcher.ResumeDownloadAsync(gameId).ConfigureAwait(true);
        if (result.Ok && !result.Queued) _services.Library.Invalidate();
        return new
        {
            ok = result.Ok, message = result.Message, path = result.Path, queued = result.Queued,
            handoffOnly = result.HandoffOnly, queuedGameIds = _services.Launcher.QueuedGameIds,
            pausedDownloads = _services.Launcher.PausedDownloads,
            progress = MapProgress(_services.Launcher.CurrentProgress),
        };
    }

    /// <summary>
    /// Install target the library lookup missed: an owned or installed source
    /// row from the cached library, or an official store client Exo can install.
    /// </summary>
    private GameEntry? TrySynthesizeFromId(string gameId, JsonElement p, bool hasParams)
    {
        var title = ReadString(p, hasParams, "title") ?? gameId;
        var fromLibrary = TryLibraryOwnedSource(gameId);
        if (fromLibrary is not null)
        {
            return new GameEntry
            {
                Id = fromLibrary.Id,
                Title = string.IsNullOrWhiteSpace(title) || title == gameId ? fromLibrary.Title : title,
                Store = fromLibrary.Store,
                Installed = fromLibrary.Installed,
                Owned = fromLibrary.Owned,
                EntitlementState = fromLibrary.EntitlementState,
                CanInstall = !fromLibrary.Installed && fromLibrary.Owned && fromLibrary.CanInstall,
                Path = fromLibrary.Path,
                LaunchTarget = fromLibrary.LaunchTarget,
                CoverUrl = fromLibrary.CoverUrl,
                Status = fromLibrary.Installed ? fromLibrary.Status : "Not installed",
                Deps = fromLibrary.Deps,
                LaunchNote = fromLibrary.LaunchNote,
            };
        }

        // Every owned Steam app is a library row keyed steam:<appid>, so the
        // library lookup above already covers Steam; nothing else is synthesized.
        return OfficialClientCatalog.TrySynthesizeOfficialClientInstall(gameId, title);
    }

    private GameEntry? TryLibraryOwnedSource(string gameId)
    {
        foreach (var game in _services.Library.PeekCachedLibrary())
        {
            if (string.Equals(game.Id, gameId, StringComparison.OrdinalIgnoreCase) &&
                (game.Owned || game.Installed))
                return game;
            var variant = game.Variants.FirstOrDefault(item =>
                string.Equals(item.Id, gameId, StringComparison.OrdinalIgnoreCase));
            if (variant is not null && (variant.Owned || variant.Installed))
                return variant.ToGameEntry(game);
        }
        return null;
    }

    private async Task<object> GameUpdateAsync(JsonElement p, bool hasParams)
    {
        var gameId = ReadString(p, hasParams, "id");
        if (string.IsNullOrWhiteSpace(gameId))
            return new { ok = false, message = "Missing game id." };

        try
        {
            await _services.Library.GetLibraryAsync().ConfigureAwait(true);
            var game = _services.Library.Find(gameId!);
            if (game is null)
                return new { ok = false, message = "Game not found. Refresh the library." };

            var skipDeps = ReadBool(p, hasParams, "skipDeps") == true;
            var result = await _services.Launcher.UpdateAsync(game, skipDeps).ConfigureAwait(true);
            if (result.Ok)
                _services.Library.Invalidate();

            return new
            {
                ok = result.Ok,
                message = result.Message,
                path = result.Path,
                handoffOnly = result.HandoffOnly,
                needsDependencies = result.NeedsDependencies,
                missingDependencies = MapMissingDeps(result.MissingDependencies),
                queued = result.Queued,
                queuedGameIds = _services.Launcher.QueuedGameIds,
                pausedDownloads = _services.Launcher.PausedDownloads,
                progress = MapProgress(_services.Launcher.CurrentProgress),
            };
        }
        catch (Exception ex)
        {
            AppLog.Warn($"game.update failed: {ex}");
            return new
            {
                ok = false,
                message = "Update failed: " + ex.Message,
                progress = MapProgress(_services.Launcher.CurrentProgress),
            };
        }
    }

    private async Task<object> GameUninstallAsync(JsonElement p, bool hasParams)
    {
        var gameId = ReadString(p, hasParams, "id");
        if (string.IsNullOrWhiteSpace(gameId))
            return new { ok = false, message = "Missing game id." };

        await _services.Library.GetLibraryAsync().ConfigureAwait(true);
        var game = _services.Library.Find(gameId!);
        if (game is null)
            return new { ok = false, message = "Game not found." };

        var result = await _services.Launcher.UninstallAsync(game).ConfigureAwait(true);
        if (result.Ok && !result.Queued && !result.HandoffOnly)
            _services.Library.Invalidate();
        if (result.Ok && result.HandoffOnly)
            _ = FollowStoreRemovalAsync(game);
        return new
        {
            ok = result.Ok,
            queued = result.Queued,
            handoffOnly = result.HandoffOnly,
            queuedGameIds = _services.Launcher.QueuedGameIds,
            pausedDownloads = _services.Launcher.PausedDownloads,
            message = result.Message,
        };
    }

    /// <summary>
    /// A removal handed to the store's own app finishes there, after Exo answered. Its install is checked until it
    /// is gone (20 minutes at most), then that store is re-read so the card changes by itself: GOG, Ubisoft and
    /// Battle.net keep installs in the registry, where no library watcher sees one go.
    /// </summary>
    private async Task FollowStoreRemovalAsync(GameEntry game)
    {
        Func<bool> stillInstalled = game.Store == StoreKind.Gog && GogAdapter.ExtractGogId(game) is { Length: > 0 } gogId
            ? () => GogAdapter.GogRegistryStillInstalled(gogId)
            : () => OfficialInstalledLibraries.IsStillInstalled(game);
        var ct = _bridgeLifetime.Token;
        try
        {
            for (var deadline = DateTimeOffset.UtcNow.AddMinutes(20); DateTimeOffset.UtcNow < deadline;)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                if (await Task.Run(stillInstalled, ct).ConfigureAwait(false)) continue;
                AppLog.Info($"Store removal finished: gameId={game.Id}.");
                await _services.Library.RefreshStoreAsync(game.Store, ct).ConfigureAwait(false);
                return;
            }
        }
        catch (OperationCanceledException) { /* Exo is closing */ }
        catch (Exception ex) { AppLog.Debug($"Following the removal of {game.Id} stopped: {ex.GetType().Name}: {ex.Message}"); }
    }

    private async Task<object> GameRepairAsync(JsonElement p, bool hasParams)
    {
        var gameId = ReadString(p, hasParams, "id");
        if (string.IsNullOrWhiteSpace(gameId))
            return new { ok = false, message = "Missing game id." };

        await _services.Library.GetLibraryAsync().ConfigureAwait(true);
        var game = _services.Library.Find(gameId!);
        if (game is null)
            return new { ok = false, message = "Game not found." };

        var result = await _services.Launcher.RepairAsync(game).ConfigureAwait(true);
        return new
        {
            ok = result.Ok,
            queued = result.Queued,
            queuedGameIds = _services.Launcher.QueuedGameIds,
            pausedDownloads = _services.Launcher.PausedDownloads,
            message = result.Message,
            handoffOnly = result.HandoffOnly,
            progress = MapProgress(_services.Launcher.CurrentProgress),
        };
    }

    private async Task<object> GameExtrasAsync(JsonElement p, bool hasParams)
    {
        var gameId = ReadString(p, hasParams, "id");
        if (string.IsNullOrWhiteSpace(gameId))
            return new { ok = false, message = "Missing game id." };
        var game = _services.Library.Find(gameId!);
        if (game is null)
            return new { ok = false, message = "Game not found." };

        // Cached counts only; a played game with none (or old ones) is read in
        // the background and pushed as game.achievements.
        var achievements = await Task.Run(() => _services.Achievements.GetSummary(game.Id)).ConfigureAwait(true);
        if (game.PlaytimeMinutes > 0 || game.LastPlayedUtc is not null)
            _services.Achievements.RefreshInBackgroundIfStale(game, achievements);
        var hasAchievements = achievements is { Total: > 0 };
        return new
        {
            ok = true,
            achievementsUnlocked = hasAchievements ? achievements!.Unlocked : (int?)null,
            achievementsTotal = hasAchievements ? achievements!.Total : (int?)null,
        };
    }

    // Raised on a worker thread; PostEvent marshals to the dispatcher.
    private void OnAchievementsChanged(string gameId)
    {
        var summary = _services.Achievements.GetSummary(gameId);
        if (summary is not { Total: > 0 }) return;
        PostEvent("game.achievements", new { id = gameId, unlocked = summary.Unlocked, total = summary.Total });
    }

    private object GameOpenFolder(JsonElement p, bool hasParams)
    {
        var gameId = ReadString(p, hasParams, "id");
        if (string.IsNullOrWhiteSpace(gameId))
            return new { ok = false, message = "Missing game id." };

        var game = _services.Library.Find(gameId!);
        if (game is null)
            return new { ok = false, message = "Game not found. Refresh the library." };

        var path = game.Path;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            if (!string.IsNullOrWhiteSpace(game.LaunchTarget) && File.Exists(game.LaunchTarget))
                path = Path.GetDirectoryName(game.LaunchTarget);
        }

        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return new { ok = false, message = "Install folder not found." };

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{path}\"",
                UseShellExecute = true,
            });
            return new { ok = true, path };
        }
        catch (Exception ex)
        {
            return new { ok = false, message = ex.Message };
        }
    }

    private async Task<object> ArtworkReplaceAsync(JsonElement p, bool hasParams)
    {
        var gameId = ReadString(p, hasParams, "id");
        if (string.IsNullOrWhiteSpace(gameId))
            return new { ok = false, cancelled = false, message = "Missing game id." };
        var card = _services.Library.FindVisualCard(gameId);
        if (card is null)
            return new { ok = false, cancelled = false, message = "That title is not in your library." };
        if (string.Equals(card.Id, "local:add", StringComparison.OrdinalIgnoreCase) ||
            (!card.Owned && !card.Installed))
            return new { ok = false, cancelled = false, message = "Artwork controls are available for library titles." };

        // The RPC has no path parameter. Only this native picker can produce the
        // path passed to the image store.
        var picked = await PickImageFileAsync().ConfigureAwait(true);
        if (picked.Cancelled)
            return new { ok = false, cancelled = true, message = "No changes made." };
        if (picked.Path is null)
            return new { ok = false, cancelled = false, message = "The image picker could not open." };

        var result = await _services.Artwork.ReplaceAsync(gameId, picked.Path).ConfigureAwait(true);
        return MapArtworkMutation(result);
    }

    private async Task<object> ArtworkResetAsync(JsonElement p, bool hasParams)
    {
        var gameId = ReadString(p, hasParams, "id");
        if (string.IsNullOrWhiteSpace(gameId))
            return new { ok = false, cancelled = false, message = "Missing game id." };
        return MapArtworkMutation(await _services.Artwork.ResetAsync(gameId).ConfigureAwait(true));
    }

    private async Task<object> ArtworkRefetchAsync(JsonElement p, bool hasParams)
    {
        var gameId = ReadString(p, hasParams, "id");
        if (string.IsNullOrWhiteSpace(gameId))
            return new { ok = false, cancelled = false, message = "Missing game id." };
        return MapArtworkMutation(await _services.Artwork.RefetchAsync(gameId).ConfigureAwait(true));
    }

    private object MapArtworkMutation(GameArtworkService.MutationResult result) => new
    {
        result.Ok,
        result.Cancelled,
        result.Message,
        game = result.Game is null ? null : MapGame(result.Game),
        result.ArtRevision,
    };

    private async Task<object> StoresAuthAsync(JsonElement p, bool hasParams)
    {
        var storeId = ReadString(p, hasParams, "store");
        if (string.IsNullOrWhiteSpace(storeId))
            return new { ok = false, message = "Missing store id." };

        var adapter = _services.FindAdapterById(storeId!);
        if (adapter is null)
            return new { ok = false, message = "Unknown store." };

        AppLog.Info($"stores.auth: {adapter.Id} sign-in started.");
        var result = await adapter.AuthenticateAsync().ConfigureAwait(true);
        if (result.Handoff)
        {
            // The store signs in inside its own app. Only claim the handoff once
            // that app's window is actually open; nothing is connected yet.
            var opened = await OpenStoreClientAsync(adapter.Id).ConfigureAwait(true);
            AppLog.Info($"stores.auth: {adapter.Id} handoff to its own app: {(opened.Ok ? "opened" : opened.Message)}");
            return opened.Ok
                ? new { ok = true, message = result.Message, requiresUserAction = true, handoff = true }
                : new { ok = false, message = opened.Message, requiresUserAction = true, handoff = false };
        }
        if (result.Ok)
        {
            AppLog.Info($"stores.auth: {adapter.Id} connected. {result.Message}");
            _services.Library.InvalidateStoreMatrix();
        }
        else
            AppLog.Warn($"stores.auth: {adapter.Id} did not connect: {result.Message}");

        return new
        {
            ok = result.Ok,
            message = result.Message,
            requiresUserAction = result.RequiresUserAction,
            handoff = false,
        };
    }

    private void ScheduleRiotHoursRefresh()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(12), _bridgeLifetime.Token).ConfigureAwait(false);
                if (_detached) return;
                _ = await RefreshRiotHoursAsync(requestedId: null, force: false, _bridgeLifetime.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                /* host is shutting down */
            }
            catch (Exception ex)
            {
                AppLog.Debug("Riot hours refresh skipped: " + ex.GetType().Name);
            }
        });
    }

    private Task<object> RiotHoursRefreshAsync(JsonElement p, bool hasParams)
    {
        var requested = hasParams &&
                        p.ValueKind == JsonValueKind.Object &&
                        p.TryGetProperty("riotId", out var id) &&
                        id.ValueKind == JsonValueKind.String
            ? id.GetString()
            : null;
        return RefreshRiotHoursAsync(requested, force: true, CancellationToken.None);
    }

    private async Task<object> RefreshRiotHoursAsync(string? requestedId, bool force, CancellationToken ct)
    {
        if (!force &&
            PlaytimeService.TryGetImportedLifetimeObservedAt() is { } observed &&
            DateTimeOffset.UtcNow - observed < TimeSpan.FromHours(12))
        {
            return new { ok = true, skipped = true, message = "Hours are already current." };
        }

        var riotId = RiotIdStore.Normalize(requestedId) ?? RiotIdStore.TryRead();
        if (riotId is null)
            return new { ok = false, profilePrivate = false, message = "Save a Riot ID first (Name#TAG)." };

        if (!string.IsNullOrWhiteSpace(requestedId))
        {
            RiotIdStore.Save(riotId);
            PlaytimeService.ForgetRiotImportUnlessFor(riotId);
        }

        if (Interlocked.CompareExchange(ref _riotHoursRefreshing, 1, 0) != 0)
            return new { ok = false, profilePrivate = false, message = "Hours refresh is already running." };

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            var result = await TrackerGgPlaytime.LoadAsync(riotId, timeout.Token).ConfigureAwait(false);
            if (result.Ok && PlaytimeService.TryImportRiotLifetime(riotId, result.Minutes))
            {
                try
                {
                    await _services.Library.RefreshDerivedStateAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    AppLog.Debug("Riot hours library refresh skipped: " + ex.GetType().Name);
                }

                result.Minutes.TryGetValue("riot:valorant", out var valorantMinutes);
                result.Minutes.TryGetValue("riot:league_of_legends", out var leagueMinutes);
                return new
                {
                    ok = true,
                    profilePrivate = false,
                    message = result.Message,
                    valorantMinutes = valorantMinutes > 0 ? valorantMinutes : (int?)null,
                    leagueMinutes = leagueMinutes > 0 ? leagueMinutes : (int?)null,
                    profileUrl = TrackerGgPlaytime.ProfilePageUrl(riotId),
                };
            }

            return new
            {
                ok = false,
                profilePrivate = result.ProfilePrivate,
                message = result.Message,
                profileUrl = TrackerGgPlaytime.ProfilePageUrl(riotId),
            };
        }
        catch (OperationCanceledException)
        {
            return new { ok = false, profilePrivate = false, message = "tracker.gg did not answer in time." };
        }
        finally
        {
            Interlocked.Exchange(ref _riotHoursRefreshing, 0);
        }
    }

    /// <summary>The picked file's path, or why there is none. Only the host may name a file.</summary>
    private sealed record PickedFile(string? Path, bool Cancelled, string? Message);

    private async Task<PickedFile> PickImageFileAsync()
    {
        var tcs = new TaskCompletionSource<PickedFile>();
        void Run() => _ = RunPickImageAsync(tcs);

        if (!_queue.HasThreadAccess)
            _queue.TryEnqueue(Run);
        else
            Run();

        return await tcs.Task.ConfigureAwait(true);
    }

    private static async Task RunPickImageAsync(TaskCompletionSource<PickedFile> tcs)
    {
        try
        {
            var window = App.MainAppWindow;
            if (window is null)
            {
                tcs.TrySetResult(new PickedFile(null, true, "The file picker could not open."));
                return;
            }

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary;
            picker.ViewMode = Windows.Storage.Pickers.PickerViewMode.Thumbnail;
            foreach (var extension in new[] { ".png", ".jpg", ".jpeg", ".webp", ".gif" })
                picker.FileTypeFilter.Add(extension);

            var file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                tcs.TrySetResult(new PickedFile(null, true, null));
                return;
            }

            tcs.TrySetResult(new PickedFile(file.Path, false, null));
        }
        catch (Exception ex)
        {
            tcs.TrySetResult(new PickedFile(null, false, ex.Message));
        }
    }

    private async Task<object> StoresCheckAsync()
    {
        var check = await Task.Run(_services.Library.CheckStoresLocal).ConfigureAwait(false);
        return new
        {
            check.state,
            checkedAtUtc = check.checkedAtUtc.ToString("O"),
            check.code,
            stores = check.stores.Select(store => new
            {
                store.store,
                store.client,
                store.backend,
                store.session,
                store.cache,
                store.readiness,
                store.code,
            }).ToList(),
        };
    }

    /// <summary>
    /// Every caller uses this mapper; library.get cannot race out a status-only row.
    /// </summary>
    private object StoreMatrixMapped() =>
        MapStoreMatrix(_services.Library.StoreMatrix());

    private static object MapStoreMatrix(IReadOnlyList<LibraryService.StoreBackendStatus> statuses) =>
        statuses.Select(status => new
        {
            status.store,
            status.displayName,
            status.agentPresent,
            status.clientPresent,
            status.backendPresent,
            status.signedIn,
            status.cachePresent,
            status.detail,
            status.checkCode,
            checkedAtUtc = status.checkedAtUtc.ToString("O"),
        }).ToList();

    private object GameProgress(JsonElement p, bool hasParams)
    {
        var gameId = ReadString(p, hasParams, "id");
        return MapProgress(_services.Launcher.GetProgress(gameId));
    }

    private object DepsList() => new
    {
        items = _services.Dependencies.DetectAll().Select(d => new
        {
            id = d.Id,
            name = d.Name,
            status = d.Status,
            detail = d.Detail,
            canOfferInstall = d.CanOfferInstall,
            officialUrl = d.OfficialUrl,
        }).ToList(),
    };

    private async Task<object> DepsOfferInstallAsync(JsonElement p, bool hasParams)
    {
        var depId = ReadString(p, hasParams, "id");
        if (string.IsNullOrWhiteSpace(depId))
            return new { ok = false, message = "Missing dependency id." };
        return await _services.Dependencies.OfferInstallAsync(depId!, (bytes, total) =>
            PostEvent("deps.progress", new { id = depId, bytes, total })).ConfigureAwait(false);
    }

    private object BuildSettings()
    {
        var s = _services.Settings.Current;
        // Locked product defaults (SettingsService.EnforceProductDefaults) and
        // dead fields are not exposed: closeStoreClientsAfterLaunch,
        // autoInstallRedistributables, minimizeWhilePlaying, antiCheatSafeMode,
        // framelessMode, theme, copyPortableIntoLibrary, allowResize,
        // checkForUpdates, libraryShowInstalled, libraryShowOwned.
        return new
        {
            appVersion = _services.AppVersion,
            reduceMotion = s.ReduceMotion,
            framelessPinTop = s.FramelessPinTop,
            framelessPinSide = s.FramelessPinSide,
            textColour = s.TextColour,
            textSize = s.TextSize,
            glass = s.Glass,
            sortMode = s.SortMode,
            defaultInstallRoot = s.DefaultInstallRoot,
            preferredGameSources = s.PreferredGameSources,
            recent = s.Recent,
            onboardingComplete = s.OnboardingComplete,
            developerKeysVisible = ExoAppCredentials.DeveloperKeysVisible(),
            steamWebApiKeySet = SteamWebApiKeyStore.HasKey(),
            itchApiKeySet = ItchApiKeyStore.HasKey(),
            riotId = RiotIdStore.TryRead(),
            riotHoursImported = PlaytimeService.TryGetImportedLifetimeObservedAt() is not null,
            showLibraryTools = s.ShowLibraryTools,
            countHoursAcrossAccounts = s.CountHoursAcrossAccounts,
            hdrForGames = s.HdrForGames,
            hdrSdrGamma = s.HdrSdrGamma,
            discordNoiseSuppression = s.DiscordNoiseSuppression,
            discordQuietWhilePlaying = s.DiscordQuietWhilePlaying,
            pauseDownloadsWhilePlaying = s.PauseDownloadsWhilePlaying,
            downloadHoursStart = s.DownloadHoursStart,
            downloadHoursEnd = s.DownloadHoursEnd,
            autoUpdateGames = s.AutoUpdateGames,
            steamOverlayDefaultOn = s.SteamOverlayDefaultOn,
            quietStoreApps = s.QuietStoreApps,
            libraryFolders = s.LibraryFolders.Select(folder => new { id = folder.Id, name = folder.Name, color = folder.Color, games = folder.Games }),

            igdbTwitchClientIdSet = CatalogApiKeyStore.HasTwitchClientId(),
            igdbTwitchClientSecretSet = CatalogApiKeyStore.HasTwitchClientSecret(),
            igdbTwitchConnected = IgdbTwitchAuth.HasAppToken(),
            steamGridDbKeySet = CatalogApiKeyStore.HasSteamGridDb(),
            openXblKeySet = CatalogApiKeyStore.HasOpenXbl(),
        };
    }

    private object SetSettings(JsonElement p, bool hasParams)
    {
        if (!hasParams || p.ValueKind != JsonValueKind.Object)
            return BuildSettings();

        // Locked/dead keys (closeStoreClientsAfterLaunch, autoInstallRedistributables,
        // minimizeWhilePlaying, antiCheatSafeMode, framelessMode, theme,
        // copyPortableIntoLibrary, allowResize, checkForUpdates,
        // libraryShowInstalled, libraryShowOwned) are ignored if an older UI sends them.
        bool? onboard = null;
        bool? reduceMotion = null;
        bool? framelessPinTop = null, framelessPinSide = null;
        bool? showLibraryTools = null;
        string? textColour = null, textSize = null, glass = null;
        string? sort = null, root = null;
        Dictionary<string, string>? preferredGameSources = null;

        if (p.TryGetProperty("preferredGameSources", out var preferredSources))
        {
            if (preferredSources.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Preferred game sources must be an object.");
            preferredGameSources = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in preferredSources.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.String || preferredGameSources.Count >= 5_000)
                    throw new InvalidDataException("Invalid preferred game sources.");
                preferredGameSources[entry.Name] = entry.Value.GetString()!;
            }
        }

        if (p.TryGetProperty("onboardingComplete", out var ob) &&
            (ob.ValueKind is JsonValueKind.True or JsonValueKind.False))
            onboard = ob.GetBoolean();
        if (p.TryGetProperty("sortMode", out var sm) && sm.ValueKind == JsonValueKind.String)
            sort = sm.GetString();
        if (p.TryGetProperty("defaultInstallRoot", out var dr))
            root = dr.ValueKind == JsonValueKind.String ? dr.GetString() : (dr.ValueKind == JsonValueKind.Null ? "" : null);
        if (!string.IsNullOrWhiteSpace(root))
            root = NormalizeInstallRoot(root) ?? throw new InvalidDataException(InstallRootRejected);
        if (p.TryGetProperty("reduceMotion", out var rm) &&
            (rm.ValueKind is JsonValueKind.True or JsonValueKind.False))
            reduceMotion = rm.GetBoolean();
        if (p.TryGetProperty("framelessPinTop", out var fpt) &&
            (fpt.ValueKind is JsonValueKind.True or JsonValueKind.False))
            framelessPinTop = fpt.GetBoolean();
        if (p.TryGetProperty("framelessPinSide", out var fps) &&
            (fps.ValueKind is JsonValueKind.True or JsonValueKind.False))
            framelessPinSide = fps.GetBoolean();
        if (p.TryGetProperty("showLibraryTools", out var slt) &&
            (slt.ValueKind is JsonValueKind.True or JsonValueKind.False))
            showLibraryTools = slt.GetBoolean();
        bool? countHoursAcrossAccounts = null;
        if (p.TryGetProperty("countHoursAcrossAccounts", out var cha) &&
            (cha.ValueKind is JsonValueKind.True or JsonValueKind.False))
            countHoursAcrossAccounts = cha.GetBoolean();
        bool? hdrForGames = null;
        if (p.TryGetProperty("hdrForGames", out var hdr) &&
            (hdr.ValueKind is JsonValueKind.True or JsonValueKind.False))
            hdrForGames = hdr.GetBoolean();
        bool? discordNoiseSuppression = null;
        if (p.TryGetProperty("discordNoiseSuppression", out var dns) &&
            (dns.ValueKind is JsonValueKind.True or JsonValueKind.False))
            discordNoiseSuppression = dns.GetBoolean();
        bool? discordQuietWhilePlaying = null;
        if (p.TryGetProperty("discordQuietWhilePlaying", out var dq) &&
            (dq.ValueKind is JsonValueKind.True or JsonValueKind.False))
            discordQuietWhilePlaying = dq.GetBoolean();
        bool? hdrSdrGamma = null;
        if (p.TryGetProperty("hdrSdrGamma", out var sdrGamma) &&
            (sdrGamma.ValueKind is JsonValueKind.True or JsonValueKind.False))
            hdrSdrGamma = sdrGamma.GetBoolean();

        if (p.TryGetProperty("textColour", out var tc) && tc.ValueKind == JsonValueKind.String)
            textColour = tc.GetString();
        if (p.TryGetProperty("textSize", out var tsz) && tsz.ValueKind == JsonValueKind.String)
            textSize = tsz.GetString();
        if (p.TryGetProperty("glass", out var gl) && gl.ValueKind == JsonValueKind.String)
            glass = gl.GetString();
        var previousShowLibraryTools = _services.Settings.Current.ShowLibraryTools;
        var previousCountHours = _services.Settings.Current.CountHoursAcrossAccounts;
        // Settings validate and commit first; a rejected patch must not leave
        // the Riot ID or developer secrets half-applied.
        _services.Settings.ApplyPatch(
            sortMode: sort,
            defaultInstallRoot: root,
            onboardingComplete: onboard,
            reduceMotion: reduceMotion,
            textColour: textColour,
            textSize: textSize,
            glass: glass,
            framelessPinTop: framelessPinTop,
            framelessPinSide: framelessPinSide,
            showLibraryTools: showLibraryTools,
            preferredGameSources: preferredGameSources,
            countHoursAcrossAccounts: countHoursAcrossAccounts,
            hdrForGames: hdrForGames,
            hdrSdrGamma: hdrSdrGamma,
            discordNoiseSuppression: discordNoiseSuppression,
            discordQuietWhilePlaying: discordQuietWhilePlaying);
        if (hdrForGames is not null || hdrSdrGamma is not null) App.MainAppWindow?.SyncHdrSoon();
        if (showLibraryTools is { } tools && tools != previousShowLibraryTools)
            RefreshLibraryAfterToolsToggle();
        else if (countHoursAcrossAccounts is { } all && all != previousCountHours)
        {
            if (all) EpicAccounts.RefreshAddedInBackground();
            RefreshLibraryAfterToolsToggle();
        }
        ApplyDownloadSettings(p);
        ApplySteamOverlaySettings(p);
        if (p.TryGetProperty("libraryFolders", out var folders) && folders.ValueKind == JsonValueKind.Array)
            _services.Settings.SetLibraryFolders(folders.EnumerateArray().Where(folder => folder.ValueKind == JsonValueKind.Object).Select(folder => new LibraryFolder
            {
                Id = folder.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString()! : "",
                Name = folder.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString()! : "",
                Color = folder.TryGetProperty("color", out var color) && color.ValueKind == JsonValueKind.String ? color.GetString()! : "gray",
                Games = folder.TryGetProperty("games", out var list) && list.ValueKind == JsonValueKind.Array
                    ? list.EnumerateArray().Where(game => game.ValueKind == JsonValueKind.String).Select(game => game.GetString()!).ToList()
                    : [],
            }).ToList());

        if (p.TryGetProperty("riotId", out var riotId) && riotId.ValueKind == JsonValueKind.String)
        {
            if (!RiotIdStore.Save(riotId.GetString()))
                throw new InvalidOperationException("Riot ID was not saved.");
            // Hours imported for another Riot ID are not this player's.
            PlaytimeService.ForgetRiotImportUnlessFor(RiotIdStore.TryRead());
        }
        ApplyUserSecrets(p);
        ApplyReportedHoursSettings(p);
        if (ExoAppCredentials.DeveloperKeysVisible())
            ApplyDeveloperSecrets(p);
        return BuildSettings();
    }

    /// <summary>
    /// showLibraryTools is applied while a scan filters adapter rows, so the
    /// cached library still reflects the old value. Every adapter is re-read
    /// in the background; the scan raises LibraryUpdated, which pushes the
    /// existing <c>library.updated</c> event and replaces the UI's list.
    /// </summary>
    private void RefreshLibraryAfterToolsToggle()
    {
        _services.Library.Invalidate();
        _ = Task.Run(async () =>
        {
            try { await _services.Library.GetLibraryAsync(force: true).ConfigureAwait(false); }
            catch (Exception ex) { AppLog.Warn("Library refresh after showLibraryTools change failed: " + ex.Message); }
        });
    }

    /// <summary>Keys a player gets from their own account on a store or service. Saved in every build.</summary>
    private void ApplyUserSecrets(JsonElement p)
    {
        if (p.TryGetProperty("steamWebApiKey", out var steamKey) &&
            steamKey.ValueKind == JsonValueKind.String &&
            !SteamWebApiKeyStore.Save(steamKey.GetString()))
        {
            throw new InvalidOperationException("Steam Web API key was not saved.");
        }
        if (p.TryGetProperty("itchApiKey", out var itchKey) &&
            itchKey.ValueKind == JsonValueKind.String &&
            !ItchApiKeyStore.Save(itchKey.GetString()))
        {
            throw new InvalidOperationException("itch.io API key was not saved.");
        }
        if (p.TryGetProperty("steamGridDbKey", out var sgdb) &&
            sgdb.ValueKind == JsonValueKind.String &&
            !CatalogApiKeyStore.SaveSteamGridDb(sgdb.GetString()))
        {
            throw new InvalidOperationException("SteamGridDB key was not saved.");
        }
        if (p.TryGetProperty("steamWebApiKey", out _) ||
            p.TryGetProperty("itchApiKey", out _))
        {
            try { _services.Library.InvalidateStoreMatrix(); }
            catch { /* settings still saved */ }
        }
    }

    /// <summary>Exo's own developer-app credentials. Debug builds or EXO_SHOW_DEV_KEYS only.</summary>
    private static void ApplyDeveloperSecrets(JsonElement p)
    {
        if (p.TryGetProperty("igdbTwitchClientId", out var igdbId) &&
            igdbId.ValueKind == JsonValueKind.String)
        {
            if (!CatalogApiKeyStore.SaveTwitchClientId(igdbId.GetString()))
                throw new InvalidOperationException("IGDB Twitch client id was not saved.");
            IgdbTwitchAuth.Invalidate();
            if (string.IsNullOrWhiteSpace(igdbId.GetString()) && !IgdbTwitchAuth.ClearUserSession())
                throw new InvalidOperationException("Legacy Twitch session files could not be cleared.");
        }
        if (p.TryGetProperty("igdbTwitchClientSecret", out var igdbSecret) &&
            igdbSecret.ValueKind == JsonValueKind.String)
        {
            if (!CatalogApiKeyStore.SaveTwitchClientSecret(igdbSecret.GetString()))
                throw new InvalidOperationException("IGDB Twitch client secret was not saved.");
            IgdbTwitchAuth.Invalidate();
        }
    }


    public void NotifyWindowState(bool maximized) =>
        PostEvent("shell.window", new { maximized });

    /// The maximize button is a native region (snap layouts), so the page
    /// never sees the pointer over it; the host reports its hover instead.
    public void NotifyMaximizeHover(bool maximized, bool hover) =>
        PostEvent("shell.window", new { maximized, maximizeHover = hover });

    private object ToggleMaximize()
    {
        var maximized = false;
        try { maximized = App.MainAppWindow?.ToggleMaximize() ?? false; } catch { }
        return new { ok = true, maximized };
    }

    private object WindowState()
    {
        var maximized = false;
        try { maximized = App.MainAppWindow?.IsMaximized ?? false; } catch { }
        return new { ok = true, maximized };
    }

    /// The title band's controls in CSS px: every other point of the band drags.
    private object SetChrome(JsonElement p, bool hasParams)
    {
        Windows.Graphics.RectInt32[]? interactiveRegions = null;
        if (hasParams && p.TryGetProperty("interactiveRegions", out var regions) &&
            regions.ValueKind == JsonValueKind.Array && regions.GetArrayLength() <= 128)
        {
            var parsed = new List<Windows.Graphics.RectInt32>();
            foreach (var region in regions.EnumerateArray())
                if (ReadChromeRect(region) is { } rect) parsed.Add(rect);
            if (parsed.Count > 0) interactiveRegions = parsed.ToArray();
        }
        var maximizeButton = hasParams && p.TryGetProperty("maximizeButton", out var max) ? ReadChromeRect(max) : null;
        void Go()
        {
            try { App.MainAppWindow?.SetChromeLayout(interactiveRegions, maximizeButton); } catch { }
        }
        if (!_queue.HasThreadAccess) _queue.TryEnqueue(Go); else Go();
        return new { ok = true };
    }

    private static Windows.Graphics.RectInt32? ReadChromeRect(JsonElement region)
    {
        if (region.ValueKind != JsonValueKind.Object ||
            !region.TryGetProperty("x", out var rx) || rx.ValueKind != JsonValueKind.Number || !rx.TryGetInt32(out var x) || x is < 0 or > 32768 ||
            !region.TryGetProperty("y", out var ry) || ry.ValueKind != JsonValueKind.Number || !ry.TryGetInt32(out var y) || y < 0 ||
            !region.TryGetProperty("width", out var rw) || rw.ValueKind != JsonValueKind.Number || !rw.TryGetInt32(out var width) || width is <= 0 or > 32768 ||
            !region.TryGetProperty("height", out var rh) || rh.ValueKind != JsonValueKind.Number || !rh.TryGetInt32(out var height) || height <= 0)
            return null;
        // The title band is 52 CSS px; a slightly overflowing control still
        // needs its hole. Clip instead of dropping it.
        if (y > 96) return null;
        if (y + height > 96) height = 96 - y;
        return height <= 0 ? null : new Windows.Graphics.RectInt32(x, y, width, height);
    }

    private object MinimizeWindow()
    {
        void Go()
        {
            try { App.MainAppWindow?.MinimizeWindow(); } catch { }
        }
        if (!_queue.HasThreadAccess) _queue.TryEnqueue(Go); else Go();
        return new { ok = true };
    }

    private object CloseWindow()
    {
        void Go()
        {
            try { App.MainAppWindow?.CloseWindow(); } catch { }
            // WinUI's dispatcher can outlive its final unpackaged window. End
            // the application loop explicitly after Closed has flushed state;
            // otherwise the user sees no window but Exo keeps its files locked.
            try { Microsoft.UI.Xaml.Application.Current?.Exit(); } catch { }
        }
        // This RPC starts synchronously inside WebMessageReceived even on the
        // UI thread. Let that native callback return before destroying its
        // WebView and exiting WinUI's message loop.
        var queued = _queue.TryEnqueue(Go);
        return new { ok = queued };
    }

    private static DateTimeOffset _lastSteamProtocolUtc = DateTimeOffset.MinValue;
    private static string? _lastSteamProtocolUri;

    private object OpenUrl(JsonElement p, bool hasParams)
    {
        var url = ReadString(p, hasParams, "url");
        if (string.IsNullOrWhiteSpace(url)) return new { ok = false };
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !ExoUrlLaunch.IsAllowed(uri))
            return new { ok = false };
        try
        {
            if (uri.Scheme.Equals("steam", StringComparison.OrdinalIgnoreCase))
                return OpenSteamProtocol(uri.AbsoluteUri);

            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            return new { ok = true };
        }
        catch (Exception ex)
        {
            return new { ok = false, message = ex.Message };
        }
    }

    /// <summary>
    /// One protocol handoff + reveal main Steam chrome only. Do not restore
    /// steamwebhelper windows — that spawned multiple Steam taskbar entries.
    /// </summary>
    private static object OpenSteamProtocol(string absoluteUri)
    {
        var now = DateTimeOffset.UtcNow;
        if (string.Equals(absoluteUri, _lastSteamProtocolUri, StringComparison.OrdinalIgnoreCase) &&
            now - _lastSteamProtocolUtc < TimeSpan.FromSeconds(2))
            return new { ok = true, message = "Already opening Steam." };

        _lastSteamProtocolUri = absoluteUri;
        _lastSteamProtocolUtc = now;

        HiddenStoreRuntime.AllowUserOpen(StoreKind.Steam);

        // Process snapshots and window walks used to run here, on the RPC thread,
        // so Buy sat still until they finished. Queue all of it and answer now,
        // the same way ShowStoreAsync does.
        _ = Task.Run(async () =>
        {
            ProcessHelper.StartProtocol(absoluteUri);

            // Main steam.exe window only — helpers stay off the taskbar.
            var chrome = StoreWindowHider.SteamMainProcessNames;
            StoreWindowHider.RestoreStoreWindows(chrome);
            // Brief settle while Steam creates the store HWND; two nudges max.
            for (var i = 0; i < 2; i++)
            {
                await Task.Delay(600).ConfigureAwait(false);
                StoreWindowHider.RestoreStoreWindows(chrome);
            }
        });
        return new { ok = true };
    }


    /// <summary>
    /// Settings → Open a verified installed store client. This is deliberately
    /// an Open action only; it does not imply library or game control support.
    /// </summary>
    private async Task<object> ShowStoreAsync(JsonElement p, bool hasParams)
    {
        // Yield the WebView RPC turn so Settings can paint "Opening…" first.
        await Task.Yield();
        var store = (ReadString(p, hasParams, "store") ?? "steam").Trim().ToLowerInvariant();
        var result = await OpenStoreClientAsync(store).ConfigureAwait(true);
        return new { ok = result.Ok, message = result.Message };
    }

    /// <summary>Opens a verified installed store client and reveals its window.</summary>
    private async Task<StoreClientOpenResult> OpenStoreClientAsync(string store)
    {
        try
        {
            return store.Trim().ToLowerInvariant() switch
            {
                "steam" => await OpenVendorClient(
                    StoreKind.Steam,
                    StoreWindowHider.SteamMainProcessNames,
                    ExecutableCommand(SteamAdapter.TryResolveSteamExePublic()),
                    missing: "Steam is not installed."),
                "epic" => await OpenVendorClient(
                    StoreKind.Epic,
                    StoreWindowHider.EpicProcessNames,
                    ExecutableCommand(ResolveEpicLauncherExe()),
                    missing: "Epic Games Launcher is not installed."),
                "gog" => await OpenVendorClient(
                    StoreKind.Gog,
                    StoreWindowHider.GalaxyProcessNames,
                    ExecutableCommand(ResolveGalaxyExe()),
                    missing: "GOG Galaxy is not installed."),
                "riot" => await OpenVendorClient(
                    StoreKind.Riot,
                    StoreWindowHider.RiotUiProcessNames,
                    ExecutableCommand(ResolveRiotClientExe()),
                    missing: "Riot Client is not installed."),
                "xbox" => await OpenOfficialClient("xbox", StoreKind.Xbox, "Xbox app is not installed."),
                "ea" => await OpenOfficialClient("ea", StoreKind.Ea, "EA app is not installed."),
                "ubisoft" => await OpenOfficialClient("ubisoft", StoreKind.Ubisoft, "Ubisoft Connect is not installed."),
                "battlenet" => await OpenOfficialClient("battlenet", StoreKind.BattleNet, "Battle.net is not installed."),
                "amazon" => await OpenOfficialClient("amazon", StoreKind.Amazon, "Amazon Games is not installed."),
                "rockstar" => await OpenOfficialClient("rockstar", StoreKind.Rockstar, "Rockstar Games Launcher is not installed."),
                "itch" => await OpenOfficialClient("itch", StoreKind.Itch, "The itch app is not installed."),
                "minecraft" => await OpenOfficialClient("minecraft", StoreKind.Minecraft, "Minecraft Launcher is not installed."),
                "roblox" => await OpenOfficialClient("roblox", StoreKind.Roblox, "Roblox is not installed."),
                "paradox" => await OpenOfficialClient("paradox", StoreKind.Paradox, "Paradox Launcher is not installed."),
                "wargaming" => await OpenOfficialClient("wargaming", StoreKind.Wargaming, "Wargaming Game Center is not installed."),
                _ => new StoreClientOpenResult(false, "Unknown store."),
            };
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Open store {store}: {ex.GetType().Name}: {ex.Message}");
            return new StoreClientOpenResult(false, "Could not open the store app.");
        }
    }

    private static async Task<StoreClientOpenResult> OpenVendorClient(
        StoreKind kind,
        string[] processNames,
        StoreClientLaunchCommand? command,
        string missing)
    {
        if (command is null ||
            (!command.IsAppx && (string.IsNullOrWhiteSpace(command.FileName) || !File.Exists(command.FileName))))
            return new StoreClientOpenResult(false, missing);

        var label = kind switch
        {
            StoreKind.Epic => "Epic Games Launcher", StoreKind.Gog => "GOG Galaxy", StoreKind.Riot => "Riot Client",
            StoreKind.Ea => "EA app", StoreKind.Ubisoft => "Ubisoft Connect", StoreKind.BattleNet => "Battle.net",
            StoreKind.Amazon => "Amazon Games", StoreKind.Rockstar => "Rockstar Games Launcher",
            StoreKind.Wargaming => "Wargaming Game Center", StoreKind.Paradox => "Paradox Launcher",
            StoreKind.Minecraft => "Minecraft Launcher", StoreKind.Itch => "itch", _ => kind.ToString(),
        };
        return await Task.Run(() => StoreClientOpener.OpenAsync(kind, label, command, processNames)).ConfigureAwait(false);
    }

    private Task<StoreClientOpenResult> OpenOfficialClient(string adapterId, StoreKind kind, string missing)
    {
        var adapter = _services.FindAdapterById(adapterId) as IOfficialStoreClient;
        return adapter is null
            ? Task.FromResult(new StoreClientOpenResult(false, "Could not open the store app."))
            : OpenVendorClient(kind, OfficialClientUiProcessNames(kind), adapter.GetClientLaunchCommand(), missing);
    }

    /// <summary>
    /// Settings → Open is allowed to reveal only named launcher chrome. Keep
    /// this independent from an adapter's wider process observation list:
    /// helpers and services can be necessary for a game and must never be
    /// foregrounded merely because the user opened the launcher.
    /// </summary>
    private static string[] OfficialClientUiProcessNames(StoreKind kind) => kind switch
    {
        StoreKind.Xbox => StoreWindowHider.XboxClientProcessNames,
        StoreKind.Ea => StoreWindowHider.EaClientProcessNames,
        StoreKind.Ubisoft => StoreWindowHider.UbisoftClientProcessNames,
        StoreKind.BattleNet => StoreWindowHider.BattleNetClientProcessNames,
        StoreKind.Amazon => StoreWindowHider.AmazonClientProcessNames,
        StoreKind.Rockstar => StoreWindowHider.RockstarClientProcessNames,
        StoreKind.Itch => StoreWindowHider.ItchClientProcessNames,
        StoreKind.Minecraft => StoreWindowHider.MinecraftClientProcessNames,
        StoreKind.Roblox => StoreWindowHider.RobloxClientProcessNames,
        StoreKind.Paradox => StoreWindowHider.ParadoxClientProcessNames,
        StoreKind.Wargaming => StoreWindowHider.WargamingClientProcessNames,
        _ => [],
    };

    private static StoreClientLaunchCommand? ExecutableCommand(string? executable) =>
        string.IsNullOrWhiteSpace(executable) || !File.Exists(executable)
            ? null
            : new StoreClientLaunchCommand(executable);

    private static string? ResolveEpicLauncherExe() =>
        EpicAdapter.ResolveEpicLauncher();

    private static string? ResolveGalaxyExe() =>
        GogAdapter.ResolveGalaxy();

    private static string? ResolveRiotClientExe() =>
        RiotAdapter.TryResolveRiotClientServicesPublic();

    // FolderPicker has no title, so the UI's "title" param is not read.
    /// <summary>A game's program (.exe), for the shader add-on's Add a game.</summary>
    private async Task<object> PickProgramAsync()
    {
        var tcs = new TaskCompletionSource<object>();
        async Task Run()
        {
            try
            {
                if (App.MainAppWindow is not { } window) { tcs.TrySetResult(new { ok = false, cancelled = true, message = "The file picker could not open." }); return; }
                var picker = new Windows.Storage.Pickers.FileOpenPicker();
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
                picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder;
                picker.ViewMode = Windows.Storage.Pickers.PickerViewMode.List;
                picker.FileTypeFilter.Add(".exe");
                var file = await picker.PickSingleFileAsync();
                tcs.TrySetResult(file is null ? new { ok = false, cancelled = true, message = "Canceled." } : new { ok = true, cancelled = false, path = file.Path });
            }
            catch (Exception ex) { tcs.TrySetResult(new { ok = false, cancelled = false, message = ex.Message }); }
        }
        if (!_queue.HasThreadAccess) _queue.TryEnqueue(() => _ = Run());
        else _ = Run();
        return await tcs.Task.ConfigureAwait(true);
    }

    private async Task<object> PickFolderAsync()
    {
        var tcs = new TaskCompletionSource<object>();
        void Run() => _ = RunPickFolderAsync(tcs);

        if (!_queue.HasThreadAccess)
            _queue.TryEnqueue(Run);
        else
            Run();

        return await tcs.Task.ConfigureAwait(true);
    }

    private static async Task RunPickFolderAsync(TaskCompletionSource<object> tcs)
    {
        try
        {
            var window = App.MainAppWindow;
            if (window is null)
            {
                tcs.TrySetResult(new { ok = false, cancelled = true, message = "The folder picker could not open." });
                return;
            }

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            var picker = new Windows.Storage.Pickers.FolderPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder;
            picker.FileTypeFilter.Add("*");
            picker.ViewMode = Windows.Storage.Pickers.PickerViewMode.List;

            var folder = await picker.PickSingleFolderAsync();
            if (folder is null)
            {
                tcs.TrySetResult(new { ok = false, cancelled = true, message = "Canceled." });
                return;
            }

            tcs.TrySetResult(new { ok = true, cancelled = false, path = folder.Path });
        }
        catch (Exception ex)
        {
            tcs.TrySetResult(new { ok = false, cancelled = false, message = ex.Message });
        }
    }

    private async Task<GameEntry?> FindGameAsync(string? gameId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(gameId)) return null;
        var game = _services.Library.Find(gameId);
        if (game is not null) return game;
        await _services.Library.GetLibraryAsync().WaitAsync(ct).ConfigureAwait(false);
        return _services.Library.Find(gameId);
    }

    private async Task<object> DlssStatusAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        var gameId = ReadString(p, hasParams, "id");
        if (string.IsNullOrWhiteSpace(gameId))
            return new { ok = false, message = "Pick a game first.", items = Array.Empty<object>() };

        var game = _services.Library.Find(gameId!);
        if (game is null)
            return new { ok = false, message = "Game not found.", items = Array.Empty<object>() };

        var refresh = ReadBool(p, hasParams, "refresh") == true;
        var consented = _services.Settings.UpscalerConsent(game.Id);
        // The folder check too: a sleeping or network drive would hold the UI thread while it answers.
        return await Task.Run(() => DlssSwapService.ResolveInstallRoot(game.Path) is null
            ? new { ok = false, message = "The install folder is unavailable. Refresh the library or reconnect the drive.", items = Array.Empty<object>() }
            : MapDlssStatus(_dlss.GetStatus(game, refresh, consented, ct)), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The game sheet's confirmation. <c>allow</c> true with the
    /// <c>antiCheat</c> the page showed: saves the user's acceptance of the ban
    /// risk for this game, only if a fresh walk finds that same anti-cheat and
    /// it is not a Riot game or Riot Vanguard. <c>allow</c> false: withdraws it.
    /// </summary>
    private async Task<object> DlssConsentAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        var game = await FindGameAsync(ReadString(p, hasParams, "id"), ct).ConfigureAwait(false);
        if (game is null)
            return new { ok = false, message = "Game not found." };

        if (ReadBool(p, hasParams, "allow") != true)
        {
            _services.Settings.SetUpscalerConsent(game.Id, null);
            AppLog.Info($"Upscaler consent withdrawn: gameId={game.Id}.");
            return new { ok = true, message = "Exo will not change this game's files." };
        }

        var shown = ReadString(p, hasParams, "antiCheat");
        var antiCheat = await Task.Run(() => _dlss.AntiCheatNow(game, ct), ct).ConfigureAwait(false);
        if (antiCheat is null)
            return new { ok = false, message = "Exo found no anti-cheat in this game, so there is nothing to allow." };
        if (DlssSwapService.NeverAllowed(game, antiCheat))
        {
            AppLog.Info($"Upscaler consent refused: gameId={game.Id}; antiCheat={antiCheat}.");
            return new { ok = false, message = DlssSwapService.AntiCheatMessage(game, antiCheat, null) };
        }
        if (!string.Equals(shown, antiCheat, StringComparison.OrdinalIgnoreCase))
            return new { ok = false, message = $"This game now shows {antiCheat}. Check the warning again before allowing changes." };

        _services.Settings.SetUpscalerConsent(game.Id, antiCheat);
        AppLog.Info($"Upscaler consent: gameId={game.Id}; anti-cheat {antiCheat}; user accepted the risk.");
        return new { ok = true, message = "Allowed for this game. Any ban is your risk." };
    }

    /// <summary>
    /// No <c>file</c>: every file to the newest release. With <c>file</c> (the
    /// row key from dlss.status) and <c>release</c>: that file to exactly that
    /// release, or <c>latest</c>. Anti-cheat games are refused by the service
    /// unless this game's saved consent names the anti-cheat found under the
    /// write lock.
    /// </summary>
    private async Task<object> DlssUpdateAllAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        var game = await FindGameAsync(ReadString(p, hasParams, "id"), ct).ConfigureAwait(false);
        if (game is null)
            return new { ok = false, message = "Game not found." };
        // A store writing the game's files would leave a half-written original behind.
        if (_services.Launcher.IsGameBusy(game))
            return new { ok = false, message = "Wait for the game's update to finish." };

        try
        {
            var file = ReadString(p, hasParams, "file");
            var release = ReadString(p, hasParams, "release");
            var result = await Task.Run(() => _dlss.ApplyAsync(game, file, release, () => _services.Settings.UpscalerConsent(game.Id), ct), ct).ConfigureAwait(false);
            return MapDlssRun(result);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLog.Warn("dlss.updateAll failed: " + ex.Message);
            return new { ok = false, updated = 0, skipped = 0, failed = 0, message = "Could not update." };
        }
    }

    private async Task<object> DlssRestoreAsync(JsonElement p, bool hasParams)
    {
        var game = await FindGameAsync(ReadString(p, hasParams, "id"), _bridgeLifetime.Token).ConfigureAwait(true);
        if (game is null)
            return new { ok = false, message = "Game not found." };
        if (_services.Launcher.IsGameBusy(game))
            return new { ok = false, message = "Wait for the game's update to finish." };

        // Once a restore has started, finish its owned file transaction even if
        // the requesting page goes away. The next status read observes the result.
        var file = ReadString(p, hasParams, "file");
        var result = await Task.Run(() => _dlss.RestoreGame(game, file), _bridgeLifetime.Token).ConfigureAwait(false);
        return MapDlssRun(result);
    }

    /// <summary>NVIDIA's DLSS overlay: read without <c>on</c>; with it, Windows asks for administrator rights.</summary>
    private static async Task<object> DlssIndicatorAsync(JsonElement p, bool hasParams)
    {
        var result = hasParams && p.TryGetProperty("on", out var on) && on.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? await DlssSwapService.SetIndicatorAsync(on.GetBoolean()).ConfigureAwait(false)
            : await Task.Run(DlssSwapService.GetIndicator).ConfigureAwait(false);
        return new { ok = result.Ok, available = result.Available, on = result.On, message = result.Message };
    }

    /// <summary>One press, reported per file with its version before and after.</summary>
    private static object MapDlssRun(DlssSwapService.UpdateResult result) => new
    {
        ok = result.Ok,
        updated = result.Updated,
        skipped = result.Skipped,
        failed = result.Failed,
        message = result.Message,
        files = (result.Files ?? []).Select(file => new
        {
            fileName = file.FileName,
            folder = file.Folder,
            state = file.State,
            before = file.Before,
            after = file.After,
            message = file.Message,
        }).ToList(),
    };

    /// <summary>
    /// Bounded DTO: a file's place in the game is its path relative to the
    /// install folder, never an absolute path.
    /// </summary>
    private static object MapDlssStatus(DlssSwapService.StatusResult status) => new
    {
        ok = status.Ok,
        message = status.Message,
        antiCheat = status.AntiCheat,
        consent = status.Consent,
        items = status.Rows.Select(row => new
        {
            key = row.Dll.RelativePath,
            fileName = row.Dll.FileName,
            folder = DlssSwapService.FolderOf(row.Dll.RelativePath),
            displayName = DlssSwapService.DisplayName(row.Dll.FileName),
            api = DlssSwapService.ApiName(row.Dll.FileName),
            currentVersion = DlssSwapService.DisplayVersion(row.Dll),
            fileVersion = row.Dll.CurrentVersion,
            currentRelease = row.Dll.MatchedRelease,
            latestVersion = row.Latest?.Display,
            latestRelease = row.Latest?.Release,
            state = row.State,
            origin = row.Dll.Origin,
            originalVersion = DlssSwapService.TrimVersion(row.Dll.OriginalVersion),
            originalState = row.Dll.OriginalState,
            canUpdate = row.CanUpdate,
            canRestore = row.CanRestore,
            skipReason = row.Dll.SkipReason,
        }).ToList(),
    };

    private object MapGame(GameEntry g, bool discoverExternalRunningGame = false)
    {
        var runState = _services.Launcher.GetGameRunState(g, discoverExternalRunningGame);
        return new
        {
        id = g.Id,
        title = g.Title,
        store = g.Store.ToString().ToLowerInvariant(),
        // `store` remains the deterministic selected source. `stores` and
        // `variants` let a card show alternate active sources without exposing
        // account identity or replacing any exact action ids.
        stores = (g.Variants.Count == 0 ? new[] { g.Store } : g.Variants.Select(v => v.Store))
            .Distinct()
            .Select(store => store.ToString().ToLowerInvariant())
            .ToArray(),
        canonicalTitleKey = g.CanonicalTitleKey,
        selectedVariantId = g.SelectedVariantId ?? g.Id,
        variants = g.Variants.Select(variant =>
        {
            // game.get is the deliberate, selected-card reconciliation point.
            // Scan every exact source on that card there, otherwise a running
            // alternate-store copy stays invisible until the user happens to
            // switch sources and triggers another round trip. library.get keeps
            // this false so the grid never enumerates every installed process.
            var variantRunState = _services.Launcher.GetGameRunState(
                variant.ToGameEntry(g), discoverExternalRunningGame);
            return new
            {
                id = variant.Id,
                store = variant.Store.ToString().ToLowerInvariant(),
                installed = variant.Installed,
                owned = variant.Owned,
                entitlementState = variant.EntitlementState,
                updateAvailable = variant.UpdateAvailable,
                canInstall = variant.CanInstall,
                primaryAction = variant.PrimaryAction,
                path = variant.Path,
                launchTarget = variant.LaunchTarget,
                playtimeMinutes = variant.PlaytimeMinutes ?? 0,
                lastPlayedUtc = variant.LastPlayedUtc?.ToString("O"),
                status = variant.Status,
                isRunning = variantRunState.IsRunning,
                canStop = variantRunState.CanStop,
            };
        }).ToArray(),
        installed = g.Installed,
        owned = g.Owned,
        entitlementState = g.EntitlementState,
        updateAvailable = g.UpdateAvailable,
        canInstall = g.CanInstall,
        primaryAction = g.PrimaryAction,
        path = g.Path,
        coverUrl = g.CoverUrl,
        coverSource = g.CoverSource,
        artRevision = g.ArtRevision,
        playtimeMinutes = g.PlaytimeMinutes ?? 0,
        sizeBytes = g.SizeBytes ?? InstalledSizeCache.Peek(g.Path)?.Bytes,
        sizeApproximate = g.SizeBytes is null && InstalledSizeCache.Peek(g.Path) is { Complete: false, Bytes: not null },
        status = g.Status,
        deps = g.Deps,
        launchNote = g.LaunchNote,
        launchTarget = g.LaunchTarget,
        lastPlayedUtc = g.LastPlayedUtc?.ToString("O"),
        isAddPortable = string.Equals(g.Id, "local:add", StringComparison.OrdinalIgnoreCase),
        isRunning = runState.IsRunning,
        canStop = runState.CanStop,
        buyUrl = Storefront.BuyUrl(g),
        };
    }

    private static object MapProgress(InstallProgress p) => new
    {
        gameId = p.GameId,
        phase = p.Phase.ToString().ToLowerInvariant(),
        percent = p.Percent,
        bytesDownloaded = p.BytesDownloaded,
        bytesToDownload = p.BytesToDownload,
        bytesPerSecond = p.BytesPerSecond,
        status = p.Status,
        canCancel = p.CanCancel,
        isActive = p.IsActive,
    };

    private static string? ReadString(JsonElement p, bool hasParams, string name)
    {
        if (!hasParams || p.ValueKind != JsonValueKind.Object) return null;
        if (!p.TryGetProperty(name, out var el)) return null;
        return el.ValueKind == JsonValueKind.String ? el.GetString() : el.ToString();
    }

    private static bool? ReadBool(JsonElement p, bool hasParams, string name)
    {
        if (!hasParams || p.ValueKind != JsonValueKind.Object) return null;
        if (!p.TryGetProperty(name, out var el)) return null;
        if (el.ValueKind is JsonValueKind.True or JsonValueKind.False) return el.GetBoolean();
        return null;
    }

    private static double? ReadDouble(JsonElement p, bool hasParams, string name)
    {
        if (!hasParams || p.ValueKind != JsonValueKind.Object) return null;
        if (!p.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number)
            return null;
        return value.TryGetDouble(out var parsed) ? parsed : null;
    }

    private static object MapDepAwareResult(LaunchResult result) => new
    {
        ok = result.Ok,
        message = result.Message,
        processId = result.ProcessId,
        backendStarted = result.BackendStarted,
        handoffOnly = result.HandoffOnly,
        needsDependencies = result.NeedsDependencies,
        missingDependencies = MapMissingDeps(result.MissingDependencies),
    };

    private static object MapMissingDeps(IReadOnlyList<DependencyInfo> deps) =>
        deps.Select(d => new
        {
            id = d.Id,
            name = d.Name,
            status = d.Status,
            canOfferInstall = d.CanOfferInstall,
            officialUrl = d.OfficialUrl,
        }).ToArray();

    private void PostResponse(string id, bool ok, object? result = null, string? error = null)
        => PostSerialized(ExoBridgeProtocol.SerializeResponse(id, ok, result, error));

    /// <summary>An event from the shader add-on's engine, for its page.</summary>
    internal void PostShadersEvent(string name, System.Text.Json.Nodes.JsonNode? data) =>
        PostEvent("shaders.event", new { name, data });

    /// <summary>capture.state and capture.saved: from Exo Capture, through MainWindow.Capture.</summary>
    internal void PostCaptureEvent(string name, object? data) => PostEvent(name, data);

    private void PostEvent(string name, object? data)
        => PostSerialized(ExoBridgeProtocol.SerializeEvent(name, data));

    private void PostSerialized(string json)
    {
        try
        {
            var web = _web;
            if (web is null) return;

            void Send()
            {
                try { web.PostWebMessageAsJson(json); } catch { }
            }

            if (!_queue.HasThreadAccess)
                _queue.TryEnqueue(Send);
            else
                Send();
        }
        catch { }
    }

    /// <summary>A Windows notification from Exo's own UI, when it is in the background: a short title and line.</summary>
    private static object AppNotify(JsonElement p)
    {
        static string? Text(JsonElement p, string name, int max) =>
            p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? (s.Length > max ? s[..max] : s) : null;
        var title = Text(p, "title", 80);
        var body = Text(p, "body", 200);
        if (title is null || body is null) return new { ok = false };
        DiscordClient.DiscordNotifier.ShowApp(title, body);
        return new { ok = true };
    }
}
