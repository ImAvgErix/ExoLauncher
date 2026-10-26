using ExoLauncher.Helpers;
using ExoLauncher.Services;

namespace ExoLauncher;

/// <summary>
/// System > Graphics > HDR > HDR for HDR games: while a game that outputs real HDR runs (PCGamingWiki
/// says so, or the game's page says Always), however it was started, HDR is on for the main
/// screen; when it closes (or Exo does) HDR goes back off and the screen's calibration is put
/// back. A game Exo starts that is already known to want HDR gets it just before its store starts it
/// (<see cref="OnHdrLaunching"/>), since some games look for HDR only at their first start.
/// Games without HDR stay SDR. Only HDR Exo itself switched on is ever switched off.
/// While any screen is in HDR (Exo's doing or the user's), SDR on it is drawn with gamma 2.2
/// (<see cref="SdrInHdr"/>); when HDR goes off the profile the screen had before comes back.
/// The screen Exo switched on is kept in settings: HDR still goes back off after a crash or an update.
/// </summary>
public sealed partial class MainWindow
{
    private static readonly TimeSpan HdrRecheck = TimeSpan.FromSeconds(5);
    private System.Threading.Timer? _hdrWatch;
    private WindowsHdr.Screen? _hdrSwitchedOn;
    private ushort[]? _hdrRamp;
    private int _hdrSyncing, _hdrAsked, _sdrGammaTries = 6;
    private bool _hdrAdopted;
    // Games Exo is starting that want HDR, by when they were handed to their store: until the process shows up (or this
    // long passes) they count as running, so HDR is already on when it starts. Some games look for HDR only at first start.
    private const long HdrLaunchGraceMs = 120_000;
    private readonly Dictionary<string, long> _hdrLaunching = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _sdrGammaWhyLogged = [];
    private string? _lastScreenMode;
    private SubclassProc? _displayChangeProc;

    // No polling while nothing runs: a game starting or closing (the bridge's run-state
    // poll reports games started outside Exo too) and a display change wake the check;
    // while a library game runs with HDR for games on, it looks again every 5 s.
    private void StartHdrForGames()
    {
        _hdrWatch = new System.Threading.Timer(_ => SyncHdr(), null, HdrRecheck, Timeout.InfiniteTimeSpan);
        App.Services.Launcher.GameRunStateChanged += OnHdrRunState;
        App.Services.Launcher.GameLaunching += OnHdrLaunching;
        var taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        _displayChangeProc = (hwnd, message, wParam, lParam, id, data) =>
        {
            if (message == WmDisplayChange) SyncHdrSoon();
            else if (message == taskbarCreated && taskbarCreated != 0) NvidiaTrayIcon.Soon();
            else if (message == WmNcDestroy) RemoveWindowSubclass(hwnd, _displayChangeProc!, id);
            return DefSubclassProc(hwnd, message, wParam, lParam);
        };
        try { SetWindowSubclass(WinRT.Interop.WindowNative.GetWindowHandle(this), _displayChangeProc, 0x48445220, 0); }
        catch (Exception ex) { AppLog.Debug("HDR: display change hook failed: " + ex.GetType().Name); }
    }

    private void OnHdrRunState(string gameId, GameRunState state) => SyncHdrSoon();

    // Just before the store starts a game that wants HDR (on the launch's own thread, so this finishes first): HDR goes
    // on now, not once the process is up, since a game that checks for HDR only at start would otherwise never offer it.
    // Unknown "auto" games are still caught once they run; only what the wiki already said counts here.
    private void OnHdrLaunching(Models.GameEntry game)
    {
        var settings = App.Services.Settings;
        if (!settings.Shared.HdrForGames || !WantsHdr(settings.HdrGameMode(game.Id), HdrGames.Supports(game.Id))) return;
        lock (_hdrLaunching) _hdrLaunching[game.Id] = Environment.TickCount64;
        SyncHdr();
    }

    /// <summary>The games Exo started that are not running yet and still inside the grace; one that shows up, or outstays it, is dropped.</summary>
    internal static List<string> PendingHdrLaunches(Dictionary<string, long> launching, IEnumerable<string> running, long now, long graceMs)
    {
        lock (launching)
        {
            var seen = running.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (id, at) in launching.ToList())
                if (seen.Contains(id) || now - at > graceMs) launching.Remove(id);
            return [.. launching.Keys];
        }
    }

    /// <summary>Runs the HDR check on the pool now: a game started or closed, a setting or the screen changed.</summary>
    internal void SyncHdrSoon()
    {
        // A screen just switched to HDR can still be settling: the profile is tried again for about 30 s.
        Volatile.Write(ref _sdrGammaTries, 6);
        try { _hdrWatch?.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { }
    }

    private void SyncHdr()
    {
        Interlocked.Exchange(ref _hdrAsked, 1);
        if (Interlocked.Exchange(ref _hdrSyncing, 1) == 1) return;
        var again = false;
        try
        {
            while (Interlocked.Exchange(ref _hdrAsked, 0) == 1) again = SyncHdrOnce();
        }
        finally { Interlocked.Exchange(ref _hdrSyncing, 0); }
        if (Volatile.Read(ref _hdrAsked) == 1) { SyncHdr(); return; }
        try { _hdrWatch?.Change(again ? HdrRecheck : Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { }
    }

    /// <summary>One look; true while a library game runs with HDR for games on, so it looks again.</summary>
    private bool SyncHdrOnce()
    {
        try
        {
            FollowScreenMode();
            AdoptHdr();
            var gammaPending = !SyncSdrGamma() && Interlocked.Decrement(ref _sdrGammaTries) >= 0;
            if (!App.Services.Settings.Shared.HdrForGames) { RestoreHdr(); return _hdrSwitchedOn is not null || gammaPending; }
            var running = RunningLibraryGames().Keys;
            var pending = PendingHdrLaunches(_hdrLaunching, running, Environment.TickCount64, HdrLaunchGraceMs);
            var want = running.Concat(pending).Any(id =>
            {
                var mode = App.Services.Settings.HdrGameMode(id);
                if (mode == "auto" && HdrGames.Cached(id) is null) LookUpHdr(id);
                return WantsHdr(mode, HdrGames.Supports(id));
            });
            if (want && _hdrSwitchedOn is null && WindowsHdr.Primary() is { Supported: true, Enabled: false } screen)
            {
                var ramp = WindowsHdr.ReadRamp(screen.GdiName);
                if (WindowsHdr.Set(screen, true))
                {
                    _hdrSwitchedOn = screen;
                    _hdrRamp = ramp;
                    RememberHdrSwitchedOn(screen.DevicePath);
                    AppLog.Info("HDR on for a game.");
                    SyncSdrGamma();
                }
            }
            else if (!want) RestoreHdr();
            return running.Count > 0 || pending.Count > 0 || _hdrSwitchedOn is not null || gammaPending;
        }
        catch (Exception ex) { AppLog.Debug("HDR for games: " + ex.GetType().Name); return _hdrSwitchedOn is not null; }
    }

    // Once a run: a screen Exo switched HDR on for before it last closed, still in HDR, is Exo's to switch back off.
    private void AdoptHdr()
    {
        if (_hdrAdopted) return;
        _hdrAdopted = true;
        var path = App.Services.Settings.Shared.HdrSwitchedOn;
        if (path.Length == 0 || _hdrSwitchedOn is not null) return;
        if (WindowsHdr.All().FirstOrDefault(screen => string.Equals(screen.DevicePath, path, StringComparison.OrdinalIgnoreCase)) is { Enabled: true } left)
        {
            _hdrSwitchedOn = left;
            AppLog.Info("HDR: still on from the last run's game; Exo switches it back off once no HDR game runs.");
        }
        else RememberHdrSwitchedOn("");
    }

    private static void RememberHdrSwitchedOn(string devicePath)
    {
        try { App.Services.Settings.UpdateProfile(settings => settings.HdrSwitchedOn = devicePath); }
        catch (Exception ex) { AppLog.Warn("HDR: " + ex.Message); }
    }

    // A new refresh rate or main screen moves the G-SYNC frame cap with it; the driver is
    // touched only when the main screen's mode changed since the last look.
    private void FollowScreenMode()
    {
        var gdi = WindowsHdr.PrimaryGdiName();
        var mode = gdi is null ? null : gdi + "@" + DisplayOutput.RefreshRates(gdi).Current;
        if (mode == _lastScreenMode) return;
        _lastScreenMode = mode;
        Services.GpuProfileService.FollowRefresh();
    }

    /// <summary>On for a game set to Always, or on Auto when the game outputs HDR itself.</summary>
    internal static bool WantsHdr(string mode, bool? supports) => mode == "on" || (mode == "auto" && supports == true);

    // A game started before its page was ever opened: ask the wiki now; the next check uses the answer.
    private void LookUpHdr(string gameId)
    {
        if (App.Services.Library.PeekCachedLibrary().FirstOrDefault(g => g.Id.Equals(gameId, StringComparison.OrdinalIgnoreCase)) is not { } game)
            return;
        _ = Task.Run(() => HdrGames.LookupAsync(game, _lifetimeCts.Token));
    }

    /// <summary>
    /// The advanced-color profile a screen had before Exo's SDR gamma one (null: none), kept in settings: Exo's
    /// profile outlives a restart while the screen stays in HDR, and the calibration must still come back after.
    /// </summary>
    internal static string? SdrGammaReplaced(SettingsService settings, string devicePath) =>
        settings.Shared.SdrGammaBefore.TryGetValue(devicePath, out var name) && name.Length > 0 ? name : null;

    private static void RememberSdrGammaReplaced(string devicePath, string? previous)
    {
        try { App.Services.Settings.UpdateProfile(settings => settings.SdrGammaBefore[devicePath] = previous ?? ""); }
        catch (Exception ex) { AppLog.Warn("HDR: the profile SDR gamma replaced was not saved: " + ex.Message); }
    }

    private static string? ForgetSdrGammaReplaced(string devicePath)
    {
        var settings = App.Services.Settings;
        if (!settings.Shared.SdrGammaBefore.ContainsKey(devicePath)) return null;
        var before = SdrGammaReplaced(settings, devicePath);
        try { settings.UpdateProfile(s => s.SdrGammaBefore.Remove(devicePath)); }
        catch (Exception ex) { AppLog.Warn("HDR: " + ex.Message); }
        return before;
    }

    /// <summary>Exo's SDR gamma 2.2 profile on every screen in HDR (when the setting is on), off every other; false while a
    /// screen in HDR is still without it.</summary>
    private bool SyncSdrGamma()
    {
        var on = App.Services.Settings.Shared.HdrSdrGamma;
        var done = true;
        foreach (var screen in WindowsHdr.All())
        {
            if (on && screen.Enabled)
            {
                if (SdrInHdr.Apply(screen, RememberSdrGammaReplaced) is not { } why) continue;
                done = false;
                // Once per screen and reason, where a friend's log shows it.
                lock (_sdrGammaWhyLogged)
                    if (_sdrGammaWhyLogged.Add(screen.DevicePath + why)) AppLog.Warn($"HDR: SDR gamma 2.2 not on {screen.GdiName} ({screen.Name}): {why}");
            }
            else SdrInHdr.Remove(screen, ForgetSdrGammaReplaced(screen.DevicePath));
        }
        return done;
    }

    private bool _hdrOffRefusedLogged;

    /// <summary>HDR off on the screen Exo turned it on for. Windows can refuse while the screen reconfigures: then
    /// Exo still owns it and the next look tries again.</summary>
    private void RestoreHdr()
    {
        if (_hdrSwitchedOn is not { } screen) return;
        SdrInHdr.Remove(screen, ForgetSdrGammaReplaced(screen.DevicePath));
        if (!WindowsHdr.Set(screen, false))
        {
            if (!_hdrOffRefusedLogged) AppLog.Warn("HDR: Windows did not turn HDR back off after the game; Exo tries again.");
            _hdrOffRefusedLogged = true;
            return;
        }
        _hdrSwitchedOn = null;
        _hdrOffRefusedLogged = false;
        RememberHdrSwitchedOn("");
        if (_hdrRamp is { } ramp) WindowsHdr.WriteRamp(screen.GdiName, ramp);
        AppLog.Info("HDR back off after the game.");
    }

    private void StopHdrForGames()
    {
        try { App.Services.Launcher.GameRunStateChanged -= OnHdrRunState; } catch { }
        try { App.Services.Launcher.GameLaunching -= OnHdrLaunching; } catch { }
        // A look still running on the pool could switch HDR on again after the restore below.
        try
        {
            using var done = new ManualResetEvent(false);
            if (_hdrWatch?.Dispose(done) == true) done.WaitOne(TimeSpan.FromSeconds(2));
        }
        catch { }
        try { RestoreHdr(); } catch { }
    }

    private const uint WmDisplayChange = 0x007E, WmNcDestroy = 0x0082;

    private delegate nint SubclassProc(nint hwnd, uint message, nuint wParam, nint lParam, nuint subclassId, nuint refData);

    [System.Runtime.InteropServices.DllImport("comctl32.dll")]
    private static extern bool SetWindowSubclass(nint hwnd, SubclassProc callback, nuint subclassId, nuint refData);

    [System.Runtime.InteropServices.DllImport("comctl32.dll")]
    private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc callback, nuint subclassId);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);

    [System.Runtime.InteropServices.DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam);
}
