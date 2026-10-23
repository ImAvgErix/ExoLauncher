using System.Diagnostics;
using ExoLauncher.Helpers;
using ExoLauncher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics;

namespace ExoLauncher;

/// <summary>
/// The Music room. Spotify is a desktop client hosted as an owned
/// popup through <see cref="DesktopClientHost"/>. YouTube Music and Apple Music are their own web players
/// (music.youtube.com, music.apple.com) on the embedded WebView2 profile. The
/// mini player's song, clock and transport come from the app's own page where
/// it has one (<see cref="MusicNowPlaying"/>: YouTube Music, Apple Music,
/// Spotify) and otherwise from the app's own Windows media
/// session (<see cref="MediaSessionService"/>); its volume is the app's own
/// (<see cref="MusicAppVolume"/>) where the app exposes one, else the WASAPI
/// per-app session volume.
/// </summary>
public sealed partial class MainWindow
{
    private WebView2? _musicWeb;
    private Task? _musicWebInit;
    private TaskCompletionSource? _musicWebLoad;
    private string? _musicWebService;
    private bool _musicWebWanted;
    private bool _musicWebClosed;
    private bool _musicWebReady;
    private bool _musicWebFailed;
    private int _musicWebGeneration;
    private readonly Dictionary<WebView2, Window> _musicPopouts = new();
    private DesktopClientHost? _musicApp;
    private MediaSessionService? _mediaSessions;
    private MusicNowPlaying? _musicNowPlaying;
    private volatile bool _musicPlaying;
    private volatile bool _musicKnownIdle;
    private bool _musicWebAudio;

    private static bool IsDesktopMusic(string? service) => service is "spotify";
    /// Providers whose own volume Exo reads in their page (<see cref="MusicAppVolume"/>).
    private static bool HasAppVolume(string? service) => service is "spotify" or "youtube" or "apple";

    private MediaSessionService MediaSessions => _mediaSessions ??= CreateMediaSessions();

    /// The provider page's now-playing reader: one at a time, replaced on a
    /// provider switch, a new YouTube Music view or a new Spotify host.
    private void SetMusicNowPlaying(MusicNowPlaying? next)
    {
        var previous = _musicNowPlaying;
        if (ReferenceEquals(previous, next)) return;
        _musicNowPlaying = next;
        MediaSessions.SetPage(next);
        previous?.Dispose();
        next?.Watch();
    }

    private MediaSessionService CreateMediaSessions()
    {
        var sessions = new MediaSessionService();
        sessions.PlaybackChanged += snapshot =>
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_closingWindow || snapshot.Provider != _musicWebService) return;
                // The hosted desktop app keeps Normal priority while its audio
                // plays so background trimming never stutters playback.
                var playing = IsDesktopMusic(snapshot.Provider) && snapshot.Status == "playing";
                var knownIdle = IsDesktopMusic(snapshot.Provider) && snapshot.Status is "paused" or "stopped";
                if (playing != _musicPlaying || knownIdle != _musicKnownIdle)
                {
                    _musicPlaying = playing;
                    _musicKnownIdle = knownIdle;
                    _musicApp?.ApplyForeground(_windowActive, !knownIdle);
                    ApplyWebViewMemoryTarget(WindowIsHiddenOrMinimized());
                }
                if (snapshot.Status == "playing") ApplyPendingMusicVolume();
                PublishMusicPlayback(snapshot);
            });
        return sessions;
    }

    /// The desktop client surface for the selected provider, created on demand.
    /// The previous provider's session retires off the UI thread; the new host
    /// waits for that tree to be gone before it launches.
    private DesktopClientHost MusicAppHost(string service)
    {
        if (_musicApp is { Key: var key, IsDisposed: false } existing && key == service) return existing;
        var previousExit = RetireMusicApp();
        var host = _musicApp = CreateDesktopHost(DesktopClients.For(service)!, "music.status");
        if (previousExit is not null) host.AwaitBefore(previousExit);
        // Spotify's page is read over its loopback DevTools port; an idle
        // freeze of its tree keeps the last song, paused.
        if (service == "spotify") SetMusicNowPlaying(MusicNowPlaying.ForSpotify(() => host.IsFrozen));
        return host;
    }

    /// The retiring session's exit task. It is kept while no desktop host
    /// exists (YouTube selected) so the next desktop host still waits for it.
    private DesktopClientExitGate? _pendingMusicExit;

    private DesktopClientExitGate? RetireMusicApp()
    {
        var pending = _pendingMusicExit;
        _pendingMusicExit = null;
        if (_musicApp is not { } previous) return pending;
        _musicApp = null;
        DropMusicAppVolume(desktop: true);
        DropSpotifyBridge();
        if (IsDesktopMusic(_musicNowPlaying?.Service)) SetMusicNowPlaying(null);
        _musicPlaying = false;
        _musicKnownIdle = false;
        var exit = previous.Retire();
        return DesktopClientExitGate.Combine(pending, exit);
    }

    internal void SelectMusicService(string service)
    {
        if (!MusicWebPolicy.IsService(service) || _musicWebService == service) return;
        _musicWebWanted = false;
        // One active provider. Switching closes the previous music app; the
        // web surface releases its renderer.
        _pendingMusicExit = RetireMusicApp();
        ResetMusicWebView();
        _musicWebService = service;
        MediaSessions.SetProvider(service);
        PublishMusicStatus(service, "loading");
    }

    internal object SetMusicBounds(string? service, bool visible,
        double left, double top, double width, double height, bool background = false,
        IReadOnlyList<Services.DesktopClientHost.DipRect>? cutouts = null)
    {
        // The page's CSS pixels at its zoom (MainWindow.PageScale) to the window's DIPs.
        var zoom = PageZoom;
        (left, top, width, height) = (left * zoom, top * zoom, width * zoom, height * zoom);
        cutouts = cutouts?.Select(cut => new Services.DesktopClientHost.DipRect(cut.Left * zoom, cut.Top * zoom, cut.Width * zoom, cut.Height * zoom)).ToList();
        if (!visible)
        {
            _musicWebWanted = false;
            if (_musicApp is not null) _musicApp.SetBounds(false, 0, 0, 0, 0);
            if (_musicWeb is not null)
            {
                // Collapsing WebView2 stalls provider updates and queued script
                // results. Keep the audio surface alive without drawing or input.
                EmbeddedClientFrame.SetInputVisible(_musicWeb, false);
            }
            ApplyWebViewMemoryTarget(WindowIsHiddenOrMinimized());
            return new { ok = true, ready = false, service = _musicWebService };
        }
        if (!MusicWebPolicy.IsService(service))
            return new { ok = false, message = "Choose Spotify, Apple Music, or YouTube Music." };
        var selected = Services.WebClientPreferences.Read().MusicService;
        if (selected is not null && selected != service)
            return new { ok = false, message = "Choose your music service in Settings." };
        if (!EmbeddedClientFrame.ValidBounds(left, top, width, height) ||
            left >= RootGrid.ActualWidth || top >= RootGrid.ActualHeight)
            return new { ok = false, service, message = "Music is still opening." };
        if (_musicWebClosed) return new { ok = false, service, message = "The window is closing." };

        if (_musicWebService != service)
            SelectMusicService(service!);
        _musicWebWanted = !background;

        if (IsDesktopMusic(service))
        {
            ResetMusicWebView();
            var app = MusicAppHost(service!);
            app.SetBounds(true, left, top, width, height, cutouts);
            return new { ok = true, ready = false, service, state = ClientStateName(app.State) };
        }

        // YouTube Music and Apple Music: their own web player. Layout is
        // answered at once; the launch (waiting for the previous app to exit,
        // then the page load) runs behind it and reports as `music.status`.
        _pendingMusicExit = RetireMusicApp();
        if (_musicWebFailed) ResetMusicWebView();
        if (_musicWeb is null)
        {
            _musicWeb = new WebView2
            {
                Visibility = Visibility.Collapsed,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                DefaultBackgroundColor = Windows.UI.Color.FromArgb(255, 0, 0, 0),
            };
            ContentHost.Children.Add(_musicWeb);
        }
        var view = _musicWeb;
        EmbeddedClientFrame.SetBounds(view, left, top,
            Math.Min(width, RootGrid.ActualWidth - left), Math.Min(height, RootGrid.ActualHeight - top));
        // Until its first document has loaded the view would only show its
        // black background over the room's own loading state.
        EmbeddedClientFrame.SetInputVisible(view, !background && _musicWebReady);
        ApplyWebViewMemoryTarget(WindowIsHiddenOrMinimized());
        if (_musicWebInit is null)
        {
            _musicWebInit = StartMusicWebAsync(view, service!, _pendingMusicExit);
            return new { ok = true, ready = false, service };
        }
        if (_musicWebReady && !_musicWebClosed && _musicWebWanted)
        {
            if (view.Visibility != Visibility.Visible) view.Visibility = Visibility.Visible;
            EmbeddedClientFrame.SetInputVisible(view, true);
        }
        return new { ok = !_musicWebFailed, ready = _musicWebReady, service,
            message = _musicWebFailed ? "The player could not load. Choose Retry to reconnect." : null };
    }

    /// The YouTube Music launch behind <see cref="SetMusicBounds"/>: the
    /// previous desktop app exits first, then the page loads. Its outcome
    /// reaches the room as `music.status` (running on load, error here).
    private async Task StartMusicWebAsync(WebView2 view, string service, DesktopClientExitGate? pendingExit)
    {
        await Task.Yield();
        if (pendingExit is not null)
        {
            try { await pendingExit.WaitAsync(_lifetimeCts.Token); }
            catch (OperationCanceledException) when (_musicWebClosed || _lifetimeCts.IsCancellationRequested) { return; }
            catch (Exception)
            {
                if (!ReferenceEquals(_musicWeb, view)) return;
                const string exitMessage = "The previous music app is still running. Close it, then reopen Music to retry.";
                AppLog.Warn("Music: " + exitMessage);
                _musicWebFailed = true;
                PublishMusicStatus(service, "error", exitMessage);
                return;
            }
            if (ReferenceEquals(_pendingMusicExit, pendingExit)) _pendingMusicExit = null;
        }
        if (_musicWebClosed || !ReferenceEquals(_musicWeb, view) || _musicWebService != service) return;
        try { await InitializeMusicWebAsync(view, service); }
        catch (Exception) when (_musicWebClosed || !ReferenceEquals(_musicWeb, view)) { }
        catch (Exception ex)
        {
            // A failed navigation or crashed renderer has already said why.
            var reported = _musicWebFailed;
            AppLog.Warn("Music web player initialization failed: " + ex.GetType().Name);
            ResetMusicWebView();
            _musicWebFailed = true;
            if (!reported)
                PublishMusicStatus(service, "error", MusicWebPolicy.Label(service) + " could not load. Choose Retry to reconnect.");
        }
    }

    /// Consented install: the room's button is the only caller.
    internal async Task<object> InstallMusicAsync(string? service)
    {
        if (!MusicWebPolicy.IsService(service) ||
            WebClientPreferences.Read().MusicService is { } selected && selected != service)
            return new { ok = false, message = "Choose your music service in Settings." };
        if (!IsDesktopMusic(service)) return new { ok = false, message = MusicWebPolicy.Label(service!) + " needs no install." };
        var outcome = await MusicAppHost(service!).InstallAsync();
        return outcome.ToWire(DesktopClients.For(service)?.Title ?? "the music app");
    }

    internal async Task<object> OpenMusicAsync(string? service)
    {
        if (!MusicWebPolicy.IsService(service) ||
            WebClientPreferences.Read().MusicService is { } selected && selected != service)
            return new { ok = false, message = "Choose your music service in Settings." };
        if (IsDesktopMusic(service))
        {
            await MusicAppHost(service!).ReopenAsync();
            return new { ok = true };
        }
        return ReloadMusic();
    }

    /// Transport goes to the source of the song the mini player shows: the
    /// app's own page or its own Windows media session. A frozen app is
    /// thawed first; the page call retries while it wakes.
    internal Task<object> ControlMusicAsync(string? action, double? positionMs)
    {
        if (MusicWebPolicy.IsWeb(_musicWebService)) WakeMusicWeb();
        _musicApp?.Wake("control");
        return MediaSessions.ControlAsync(action, positionMs);
    }

    internal object MusicPlayback() => new { ok = true, playback = PlaybackDto(MediaSessions.Current) };

    /// One volume: the music app's own. Spotify and YouTube Music are read and
    /// written inside their page (<see cref="MusicAppVolume"/>), so the mini
    /// player and the app's own slider are the same control, and a change made
    /// in the app reaches the shell as a <c>music.volume</c> event. A provider
    /// with no volume of its own uses the WASAPI session volume of the hosted tree, which the mini player re-reads while it is
    /// shown so it follows the Windows mixer.
    /// The session path always answers: before the player has an audio session
    /// the chosen level is remembered and applied the moment sound starts, so
    /// the control never greys out or snaps back because of the app's state.
    internal async Task<object> MusicVolumeAsync(double? level, bool? muted)
    {
        var change = level is not null || muted is not null;
        if (change)
        {
            _musicApp?.Wake("volume");
            if (MusicWebPolicy.IsWeb(_musicWebService)) WakeMusicWeb();
        }
        // A plain read of a frozen app (reads never thaw it) answers at once with
        // the level it last reported, instead of waiting out the retries below.
        if (level is null && muted is null && _musicApp?.IsFrozen == true &&
            _musicAppLevelLast is { } frozenLast && frozenLast.Service == _musicWebService)
            return new { ok = true, level = frozenLast.Level, muted = frozenLast.Level <= 0, source = "app" };
        if (MusicAppLevel() is { } app)
        {
            var target = muted == true ? 0 : level;
            var appLevel = target is { } wanted ? await app.WriteAsync(wanted) : await app.ReadAsync();
            // An idle Spotify is frozen until Wake thaws it; its page needs a
            // moment to answer again, so a change is retried instead of
            // falling back to the Windows session volume. A read thawed
            // nothing: it answers at once below, and the shell asks again.
            for (var attempt = 0; change && appLevel is null && attempt < 6 && ReferenceEquals(app, _musicAppVolume); attempt++)
            {
                await Task.Delay(500);
                appLevel = target is { } retry ? await app.WriteAsync(retry) : await app.ReadAsync();
            }
            if (appLevel is { } value && ReferenceEquals(app, _musicAppVolume))
            {
                _musicVolumeWanted = null;
                value = await FoldSessionVolumeAsync(app, value);
                _musicAppLevelLast = (_musicWebService, value);
                return new { ok = true, level = value, muted = value <= 0, source = "app" };
            }
        }
        if (HasAppVolume(_musicWebService))
        {
            // The page did not answer (not started, loading, restarting): its
            // level is unknown. The Windows session is not the app's volume (it
            // sits at 100% once folded), so it is neither shown nor written in
            // its place; a chosen level waits for the page, and the shell asks
            // again until the page answers.
            var chosen = muted == true ? 0 : level;
            if (chosen is { } later) _musicVolumeWanted = later;
            // A read of a paused, frozen app (reads do not thaw it) shows the
            // level it last reported, so the slider stays live instead of
            // greying out at 0 until the music plays again.
            if (chosen is null && _musicAppLevelLast is { } last && last.Service == _musicWebService)
                return new { ok = true, level = last.Level, muted = last.Level <= 0, source = "app" };
            return new { ok = true, level = chosen, muted = chosen is <= 0, pending = true, source = "app" };
        }
        var pids = MusicVolumePids();
        var names = MusicVolumeProcessNames();
        var result = pids.Count == 0 && names.Count == 0 ? null : await AppVolume.ApplyAsync(pids, level, muted, names);
        if (result is not null)
        {
            _musicVolumeWanted = null;
            _musicVolumeLast = result.Muted ? 0.0 : result.Level;
            return new { ok = true, level = _musicVolumeLast, muted = result.Muted, source = "session" };
        }
        if (level is { } queued)
        {
            _musicVolumeWanted = queued;
            return new { ok = true, level = queued, muted = muted == true, pending = true, source = "session" };
        }
        if (_musicVolumeWanted is { } pending)
            return new { ok = true, level = pending, muted = false, pending = true, source = "session" };
        // No session yet and nothing queued: show the level the last session
        // actually had, so the slider is live before the first note instead of
        // greyed out. It is never written until the user moves it.
        return new { ok = true, level = _musicVolumeLast, muted = false, pending = true, source = "session" };
    }

    private double? _musicVolumeWanted;
    private double? _musicVolumeLast;
    /// <summary>The level the music app last reported, by service: shown while a paused app is frozen.</summary>
    private (string? Service, double Level)? _musicAppLevelLast;
    private MusicAppVolume? _musicAppVolume;

    /// The selected provider's own volume, when it has one and its page is up.
    private MusicAppVolume? MusicAppLevel()
    {
        if (_closingWindow) return null;
        if (_musicAppVolume is { } current)
        {
            if (current.Service == _musicWebService) return current;
            _musicAppVolume = null;
            current.Dispose();
        }
        var link = _musicWebService switch
        {
            "spotify" when _musicApp is { Key: "spotify", IsDisposed: false } host => MusicAppVolume.ForSpotify(() => host.IsFrozen),
            "youtube" or "apple" when _musicWebReady && _musicWeb?.CoreWebView2 is { } core =>
                MusicAppVolume.ForWebPage(_musicWebService, MusicWebEvaluate(core), paused: () => MusicWebSuspended(core)),
            _ => null,
        };
        if (link is null) return null;
        _musicAppVolume = link;
        link.Changed += changed => DispatcherQueue.TryEnqueue(() =>
        {
            if (!ReferenceEquals(_musicAppVolume, link)) return;
            // The page answers again: a level chosen while it could not lands now.
            var wanted = _musicVolumeWanted;
            _musicVolumeWanted = null;
            if (wanted is { } level) _ = MusicVolumeAsync(level, null);
            else PublishMusicVolume(changed);
        });
        link.Watch();
        return link;
    }

    /// Runtime.evaluate in Exo's own music web document (YouTube Music, Apple
    /// Music), through its DevTools protocol: no host object or web message
    /// ever reaches the embedded profile. UI thread only. <paramref name="click"/>
    /// marks the call as the user's own click: only for what the user just
    /// pressed in Exo (play, transport), which Apple's page, like any page,
    /// only starts from a click.
    private MusicAppVolume.Evaluate MusicWebEvaluate(CoreWebView2 core, bool click = false) => async (expression, timeout, cancellation) =>
    {
        if (!ReferenceEquals(_musicWeb?.CoreWebView2, core)) return null;
        var request = System.Text.Json.JsonSerializer.Serialize(new { expression, awaitPromise = true, returnByValue = true, userGesture = click });
        var reply = await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", request).AsTask().WaitAsync(timeout, cancellation);
        using var json = System.Text.Json.JsonDocument.Parse(reply);
        if (json.RootElement.TryGetProperty("exceptionDetails", out _) ||
            !json.RootElement.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("value", out var value))
            return null;
        return value.Clone();
    };

    private static bool MusicWebSuspended(CoreWebView2 core)
    {
        try { return core.IsSuspended; }
        catch { return true; }
    }

    private void DropMusicAppVolume(bool desktop)
    {
        if (_musicAppVolume is not { } link || IsDesktopMusic(link.Service) != desktop) return;
        _musicAppVolume = null;
        link.Dispose();
    }

    /// Exo used to set the Windows session volume, and left below 100% it
    /// would quietly scale the app's own slider. The first time the app's own
    /// volume answers while an audio session exists, the session goes back to
    /// 100% and its level is folded into the app's, so loudness does not jump.
    private async Task<double> FoldSessionVolumeAsync(MusicAppVolume app, double level)
    {
        if (app.SessionFolded) return level;
        var pids = MusicVolumePids();
        var names = MusicVolumeProcessNames();
        if (pids.Count == 0 && names.Count == 0) return level;
        // Only a playing session is the app's: an idle one (another WebView in
        // Exo's environment, a session left from an earlier run) never decides
        // the fold. It is tried again when the song starts.
        if (await AppVolume.ApplyAsync(pids, null, null, names) is not { Active: true } session) return level;
        app.SessionFolded = true;
        if (session.Level >= 0.995 && !session.Muted) return level;
        var folded = session.Muted ? 0 : Math.Round(level * session.Level, 3);
        await AppVolume.ApplyAsync(pids, 1.0, false, names);
        AppLog.Info($"Music volume: {app.Service} session volume reset to 100%; its own volume {level:0.00} -> {folded:0.00}.");
        return await app.WriteAsync(folded) ?? folded;
    }

    private void PublishMusicVolume(double level)
    {
        if (_closingWindow) return;
        try
        {
            var core = WebHost.CoreWebView2;
            if (core is null || !WebViewTrustPolicy.IsTrustedAppUri(core.Source)) return;
            core.PostWebMessageAsJson(System.Text.Json.JsonSerializer.Serialize(new
            {
                @event = "music.volume",
                data = new { level, source = "app" },
            }));
        }
        catch { }
    }

    private HashSet<string> MusicVolumeProcessNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (IsDesktopMusic(_musicWebService) && DesktopClients.For(_musicWebService) is { } spec)
            foreach (var name in spec.ProcessNames) names.Add(name);
        return names;
    }

    /// Playback started: a level chosen before the session existed lands now,
    /// and the app-level link folds a session volume that only now exists.
    private void ApplyPendingMusicVolume()
    {
        if (_musicVolumeWanted is { } wanted) _ = MusicVolumeAsync(wanted, null);
        else if (_musicAppVolume is { SessionFolded: false } link) _ = FoldOncePlayingAsync(link);
    }

    /// The audio session turns active a moment after the app reports playing.
    private async Task FoldOncePlayingAsync(MusicAppVolume link)
    {
        await Task.Delay(1500);
        if (ReferenceEquals(_musicAppVolume, link) && !link.SessionFolded) await MusicVolumeAsync(null, null);
    }

    private HashSet<int> MusicVolumePids()
    {
        var pids = new HashSet<int>();
        if (IsDesktopMusic(_musicWebService))
        {
            if (_musicApp is { } app)
                foreach (var identity in app.ResourceGroup.Processes) pids.Add(identity.Pid);
        }
        else if (_musicWeb?.CoreWebView2 is { } core)
        {
            try
            {
                foreach (var process in core.Environment.GetProcessInfos()) pids.Add(process.ProcessId);
            }
            catch { }
        }
        return pids;
    }

    /// positionAgeMs is measured on the host's monotonic clock at send time;
    /// the shell anchors it to its own clock on receipt, so wall-clock skew or
    /// a clock change never moves the time.
    private static object? PlaybackDto(MediaSessionService.Snapshot? snapshot) => snapshot is null ? null : new
    {
        seq = snapshot.Seq,
        provider = snapshot.Provider,
        status = snapshot.Status,
        title = snapshot.Title,
        artist = snapshot.Artist,
        artworkDataUrl = snapshot.ArtworkDataUrl,
        positionMs = snapshot.PositionMs,
        durationMs = snapshot.DurationMs,
        positionAgeMs = Math.Max(0, Environment.TickCount64 - snapshot.CapturedAt),
        rate = snapshot.Rate,
        canPlay = snapshot.CanPlay,
        canPause = snapshot.CanPause,
        canPrev = snapshot.CanPrev,
        canNext = snapshot.CanNext,
        canSeek = snapshot.CanSeek,
    };

    private void PublishMusicPlayback(MediaSessionService.Snapshot snapshot)
    {
        if (_closingWindow) return;
        try
        {
            var core = WebHost.CoreWebView2;
            if (core is null || !WebViewTrustPolicy.IsTrustedAppUri(core.Source)) return;
            core.PostWebMessageAsJson(System.Text.Json.JsonSerializer.Serialize(new
            {
                @event = "music.playback",
                data = PlaybackDto(snapshot),
            }));
        }
        catch { }
    }

    private CancellationTokenSource? _musicIdleCancellation;

    private void WakeMusicWeb()
    {
        _musicIdleCancellation?.Cancel();
        _musicIdleCancellation = null;
        try
        {
            if (_musicWeb is not { } view || view.CoreWebView2 is not { } core) return;
            view.Visibility = Visibility.Visible;
            if (core.IsSuspended) core.Resume();
            SetWebViewMemoryTarget(core, CoreWebView2MemoryUsageTargetLevel.Normal);
        }
        catch { /* Navigation/recreation reports its own recovery state. */ }
    }

    /// <summary>
    /// The pointer reached the mini player or the Music tab: thaw an idle,
    /// frozen music app now, so the click that follows lands on a running app
    /// instead of waiting for the thaw. Cheap when nothing is frozen.
    /// </summary>
    internal object WarmMusic()
    {
        _musicApp?.Wake("warm");
        if (MusicWebPolicy.IsWeb(_musicWebService)) WakeMusicWeb();
        return new { ok = true };
    }

    internal object ReloadMusic()
    {
        if (_musicWebFailed)
        {
            ResetMusicWebView();
            return new { ok = false, service = _musicWebService, message = "Reopening the player." };
        }
        if (_musicWeb?.CoreWebView2 is not { } core)
            return new { ok = false, service = _musicWebService, message = "Open a music service first." };
        _musicWebReady = false;
        PublishMusicStatus(_musicWebService!, "loading");
        core.Reload();
        return new { ok = true, ready = false, service = _musicWebService };
    }

    private void ResetMusicWebView()
    {
        DropMusicAppVolume(desktop: false);
        if (MusicWebPolicy.IsWeb(_musicNowPlaying?.Service)) SetMusicNowPlaying(null);
        CancelConnectedPermissions(_musicWeb);
        _musicIdleCancellation?.Cancel();
        _musicIdleCancellation = null;
        _musicWebGeneration++;
        foreach (var popup in _musicPopouts.Values.ToArray())
            try { popup.Close(); } catch { }
        _musicPopouts.Clear();
        var previous = _musicWeb;
        // Invalidate callbacks before closing the old view.
        _musicWeb = null;
        _musicWebInit = null;
        _musicWebLoad?.TrySetCanceled();
        _musicWebLoad = null;
        _musicWebReady = false;
        _musicWebFailed = false;
        Volatile.Write(ref _musicWebAudio, false);
        _embeddedRenderers = Array.Empty<EmbeddedRendererInfo>();
        ApplyWebViewMemoryTarget(WindowIsHiddenOrMinimized());
        if (previous is null) return;
        try { WebExtensions.Detach(previous.CoreWebView2); previous.Close(); } catch { }
        ContentHost.Children.Remove(previous);
    }

    /// Returns the music sessions still closing: a retire already in flight
    /// (it owns the previous tree's polite close) and the current app. The
    /// window waits for them together with the add-ons, once.
    private List<Task> CloseMusicWeb()
    {
        _musicWebClosed = true;
        _musicWebWanted = false;
        ResetMusicWebView();
        DropMusicAppVolume(desktop: true);
        DropSpotifyBridge();
        var closing = new List<Task>();
        var pending = _pendingMusicExit;
        _pendingMusicExit = null;
        if (pending is not null) closing.Add(pending.WaitAsync());
        if (_musicApp is { } music) closing.Add(music.ShutdownAsync(HostCloseGrace));
        _musicNowPlaying?.Dispose();
        _musicNowPlaying = null;
        _mediaSessions?.Dispose();
        return closing;
    }

    private void ApplyMusicMemoryTarget(bool hiddenOrMinimized)
    {
        try
        {
            if (_musicWeb?.CoreWebView2 is not { } core) return;
            var active = _musicWebWanted && !hiddenOrMinimized;
            // A hidden player still needs its decoder and next-track buffers.
            // Only a confirmed idle, suspended document may release that memory.
            SetWebViewMemoryTarget(core, core.IsSuspended ? CoreWebView2MemoryUsageTargetLevel.Low : CoreWebView2MemoryUsageTargetLevel.Normal);
            SetEmbeddedActivity(core, active);
            if (_musicWebWanted && !hiddenOrMinimized)
            {
                WakeMusicWeb();
                return;
            }
            // Paused or empty players may sleep after a final idle wait.
            if (core.IsDocumentPlayingAudio || core.IsSuspended || _musicIdleCancellation is not null) return;
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
            _musicIdleCancellation = cancellation;
            _ = SuspendIdleMusicAsync(_musicWeb, cancellation);
        }
        catch { }
    }

    private async Task SuspendIdleMusicAsync(WebView2 view, CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(60), cancellation.Token);
            // A session the provider still reports as playing (buffering, a
            // silent stretch) is not idle even while no audio is audible.
            if (cancellation.IsCancellationRequested || !ReferenceEquals(view, _musicWeb) ||
                _musicWebWanted && !WindowIsHiddenOrMinimized() ||
                view.CoreWebView2 is not { } core || core.IsDocumentPlayingAudio ||
                MediaSessions.Current is { Status: "playing" } now && now.Provider == _musicWebService) return;
            // A player that has been silent and hidden for a minute is paused;
            // let the native host own idle scheduling independently. Suspend
            // is only valid on a hidden controller, so the view collapses first.
            view.Visibility = Visibility.Collapsed;
            var suspended = false;
            if (!core.IsSuspended && ReferenceEquals(view, _musicWeb))
            {
                // The controller hears it is hidden on a later layout turn, so a suspend in this
                // one is refused: wait one low-priority turn, and once more if it still refuses.
                string? failure = null;
                for (var attempt = 0; attempt < 2 && !suspended && !core.IsSuspended && !cancellation.IsCancellationRequested; attempt++)
                {
                    await NextLowPriorityTurnAsync();
                    try { suspended = await core.TrySuspendAsync(); failure = null; }
                    catch (Exception error) { failure = error.GetType().Name; }
                }
                if (failure is not null) LogMusicSuspendFailure(failure);
                else if (!suspended && !core.IsSuspended) LogMusicSuspendFailure("declined");
            }
            if (!suspended || cancellation.IsCancellationRequested ||
                _musicWebWanted && !WindowIsHiddenOrMinimized() || core.IsDocumentPlayingAudio)
            {
                view.Visibility = Visibility.Visible;
                if (core.IsSuspended) core.Resume();
            }
            SetWebViewMemoryTarget(core, core.IsSuspended ? CoreWebView2MemoryUsageTargetLevel.Low : CoreWebView2MemoryUsageTargetLevel.Normal);
            if (!core.IsSuspended) return;
            // Still asleep after a long while: close the player and its renderer.
            // Any wake (room, mini player, transport) cancels this; Music loads it again.
            await Task.Delay(MusicReleaseDelay, cancellation.Token);
            if (!ReferenceEquals(view, _musicWeb) || !core.IsSuspended || _musicWebWanted && !WindowIsHiddenOrMinimized() ||
                _musicPopouts.Count > 0 || _musicSignInWindow is not null) return;
            AppLog.Info(MusicWebPolicy.Label(_musicWebService ?? "") + " web player released after " + MusicReleaseDelay.TotalMinutes + " idle minutes.");
            ResetMusicWebView();
        }
        catch { if (ReferenceEquals(view, _musicWeb)) view.Visibility = Visibility.Visible; }
        finally
        {
            if (ReferenceEquals(_musicIdleCancellation, cancellation)) _musicIdleCancellation = null;
            cancellation.Dispose();
        }
    }

    /// How long an idle music player stays asleep before Exo closes it: the web
    /// player's WebView, or a desktop app Exo started (never one the user had open).
    private static readonly TimeSpan MusicReleaseDelay = TimeSpan.FromMinutes(15);

    /// From the trim loop: a desktop music app frozen past <see cref="MusicReleaseDelay"/>
    /// closes; opening Music starts it again.
    private void ReleaseIdleMusicApp()
    {
        if (_musicApp is not { } app || !app.FrozenLongerThan(MusicReleaseDelay)) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_closingWindow || !ReferenceEquals(_musicApp, app) || !app.FrozenLongerThan(MusicReleaseDelay)) return;
            AppLog.Info(MusicWebPolicy.Label(app.Key) + " closed after " + MusicReleaseDelay.TotalMinutes + " idle minutes.");
            _pendingMusicExit = RetireMusicApp();
        });
    }

    private bool _musicSuspendFailureLogged;

    /// Resumes after the dispatcher has run everything more urgent (layout included).
    private Task NextLowPriorityTurnAsync()
    {
        var turn = new TaskCompletionSource();
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => turn.TrySetResult())) turn.TrySetResult();
        return turn.Task;
    }

    private void LogMusicSuspendFailure(string reason)
    {
        if (_musicSuspendFailureLogged) return;
        _musicSuspendFailureLogged = true;
        AppLog.Warn("Music web player could not suspend while hidden (" + reason + "); it stays loaded.");
    }

    private void PublishMusicStatus(string service, string state, string? message = null)
    {
        if (_musicWebClosed || _musicWebService != service) return;
        try
        {
            var core = WebHost.CoreWebView2;
            if (core is null || !WebViewTrustPolicy.IsTrustedAppUri(core.Source)) return;
            core.PostWebMessageAsJson(System.Text.Json.JsonSerializer.Serialize(new
            {
                @event = "music.status", data = new { ok = state != "error", ready = state == "running", service, state, message },
            }));
        }
        catch { }
    }

    private async Task InitializeMusicWebAsync(WebView2 view, string service)
    {
        var initTimer = Stopwatch.StartNew();
        var environment = await WebViewEnvironmentFactory.GetAsync();
        var envMs = initTimer.ElapsedMilliseconds;
        if (_musicWebClosed || !ReferenceEquals(_musicWeb, view)) return;
        await view.EnsureCoreWebView2Async(environment, WebViewEnvironmentFactory.EmbeddedControllerOptions(environment));
        var coreMs = initTimer.ElapsedMilliseconds;
        if (!_musicWebClosed && ReferenceEquals(_musicWeb, view))
            WebViewEnvironmentFactory.LogEmbeddedProfileOnce(view.CoreWebView2);
        if (_musicWebClosed || !ReferenceEquals(_musicWeb, view)) return;
        EmbeddedClientFrame.Apply(view);
        var core = view.CoreWebView2;
        // Installed extensions apply to the profile regardless of bookkeeping;
        // attach in parallel so the first navigation is never blocked by it.
        _ = WebExtensions.AttachAsync(core, MusicWebPolicy.Label(service), WinRT.Interop.WindowNative.GetWindowHandle(this));
        await ResetPermissionGrantsAsync(core.Profile);
        var permissionsMs = initTimer.ElapsedMilliseconds;
        if (_musicWebClosed || !ReferenceEquals(_musicWeb, view)) return;
        core.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Dark;
        await SetMusicMotionPreferenceAsync(core);
        if (_musicWebClosed || !ReferenceEquals(_musicWeb, view)) return;
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _musicWebLoad = loaded;
        ConfigureMusicCore(core, environment, view, service);
        ApplyWebViewMemoryTarget(WindowIsHiddenOrMinimized());
        Volatile.Write(ref _musicWebAudio, core.IsDocumentPlayingAudio);
        core.IsDocumentPlayingAudioChanged += (_, _) =>
        {
            if (!ReferenceEquals(_musicWeb, view) || _musicWebClosed) return;
            Volatile.Write(ref _musicWebAudio, core.IsDocumentPlayingAudio);
            ApplyMusicMemoryTarget(WindowIsHiddenOrMinimized());
        };
        core.NavigationCompleted += (_, e) =>
        {
            if (!ReferenceEquals(_musicWeb, view) || _musicWebClosed) return;
            if (e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled) return;
            if (!e.IsSuccess)
            {
                _musicWebReady = false;
                _musicWebFailed = true;
                view.Visibility = Visibility.Collapsed;
                ApplyWebViewMemoryTarget(WindowIsHiddenOrMinimized());
                var message = MusicWebPolicy.Label(service) + " could not load. Check your connection and retry.";
                PublishMusicStatus(service, "error", message);
                loaded.TrySetException(new InvalidOperationException(message));
                return;
            }
            _musicWebReady = true;
            _musicWebFailed = false;
            SetEmbeddedActivity(core, _musicWebWanted && !WindowIsHiddenOrMinimized(), force: true);
            // The reader outlives reloads: its page helper is reinstalled in
            // each new document on the next call.
            if (_musicNowPlaying?.Service != service)
                SetMusicNowPlaying(MusicNowPlaying.ForWebPage(service, MusicWebEvaluate(core), () => MusicWebSuspended(core),
                    control: MusicWebEvaluate(core, click: true)));
            loaded.TrySetResult();
            // First reveal: a preloaded (background) player stays rendered but
            // transparent until its room opens.
            view.Visibility = Visibility.Visible;
            EmbeddedClientFrame.SetInputVisible(view, _musicWebWanted);
            ApplyWebViewMemoryTarget(WindowIsHiddenOrMinimized());
            PublishMusicStatus(service, "running");
            RefreshEmbeddedRendererMapAsync().ContinueWith(static task => task.Exception?.Handle(static _ => true),
                TaskContinuationOptions.OnlyOnFaulted);
        };
        core.ProcessFailed += (_, e) =>
        {
            if (!ReferenceEquals(_musicWeb, view) || _musicWebClosed) return;
            if (e.ProcessFailedKind is not (CoreWebView2ProcessFailedKind.BrowserProcessExited or
                CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive))
                return;
            _musicWebFailed = true;
            _musicWebReady = false;
            view.Visibility = Visibility.Collapsed;
            ApplyWebViewMemoryTarget(WindowIsHiddenOrMinimized());
            PublishMusicStatus(service, "error", "The player stopped responding. Choose Retry to reconnect.");
            loaded.TrySetException(new InvalidOperationException("Music web process failed."));
            AppLog.Warn("Music web player process stopped; reload is available.");
        };
        await core.AddScriptToExecuteOnDocumentCreatedAsync(EmbeddedClientPresentation.Script);
        if (service == "youtube")
            await core.AddScriptToExecuteOnDocumentCreatedAsync(EmbeddedClientPresentation.OptimizeScript);
        var domLogged = false;
        core.DOMContentLoaded += (_, _) =>
        {
            if (domLogged || _musicWebClosed || !ReferenceEquals(_musicWeb, view)) return;
            domLogged = true;
            AppLog.Info($"PERF music-init service={service} domContentLoadedMs={initTimer.ElapsedMilliseconds}");
        };
        // The provider owns its document and sign-in colors. Forcing CSS
        // color-scheme into an embedded account form can hide typed text.
        core.Navigate(MusicWebPolicy.StartUri(service));
        AppLog.Info($"PERF music-init service={service} envMs={envMs} coreMs={coreMs} permissionsMs={permissionsMs} navigateMs={initTimer.ElapsedMilliseconds}");
        await loaded.Task.WaitAsync(TimeSpan.FromSeconds(30), _lifetimeCts.Token);
        AppLog.Info(MusicWebPolicy.Label(service) + " official web player started on the embedded profile.");
    }

    private void ConfigureMusicCore(CoreWebView2 core, CoreWebView2Environment environment,
        WebView2 surface, string service, bool popup = false)
    {
        var generation = _musicWebGeneration;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsWebMessageEnabled = false;
        core.PermissionRequested += (_, args) => OnConnectedPermissionRequested(args, surface, popup, service);
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = true;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsPinchZoomEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        ConfigureConnectedChrome(core, surface, MusicWebPolicy.Label(service), uri => MusicWebPolicy.IsPlayerDocument(service, uri));
        core.NavigationStarting += (sender, e) =>
        {
            if (MusicWebPolicy.IsServiceDocument(service, e.Uri) || (popup && e.Uri == "about:blank")) return;
            e.Cancel = true;
            if (popup) {
                // A popup that leaves its allowed hosts has finished its job:
                // hand the destination to the system browser and close it so
                // the user never lands on a blank window. Only a click opens the browser,
                // so a page's own redirect cannot launch one.
                if (e.IsUserInitiated && MusicWebPolicy.IsHttps(e.Uri)) OpenMusicExternal(e.Uri);
                if (_musicPopouts.TryGetValue(surface, out var popupWindow)) {
                    try { popupWindow.Close(); } catch { }
                }
                return;
            }
            if (e.IsUserInitiated && MusicWebPolicy.IsHttps(e.Uri)) OpenMusicExternal(e.Uri);
        };
        core.NewWindowRequested += async (sender, e) =>
        {
            e.Handled = true;
            if (_musicWebClosed || _musicWebService != service || generation != _musicWebGeneration) return;
            var authContinuation = MusicWebPolicy.IsServiceDocument(service, core.Source) &&
                !MusicWebPolicy.IsPlayerDocument(service, core.Source) &&
                (MusicWebPolicy.IsServiceDocument(service, e.Uri) || e.Uri == "about:blank");
            if (!e.IsUserInitiated && !authContinuation && !(MusicSignInRequested() && MusicWebPolicy.IsServiceDocument(service, e.Uri))) return;
            if (!MusicWebPolicy.IsServiceDocument(service, e.Uri) && e.Uri != "about:blank")
            {
                if (MusicWebPolicy.IsHttps(e.Uri)) OpenMusicExternal(e.Uri);
                return;
            }
            var deferral = e.GetDeferral();
            var popupTimer = Stopwatch.StartNew();
            Window? child = null;
            var popupDismissed = false;
            try
            {
                var view = new WebView2 { DefaultBackgroundColor = Windows.UI.Color.FromArgb(255, 0, 0, 0) };
                child = CreateExoWebPopup(MusicWebPolicy.Label(service), view);
                var window = child;
                _musicPopouts[view] = window;
                ApplyWebViewMemoryTarget(WindowIsHiddenOrMinimized());
                window.Closed += (_, _) =>
                {
                    CancelConnectedPermissions(view);
                    popupDismissed = true;
                    _musicPopouts.Remove(view);
                    ApplyWebViewMemoryTarget(WindowIsHiddenOrMinimized());
                    try { WebExtensions.Detach(view.CoreWebView2); view.Close(); } catch { }
                };
                window.AppWindow.Resize(new SizeInt32(960, 720));
                window.Activate();
                await view.EnsureCoreWebView2Async(environment, WebViewEnvironmentFactory.EmbeddedControllerOptions(environment));
                var ensureMs = popupTimer.ElapsedMilliseconds;
                if (_musicWebClosed || _musicWebService != service || generation != _musicWebGeneration) { window.Close(); return; }
                _ = WebExtensions.AttachAsync(view.CoreWebView2, MusicWebPolicy.Label(service), WinRT.Interop.WindowNative.GetWindowHandle(window));
                if (popupDismissed || _musicWebClosed || _musicWebService != service || generation != _musicWebGeneration) { window.Close(); return; }
                await SetMusicMotionPreferenceAsync(view.CoreWebView2);
                if (popupDismissed || _musicWebClosed || _musicWebService != service || generation != _musicWebGeneration) { window.Close(); return; }
                ConfigureMusicCore(view.CoreWebView2, environment, view, service, popup: true);
                await view.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(EmbeddedClientPresentation.Script);
                void UpdatePopupActivity(bool force = false)
                {
                    var minimized = window.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter &&
                        presenter.State == Microsoft.UI.Windowing.OverlappedPresenterState.Minimized;
                    SetEmbeddedActivity(view.CoreWebView2, !minimized, force);
                    SetWebViewMemoryTarget(view.CoreWebView2, CoreWebView2MemoryUsageTargetLevel.Normal);
                }
                window.AppWindow.Changed += (_, _) => { try { UpdatePopupActivity(); } catch { } };
                view.CoreWebView2.NavigationCompleted += (_, _) => { try { UpdatePopupActivity(force: true); } catch { } };

                view.CoreWebView2.WindowCloseRequested += (_, _) => window.Close();
                var popupDomLogged = false;
                view.CoreWebView2.DOMContentLoaded += (_, _) =>
                {
                    if (popupDomLogged) return;
                    popupDomLogged = true;
                    AppLog.Info($"PERF music-popup service={service} domContentLoadedMs={popupTimer.ElapsedMilliseconds}");
                };
                e.NewWindow = view.CoreWebView2;
                AppLog.Info($"PERF music-popup service={service} ensureMs={ensureMs} readyMs={popupTimer.ElapsedMilliseconds}");
            }
            catch
            {
                var dismissed = popupDismissed;
                try { child?.Close(); } catch { }
                if (!dismissed && generation == _musicWebGeneration)
                    PublishMusicStatus(service, "error", "The player's window could not open. Try opening it in your browser.");
            }
            finally { try { deferral.Complete(); } catch { } }
        };
        // Provider permissions and DRM prompts keep WebView2's ordinary user
        // decision flow. No auto-grants, license interception or request blocking.
    }

    private async Task SetMusicMotionPreferenceAsync(CoreWebView2 core)
    {
        try
        {
            // Use the provider's reduced-motion path, including shadow DOM and
            // script-driven effects. This does not enable a debugging endpoint.
            await core.CallDevToolsProtocolMethodAsync("Emulation.setEmulatedMedia",
                "{\"features\":[{\"name\":\"prefers-reduced-motion\",\"value\":\"reduce\"}]}")
                .AsTask().WaitAsync(TimeSpan.FromSeconds(2), _lifetimeCts.Token);
        }
        catch { /* Motion support must not prevent the player from opening. */ }
    }

    private static void OpenMusicExternal(string uri)
    {
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch { AppLog.Warn("Music external link could not open."); }
    }

    private DesktopClientHost CreateDesktopHost(DesktopClientSpec spec, string statusEvent)
    {
        var host = new DesktopClientHost(spec, WinRT.Interop.WindowNative.GetWindowHandle(this), DispatcherQueue);
        host.StatusChanged += (state, message) =>
            DispatcherQueue.TryEnqueue(() => PublishDesktopClientStatus(statusEvent, spec.Key, state, message));
        return host;
    }

    /// Disk presence for every hosted desktop app, plus live state when Exo
    /// already has a host. Does not start anything.
    internal object DesktopClientsStatus()
    {
        return new
        {
            ok = true,
            apps = DesktopClients.Hosted.Select(spec =>
            {
                var live = _musicApp is { } music && music.Key == spec.Key ? music : null;
                var state = live is not null ? ClientStateName(live.State)
                    : DesktopClients.Present(spec) ? "installed" : "notInstalled";
                if (state == "notInstalled" && DesktopClients.OnlyStoreEdition(spec)) state = "storeEdition";
                return new { id = spec.Key, title = spec.Title, state };
            }).ToArray(),
        };
    }

    /// Install without requiring the app to be the selected Music service.
    /// Extra players stay on disk; the current music host may launch if it
    /// already owns a session.
    internal async Task<object> InstallDesktopClientAsync(string? key)
    {
        if (DesktopClients.For(key) is not { } spec)
            return new { ok = false, message = "That app cannot be installed from Exo." };
        if (_musicApp is { } music && music.Key == spec.Key)
            return (await music.InstallAsync()).ToWire(spec.Title);
        var installer = CreateDesktopHost(spec, "music.status");
        try { return (await installer.InstallAsync(launch: false)).ToWire(spec.Title); }
        finally { installer.Dispose(); }
    }

    private static string ClientStateName(DesktopClientHost.ClientState state) => state switch
    {
        DesktopClientHost.ClientState.Loading => "loading",
        DesktopClientHost.ClientState.Installing => "installing",
        DesktopClientHost.ClientState.NotInstalled => "notInstalled",
        DesktopClientHost.ClientState.Installed => "installed",
        DesktopClientHost.ClientState.Running => "running",
        DesktopClientHost.ClientState.Closed => "closed",
        _ => "error",
    };

    private void PublishDesktopClientStatus(string statusEvent, string service, DesktopClientHost.ClientState state, string? message)
    {
        if (_closingWindow) return;
        try
        {
            var core = WebHost.CoreWebView2;
            if (core is null || !WebViewTrustPolicy.IsTrustedAppUri(core.Source)) return;
            core.PostWebMessageAsJson(System.Text.Json.JsonSerializer.Serialize(new
            {
                @event = statusEvent,
                data = new { ok = state == DesktopClientHost.ClientState.Running, state = ClientStateName(state), service, message },
            }));
        }
        catch { }
    }
}
