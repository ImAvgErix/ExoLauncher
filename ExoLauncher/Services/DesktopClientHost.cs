using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ExoLauncher.Helpers;
using Microsoft.UI.Dispatching;
using Windows.Graphics;

namespace ExoLauncher.Services;

/// <summary>
/// Result of an install request. A null <see cref="Message"/> on failure
/// means the host was closing.
/// </summary>
internal sealed record DesktopInstallOutcome(bool Ok, string? Message)
{
    internal static DesktopInstallOutcome Installed(string? message) => new(true, message);
    internal static DesktopInstallOutcome Failed(string? message) => new(false, message);

    /// The wire shape shared by `desktopClients.install` and `music.install`.
    internal object ToWire(string title) => new
    {
        ok = Ok,
        message = Message ?? (Ok ? null : $"Could not install {title}. Try again."),
    };
}

/// <summary>
/// Hosts an official desktop client (Spotify)
/// inside an Exo room. Exo launches the client, finds its main window, strips
/// its chrome and makes it an owned popup of the Exo window — not a SetParent
/// child: a reparented Chromium window never receives activation, so the page
/// stays blurred and swallows all keyboard input while mouse still works.
/// Ownership gives the same behavior (floats above Exo, no taskbar or Alt+Tab
/// entry, hides with owner minimize, dies with owner close) while staying a
/// real top-level window with normal input. Every embedded session belongs to
/// Exo from that point on — a client the user had open is adopted, and all of
/// them close with Exo or when the provider changes. Nothing about the
/// client's UI is modified.
/// </summary>
internal sealed partial class DesktopClientHost : IDisposable
{
    internal enum ClientState { Loading, Installing, NotInstalled, Installed, Running, Closed, Error }

    internal event Action<ClientState, string?>? StatusChanged;

    // Also how long a prewarm's boot window can stay on screen before it is hidden.
    private static readonly TimeSpan WindowPoll = TimeSpan.FromMilliseconds(150);
    /// How long the host waits for the client window before reporting failure.
    private static readonly TimeSpan WindowTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan WatchInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(2);
    // A client restarting itself (an update) gets this long to bring a new
    // window before a windowless tree is closed; its windows are adopted for
    // up to a minute after the old one disappeared, and never after that.
    private static readonly TimeSpan RestartGrace = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AdoptWindow = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan GroupCacheLifetime = TimeSpan.FromSeconds(30);

    private readonly DesktopClientSpec _spec;
    private readonly nint _owner;
    private readonly DispatcherQueue _queue;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();

    private nint _client;
    private int _clientPid;
    private bool _borrowed;
    private bool _alreadyRunning;
    private bool _restartedStalled;
    private bool _adoptRestarted;
    private DesktopClientExitGate? _previousExit;
    private ClientState _state = ClientState.Loading;
    private RoomMargins _room;
    private bool _hasRoom;
    private bool _wanted;
    // A room drives the client without showing it (Exo's own Spotify room):
    // it stays awake like an open room but is never placed on screen.
    private bool _engineWanted;
    private bool _inFront = true;
    private bool _mediaPlaying;
    private long _windowLostAt;
    private bool _lostEmbedded;
    private nint _launchForeground;
    private bool _launchForegroundExo;
    private Task? _launchedWork;
    private bool _surfaceHeld;
    private bool _revealing;
    private int _revealVersion;
    private bool _regionEmpty;
    private bool _placementPending;
    private readonly Dictionary<(int Pid, long Started), bool> _matchCache = new();
    private DesktopClientInstall? _matchInstall;

    /// The room as distances from Exo's client edges, in DIPs: a resize,
    /// maximize or DPI change rebuilds the rect from the owner alone.
    private readonly record struct RoomMargins(double Left, double Top, double Right, double Bottom);

    /// <summary>An open Exo popover over the room, in DIPs from Exo's client origin.</summary>
    internal readonly record struct DipRect(double Left, double Top, double Width, double Height);

    /// Exo's open menus over the room: cut out of the client's visible region,
    /// so a menu drawn by Exo's page shows over the hosted window.
    private IReadOnlyList<DipRect> _cutouts = [];
    private IReadOnlyList<RectInt32> _appliedHoles = [];

    /// <c>cutouts</c> from a setBounds request: at most eight finite, non-empty rects.
    internal static IReadOnlyList<DipRect> ReadCutouts(System.Text.Json.JsonElement data)
    {
        if (data.ValueKind != System.Text.Json.JsonValueKind.Object ||
            !data.TryGetProperty("cutouts", out var list) || list.ValueKind != System.Text.Json.JsonValueKind.Array) return [];
        var rects = new List<DipRect>();
        foreach (var item in list.EnumerateArray().Take(8))
        {
            if (item.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
            double Read(string name) => item.TryGetProperty(name, out var value) && value.TryGetDouble(out var number) && double.IsFinite(number) ? number : 0;
            var rect = new DipRect(Read("left"), Read("top"), Read("width"), Read("height"));
            if (rect.Width >= 1 && rect.Height >= 1) rects.Add(rect);
        }
        return rects;
    }

    // Idle freeze: after the room has been closed and the client silent for
    // this long, the whole tree is suspended and its pages released. Mini
    // player skip/play and reopening the room thaw first, so 30s does not
    // make changing songs wait on a cold start.
    internal static readonly TimeSpan FreezeDelay = HostedProcessClass.IdleFreezeDelay;
    private readonly List<ManagedProcessIdentity> _frozen = [];
    // Windows the client showed on its own while booting and Exo hid on
    // sight; a freshly launched client is only embedded from these.
    private readonly HashSet<IntPtr> _quietHidden = [];

    /// A dialog is never hidden: the user has to be able to answer it.
    private static bool IsPrompt(IntPtr hwnd, string className) =>
        string.Equals(className, "#32770", StringComparison.Ordinal) ||
        (GetWindowLongPtr(hwnd, GwlExstyle).ToInt64() & WsExDlgModalFrame) != 0;

    /// A visible dialog of the client's own processes.
    private IntPtr FindPrompt(DesktopClientInstall install)
    {
        var pids = ClientWindowPids(install);
        if (pids.Count == 0) return IntPtr.Zero;
        var found = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (!pids.Contains((int)pid) || !IsPrompt(hwnd, ClassName(hwnd))) return true;
            found = hwnd;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    /// A dialog the client raised instead of its window is owned by Exo, so
    /// it cannot escape to another display, and centred on the room. False
    /// when Exo already owned it.
    private bool HostPrompt(IntPtr prompt)
    {
        if (GetWindowLongPtr(prompt, GwlpHwndParent) == _owner) return false;
        SetWindowLongPtr(prompt, GwlpHwndParent, _owner);
        if (!GetWindowRect(prompt, out var rect)) return true;
        var area = RoomOnScreen() ?? OwnerClientOnScreen();
        SetWindowPos(prompt, IntPtr.Zero,
            area.X + (area.Width - (rect.Right - rect.Left)) / 2,
            area.Y + (area.Height - (rect.Bottom - rect.Top)) / 2,
            0, 0, SwpNoSize | SwpNoZorder | SwpNoActivate | SwpAsyncWindowPos);
        return true;
    }

    private string WaitingOn(IntPtr prompt)
    {
        var title = WindowTitle(prompt).Trim();
        return _spec.Title + " is waiting on: " + (title.Length > 0 ? title : "a dialog") + ".";
    }

    private long _idleSinceTicks;
    private int _idleProbeBusy;
    private bool _audioActive;

    internal bool IsFrozen { get { lock (_gate) return _frozen.Count > 0; } }
    private long _frozenAt;

    /// Frozen while idle for longer than <paramref name="span"/>, and Exo launched it (a client the user had open is never closed for this).
    internal bool FrozenLongerThan(TimeSpan span) { lock (_gate) return _frozen.Count > 0 && !_borrowed && Environment.TickCount64 - _frozenAt > span.TotalMilliseconds; }
    internal bool HasActiveAudio { get { lock (_gate) return _audioActive; } }

    /// Called from the host's background loop. Freezes a client whose room
    /// is closed, that is not playing media, and that has had no active
    /// audio session (call, song, microphone) for <see cref="FreezeDelay"/>.
    internal async Task IdleTickAsync()
    {
        if (_disposed || Interlocked.Exchange(ref _idleProbeBusy, 1) != 0) return;
        try
        {
            bool wanted, playing, frozen, embedded; int clientPid;
            lock (_gate) { wanted = _wanted || _engineWanted; playing = _mediaPlaying; frozen = _frozen.Count > 0; clientPid = _clientPid; embedded = _client != IntPtr.Zero; }
            if (clientPid == 0 || !ProcessAlive(clientPid))
            {
                // The root died, but its helpers may still be suspended orphans.
                List<ManagedProcessIdentity> orphans;
                lock (_gate) { orphans = [.. _frozen]; _frozen.Clear(); _audioActive = false; }
                ProcessFreezer.Thaw(orphans);
                return;
            }
            // Still booting: the window has not been embedded. Suspending now
            // would stall the launch and time out the window wait.
            if (!embedded) return;
            if (wanted || playing)
            {
                lock (_gate) _audioActive = true;
                Wake("room or playback");
                return;
            }
            if (frozen) return;
            if (await AppVolume.ActiveAsync(ResourceGroup.Processes.Select(identity => identity.Pid).ToHashSet()) == true)
            {
                lock (_gate) _audioActive = true;
                Interlocked.Exchange(ref _idleSinceTicks, 0);
                return;
            }
            lock (_gate) _audioActive = false;
            var now = Environment.TickCount64;
            var since = Interlocked.CompareExchange(ref _idleSinceTicks, now, 0);
            if (since == 0 || now - since < FreezeDelay.TotalMilliseconds) return;
            // A fresh, start-time-checked group limited to the client's own
            // tree: never a browser the client opened, never a shared daemon.
            // The cached group can miss an audio process Chromium spawned
            // since (a ringtone, a track started with the room closed), so the
            // probe runs again on the fresh membership before anything freezes.
            lock (_gate) _groupCache = null;
            var group = ResourceGroup.Processes;
            if (await AppVolume.ActiveAsync(group.Select(identity => identity.Pid).ToHashSet()) == true)
            {
                lock (_gate) _audioActive = true;
                Interlocked.Exchange(ref _idleSinceTicks, 0);
                return;
            }
            var tree = BackgroundTrim.TreePids(clientPid).ToHashSet();
            var targets = group.Where(identity => tree.Contains(identity.Pid)).ToArray();
            // The freeze itself runs on the UI thread, where every window call
            // on the client is made: none of them can then race the suspend
            // and block forever on a thread frozen between check and call.
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_queue.TryEnqueue(() =>
            {
                try { FreezeIdle(targets); }
                finally { done.TrySetResult(); }
            })) return;
            await done.Task.ConfigureAwait(false);
        }
        catch (Exception error) { AppLog.Info(_spec.Title + " idle probe skipped: " + error.GetType().Name); }
        finally { Interlocked.Exchange(ref _idleProbeBusy, 0); }
    }

    private void FreezeIdle(IReadOnlyList<ManagedProcessIdentity> targets)
    {
        nint client;
        lock (_gate)
        {
            if (_disposed || _wanted || _engineWanted || _mediaPlaying || _frozen.Count > 0) return;
            client = _client;
        }
        // Ownership makes Exo wait on this window for every activation and
        // z-order change; a suspended window would hang the owner. Cut the
        // tie (it is already hidden) and restore it on wake.
        if (client != IntPtr.Zero && IsWindow(client) && IsWindowVisible(client)) ShowWindow(client, SwHide);
        DisownTreeWindows(targets.Select(identity => identity.Pid).ToHashSet());
        var done = ProcessFreezer.Freeze(targets);
        lock (_gate) { _frozen.AddRange(done); _frozenAt = Environment.TickCount64; }
        UpdateKeyHook();
        AppLog.Info(_spec.Title + " frozen while idle: " + done.Count + " process(es) suspended, pages released.");
    }

    /// Resumes a frozen tree. Cheap when nothing is frozen; every path that
    /// needs the client responsive calls it first.
    /// Every top-level window of the tree that Exo owns loses its owner before the suspend; ownership is what makes Exo
    /// wait on the window during activation and z-order changes.
    private void DisownTreeWindows(HashSet<int> pids)
    {
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pids.Contains((int)pid) && GetWindowLongPtr(hwnd, GwlpHwndParent) == _owner)
                SetWindowLongPtr(hwnd, GwlpHwndParent, IntPtr.Zero);
            return true;
        }, IntPtr.Zero);
    }

    internal void Wake(string reason) => Wake(reason, _queue.HasThreadAccess);

    /// The room uses the client headless; while on, it is never frozen.
    internal void SetEngineWanted(bool on)
    {
        lock (_gate) _engineWanted = on;
        if (on) Wake("room in use");
    }

    /// The thaw is immediate; restoring ownership is a window call on the
    /// client, so off the UI thread (or inside the keyboard hook) it is queued.
    private void Wake(string reason, bool reownNow)
    {
        List<ManagedProcessIdentity> resume;
        lock (_gate)
        {
            Interlocked.Exchange(ref _idleSinceTicks, 0);
            if (_frozen.Count == 0) return;
            resume = [.. _frozen];
            _frozen.Clear();
        }
        ProcessFreezer.Thaw(resume);
        if (reownNow) Reown(reason);
        else _queue.TryEnqueue(() => Reown(reason));
    }

    private void Reown(string reason)
    {
        nint client; lock (_gate) client = _client;
        if (client != IntPtr.Zero && IsWindow(client)) SetWindowLongPtr(client, GwlpHwndParent, _owner);
        UpdateKeyHook();
        AppLog.Info(_spec.Title + " resumed (" + reason + ").");
    }
    private Task? _start;
    private Task<DesktopInstallOutcome>? _install;
    private DispatcherQueueTimer? _watch;
    private bool _disposed;
    private bool _clampLogged;
    private bool _hasVisibleRegion;
    private bool _layoutApplied;
    private RectInt32 _appliedTarget;
    private RectInt32 _appliedWindow;
    private RectInt32 _appliedClip;

    private uint _appliedDpi;
    private ManagedProcessGroup? _groupCache;
    private long _groupCacheAt;
    private nint _hookMoveSize;
    private nint _hookShowHide;
    private nint _hookLocation;
    private WinEventProc? _winEventProc;
    private bool _enforceQueued;
    private nint _hookedClient;
    private ManagedProcessIdentity? _resourceRoot;
    private string? _resourceDirectory;
    private string? _resourceExecutable;
    private int _watchTicks;
    private bool _recovering;
    private bool _trayLogged;
    private bool _launched;
    private DesktopClientInstall? _activeInstall;
    private string? _prepareWarning;


    /// Created on the UI thread, which owns the shared foreground and keyboard hooks.
    internal DesktopClientHost(DesktopClientSpec spec, nint owner, DispatcherQueue queue)
    {
        _spec = spec;
        _owner = owner;
        _queue = queue;
        Register(this);
    }

    internal string Key => _spec.Key;
    internal ClientState State { get { lock (_gate) return _state; } }
    internal bool IsDisposed { get { lock (_gate) return _disposed; } }
    internal int ClientPid { get { lock (_gate) return _clientPid; } }
    internal bool Wanted { get { lock (_gate) return _wanted; } }

    /// The previous provider's tree must be gone before this one launches:
    /// single-instance clients only re-activate a dying process otherwise.
    internal void AwaitBefore(DesktopClientExitGate previousExit)
    {
        lock (_gate) _previousExit = previousExit;
    }

    /// Start the client now but keep it off screen until the room asks for
    /// it, so opening the room later is instant.
    internal Task PrewarmAsync() => EnsureRunningAsync();

    /// Building the group walks the process table and reads a module path
    /// per descendant; the trim loop, playback events and the audio probe
    /// ask for it often, so it is reused until a member exits or 30 s pass.
    internal ManagedProcessGroup ResourceGroup
    {
        get
        {
            ManagedProcessIdentity? root;
            string? directory;
            ManagedProcessGroup? cached;
            long cachedAt;
            lock (_gate)
            {
                cached = _groupCache; cachedAt = _groupCacheAt;
                root = _resourceRoot; directory = _resourceDirectory;
            }
            if (cached is not null && Environment.TickCount64 - cachedAt < GroupCacheLifetime.TotalMilliseconds &&
                ManagedProcessResources.AllAlive(cached)) return cached;
            var group = ManagedProcessResources.HostedGroup(Key, root, directory);
            lock (_gate) { _groupCache = group; _groupCacheAt = Environment.TickCount64; }
            return group;
        }
    }

    /// React reports the room rect in DIPs. It is kept as margins to Exo's
    /// client edges, converted at Exo's current DPI on every placement.
    internal void SetBounds(bool visible, double left, double top, double width, double height,
        IReadOnlyList<DipRect>? cutouts = null)
    {
        if (_disposed) return;
        lock (_gate) _cutouts = cutouts ?? [];
        if (!visible)
        {
            lock (_gate) _wanted = false;
            HideSurface();
            return;
        }
        Wake("room opened");
        var scale = OwnerScale();
        GetClientRect(_owner, out var owner);
        bool opening;
        lock (_gate)
        {
            opening = !_wanted;
            _wanted = true;
            _priming = false;
            _hasRoom = true;
            _room = new RoomMargins(left, top,
                owner.Right / scale - left - Math.Max(1, width), owner.Bottom / scale - top - Math.Max(1, height));
        }
        // A room that was closed had its pages trimmed or frozen: the surface
        // shows with nothing drawn until the client answers again. Its EcoQoS
        // goes now, off the UI thread, not on the trim loop's next tick.
        if (opening)
        {
            _ = Task.Run(() =>
            {
                try { foreach (var identity in ResourceGroup.Processes) BackgroundTrim.ClearEfficiency(identity); }
                catch { /* the trim loop clears it on its next tick */ }
            });
            BeginReveal();
        }
        MoveSurface();
        ApplySurfaceVisibility(activate: opening);
        _ = EnsureRunningAsync();
    }

    private double OwnerScale()
    {
        var dpi = GetDpiForWindow(_owner);
        return dpi == 0 ? 1.0 : dpi / 96.0;
    }

    /// The room in Exo client pixels.
    private RectInt32 RoomInClient(RoomMargins room)
    {
        var scale = OwnerScale();
        GetClientRect(_owner, out var owner);
        var x = (int)Math.Round(room.Left * scale);
        var y = (int)Math.Round(room.Top * scale);
        return new RectInt32(x, y,
            Math.Max(1, owner.Right - x - (int)Math.Round(room.Right * scale)),
            Math.Max(1, owner.Bottom - y - (int)Math.Round(room.Bottom * scale)));
    }

    private RectInt32? RoomOnScreen()
    {
        RoomMargins room;
        lock (_gate) { if (!_hasRoom) return null; room = _room; }
        var bounds = RoomInClient(room);
        var origin = new PointInt32 { X = bounds.X, Y = bounds.Y };
        ClientToScreen(_owner, ref origin);
        return new RectInt32(origin.X, origin.Y, bounds.Width, bounds.Height);
    }

    /// Show the surface with an empty region, wait off the UI thread until
    /// the client's thread answers (its pages are back) and DWM has composed
    /// a frame, then open the real region: no black frame while memory faults in.
    private void BeginReveal()
    {
        nint client;
        int version;
        lock (_gate)
        {
            client = _client;
            if (client == IntPtr.Zero) return;
            _revealing = true;
            version = ++_revealVersion;
        }
        _ = Task.Run(() =>
        {
            SendMessageTimeout(client, WmNull, IntPtr.Zero, IntPtr.Zero, SmtoAbortIfHung, 500, out _);
            DwmFlush();
            _queue.TryEnqueue(() =>
            {
                lock (_gate)
                {
                    if (_revealVersion != version) return;
                    _revealing = false;
                }
                MoveSurface();
                ApplySurfaceVisibility();
            });
        });
    }

    internal Task EnsureRunningAsync()
    {
        lock (_gate)
            return _start ??= StartAsync();
    }

    /// <param name="launch">
    /// When false, the installer is only required to leave the app on disk
    /// (onboarding installs it without hosting it).
    /// </param>
    internal Task<DesktopInstallOutcome> InstallAsync(bool launch = true)
    {
        lock (_gate)
            return _install ??= InstallCoreAsync(launch);
    }

    /// Closed or failed: run the normal start path again.
    internal Task ReopenAsync()
    {
        lock (_gate)
        {
            if (_state == ClientState.Running && _client != IntPtr.Zero && IsWindow(_client) &&
                _clientPid != 0 && ProcessAlive(_clientPid))
                return Task.CompletedTask;
            _start = null;
            _install = null;
            _adoptRestarted = false;
        }
        return EnsureRunningAsync();
    }

    // A client that was already running when Exo first launched (the user's,
    // or a crashed Exo's leftover) may have its window hidden; that one counts.
    private bool AlreadyRunning { get { lock (_gate) return _alreadyRunning; } }
    private DesktopClientInstall? ActiveInstall { get { lock (_gate) return _activeInstall; } }

    private DesktopClientInstall? Locate()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return _spec.Locate(local, roaming);
    }

    private async Task StartAsync()
    {
        if (_disposed) return;
        _prepareWarning = null;
        Publish(ClientState.Loading, null);
        DesktopClientExitGate? previousExit;
        lock (_gate) previousExit = _previousExit;
        if (previousExit is not null)
        {
            try { await previousExit.WaitAsync(_lifetime.Token); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                Publish(ClientState.Error, "The previous music app is still running. Close it, then choose Open again.");
                AppLog.Warn("Previous music session could not close: " + error.GetType().Name);
                return;
            }
            if (_disposed) return;
            lock (_gate) if (ReferenceEquals(_previousExit, previousExit)) _previousExit = null;
        }
        var install = Locate();
        if (install is null)
        {
            Publish(ClientState.NotInstalled, _spec.Title + " isn't installed.");
            return;
        }
        try { await Task.Run(() => _spec.PrepareSettings(install)); }
        catch (Exception error)
        {
            if (_spec.Key == "spotify")
                _prepareWarning = "Spotify's black background could not be applied.";
            AppLog.Warn(_spec.Title + " settings could not be prepared: " + error.Message);
        }
        try
        {
            _lifetime.Token.ThrowIfCancellationRequested();
            var (client, prompt) = await WaitForClientAsync(install, launch: true);
            if (_disposed)
            {
                if (client != IntPtr.Zero)
                    try { PostMessage(client, WmClose, IntPtr.Zero, IntPtr.Zero); } catch { }
                return;
            }
            if (client == IntPtr.Zero)
            {
                // The client never produced its window. A dialog it raised
                // instead (an update or runtime prompt) is the usual reason:
                // it is brought into the room and named so the user can answer
                // it, and the watch adopts the window once it arrives.
                if (prompt == IntPtr.Zero) prompt = FindPrompt(ActiveInstall ?? install);
                if (prompt != IntPtr.Zero) HostPrompt(prompt);
                Publish(ClientState.Error, prompt == IntPtr.Zero
                    ? _spec.Title + " did not open a window."
                    : WaitingOn(prompt));
                lock (_gate) { _windowLostAt = Environment.TickCount64; _lostEmbedded = false; }
                EnsureWatch();
                return;
            }
            Embed(client);
            RestoreQuietHidden(client);
            Publish(ClientState.Running, AppearanceWarning());
            AppLog.Info(_spec.Title + " desktop client embedded: " + Describe(client) +
                (Wanted ? "" : " (hidden until its room opens)"));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            Publish(ClientState.Error, _spec.Title + " could not start.");
            AppLog.Warn(_spec.Title + " desktop launch failed: " + error.Message);
        }
    }

    private async Task<DesktopInstallOutcome> InstallCoreAsync(bool launch)
    {
        if (_disposed) return DesktopInstallOutcome.Failed(null);
        if (Locate() is not null)
        {
            AppLog.Info(_spec.Title + " install: already installed.");
            return FinishInstall(launch);
        }
        try
        {
            Publish(ClientState.Installing, null);
            return await InstallFromDownloadAsync(_spec.InstallerUri, launch);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return DesktopInstallOutcome.Failed(null);
        }
        catch (Exception error)
        {
            Publish(ClientState.Error, "The install could not start. Try again.");
            AppLog.Warn(_spec.Title + " install could not start: " + error.GetType().Name);
            return DesktopInstallOutcome.Failed("The install could not start. Try again.");
        }
        finally
        {
            lock (_gate) _install = null;
        }
    }

    private DesktopInstallOutcome FinishInstall(bool launch)
    {
        if (launch)
        {
            Publish(ClientState.Loading, null);
            lock (_gate) _start = null;
            _ = EnsureRunningAsync();
            return DesktopInstallOutcome.Installed(null);
        }
        Publish(ClientState.Installed, _spec.Title + " is installed.");
        return DesktopInstallOutcome.Installed(_spec.Title + " is installed.");
    }

    private async Task<DesktopInstallOutcome> InstallFromDownloadAsync(string uri, bool launch)
    {
        var setup = Path.Combine(Path.GetTempPath(), _spec.Key + "-setup-" + Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            using (var http = ExoHttp.Create(InstallTimeout))
            {
                await using var input = await http.GetStreamAsync(uri, _lifetime.Token);
                await using var output = File.Create(setup);
                await input.CopyToAsync(output, _lifetime.Token);
            }
            if (!DesktopClients.IsSignedBy(setup, _spec.InstallerSigner))
            {
                var unsigned = "The " + _spec.Title + " installer is not signed by " + _spec.Title + ". It was deleted.";
                Publish(ClientState.Error, unsigned);
                AppLog.Warn(_spec.Title + " installer signature check failed; installer deleted.");
                return DesktopInstallOutcome.Failed(unsigned);
            }
            _lifetime.Token.ThrowIfCancellationRequested();
            if (await _spec.BeforeInstaller(_lifetime.Token) is { } refused)
            {
                Publish(ClientState.Error, refused);
                return DesktopInstallOutcome.Failed(refused);
            }
            using var installer = Process.Start(new ProcessStartInfo(setup, _spec.InstallerArguments) { UseShellExecute = true });
            return await WaitForInstallAsync(InstallTimeout,
                _spec.Title + " is not installed yet. Finish its installer, then try again.",
                launch);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return DesktopInstallOutcome.Failed(null);
        }
        catch (Exception error)
        {
            var failed = _spec.Title + " could not be downloaded. Check your connection.";
            Publish(ClientState.Error, failed);
            AppLog.Warn(_spec.Title + " install failed: " + error.Message);
            return DesktopInstallOutcome.Failed(failed);
        }
        finally
        {
            try { File.Delete(setup); } catch { }
        }
    }

    private static double Scale(double value, string unit) =>
        value * (unit == "GB" ? 1 << 30 : unit == "MB" ? 1 << 20 : 1 << 10);

    private async Task<DesktopInstallOutcome> WaitForInstallAsync(TimeSpan timeout, string timeoutMessage, bool launch)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            if (_disposed) return DesktopInstallOutcome.Failed(null);
            // Locating reads the disk: off the UI thread.
            if (await Task.Run(Locate) is not null)
                return FinishInstall(launch);
            await Task.Delay(1000, _lifetime.Token);
        }
        Publish(ClientState.Error, timeoutMessage);
        return DesktopInstallOutcome.Failed(timeoutMessage);
    }

    /// The client always launches the way it would from its own shortcut: a
    /// Chromium client started hidden or minimized never marks its content
    /// visible, and an externally shown window then paints black. The window
    /// is hidden again within one poll of appearing when the room is not open.
    private void Launch(DesktopClientInstall install)
    {
        bool launchedByExo;
        lock (_gate)
        {
            if (_disposed || (_client != IntPtr.Zero && IsWindow(_client) && IsClientAlive())) return;
            // A client the user already had open is adopted as-is; its
            // pre-launch preparation (Spotify's archive patch) waits for the
            // next start. One a crashed Exo left behind still carries what
            // Exo did to its window and is Exo's own. The stall nudge
            // relaunches the same install, so this is decided once — by the
            // first launch.
            if (!_launched)
            {
                _launched = true;
                _alreadyRunning = AnyClientRunning(install);
                _borrowed = _alreadyRunning && !LeftByExo(install);
            }
            _activeInstall = install;
            launchedByExo = !_borrowed;
        }
        StartProcess(install);
        AppLog.Info(_spec.Title + " desktop launched: " + install.Executable + " " + install.Arguments);
        // Runtime-only session work (Spotify's DevTools pipe attach) belongs to
        // the process Exo started; an adopted client keeps its own chrome.
        if (launchedByExo)
        {
            var work = Task.Run(() => _spec.LaunchedAsync(install, _lifetime.Token));
            lock (_gate) _launchedWork = work;
        }
    }

    /// A window an earlier Exo session hosted keeps what Exo did to it: a
    /// tool window with no caption and a clip region.
    private bool LeftByExo(DesktopClientInstall install)
    {
        var pids = ClientWindowPids(install);
        var found = false;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (!pids.Contains((int)pid)) return true;
            if ((GetWindowLongPtr(hwnd, GwlExstyle).ToInt64() & WsExToolWindow) == 0 ||
                (GetWindowLongPtr(hwnd, GwlStyle).ToInt64() & WsCaption) != 0) return true;
            var region = CreateRectRgn(0, 0, 0, 0);
            if (region == IntPtr.Zero) return true;
            try { found = GetWindowRgn(hwnd, region) != RgnError; }
            finally { DeleteObject(region); }
            return !found;
        }, IntPtr.Zero);
        return found;
    }

    /// What had the keyboard before the client starts. A Chromium client
    /// launched like its shortcut takes the foreground, and hiding its window
    /// does not give it back (see <see cref="ReturnForeground"/>).
    private void RememberForeground()
    {
        var foreground = GetForegroundWindow();
        // A hidden foreground is a client window Exo already hid (a stall
        // relaunch); the window remembered before it stands.
        if (foreground == IntPtr.Zero || !IsWindowVisible(foreground)) return;
        GetWindowThreadProcessId(foreground, out var pid);
        lock (_gate)
        {
            _launchForeground = foreground;
            _launchForegroundExo = pid == Environment.ProcessId;
        }
    }

    /// Exo just hid a client window that holds the foreground: the game or
    /// app the user was in (else Exo, when Exo was it) gets it back. Windows
    /// lets only a thread sharing the foreground input state move it. Never
    /// while the room is open — then the client is meant to have focus.
    private void ReturnForeground(IntPtr hidden)
    {
        nint previous;
        bool previousExo, wanted;
        lock (_gate) { previous = _launchForeground; previousExo = _launchForegroundExo; wanted = _wanted; }
        if (wanted || GetForegroundWindow() != hidden) return;
        var target = previous != IntPtr.Zero && previous != hidden && IsWindow(previous) && IsWindowVisible(previous)
            ? previous
            : previousExo && IsWindowVisible(_owner) ? _owner : IntPtr.Zero;
        if (target == IntPtr.Zero) return;
        var foregroundThread = GetWindowThreadProcessId(hidden, out _);
        var self = GetCurrentThreadId();
        var attached = foregroundThread != 0 && foregroundThread != self && AttachThreadInput(self, foregroundThread, true);
        try { SetForegroundWindow(target); }
        finally { if (attached) AttachThreadInput(self, foregroundThread, false); }
    }

    private void StartProcess(DesktopClientInstall install)
    {
        RememberForeground();
        // UseShellExecute: CreateProcess would inherit Exo's std handles, and a
        // packaged app has no console — a Chromium client's console.log hits a
        // broken stdout pipe and dies with an EPIPE dialog.
        // Only when Exo starts the client's own executable: a shell
        // activation (explorer.exe) or an updater is not the client's tree.
        var ownExecutable = _spec.ProcessNames.Contains(
            Path.GetFileNameWithoutExtension(install.Executable), StringComparer.OrdinalIgnoreCase);
        var arguments = install.Arguments;
        int? pid;
        if (ownExecutable && _spec.Start is { } start) pid = start(install, arguments);
        else
        {
            using var process = Process.Start(new ProcessStartInfo(install.Executable)
            {
                Arguments = arguments,
                WorkingDirectory = install.AppDirectory,
                UseShellExecute = true,
            });
            pid = process?.Id;
        }
        if (pid is { } started && ownExecutable)
        {
            _launchGuard?.Dispose();
            _launchGuard = new LaunchShowGuard(started, HideBootWindow, WindowTimeout + TimeSpan.FromSeconds(30));
        }
    }

    private LaunchShowGuard? _launchGuard;

    /// A window the starting client just showed, hidden before it reaches the
    /// screen for more than a frame: the same rule <see cref="FindClientWindow"/>
    /// applies when its poll comes round (the client window and any unowned
    /// window go; a dialog or an owned window stays for the user to answer).
    /// Runs on the guard's own thread; HideQuietly only posts.
    private void HideBootWindow(IntPtr hwnd)
    {
        if (_disposed || GetWindow(hwnd, GwOwner) != IntPtr.Zero || IsPrompt(hwnd, ClassName(hwnd))) return;
        lock (_gate) if (_hookedClient != IntPtr.Zero) return;
        HideQuietly(hwnd);
    }

    /// Measured at Exo's start: a Chromium client's splash and main window were each
    /// on screen for about 60 ms, and Spotify's window for most of a second
    /// (it ignores a minimized start), before the window poll hid them. This
    /// hooks EVENT_OBJECT_SHOW for the new process on a thread of its own,
    /// so a busy UI thread at startup cannot delay the hide, until the host
    /// embeds the client window or the boot runs out of time.
    private sealed class LaunchShowGuard : IDisposable
    {
        private readonly Thread _thread;
        private uint _threadId;
        private int _done;

        internal LaunchShowGuard(int pid, Action<IntPtr> hide, TimeSpan lifetime)
        {
            var started = new ManualResetEventSlim();
            _thread = new Thread(() =>
            {
                _threadId = GetCurrentThreadId();
                // Kept alive by the closure for the hook's lifetime.
                WinEventProc proc = (_, _, hwnd, idObject, idChild, _, _) =>
                {
                    if (idObject != ObjidWindow || idChild != 0 || GetAncestor(hwnd, GaRoot) != hwnd) return;
                    try { hide(hwnd); } catch { }
                };
                var hook = SetWinEventHook(EventObjectShow, EventObjectShow, IntPtr.Zero, proc, (uint)pid, 0, WineventOutOfContext);
                started.Set();
                using var timer = new Timer(_ => Dispose(), null, lifetime, Timeout.InfiniteTimeSpan);
                while (Volatile.Read(ref _done) == 0 && GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
                {
                    TranslateMessage(ref message);
                    DispatchMessage(ref message);
                }
                if (hook != IntPtr.Zero) UnhookWinEvent(hook);
                GC.KeepAlive(proc);
            }) { IsBackground = true, Name = "Exo launch guard" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            started.Wait(TimeSpan.FromSeconds(2));
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) != 0) return;
            if (_threadId != 0) PostThreadMessage(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        }
    }

    /// A client that is running but has produced no window of its own is
    /// wedged: a runtime replaced under it, a half-finished update, a splash
    /// that never resolved. Activating it again only pokes the same dead
    /// process, so its tree is closed once and the next attempt starts clean.
    /// Only a client Exo started is ever restarted — one the user already had
    /// open is theirs, and killing it would throw away their session — and
    /// never one showing a dialog: that is a question for the user, not a stall.
    private async Task<bool> RestartStalledClientAsync(DesktopClientInstall install)
    {
        if (FindPrompt(install) != IntPtr.Zero) return false;
        lock (_gate)
        {
            if (_disposed || _restartedStalled || _borrowed || !_launched) return false;
            _restartedStalled = true;
        }
        var targets = CloseTargetIdentities(true, install, null, null);
        if (targets.Count == 0) return false;
        // Asked first: a client that still pumps messages closes its own way.
        var pids = ClientWindowPids(install);
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pids.Contains((int)pid) && GetWindowTextLength(hwnd) > 0)
                PostMessage(hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);
            return true;
        }, IntPtr.Zero);
        var killed = await Task.Run(() => WaitThenKill(targets, TimeSpan.FromSeconds(3)));
        AppLog.Warn(_spec.Title + " produced no window of its own; its tree was closed so the next start is clean. " +
            (killed > 0 ? killed + " process(es) terminated." : "It closed on request."));
        return true;
    }

    /// The client window belongs to whichever process the launcher hands the
    /// session to; scan every process with the client's names under the
    /// install. A dialog raised instead ends the wait (Prompt): it needs the
    /// user, and waiting out the timeout would only restart the client under it.
    private async Task<(IntPtr Client, IntPtr Prompt)> WaitForClientAsync(DesktopClientInstall install, bool launch)
    {
        var timeout = WindowTimeout;
        var prompt = IntPtr.Zero;
        var relocatedAt = Environment.TickCount64;
        // Runs on the thread pool, one poll at a time.
        nint Find()
        {
            var scan = FindClientWindow(install, adopt: !launch || AlreadyRunning);
            if (scan.Prompt != IntPtr.Zero) return prompt = scan.Prompt;
            if (scan.Window != IntPtr.Zero || scan.Running || Environment.TickCount64 - relocatedAt < 2000) return scan.Window;
            // Nothing of the client runs under this install: a self-update
            // moved it to a new folder or package version mid-wait.
            relocatedAt = Environment.TickCount64;
            if (Locate() is { } fresh && fresh != install)
            {
                install = fresh;
                lock (_gate) if (_activeInstall is not null) _activeInstall = fresh;
                AppLog.Info(_spec.Title + " moved to " + fresh.AppDirectory + " while starting; following it.");
            }
            return IntPtr.Zero;
        }
        // Store activation occasionally stalls when a relaunch lands while
        // the previous instance is still tearing down. One extra shell
        // activation only raises the existing process; it cannot double it.
        var window = await DesktopClients.WaitForWindowAsync(
            Find,
            () => { if (launch) Launch(install); },
            () =>
            {
                if (!launch || !AnyClientRunning(install)) return;
                try
                {
                    StartProcess(install);
                    AppLog.Info(_spec.Title + " activation re-requested after a stalled window. " + WindowDiagnostics(install));
                }
                catch { }
            },
            timeout, WindowPoll, _lifetime.Token);
        if (window != IntPtr.Zero && window == prompt) return (IntPtr.Zero, prompt);
        if (window == IntPtr.Zero)
        {
            AppLog.Warn(_spec.Title + " opened no matching window within " +
                (int)timeout.TotalSeconds + "s. " + WindowDiagnostics(install));
            // A stalled client is given one clean start rather than being left
            // wedged for the rest of the session.
            var restarted = launch && await RestartStalledClientAsync(install);
            if (restarted)
                window = await DesktopClients.WaitForWindowAsync(
                    Find,
                    () => Launch(install),
                    () => { },
                    timeout, WindowPoll, _lifetime.Token);
            if (window != IntPtr.Zero && window == prompt) return (IntPtr.Zero, prompt);
            if (window == IntPtr.Zero && restarted)
                AppLog.Warn(_spec.Title + " still opened no window after a clean start. " + WindowDiagnostics(install));
        }
        return (window, IntPtr.Zero);
    }

    private bool AnyClientRunning(DesktopClientInstall install)
    {
        foreach (var name in _spec.ProcessNames)
        foreach (var process in Process.GetProcessesByName(name))
        {
            try { if (ProcessMatches(process, install)) return true; }
            catch { }
            finally { process.Dispose(); }
        }
        return false;
    }

    /// A process of this client's own install runs now (the user started it,
    /// or a crashed Exo left it). Reads module paths: off the UI thread.
    internal static bool IsRunning(DesktopClientSpec spec)
    {
        var install = spec.Locate(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        if (install is null) return false;
        foreach (var name in spec.ProcessNames)
        foreach (var process in Process.GetProcessesByName(name))
        {
            try
            {
                if (DesktopClients.MatchesClientProcess(spec, process.ProcessName, process.MainModule?.FileName, install)) return true;
            }
            catch { }
            finally { process.Dispose(); }
        }
        return false;
    }

    /// Reading a module path is the expensive part of a
    /// poll, so each process (pid plus start time) is judged once per install.
    private bool ProcessMatches(Process process, DesktopClientInstall install)
    {
        try
        {
            var key = (process.Id, process.StartTime.ToUniversalTime().Ticks);
            lock (_matchCache)
            {
                if (_matchInstall != install) { _matchCache.Clear(); _matchInstall = install; }
                if (_matchCache.TryGetValue(key, out var known)) return known;
            }
            string? executable = null;
            try { executable = process.MainModule?.FileName; }
            catch { }
            var match = DesktopClients.MatchesClientProcess(_spec, process.ProcessName, executable, install);
            lock (_matchCache)
            {
                if (_matchInstall == install)
                {
                    if (_matchCache.Count >= 256) _matchCache.Clear();
                    _matchCache[key] = match;
                }
            }
            return match;
        }
        catch { return false; }
    }

    /// Processes allowed to own the client window: only the client's own
    /// named processes rooted at the install. A hosted runtime (an
    /// msedgewebview2 helper) or a browser opened through a client link is a
    /// descendant of the tree but can never own the HWND — its name is
    /// foreign and its module lives outside the install root.
    private HashSet<int> ClientWindowPids(DesktopClientInstall install)
    {
        var pids = new HashSet<int>();
        foreach (var name in _spec.ProcessNames)
        foreach (var process in Process.GetProcessesByName(name))
        {
            try { if (ProcessMatches(process, install)) pids.Add(process.Id); }
            catch { }
            process.Dispose();
        }
        return pids;
    }

    /// What the window scan actually saw: every top-level window owned by the
    /// client's process tree, so a timeout says whether the app never created
    /// one or created one the matcher rejected.
    private string WindowDiagnostics(DesktopClientInstall install)
    {
        var pids = ClientWindowPids(install);
        if (pids.Count == 0) return "No " + _spec.Title + " process is running.";
        var lines = new List<string>();
        EnumWindows((hwnd, _) =>
        {
            if (lines.Count >= 12) return false;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (!pids.Contains((int)pid)) return true;
            lines.Add("pid " + pid + " " + Describe(hwnd));
            return true;
        }, IntPtr.Zero);
        return lines.Count == 0
            ? _spec.Title + " processes (" + string.Join(",", pids) + ") have no top-level windows."
            : string.Join(" | ", lines);
    }

    private static string Describe(IntPtr hwnd)
    {
        var title = WindowTitle(hwnd);
        if (title.Length > 79) title = title[..79];
        GetWindowRect(hwnd, out var rect);
        return $"hwnd 0x{hwnd:X} class \"{ClassName(hwnd)}\" title \"{title}\" {rect.Right - rect.Left}x{rect.Bottom - rect.Top} visible={IsWindowVisible(hwnd)}";
    }

    /// The window's size as the client means it: a client started minimized
    /// (Spotify's --minimized) reports a 160x28 caption at -32000 until it is
    /// restored, so its normal-position rect decides whether it is the client.
    private static Rect RestoredRect(IntPtr hwnd)
    {
        GetWindowRect(hwnd, out var rect);
        if (!IsIconic(hwnd)) return rect;
        var placement = new WindowPlacement { Length = (uint)Marshal.SizeOf<WindowPlacement>() };
        return GetWindowPlacement(hwnd, ref placement) ? placement.NormalPosition : rect;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacement { public uint Length, Flags, ShowCmd; public int MinX, MinY, MaxX, MaxY; public Rect NormalPosition; }

    [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr hwnd, ref WindowPlacement placement);

    private static string ClassName(IntPtr hwnd)
    {
        var name = new StringBuilder(64);
        GetClassName(hwnd, name, name.Capacity);
        return name.ToString();
    }

    private static string WindowTitle(IntPtr hwnd)
    {
        var title = new StringBuilder(GetWindowTextLength(hwnd) + 1);
        GetWindowText(hwnd, title, title.Capacity);
        return title.ToString();
    }

    private readonly record struct ClientScan(IntPtr Window, int Pid, IntPtr Prompt, bool Running);

    /// <param name="adopt">
    /// The client was not just started by Exo: a window left hidden by an
    /// earlier Exo session (or minimized to tray) still counts. A process Exo
    /// just launched is only taken once it has shown the window itself — one
    /// embedded before its first paint leaves the room black.
    /// </param>
    private ClientScan FindClientWindow(DesktopClientInstall install, bool adopt)
    {
        var pids = ClientWindowPids(install);
        if (pids.Count == 0) return default;
        CaptureClientRoot(pids, install.AppDirectory);
        var prompt = IntPtr.Zero;
        var found = Scan(visibleOnly: true);
        if (found.Window == IntPtr.Zero && prompt == IntPtr.Zero) found = Scan(visibleOnly: false);
        return new ClientScan(found.Window, found.Pid, found.Window == IntPtr.Zero ? prompt : IntPtr.Zero, true);

        // Every top-level window the client shows while it boots (splash,
        // main window, a WPF shell) is hidden the moment it appears, so a
        // prewarm never lands on screen. A dialog, or a window some other
        // window owns, is left alone: the user must still be able to answer it.
        (IntPtr Window, int Pid) Scan(bool visibleOnly)
        {
            (IntPtr Window, int Pid) hit = default;
            EnumWindows((hwnd, _) =>
            {
                var visible = IsWindowVisible(hwnd);
                if (visibleOnly && !visible) return true;
                GetWindowThreadProcessId(hwnd, out var pid);
                if (!pids.Contains((int)pid)) return true;
                var className = ClassName(hwnd);
                var rect = RestoredRect(hwnd);
                var isClient = _spec.IsClientWindow(className, WindowTitle(hwnd),
                    rect.Right - rect.Left, rect.Bottom - rect.Top);
                if (!visible)
                {
                    bool shownByClient;
                    lock (_gate) shownByClient = _quietHidden.Contains(hwnd);
                    if (!isClient || !(adopt || shownByClient)) return true;
                }
                else if (!isClient && IsPrompt(hwnd, className))
                {
                    if (prompt == IntPtr.Zero) prompt = hwnd;
                    return true;
                }
                else if (isClient || GetWindow(hwnd, GwOwner) == IntPtr.Zero) HideQuietly(hwnd);
                if (!isClient) return true;
                hit = (hwnd, (int)pid);
                return false;
            }, IntPtr.Zero);
            return hit;
        }
    }

    private void HideQuietly(IntPtr hwnd)
    {
        var foreground = GetForegroundWindow() == hwnd;
        if (!ShowWindowAsync(hwnd, SwHide)) return;
        lock (_gate) _quietHidden.Add(hwnd);
        if (foreground) _queue.TryEnqueue(() => ReturnForeground(hwnd));
    }

    /// Once an adopted client is embedded, the other windows hidden while Exo
    /// looked for it (a client the user already had open: its pop-out
    /// windows) are shown again if they still belong to its tree. A
    /// client Exo started only ever showed boot windows, which stay hidden.
    private void RestoreQuietHidden(IntPtr client)
    {
        IntPtr[] hidden;
        bool adopted;
        lock (_gate) { hidden = [.. _quietHidden]; _quietHidden.Clear(); adopted = _borrowed; }
        if (hidden.Length == 0 || !adopted) return;
        GetWindowThreadProcessId(client, out var rootPid);
        var tree = BackgroundTrim.TreePids((int)rootPid).ToHashSet();
        foreach (var hwnd in hidden)
        {
            if (hwnd == client || !IsWindow(hwnd)) continue;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (tree.Contains((int)pid)) ShowWindowAsync(hwnd, SwShowNoActivate);
        }
    }

    private void CaptureClientRoot(HashSet<int> pids, string root)
    {
        ManagedProcessIdentity? existing;
        lock (_gate) { if (_disposed) return; existing = _resourceRoot; }
        if (existing is { } known && ManagedProcessResources.Identify(known.Pid) == known) return;
        ManagedProcessIdentity? earliest = null;
        foreach (var candidate in pids)
        {
            var identity = ManagedProcessResources.Identify(candidate);
            if (identity is { } observed && observed.StartedUtcTicks != 0 &&
                (earliest is null || observed.StartedUtcTicks < earliest.Value.StartedUtcTicks))
                earliest = observed;
        }
        if (earliest is not { } captured) return;
        string? executable = null;
        try
        {
            using var process = Process.GetProcessById(captured.Pid);
            executable = process.MainModule?.FileName;
        }
        catch { }
        lock (_gate)
        {
            if (_disposed) return;
            if (_resourceRoot is { } current && ManagedProcessResources.Identify(current.Pid) == current) return;
            _clientPid = captured.Pid;
            _resourceRoot = captured;
            _resourceDirectory = Path.GetDirectoryName(executable) ?? root;
            _resourceExecutable = executable;
        }
    }

    private void Embed(IntPtr client)
    {
        if (_disposed) return;
        GetWindowThreadProcessId(client, out var pid);
        var rootPid = (int)pid;
        // A tree left suspended by an earlier Exo session must run before its window is touched.
        ProcessFreezer.Thaw(BackgroundTrim.TreePids(rootPid).Select(ManagedProcessResources.Identify)
            .OfType<ManagedProcessIdentity>());
        var hadForeground = GetForegroundWindow() == client;
        var identity = ManagedProcessResources.Identify(rootPid)
            ?? throw new InvalidOperationException(_spec.Title + " process identity could not be verified. Close the app and retry.");
        // Owned popup, not a child: strip the chrome, keep WS_POPUP, and set
        // GWLP_HWNDPARENT so the window is owned by Exo. Owned windows float
        // above their owner, hide when it minimizes, have no taskbar or
        // Alt+Tab entry (WS_EX_TOOLWINDOW), and still get normal activation
        // and keyboard input — everything SetParent broke. The window hides
        // first so a partially-owned surface can never flash on the taskbar.
        var originalOwner = GetWindowLongPtr(client, GwlpHwndParent);
        var originalStyle = GetWindowLongPtr(client, GwlStyle);
        var originalExtended = GetWindowLongPtr(client, GwlExstyle);
        ShowWindow(client, SwHide);
        var style = originalStyle.ToInt64();
        style &= ~(WsCaption | WsThickframe | WsBorder | WsDlgFrame | WsSysmenu | WsMinimizeBox | WsMaximizeBox | WsChild);
        style |= WsPopup | WsClipChildren | WsClipSiblings;
        SetWindowLongPtr(client, GwlStyle, new IntPtr(style));
        var extended = originalExtended.ToInt64();
        extended = (extended & ~WsExAppWindow) | WsExToolWindow;
        SetWindowLongPtr(client, GwlExstyle, new IntPtr(extended));
        SetWindowLongPtr(client, GwlpHwndParent, _owner);
        SetWindowPos(client, IntPtr.Zero, 0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoZorder | SwpNoActivate | SwpFrameChanged | SwpAsyncWindowPos);
        ApplyDwmHosted(client);
        var owner = GetWindowLongPtr(client, GwlpHwndParent);
        var appliedExtended = GetWindowLongPtr(client, GwlExstyle).ToInt64();
        if (owner != _owner || (appliedExtended & WsExAppWindow) != 0 || (appliedExtended & WsExToolWindow) == 0)
        {
            SetWindowLongPtr(client, GwlpHwndParent, originalOwner);
            SetWindowLongPtr(client, GwlStyle, originalStyle);
            SetWindowLongPtr(client, GwlExstyle, originalExtended);
            SetWindowPos(client, IntPtr.Zero, 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpNoZorder | SwpNoActivate | SwpFrameChanged | SwpAsyncWindowPos);
            ShowWindowAsync(client, SwShow);
            throw new InvalidOperationException(_spec.Title + " could not be hosted; window ownership or taskbar style did not apply.");
        }
        string? directory = null;
        string? executable = null;
        try
        {
            using var process = Process.GetProcessById(rootPid);
            executable = process.MainModule?.FileName;
            directory = Path.GetDirectoryName(executable);
        }
        catch { }
        directory ??= _activeInstall?.AppDirectory;
        bool launchedByExo, wanted;
        Task? launchedWork;
        lock (_gate)
        {
            _client = client;
            _clientPid = rootPid;
            _resourceRoot = identity;
            _resourceDirectory = directory;
            _resourceExecutable = executable;
            _layoutApplied = false;
            _hasVisibleRegion = false;
            _groupCache = null;
            launchedByExo = _launched && !_borrowed;
            wanted = _wanted;
            launchedWork = _launchedWork;
            // Spotify collapses its caption strip through the debug port a
            // moment after the window appears; the room shows the client once
            // that is done (or after a second), so the page never jumps.
            _surfaceHeld = launchedWork is { IsCompleted: false };
        }
        if (launchedByExo) JoinExitJob(rootPid);
        if (launchedWork is { IsCompleted: false } pending) _ = ReleaseSurfaceAsync(pending);
        HookClientEvents(client);
        MoveSurface();
        // The user already opened the room and is looking at Exo: the client
        // gets the keyboard, as it would have from a click.
        var foreground = GetForegroundWindow();
        ApplySurfaceVisibility(activate: wanted && (foreground == _owner || foreground == client));
        if (hadForeground) ReturnForeground(client);
        // A fresh window may arrive while Exo is already backgrounded — replay
        // the last foreground state so the tree gets its priority policy now.
        ApplyForeground(_inFront, _mediaPlaying);
        EnsureWatch();
        UpdateKeyHook();
        Publish(ClientState.Running, AppearanceWarning());
        RemoveTrayIcons();
    }

    private async Task ReleaseSurfaceAsync(Task work)
    {
        await Task.WhenAny(work, Task.Delay(1000)).ConfigureAwait(false);
        _queue.TryEnqueue(() =>
        {
            lock (_gate) _surfaceHeld = false;
            ApplySurfaceVisibility();
        });
    }

    // Clients Exo started die with Exo even when Exo itself dies: the root
    // joins a kill-on-close job whose only handle is Exo's, closed by Windows
    // when Exo's process ends. Silent breakaway keeps children out of it, so a
    // browser a client opens is never killed with Exo; Chromium helpers exit
    // with their root anyway, and the tree close covers the rest.
    private static readonly object JobGate = new();
    private static nint _exitJob;

    private void JoinExitJob(int pid)
    {
        lock (JobGate)
        {
            if (_exitJob == IntPtr.Zero)
            {
                var job = CreateJobObjectW(IntPtr.Zero, null);
                if (job == IntPtr.Zero) return;
                var limits = new JobObjectExtendedLimitInformation();
                limits.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose | JobObjectLimitSilentBreakawayOk;
                if (!SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, ref limits,
                        (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
                {
                    CloseHandle(job);
                    return;
                }
                _exitJob = job;
            }
        }
        var process = OpenProcess(ProcessSetQuota | ProcessTerminate, false, pid);
        var joined = process != IntPtr.Zero && AssignProcessToJobObject(_exitJob, process);
        var error = joined ? 0 : Marshal.GetLastWin32Error();
        if (process != IntPtr.Zero) CloseHandle(process);
        if (!joined) AppLog.Info(_spec.Title + " could not join Exo's exit job (" + error + "); it closes with Exo's shutdown only.");
    }

    private string? AppearanceWarning()
    {
        if (_prepareWarning is not null) return _prepareWarning;
        if (_spec.Key != "spotify") return null;
        bool borrowed;
        lock (_gate) borrowed = _borrowed;
        return borrowed
            ? "Spotify was already open; black backgrounds apply the next time Exo starts it."
            : null;
    }

    private void ApplyDwmHosted(IntPtr client)
    {
        var corner = DwmcwcpDonotround;
        DwmSetWindowAttribute(client, DwmwaWindowCornerPreference, ref corner, sizeof(int));
        var border = DwmColorNone;
        DwmSetWindowAttribute(client, DwmwaBorderColor, ref border, sizeof(int));
        var backdrop = DwmsbtNone;
        DwmSetWindowAttribute(client, DwmwaSystemBackdropType, ref backdrop, sizeof(int));
    }

    private void HookClientEvents(IntPtr client)
    {
        GetWindowThreadProcessId(client, out var pid);
        _winEventProc ??= OnClientWinEvent;
        _queue.TryEnqueue(() =>
        {
            if (_disposed) return;
            nint current;
            lock (_gate) current = _client;
            if (current != client || _hookedClient == client) return;
            _hookedClient = client;
            _launchGuard?.Dispose();
            _launchGuard = null;
            _hookMoveSize = SetWinEventHook(EventSystemMoveSizeStart, EventSystemMoveSizeEnd,
                IntPtr.Zero, _winEventProc, pid, 0, WineventOutOfContext);
            _hookShowHide = SetWinEventHook(EventObjectShow, EventObjectHide,
                IntPtr.Zero, _winEventProc, pid, 0, WineventOutOfContext);
            // The client moving its own window (Electron restoring bounds, a
            // display change) is corrected at once, not on the next watch
            // tick. The hook also reports every caret and cursor move in the
            // client; the callback drops anything but the client window itself.
            _hookLocation = SetWinEventHook(EventObjectLocationChange, EventObjectLocationChange,
                IntPtr.Zero, _winEventProc, pid, 0, WineventOutOfContext);
            if (_hookMoveSize == IntPtr.Zero || _hookShowHide == IntPtr.Zero)
                AppLog.Warn(_spec.Title + " window event hooks could not be installed; move/size containment is inactive.");
            EnforceSurface();
        });
    }

    private void UnhookClientEvents()
    {
        _hookedClient = IntPtr.Zero;
        if (_hookMoveSize != IntPtr.Zero) { UnhookWinEvent(_hookMoveSize); _hookMoveSize = IntPtr.Zero; }
        if (_hookShowHide != IntPtr.Zero) { UnhookWinEvent(_hookShowHide); _hookShowHide = IntPtr.Zero; }
        if (_hookLocation != IntPtr.Zero) { UnhookWinEvent(_hookLocation); _hookLocation = IntPtr.Zero; }
    }

    private void OnClientWinEvent(nint hook, uint evt, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (_disposed || idObject != ObjidWindow || idChild != 0) return;
        // Location changes arrive for every Chromium child window; only the
        // hooked client counts, checked before taking the lock.
        if (evt == EventObjectLocationChange && hwnd != _hookedClient) return;
        nint client;
        lock (_gate) client = _client;
        if (client == IntPtr.Zero || hwnd != client) return;
        if (evt == EventSystemMoveSizeStart)
            PostMessage(client, WmCancelMode, IntPtr.Zero, IntPtr.Zero);
        QueueEnforce();
    }

    private void QueueEnforce()
    {
        lock (_gate) { if (_enforceQueued) return; _enforceQueued = true; }
        if (!_queue.TryEnqueue(() =>
        {
            lock (_gate) _enforceQueued = false;
            EnforceSurface();
        }))
            lock (_gate) _enforceQueued = false;
    }

    // One low-level keyboard hook serves every host, and only while it has
    // work: a hosted window is in front (Alt+F4 there closes Exo, not the
    // client), or a music client is frozen (a media key must thaw it before
    // the key reaches it). A game in front with nothing frozen sends no
    // keystroke through Exo. Hooks and host list live on the UI thread.
    private static readonly List<DesktopClientHost> Hosts = [];
    private static WinEventProc? _foregroundProc;
    private static nint _foregroundHook;
    private static LowLevelKeyboardProc? _keyProc;
    private static nint _keyHook;
    private static bool _keyHookWarned;
    private static bool _closeKeyDown;

    private static void Register(DesktopClientHost host)
    {
        Hosts.Add(host);
        if (_foregroundHook != IntPtr.Zero) return;
        _foregroundProc ??= (_, _, _, _, _, _, _) => UpdateKeyHook();
        _foregroundHook = SetWinEventHook(EventSystemForeground, EventSystemForeground,
            IntPtr.Zero, _foregroundProc, 0, 0, WineventOutOfContext);
    }

    private void Unregister()
    {
        if (!_queue.HasThreadAccess) { _queue.TryEnqueue(Unregister); return; }
        if (!Hosts.Remove(this)) return;
        UpdateKeyHook();
        if (Hosts.Count > 0 || _foregroundHook == IntPtr.Zero) return;
        UnhookWinEvent(_foregroundHook);
        _foregroundHook = IntPtr.Zero;
    }

    private static void UpdateKeyHook()
    {
        var foreground = GetForegroundWindow();
        var wanted = false;
        foreach (var host in Hosts)
        {
            nint client;
            lock (host._gate) client = host._client;
            wanted |= (client != IntPtr.Zero && foreground == client) || host.IsFrozen;
        }
        if (wanted == (_keyHook != IntPtr.Zero)) return;
        if (!wanted)
        {
            UnhookWindowsHookEx(_keyHook);
            _keyHook = IntPtr.Zero;
            _closeKeyDown = false;
            return;
        }
        _keyProc ??= OnKey;
        _keyHook = SetWindowsHookExW(WhKeyboardLl, _keyProc, GetModuleHandleW(null), 0);
        if (_keyHook != IntPtr.Zero || _keyHookWarned) return;
        _keyHookWarned = true;
        AppLog.Warn("Hosted client keyboard hook could not be installed; Alt+F4 closes only the client and media keys wait for the room.");
    }

    // Trivial on purpose: Windows drops a hook that answers slowly. The thaw
    // is two syscalls per process; everything else is queued.
    private static nint OnKey(int code, nint message, nint data)
    {
        if (code < 0) return CallNextHookEx(_keyHook, code, message, data);
        var key = Marshal.PtrToStructure<KeyboardInput>(data);
        var up = (key.Flags & 0x80) != 0;
        // Next, previous, stop, play/pause: a suspended player cannot hear them.
        if (!up && key.VirtualKey is >= 0xB0 and <= 0xB3)
            foreach (var host in Hosts)
                if (host.IsFrozen) host.Wake("media key", reownNow: false);
        if (key.VirtualKey == 0x73)
        {
            if (up && _closeKeyDown) { _closeKeyDown = false; return 1; }
            if (!up)
                foreach (var host in Hosts)
                {
                    nint client;
                    lock (host._gate) client = host._client;
                    if (host._disposed || !DesktopClients.RoutesCloseToOwner(key.VirtualKey, key.Flags, GetForegroundWindow(), client)) continue;
                    if (!_closeKeyDown) { _closeKeyDown = true; PostMessage(host._owner, WmClose, 0, 0); }
                    return 1;
                }
        }
        return CallNextHookEx(_keyHook, code, message, data);
    }

    private void EnforceSurface()
    {
        if (_disposed || IsFrozen) return;
        nint client;
        bool wanted;
        lock (_gate) { client = _client; wanted = _wanted; }
        if (client == IntPtr.Zero || !IsWindow(client)) return;
        if (wanted && IsWindowVisible(_owner) && !IsIconic(_owner) && (IsZoomed(client) || IsIconic(client)))
            ShowWindowAsync(client, SwShowNoActivate);
        ReassertHosting(client);
        MoveSurface();
        ApplySurfaceVisibility();
    }

    private void ReassertHosting(IntPtr client)
    {
        var changed = false;
        var style = GetWindowLongPtr(client, GwlStyle).ToInt64();
        var targetStyle = (style &
            ~(WsCaption | WsThickframe | WsBorder | WsDlgFrame | WsSysmenu | WsMinimizeBox | WsMaximizeBox | WsChild)) |
            WsPopup | WsClipChildren | WsClipSiblings;
        if (targetStyle != style)
        {
            SetWindowLongPtr(client, GwlStyle, new IntPtr(targetStyle));
            changed = true;
        }
        var extended = GetWindowLongPtr(client, GwlExstyle).ToInt64();
        var targetExtended = (extended & ~WsExAppWindow) | WsExToolWindow;
        if (targetExtended != extended)
        {
            SetWindowLongPtr(client, GwlExstyle, new IntPtr(targetExtended));
            changed = true;
        }
        if (GetWindowLongPtr(client, GwlpHwndParent) != _owner)
        {
            SetWindowLongPtr(client, GwlpHwndParent, _owner);
            changed = true;
        }
        if (changed)
            SetWindowPos(client, IntPtr.Zero, 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpNoZorder | SwpNoActivate | SwpFrameChanged | SwpAsyncWindowPos);
    }

    /// Exo's window moved, resized, maximized or changed DPI: the room keeps
    /// its margins to Exo's client edges, so it is placed once, from the
    /// owner's rect, without waiting for React to resend it.
    internal void FollowOwner()
    {
        MoveSurface();
        ApplySurfaceVisibility();
    }

    /// Room bounds are Exo client-area pixels; the owned popup is top-level
    /// and wants screen coordinates. The room is capped at Exo's client right
    /// and bottom (a mid-transition React rect can report a stale oversized
    /// room), then the window is sized larger than the room by its real
    /// nonclient frame plus the spec's title bar so the visible region shows
    /// only content inside the room.
    private void MoveSurface()
    {
        // A prime owns the window's place and region until it ends.
        if (IsFrozen || _priming) return;
        nint client;
        RoomMargins margins;
        bool wanted, hasRoom, revealing;
        lock (_gate) { client = _client; margins = _room; hasRoom = _hasRoom; wanted = _wanted; revealing = _revealing; }
        if (client == IntPtr.Zero) return;
        // No room layout yet, or a minimized or hidden owner: nothing worth
        // mapping; the surface follows once there is.
        if (!hasRoom)
        {
            lock (_gate) { _hasVisibleRegion = false; _layoutApplied = false; }
            HideSurface();
            return;
        }
        if (IsIconic(_owner) || !IsWindowVisible(_owner)) { HideSurface(); return; }
        if (IsIconic(client))
        {
            if (!wanted) return;
            ShowWindowAsync(client, SwShowNoActivate);
        }
        var bounds = RoomInClient(margins);
        var origin = new PointInt32 { X = bounds.X, Y = bounds.Y };
        ClientToScreen(_owner, ref origin);
        var room = new RectInt32(origin.X, origin.Y, bounds.Width, bounds.Height);
        var ownerClient = OwnerClientOnScreen();
        var capped = new RectInt32(room.X, room.Y,
            Math.Min(room.X + room.Width, ownerClient.X + ownerClient.Width) - room.X,
            Math.Min(room.Y + room.Height, ownerClient.Y + ownerClient.Height) - room.Y);
        if (!capped.Equals(room) && !_clampLogged)
        {
            _clampLogged = true;
            GetWindowRect(_owner, out var window);
            AppLog.Info(_spec.Title + " room clamped to Exo client: window " +
                $"{window.Left},{window.Top}-{window.Right},{window.Bottom}" +
                $" client {ownerClient.X},{ownerClient.Y}-{ownerClient.X + ownerClient.Width},{ownerClient.Y + ownerClient.Height}" +
                $" room {room.X},{room.Y}-{room.X + room.Width},{room.Y + room.Height}");
        }
        var dpi = GetDpiForWindow(client);
        if (dpi == 0) dpi = 96;
        var frame = MeasureFrame(client);
        var layout = DesktopClients.Layout(capped, ownerClient, frame);
        var clipScreen = DesktopClients.ClampToOwner(capped, ownerClient);
        if (layout is not { } next || clipScreen is not { } clip)
        {
            lock (_gate) { _hasVisibleRegion = false; _layoutApplied = false; }
            HideSurface();
            return;
        }
        GetWindowRect(client, out var raw);
        var actual = new RectInt32(raw.Left, raw.Top, raw.Right - raw.Left, raw.Bottom - raw.Top);
        // The placement is posted, not sent: a busy client never blocks Exo's
        // UI thread. Until the client applies it the target stands in for the
        // real rect, and the rect it then settles on is accepted rather than
        // fought (a client may hold its own minimum size).
        var placed = DesktopClients.NeedsPlacement(_layoutApplied, next.Window, _appliedTarget, actual,
            _placementPending ? actual : _appliedWindow);
        if (placed)
        {
            if (_layoutApplied && !_placementPending && (actual.X != _appliedWindow.X || actual.Y != _appliedWindow.Y))
                AppLog.Info(_spec.Title + " correcting hosted window placement: actual " +
                    $"{actual.X},{actual.Y} {actual.Width}x{actual.Height}; target " +
                    $"{next.Window.X},{next.Window.Y} {next.Window.Width}x{next.Window.Height}");
            SetWindowPos(client, IntPtr.Zero, next.Window.X, next.Window.Y,
                next.Window.Width, next.Window.Height, SwpNoZorder | SwpNoActivate | SwpAsyncWindowPos);
            actual = next.Window;
        }
        var clipLocal = DesktopClients.ClampToOwner(
            new RectInt32(clip.X - actual.X, clip.Y - actual.Y, clip.Width, clip.Height),
            new RectInt32(0, 0, actual.Width, actual.Height));
        if (clipLocal is not { } region)
        {
            lock (_gate) { _hasVisibleRegion = false; _layoutApplied = false; }
            HideSurface();
            return;
        }
        var holes = HolesIn(actual);
        var unchanged = !revealing && !_regionEmpty && _layoutApplied && _appliedDpi == dpi &&
            next.Window.Equals(_appliedTarget) && actual.Equals(_appliedWindow) &&
            region.Equals(_appliedClip) && holes.SequenceEqual(_appliedHoles);
        if (revealing ? !_regionEmpty && !ApplyEmptyRegion(client) :
            !unchanged && !ApplyRegion(client, region, holes))
        {
            lock (_gate) { _layoutApplied = false; _hasVisibleRegion = false; }
            HideSurface();
            return;
        }
        _regionEmpty = revealing;
        lock (_gate)
        {
            _appliedTarget = next.Window;
            _appliedWindow = actual;
            _appliedClip = region;
            _appliedHoles = holes;
            _appliedDpi = dpi;
            _layoutApplied = true;
            _hasVisibleRegion = true;
            _placementPending = placed;
        }
    }

    private static bool ApplyEmptyRegion(IntPtr client)
    {
        var region = CreateRectRgn(0, 0, 0, 0);
        if (region == IntPtr.Zero) return false;
        if (SetWindowRgn(client, region, true) != 0) return true;
        DeleteObject(region);
        return false;
    }

    /// Exo's open popovers in the client window's own pixels (rounded outward).
    private IReadOnlyList<RectInt32> HolesIn(RectInt32 window)
    {
        IReadOnlyList<DipRect> cutouts;
        lock (_gate) cutouts = _cutouts;
        if (cutouts.Count == 0) return [];
        var scale = OwnerScale();
        var holes = new List<RectInt32>(cutouts.Count);
        foreach (var cut in cutouts)
        {
            var origin = new PointInt32 { X = (int)Math.Floor(cut.Left * scale), Y = (int)Math.Floor(cut.Top * scale) };
            ClientToScreen(_owner, ref origin);
            holes.Add(new RectInt32(origin.X - window.X, origin.Y - window.Y,
                (int)Math.Ceiling(cut.Width * scale) + 1, (int)Math.Ceiling(cut.Height * scale) + 1));
        }
        return holes;
    }

    private static bool ApplyRegion(IntPtr client, RectInt32 clip, IReadOnlyList<RectInt32> holes)
    {
        var region = CreateRectRgn(clip.X, clip.Y, clip.X + clip.Width, clip.Y + clip.Height);
        if (region == IntPtr.Zero) return false;
        foreach (var hole in holes)
        {
            var cut = CreateRectRgn(hole.X, hole.Y, hole.X + hole.Width, hole.Y + hole.Height);
            if (cut == IntPtr.Zero) continue;
            CombineRgn(region, region, cut, RgnDiff);
            DeleteObject(cut);
        }
        if (SetWindowRgn(client, region, true) == 0)
        {
            DeleteObject(region);
            return false;
        }
        return true;
    }

    private static DesktopFrameInsets MeasureFrame(IntPtr client)
    {
        GetWindowRect(client, out var window);
        GetClientRect(client, out var clientArea);
        var topLeft = new PointInt32 { X = clientArea.Left, Y = clientArea.Top };
        ClientToScreen(client, ref topLeft);
        return new DesktopFrameInsets(
            Math.Max(0, topLeft.X - window.Left),
            Math.Max(0, topLeft.Y - window.Top),
            Math.Max(0, window.Right - topLeft.X - (clientArea.Right - clientArea.Left)),
            Math.Max(0, window.Bottom - topLeft.Y - (clientArea.Bottom - clientArea.Top)));
    }

    private RectInt32 OwnerClientOnScreen()
    {
        GetClientRect(_owner, out var rect);
        var topLeft = new PointInt32 { X = rect.Left, Y = rect.Top };
        var bottomRight = new PointInt32 { X = rect.Right, Y = rect.Bottom };
        ClientToScreen(_owner, ref topLeft);
        ClientToScreen(_owner, ref bottomRight);
        return new RectInt32(topLeft.X, topLeft.Y, bottomRight.X - topLeft.X, bottomRight.Y - topLeft.Y);
    }

    /// Posted, never sent: a client that is busy (or being suspended) cannot
    /// hold Exo's UI thread. Routine re-shows never take focus; only the user
    /// opening the room hands the client the keyboard.
    private void ApplySurfaceVisibility(bool activate = false)
    {
        if (IsFrozen) return;
        nint client;
        bool show;
        lock (_gate)
        {
            client = _client;
            show = _wanted && !_disposed && _hasVisibleRegion && !_surfaceHeld && client != IntPtr.Zero;
        }
        if (client == IntPtr.Zero) return;
        // Always posted: a hide still queued would make a visibility check stale.
        show = show && IsWindowVisible(_owner) && !IsIconic(_owner);
        if (show) lock (_gate) _primedClient = client;
        else if (_priming) return;
        // A client started minimized (Spotify --minimized) comes back at its own size.
        ShowWindowAsync(client, show ? (IsIconic(client) ? SwShowNoActivate : SwShowNa) : SwHide);
        if (show && activate) SetForegroundWindow(client);
    }

    private void HideSurface()
    {
        if (IsFrozen || _priming) return;
        nint client;
        lock (_gate) client = _client;
        if (client != IntPtr.Zero) ShowWindowAsync(client, SwHide);
    }

    // A Chromium client whose window was hidden before it ever reached the
    // screen can go on drawing while hidden: its compositor never saw the
    // window become visible, so it never sees it go away. Measured on a
    // prewarmed Discord app while Spotify played: its GPU process woke about 45
    // times a second, and one real reveal stops that. An empty region or an
    // off-screen show does not count as visible. So once per window, while
    // the room is closed, the client is shown for a moment through a
    // one-pixel region over Exo's own black client area, then hidden again.
    private static readonly TimeSpan PrimeShow = TimeSpan.FromSeconds(1);
    private const int PrimeInsetPx = 16;
    private const int PrimeAfterTicks = 10;
    private nint _primedClient;
    private bool _priming;

    private void PrimeHidden(IntPtr client)
    {
        lock (_gate) { if (_priming || _primedClient == client || _wanted || _revealing) return; }
        if (IsWindowVisible(client) || !IsWindowVisible(_owner) || IsIconic(_owner) ||
            !GetWindowRect(client, out var rect)) return;
        var height = rect.Bottom - rect.Top;
        var owner = OwnerClientOnScreen();
        if (height < 1 || owner.Width <= 2 * PrimeInsetPx || owner.Height <= 2 * PrimeInsetPx) return;
        var region = CreateRectRgn(0, height - 1, 1, height);
        if (region == IntPtr.Zero) return;
        if (SetWindowRgn(client, region, false) == 0)
        {
            DeleteObject(region);
            return;
        }
        lock (_gate)
        {
            _primedClient = client;
            _priming = true;
            // The room's next reveal places the window and its region afresh.
            _layoutApplied = false;
            _hasVisibleRegion = false;
        }
        _regionEmpty = false;
        // The window's bottom-left pixel lands inside Exo's client area,
        // clear of its rounded corner.
        SetWindowPos(client, IntPtr.Zero, owner.X + PrimeInsetPx, owner.Y + owner.Height - PrimeInsetPx - height,
            0, 0, SwpNoSize | SwpNoZorder | SwpNoActivate | SwpAsyncWindowPos);
        ShowWindowAsync(client, SwShowNa);
        AppLog.Info(_spec.Title + " shown once through a one-pixel region so it stops drawing while hidden.");
        _ = Task.Delay(PrimeShow).ContinueWith(_ => _queue.TryEnqueue(() => EndPrime(client)), TaskScheduler.Default);
    }

    private void EndPrime(IntPtr client)
    {
        bool wanted;
        nint current;
        lock (_gate) { _priming = false; wanted = _wanted; current = _client; }
        if (!_disposed && !wanted && current == client && IsWindow(client) && !IsFrozen)
            ShowWindowAsync(client, SwHide);
    }

    private void EnsureWatch()
    {
        if (_watch is null)
        {
            _watch = _queue.CreateTimer();
            _watch.Interval = WatchInterval;
            _watch.Tick += (_, _) => WatchTick();
        }
        if (!_watch.IsRunning) _watch.Start();
    }

    private void StopWatch()
    {
        try { _watch?.Stop(); } catch { }
    }

    private void WatchTick()
    {
        if (_disposed) return;
        nint client;
        int pid;
        lock (_gate) { client = _client; pid = _clientPid; }
        // A frozen client cannot answer window messages; every call below would block the UI thread.
        if (IsFrozen) return;
        if (client != IntPtr.Zero && !IsWindow(client))
        {
            UnhookClientEvents();
            lock (_gate)
            {
                _client = IntPtr.Zero;
                _hasVisibleRegion = false;
                _layoutApplied = false;
                _watchTicks = 0;
                _trayLogged = false;
                _windowLostAt = Environment.TickCount64;
                _lostEmbedded = true;
            }
            client = IntPtr.Zero;
            UpdateKeyHook();
        }
        if (client == IntPtr.Zero)
        {
            if (!_recovering) _ = RecoverWindowAsync();
            return;
        }
        if (Wanted && IsWindowVisible(_owner) && !IsIconic(_owner)) EnforceSurface();
        // After the client has settled: a prime during its boot can land
        // before its first frame and change nothing.
        else if (_watchTicks >= PrimeAfterTicks) PrimeHidden(client);
        // A client update re-registers its tray icon outright; re-delete on a schedule.
        if (++_watchTicks % 15 == 0) RemoveTrayIcons();
    }

    /// The hosted window is gone, or never came. A client restarting itself
    /// (an update, possibly into a new app-* folder or package version) is
    /// adopted again when its window returns; a tree that stays windowless
    /// with no updater or dialog is closed; a clean exit is reported closed —
    /// or started again when it left a newer version installed. Once closed
    /// the watch stops: a client the user opens later is theirs until Open.
    private async Task RecoverWindowAsync()
    {
        _recovering = true;
        try
        {
            // Locating reads the install from disk, kept off the UI thread; the
            // rest continues back on it, where the window calls belong.
            var install = await Task.Run(Locate);
            nint client;
            int pid;
            lock (_gate) { client = _client; pid = _clientPid; }
            if (_disposed || IsFrozen || client != IntPtr.Zero || _watch is not { IsRunning: true }) return;
            RecoverWindow(pid, install);
        }
        catch (Exception error) { AppLog.Warn(_spec.Title + " window recovery failed: " + error.Message); }
        finally { _recovering = false; }
    }

    private void RecoverWindow(int pid, DesktopClientInstall? install)
    {
        long lostAt;
        bool embedded, launched;
        DesktopClientInstall? active;
        lock (_gate) { lostAt = _windowLostAt; embedded = _lostEmbedded; launched = _launched; active = _activeInstall; }
        var elapsed = Environment.TickCount64 - lostAt;
        var current = install ?? active;
        if (current is not null && FindPrompt(current) is var prompt && prompt != IntPtr.Zero)
        {
            // Waiting on the user: the window is adopted whenever they answer.
            if (HostPrompt(prompt)) Publish(ClientState.Error, WaitingOn(prompt));
            lock (_gate) _windowLostAt = Environment.TickCount64;
            return;
        }
        if (install is not null && elapsed < AdoptWindow.TotalMilliseconds)
        {
            var found = FindClientWindow(install, adopt: true);
            if (found.Window != IntPtr.Zero)
            {
                lock (_gate) _activeInstall = install;
                try
                {
                    Embed(found.Window);
                    RestoreQuietHidden(found.Window);
                    AppLog.Info(_spec.Title + " window adopted again: " + Describe(found.Window));
                    RestartAdoptedOnce(install, found.Pid);
                }
                catch (Exception error)
                {
                    AppLog.Warn(_spec.Title + " window could not be embedded: " + error.Message);
                    ClientState state;
                    lock (_gate) state = _state;
                    if (state != ClientState.Error)
                        Publish(ClientState.Error, _spec.Title + " could not be embedded. Try Open again.");
                }
                return;
            }
        }
        if (pid != 0 && ProcessAlive(pid))
        {
            // The window is gone but the process survives: an update
            // restarting it, or a client closed to its tray. After the grace,
            // and with no updater at work, the session is Exo's, so the
            // leftover tree is closed rather than left running invisibly.
            if (!embedded)
            {
                if (elapsed >= AdoptWindow.TotalMilliseconds) StopWatch();
                return;
            }
            if (elapsed < RestartGrace.TotalMilliseconds || UpdaterRunning(pid, current)) return;
            List<ManagedProcessIdentity> leftovers;
            lock (_gate)
                leftovers = CloseTargetIdentities(_launched, _activeInstall, _resourceRoot, _resourceDirectory);
            _ = Task.Run(() => WaitThenKill(leftovers, TimeSpan.FromSeconds(2)));
            return;
        }
        // The root is gone. A restart hands over to a new process or an
        // updater first; give it the grace before calling the session closed.
        if (current is not null && elapsed < RestartGrace.TotalMilliseconds &&
            (AnyClientRunning(current) || UpdaterRunning(0, current))) return;
        StopWatch();
        var closed = ResetSession();
        HideSurface();
        UpdateKeyHook();
        // A clean exit that left a newer version installed was an update
        // restart: the new version starts the normal way, hidden unless the
        // room is open. Otherwise closed stays closed until the user opens it
        // again — a layout resend must not relaunch a client the user quit.
        if (launched && closed is not null && install is not null &&
            install.Version.Length > 0 && install.Version != closed.Version)
        {
            AppLog.Info(_spec.Title + " updated to " + install.Version + "; starting it again.");
            lock (_gate) _start = null;
            _ = EnsureRunningAsync();
            return;
        }
        if (embedded) Publish(ClientState.Closed, _spec.Title + " closed.");
    }

    /// Squirrel's Update.exe, a setup or a migrator is replacing the client;
    /// its window comes back when that finishes, so nothing is closed meanwhile.
    private static bool UpdaterRunning(int rootPid, DesktopClientInstall? install)
    {
        if (rootPid != 0)
            foreach (var pid in BackgroundTrim.TreePids(rootPid))
                if (Adapters.ProcessHelper.TryReadProcessName(pid) is { } name && IsUpdater(name)) return true;
        if (install is null || string.IsNullOrEmpty(install.AppDirectory)) return false;
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(install.AppDirectory)) + Path.DirectorySeparatorChar;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (IsUpdater(process.ProcessName) && process.MainModule?.FileName is { } path &&
                        Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch { }
            }
        }
        return false;
    }

    private static bool IsUpdater(string name) =>
        name.Contains("Update", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Setup", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Migrator", StringComparison.OrdinalIgnoreCase);

    /// A client that restarted itself comes back without what Exo prepares at
    /// launch (see <see cref="DesktopClientSpec.RestartAdopted"/>). It is
    /// closed politely and started the normal way — once, and only while
    /// nobody is looking at it or listening to it.
    private void RestartAdoptedOnce(DesktopClientInstall install, int pid)
    {
        lock (_gate)
            if (_adoptRestarted || _borrowed || _wanted || _audioActive || _mediaPlaying) return;
        if (!_spec.RestartAdopted(install, pid)) return;
        RestartWithPreparation();
    }

    /// Exo's own room needs what Exo's launch prepares (Spotify's DevTools pipe)
    /// and the running client lacks it: it was open before Exo started, an
    /// earlier Exo started it (the pipe ended with that Exo), or it restarted
    /// itself. Opening the room asks for the client, so it is restarted through
    /// the normal start, even when borrowed, looked at or playing: the room picks
    /// the song up again where it was (SpotifyStartAsync). False when it was not.
    internal bool RestartForRoom()
    {
        DesktopClientInstall? install;
        int pid;
        lock (_gate)
        {
            // Not only once: a restart that landed on a client still closing (it handed itself to that one and lost
            // the pipe) is tried again, at most every 20 seconds, so the room heals by itself instead of asking.
            if (_activeInstall is null) return false;
            if (Environment.TickCount64 - _roomRestartAt < 20_000) return true;
            _roomRestartAt = Environment.TickCount64;
            install = _activeInstall;
            pid = _clientPid;
        }
        if (!_spec.RestartAdopted(install, pid)) return false;
        RestartWithPreparation();
        return true;
    }

    private long _roomRestartAt = -20_000;

    private void RestartWithPreparation()
    {
        List<ManagedProcessIdentity> targets;
        nint client;
        lock (_gate)
        {
            _adoptRestarted = true;
            targets = CloseTargetIdentities(_launched, _activeInstall, _resourceRoot, _resourceDirectory);
            client = _client;
        }
        AppLog.Info(_spec.Title + " came back without Exo's launch preparation; restarting it once.");
        StopWatch();
        UnhookClientEvents();
        ResetSession();
        UpdateKeyHook();
        if (client != IntPtr.Zero) PostMessage(client, WmClose, IntPtr.Zero, IntPtr.Zero);
        Publish(ClientState.Loading, null);
        _ = Task.Run(() =>
        {
            WaitThenKill(targets, TimeSpan.FromSeconds(5));
            // As in Retire: the relaunch's preparation must not see the dying tree.
            var deadline = Environment.TickCount64 + 2000;
            while (AnyIdentityAlive(targets) && Environment.TickCount64 < deadline) Thread.Sleep(50);
            _queue.TryEnqueue(() =>
            {
                if (_disposed) return;
                lock (_gate) _start = null;
                _ = EnsureRunningAsync();
            });
        });
    }

    /// Forgets the session's window and processes; the next start begins clean.
    private DesktopClientInstall? ResetSession()
    {
        lock (_gate)
        {
            var previous = _activeInstall;
            _client = IntPtr.Zero;
            _clientPid = 0;
            _borrowed = false;
            _alreadyRunning = false;
            _launched = false;
            _activeInstall = null;
            _resourceRoot = null;
            _resourceDirectory = null;
            _resourceExecutable = null;
            _groupCache = null;
            _hasVisibleRegion = false;
            _layoutApplied = false;
            _launchedWork = null;
            _surfaceHeld = false;
            _quietHidden.Clear();
            return previous;
        }
    }

    private void RemoveTrayIcons()
    {
        string? executable;
        int pid;
        lock (_gate) { executable = _resourceExecutable; pid = _clientPid; }
        if (executable is null || pid == 0) return;
        var icons = TrayIcons.Read(executable);
        if (icons.Count == 0) return;
        var removed = TrayIconRemover.Remove(icons, ResourceGroup.Processes.Select(identity => identity.Pid).ToArray());
        if (removed > 0 && !_trayLogged)
        {
            _trayLogged = true;
            AppLog.Info(_spec.Title + " tray icon removed.");
        }
    }

    private static bool ProcessAlive(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch { return false; }
    }

    private void Publish(ClientState state, string? message)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _state = state;
        }
        StatusChanged?.Invoke(state, message);
    }

    /// CPU priority until the trim loop's next tick. A visible room or live
    /// media stays Normal; a closed room yields like any other idle host.
    /// While a game runs, a selected room behind it no longer holds Normal;
    /// a live song still does, since a starved decoder or network thread is
    /// heard as crackle. Same rule as the trim loop in MainWindow.
    internal void ApplyForeground(bool inFront, bool mediaPlaying = false, bool gameRunning = false)
    {
        _inFront = inFront;
        _mediaPlaying = mediaPlaying;
        if (mediaPlaying) Wake("playback");
        bool wanted, audio;
        lock (_gate) { wanted = _wanted; audio = _audioActive; }
        var keepHot = (wanted && !gameRunning) || mediaPlaying || audio;
        var priority = inFront || keepHot ? ProcessPriorityClass.Normal : ProcessPriorityClass.BelowNormal;
        foreach (var identity in ResourceGroup.Processes)
            BackgroundTrim.ApplyManaged(identity, low: !inFront && !keepHot, trim: false, priority);
    }

    internal bool IsClientAlive() => ClientPid != 0 && ProcessAlive(ClientPid);

    /// Exo is closing: every embedded session closes with it. The polite close
    /// is posted now; the returned task (on the thread pool, independent of
    /// the dispatcher) gives the tree its grace and kills what is left, so the
    /// hosts close side by side under the window's one bounded wait.
    internal Task ShutdownAsync(TimeSpan grace)
    {
        var targets = BeginClose();
        Forget();
        return Task.Run(() => WaitThenKill(targets, grace));
    }

    /// The provider changed: close this session off the UI thread and hand
    /// back a task the replacement host awaits before it launches.
    internal DesktopClientExitGate Retire()
    {
        DesktopClientExitGate? previous;
        lock (_gate) previous = _previousExit;
        var targets = BeginClose();
        Forget();
        var ownExit = new DesktopClientExitGate(() =>
        {
            var terminated = WaitThenKill(targets, TimeSpan.FromSeconds(5));
            if (terminated > 0) AppLog.Warn(_spec.Title + " had to be terminated during the provider switch.");
            // Killed trees release their single-instance mutex a moment later.
            var deadline = Environment.TickCount64 + 2000;
            while (AnyIdentityAlive(targets) && Environment.TickCount64 < deadline) Thread.Sleep(50);
            if (AnyIdentityAlive(targets)) throw new TimeoutException(_spec.Title + " did not exit.");
            return Task.CompletedTask;
        });
        var exit = DesktopClientExitGate.Combine(previous, ownExit)!;
        _ = ObserveRetirementAsync(exit);
        return exit;
    }

    private static async Task ObserveRetirementAsync(DesktopClientExitGate exit)
    {
        try { await exit.WaitAsync().ConfigureAwait(false); }
        catch (Exception error) { AppLog.Warn("Music session retirement failed: " + error.GetType().Name); }
    }

    /// Stop hosting, ask the client to close politely, and return the process
    /// identities that must be gone.
    private List<ManagedProcessIdentity> BeginClose()
    {
        Wake("close");
        nint client;
        bool launched;
        DesktopClientInstall? install;
        ManagedProcessIdentity? root;
        string? directory;
        lock (_gate)
        {
            client = _client; launched = _launched; install = _activeInstall;
            root = _resourceRoot; directory = _resourceDirectory;
            _disposed = true;
        }
        _lifetime.Cancel();
        StopWatch();
        UnhookClientEvents();
        Unregister();
        var targets = CloseTargetIdentities(launched, install, root, directory);
        if (client != IntPtr.Zero)
        {
            try { ShowWindowAsync(client, SwHide); } catch { }
            try { PostMessage(client, WmClose, IntPtr.Zero, IntPtr.Zero); } catch { }
        }
        return targets;
    }

    private void Forget()
    {
        ResetSession();
        lock (_gate)
        {
            _watchTicks = 0;
            _trayLogged = false;
            _prepareWarning = null;
            _restartedStalled = false;
            _adoptRestarted = false;
        }
    }

    /// Give the tree the grace period, then terminate what is still running.
    /// An updater (a Squirrel Update.exe) is never killed or waited on: it may
    /// be mid-install. Returns how many processes needed termination.
    private int WaitThenKill(IReadOnlyList<ManagedProcessIdentity> targets, TimeSpan grace)
    {
        if (targets.Count == 0) return 0;
        var deadline = Environment.TickCount64 + (long)grace.TotalMilliseconds;
        while (AnyIdentityAlive(targets) && Environment.TickCount64 < deadline) Thread.Sleep(100);
        var killed = 0;
        foreach (var identity in targets)
        {
            try
            {
                using var process = Process.GetProcessById(identity.Pid);
                if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != identity.StartedUtcTicks ||
                    IsUpdater(process.ProcessName)) continue;
                if (ProcessTermination.Kill(process)) killed++;
            }
            catch { }
        }
        return killed;
    }

    private List<ManagedProcessIdentity> CloseTargetIdentities(bool launched,
        DesktopClientInstall? install, ManagedProcessIdentity? root, string? directory)
    {
        var targets = new List<ManagedProcessIdentity>();
        if (root is { } captured)
        {
            targets.Add(captured);
            targets.AddRange(ManagedProcessResources.HostedGroup(Key, captured, directory).Processes);
        }
        // Launched but not yet embedded (or the root already died): whatever
        // the install currently runs is still this session's.
        if (launched && install is not null &&
            (root is null || ManagedProcessResources.Identify(root.Value.Pid) != root.Value))
        {
            foreach (var candidate in ClientWindowPids(install))
            {
                var identity = ManagedProcessResources.Identify(candidate);
                if (identity is not { } process || process.StartedUtcTicks == 0) continue;
                targets.Add(process);
                targets.AddRange(ManagedProcessResources.HostedGroup(Key, process, install.AppDirectory).Processes);
            }
        }
        return targets.Where(identity => identity.StartedUtcTicks != 0).Distinct().ToList();
    }

    private bool AnyIdentityAlive(IReadOnlyList<ManagedProcessIdentity> targets)
    {
        foreach (var identity in targets)
        {
            try
            {
                using var process = Process.GetProcessById(identity.Pid);
                if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != identity.StartedUtcTicks ||
                    IsUpdater(process.ProcessName)) continue;
                return true;
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            catch { return true; }
        }
        return false;
    }

    public void Dispose()
    {
        Wake("dispose");
        _disposed = true;
        _lifetime.Cancel();
        StopWatch();
        UnhookClientEvents();
        _launchGuard?.Dispose();
        Unregister();
    }

    private const int GwlStyle = -16;
    private const int GwlExstyle = -20;
    private const int GwlpHwndParent = -8;
    private const long WsCaption = 0x00C00000;
    private const long WsThickframe = 0x00040000;
    private const long WsPopup = 0x80000000;
    private const long WsBorder = 0x00800000;
    private const long WsDlgFrame = 0x00400000;
    private const long WsSysmenu = 0x00080000;
    private const long WsMinimizeBox = 0x00020000;
    private const long WsMaximizeBox = 0x00010000;
    private const long WsChild = 0x40000000;
    private const long WsClipChildren = 0x02000000;
    private const long WsClipSiblings = 0x04000000;
    private const long WsExAppWindow = 0x00040000;
    private const long WsExToolWindow = 0x00000080;
    private const long WsExDlgModalFrame = 0x00000001;
    private const int SwHide = 0;
    private const int SwShow = 5;
    private const int SwShowNoActivate = 4;
    private const int SwShowNa = 8;
    private const int SwpNoSize = 0x0001;
    private const int SwpNoMove = 0x0002;
    private const int SwpNoZorder = 0x0004;
    private const int SwpNoActivate = 0x0010;
    private const int SwpFrameChanged = 0x0020;
    private const int SwpAsyncWindowPos = 0x4000;
    private const uint GwOwner = 4;
    private const int RgnError = 0;
    private const int WmClose = 0x0010;
    private const uint WmNull = 0x0000;
    private const uint SmtoAbortIfHung = 0x0002;
    private const uint WmCancelMode = 0x001F;
    private const uint EventSystemForeground = 0x0003;
    private const uint EventSystemMoveSizeStart = 0x000A;
    private const uint EventSystemMoveSizeEnd = 0x000B;
    private const uint EventObjectShow = 0x8002;
    private const uint EventObjectHide = 0x8003;
    private const uint EventObjectLocationChange = 0x800B;
    private const int ObjidWindow = 0;
    private const uint GaRoot = 2;
    private const uint WmQuit = 0x0012;

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg { public IntPtr Hwnd; public uint Message; public IntPtr WParam; public IntPtr LParam; public uint Time; public int X; public int Y; }

    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern int GetMessage(out Msg message, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Msg message);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref Msg message);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint thread, uint message, IntPtr wParam, IntPtr lParam);
    private const uint ProcessSetQuota = 0x0100;
    private const uint ProcessTerminate = 0x0001;
    private const int JobObjectExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitSilentBreakawayOk = 0x00001000;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const uint WineventOutOfContext = 0x0000;
    private const int WhKeyboardLl = 13;
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmsbtNone = 1;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmcwcpDonotround = 1;
    private const int DwmColorNone = unchecked((int)0xFFFFFFFE);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    private delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    private delegate nint LowLevelKeyboardProc(int code, nint message, nint data);

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput { public uint VirtualKey, ScanCode, Flags, Time; public nuint ExtraInfo; }

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll")] private static extern bool GetClassName(IntPtr hwnd, StringBuilder name, int maxCount);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref PointInt32 point);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc callback, uint processId, uint threadId, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookExW(int id, LowLevelKeyboardProc callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? module);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern int CombineRgn(IntPtr destination, IntPtr first, IntPtr second, int mode);
    private const int RgnDiff = 4;
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(IntPtr hwnd, IntPtr region);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint attachTo, bool join);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JobObjectExtendedLimitInformation info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hwnd, int index);
    private static IntPtr GetWindowLongPtr(IntPtr hwnd, int index) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, index) : new IntPtr(GetWindowLong32(hwnd, index));

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern IntPtr SetWindowLong32(IntPtr hwnd, int index, int value);
    private static IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value) =>
        IntPtr.Size == 8 ? SetWindowLongPtr64(hwnd, index, value) : new IntPtr(SetWindowLong32(hwnd, index, value.ToInt32()));
}
