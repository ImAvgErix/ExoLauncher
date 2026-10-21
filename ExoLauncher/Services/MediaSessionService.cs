using System.Text.Json;
using ExoLauncher.Helpers;
using Windows.Graphics.Imaging;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace ExoLauncher.Services;

/// <summary>
/// The mini player's source of truth for the selected music provider.
/// YouTube Music, Apple Music and Spotify are read from their own page
/// (<see cref="MusicNowPlaying"/>); Spotify, while
/// its page cannot answer, from the Windows media session (SMTC) that
/// carries that provider's own app identity, never another app's. Publishes
/// coalesced snapshots to the shell and sends transport to the same source
/// the shown snapshot came from.
/// </summary>
internal sealed class MediaSessionService : IDisposable
{
    /// <param name="Source">"page", "session" or "none": where controls go.</param>
    /// <param name="Rate">How fast <paramref name="PositionMs"/> advances; 0 while paused, buffering or in an ad.</param>
    /// <param name="CapturedAt"><see cref="Environment.TickCount64"/> when the position was true.</param>
    internal sealed record Snapshot(
        string Provider, string Source, string Status, string Title, string Artist,
        string? ArtworkDataUrl, long? PositionMs, long? DurationMs, double Rate, long CapturedAt,
        bool CanPlay, bool CanPause, bool CanPrev, bool CanNext, bool CanSeek)
    {
        /// Publish order, so the shell can drop an answer older than an event.
        internal long Seq { get; init; }
        internal bool HasTrack => Status != "none" && Title.Length > 0;
    }

    internal event Action<Snapshot>? PlaybackChanged;

    private static readonly TimeSpan SessionCoalesce = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan PageCoalesce = TimeSpan.FromMilliseconds(30);
    // A track that disappears is kept this long before the mini player hides:
    // a session re-registering or a page reloading must not make it flap.
    private static readonly TimeSpan HideDelay = TimeSpan.FromSeconds(4);
    // A page that stopped answering (and is not frozen on purpose) stops
    // being believed after this long.
    private const long PageStaleMs = 10_000;
    private const int MaxArtworkDataUrlBytes = 768 * 1024;
    private static readonly HttpClient ArtworkHttp = CreateArtworkHttp();

    private static HttpClient CreateArtworkHttp()
    {
        var http = ExoHttp.Create(TimeSpan.FromSeconds(10));
        http.MaxResponseContentBufferSize = 4 << 20;
        return http;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, string?> _pageArtwork = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pageArtworkPending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _spotifyCovers = new(StringComparer.Ordinal);
    // A cover that could not be fetched, and when: asked again after a minute, never on every update.
    private readonly Dictionary<string, long> _pageArtworkFailed = new(StringComparer.Ordinal);
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private Task<GlobalSystemMediaTransportControlsSessionManager?>? _managerTask;
    private GlobalSystemMediaTransportControlsSession? _session;
    private string? _sessionArtworkKey;
    private string? _sessionArtwork;
    private bool _sessionArtworkDirty = true;
    private MusicNowPlaying? _page;
    private string? _provider;
    private Snapshot? _last;
    private long _seq;
    private long _hideAt;
    private long _generation;
    private long _playingSince;
    private Timer? _publishTimer;
    private Timer? _hideTimer;
    private Timer? _retryTimer;
    private int _retryAttempts;
    private string? _lastUnmatched;
    private bool _publishing;
    private bool _publishAgain;
    private bool _disposed;

    internal Snapshot? Current { get { lock (_gate) return _last; } }

    /// A provider switch clears the old song at once (no hide delay) and
    /// drops the old provider's session.
    internal void SetProvider(string? provider)
    {
        GlobalSystemMediaTransportControlsSession? detach;
        lock (_gate)
        {
            if (_provider == provider) return;
            _provider = provider;
            _generation++;
            _retryAttempts = 0;
            _lastUnmatched = null;
            _hideAt = 0;
            _playingSince = 0;
            detach = _session;
            _session = null;
            _sessionArtworkKey = null;
            _sessionArtwork = null;
            _last = null;
        }
        if (detach is not null) Detach(detach);
        SchedulePublish(PageCoalesce);
        if (!UsesSession(provider)) return;
        _ = EnsureManagerAsync().ContinueWith(task =>
        {
            if (task.Status == TaskStatus.RanToCompletion) Reselect();
        }, TaskScheduler.Default);
    }

    /// The provider's own page reader, or null when it closed. The owner disposes it.
    internal void SetPage(MusicNowPlaying? page)
    {
        MusicNowPlaying? previous;
        lock (_gate)
        {
            previous = _page;
            if (ReferenceEquals(previous, page)) return;
            _page = page;
        }
        if (previous is not null) previous.Changed -= OnPage;
        if (page is not null) page.Changed += OnPage;
        SchedulePublish(PageCoalesce);
    }

    private void OnPage() => SchedulePublish(PageCoalesce);

    /// YouTube Music and Apple Music never use a Windows session: every WebView2 on the
    /// machine (Exo's shell, other apps) reports msedgewebview2.exe, and a
    /// session carries no process id that could prove it is Exo's.
    private static bool UsesSession(string? provider) => provider is "spotify";

    private Task<GlobalSystemMediaTransportControlsSessionManager?> EnsureManagerAsync()
    {
        lock (_gate)
            return _managerTask ??= CreateManagerAsync();
    }

    private async Task<GlobalSystemMediaTransportControlsSessionManager?> CreateManagerAsync()
    {
        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            lock (_gate) _manager = manager;
            manager.SessionsChanged += (_, _) => Reselect();
            Reselect();
            return manager;
        }
        catch (Exception error)
        {
            AppLog.Warn("Media session manager could not start: " + error.Message);
            return null;
        }
    }

    /// Only the provider's own app identity: Spotify's desktop exe or Store
    /// package.
    internal static bool MatchesProvider(string? sourceAppId, string? provider)
    {
        if (string.IsNullOrEmpty(sourceAppId)) return false;
        return provider switch
        {
            "spotify" => sourceAppId.Equals("Spotify.exe", StringComparison.OrdinalIgnoreCase) ||
                sourceAppId.StartsWith("SpotifyAB.SpotifyMusic_", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    internal static string StatusName(GlobalSystemMediaTransportControlsSessionPlaybackStatus status) => status switch
    {
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => "playing",
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => "paused",
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => "stopped",
        _ => "none",
    };

    /// Windows does not always raise SessionsChanged when a client registers
    /// its session, and a hosted player publishes none at all until it has a
    /// track loaded. An unmatched provider is re-checked every two seconds at
    /// first and every ten for as long as it stays unmatched.
    private void ScheduleRetry(bool unmatched)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (!unmatched || !UsesSession(_provider))
            {
                _retryAttempts = 0;
                _retryTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                return;
            }
            var fast = _retryAttempts < 30;
            _retryAttempts++;
            _retryTimer ??= new Timer(_ => Reselect(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _retryTimer.Change(fast ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan);
        }
    }

    private void Reselect()
    {
        GlobalSystemMediaTransportControlsSessionManager? manager;
        string? provider;
        lock (_gate)
        {
            if (_disposed) return;
            manager = _manager;
            provider = _provider;
        }
        if (manager is null || !UsesSession(provider)) return;
        GlobalSystemMediaTransportControlsSession? chosen = null;
        try
        {
            var seen = new List<string>();
            GlobalSystemMediaTransportControlsSession? first = null;
            foreach (var candidate in manager.GetSessions())
            {
                seen.Add(candidate.SourceAppUserModelId ?? "?");
                if (!MatchesProvider(candidate.SourceAppUserModelId, provider)) continue;
                first ??= candidate;
                try
                {
                    if (candidate.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                    {
                        chosen = candidate;
                        break;
                    }
                }
                catch { }
            }
            chosen ??= first;
            // One line per distinct candidate list rather than one per retry.
            var summary = string.Join(", ", seen);
            var announce = false;
            lock (_gate)
            {
                if (chosen is not null) _lastUnmatched = null;
                else if (!string.Equals(_lastUnmatched, summary, StringComparison.Ordinal))
                {
                    _lastUnmatched = summary;
                    announce = true;
                }
            }
            if (announce) AppLog.Info("Media sessions for " + provider + ": [" + summary + "] none is " + provider + "'s.");
        }
        catch { }
        // A failed enumeration must still re-arm the retry.
        finally { ScheduleRetry(chosen is null); }
        GlobalSystemMediaTransportControlsSession? previous;
        lock (_gate)
        {
            if (_disposed || _provider != provider) return;
            previous = _session;
            if (ReferenceEquals(previous, chosen)) return;
            _session = chosen;
            _sessionArtworkDirty = true;
        }
        if (previous is not null) Detach(previous);
        if (chosen is not null) Attach(chosen);
        SchedulePublish(SessionCoalesce);
    }

    private void Attach(GlobalSystemMediaTransportControlsSession session)
    {
        try { session.MediaPropertiesChanged += OnMediaProperties; } catch { }
        try { session.PlaybackInfoChanged += OnPlaybackInfo; } catch { }
        try { session.TimelinePropertiesChanged += OnTimeline; } catch { }
    }

    private void Detach(GlobalSystemMediaTransportControlsSession session)
    {
        try { session.MediaPropertiesChanged -= OnMediaProperties; } catch { }
        try { session.PlaybackInfoChanged -= OnPlaybackInfo; } catch { }
        try { session.TimelinePropertiesChanged -= OnTimeline; } catch { }
    }

    /// Clients raise this again when the artwork arrives after the title, so
    /// the thumbnail is re-read on every raise instead of cached by title.
    private void OnMediaProperties(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs a)
    {
        lock (_gate) _sessionArtworkDirty = true;
        SchedulePublish(SessionCoalesce);
    }

    private void OnPlaybackInfo(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs a) => SchedulePublish(SessionCoalesce);
    private void OnTimeline(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs a) => SchedulePublish(SessionCoalesce);

    private void SchedulePublish(TimeSpan delay)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_publishing) { _publishAgain = true; return; }
            _publishTimer ??= new Timer(_ => _ = PublishAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _publishTimer.Change(delay, Timeout.InfiniteTimeSpan);
        }
    }

    private async Task PublishAsync()
    {
        GlobalSystemMediaTransportControlsSession? session;
        MusicNowPlaying? page;
        string? provider;
        long generation;
        lock (_gate)
        {
            if (_disposed || _publishing) return;
            _publishing = true;
            _publishAgain = false;
            session = _session;
            page = _page;
            provider = _provider;
            generation = _generation;
        }
        Snapshot? publish = null;
        try
        {
            var next = await ComposeAsync(provider, page, session);
            lock (_gate)
            {
                if (next is null || _disposed || _generation != generation || _provider != provider) return;
                var now = Environment.TickCount64;
                if (!next.HasTrack && _last is { HasTrack: true })
                {
                    if (_hideAt == 0)
                    {
                        _hideAt = now + (long)HideDelay.TotalMilliseconds;
                        _hideTimer ??= new Timer(_ => SchedulePublish(TimeSpan.Zero), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                        _hideTimer.Change(HideDelay, Timeout.InfiniteTimeSpan);
                    }
                    if (now < _hideAt) return;
                }
                if (next.HasTrack || _last is not { HasTrack: true }) _hideAt = 0;
                if (_last is { } last && Same(last, next)) return;
                _last = publish = next with { Seq = ++_seq };
            }
        }
        catch { }
        finally
        {
            lock (_gate)
            {
                _publishing = false;
                if (_publishAgain)
                {
                    _publishAgain = false;
                    _publishTimer?.Change(PageCoalesce, Timeout.InfiniteTimeSpan);
                }
            }
        }
        if (publish is not null && PlaybackChanged is { } changed)
            foreach (var handler in changed.GetInvocationList())
                try { ((Action<Snapshot>)handler)(publish); } catch { }
    }

    /// Equal for the shell: same song, state and controls, and a position
    /// within 0.4 s of where the shown one has advanced to.
    private static bool Same(Snapshot a, Snapshot b)
    {
        if (a.Provider != b.Provider || a.Source != b.Source || a.Status != b.Status || a.Title != b.Title ||
            a.Artist != b.Artist || !string.Equals(a.ArtworkDataUrl, b.ArtworkDataUrl, StringComparison.Ordinal) ||
            a.DurationMs != b.DurationMs || Math.Abs(a.Rate - b.Rate) > 0.001 || a.CanPlay != b.CanPlay ||
            a.CanPause != b.CanPause || a.CanPrev != b.CanPrev || a.CanNext != b.CanNext || a.CanSeek != b.CanSeek)
            return false;
        if (a.PositionMs is not { } from || b.PositionMs is not { } to) return a.PositionMs == b.PositionMs;
        var expected = from + (b.CapturedAt - a.CapturedAt) * a.Rate;
        return Math.Abs(expected - to) <= 400;
    }

    private async Task<Snapshot?> ComposeAsync(string? provider, MusicNowPlaying? page, GlobalSystemMediaTransportControlsSession? session)
    {
        if (provider is null) return null;
        if (page is not null && page.Service == provider)
        {
            var state = page.Current;
            var frozen = page.Frozen;
            var answering = state.FailingSince == 0 || frozen || Environment.TickCount64 - state.FailingSince < PageStaleMs;
            // Spotify's page only rules once its player was found; until then,
            // and after it stops answering, its Windows session stands in.
            if (answering && (state.Player || MusicWebPolicy.IsWeb(provider)))
            {
                if (state.Track is not { } track) return None(provider);
                var fromPage = FromPage(provider, track, frozen);
                // The page names no cover (Spotify right after it opens, a song restored paused) or it
                // is still on its way: the Windows session's cover of the same song stands in.
                if (fromPage.ArtworkDataUrl is null && session is not null && UsesSession(provider) &&
                    await SessionArtworkAsync(session, fromPage.Title, fromPage.Artist) is { } cover)
                    fromPage = fromPage with { ArtworkDataUrl = cover };
                return fromPage;
            }
        }
        if (session is not null && UsesSession(provider)) return await ReadSessionAsync(session, provider);
        return None(provider);
    }

    private static Snapshot None(string provider) =>
        new(provider, "none", "none", "", "", null, null, null, 0, Environment.TickCount64,
            CanPlay: false, CanPause: false, CanPrev: false, CanNext: false, CanSeek: false);

    /// A frozen provider only froze after it fell silent: its song is paused.
    private Snapshot FromPage(string provider, MusicNowPlaying.Track track, bool frozen)
    {
        var playing = track.Playing && !frozen;
        return new Snapshot(provider, "page", playing ? "playing" : "paused", track.Title, track.Artist,
            PageArtwork(track.ArtworkUrl ?? (provider == "spotify" ? SpotifyCoverUrl(track.Id) : null)), track.PositionMs, track.DurationMs, playing ? track.Rate : 0, track.CapturedAt,
            CanPlay: true, CanPause: true, CanPrev: track.CanPrev, CanNext: track.CanNext, CanSeek: track.CanSeek);
    }

    private static readonly string[] ArtworkHosts = ["googleusercontent.com", "ggpht.com", "ytimg.com", "scdn.co", "spotifycdn.com", "mzstatic.com"];

    /// The page's artwork URL, fetched once and re-encoded as a bounded data
    /// URL. Keyed by URL, which arrives with its title, so a track change can
    /// never show the previous cover; until it is ready the snapshot has none.
    private string? PageArtwork(string? url)
    {
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !ArtworkHosts.Any(host => uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase) ||
                uri.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase)))
            return null;
        lock (_gate)
        {
            if (_pageArtwork.TryGetValue(url, out var cached)) return cached;
            if (_pageArtworkFailed.TryGetValue(url, out var failed) && Environment.TickCount64 - failed < 60_000) return null;
            if (!_pageArtworkPending.Add(url)) return null;
        }
        _ = FetchArtworkAsync(url, uri);
        return null;
    }

    /// <summary>
    /// A Spotify song the player names without a cover (restored paused, then frozen while idle, so it
    /// never fills in): Spotify's public oEmbed for the song names it, asked once per song, and the
    /// 640 px size of the same picture is used.
    /// </summary>
    private string? SpotifyCoverUrl(string id)
    {
        var parts = id.Split(':');
        if (parts.Length != 3 || parts[0] != "spotify" || parts[1] is not ("track" or "episode")) return null;
        lock (_gate)
        {
            if (_spotifyCovers.TryGetValue(id, out var known)) return known;
            // A failed ask (offline, Spotify busy) is asked again after a minute, like a page cover.
            if (_pageArtworkFailed.TryGetValue(id, out var failed) && Environment.TickCount64 - failed < 60_000) return null;
            _spotifyCovers[id] = null;
        }
        _ = FetchSpotifyCoverAsync(id, parts[1], parts[2]);
        return null;
    }

    private async Task FetchSpotifyCoverAsync(string id, string kind, string key)
    {
        string? url = null;
        try
        {
            var page = Uri.EscapeDataString($"https://open.spotify.com/{kind}/{key}");
            using var doc = JsonDocument.Parse(await ArtworkHttp.GetStringAsync($"https://open.spotify.com/oembed?url={page}").ConfigureAwait(false));
            if (doc.RootElement.TryGetProperty("thumbnail_url", out var thumb) && thumb.GetString() is { Length: > 0 } small)
                url = small.Replace("ab67616d00001e02", "ab67616d0000b273", StringComparison.Ordinal);
        }
        catch { }
        lock (_gate)
        {
            if (url is null)
            {
                _spotifyCovers.Remove(id);
                _pageArtworkFailed[id] = Environment.TickCount64;
                return;
            }
            _pageArtworkFailed.Remove(id);
            if (_spotifyCovers.Count >= 64) _spotifyCovers.Clear();
            _spotifyCovers[id] = url;
        }
        if (url is not null) SchedulePublish(PageCoalesce);
    }

    private async Task FetchArtworkAsync(string url, Uri uri)
    {
        string? artwork = null;
        try
        {
            artwork = await ArtworkDataUrlAsync(await ArtworkHttp.GetByteArrayAsync(uri).ConfigureAwait(false));
        }
        catch { }
        lock (_gate)
        {
            _pageArtworkPending.Remove(url);
            if (artwork is null) { _pageArtworkFailed[url] = Environment.TickCount64; return; }
            _pageArtworkFailed.Remove(url);
            if (_pageArtwork.Count >= 16) _pageArtwork.Clear();
            _pageArtwork[url] = artwork;
        }
        if (artwork is not null) SchedulePublish(PageCoalesce);
    }

    private async Task<Snapshot?> ReadSessionAsync(GlobalSystemMediaTransportControlsSession session, string provider)
    {
        try
        {
            var info = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();
            var properties = await session.TryGetMediaPropertiesAsync();
            var title = properties?.Title ?? "";
            var artist = properties?.Artist ?? "";
            var artwork = await SessionArtworkAsync(properties, title + "\n" + artist);
            var controls = info.Controls;
            var status = StatusName(info.PlaybackStatus);
            var (positionMs, durationMs, capturedAt) = Timeline(timeline, status);
            var rate = status == "playing" ? Math.Clamp(info.PlaybackRate ?? 1.0, 0, 4) : 0;
            return new Snapshot(provider, "session", status, title, artist, artwork, positionMs, durationMs, rate, capturedAt,
                CanPlay: controls.IsPlayEnabled,
                CanPause: controls.IsPauseEnabled,
                CanPrev: controls.IsPreviousEnabled,
                CanNext: controls.IsNextEnabled,
                CanSeek: controls.IsPlaybackPositionEnabled);
        }
        catch { return null; }
    }

    /// The session's cover when it is showing this song (title and artist match), else null.
    private async Task<string?> SessionArtworkAsync(GlobalSystemMediaTransportControlsSession session, string title, string artist)
    {
        try
        {
            var properties = await session.TryGetMediaPropertiesAsync();
            return properties is not null && properties.Title == title && properties.Artist == artist
                ? await SessionArtworkAsync(properties, title + "\n" + artist) : null;
        }
        catch { return null; }
    }

    /// The session's cover, read again only when the song changes (or the session said it changed).
    private async Task<string?> SessionArtworkAsync(GlobalSystemMediaTransportControlsSessionMediaProperties? properties, string key)
    {
        string? artwork;
        bool reread;
        lock (_gate)
        {
            reread = _sessionArtworkDirty || _sessionArtworkKey != key;
            _sessionArtworkDirty = false;
            artwork = reread ? null : _sessionArtwork;
        }
        if (!reread) return artwork;
        if (properties?.Thumbnail is { } thumbnail)
        {
            try
            {
                using var stream = await thumbnail.OpenReadAsync();
                artwork = await ArtworkDataUrlAsync(stream);
            }
            catch { artwork = null; }
        }
        lock (_gate)
        {
            _sessionArtworkKey = key;
            _sessionArtwork = artwork;
        }
        return artwork;
    }

    /// <summary>
    /// Position and length relative to the track start (clients such as Apple
    /// Music report a non-zero StartTime), the moment the position was true
    /// on the monotonic clock, and no jump-ahead after a resume: a client that
    /// keeps a stale timeline across pause would otherwise let the shell
    /// extrapolate the whole paused stretch.
    /// </summary>
    private (long? PositionMs, long? DurationMs, long CapturedAt) Timeline(
        GlobalSystemMediaTransportControlsSessionTimelineProperties timeline, string status)
    {
        var start = timeline.StartTime > TimeSpan.Zero ? timeline.StartTime : TimeSpan.Zero;
        long? position = timeline.Position >= start ? (long)(timeline.Position - start).TotalMilliseconds
            : timeline.Position >= TimeSpan.Zero ? (long)timeline.Position.TotalMilliseconds : null;
        long? duration = timeline.EndTime > start ? (long)(timeline.EndTime - start).TotalMilliseconds : null;
        if (position is { } clamp && duration is { } length && clamp > length) position = length;
        var tick = Environment.TickCount64;
        var ageMs = timeline.LastUpdatedTime.Year >= 2000
            ? Math.Max(0, (long)(DateTimeOffset.UtcNow - timeline.LastUpdatedTime).TotalMilliseconds)
            : 0;
        var captured = tick - ageMs;
        lock (_gate)
        {
            if (status != "playing") _playingSince = 0;
            else if (_playingSince == 0 || _last?.Status != "playing") _playingSince = tick;
            if (status == "playing" && captured < _playingSince) captured = _playingSince;
        }
        return (position, duration, captured);
    }

    /// Transport goes to the source of the song the shell shows. Must be
    /// called on the UI thread: YouTube Music's page is reached from there.
    internal async Task<object> ControlAsync(string? action, double? positionMs)
    {
        Snapshot? shown;
        MusicNowPlaying? page;
        GlobalSystemMediaTransportControlsSession? session;
        lock (_gate) { shown = _last; page = _page; session = _session; }
        if (action is not ("play" or "pause" or "toggle" or "next" or "prev" or "previous" or "seek"))
            return new { ok = false, message = "This control is unavailable." };
        if (action == "seek" && (positionMs is not { } target || !double.IsFinite(target) || target < 0))
            return new { ok = false, message = "This control is unavailable." };
        if (shown is { Source: "page" } && page is not null && page.Service == shown.Provider)
        {
            var done = await page.ControlAsync(action == "prev" ? "previous" : action, action == "seek" ? positionMs : null);
            return done switch
            {
                true => new { ok = true, message = (string?)null },
                false => new { ok = false, message = (string?)"The player did not accept the control." },
                null => new { ok = false, message = (string?)"The player could not respond." },
            };
        }
        if (shown is not { Source: "session" } || session is null)
            return new { ok = false, message = "Nothing is playing." };
        try
        {
            var status = session.GetPlaybackInfo().PlaybackStatus;
            Task<bool>? call = action switch
            {
                "play" => session.TryPlayAsync().AsTask(),
                "pause" => session.TryPauseAsync().AsTask(),
                "toggle" => status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                    ? session.TryPauseAsync().AsTask()
                    : session.TryPlayAsync().AsTask(),
                "next" => session.TrySkipNextAsync().AsTask(),
                "prev" or "previous" => session.TrySkipPreviousAsync().AsTask(),
                // SMTC positions are 100-nanosecond ticks, not a TimeSpan.
                "seek" => session.TryChangePlaybackPositionAsync((long)(positionMs!.Value * 10_000)).AsTask(),
                _ => null,
            };
            if (call is null) return new { ok = false, message = "This control is unavailable." };
            var done = await call;
            if (done) SchedulePublish(SessionCoalesce);
            return new { ok = done, message = done ? null : "The player did not accept the control." };
        }
        catch { return new { ok = false, message = "The player could not respond." }; }
    }

    /// A downloaded cover: a JPEG or PNG within the budget goes out exactly as it came (nothing to
    /// decode); anything else is decoded and re-encoded below.
    private static async Task<string?> ArtworkDataUrlAsync(byte[] raw)
    {
        var mime = raw is [0xFF, 0xD8, 0xFF, ..] ? "image/jpeg" : raw is [0x89, 0x50, 0x4E, 0x47, ..] ? "image/png" : null;
        if (mime is not null && raw.Length < MaxArtworkDataUrlBytes / 4 * 3) return "data:" + mime + ";base64," + Convert.ToBase64String(raw);
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(raw);
            await writer.StoreAsync();
            writer.DetachStream();
        }
        stream.Seek(0);
        return await ArtworkDataUrlAsync(stream);
    }

    /// Artwork as a data URL at full quality: the provider's own JPEG or PNG exactly as it came when
    /// it fits the budget (never compressed a second time), else re-encoded at up to 640 px, finely.
    private static async Task<string?> ArtworkDataUrlAsync(IRandomAccessStream stream)
    {
        try
        {
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var codec = decoder.DecoderInformation?.CodecId;
            var mime = codec == BitmapDecoder.PngDecoderId ? "image/png" : codec == BitmapDecoder.JpegDecoderId ? "image/jpeg" : null;
            if (mime is not null && stream.Size is > 0 and < MaxArtworkDataUrlBytes / 4 * 3)
            {
                using var original = new DataReader(stream.GetInputStreamAt(0));
                await original.LoadAsync((uint)stream.Size);
                var raw = new byte[stream.Size];
                original.ReadBytes(raw);
                return "data:" + mime + ";base64," + Convert.ToBase64String(raw);
            }
            var largest = Math.Max(decoder.PixelWidth, decoder.PixelHeight);
            foreach (var size in new[] { 640u, 480u, 320u })
            {
                var ratio = Math.Min(1.0, size / (double)largest);
                foreach (var quality in new[] { 0.95f, 0.85f })
                {
                    using var output = new InMemoryRandomAccessStream();
                    var options = new BitmapPropertySet
                    {
                        { "ImageQuality", new BitmapTypedValue(quality, Windows.Foundation.PropertyType.Single) },
                    };
                    var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output, options);
                    var transform = new BitmapTransform
                    {
                        ScaledWidth = Math.Max(1, (uint)Math.Round(decoder.PixelWidth * ratio)),
                        ScaledHeight = Math.Max(1, (uint)Math.Round(decoder.PixelHeight * ratio)),
                    };
                    using var bitmap = await decoder.GetSoftwareBitmapAsync(
                        BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
                        ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
                    encoder.SetSoftwareBitmap(bitmap);
                    await encoder.FlushAsync();
                    using var reader = new DataReader(output.GetInputStreamAt(0));
                    await reader.LoadAsync((uint)output.Size);
                    var bytes = new byte[output.Size];
                    reader.ReadBytes(bytes);
                    var dataUrl = "data:image/jpeg;base64," + Convert.ToBase64String(bytes);
                    if (dataUrl.Length <= MaxArtworkDataUrlBytes) return dataUrl;
                }
            }
            return null;
        }
        catch { return null; }
    }

    public void Dispose()
    {
        GlobalSystemMediaTransportControlsSession? session;
        MusicNowPlaying? page;
        lock (_gate)
        {
            _disposed = true;
            session = _session;
            _session = null;
            page = _page;
            _page = null;
        }
        if (page is not null) page.Changed -= OnPage;
        try { _publishTimer?.Dispose(); } catch { }
        try { _hideTimer?.Dispose(); } catch { }
        try { _retryTimer?.Dispose(); } catch { }
        if (session is not null) Detach(session);
        PlaybackChanged = null;
    }
}
