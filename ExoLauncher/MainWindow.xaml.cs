using System.Diagnostics;
using System.Runtime.InteropServices;
using ExoLauncher.Helpers;
using ExoLauncher.Services;
using ExoLauncher.Ui;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics;
using WinRT.Interop;

namespace ExoLauncher;

/// <summary>
/// Thin native shell: default 1400×900 AMOLED window + WebView2 product UI.
/// The window is resizable and maximizable, with a 960×640 floor.
/// Launch/discovery stay in C# via <see cref="WebHostBridge"/>.
/// </summary>
public sealed partial class MainWindow : Window
{
    private Microsoft.UI.Windowing.AppWindowPresenter? _beforeFullScreen;
    private const int DefaultWindowWidth = 1400;
    private const int DefaultWindowHeight = 900;
    private const int MinWindowWidth = 960;
    private const int MinWindowHeight = 640;

    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly Stopwatch _startupStopwatch = Stopwatch.StartNew();
    private WebHostBridge? _bridge;
    private NotificationAreaIcon? _notificationAreaIcon;
    private bool _movingToNotificationArea;
    private bool _webReady;
    private bool _windowActive = true;
    private bool _closingWindow;
    private Task? _ensureWebTask;

    public MainWindow()
    {
        InitializeComponent();
        App.MainAppWindow = this;

        try
        {
            ApplyWindowChrome();
            ApplyInitialWindowBounds();
            RestoreWindowPlacement();
            TrySetWindowIcon();
        }
        catch { }

        ExtendsContentIntoTitleBar = true;
        try
        {
            if (AppWindow.Presenter is OverlappedPresenter op)
                op.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
        }
        catch { }

        try
        {
            var tb = AppWindow.TitleBar;
            tb.ExtendsContentIntoTitleBar = true;
            tb.IconShowOptions = IconShowOptions.HideIconAndSystemMenu;
            var transparent = Windows.UI.Color.FromArgb(0, 0, 0, 0);
            var black = Windows.UI.Color.FromArgb(255, 0, 0, 0);
            var ink = Windows.UI.Color.FromArgb(255, 233, 233, 236);
            var hover = Windows.UI.Color.FromArgb(255, 26, 26, 31);
            tb.BackgroundColor = black;
            tb.InactiveBackgroundColor = black;
            // Focus inside a hosted client deactivates Exo's own window while
            // the user is still in Exo; the caption must not dim for that.
            tb.ForegroundColor = ink;
            tb.InactiveForegroundColor = ink;
            tb.ButtonBackgroundColor = transparent;
            tb.ButtonInactiveBackgroundColor = transparent;
            tb.ButtonForegroundColor = ink;
            tb.ButtonInactiveForegroundColor = ink;
            tb.ButtonHoverBackgroundColor = hover;
            tb.ButtonHoverForegroundColor = ink;
            tb.ButtonPressedBackgroundColor = hover;
            tb.ButtonPressedForegroundColor = ink;
        }
        catch { }

        try { AttachCaptionInput(); } catch { }
        try { UpdateCaptionDragRegions(); } catch { }

        try { AppWindow.Changed += OnAppWindowChanged; } catch { }

        RootGrid.Loaded += async (_, _) =>
        {
            ApplyWindowChrome();
            StartHdrForGames();
            _ = Task.Run(() => NvidiaColor.ApplySaved(App.Services.Settings));
            _foregroundWatcher = ForegroundWatcher.Start(WindowNative.GetWindowHandle(this), inFront =>
            {
                if (_closingWindow) return;
                SetWindowActive(inFront);
                ApplyWebViewMemoryTarget(WindowIsHiddenOrMinimized());
                ApplyHostPriority();
            });
            _ = TrimLoopAsync();
            Services.DiscordClient.Voice.LiveAudio.Changed += OnLiveAudioChanged;
            await EnsureWebAsync();
        };
        LogStartupMilestone("window-constructed");
        WireDiscordClient();
        Activated += (_, e) =>
        {
            if (_closingWindow) return;
            // Activated flaps when focus moves into a WebView2 child; trust the
            // real foreground window instead. A hosted client is an owned
            // popup of this window, so typing in it counts as Exo.
            SetWindowActive(ForegroundWatcher.ExoInFront(WindowNative.GetWindowHandle(this)));
            if (_windowActive)
                _ = EnsureWebAsync();
            ApplyWebViewMemoryTarget(WindowIsHiddenOrMinimized());
            ApplyHostPriority();
        };
        AppWindow.Closing += (_, _) => PrepareWindowClose();
        Closed += (_, _) =>
        {
            PrepareWindowClose();
            App.MainAppWindow = null;
        };
    }

    internal void CloseWindow()
    {
        PrepareWindowClose();
        Close();
    }

    private int _runningGames;
    private ForegroundWatcher? _foregroundWatcher;
    // Fast while there is something to release (Exo backgrounded, a hidden
    // embedded renderer, a visibility change just now); slow otherwise.
    private static readonly TimeSpan TrimInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TrimIdleInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TrimSettleTime = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan TrimSummaryInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StoreRetrimInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan HostCloseGrace = TimeSpan.FromSeconds(3);
    private readonly SemaphoreSlim _trimWake = new(0, 1);
    private long _trimChangedAt;
    private bool _launcherEventsAttached;
    private volatile TrimSnapshot _trimSnapshot = new(InFront: true, HiddenOrMinimized: false, MusicWanted: false, MusicPopout: false, MusicPlaying: false);
    private volatile IReadOnlyList<EmbeddedRendererInfo> _embeddedRenderers = Array.Empty<EmbeddedRendererInfo>();
    private volatile bool _backgroundTreeLow;
    private int? _shellUiInactive;

    private sealed record TrimSnapshot(bool InFront, bool HiddenOrMinimized, bool MusicWanted, bool MusicPopout, bool MusicPlaying);
    private sealed record EmbeddedRendererInfo(int Pid, EmbeddedRendererKind Kind, bool Music);

    // Subscribing builds the launcher (every adapter, the library and the
    // orchestrator) on this thread, so it waits until the shell has painted.
    private void AttachLauncherEvents()
    {
        if (_launcherEventsAttached || _closingWindow) return;
        _launcherEventsAttached = true;
        App.Services.Launcher.GameLaunched += OnGameLaunched;
        App.Services.Launcher.GameSessionCompleted += OnGameSessionCompleted;
    }

    private void OnGameLaunched(Models.GameEntry game)
    {
        Interlocked.Increment(ref _runningGames);
        ApplyHostPriority();
        DispatcherQueue.TryEnqueue(() => { if (!_closingWindow) ApplyWebViewMemoryTarget(WindowIsHiddenOrMinimized()); });
    }

    /// <summary>
    /// The foreground hook can miss Exo coming to the front (started without
    /// foreground rights, as a WMI launch is, or Activated flapping after the
    /// hook already saw it), which left Exo throttled and its shell paused while
    /// in front. While Exo thinks it is behind, each trim tick asks Windows.
    /// </summary>
    private void ReconcileForeground()
    {
        if (_closingWindow) return;
        var inFront = ForegroundWatcher.ExoInFront(WindowNative.GetWindowHandle(this));
        if (inFront == _windowActive) return;
        SetWindowActive(inFront);
        ApplyWebViewMemoryTarget(WindowIsHiddenOrMinimized());
        ApplyHostPriority();
    }

    private void OnGameSessionCompleted(Models.GameEntry game)
    {
        if (Interlocked.Decrement(ref _runningGames) < 0)
            Interlocked.Exchange(ref _runningGames, 0);
        ApplyHostPriority();
        DispatcherQueue.TryEnqueue(() => { if (!_closingWindow) ApplyWebViewMemoryTarget(WindowIsHiddenOrMinimized()); });
    }

    // A call (or other live sound) started or stopped: the host's priority and the trim loop follow at once.
    private void OnLiveAudioChanged()
    {
        if (_closingWindow) return;
        ApplyHostPriority();
        if (_trimWake.CurrentCount == 0)
            try { _trimWake.Release(); } catch (SemaphoreFullException) { }
    }

    // Only this process and the hosted music tree; other processes are
    // never touched. Exo itself yields only while a launched game holds the
    // foreground. Hosted clients yield while Exo is behind something and
    // they carry no sound; a selected room holds Normal except while a game
    // runs. Songs always keep Normal (see ApplyForeground).
    private void ApplyHostPriority()
    {
        try
        {
            var gameRunning = Volatile.Read(ref _runningGames) > 0;
            var throttle = HostPriorityPolicy.Wanted(gameRunning, _windowActive, Services.DiscordClient.Voice.LiveAudio.Active);
            Process.GetCurrentProcess().PriorityClass = throttle
                ? ProcessPriorityClass.BelowNormal
                : ProcessPriorityClass.Normal;
            _musicApp?.ApplyForeground(_windowActive, !_musicKnownIdle, gameRunning);
        }
        catch { }
    }

    // One loop, two classes: while Exo is in front, hidden embedded renderers
    // are trimmed once when they become eligible; while it is not, the whole
    // process tree is. The timer continues on a thread-pool thread, so nothing
    // here may touch WebView2 or XAML — UI state arrives through the volatile
    // snapshot and the renderer map, both published from the UI thread.
    private async Task TrimLoopAsync()
    {
        var lastSummary = DateTime.MinValue;
        var lastRetrim = DateTime.MinValue;
        var lastStoreRetrim = DateTime.MinValue;
        var policy = new ManagedProcessPolicyState();
        // A process's role comes from its command line, which never changes.
        var roles = new Dictionary<ManagedProcessIdentity, HostedProcessKind>();
        var interval = TrimInterval;
        try
        {
            while (true)
            {
                // A visibility change wakes the loop early; the short settle
                // lets a burst of changes land as one tick.
                if (await _trimWake.WaitAsync(interval, _lifetimeCts.Token).ConfigureAwait(false))
                    await Task.Delay(500, _lifetimeCts.Token).ConfigureAwait(false);
                // One failed tick is logged and the loop goes on: trimming, priorities and the idle-music close
                // must not stop for the rest of the session.
                try
                {
                var state = _trimSnapshot;
                if (!state.InFront) DispatcherQueue.TryEnqueue(ReconcileForeground);
                var now = DateTime.UtcNow;
                if (state.InFront)
                {
                    if (_backgroundTreeLow)
                    {
                        _backgroundTreeLow = false;
                        BackgroundTrim.SetHostEfficiencyMode(false);
                        AppLog.Info("Trim: foreground mode (hidden Music renderers only).");
                    }
                }
                else if (!_backgroundTreeLow)
                {
                    _backgroundTreeLow = true;
                    BackgroundTrim.SetHostEfficiencyMode(true);
                    AppLog.Info("Trim: background mode (tree trimmed once per inactivity; memory priority low, host efficiency on).");
                }
                var musicVisible = (state.MusicWanted && !state.HiddenOrMinimized) || state.MusicPopout;
                if (_musicApp is { } musicHost) await musicHost.IdleTickAsync().ConfigureAwait(false);
                ReleaseIdleMusicApp();
                var groups = ManagedResourceGroups();
                var live = groups.SelectMany(group => group.Processes).ToHashSet();
                policy.Retain(live);
                foreach (var gone in roles.Keys.Where(identity => !live.Contains(identity)).ToArray()) roles.Remove(gone);
                var musicPlaying = state.MusicPlaying || _musicApp?.HasActiveAudio == true;
                var hiddenRendererPids = _embeddedRenderers
                    .Where(renderer => EmbeddedRendererClass.ShouldTrimInForeground(renderer.Kind, musicVisible, musicPlaying))
                    .Select(renderer => renderer.Pid).ToHashSet();
                var liveAudio = Services.DiscordClient.Voice.LiveAudio.Active;
                var hostedPolicy = new HostedTrimPolicy(
                    MusicRoomOpen: musicVisible,
                    MusicPlaying: musicPlaying,
                    ShellHidden: state.HiddenOrMinimized,
                    LiveAudio: liveAudio);
                HostedProcessKind RoleOf(ManagedProcessIdentity identity)
                {
                    if (!roles.TryGetValue(identity, out var kind))
                        roles[identity] = kind = HostedProcessClass.FromCommandLine(HostedProcessClass.QueryCommandLine(identity.Pid));
                    return kind;
                }
                var targets = ManagedProcessResources.TrimTargets(groups, hiddenRendererPids, hostedPolicy, RoleOf).ToHashSet();
                // Store clients' UI helpers while their window is out of sight.
                var quietStores = Adapters.StoreClientHelpers.QuietGroupKeys(groups);
                targets.UnionWith(groups.Where(group => quietStores.Contains(group.Key)).SelectMany(group => group.Processes));
                var throttled = ManagedProcessResources.ThrottleTargets(groups, hostedPolicy, RoleOf).ToHashSet();
                var retrim = now - lastRetrim >= TimeSpan.FromSeconds(30);
                if (retrim) lastRetrim = now;
                // A store helper is retrimmed rarely: the Steam overlay draws in
                // steamwebhelper, and refaulting it every 30s would stutter it.
                var storeRetrim = now - lastStoreRetrim >= StoreRetrimInterval;
                if (storeRetrim) lastStoreRetrim = now;
                foreach (var group in groups)
                foreach (var identity in group.Processes)
                {
                    var eligible = targets.Contains(identity);
                    if (Adapters.StoreClientHelpers.IsGroupKey(group.Key))
                    {
                        // Memory only: no EcoQoS or priority class, so a shown client and its overlay run at full speed.
                        var storeStep = policy.Next(identity, eligible, null, false, eligible,
                            storeRetrim && eligible && policy.GrewSinceTrim(identity));
                        if (storeStep.Apply && BackgroundTrim.ApplyManaged(identity, eligible, storeStep.Trim))
                            policy.Accept(identity, eligible, null, false, eligible, storeStep.Trim);
                        continue;
                    }
                    // Music playing keeps Spotify at normal memory priority; a call keeps the host there.
                    var keepHot = (group.Key is "spotify" && musicPlaying) || (group.Key is "exo" && liveAudio);
                    var low = !state.InFront && !keepHot;
                    ProcessPriorityClass? priority = group.Key switch
                    {
                        "spotify" => state.InFront || keepHot
                            ? ProcessPriorityClass.Normal : ProcessPriorityClass.BelowNormal,
                        _ => null,
                    };
                    // Exo's own processes: the host's efficiency mode is set above.
                    bool? efficient = group.Key is "exo" or "webview" ? null : throttled.Contains(identity);
                    var step = policy.Next(identity, low, priority, efficient == true, eligible,
                        retrim && eligible && policy.GrewSinceTrim(identity), refresh: retrim);
                    if (step.Apply && BackgroundTrim.ApplyManaged(identity, low, step.Trim, priority, efficient))
                        policy.Accept(identity, low, priority, efficient == true, eligible, step.Trim);
                }
                if (now - lastSummary >= TrimSummaryInterval)
                {
                    lastSummary = now;
                    var stores = groups.Where(group => Adapters.StoreClientHelpers.IsGroupKey(group.Key)).ToArray();
                    if (stores.Length > 0)
                    {
                        var storeSnapshot = ManagedProcessResources.Capture(stores);
                        AppLog.Info("Trim: store client helpers (working set/private MB): " + string.Join(", ",
                            storeSnapshot.Groups.Select(group =>
                                $"{group.Key} {group.WorkingSetBytes / 1048576d:N0}/{group.PrivateBytes / 1048576d:N0} across {group.ProcessCount}" +
                                (quietStores.Contains(group.Key) ? " (trimmed)" : " (shown)"))));
                    }
                    var snapshot = ManagedProcessResources.Capture(groups.Except(stores));
                    AppLog.Info($"Trim: managed total {snapshot.WorkingSetBytes / 1048576d:N0} MB working set, " +
                        $"{snapshot.PrivateBytes / 1048576d:N0} MB private across {snapshot.ProcessCount} processes" +
                        (snapshot.Complete ? "." : $" ({snapshot.ReadableCount}/{snapshot.ProcessCount} readable).") +
                        " By group (working set/private MB): " + string.Join(", ", snapshot.Groups.Select(group =>
                            $"{group.Key} {group.WorkingSetBytes / 1048576d:N0}/{group.PrivateBytes / 1048576d:N0} across {group.ProcessCount}")));
                }
                var settling = Environment.TickCount64 - Interlocked.Read(ref _trimChangedAt) < TrimSettleTime.TotalMilliseconds;
                interval = !state.InFront || hiddenRendererPids.Count > 0 || settling ? TrimInterval : TrimIdleInterval;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    AppLog.Info("Background trim tick failed: " + e.GetType().Name + ".");
                    interval = TrimInterval;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { AppLog.Info("Background trim failed: " + e.GetType().Name + "."); }
    }

    private bool _rendererRefreshBusy;
    private bool _rendererRefreshAgain;

    // UI thread: WebView2 environment APIs are dispatcher-bound. Runs when
    // the environment's processes change or the player's document does; the
    // renderer map only matters while the YouTube Music view exists.
    private async Task RefreshEmbeddedRendererMapAsync()
    {
        if (_rendererRefreshBusy) { _rendererRefreshAgain = true; return; }
        _rendererRefreshBusy = true;
        try
        {
            do
            {
                _rendererRefreshAgain = false;
                if (_closingWindow) return;
                RefreshRuntimeResourceIdentities();
                if (_musicWeb is null)
                {
                    _embeddedRenderers = Array.Empty<EmbeddedRendererInfo>();
                    continue;
                }
                var environment = await WebViewEnvironmentFactory.GetAsync();
                if (_closingWindow) return;
                var infos = await environment.GetProcessExtendedInfosAsync();
                var service = _musicWebService;
                var list = new List<EmbeddedRendererInfo>();
                foreach (var info in infos)
                {
                    if (info.ProcessInfo is not { Kind: CoreWebView2ProcessKind.Renderer } process) continue;
                    var sources = info.AssociatedFrameInfos.Select(frame => frame.Source).ToArray();
                    list.Add(new EmbeddedRendererInfo(
                        process.ProcessId,
                        EmbeddedRendererClass.From(sources),
                        EmbeddedRendererClass.HasMusicHost(sources, service)));
                }
                _embeddedRenderers = list;
            } while (_rendererRefreshAgain);
        }
        catch { }
        finally { _rendererRefreshBusy = false; }
    }

    private void PrepareWindowClose()
    {
        if (_closingWindow) return;
        SaveWindowPlacement();
        _closingWindow = true;
        _windowActive = false;
        AppLog.Info("Shutdown: window cleanup started.");
        _lifetimeCts.Cancel();
        try { _foregroundWatcher?.Dispose(); } catch { }
        StopHdrForGames();
        if (_launcherEventsAttached)
        {
            try { App.Services.Launcher.GameLaunched -= OnGameLaunched; } catch { }
            try { App.Services.Launcher.GameSessionCompleted -= OnGameSessionCompleted; } catch { }
        }
        try { AppWindow.Changed -= OnAppWindowChanged; } catch { }
        try { _notificationAreaIcon?.Dispose(); } catch { }
        try { _bridge?.Detach(); } catch { }
        AppLog.Info("Shutdown: bridge detached.");
        // The hosted sessions close side
        // by side under one bounded wait.
        var closing = CloseMusicWeb();
        // The shader engine stops its queue so the driver writes its cache, then exits.
        if (_shadersAddon is { } shadersAddon) closing.Add(Task.Run(shadersAddon.Dispose));
        // Exo Capture closes a recording in progress, then exits.
        if (_captureAddon is { } captureAddon) closing.Add(Task.Run(captureAddon.Dispose));
        try { Task.WaitAll(closing.ToArray(), HostCloseGrace + TimeSpan.FromSeconds(1)); } catch { }
        try { WebHost.Close(); } catch { }
        AppLog.Info("Shutdown: shell webview closed.");
        try { App.Services.Shutdown(); } catch { }
        App.Services.Settings.Flush();
        AppLog.Info("Shutdown: window cleanup completed.");
    }

    /// <summary>The title bar's minimize: an ordinary taskbar minimize. Hiding to the tray is for gameplay only.</summary>
    public void MinimizeWindow()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.Minimize();
    }

    public void HideForGameplay()
    {
        if (_movingToNotificationArea) return;
        _movingToNotificationArea = true;
        try
        {
            EnsureNotificationAreaIcon();
            if (!_notificationAreaIcon!.Show())
            {
                if (AppWindow.Presenter is OverlappedPresenter fallbackPresenter)
                    fallbackPresenter.Minimize();
                ApplyWebViewMemoryTarget(hiddenOrMinimized: true);
                return;
            }

            AppWindow.IsShownInSwitchers = false;
            AppWindow.Hide();
            ApplyWebViewMemoryTarget(hiddenOrMinimized: true);
        }
        catch
        {
            try
            {
                if (AppWindow.Presenter is OverlappedPresenter presenter)
                    presenter.Minimize();
            }
            catch { }
        }
        finally
        {
            _movingToNotificationArea = false;
        }
    }

    private void EnsureNotificationAreaIcon()
    {
        if (_notificationAreaIcon is not null) return;
        var hwnd = WindowNative.GetWindowHandle(this);
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "ExoLauncher.ico");
        _notificationAreaIcon = new NotificationAreaIcon(
            hwnd,
            iconPath,
            RestoreFromNotificationArea);
    }

    private void RestoreFromNotificationArea()
        => RestoreAndActivate();

    public void RestoreAndActivate()
    {
        try
        {
            _notificationAreaIcon?.Hide();
            AppWindow.IsShownInSwitchers = true;
            AppWindow.Show();
            if (AppWindow.Presenter is OverlappedPresenter presenter && presenter.State == OverlappedPresenterState.Minimized)
            {
                if (_normalPlacement?.Maximized == true) presenter.Maximize();
                else presenter.Restore();
            }
            Activate();
            ApplyWebViewMemoryTarget(hiddenOrMinimized: false);
        }
        catch { }
    }

    private Task EnsureWebAsync()
    {
        if (_closingWindow || _webReady) return Task.CompletedTask;
        return _ensureWebTask ??= EnsureWebCoreAsync();
    }

    private async Task EnsureWebCoreAsync()
    {
        if (_webReady) return;
        // Plain black until the page draws: its splash is the first thing on screen, no text before it.
        ShowBootPanel(null);
        LogStartupMilestone("webview-init-start");
        try
        {
            // 0x80070002 (file not found) is usually a runtime mid-update or a
            // stale evergreen pv: back off (0.5s, 2s, 5s) and, from the second
            // attempt on, name the installed runtime folder explicitly.
            for (var attempt = 0; ; attempt++)
            {
                CoreWebView2Environment? webViewEnvironment = null;
                try
                {
                    webViewEnvironment = await WebViewEnvironmentFactory.GetAsync(explicitInstalledRuntime: attempt > 0);
                    if (_closingWindow) return;
                    await WebHost.EnsureCoreWebView2Async(webViewEnvironment);
                    if (_closingWindow) return;
                    if (!_processWatchAttached)
                    {
                        _processWatchAttached = true;
                        webViewEnvironment.ProcessInfosChanged += (_, _) =>
                        {
                            if (!_closingWindow) _ = RefreshEmbeddedRendererMapAsync();
                        };
                    }
                    LogStartupMilestone("webview-core-ready");
                    break;
                }
                catch (Exception ex)
                {
                    if (_closingWindow) return;
                    var fileNotFound = WebViewRuntimeLocator.IsFileNotFound(ex);
                    AppLog.Warn($"webview-init-failed attempt={attempt + 1} hr=0x{ex.HResult:X8}: {ex.Message}");
                    if (fileNotFound && attempt == 0)
                        AppLog.Info("webview-runtime-candidates: " + WebViewRuntimeLocator.DescribeCandidates());
                    if (!fileNotFound || attempt >= WebViewRuntimeLocator.RetryDelays.Length)
                    {
                        ShowWebFallback();
                        _ensureWebTask = null;
                        return;
                    }
                    WebViewEnvironmentFactory.Reset(webViewEnvironment);
                    await Task.Delay(WebViewRuntimeLocator.RetryDelays[attempt]);
                    if (_closingWindow) return;
                }
            }

            var core = WebHost.CoreWebView2;
            if (core is not null) _ = WebExtensions.PruneAsync(core, "shell");
            if (core is null)
            {
                ShowWebFallback();
                _ensureWebTask = null;
                return;
            }

            core.NavigationCompleted += (sender, args) =>
            {
                if (_closingWindow) return;
                if (args.IsSuccess)
                {
                    ApplyPageScale(again: true);
                    // A shell reload can discard a WebView2 overlay without a
                    // dismiss; never leave the embedded player hidden underneath.
                    if (_musicWeb is { } musicSurface && _musicWebWanted && _musicWebReady && !_musicWebClosed)
                        EmbeddedClientFrame.SetInputVisible(musicSurface, true);
                    _shellUiInactive = null;
                    RefreshRuntimeResourceIdentities();
                    LogStartupMilestone("webview-navigation-complete");
                    // Shown once the page says its first frame is drawn (shell.painted), so the
                    // runtime's own surface never flashes; a page that never says so shows anyway.
                    _revealPending = true;
                    _ = Task.Delay(RevealFallback).ContinueWith(_ => DispatcherQueue.TryEnqueue(RevealPainted), TaskScheduler.Default);
                    if (UpdateStartupHealth.Requested) _ = VerifyUpdateStartupAsync(core);
                    if (PackagedSmoke.Enabled) _ = PackagedSmoke.RunAsync(core, this);
                }
                else ShowWebFallback();
            };

            var www = ResolveWwwRoot();
            if (www is null)
            {
                core.NavigateToString(
                    "<html><body style='background:#000;color:#fff;font-family:Segoe UI;padding:24px'>" +
                    "<h2>Exo Launcher UI not built</h2><p>Run: <code>cd ui &amp;&amp; npm ci &amp;&amp; npm run build</code></p></body></html>");
                _webReady = true;
                return;
            }

            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            WebHost.SizeChanged += (_, _) => ApplyPageScale();
            try { core.Settings.IsPasswordAutosaveEnabled = false; } catch { }
            try { core.Settings.IsGeneralAutofillEnabled = false; } catch { }
            try { core.Settings.IsWebMessageEnabled = true; } catch { }
            try { core.Settings.AreHostObjectsAllowed = false; } catch { }
#if !DEBUG
            try { core.Settings.AreBrowserAcceleratorKeysEnabled = true; } catch { }
#endif
            ApplyWebViewMemoryTarget(WindowIsHiddenOrMinimized());
#if DEBUG
            try { core.Settings.AreDevToolsEnabled = true; } catch { }
#else
            core.Settings.AreDevToolsEnabled = false;
#endif

            core.SetVirtualHostNameToFolderMapping(
                WebViewTrustPolicy.TrustedAppHost,
                www,
                CoreWebView2HostResourceAccessKind.DenyCors);

            core.NavigationStarting += OnWebNavigationStarting;
            core.NewWindowRequested += OnWebNewWindowRequested;
            core.ProcessFailed += OnWebProcessFailed;
            core.PermissionRequested += OnShellPermissionRequested;
            // A page element in fullscreen (a stream, a trailer) takes the whole screen, not only Exo's window; leaving it
            // puts the window back as it was (maximized or not).
            core.ContainsFullScreenElementChanged += (sender, _) =>
            {
                try
                {
                    if (sender.ContainsFullScreenElement) { _beforeFullScreen ??= AppWindow.Presenter; AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen); }
                    else if (_beforeFullScreen is { } before) { _beforeFullScreen = null; AppWindow.SetPresenter(before); }
                }
                catch (Exception ex) { AppLog.Warn("Fullscreen: " + ex.GetType().Name); }
            };

            // Virtual-folder mapping is the fast path. The explicit handler is
            // retained only as a startup fallback; intercepting every mapped
            // image would synchronously read cover bytes on the UI thread.
            var coverVirtualHostMapped = false;
            try
            {
                Directory.CreateDirectory(Services.CoverArtService.CacheRoot);
                // Allow, not DenyCors: Liquid Glass reads how bright the art under it is (ui/src/lib/glass.ts),
                // which needs CORS. The cache holds only public game art.
                core.SetVirtualHostNameToFolderMapping(
                    Services.CoverArtService.VirtualHost,
                    Services.CoverArtService.CacheRoot,
                    CoreWebView2HostResourceAccessKind.Allow);
                coverVirtualHostMapped = true;
            }
            catch { /* handler below still serves covers */ }

            // The shader add-on's page, on its own host so it runs as another origin in its frame.
            if (Services.ShadersAddon.Present)
            {
                try
                {
                    core.SetVirtualHostNameToFolderMapping(
                        Services.ShadersAddon.VirtualHost,
                        Services.ShadersAddon.PageFolder,
                        CoreWebView2HostResourceAccessKind.DenyCors);
                }
                catch (Exception ex) { AppLog.Warn("Shaders add-on page could not be mapped: " + ex.Message); }
            }
            // Exo Mouse likewise: another origin in its frame, so it reaches Exo only through mouseRelay.
            try
            {
                core.SetVirtualHostNameToFolderMapping(
                    WebViewTrustPolicy.MouseHost,
                    Path.Combine(www, "mouse"),
                    CoreWebView2HostResourceAccessKind.DenyCors);
            }
            catch (Exception ex) { AppLog.Warn("Exo Mouse page could not be mapped: " + ex.Message); }
            // The Discord Shop's card form: another origin, which alone may load Stripe's card fields.
            try
            {
                core.SetVirtualHostNameToFolderMapping(WebViewTrustPolicy.PayHost, Path.Combine(www, "pay"), CoreWebView2HostResourceAccessKind.DenyCors);
            }
            catch (Exception ex) { AppLog.Warn("Card form page could not be mapped: " + ex.Message); }
            MapCaptureHosts(core);

            if (!coverVirtualHostMapped)
            {
                try
                {
                    core.AddWebResourceRequestedFilter(
                        $"https://{Services.CoverArtService.VirtualHost}/*",
                        CoreWebView2WebResourceContext.All);
                    core.WebResourceRequested += CoverResourceRequested;
                }
                catch { /* virtual host alone may still work */ }
            }

            try
            {
                // Every frame: the shader add-on's page shows the same thumbnails.
                core.AddWebResourceRequestedFilter(
                    $"https://{Services.CoverArtService.ThumbnailHost}/*",
                    CoreWebView2WebResourceContext.Image,
                    CoreWebView2WebResourceRequestSourceKinds.All);
                core.WebResourceRequested += ThumbnailResourceRequested;
            }
            catch { /* cards fall back to their next candidate */ }

            try { _bridge?.Detach(); } catch { }
            _bridge = new WebHostBridge(App.Services, DispatcherQueue);
            _bridge.Attach(core);
            _bridge.NotifyWindowState(IsMaximized);
            LogStartupMilestone("bridge-attached");

            WebHost.Source = new Uri(WebViewTrustPolicy.StartUri);
            LogStartupMilestone("webview-navigation-start");
            _webReady = true;
        }
        catch
        {
            _ensureWebTask = null;
            ShowWebFallback();
        }
    }

    private static void OnWebNavigationStarting(
        CoreWebView2 sender,
        CoreWebView2NavigationStartingEventArgs e)
    {
        if (WebViewTrustPolicy.IsTrustedAppUri(e.Uri)) return;

        e.Cancel = true;
        AppLog.Warn("Blocked an untrusted main-frame WebView navigation.");
    }

    private bool _processWatchAttached;
    private int _webProcessRecoveries;
    private const int MaxWebProcessRecoveries = 2;

    /// <summary>
    /// The camera for Discord calls in Exo's own page: Exo's own origin may use it (Windows' camera privacy still
    /// decides, and its first use is the Turn On Camera click), every other frame (Exo Mouse, Shaders) may not.
    /// Other permissions keep WebView2's own handling.
    /// </summary>
    private static void OnShellPermissionRequested(CoreWebView2 sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        if (e.PermissionKind != CoreWebView2PermissionKind.Camera) return;
        e.SavesInProfile = false;
        e.State = WebViewTrustPolicy.IsTrustedAppUri(e.Uri) ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
        e.Handled = true;
    }

    /// <summary>
    /// A dead renderer must never leave a white window. Reload the cached UI a
    /// bounded number of times; after that, show the native fallback with Restart.
    /// </summary>
    private void OnWebProcessFailed(CoreWebView2 sender, CoreWebView2ProcessFailedEventArgs e)
    {
        var line = $"webview-process-failed kind={e.ProcessFailedKind} reason={e.Reason} exit={e.ExitCode} recoveries={_webProcessRecoveries}";
        // WebView2 restarts its GPU and utility processes by itself; only the page's own processes need Exo.
        if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.RenderProcessExited
            or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive
            or CoreWebView2ProcessFailedKind.BrowserProcessExited) AppLog.Error(line);
        else AppLog.Warn(line);
        // The browser process is gone with its CoreWebView2: a reload can't bring it back, a restart of Exo can.
        if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.BrowserProcessExited)
        {
            DispatcherQueue.TryEnqueue(() => ShowWebFallback(crashed: true));
            return;
        }
        if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.RenderProcessExited
            or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
        {
            if (Interlocked.Increment(ref _webProcessRecoveries) <= MaxWebProcessRecoveries)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        ShowBootPanel("Recovering the Exo window…");
                        sender.Reload();
                    }
                    catch (Exception ex)
                    {
                        AppLog.Error("webview-reload-failed", ex);
                        ShowWebFallback(crashed: true);
                    }
                });
                return;
            }
            DispatcherQueue.TryEnqueue(() => ShowWebFallback(crashed: true));
        }
    }

    private static void OnWebNewWindowRequested(
        CoreWebView2 sender,
        CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        AppLog.Warn("Blocked a WebView new-window request.");
    }

    /// Hides the page behind black, with a status line only when there is something to say (a recovery).
    private void ShowBootPanel(string? text)
    {
        _musicApp?.SetBounds(false, 0, 0, 0, 0);
        WebBootText.Text = text ?? "";
        WebBootProgressRing.IsActive = text is not null;
        WebBootPanel.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        WebViewFallback.Visibility = Visibility.Collapsed;
        // Startup keeps the page on screen: it is black until it draws, and showing a hidden
        // WebView2 later flashes one frame of its own surface.
        if (text is not null) WebHost.Visibility = Visibility.Collapsed;
    }

    private static readonly TimeSpan RevealFallback = TimeSpan.FromSeconds(3);
    private bool _revealPending;

    /// shell.painted, or the fallback: shows the page once per navigation.
    internal void RevealPainted()
    {
        if (!_revealPending || _closingWindow) return;
        _revealPending = false;
        RevealWeb();
    }

    private void RevealWeb()
    {
        WebBootProgressRing.IsActive = false;
        WebBootPanel.Visibility = Visibility.Collapsed;
        WebViewFallback.Visibility = Visibility.Collapsed;
        WebHost.Visibility = Visibility.Visible;
        // Transparent until now (XAML), never collapsed at startup: a collapsed WebView2 shown later,
        // or one shown before its first frame, flashes its own gray surface.
        WebHost.Opacity = 1;
        UpdateCaptionDragRegions();
        LogStartupMilestone("webview-visible");
        App.Services.StartDeferredServices();
        AttachLauncherEvents();
    }

    private void LogStartupMilestone(string milestone) =>
        AppLog.Info($"PERF startup milestone={milestone} elapsedMs={_startupStopwatch.ElapsedMilliseconds}");

    private void ShowWebFallback(bool crashed = false)
    {
        if (crashed)
        {
            // The runtime is there and ran; it stopped. Restart is the way back, not a WebView2 install.
            WebViewFallbackTitle.Text = "Exo Launcher's window stopped";
            WebViewFallbackBody.Text = "Restart Exo Launcher to bring it back.";
            WebViewFallbackButton.Visibility = Visibility.Collapsed;
        }
        WebBootProgressRing.IsActive = false;
        _musicApp?.SetBounds(false, 0, 0, 0, 0);
        WebBootPanel.Visibility = Visibility.Collapsed;
        WebHost.Visibility = Visibility.Collapsed;
        WebViewFallback.Visibility = Visibility.Visible;
        App.Services.StartDeferredServices();
        AttachLauncherEvents();
        WebViewRestartButton.Focus(FocusState.Programmatic);
    }

    private void WebViewRestartButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // The SDK releases the old single-instance process before starting
            // its replacement. On success this call does not return.
            var reason = Microsoft.Windows.AppLifecycle.AppInstance.Restart("");
            AppLog.Warn("App restart failed: " + reason);
            WebViewFallbackBody.Text = "Exo could not restart automatically. Close Exo and open it again.";
        }
        catch (Exception ex)
        {
            AppLog.Warn("App restart failed: " + ex.Message);
            WebViewFallbackBody.Text = "Exo could not restart automatically. Close Exo and open it again.";
        }
    }

    private void WebViewFallbackButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://go.microsoft.com/fwlink/p/?LinkId=2124703")
            {
                UseShellExecute = true,
            });
        }
        catch { }
    }

    private static string? ResolveWwwRoot()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "wwwroot"),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "wwwroot")),
        };
        foreach (var c in candidates)
        {
            if (Directory.Exists(c) && File.Exists(Path.Combine(c, "index.html")))
                return c;
        }
        return null;
    }

    /// <summary>
    /// Serve cover files from disk for https://covers.exo-launcher.local/*.
    /// Synchronous in-memory body so WebView2 always gets a complete response.
    /// </summary>
    private void CoverResourceRequested(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        try
        {
            var uri = e.Request.Uri;
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var u) ||
                !string.Equals(u.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(u.IdnHost, Services.CoverArtService.VirtualHost, StringComparison.OrdinalIgnoreCase) ||
                u.Port != 443 || !string.IsNullOrEmpty(u.UserInfo) || !string.IsNullOrEmpty(u.Fragment))
                return;
            var name = Uri.UnescapeDataString(u.AbsolutePath.TrimStart('/'));
            if (string.IsNullOrWhiteSpace(name) || name.Contains("..", StringComparison.Ordinal) ||
                name.Contains('/') || name.Contains('\\'))
                return;

            var path = Path.GetFullPath(Path.Combine(Services.CoverArtService.CacheRoot, name));
            var root = Path.GetFullPath(Services.CoverArtService.CacheRoot)
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                return;

            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < Services.CoverArtService.MinCoverBytes) return;

            // Write into a MemoryStream and expose as IRandomAccessStream via AsRandomAccessStream.
            var ms = new MemoryStream(bytes, writable: false);
            var contentType = Services.CoverArtService.ContentTypeFor(path);

            e.Response = sender.Environment.CreateWebResourceResponse(
                ms.AsRandomAccessStream(),
                200,
                "OK",
                $"Content-Type: {contentType}\r\nContent-Length: {bytes.Length}\r\nX-Content-Type-Options: nosniff\r\nCache-Control: public, max-age=86400\r\n");
        }
        catch
        {
            /* monogram fallback in UI */
        }
    }

    /// <summary>
    /// Grid thumbnails for https://thumbs.exo-launcher.local/&lt;cover&gt;?w=&lt;px&gt;.
    /// Lookup, scaling and file reads run on the thread pool; only the response
    /// object is created here, because the WebView2 environment is UI-thread bound.
    /// </summary>
    private void ThumbnailResourceRequested(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var u) ||
            !string.Equals(u.Scheme, Uri.UriSchemeHttps,StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(u.IdnHost, Services.CoverArtService.ThumbnailHost, StringComparison.OrdinalIgnoreCase))
            return;
        var environment = sender.Environment;
        var name = Uri.UnescapeDataString(u.AbsolutePath.TrimStart('/'));
        var width = 0;
        foreach (var pair in u.Query.TrimStart('?').Split('&'))
        {
            if (pair.StartsWith("w=", StringComparison.Ordinal) && int.TryParse(pair.AsSpan(2), out var w)) width = w;
        }
        if (u.Port != 443 || !string.IsNullOrEmpty(u.UserInfo) || name.Contains('/') || name.Contains('\\') ||
            !Services.CoverArtService.IsThumbnailWidth(width))
        {
            e.Response = environment.CreateWebResourceResponse(null, 404, "Not Found", "");
            return;
        }

        var deferral = e.GetDeferral();
        _ = Task.Run(() => Services.CoverArtService.ReadThumbnailAsync(name, width)).ContinueWith(task =>
        {
            var body = task.IsCompletedSuccessfully ? task.Result : null;
            if (DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.High, () =>
            {
                try
                {
                    e.Response = body is { } b
                        ? environment.CreateWebResourceResponse(
                            new MemoryStream(b.Bytes, writable: false).AsRandomAccessStream(), 200, "OK",
                            $"Content-Type: {b.ContentType}\r\nContent-Length: {b.Bytes.Length}\r\nX-Content-Type-Options: nosniff\r\nCache-Control: public, max-age=86400\r\n")
                        : environment.CreateWebResourceResponse(null, 404, "Not Found", "");
                }
                catch { /* the card moves to its next candidate */ }
                finally { deferral.Complete(); }
            })) return;
            deferral.Complete();
        }, TaskScheduler.Default);
    }

    public bool IsMaximized =>
        AppWindow.Presenter is OverlappedPresenter presenter &&
        presenter.State == OverlappedPresenterState.Maximized;

    public bool ToggleMaximize()
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter) return false;
        if (presenter.State == OverlappedPresenterState.Maximized)
        {
            presenter.Restore();
            return false;
        }
        presenter.Maximize();
        return true;
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (_closingWindow) return;
        if (!args.DidPresenterChange && !args.DidPositionChange && !args.DidSizeChange &&
            !args.DidVisibilityChange) return;

        // AppWindow minimums are physical pixels. Moving between monitors can
        // change DPI without changing the logical 960×640 product floor.
        ApplyWindowMinimumSize();
        // Another screen can call for another zoom; the same one returns at once.
        if (args.DidPositionChange) ApplyPageScale();
        if (args.DidPresenterChange || args.DidSizeChange)
        {
            _bridge?.NotifyWindowState(IsMaximized);
            // Regions are client-relative: a move alone never changes them,
            // and re-setting them on every step of a drag only adds work.
            UpdateCaptionDragRegions();
        }
        _musicApp?.FollowOwner();
        if (args.DidPresenterChange || args.DidPositionChange || args.DidSizeChange)
            RememberWindowPlacement();
        if (args.DidPresenterChange || args.DidVisibilityChange)
            ApplyWebViewMemoryTarget(WindowIsHiddenOrMinimized());
    }

    private bool WindowIsHiddenOrMinimized()
    {
        try
        {
            if (!AppWindow.IsVisible) return true;
            if (AppWindow.Presenter is OverlappedPresenter presenter &&
                presenter.State == OverlappedPresenterState.Minimized)
                return true;
        }
        catch { }
        return false;
    }

    /// Two flags for the shell: exoInactive (Exo is not the focused window, or
    /// hidden) pauses background work; exoHidden (hidden or minimized) is all
    /// that stops something the user can still see, like the mini player's clock.
    private async Task UpdateShellActivityAsync(bool inactive, bool hidden)
    {
        var core = WebHost.CoreWebView2;
        var state = (inactive ? 1 : 0) | (hidden ? 2 : 0);
        if (core is null || !WebViewTrustPolicy.IsTrustedAppUri(core.Source) || _shellUiInactive == state) return;
        _shellUiInactive = state;
        try
        {
            await core.ExecuteScriptAsync("document.documentElement.dataset.exoInactive='" + (inactive ? "true" : "false") +
                "';document.documentElement.dataset.exoHidden='" + (hidden ? "true" : "false") +
                "';document.dispatchEvent(new Event('exo:activity'));");
        }
        catch { _shellUiInactive = null; }
    }

    private void ApplyWebViewMemoryTarget(bool hiddenOrMinimized)
    {
        _ = UpdateShellActivityAsync(hiddenOrMinimized || !_windowActive, hiddenOrMinimized);
        var snapshot = new TrimSnapshot(_windowActive, hiddenOrMinimized,
            _musicWebWanted, _musicPopouts.Count > 0,
            IsDesktopMusic(_musicWebService) ? !_musicKnownIdle : Volatile.Read(ref _musicWebAudio));
        if (snapshot != _trimSnapshot)
        {
            _trimSnapshot = snapshot;
            Interlocked.Exchange(ref _trimChangedAt, Environment.TickCount64);
            if (_trimWake.CurrentCount == 0)
                try { _trimWake.Release(); } catch (SemaphoreFullException) { }
        }
        NotifyConnectedPermissionStateChanged();
        ApplyMusicMemoryTarget(hiddenOrMinimized);
        try
        {
            var core = WebHost.CoreWebView2;
            if (core is null) return;
            // The shell remains alive for navigation and events while an isolated
            // client covers its content. Loading/error UI stays at normal priority.
            var contentCovered =
                _musicWebWanted && !_musicWebClosed && _musicWeb?.Visibility == Visibility.Visible;
            SetWebViewMemoryTarget(core, WebViewMemoryPolicy.UseLowTarget(hiddenOrMinimized, contentCovered)
                ? CoreWebView2MemoryUsageTargetLevel.Low
                : CoreWebView2MemoryUsageTargetLevel.Normal);
        }
        catch { }
    }

    private void ApplyWindowChrome()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = true;
            presenter.IsResizable = true;
            presenter.IsMinimizable = true;
            ApplyWindowMinimumSize();
            try { presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false); }
            catch { }
            try
            {
                AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Collapsed;
            }
            catch { }
        }
        UpdateCaptionDragRegions();
    }

    private void ApplyWindowMinimumSize()
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter) return;
        var scale = GetWindowScale();
        var width = Math.Max(1, (int)Math.Round(MinWindowWidth * scale));
        var height = Math.Max(1, (int)Math.Round(MinWindowHeight * scale));
        try
        {
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
            if (area is not null) { width = Math.Min(width, area.WorkArea.Width); height = Math.Min(height, area.WorkArea.Height); }
        }
        catch { }
        presenter.PreferredMinimumWidth = width;
        presenter.PreferredMinimumHeight = height;
    }

    private void ApplyInitialWindowBounds()
    {
        try
        {
            var scale = 1.0;
            try
            {
                var hwnd = WindowNative.GetWindowHandle(this);
                var dpi = GetDpiForWindow(hwnd);
                if (dpi > 0) scale = dpi / 96.0;
            }
            catch { }

            var w = (int)Math.Round(DefaultWindowWidth * scale);
            var h = (int)Math.Round(DefaultWindowHeight * scale);

            try
            {
                var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
                if (area is not null)
                {
                    w = Math.Min(w, area.WorkArea.Width);
                    h = Math.Min(h, area.WorkArea.Height);
                }
            }
            catch { }

            AppWindow.Resize(new SizeInt32(w, h));
        }
        catch { }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private void TryCenterOnScreen()
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            var id = Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(id);
            var display = DisplayArea.GetFromWindowId(id, DisplayAreaFallback.Nearest);
            if (display is null) return;
            var work = display.WorkArea;
            var x = work.X + (work.Width - appWindow.Size.Width) / 2;
            var y = work.Y + (work.Height - appWindow.Size.Height) / 2;
            appWindow.Move(new PointInt32(x, y));
        }
        catch { }
    }

    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        try
        {
            ContentHost.ClearValue(FrameworkElement.WidthProperty);
            ContentHost.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
        catch { }
        ApplyWindowMinimumSize();
        UpdateCaptionDragRegions();
    }

    private double GetWindowScale()
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            var dpi = GetDpiForWindow(hwnd);
            if (dpi > 0) return dpi / 96.0;
        }
        catch { }
        return 1.0;
    }

    private void TrySetWindowIcon()
    {
        try
        {
            var ico = Path.Combine(AppContext.BaseDirectory, "Assets", "ExoLauncher.ico");
            if (File.Exists(ico))
                AppWindow.SetIcon(ico);
        }
        catch { }
    }
}
