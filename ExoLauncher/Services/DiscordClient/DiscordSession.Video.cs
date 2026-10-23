using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExoLauncher.Helpers;
using ExoLauncher.Services.DiscordClient.Voice;

namespace ExoLauncher.Services.DiscordClient;

/// <summary>
/// Video for Exo's own Discord client (docs/architecture/discord-video.md). Cameras arrive on the
/// call's own connection. A Go Live stream is watched on its own connection: op 20 STREAM_WATCH with
/// the stream key, then Discord's STREAM_CREATE (the stream's rtc_server_id) and STREAM_SERVER_UPDATE
/// (its voice server and token) open a listening <see cref="VoiceConnection"/> on that server, with
/// the call's session and DAVE group rtc_server_id - 1. Frames go to the page as discord.video.frame.
/// This client's own Go Live is the same in reverse: Exo Capture encodes the window or screen
/// (<see cref="GoLiveCapture"/>) and its frames go straight onto the stream's connection.
/// </summary>
internal sealed partial class DiscordSession
{
    // The streams this client watches, by stream key: as in Discord, several at once.
    private readonly Dictionary<string, Watch> _watches = [];
    private VoiceConnection? _live;
    private GoLiveCapture? _liveCapture;
    // Your stream's tile is on screen: it is sent the stream's own frames.
    private volatile bool _livePreview;
    private string? _liveKey, _liveServer, _liveChannel, _liveEndpoint, _liveToken, _liveTitle;
    private uint? _liveSound; // null: no sound; 0: this PC's but Exo's; else the shared app's process
    private int _liveMost; // the plan's rate: the most the stream goes up to when the server takes it
    private (CaptureAddon Addon, GoLiveSources.Source Source, GoLivePlan Plan)? _liveStart; // to start Exo Capture again in another codec
    private const int StreamStart = 8_000_000;

    /// <summary>
    /// Go Live: this client's own stream of a window or screen in the call it is in. Exo Capture starts
    /// first (so a window that can't be captured says so at once), then op 18 STREAM_CREATE and op 22
    /// unpaused; frames flow once the stream's own connection is up.
    /// </summary>
    internal async Task<string?> StartGoLiveAsync(GoLiveSources.Source source, int height, int fps, bool sound, CaptureAddon addon, CancellationToken ct)
    {
        if (Cache.Me is null || StreamKeyOf(Cache.Me.Id) is null) return "Join a voice channel to go live.";
        StopGoLive();
        var plan = GoLivePlan.For(source.Width, source.Height, height, fps);
        // The stream starts at most at 8 Mbit/s (Discord's server takes 9 at most) and then follows what the server says
        // it takes, up to the plan's rate (VoiceConnection.FollowServer).
        var start = plan with { Bitrate = Math.Min(plan.Bitrate, StreamStart) };
        var (capture, error) = await GoLiveCapture.StartAsync(addon, source, start, ct).ConfigureAwait(false);
        if (capture is null) return error;
        _liveMost = plan.Bitrate;
        _liveStart = (addon, source, start);
        WireCapture(capture);
        string? key, guild, channel;
        lock (_voiceGate)
        {
            // The call may have ended while the capture started.
            key = Cache.Me is { } me ? StreamKeyOf(me.Id) : null;
            if (key is not null)
            {
                _liveKey = key;
                _liveServer = _liveChannel = _liveEndpoint = _liveToken = null;
                _liveCapture = capture;
                _liveTitle = source.Title;
                _liveSound = !sound ? null : source.Kind == "window" ? source.ProcessId : 0;
            }
            guild = _voiceGuild;
            channel = _voiceChannel;
        }
        if (key is null)
        {
            await capture.DisposeAsync().ConfigureAwait(false);
            return "Join a voice channel to go live.";
        }
        _ = SendAsync(18, new JsonObject { ["type"] = guild is null ? "call" : "guild", ["guild_id"] = guild, ["channel_id"] = channel, ["preferred_region"] = null });
        _ = SendAsync(22, new JsonObject { ["stream_key"] = key, ["paused"] = false });
        Event?.Invoke("discord.golive", new { state = "connecting", title = source.Title });
        AppLog.Info($"Discord video: going live ({capture.Width}x{capture.Height}, {capture.Fps} fps, {capture.Bitrate / 1000} kbit/s, {source.Kind}, sound {(sound ? "on" : "off")}).");
        return null;
    }

    /// <summary>Ends this client's Go Live; why reaches the page when it ended on its own.</summary>
    internal void StopGoLive(string? why = null)
    {
        string? key;
        VoiceConnection? live;
        GoLiveCapture? capture;
        lock (_voiceGate)
        {
            key = _liveKey;
            live = _live;
            capture = _liveCapture;
            _liveKey = null;
            _live = null;
            _liveCapture = null;
        }
        _liveStart = null;
        if (capture is not null) _ = capture.DisposeAsync().AsTask();
        if (live is not null) _ = live.DisposeAsync().AsTask();
        if (key is null) return;
        AppLog.Info("Discord video: your stream stopped" + (why is null ? "." : " (" + why.TrimEnd('.') + ")."));
        _ = SendAsync(19, new JsonObject { ["stream_key"] = key });
        Event?.Invoke("discord.golive", new { state = "off", message = why });
    }

    /// <summary>Your stream's tile shows or leaves: it plays the very frames viewers get (from a fresh key frame), or stops.</summary>
    internal void WatchOwnStream(bool on)
    {
        _livePreview = on;
        if (on) _liveCapture?.Keyframe();
        else if (Cache.Me is { } me) Event?.Invoke("discord.video", new { source = "self", userId = me.Id, active = false });
    }

    internal Task<string?> GoLivePreviewAsync(int width) => _liveCapture?.PreviewAsync(width) ?? Task.FromResult<string?>(null);

    // A Go Live capture's frames go to the stream, and to your own tile while it shows; its end ends the stream, unless
    // it is being replaced by one in another codec.
    private void WireCapture(GoLiveCapture capture)
    {
        capture.Frame += (frame, timestamp) =>
        {
            _live?.SendVideoFrame(frame, timestamp, capture.Hevc);
            if (_livePreview && Cache.Me is { } me)
                PublishFrame("self", me.Id, timestamp, capture.Hevc ? H265Rtp.IsKeyframe(frame) : H264Rtp.IsKeyframe(frame), frame, hevc: capture.Hevc);
        };
        capture.Stats += (sent, bits) => Event?.Invoke("discord.golive.stats", new { width = capture.Width, height = capture.Height, fps = sent, bitrate = bits, max = capture.Bitrate, codec = capture.Hevc ? "H.265" : "H.264" });
        capture.Ended += why => { if (ReferenceEquals(_liveCapture, capture) && !capture.Retiring) StopGoLive("Your stream ended: " + why + "."); };
    }

    // The server picked the stream's codec (H.265 when every viewer can play it, H.264 again when one who can't joins):
    // Exo Capture starts over in it at the rate the stream had reached. Frames in the old codec stop at once
    // (VoiceConnection.SendVideoFrame drops them), so viewers wait at most for the new encoder's first frame.
    private async Task SwitchCodecAsync(VoiceConnection live, bool hevc)
    {
        GoLiveCapture? old;
        (CaptureAddon Addon, GoLiveSources.Source Source, GoLivePlan Plan)? start;
        lock (_voiceGate) (old, start) = (_liveCapture, _liveStart);
        if (old is null || start is not { } s || old.Hevc == hevc || (hevc && !old.HevcSupported)) return;
        old.Retiring = true; // starting the new stream ends the old one's pipe
        var (next, error) = await GoLiveCapture.StartAsync(s.Addon, s.Source, s.Plan with { Bitrate = old.Bitrate }, CancellationToken.None, hevc).ConfigureAwait(false);
        if (next is null || next.Hevc != hevc)
        {
            if (next is not null) await next.DisposeAsync().ConfigureAwait(false);
            if (ReferenceEquals(_live, live)) StopGoLive($"Your stream ended: it couldn't change to {(hevc ? "H.265" : "H.264")} ({error ?? "Exo Capture used the other"}).");
            return;
        }
        WireCapture(next);
        bool current;
        lock (_voiceGate)
        {
            current = ReferenceEquals(_live, live) && ReferenceEquals(_liveCapture, old);
            if (current) _liveCapture = next;
        }
        await old.DisposeAsync().ConfigureAwait(false);
        if (!current) { await next.DisposeAsync().ConfigureAwait(false); return; }
        AppLog.Info($"Discord video: your stream now sends {(hevc ? "H.265" : "H.264")}.");
        next.Keyframe();
    }

    private void OpenLive()
    {
        VoiceConnection live;
        string? title;
        uint? sound;
        lock (_voiceGate)
        {
            if (_live is not null || _liveKey is null || _liveServer is null || _liveEndpoint is null || _liveToken is null
                || _voiceSession is null || Cache.Me is null)
            {
                if (_live is null && _liveKey is not null && _liveServer is not null && _liveEndpoint is not null)
                    AppLog.Info($"Discord video: your stream can't connect yet (token {(_liveToken is null ? "missing" : "given")}, voice session {(_voiceSession is null ? "missing" : "given")}).");
                return;
            }
            if (StreamChannel(_liveServer, _liveChannel) is not { } channel)
            {
                AppLog.Info("Discord video: your stream has no channel to connect to.");
                return;
            }
            AppLog.Info("Discord video: connecting your stream.");
            live = new VoiceConnection(_liveEndpoint, _liveServer, channel, Cache.Me.Id, _voiceSession, _liveToken, listening: true, screen: true)
            {
                EncodesH265 = _liveCapture?.HevcSupported == true,
            };
            _live = live;
            (title, sound) = (_liveTitle, _liveSound);
        }
        // The capture of the moment: it is replaced when the server changes the stream's codec.
        live.KeyframeRequested += () => _liveCapture?.Keyframe();
        live.RateWanted += rate => _liveCapture?.SetBitrate(rate);
        live.CodecChosen += hevc => _ = SwitchCodecAsync(live, hevc);
        live.Changed += (kind, data) =>
        {
            // A stream stopped (or replaced by a new one) says "disconnected" as it closes: that one no longer speaks for Go Live.
            if (kind != "state" || !ReferenceEquals(live, _live)) return;
            var state = JsonSerializer.SerializeToElement(data).GetProperty("state").GetString();
            if (state == "connected" && _liveCapture is { } capture)
            {
                live.FollowServer(capture.Bitrate, Math.Max(capture.Bitrate, _liveMost));
                _ = live.SetVideoAsync(capture.Width, capture.Height, capture.Fps, Math.Max(capture.Bitrate, _liveMost));
                _ = live.SpeakingAsync(2);
                if (sound is { } app) live.StartSoundshare(app);
                capture.Keyframe();
                Event?.Invoke("discord.golive", new { state = "live", title });
            }
            else if (state == "disconnected") StopGoLive("Your stream ended: Discord closed it.");
        };
        live.Start();
    }

    /// <summary>
    /// A stream's own channel, which its connection identifies with and is its DAVE group: STREAM_CREATE's rtc_channel_id,
    /// else rtc_server_id - 1 (Discord's RTCConnection.trueChannelId); null when the server id isn't a number.
    /// </summary>
    internal static string? StreamChannel(string server, string? channel) =>
        !string.IsNullOrEmpty(channel) ? channel
        : ulong.TryParse(server, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? (id - 1).ToString(CultureInfo.InvariantCulture) : null;

    /// <summary>A user's Go Live stream key in the call this client is in.</summary>
    private string? StreamKeyOf(string userId)
    {
        lock (_voiceGate)
        {
            if (_voiceChannel is null) return null;
            return _voiceGuild is { } guild ? $"guild:{guild}:{_voiceChannel}:{userId}" : $"call:{_voiceChannel}:{userId}";
        }
    }

    // One watched stream: what Discord said about it, and its own connection once it has them.
    private sealed class Watch(string key)
    {
        internal readonly string Key = key;
        internal string? Server, Channel, Endpoint, Token;
        internal VoiceConnection? Connection;
        internal long AskedAt = Environment.TickCount64;
        internal bool Retried;
    }

    /// <summary>Watches someone's Go Live in the call this client is in, beside any other watched; one watched already starts over (Try Again).</summary>
    internal string? WatchStream(string userId)
    {
        if (StreamKeyOf(userId) is not { } key) return "Join the voice channel to watch.";
        StopWatching(userId);
        lock (_voiceGate) _watches[key] = new Watch(key);
        _ = SendAsync(20, new JsonObject { ["stream_key"] = key });
        AppLog.Info("Discord video: watching a stream.");
        return null;
    }

    /// <summary>Stops watching one person's stream, or every stream (null).</summary>
    internal void StopWatching(string? userId = null)
    {
        List<Watch> stopped;
        lock (_voiceGate)
        {
            stopped = [.. _watches.Values.Where(w => userId is null || UserOfKey(w.Key) == userId)];
            foreach (var watch in stopped) _watches.Remove(watch.Key);
        }
        foreach (var watch in stopped)
        {
            if (watch.Connection is { } connection) _ = connection.DisposeAsync().AsTask();
            _ = SendAsync(19, new JsonObject { ["stream_key"] = watch.Key });
            Event?.Invoke("discord.video", new { source = "stream", userId = UserOfKey(watch.Key), active = false });
        }
    }

    // Under _voiceGate: every watched stream's connection.
    private IEnumerable<VoiceConnection> WatchedConnections() => _watches.Values.Select(w => w.Connection).OfType<VoiceConnection>();

    private static string UserOfKey(string key) => key[(key.LastIndexOf(':') + 1)..];

    // Main gateway Go Live dispatches; true when handled here.
    private bool OnStreamDispatch(string type, JsonElement d)
    {
        if (type is not ("STREAM_CREATE" or "STREAM_SERVER_UPDATE" or "STREAM_DELETE" or "STREAM_UPDATE")) return false;
        var key = d.TryGetProperty("stream_key", out var k) ? k.GetString() : null;
        bool watched;
        lock (_voiceGate) watched = key is not null && _watches.ContainsKey(key);
        // Each Go Live message for a stream this client watches or sends (never its token): a stream that won't open says why.
        if (key is not null && (key == _liveKey || watched))
            AppLog.Info($"Discord video: {type} for {(key == _liveKey ? "your stream" : "the watched stream")}" + type switch
            {
                "STREAM_CREATE" => $" (server {(d.TryGetProperty("rtc_server_id", out var rs) ? "given" : "missing")}, channel {(d.TryGetProperty("rtc_channel_id", out var rc) && rc.ValueKind == JsonValueKind.String ? "given" : "none")}, paused {(d.TryGetProperty("paused", out var pz) ? pz.GetRawText() : "?")}).",
                "STREAM_SERVER_UPDATE" => $" (endpoint {(d.TryGetProperty("endpoint", out var ep) && ep.GetString() is { } host ? host.Split(':')[0] : "missing")}).",
                "STREAM_DELETE" => $" (reason {(d.TryGetProperty("reason", out var why) ? why.GetRawText() : "none")}).",
                _ => ".",
            });
        if (key is not null && key == _liveKey) { OnLiveDispatch(type, d); return true; }
        Watch? watch;
        var again = false;
        lock (_voiceGate)
        {
            if (key is null || !_watches.TryGetValue(key, out watch)) return true;
            switch (type)
            {
                case "STREAM_CREATE":
                    watch.Server = d.TryGetProperty("rtc_server_id", out var s) ? s.GetString() : null;
                    watch.Channel = d.TryGetProperty("rtc_channel_id", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                    break;
                case "STREAM_SERVER_UPDATE":
                    watch.Endpoint = d.TryGetProperty("endpoint", out var e) ? e.GetString() : null;
                    watch.Token = d.TryGetProperty("token", out var t) ? t.GetString() : null;
                    break;
                case "STREAM_DELETE":
                    // Discord at times ends a watch the moment it's asked for (the stream between two of its states), and
                    // Try Again then works: it is asked once more by itself, a second later.
                    again = !watch.Retried && watch.Connection is null && Environment.TickCount64 - watch.AskedAt < 5000;
                    watch.Retried |= again;
                    break;
                default:
                    return true;
            }
        }
        if (type == "STREAM_DELETE")
        {
            if (again) _ = AskAgainAsync(watch);
            else StopWatching(UserOfKey(watch.Key));
            return true;
        }
        OpenStream(watch);
        return true;
    }

    private async Task AskAgainAsync(Watch watch)
    {
        AppLog.Info("Discord video: the watched stream closed as it was asked for: asking again.");
        await Task.Delay(1000).ConfigureAwait(false);
        lock (_voiceGate)
        {
            if (!_watches.TryGetValue(watch.Key, out var now) || now != watch) return;
            watch.AskedAt = Environment.TickCount64;
        }
        _ = SendAsync(20, new JsonObject { ["stream_key"] = watch.Key });
    }

    private void OpenStream(Watch watch)
    {
        VoiceConnection stream;
        string user;
        lock (_voiceGate)
        {
            if (!_watches.TryGetValue(watch.Key, out var current) || current != watch || watch.Connection is not null
                || watch.Server is null || watch.Endpoint is null || watch.Token is null || _voiceSession is null || Cache.Me is null) return;
            if (StreamChannel(watch.Server, watch.Channel) is not { } channel)
            {
                AppLog.Info("Discord video: the watched stream has no channel to connect to.");
                return;
            }
            AppLog.Info("Discord video: connecting to the watched stream.");
            user = UserOfKey(watch.Key);
            stream = new VoiceConnection(watch.Endpoint, watch.Server, channel, Cache.Me.Id, _voiceSession, watch.Token, listening: true)
            {
                Deafened = _deafened,
                // The stream's sound at its Stream Volume, or silent when muted for me.
                UserGain = StreamGain,
            };
            watch.Connection = stream;
        }
        stream.Frame += (from, timestamp, key, data, orientation, hevc) => PublishFrame("stream", from, timestamp, key, data, orientation, hevc);
        stream.Changed += (kind, data) =>
        {
            // One stopped watching (StopWatching said so itself) closing late must not mark the stream watched now as ended.
            if (kind != "state") return;
            lock (_voiceGate)
                if (!_watches.TryGetValue(watch.Key, out var current) || current != watch || !ReferenceEquals(stream, watch.Connection)) return;
            var state = JsonSerializer.SerializeToElement(data).GetProperty("state").GetString();
            Event?.Invoke("discord.video", new { source = "stream", userId = user, active = state == "connected", state });
        };
        stream.Start();
    }

    private void OnLiveDispatch(string type, JsonElement d)
    {
        // Whether Discord holds your stream paused and how many watch it: the first thing to read when viewers can't see it.
        if (type is "STREAM_CREATE" or "STREAM_UPDATE")
            AppLog.Info($"Discord video: {type} for your stream (paused {(d.TryGetProperty("paused", out var paused) ? paused.GetRawText() : "unset")}, " +
                $"{(d.TryGetProperty("viewer_ids", out var viewers) && viewers.ValueKind == JsonValueKind.Array ? viewers.GetArrayLength() : 0)} watching).");
        lock (_voiceGate)
        {
            switch (type)
            {
                case "STREAM_CREATE":
                    _liveServer = d.TryGetProperty("rtc_server_id", out var s) ? s.GetString() : null;
                    _liveChannel = d.TryGetProperty("rtc_channel_id", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                    break;
                case "STREAM_SERVER_UPDATE":
                    _liveEndpoint = d.TryGetProperty("endpoint", out var e) ? e.GetString() : null;
                    _liveToken = d.TryGetProperty("token", out var t) ? t.GetString() : null;
                    break;
                case "STREAM_DELETE": break;
                default: return;
            }
        }
        if (type == "STREAM_DELETE") { StopGoLive(); return; }
        OpenLive();
    }

    /// <summary>The camera on in the call this client is in: voice state self_video, then op 12 with its size.</summary>
    internal string? StartCamera(int width, int height, int fps)
    {
        VoiceConnection? voice;
        lock (_voiceGate)
        {
            voice = _voiceState == "connected" ? _voice : null;
            if (voice is null) return "Join a voice channel to turn on your camera.";
            _camera = true;
        }
        SendVoiceState();
        _ = voice.SetVideoAsync(width, height, fps);
        Event?.Invoke("discord.camera", new { state = "live" });
        return null;
    }

    internal void StopCamera()
    {
        VoiceConnection? voice;
        lock (_voiceGate)
        {
            if (!_camera) return;
            _camera = false;
            voice = _voice;
        }
        if (voice is not null) _ = voice.SetVideoAsync(0, 0, 0);
        SendVoiceState();
        Event?.Invoke("discord.camera", new { state = "off" });
    }

    /// <summary>One encoded camera frame (Annex B H.264), timed in milliseconds.</summary>
    internal void SendCameraFrame(byte[] frame, double milliseconds)
    {
        if (_camera) _voice?.SendVideoFrame(frame, unchecked((uint)(long)(milliseconds * 90)));
    }

    /// <summary>The call's connection reports cameras turning on and off, and their frames.</summary>
    private void WatchCallVideo(VoiceConnection connection)
    {
        connection.Frame += (from, timestamp, key, data, orientation, hevc) => PublishFrame("call", from, timestamp, key, data, orientation, hevc);
        connection.KeyframeRequested += () => Event?.Invoke("discord.video.keyframe", new { source = "camera" });
    }

    // Whether the page shows a camera or a watched stream right now (on screen, with somewhere to draw it): only then
    // do their frames cross to it. Your own stream's tile asks for its frames separately (WatchOwnStream).
    private volatile bool _framesWanted;

    /// <summary>The page started or stopped showing call video; starting again asks every sender for a keyframe to begin from.</summary>
    internal void WantFrames(bool on)
    {
        if (_framesWanted == on) return;
        _framesWanted = on;
        if (!on) return;
        _voice?.RequestKeyframes();
        List<VoiceConnection> watched;
        lock (_voiceGate) watched = [.. WatchedConnections()];
        foreach (var stream in watched) stream.RequestKeyframes();
    }

    // rotation: the sender's quarter turns clockwise (0-3) to show it upright; flip: mirrored first (urn:3gpp:video-orientation).
    private int _framesHeldBack;

    private void PublishFrame(string source, string userId, uint timestamp, bool keyframe, byte[] data, int orientation = 0, bool hevc = false)
    {
        if (source != "self" && !_framesWanted)
        {
            // The page hasn't asked for call video: said once in a while, so a stream that never shows says so.
            if (Interlocked.Increment(ref _framesHeldBack) % 600 == 1) AppLog.Info("Discord video: frames held back (the page hasn't asked for call video).");
            return;
        }
        VideoFrame?.Invoke(new { @event = "discord.video.frame", source, userId, timestamp, keyframe, rotation = orientation & 3, flip = (orientation & 4) != 0, codec = hevc ? "h265" : "h264" }, data);
    }
}
