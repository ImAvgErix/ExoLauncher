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
    private VoiceConnection? _stream;
    private string? _streamKey, _streamServer, _streamChannel, _streamEndpoint, _streamToken;
    private VoiceConnection? _live;
    private GoLiveCapture? _liveCapture;
    // Your stream's tile is on screen: it is sent the stream's own frames.
    private volatile bool _livePreview;
    private string? _liveKey, _liveServer, _liveChannel, _liveEndpoint, _liveToken, _liveTitle;
    private uint? _liveSound; // null: no sound; 0: this PC's but Exo's; else the shared app's process

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
        var (capture, error) = await GoLiveCapture.StartAsync(addon, source, plan, ct).ConfigureAwait(false);
        if (capture is null) return error;
        capture.Frame += (frame, timestamp) =>
        {
            _live?.SendVideoFrame(frame, timestamp);
            if (_livePreview && Cache.Me is { } me) PublishFrame("self", me.Id, timestamp, H264Rtp.IsKeyframe(frame), frame);
        };
        capture.Stats += (sent, bits) => Event?.Invoke("discord.golive.stats", new { width = capture.Width, height = capture.Height, fps = sent, bitrate = bits, max = capture.Bitrate });
        capture.Ended += why => { if (ReferenceEquals(_liveCapture, capture)) StopGoLive("Your stream ended: " + why + "."); };
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
        if (capture is not null) _ = capture.DisposeAsync().AsTask();
        if (live is not null) _ = live.DisposeAsync().AsTask();
        if (key is null) return;
        _ = SendAsync(19, new JsonObject { ["stream_key"] = key });
        Event?.Invoke("discord.golive", new { state = "off", message = why });
    }

    /// <summary>What this client's stream sends now, as a JPEG data URL at most width across; null when not live.</summary>
    /// <summary>Your stream's tile shows or leaves: it plays the very frames viewers get (from a fresh key frame), or stops.</summary>
    internal void WatchOwnStream(bool on)
    {
        _livePreview = on;
        if (on) _liveCapture?.Keyframe();
        else if (Cache.Me is { } me) Event?.Invoke("discord.video", new { source = "self", userId = me.Id, active = false });
    }

    internal Task<string?> GoLivePreviewAsync(int width) => _liveCapture?.PreviewAsync(width) ?? Task.FromResult<string?>(null);

    private void OpenLive()
    {
        VoiceConnection live;
        GoLiveCapture? capture;
        string? title;
        uint? sound;
        lock (_voiceGate)
        {
            if (_live is not null || _liveKey is null || _liveServer is null || _liveEndpoint is null || _liveToken is null
                || _voiceSession is null || Cache.Me is null) return;
            if (StreamChannel(_liveServer, _liveChannel) is not { } channel) return;
            live = new VoiceConnection(_liveEndpoint, _liveServer, channel, Cache.Me.Id, _voiceSession, _liveToken, listening: true, screen: true);
            _live = live;
            (capture, title, sound) = (_liveCapture, _liveTitle, _liveSound);
        }
        live.KeyframeRequested += () => capture?.Keyframe();
        live.Changed += (kind, data) =>
        {
            // A stream stopped (or replaced by a new one) says "disconnected" as it closes: that one no longer speaks for Go Live.
            if (kind != "state" || !ReferenceEquals(live, _live)) return;
            var state = JsonSerializer.SerializeToElement(data).GetProperty("state").GetString();
            if (state == "connected" && capture is not null)
            {
                _ = live.SetVideoAsync(capture.Width, capture.Height, capture.Fps, capture.Bitrate);
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

    /// <summary>Watches someone's Go Live in the call this client is in (stops watching any other first).</summary>
    internal string? WatchStream(string userId)
    {
        if (StreamKeyOf(userId) is not { } key) return "Join the voice channel to watch.";
        StopWatching();
        lock (_voiceGate) { _streamKey = key; _streamServer = _streamChannel = _streamEndpoint = _streamToken = null; }
        _ = SendAsync(20, new JsonObject { ["stream_key"] = key });
        AppLog.Info("Discord video: watching a stream.");
        return null;
    }

    internal void StopWatching()
    {
        string? key;
        VoiceConnection? stream;
        lock (_voiceGate)
        {
            key = _streamKey;
            stream = _stream;
            _streamKey = null;
            _stream = null;
        }
        if (stream is not null) _ = stream.DisposeAsync().AsTask();
        if (key is not null)
        {
            _ = SendAsync(19, new JsonObject { ["stream_key"] = key });
            Event?.Invoke("discord.video", new { source = "stream", userId = UserOfKey(key), active = false });
        }
    }

    private static string UserOfKey(string key) => key[(key.LastIndexOf(':') + 1)..];

    // Main gateway Go Live dispatches; true when handled here.
    private bool OnStreamDispatch(string type, JsonElement d)
    {
        if (type is not ("STREAM_CREATE" or "STREAM_SERVER_UPDATE" or "STREAM_DELETE" or "STREAM_UPDATE")) return false;
        var key = d.TryGetProperty("stream_key", out var k) ? k.GetString() : null;
        if (key is not null && key == _liveKey) { OnLiveDispatch(type, d); return true; }
        lock (_voiceGate)
        {
            if (key is null || key != _streamKey) return true;
            switch (type)
            {
                case "STREAM_CREATE":
                    _streamServer = d.TryGetProperty("rtc_server_id", out var s) ? s.GetString() : null;
                    _streamChannel = d.TryGetProperty("rtc_channel_id", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                    break;
                case "STREAM_SERVER_UPDATE":
                    _streamEndpoint = d.TryGetProperty("endpoint", out var e) ? e.GetString() : null;
                    _streamToken = d.TryGetProperty("token", out var t) ? t.GetString() : null;
                    break;
                case "STREAM_DELETE":
                    break;
                default:
                    return true;
            }
        }
        if (type == "STREAM_DELETE") { StopWatching(); return true; }
        OpenStream();
        return true;
    }

    private void OpenStream()
    {
        VoiceConnection stream;
        string user;
        lock (_voiceGate)
        {
            if (_stream is not null || _streamKey is null || _streamServer is null || _streamEndpoint is null || _streamToken is null
                || _voiceSession is null || Cache.Me is null) return;
            if (StreamChannel(_streamServer, _streamChannel) is not { } channel) return;
            user = UserOfKey(_streamKey);
            stream = new VoiceConnection(_streamEndpoint, _streamServer, channel, Cache.Me.Id, _voiceSession, _streamToken, listening: true)
            {
                Deafened = _deafened,
                // The stream's sound at its Stream Volume, or silent when muted for me.
                UserGain = StreamGain,
            };
            _stream = stream;
        }
        stream.Frame += (from, timestamp, key, data, orientation) => PublishFrame("stream", from, timestamp, key, data, orientation);
        stream.Changed += (kind, data) =>
        {
            // One stopped watching (StopWatching said so itself) closing late must not mark the stream watched now as ended.
            if (kind != "state" || !ReferenceEquals(stream, _stream)) return;
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
        connection.Frame += (from, timestamp, key, data, orientation) => PublishFrame("call", from, timestamp, key, data, orientation);
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
        _stream?.RequestKeyframes();
    }

    // rotation: the sender's quarter turns clockwise (0-3) to show it upright; flip: mirrored first (urn:3gpp:video-orientation).
    private void PublishFrame(string source, string userId, uint timestamp, bool keyframe, byte[] data, int orientation = 0)
    {
        if (source != "self" && !_framesWanted) return;
        Event?.Invoke("discord.video.frame", new { source, userId, timestamp, keyframe, data = Convert.ToBase64String(data), rotation = orientation & 3, flip = (orientation & 4) != 0 });
    }
}
