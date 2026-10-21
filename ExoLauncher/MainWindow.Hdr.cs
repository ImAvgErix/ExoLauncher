using ExoLauncher.Helpers;
using ExoLauncher.Services;

namespace ExoLauncher;

/// <summary>
/// System > Graphics > HDR > HDR for HDR games: while a game that outputs real HDR runs (PCGamingWiki
/// says so, or the game's page says Always), however it was started, HDR is on for the main
/// screen; when it closes (or Exo does) HDR goes back off and the screen's calibration is put
/// back. Games without HDR stay SDR. Only HDR Exo itself switched on is ever switched off.
/// While any screen is in HDR (Exo's doing or the user's), SDR on it is drawn with gamma 2.2
/// (<see cref="SdrInHdr"/>); when HDR goes off the profile the screen had before comes back.
/// </summary>
public sealed partial class MainWindow
{
    private static readonly TimeSpan HdrRecheck = TimeSpan.FromSeconds(5);
    private System.Threading.Timer? _hdrWatch;
    private WindowsHdr.Screen? _hdrSwitchedOn;
    private ushort[]? _hdrRamp;
    private int _hdrSyncing, _hdrAsked;
    private string? _lastScreenMode;
    private SubclassProc? _displayChangeProc;

    // No polling while nothing runs: a game starting or closing (the bridge's run-state
    // poll reports games started outside Exo too) and a display change wake the check;
    // while a library game runs with HDR for games on, it looks again every 5 s.
    private void StartHdrForGames()
    {
        _hdrWatch = new System.Threading.Timer(_ => SyncHdr(), null, HdrRecheck, Timeout.InfiniteTimeSpan);
        App.Services.Launcher.GameRunStateChanged += OnHdrRunState;
        _displayChangeProc = (hwnd, message, wParam, lParam, id, data) =>
        {
            if (message == WmDisplayChange) SyncHdrSoon();
            else if (message == WmNcDestroy) RemoveWindowSubclass(hwnd, _displayChangeProc!, id);
            return DefSubclassProc(hwnd, message, wParam, lParam);
        };
        try { SetWindowSubclass(WinRT.Interop.WindowNative.GetWindowHandle(this), _displayChangeProc, 0x48445220, 0); }
        catch (Exception ex) { AppLog.Debug("HDR: display change hook failed: " + ex.GetType().Name); }
    }

    private void OnHdrRunState(string gameId, GameRunState state) => SyncHdrSoon();

    /// <summary>Runs the HDR check on the pool now: a game started or closed, a setting or the screen changed.</summary>
    internal void SyncHdrSoon()
    {
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
            SyncSdrGamma();
            if (!App.Services.Settings.Shared.HdrForGames) { RestoreHdr(); return _hdrSwitchedOn is not null; }
            var running = RunningLibraryGames().Keys;
            var want = running.Any(id =>
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
                    AppLog.Info("HDR on for a game.");
                    SyncSdrGamma();
                }
            }
            else if (!want) RestoreHdr();
            return running.Count > 0 || _hdrSwitchedOn is not null;
        }
        catch (Exception ex) { AppLog.Debug("HDR for games: " + ex.GetType().Name); return _hdrSwitchedOn is not null; }
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

    /// <summary>Exo's SDR gamma 2.2 profile on every screen in HDR (when the setting is on), off every other.</summary>
    private void SyncSdrGamma()
    {
        var on = App.Services.Settings.Shared.HdrSdrGamma;
        foreach (var screen in WindowsHdr.All())
        {
            if (on && screen.Enabled)
            {
                if (SdrInHdr.Apply(screen, RememberSdrGammaReplaced) is { } why)
                    AppLog.Debug("HDR: SDR gamma 2.2 not on " + screen.GdiName + ": " + why);
            }
            else SdrInHdr.Remove(screen, ForgetSdrGammaReplaced(screen.DevicePath));
        }
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
        if (_hdrRamp is { } ramp) WindowsHdr.WriteRamp(screen.GdiName, ramp);
        AppLog.Info("HDR back off after the game.");
    }

    private void StopHdrForGames()
    {
        try { App.Services.Launcher.GameRunStateChanged -= OnHdrRunState; } catch { }
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

    [System.Runtime.InteropServices.DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam);
}
