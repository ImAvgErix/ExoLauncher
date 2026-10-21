using System.Globalization;
using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// The song, position and transport as the music app's own page knows them:
/// YouTube Music's player (Exo's own WebView2, through its DevTools protocol;
/// never a host object or web message) and Spotify's PlayerAPI (its loopback
/// DevTools session, <see cref="Helpers.SpotifyPage"/>). An event-driven
/// long-poll wakes on the app's own play, pause, seek, rate, track and
/// metadata events, so changes arrive at once and nothing is polled from the
/// host. <see cref="MediaSessionService"/> turns it into the mini player's
/// snapshot.
///
/// How it can fail, and what it does then:
/// - Page not loaded, navigating or reloading: the call answers nothing; the
///   last state is kept and the call is retried every second. The page helper
///   is per document, so a reload installs it again.
/// - Provider frozen while idle (Spotify's tree suspended, YouTube Music's
///   view suspended): no call is made; the last state is kept. Both freezes
///   only follow silence, so the track shows as paused.
/// - Player or metadata missing: YouTube Music falls back from Media Session
///   metadata to the player's own video data and the player bar; Spotify
///   answers "no player" and the Windows media session stands in.
/// - Ads (YouTube Music): the player bar's song is shown with the clock
///   stopped and seeking off; the ad's own video time is not the song's.
/// - Track change mid-poll: each answer is one synchronous read in the page;
///   metadata that still names the previous song is not used, and the
///   artwork URL travels in the same answer as the title.
/// - Seek, stall or a skipped event: the page compares the position with its
///   own prediction every second and answers when they differ by 0.75 s.
/// - DevTools socket drop (Spotify restarts or quits): the call fails, the
///   socket reconnects lazily, and after 10 s without an answer the Windows
///   media session stands in.
/// </summary>
internal sealed class MusicNowPlaying : IDisposable
{
    internal sealed record Track(
        string Id, string Title, string Artist, string? ArtworkUrl,
        long PositionMs, long? DurationMs, bool Playing, double Rate,
        bool CanPrev, bool CanNext, bool CanSeek,
        /// <summary><see cref="Environment.TickCount64"/> when the page read the position.</summary>
        long CapturedAt);

    /// <param name="Player">The page answered and its player exists.</param>
    /// <param name="Track">The loaded song; null when the player has none.</param>
    /// <param name="FailingSince">Tick of the first unanswered call since the last answer; 0 while answering.</param>
    internal sealed record State(bool Player, Track? Track, long FailingSince);

    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(3);
    // A hidden, silent page may have its timers throttled to one a minute,
    // which can stretch a wait well past its window; only a call this late
    // counts as unanswered. Navigation, a crash or a dropped socket fail at once.
    private static readonly TimeSpan WaitGrace = TimeSpan.FromSeconds(70);
    // A playing song is re-anchored at least every 5 s; a paused one is only
    // re-read on the app's own events or every 20 s.
    private const int PlayingWindowMs = 5000;
    private const int IdleWindowMs = 20000;

    private readonly MusicAppVolume.Evaluate _evaluate;
    private readonly MusicAppVolume.Evaluate _control;
    private readonly string _page;
    private readonly Func<bool> _frozen;
    private readonly Action? _dispose;
    private readonly CancellationTokenSource _life = new();
    private State _state = new(false, null, Environment.TickCount64);
    private string? _lastKey;
    private bool _watching;
    private bool _frozenNow;
    private bool _answered;

    internal string Service { get; }
    internal State Current => Volatile.Read(ref _state);
    /// The provider is frozen or suspended while idle; nothing is asked of it.
    /// Read on the watch loop's context (YouTube Music's view answers only on
    /// the UI thread) and cached for other threads.
    internal bool Frozen => Volatile.Read(ref _frozenNow);
    /// Raised on the watch loop's context after every answer, failure or freeze change.
    internal event Action? Changed;

    private MusicNowPlaying(string service, MusicAppVolume.Evaluate evaluate, string adapter, Func<bool> frozen, Action? dispose = null,
        MusicAppVolume.Evaluate? control = null)
    {
        Service = service;
        _evaluate = evaluate;
        _control = control ?? evaluate;
        _page = Wrapper.Replace("__ADAPTER__", adapter, StringComparison.Ordinal);
        _frozen = frozen;
        _dispose = dispose;
    }

    internal static MusicNowPlaying ForSpotify(Func<bool> frozen)
    {
        var page = new Helpers.SpotifyPage();
        return new MusicNowPlaying("spotify", page.EvaluateAsync, SpotifyAdapter, frozen, page.Dispose);
    }

    /// YouTube Music or Apple Music in Exo's own WebView2. <paramref name="control"/>
    /// runs transport (a click the user just made in Exo).
    internal static MusicNowPlaying ForWebPage(string service, MusicAppVolume.Evaluate evaluate, Func<bool> frozen,
        MusicAppVolume.Evaluate? control = null) =>
        new(service, evaluate, service == "apple" ? AppleMusicAdapter : YouTubeMusicAdapter, frozen, control: control);

    /// Starts the long-poll on the caller's context (the UI thread for
    /// YouTube Music, whose DevTools calls must be made there).
    internal void Watch()
    {
        if (_watching) return;
        _watching = true;
        _ = WatchAsync();
    }

    private async Task WatchAsync()
    {
        var token = _life.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                bool frozen;
                try { frozen = _frozen(); } catch { frozen = false; }
                if (frozen != _frozenNow) { Volatile.Write(ref _frozenNow, frozen); Raise(); }
                if (frozen) { await Task.Delay(2000, token); continue; }
                var window = Current.Track is { Playing: true } ? PlayingWindowMs : IdleWindowMs;
                var call = "wait(" + JsonSerializer.Serialize(_lastKey) + "," + window.ToString(CultureInfo.InvariantCulture) + ")";
                JsonElement? reply;
                try { reply = await _evaluate("(" + _page + ")." + call, TimeSpan.FromMilliseconds(window) + WaitGrace, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch { reply = null; }
                if (token.IsCancellationRequested) return;
                if (reply is { ValueKind: JsonValueKind.Object } answer) Accept(answer);
                else
                {
                    Fail();
                    await Task.Delay(1000, token);
                }
            }
            catch (OperationCanceledException) { return; }
            catch
            {
                Fail();
                try { await Task.Delay(3000, token); } catch (OperationCanceledException) { return; }
            }
        }
    }

    private void Fail()
    {
        var state = Current;
        if (state.FailingSince == 0) Volatile.Write(ref _state, state with { FailingSince = Environment.TickCount64 });
        Raise();
    }

    private void Accept(JsonElement answer)
    {
        var player = answer.TryGetProperty("api", out var api) && api.ValueKind == JsonValueKind.True;
        _lastKey = answer.TryGetProperty("k", out var key) && key.ValueKind == JsonValueKind.String ? key.GetString() : null;
        Track? track = null;
        if (player && answer.TryGetProperty("t", out var t) && t.ValueKind == JsonValueKind.Object)
        {
            var title = Text(t, "title", 300);
            var id = Text(t, "id", 200);
            var position = Number(t, "pos") ?? 0;
            var duration = Number(t, "dur") is { } length && length > 0 ? length : (double?)null;
            if (duration is { } end && position > end) position = end;
            var playing = Flag(t, "playing");
            if (title.Length > 0 || id.Length > 0)
                track = new Track(
                    Id: id.Length > 0 ? id : title,
                    Title: title,
                    Artist: Text(t, "artist", 300),
                    ArtworkUrl: Text(t, "art", 2048) is { Length: > 0 } art ? art : null,
                    PositionMs: (long)position,
                    DurationMs: duration is { } ms ? (long)ms : null,
                    Playing: playing,
                    Rate: playing ? Math.Clamp(Number(t, "rate") ?? 0, 0, 4) : 0,
                    CanPrev: Flag(t, "prev"),
                    CanNext: Flag(t, "next"),
                    CanSeek: Flag(t, "seek") && duration is not null,
                    CapturedAt: Environment.TickCount64);
        }
        var previous = Current;
        Volatile.Write(ref _state, new State(player, track, 0));
        if (!_answered || player != previous.Player)
            AppLog.Info("Now playing: " + Service + (player ? " player found in its page." : " page answers without its player."));
        _answered = true;
        Raise();
    }

    private void Raise()
    {
        try { Changed?.Invoke(); } catch { }
    }

    private static string Text(JsonElement value, string name, int max)
    {
        if (!value.TryGetProperty(name, out var text) || text.ValueKind != JsonValueKind.String) return "";
        var s = text.GetString() ?? "";
        return s.Length > max ? s[..max] : s;
    }

    private static double? Number(JsonElement value, string name) =>
        value.TryGetProperty(name, out var number) && number.ValueKind == JsonValueKind.Number &&
        number.GetDouble() is var n && double.IsFinite(n) && n >= 0 ? n : null;

    private static bool Flag(JsonElement value, string name) =>
        value.TryGetProperty(name, out var flag) && flag.ValueKind == JsonValueKind.True;

    /// Play, pause, toggle, next, previous or seek through the app's own
    /// controls. A page that is still thawing is retried for about three
    /// seconds; null means it never answered.
    internal async Task<bool?> ControlAsync(string action, double? positionMs)
    {
        var ms = positionMs is { } p && double.IsFinite(p) && p >= 0 ? p.ToString("0", CultureInfo.InvariantCulture) : "null";
        var call = "(" + _page + ").control(" + JsonSerializer.Serialize(action) + "," + ms + ")";
        for (var attempt = 0; attempt < 7 && !_life.IsCancellationRequested; attempt++)
        {
            if (attempt > 0)
            {
                try { await Task.Delay(500, _life.Token); } catch (OperationCanceledException) { return null; }
            }
            JsonElement? reply;
            try { reply = await _control(call, CallTimeout, _life.Token); }
            catch { reply = null; }
            if (reply is { ValueKind: JsonValueKind.True }) return true;
            if (reply is { ValueKind: JsonValueKind.False }) return false;
        }
        return null;
    }

    public void Dispose()
    {
        _life.Cancel();
        Changed = null;
        _dispose?.Invoke();
    }

    /// Installed once per document. wait(last, ms) answers { api, k, t } as
    /// soon as the track key differs from the caller's, the position drifts
    /// from the page's own prediction, or the window ends; the app's own
    /// events wake it, with a one-second in-page check behind them. A player
    /// that cannot be found is looked for again at most every five seconds.
    private const string Wrapper = """
        (function (adapter) {
          var w = window;
          if (w.__exoNow && w.__exoNow.v === 5) return w.__exoNow;
          var handle = null, heard = null, missAt = 0, waiters = [], base = null;
          function wake() { var list = waiters; waiters = []; list.forEach(function (f) { f(); }); }
          function locate() {
            try { if (handle && !adapter.alive(handle)) handle = null; } catch (_) { handle = null; }
            if (!handle && Date.now() - missAt > 5000) {
              try { handle = adapter.find(); } catch (_) { handle = null; }
              if (!handle) missAt = Date.now();
            }
            if (handle && heard !== handle) { heard = handle; try { adapter.listen(handle, wake); } catch (_) {} }
            return handle;
          }
          function num(v) { return typeof v === 'number' && isFinite(v) && v >= 0 ? v : null; }
          function str(v, n) { return typeof v === 'string' ? v.trim().slice(0, n) : ''; }
          function read() {
            var h = locate();
            if (!h) return { api: false, k: 'none', t: null };
            var t = null;
            try { t = adapter.read(h, wake); } catch (_) { t = null; }
            if (!t || !(t.title || t.id)) return { api: true, k: '', t: null };
            var dur = num(t.dur), pos = num(t.pos) || 0;
            if (dur !== null && pos > dur) pos = dur;
            t = { id: str(t.id, 200), title: str(t.title, 300), artist: str(t.artist, 300), art: str(t.art, 2048),
                  pos: pos, dur: dur, playing: !!t.playing, rate: t.playing ? num(t.rate) || 0 : 0,
                  prev: t.prev !== false, next: t.next !== false, seek: !!t.seek && dur !== null };
            var k = [t.id, t.title, t.artist, t.art, t.playing, t.rate, dur === null ? '' : Math.round(dur / 1000), t.prev, t.next, t.seek].join('\n');
            return { api: true, k: k, t: t };
          }
          function drifted(s) {
            if (!base || !s.t) return false;
            return Math.abs(s.t.pos - (base.pos + (performance.now() - base.at) * base.rate)) > 750;
          }
          function mark(s) { base = s.t ? { pos: s.t.pos, at: performance.now(), rate: s.t.rate } : null; return s; }
          async function wait(last, ms) {
            var end = Date.now() + ms;
            for (;;) {
              var s = read();
              var left = end - Date.now();
              if (s.k !== last || drifted(s) || left <= 0) return mark(s);
              // A wait that times out takes its waiter back, so the list never grows between events.
              await new Promise(function (resolve) {
                var woke = function () { clearTimeout(t); resolve(); };
                var t = setTimeout(function () { var i = waiters.indexOf(woke); if (i >= 0) waiters.splice(i, 1); resolve(); }, Math.min(left, 1000));
                waiters.push(woke);
              });
            }
          }
          async function control(action, ms) {
            var h = locate();
            if (!h) return false;
            try { return (await adapter.control(h, action, ms)) !== false; } catch (_) { return false; }
          }
          return (w.__exoNow = { v: 5, wait: wait, control: control });
        })(__ADAPTER__)
        """;

    /// YouTube Music: the #movie_player element, its &lt;video&gt;, the player
    /// bar and navigator.mediaSession. Metadata that does not name the
    /// player's current song is stale (it lags a track change) and is not
    /// used. Transport clicks the player bar's own buttons.
    private const string YouTubeMusicAdapter = """
        {
          alive: function (p) { return p.isConnected; },
          find: function () {
            var p = document.getElementById('movie_player');
            return p && typeof p.getPlayerState === 'function' ? p : null;
          },
          listen: function (p, wake) {
            try { p.addEventListener('onStateChange', wake); } catch (_) {}
          },
          read: function (p, wake) {
            var v = p.querySelector('video');
            if (v && !v.__exoNowHooked) {
              v.__exoNowHooked = true;
              ['play', 'pause', 'playing', 'waiting', 'seeked', 'ratechange', 'durationchange', 'loadedmetadata', 'emptied', 'ended']
                .forEach(function (e) { v.addEventListener(e, wake); });
            }
            var bar = document.querySelector('ytmusic-player-bar');
            function text(sel) { var e = bar && bar.querySelector(sel); return e ? (e.textContent || '').trim() : ''; }
            function enabled(sel) { var e = bar && bar.querySelector(sel); return !e || !(e.disabled || e.hasAttribute('disabled') || e.getAttribute('aria-disabled') === 'true'); }
            var data = {};
            try { data = (typeof p.getVideoData === 'function' && p.getVideoData()) || {}; } catch (_) {}
            var ad = p.classList.contains('ad-showing') || p.classList.contains('ad-interrupting');
            var barTitle = text('.title');
            var m = navigator.mediaSession && navigator.mediaSession.metadata;
            var title = ad ? barTitle : (data.title || barTitle);
            if (!title && m && m.title) title = m.title;
            if (!title && !data.video_id) return null;
            var fresh = !!(m && m.title) && (m.title === title || m.title === barTitle);
            var artist = fresh && m.artist ? m.artist : (text('.byline').split('•')[0] || '').trim() || data.author || '';
            var art = '';
            if (fresh && m.artwork && m.artwork.length) {
              var best = null, score = function (a) { var n = parseInt(String(a.sizes || '0'), 10) || 0; return n >= 256 ? 10000 - n : n; };
              for (var i = 0; i < m.artwork.length; i++) if (m.artwork[i] && m.artwork[i].src && (!best || score(m.artwork[i]) > score(best))) best = m.artwork[i];
              art = best ? String(best.src) : '';
            } else if (barTitle && barTitle === title) {
              var img = bar.querySelector('img.image');
              art = img && img.src && img.src.indexOf('https:') === 0 ? img.src : '';
            }
            var state = -1;
            try { state = p.getPlayerState(); } catch (_) {}
            var playing = v ? !v.paused && !v.ended : state === 1 || state === 3;
            // The song's own clock is the player's, not the <video>'s: YouTube
            // Music appends each queued song to the same media source, so the
            // element's currentTime and duration keep counting across tracks.
            // The player bar's slider is what YouTube Music itself shows.
            var slider = bar && bar.querySelector('#progress-bar');
            var shownMax = slider ? Number(slider.max || slider.getAttribute('aria-valuemax')) : NaN;
            var dur = isFinite(shownMax) && shownMax > 0 ? shownMax * 1000 : null;
            if (dur === null) { try { dur = p.getDuration() * 1000 || null; } catch (_) {} }
            if (dur === null && v && isFinite(v.duration) && v.duration > 0) dur = v.duration * 1000;
            var pos = 0;
            try { pos = p.getCurrentTime() * 1000; } catch (_) { pos = v ? v.currentTime * 1000 : 0; }
            if (!isFinite(pos) || pos < 0) pos = 0;
            // The player's own state says whether the song advances (1 playing,
            // 3 buffering). readyState alone lies here: with the video track not
            // drawn while the room is hidden it can sit below 3 while the audio
            // plays, so a position that really advanced also counts as moving.
            var now = performance.now(), last = v && v.__exoNowLast;
            var advanced = !!last && now - last.at > 250 && v.currentTime - last.t > (now - last.at) / 2000;
            if (v) v.__exoNowLast = { t: v.currentTime, at: now };
            var moving = playing && state !== 3 && (state === 1 || advanced || !v || v.readyState >= 3);
            return {
              id: ad ? 'ad:' + title : (data.video_id || title), title: title, artist: artist, art: art,
              pos: ad ? 0 : pos, dur: ad ? null : dur, playing: playing,
              rate: ad || !moving ? 0 : (v ? v.playbackRate : 1),
              prev: enabled('.previous-button'), next: enabled('.next-button'), seek: !ad
            };
          },
          control: function (p, action, ms) {
            var bar = document.querySelector('ytmusic-player-bar');
            function click(sel) { var b = bar && bar.querySelector(sel); if (!b) return false; b.click(); return true; }
            var v = p.querySelector('video');
            var playing = v ? !v.paused && !v.ended : p.getPlayerState() === 1;
            if (action === 'toggle') action = playing ? 'pause' : 'play';
            if (action === 'play') { if (playing) return true; if (click('#play-pause-button')) return true; p.playVideo(); return true; }
            if (action === 'pause') { if (!playing) return true; if (click('#play-pause-button')) return true; p.pauseVideo(); return true; }
            if (action === 'next') return click('.next-button');
            if (action === 'previous') return click('.previous-button');
            if (action === 'seek' && typeof ms === 'number') {
              if (typeof p.seekTo === 'function') p.seekTo(ms / 1000, true);
              else if (v) v.currentTime = ms / 1000;
              else return false;
              return true;
            }
            return false;
          }
        }
        """;

    /// Apple Music: the page's own MusicKit instance (nowPlayingItem,
    /// playbackState, its events) and the &lt;audio&gt; element it plays
    /// through, whose clock is finer than MusicKit's whole seconds. Transport
    /// is MusicKit's own play, pause, skip and seekToTime, as the page's own
    /// player bar uses.
    private const string AppleMusicAdapter = """
        {
          alive: function () { return true; },
          find: function () {
            try { var m = window.MusicKit && MusicKit.getInstance(); return m && typeof m.play === 'function' ? m : null; } catch (_) { return null; }
          },
          listen: function (m, wake) {
            ['playbackStateDidChange', 'nowPlayingItemDidChange', 'playbackDurationDidChange', 'playbackRateDidChange', 'queuePositionDidChange', 'mediaPlaybackError']
              .forEach(function (e) { try { m.addEventListener(e, wake); } catch (_) {} });
          },
          read: function (m) {
            var it = m.nowPlayingItem;
            if (!it) return null;
            var a = it.attributes || {};
            var state = m.playbackState, playing = !!m.isPlaying || state === 2;
            var dur = (+m.currentPlaybackDuration || 0) * 1000 || +it.playbackDuration || null;
            var pos = (+m.currentPlaybackTime || 0) * 1000;
            var el = document.getElementById('apple-music-player');
            if (el && isFinite(el.currentTime) && isFinite(el.duration) && dur && Math.abs(el.duration * 1000 - dur) < 2000) pos = el.currentTime * 1000;
            var q = m.queue, i = m.nowPlayingItemIndex;
            var art = String(it.artworkURL || a.artwork && a.artwork.url || '').replace('{w}', '600').replace('{h}', '600').replace('{c}', 'bb').replace('{f}', 'jpg');
            return {
              id: String(it.id || ''), title: it.title || a.name || '', artist: it.artistName || a.artistName || '', art: art.indexOf('https:') === 0 ? art : '',
              pos: pos, dur: dur, playing: playing, rate: playing && state === 2 ? (+m.playbackRate || 1) : 0,
              prev: true, next: !q || typeof i !== 'number' || i < q.length - 1 || m.repeatMode !== 0, seek: true
            };
          },
          control: function (m, action, ms) {
            var playing = !!m.isPlaying;
            if (action === 'toggle') action = playing ? 'pause' : 'play';
            if (action === 'play') return Promise.resolve(m.play()).then(function () { return true; });
            if (action === 'pause') return Promise.resolve(m.pause()).then(function () { return true; });
            if (action === 'next') return Promise.resolve(m.skipToNextItem()).then(function () { return true; });
            if (action === 'previous') return Promise.resolve(m.skipToPreviousItem()).then(function () { return true; });
            if (action === 'seek' && typeof ms === 'number') return Promise.resolve(m.seekToTime(ms / 1000)).then(function () { return true; });
            return false;
          }
        }
        """;

    /// Spotify keeps no global handle; its PlayerAPI (getState, getEvents,
    /// resume, pause, skipToNext, skipToPrevious, seekTo: the API its own
    /// player bar uses) is found through the React tree like the volume
    /// adapter's PlaybackAPI: first near the player controls, then in a
    /// bounded walk of the whole tree.
    private const string SpotifyAdapter = """
        {
          alive: function () { return true; },
          find: function () {
            function api(v) {
              try {
                return !!v && typeof v === 'object' && typeof v.getState === 'function' && typeof v.getEvents === 'function' &&
                  typeof v.resume === 'function' && typeof v.pause === 'function' && typeof v.skipToNext === 'function' &&
                  typeof v.skipToPrevious === 'function' && typeof v.seekTo === 'function';
              } catch (_) { return false; }
            }
            function pick(v) {
              if (api(v)) return v;
              try { if (v && typeof v === 'object' && api(v.PlayerAPI)) return v.PlayerAPI; } catch (_) {}
              return null;
            }
            function inspect(f) {
              var props = f.memoizedProps, hit = props && pick(props.value);
              if (hit) return hit;
              var h = f.memoizedState;
              for (var i = 0; h && typeof h === 'object' && i < 64; i++, h = h.next) {
                var s = h.memoizedState;
                hit = pick(s) || (Array.isArray(s) ? pick(s[0]) : null);
                if (hit) return hit;
                var deps = Array.isArray(s) && Array.isArray(s[1]) ? s[1] : (s && typeof s === 'object' && Array.isArray(s.deps) ? s.deps : null);
                if (deps) for (var j = 0; j < deps.length; j++) if ((hit = pick(deps[j]))) return hit;
                if (!('next' in h)) break;
              }
              return null;
            }
            function key(node, prefix) { for (var k in node) if (k.lastIndexOf(prefix, 0) === 0) return node[k]; return null; }
            var near = ['[data-testid="control-button-playpause"]', '[data-testid="now-playing-widget"]', '[data-testid="player-controls"]'];
            for (var q = 0; q < near.length; q++) {
              var el = document.querySelector(near[q]);
              for (var f = el && key(el, '__reactFiber$'), d = 0; f && d < 80; f = f.return, d++) {
                var hit = inspect(f);
                if (hit) return hit;
              }
            }
            var roots = [document.getElementById('main'), document.getElementById('root')].concat([].slice.call(document.body.children));
            for (var r = 0; r < roots.length; r++) {
              var container = roots[r] && key(roots[r], '__reactContainer$');
              if (!container) continue;
              var top = container.stateNode && container.stateNode.current || container;
              var stack = [top], seen = 0;
              while (stack.length && seen++ < 60000) {
                var n = stack.pop(), found = inspect(n);
                if (found) return found;
                if (n.sibling) stack.push(n.sibling);
                if (n.child) stack.push(n.child);
              }
            }
            return null;
          },
          listen: function (a, wake) { a.getEvents().addListener('update', wake); },
          read: function (a) {
            var s = a.getState();
            if (!s || !s.item) return null;
            var it = s.item, md = it.metadata || {}, uri = String(it.uri || '');
            var ad = it.type === 'ad' || md.is_advertisement === 'true' || uri.indexOf('spotify:ad:') === 0;
            var images = (it.images && it.images.length ? it.images : it.album && it.album.images) || [];
            var rank = { xlarge: 4, large: 3, standard: 2, small: 1 }, best = null;
            for (var i = 0; i < images.length; i++)
              if (images[i] && images[i].url && (!best || (rank[images[i].label] || 0) > (rank[best.label] || 0))) best = images[i];
            var art = best ? String(best.url) : '';
            if (art.indexOf('spotify:image:') === 0) art = 'https://i.scdn.co/image/' + art.slice(14);
            var dur = s.duration || (it.duration && it.duration.milliseconds) || null;
            var paused = !!s.isPaused, speed = typeof s.speed === 'number' && s.speed > 0 ? s.speed : 1;
            var moving = !paused && !s.isBuffering;
            var pos = s.positionAsOfTimestamp || 0;
            if (moving && typeof s.timestamp === 'number') pos += Math.max(0, Date.now() - s.timestamp) * speed;
            var r = s.restrictions || {};
            function allowed(name) { var v = r[name]; return v !== false && !(Array.isArray(v) && v.length); }
            return {
              id: uri || it.uid || it.name, title: it.name,
              artist: (it.artists || []).map(function (x) { return x && x.name; }).filter(Boolean).join(', '),
              art: art, pos: pos, dur: dur, playing: !paused, rate: moving ? speed : 0,
              prev: allowed('canSkipPrevious'), next: allowed('canSkipNext'), seek: !ad && allowed('canSeek')
            };
          },
          control: function (a, action, ms) {
            var s = a.getState() || {};
            if (action === 'toggle') action = s.isPaused ? 'play' : 'pause';
            if (action === 'play') return s.isPaused === false ? true : a.resume();
            if (action === 'pause') return s.isPaused ? true : a.pause();
            if (action === 'next') return a.skipToNext();
            if (action === 'previous') return a.skipToPrevious();
            if (action === 'seek' && typeof ms === 'number') return a.seekTo(ms);
            return false;
          }
        }
        """;
}
