using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services.DiscordClient.Voice;

/// <summary>
/// One voice call: Discord's voice gateway (v8: heartbeats with seq_ack, resume after a drop,
/// binary DAVE messages), the UDP leg (<see cref="VoiceTransport"/>), DAVE (<see cref="DaveSession"/>)
/// and the sound (<see cref="VoiceAudio"/>). Sending: microphone frame, voice detection, Opus, DAVE,
/// transport. Receiving the reverse, by SSRC, for members the gateway announced. Speaking changes
/// and the connection state go to the UI through <see cref="Changed"/>.
/// Video (docs/architecture/discord-video.md): H.264 on payload type 101, announced per user by op 12;
/// each user's frames are put back together (<see cref="H264Rtp.H264Assembler"/>), DAVE-decrypted
/// and handed to the page to decode. A Go Live stream is its own connection of this class
/// (<see cref="Listening"/>: no microphone), on the stream's server with DAVE group rtc_server_id - 1.
/// </summary>
internal sealed class VoiceConnection : IAsyncDisposable
{
    private readonly string _endpoint, _serverId, _channelId, _userId, _sessionId, _token;
    // "screen" for this client's own Go Live, "video" for a call's camera, as Discord's client names them.
    private readonly string _videoType;
    private readonly ulong _groupId;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly Dictionary<uint, string> _ssrcUsers = [];
    private readonly Dictionary<uint, int> _orientation = [];
    private readonly Dictionary<uint, nint> _decoders = [];
    private readonly Dictionary<uint, ushort> _lastAudioSequence = [];
    private readonly Dictionary<string, long> _heardAt = [];
    private readonly Dictionary<uint, string> _videoUsers = [];
    private readonly Dictionary<uint, H264Rtp.H264Receiver> _receivers = [];
    private readonly Dictionary<uint, uint> _rtxOf = []; // RTX SSRC → the video SSRC it resends
    private readonly H264Rtp.SentHistory _sent = new();
    private Timer? _videoTimer;
    private ushort _rtxSequence;
    private readonly Lock _videoSendGate = new();
    private readonly Lock _gate = new();
    private readonly Lock _decodeGate = new(); // _decoders, _lastAudioSequence and _cleaners
    // Noise Suppression for Others: a DeepFilterNet per speaker, made when they first talk with it on.
    private readonly Dictionary<uint, InboundCleaner> _cleaners = [];
    private ClientWebSocket? _ws;
    private VoiceTransport? _transport;
    private DaveSession? _dave;
    private VoiceAudio? _audio;
    // The microphone's processing: the same chain Voice Check and Voice Tune play back.
    private readonly VoiceChain _chain = new();
    private bool _toldMyVoiceStopped;
    private VoiceStages _loggedStages;
    private int _gateFrames;
    private nint _encoder;
    private uint _ssrc, _videoSsrc, _rtxSsrc;
    // The video stream's type as the server's ready names it ("video", even for Go Live's "screen"): op 12 echoes it,
    // as Discord's client does (initializeStreamParameters keeps READY's type).
    private string? _streamType;
    private ushort _videoSequence;
    private readonly bool _listening, _screen;
    private readonly int _bitrate;
    private int _seqAck = -1;
    private bool _acked = true;
    private bool _speaking;
    private int _silence;
    private readonly MicSilence _micSilence = new();
    private readonly PushToTalkGate _ptt = new();
    internal bool Muted { get; set; }
    // Kept here too: the sound opens only once Discord's session description comes, and starts deafened if the user is.
    private volatile bool _deafened;
    internal bool Deafened
    {
        get => _deafened;
        set { _deafened = value; if (_audio is not null) _audio.Deafened = value; }
    }

    internal void ReopenAudio(bool capture) => _audio?.Reopen(capture);

    /// <summary>How loud each person plays here by user id (their User Volume, 0 when muted for me); unset, everyone at 1.</summary>
    internal Func<string, float>? UserGain { get; init; }

    /// <summary>(kind, data): "state" {state} and "speaking" {userId, speaking}.</summary>
    internal event Action<string, object>? Changed;

    /// <summary>A Go Live stream being watched: its sound plays, nothing is captured or sent.</summary>
    internal bool Listening => _listening;

    /// <summary>
    /// channelId is the call's channel, or a Go Live stream's (its rtc_channel_id, else rtc_server_id - 1): Discord's client
    /// identifies with it and it is the DAVE group. screen: this client's own Go Live, whose video Discord's client calls
    /// "screen" (MediaEngineStore.getVideoStreamParameters for the stream context), and whose signaling goes to app.log.
    /// </summary>
    internal VoiceConnection(string endpoint, string serverId, string channelId, string userId, string sessionId, string token, bool listening = false, int bitrate = 64000, bool screen = false)
    {
        _bitrate = Math.Clamp(bitrate, 8000, 384000);
        _endpoint = endpoint;
        _serverId = serverId;
        _channelId = channelId;
        _videoType = screen ? "screen" : "video";
        _screen = screen;
        _listening = listening;
        _groupId = ulong.Parse(channelId, System.Globalization.CultureInfo.InvariantCulture);
        _userId = userId;
        _sessionId = sessionId;
        _token = token;
    }

    internal void Start() => _ = Task.Run(RunAsync);

    private async Task RunAsync()
    {
        var resume = false;
        for (var attempt = 0; !_stop.IsCancellationRequested && attempt < 5; attempt++)
        {
            Changed?.Invoke("state", new { state = "connecting" });
            var began = Environment.TickCount64;
            string outcome;
            try { outcome = await ConnectOnceAsync(resume).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Anything a connection didn't expect ended it without a word (a stream that never came up said nothing).
                AppLog.Info($"Discord {(_screen ? "video: your stream's" : _listening ? "video: the watched stream's" : "voice:")} connection failed ({ex.GetType().Name}: {ex.Message}).");
                outcome = "retry";
            }
            if (outcome == "ended" || _stop.IsCancellationRequested) break;
            // A drop after a minute or more of call is a fresh blip: the budget of five tries starts over.
            if (Environment.TickCount64 - began > 60_000) attempt = -1;
            resume = outcome == "resume";
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, 1 + attempt)), _stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
        Changed?.Invoke("state", new { state = "disconnected" });
    }

    private async Task<string> ConnectOnceAsync(bool resume)
    {
        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("User-Agent", DiscordRest.UserAgent);
        _ws = ws;
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        try
        {
            await ws.ConnectAsync(new Uri($"wss://{_endpoint.Replace(":443", "", StringComparison.Ordinal)}/?v=8"), connection.Token).ConfigureAwait(false);
            if (_screen) AppLog.Info("Discord video: your stream's connection reached its server.");
            while (ws.State == WebSocketState.Open)
            {
                var (text, binary) = await ReceiveAsync(ws, connection.Token).ConfigureAwait(false);
                if (text is null && binary is null) break;
                if (binary is not null) OnBinary(binary);
                else await OnTextAsync(text!, resume, ws, connection.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return "ended"; }
        catch (Exception ex) when (ex is WebSocketException or IOException or JsonException or TimeoutException or OperationCanceledException)
        {
            AppLog.Info("Discord voice: connection dropped (" + ex.GetType().Name + ").");
        }
        finally { await connection.CancelAsync().ConfigureAwait(false); _ws = null; }
        var code = (int?)ws.CloseStatus;
        if (code is not null) AppLog.Info($"Discord voice: gateway closed {code}.");
        return code switch
        {
            4014 or 4021 or 4022 or 4004 or 4011 or 4016 or 4017 => "ended",
            4006 or 4009 => "new",
            // Resuming keeps the UDP leg as it is: one that never got its key (IP discovery failed) would leave the call
            // connected and silent, so that one starts over.
            _ => _transport is { Started: true } ? "resume" : "new",
        };
    }

    private async Task OnTextAsync(string text, bool resume, ClientWebSocket ws, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        if (root.TryGetProperty("seq", out var seq) && seq.ValueKind == JsonValueKind.Number) _seqAck = seq.GetInt32();
        var op = root.GetProperty("op").GetInt32();
        var d = root.TryGetProperty("d", out var data) ? data : default;
        switch (op)
        {
            case 8:
                _ = HeartbeatAsync(ws, d.GetProperty("heartbeat_interval").GetDouble(), ct);
                if (resume && _transport is not null)
                    await SendJsonAsync(7, new { server_id = _serverId, session_id = _sessionId, token = _token, seq_ack = _seqAck }).ConfigureAwait(false);
                else
                    await SendJsonAsync(0, Identify(_serverId, _channelId, _userId, _sessionId, _token, _videoType, DaveSession.MaxVersion)).ConfigureAwait(false);
                break;
            case 6: _acked = true; break;
            case 2: await OnReadyAsync(d, ct).ConfigureAwait(false); break;
            case 4: OnSessionDescription(d); break;
            case 14: UseCodec(d); break;
            case 9: Changed?.Invoke("state", new { state = "connected" }); break;
            case 5:
                if (d.TryGetProperty("user_id", out var su) && d.TryGetProperty("ssrc", out var ss))
                    lock (_gate) _ssrcUsers[ss.GetUInt32()] = su.GetString() ?? "";
                break;
            case 11:
                foreach (var id in d.GetProperty("user_ids").EnumerateArray())
                    if (id.GetString() is { } user) _dave?.AddUser(user);
                if (_screen) AppLog.Info($"Discord video: {d.GetProperty("user_ids").GetArrayLength()} joined your stream's connection.");
                break;
            // Client flags: someone else with CLIPS_ENABLED (1) records the call, which Discord tells everyone in it.
            case 18:
                if (d.TryGetProperty("user_id", out var flagged) && flagged.GetString() != _userId && d.TryGetProperty("flags", out var flags) && flags.TryGetInt32(out var bits) && (bits & 1) != 0)
                    Changed?.Invoke("clips", new { userId = flagged.GetString() });
                break;
            case 13:
                if (d.GetProperty("user_id").GetString() is { } gone) Forget(gone);
                if (_screen) AppLog.Info("Discord video: someone left your stream's connection.");
                break;
            case 15 when _screen:
                AppLog.Info("Discord video: your stream's viewers want " + d.GetRawText() + ".");
                break;
            case 12: OnVideo(d); break;
            case 21: _dave?.OnPrepareTransition(d.GetProperty("transition_id").GetUInt16(), d.GetProperty("protocol_version").GetUInt16()); break;
            case 22: _dave?.OnExecuteTransition(d.GetProperty("transition_id").GetUInt16()); break;
            case 24: _dave?.OnPrepareEpoch(d.GetProperty("epoch").GetUInt64(), d.GetProperty("protocol_version").GetUInt16()); break;
        }
    }

    // Binary DAVE messages from the server: [seq u16][op u8][payload].
    private void OnBinary(byte[] message)
    {
        if (message.Length < 3 || _dave is null) return;
        _seqAck = BinaryPrimitives.ReadUInt16BigEndian(message);
        var payload = message.AsSpan(3);
        switch (message[2])
        {
            case 25: _dave.OnExternalSender(payload); break;
            case 27: _dave.OnProposals(payload); break;
            case 29 when payload.Length > 2: _dave.OnAnnounceCommit(BinaryPrimitives.ReadUInt16BigEndian(payload), payload[2..]); break;
            case 30 when payload.Length > 2: _dave.OnWelcome(BinaryPrimitives.ReadUInt16BigEndian(payload), payload[2..]); break;
        }
    }

    private async Task OnReadyAsync(JsonElement d, CancellationToken ct)
    {
        _ssrc = d.GetProperty("ssrc").GetUInt32();
        if (d.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array && streams.GetArrayLength() > 0)
        {
            var stream = streams[0];
            _videoSsrc = stream.TryGetProperty("ssrc", out var vs) ? vs.GetUInt32() : 0;
            _rtxSsrc = stream.TryGetProperty("rtx_ssrc", out var rs) ? rs.GetUInt32() : 0;
            _streamType = stream.TryGetProperty("type", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() : null;
        }
        if (_screen) AppLog.Info("Discord video: your stream's connection is ready (streams " + (d.TryGetProperty("streams", out var announced) ? announced.GetRawText() : "none") + ").");
        var modes = d.GetProperty("modes").EnumerateArray().Select(m => m.GetString()).ToList();
        var mode = modes.Contains(VoiceTransport.Aes) ? VoiceTransport.Aes : VoiceTransport.XChaCha;
        _transport?.Dispose();
        _transport = new VoiceTransport(d.GetProperty("ip").GetString()!, d.GetProperty("port").GetInt32(), _ssrc);
        _transport.Received += OnPacket;
        _transport.RtcpReceived += OnRtcp;
        _videoTimer ??= new Timer(_ => PollVideo(), null, 20, 20);
        var (address, port) = await _transport.DiscoverAsync(ct).ConfigureAwait(false);
        if (_dave is null)
        {
            _dave = new DaveSession(_userId, _groupId, (op, payload) => _ = SendBinaryAsync(op, payload), (op, body) => _ = SendJsonAsync(op, body)) { Verbose = _screen || _listening };
            // New keys (a viewer joined or left): a key frame under them at once, as viewers can't decode the frames before.
            if (_screen) _dave.OwnKeysChanged += () => KeyframeRequested?.Invoke();
        }
        _dave.AssignSsrc(_ssrc);
        if (_videoSsrc != 0) _dave.AssignSsrc(_videoSsrc, _hevc ? Dave.CodecH265 : Dave.CodecH264);
        await SendJsonAsync(1, new
        {
            protocol = "udp",
            data = new { address, port, mode },
            codecs = new object[]
            {
                new { name = "opus", type = "audio", priority = 1000, payload_type = 120 },
                // H.265 ahead of H.264, as Discord's apps list them: the server picks it when everyone can play it. Exo sends
                // it only on Go Live (Exo Capture's HEVC; the camera is H.264) and plays it where its page decodes it in hardware.
                new { name = "H265", type = "video", priority = 1000, payload_type = H265Rtp.PayloadType, rtx_payload_type = H265Rtp.RtxPayloadType, encode = _screen && EncodesH265, decode = DecodesH265 },
                new { name = "H264", type = "video", priority = 2000, payload_type = H264Rtp.PayloadType, rtx_payload_type = H264Rtp.RtxPayloadType, encode = true, decode = true },
            },
        }).ConfigureAwait(false);
    }

    private void OnSessionDescription(JsonElement d)
    {
        var key = d.GetProperty("secret_key").EnumerateArray().Select(b => (byte)b.GetInt32()).ToArray();
        _transport!.Start(d.GetProperty("mode").GetString() ?? VoiceTransport.Aes, key);
        var version = d.TryGetProperty("dave_protocol_version", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetUInt16() : (ushort)0;
        if (_screen || _listening)
            AppLog.Info($"Discord video: {(_screen ? "your stream's" : "the watched stream's")} connection is up (video codec {(d.TryGetProperty("video_codec", out var codec) ? codec.GetString() : "none")}, DAVE {version}).");
        UseCodec(d);
        _dave!.OnSessionDescription(version);
        if (_audio is null)
        {
            _encoder = Opus.NewEncoder(_bitrate);
            _audio = new VoiceAudio { Deafened = _deafened, EvensVoices = !_listening };
            if (!_listening) _audio.Captured += SendCaptured;
            _audio.Start(capture: !_listening);
            // A recording or replay running now takes its voice from this call's chain instead of cleaning the mic again.
            if (!_listening && _audio is { } callAudio) RecordedVoice.Call(_chain, () => callAudio.FrameAt);
        }
        // All the video in the call, at full quality (Discord's media sink wants).
        _ = SendJsonAsync(15, new { any = 100 });
        Changed?.Invoke("state", new { state = "connected", e2ee = version > 0 });
        AppLog.Info($"Discord voice: connected ({(version > 0 ? "end-to-end encrypted" : "transport encrypted")}).");
    }

    // ---- sending ----

    // On Exo's voice thread (VoiceAudio drops a frame that fails, logged once).
    private unsafe void SendCaptured(float[] frame)
    {
        if (_transport is not { Started: true } || _dave is null || _encoder == 0 || _audio is not { } audio) return;
        var started = VoiceAudio.QpcNow();
        var processing = VoiceProcessing.Current;
        bool voiced;
        // The microphone as it came, before anything cleans or levels it: a mic that sends nothing (unplugged in all but
        // name, muted on its own button, its gain knob all the way down) is said in the voice panel, as Discord does.
        if (_micSilence.Hear(frame, Muted) is { } silent) Changed?.Invoke("micSilent", new { silent });
        // Only My Voice stopped holding back (it held back the user's own speech): said once, in the voice panel.
        if (!_toldMyVoiceStopped && _chain.MyVoiceStopped)
        {
            _toldMyVoiceStopped = true;
            Changed?.Invoke("myVoiceStopped", new { });
        }
        if (Muted)
        {
            // A recording that takes its voice from this chain (RecordedVoice) keeps hearing it while Discord doesn't.
            if (_chain.Cleaned is not null) _chain.Process(frame, processing, audio.MicId, audio.SystemGain);
            _chain.Mute();
            voiced = false;
        }
        else
        {
            voiced = Transmit(_chain.Process(frame, processing, audio.MicId, audio.SystemGain), processing);
            if (_chain.Stages != _loggedStages)
            {
                _loggedStages = _chain.Stages;
                AppLog.Info($"Discord voice: microphone: {_loggedStages}.");
            }
            // Once a minute: how much the noise gate let through, so a voice it holds back shows in app.log.
            if (++_gateFrames % 3000 == 0)
            {
                var (open, shut, loudest) = _chain.TakeGateStats();
                AppLog.Info(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"Discord voice: noise gate let {open} frames through and held {shut} shut (the loudest held shut at {loudest:0.0} dBFS)."));
            }
        }
        var processed = VoiceAudio.QpcNow();
        if (!voiced)
        {
            if (!_speaking) return;
            // Five frames of Opus silence before stopping, as Discord asks, then speaking off.
            if (_silence++ < 5) { if (SendFrame([0xF8, 0xFF, 0xFE])) MeasureSend(audio, started, processed); return; }
            _speaking = false;
            _ = SendJsonAsync(5, new { speaking = 0, delay = 0, ssrc = _ssrc });
            Changed?.Invoke("speaking", new { userId = _userId, speaking = false });
            return;
        }
        _silence = 0;
        if (!_speaking)
        {
            _speaking = true;
            _ = SendJsonAsync(5, new { speaking = 1, delay = 0, ssrc = _ssrc });
            Changed?.Invoke("speaking", new { userId = _userId, speaking = true });
        }
        if (_lossWanted != _lossSet) Opus.SetExpectedLoss(_encoder, _lossSet = _lossWanted);
        // The gate just opened: the 100 ms before it go first, as Discord's client sends them.
        foreach (var lead in _chain.TakeLead()) Encode(lead);
        if (Encode(frame)) MeasureSend(audio, started, processed);
    }

    // A frame of the microphone through Opus and out; whether it went.
    private unsafe bool Encode(float[] frame)
    {
        Span<byte> opus = stackalloc byte[1275];
        int length;
        fixed (float* pcm = frame)
        fixed (byte* o = opus)
            length = Opus.EncodeFloat(_encoder, pcm, Opus.FrameSamples, o, opus.Length);
        return length > 0 && SendFrame(opus[..length]);
    }

    // A frame of Opus through DAVE onto the socket; whether it went.
    private bool SendFrame(ReadOnlySpan<byte> opus)
    {
        Span<byte> sealedFrame = stackalloc byte[opus.Length + 64];
        var length = _dave!.EncryptFrame(_ssrc, opus, sealedFrame);
        if (length <= 0) return false;
        _transport!.Send(sealedFrame[..length]);
        return true;
    }

    // The microphone's send path, measured (VoiceLatency: the voice's age at the socket and each stage's part), the gap
    // between packets, and the CPU Exo and the call's threads used (the share of one core, so a call's cost can be read
    // in a game, and Exo's busiest threads by name); written to app.log each minute something was sent. The minute's
    // line is put together off the voice thread: reading every thread takes a few milliseconds.
    private readonly VoiceLatency _latency = new();
    private readonly ThreadUsage _threads = new();
    private long _gapMax, _lastSend, _statsSince;
    private (long Capture, long Voice, long Render) _cpuSince;

    private void MeasureSend(VoiceAudio audio, long started, long processed)
    {
        var now = VoiceAudio.QpcNow();
        _latency.Add(audio.FrameAt, audio.FrameWholeAt, started, processed, now, _chain.DelaySamples);
        if (_lastSend != 0 && now - _lastSend < 1_000_000) _gapMax = Math.Max(_gapMax, now - _lastSend);
        _lastSend = now;
        if (_statsSince == 0)
        {
            _statsSince = now;
            _cpuSince = Cpu(audio);
            _ = Task.Run(() =>
            {
                Interlocked.Exchange(ref _exoSince, ExoCpu());
                _threads.Busiest(); // where the first minute's thread times start
            });
        }
        if (now - _statsSince < 600_000_000) return;
        var cpu = Cpu(audio);
        var wall = now - _statsSince;
        var line = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"Discord voice: sent {_latency.Packets} frames, {_latency}; packets at most {_gapMax / 10_000.0:0.0} ms apart, {audio.LateDrops} late microphone packets dropped, {audio.BehindDrops} frames dropped behind; CPU ");
        var threads = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{CoreShare(cpu.Capture - _cpuSince.Capture, wall):0.0}% microphone thread, {CoreShare(cpu.Voice - _cpuSince.Voice, wall):0.0}% voice thread, {CoreShare(cpu.Render - _cpuSince.Render, wall):0.0}% speaker thread");
        _ = Task.Run(() =>
        {
            var exo = ExoCpu();
            var since = Interlocked.Exchange(ref _exoSince, exo);
            AppLog.Info(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{line}{CoreShare(exo - since, wall):0.0}% of a core for Exo, {threads}; busiest threads: {_threads.Busiest()}."));
        });
        _latency.Reset();
        _gapMax = 0;
        _statsSince = now;
        _cpuSince = cpu;
    }

    private long _exoSince;

    private static long ExoCpu()
    {
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        return self.TotalProcessorTime.Ticks;
    }

    private static (long Capture, long Voice, long Render) Cpu(VoiceAudio audio) =>
        (Volatile.Read(ref audio.CaptureCpu), Volatile.Read(ref audio.VoiceCpu), Volatile.Read(ref audio.RenderCpu));

    /// <summary>CPU time over wall time (both 100 ns) as a share of one core in percent; 0 when a thread reopened and its count restarted.</summary>
    internal static double CoreShare(long cpu, long wall) => wall <= 0 || cpu <= 0 ? 0 : 100.0 * cpu / wall;

    // Voice Activity (the chain's gate, at the chosen sensitivity) or Push to Talk (the key, and its release delay
    // after); a Push to Mute key held shuts either.
    private bool Transmit(bool gateOpen, VoiceProcessing processing)
    {
        if (Keybinds.Held(Keybinds.PushToMute)) return false;
        if (!processing.PushToTalk) return gateOpen;
        var open = _ptt.Open(processing.PttDelay, out var changed);
        if (changed) Changed?.Invoke("ptt", new { on = _ptt.Held });
        return open;
    }

    // ---- receiving ----

    // Watching a stream: each kind of packet the first time it arrives, so a stream that never shows says why.
    private readonly HashSet<int> _seenTypes = [];

    private void OnPacket(VoiceTransport.Packet packet)
    {
        if (_listening)
            lock (_seenTypes)
                if (_seenTypes.Add(packet.PayloadType))
                    AppLog.Info($"Discord video: the watched stream's first packet of type {packet.PayloadType}{packet.PayloadType switch { H264Rtp.PayloadType => " (H.264 video)", H265Rtp.PayloadType => " (H.265 video)", 120 => " (Opus sound)", H264Rtp.RtxPayloadType or H265Rtp.RtxPayloadType => " (resent video)", _ => " (not one Exo takes)" }}" +
                        $" on SSRC {packet.Ssrc}, {packet.Payload.Length} bytes, marker {packet.Marker}, extensions {VoiceTransport.Elements(packet.Extension.Span)}.");
        if (packet.TransportSequence >= 0) Arrived(packet);
        if (packet.PayloadType is H264Rtp.PayloadType or H265Rtp.PayloadType) OnVideoPacket(packet.Ssrc, packet.Sequence, packet, hevc: packet.PayloadType == H265Rtp.PayloadType);
        else if (packet.PayloadType is H264Rtp.RtxPayloadType or H265Rtp.RtxPayloadType) OnResent(packet);
        else if (packet.PayloadType == 120) OnAudio(packet.Ssrc, packet.Payload, packet.Sequence);
    }

    private unsafe void OnAudio(uint ssrc, ReadOnlyMemory<byte> payload, ushort sequence)
    {
        string? user;
        lock (_gate) _ssrcUsers.TryGetValue(ssrc, out user);
        if (user is null || _dave is null || _audio is null) return;
        var gain = UserGain?.Invoke(user) ?? 1f;
        var data = payload.Span;
        if (data.Length == 3 && data[0] == 0xF8 && data[1] == 0xFF && data[2] == 0xFE)
        {
            lock (_decodeGate)
            {
                _lastAudioSequence[ssrc] = sequence;
                if (_cleaners.TryGetValue(ssrc, out var draining)) draining.Drain();
            }
            return;
        }
        Span<byte> plain = stackalloc byte[data.Length + 64];
        var length = _dave.DecryptFrame(user, data, plain);
        if (length <= 0) return;
        // A speaker's decoder is used and freed (they left, the call closed) under one lock, never mid-decode.
        lock (_decodeGate)
        {
            if (_stop.IsCancellationRequested) return;
            var missing = _lastAudioSequence.TryGetValue(ssrc, out var previous) ? Missing(previous, sequence) : 0;
            if (missing < 0) return; // late or repeated: already played around it
            _lastAudioSequence[ssrc] = sequence;
            if (!_decoders.TryGetValue(ssrc, out var decoder)) _decoders[ssrc] = decoder = Opus.NewDecoder();
            // A lost packet or two: the frame just before this one comes back from this packet's in-band FEC,
            // any earlier ones from Opus's concealment, so a drop is smooth instead of a click or a gap.
            int samples;
            if (missing is > 0 and <= 5)
            {
                for (var gap = 0; gap < missing - 1; gap++)
                {
                    var concealed = _audio.Rent();
                    fixed (float* o = concealed)
                        if (Opus.DecodeFloat(decoder, null, 0, o, Opus.FrameSamples, 0) > 0) Play(ssrc, concealed, gain);
                }
                var recovered = _audio.Rent();
                fixed (byte* p = plain)
                fixed (float* o = recovered)
                    samples = Opus.DecodeFloat(decoder, p, length, o, Opus.FrameSamples, 1);
                if (samples > 0) Play(ssrc, recovered, gain);
            }
            var pcm = _audio.Rent();
            fixed (byte* p = plain)
            fixed (float* o = pcm)
                samples = Opus.DecodeFloat(decoder, p, length, o, Opus.FrameSamples, 0);
            if (samples <= 0) return;
            Play(ssrc, pcm, gain);
        }
        var now = Environment.TickCount64;
        bool started;
        lock (_gate)
        {
            started = !_heardAt.TryGetValue(user, out var last) || now - last > 400;
            _heardAt[user] = now;
        }
        if (started) Changed?.Invoke("speaking", new { userId = user, speaking = true });
    }

    /// <summary>
    /// How many of a speaker's packets went missing before this one; -1 when it is a repeat or up to a second late
    /// (already played around). A jump further back is the sender counting from somewhere new (a reconnect, a
    /// restarted encoder), not a late packet: it plays, and the count follows it.
    /// </summary>
    internal static int Missing(ushort previous, ushort sequence)
    {
        var ahead = (ushort)(sequence - previous);
        if (ahead == 0) return -1;
        if (ahead < 0x8000) return ahead - 1;
        return 0x10000 - ahead <= 50 ? -1 : 0;
    }

    // ---- video ----

    /// <summary>(op 12) Who sends video on which SSRC; no streams (or SSRC 0) means their video stopped.</summary>
    private void OnVideo(JsonElement d)
    {
        if (_listening) AppLog.Info("Discord video: the watched stream announces " + d.GetRawText() + ".");
        if (d.TryGetProperty("user_id", out var u) is false || u.GetString() is not { } user) return;
        var ssrc = d.TryGetProperty("video_ssrc", out var vs) && vs.ValueKind == JsonValueKind.Number ? vs.GetUInt32() : 0;
        var active = ssrc != 0 && (!d.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array ||
            streams.EnumerateArray().Any(s => !s.TryGetProperty("active", out var a) || a.ValueKind != JsonValueKind.False));
        var rtx = d.TryGetProperty("rtx_ssrc", out var rs) && rs.ValueKind == JsonValueKind.Number ? rs.GetUInt32() : 0;
        lock (_gate)
        {
            foreach (var old in _videoUsers.Where(p => p.Value == user).Select(p => p.Key).ToList())
            {
                _videoUsers.Remove(old);
                _receivers.Remove(old);
                _orientation.Remove(old);
                foreach (var r in _rtxOf.Where(p => p.Value == old).Select(p => p.Key).ToList()) _rtxOf.Remove(r);
            }
            if (active) { _videoUsers[ssrc] = user; if (rtx != 0) _rtxOf[rtx] = ssrc; }
            if (d.TryGetProperty("audio_ssrc", out var asr) && asr.ValueKind == JsonValueKind.Number && asr.GetUInt32() is > 0 and var audio) _ssrcUsers[audio] = user;
        }
        Changed?.Invoke("video", new { userId = user, active });
    }

    // One video packet (or a resent one, with its original sequence number) into its sender's jitter buffer.
    private void OnVideoPacket(uint ssrc, ushort sequence, VoiceTransport.Packet packet, int skip = 0, bool hevc = false)
    {
        H264Rtp.H264Receiver? receiver;
        lock (_gate)
        {
            if (!_videoUsers.TryGetValue(ssrc, out var user)) return;
            // A new sender, or one the server moved to the other codec: a buffer for what it sends now.
            if (!_receivers.TryGetValue(ssrc, out receiver) || receiver.Hevc != hevc) _receivers[ssrc] = receiver = NewReceiver(ssrc, user, hevc);
            // Sent on a keyframe's last packet and whenever it changes (a phone turned): kept until the next.
            if (packet.Orientation >= 0) _orientation[ssrc] = packet.Orientation;
            receiver.Add(sequence, packet.Timestamp, packet.Marker, packet.Payload.Span[skip..], Environment.TickCount64);
        }
    }

    // RFC 4588: an RTX packet is the original sequence number, then the original payload.
    private void OnResent(VoiceTransport.Packet packet)
    {
        uint ssrc;
        lock (_gate) if (!_rtxOf.TryGetValue(packet.Ssrc, out ssrc)) return;
        if (packet.Payload.Length < 3) return;
        OnVideoPacket(ssrc, BinaryPrimitives.ReadUInt16BigEndian(packet.Payload.Span), packet, skip: 2, hevc: packet.PayloadType == H265Rtp.RtxPayloadType);
    }

    private H264Rtp.H264Receiver NewReceiver(uint ssrc, string user, bool hevc)
    {
        var receiver = new H264Rtp.H264Receiver(hevc);
        receiver.Nack += lost => _transport?.SendRtcp(Rtcp.Nack(_ssrc, ssrc, lost));
        receiver.KeyframeNeeded += () => _transport?.SendRtcp(Rtcp.Pli(_ssrc, ssrc));
        receiver.Frame += (timestamp, frame) =>
        {
            Interlocked.Increment(ref _assembled);
            if (_dave is null) return;
            var plain = new byte[frame.Length + 64];
            var length = _dave.DecryptFrame(user, frame, plain, video: true);
            // Not decrypted (a key change under way): the frames after it can't be shown until a keyframe, which is
            // asked for at most twice a second, not on every frame that fails.
            if (length <= 0) { Interlocked.Increment(ref _undecrypted); receiver.Lost(Environment.TickCount64); return; }
            Interlocked.Increment(ref _decrypted);
            var data = plain.AsSpan(0, length).ToArray();
            var key = hevc ? H265Rtp.IsKeyframe(data) : H264Rtp.IsKeyframe(data);
            if (_listening && !_describedWatched && key)
            {
                _describedWatched = true;
                AppLog.Info(hevc
                    ? $"Discord video: the watched stream's first key frame (H.265): sealed {H265Rtp.Describe(frame)}, open {H265Rtp.Describe(data)}."
                    : $"Discord video: the watched stream's first key frame: sealed {H264Rtp.Describe(frame)}, open {H264Rtp.Describe(data)}, SPS {H264Rtp.SpsHex(data)}.");
            }
            int orientation;
            lock (_gate) orientation = _orientation.GetValueOrDefault(ssrc);
            Frame?.Invoke(user, timestamp, key, data, orientation, hevc);
        };
        return receiver;
    }

    // ---- receiving: telling the server what arrives, so it sends the stream at the quality this connection can take ----

    // Arrival times (microseconds) by unwrapped transport sequence number, not yet reported; what came in this second.
    private readonly SortedDictionary<long, long> _arrivals = [];
    private long _lastTransport = -1, _nextReported = -1, _feedbackAt, _rembAt, _received, _receivedSince, _receivedPerSecond;
    // The smoothed share of the media's packets lost on the way in (0-1): over 5 % it reins REMB in, so the
    // server eases off a link that is losing instead of being told it can take twice what arrived.
    private double _lossShare;
    private uint _media;
    private byte _feedbackCount;
    private int _secondsLogged;
    private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();

    private void Arrived(VoiceTransport.Packet packet)
    {
        var micros = Clock.Elapsed.Ticks / 10;
        lock (_arrivals)
        {
            var sequence = (long)packet.TransportSequence;
            if (_lastTransport >= 0)
            {
                // Unwrapped to the nearest of its 16-bit wraps around the last one.
                sequence += _lastTransport & ~0xFFFFL;
                if (sequence < _lastTransport - 0x8000) sequence += 0x10000;
                else if (sequence > _lastTransport + 0x8000) sequence -= 0x10000;
            }
            if (sequence > _lastTransport) _lastTransport = sequence;
            if (_nextReported < 0) _nextReported = sequence;
            if (sequence >= _nextReported) _arrivals.TryAdd(sequence, micros);
            if (packet.PayloadType is H264Rtp.PayloadType or H265Rtp.PayloadType) _media = packet.Ssrc;
            _received += packet.Payload.Length + 40;
        }
    }

    // Every 100 ms: when each packet since the last report arrived (transport-cc); every second: the most this
    // connection takes (REMB), twice what arrived and at least 10 Mbit/s, so a stream rises to the quality it was sent at.
    private void ReportArrivals(long now)
    {
        byte[]? feedback = null, remb = null;
        lock (_arrivals)
        {
            if (_arrivals.Count > 0 && now - _feedbackAt >= 100)
            {
                _feedbackAt = now;
                var from = _nextReported;
                var to = Math.Min(_arrivals.Keys.Max(), from + 2000);
                var times = new List<long?>();
                var lost = 0;
                for (var s = from; s <= to; s++)
                {
                    if (_arrivals.Remove(s, out var at)) times.Add(at);
                    else { times.Add(null); lost++; }
                }
                foreach (var stale in _arrivals.Keys.Where(k => k <= to).ToList()) _arrivals.Remove(stale);
                _nextReported = to + 1;
                // Missing in the window counts as lost; late ones still arriving were only jitter to it.
                _lossShare += ((double)lost / times.Count - _lossShare) * 0.3;
                feedback = Rtcp.TransportFeedback(_ssrc, _media, (ushort)from, _feedbackCount++, times);
            }
            if (now - _rembAt >= 1000)
            {
                _receivedPerSecond = _rembAt == 0 ? 0 : (_received - _receivedSince) * 8 * 1000 / Math.Max(1, now - _rembAt);
                _receivedSince = _received;
                _rembAt = now;
                // Losing over 5 %: tell the server a little under what arrived, so it finds the rate the link keeps.
                // Under it: twice what arrived and at least 10 Mbit/s, so a stream rises to the quality it was sent at.
                var takes = _lossShare > 0.05 ? Math.Max(1_500_000, (long)(_receivedPerSecond * 0.9)) : Math.Max(10_000_000, _receivedPerSecond * 2);
                if (_media != 0) remb = Rtcp.Remb(_ssrc, takes, [_media]);
                // What a watched stream or camera comes in at, once a minute, so its quality can be read back.
                if (_media != 0 && ++_secondsLogged % 60 == 0) AppLog.Info($"Discord video: receiving {_receivedPerSecond / 1000} kbit/s.");
            }
        }
        if (feedback is not null) _transport?.SendRtcp(feedback);
        if (remb is not null) _transport?.SendRtcp(remb);
    }

    // On a timer thread, which has no handler of its own.
    // A watched stream's frames through each stage (assembled from packets, decrypted), told at 5 s and every 30 s after:
    // a stream that arrives but never shows says where its frames stop.
    private int _assembled, _decrypted, _undecrypted;
    private long _stagesAt;

    private void PollVideo()
    {
        var now = Environment.TickCount64;
        if (_listening && _media != 0)
        {
            if (_stagesAt == 0) _stagesAt = now + 5000;
            else if (now >= _stagesAt)
            {
                _stagesAt = now + 30_000;
                AppLog.Info($"Discord video: the watched stream: {_assembled} frames from its packets, {_decrypted} decrypted, {_undecrypted} not.");
            }
        }
        if (_sent.Due(now) is { } line) AppLog.Info("Discord video: " + line + (_rate is { } rate ? $" Sending at {rate.Rate / 1000} kbit/s." : ""));
        try { ReportArrivals(now); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { AppLog.Debug("Discord video: arrival report failed (" + ex.GetType().Name + ")."); }
        try { lock (_gate) foreach (var receiver in _receivers.Values) receiver.Poll(now); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { AppLog.Debug("Discord video: poll failed (" + ex.GetType().Name + ")."); }
    }

    /// <summary>Who sends video in the call now.</summary>
    internal string[] VideoUsers
    {
        get { lock (_gate) return [.. _videoUsers.Values.Distinct()]; }
    }

    /// <summary>Asks every camera in the call for a keyframe (the page starts showing them again).</summary>
    internal void RequestKeyframes()
    {
        uint[] ssrcs;
        lock (_gate) ssrcs = [.. _videoUsers.Keys];
        foreach (var ssrc in ssrcs) _transport?.SendRtcp(Rtcp.Pli(_ssrc, ssrc));
    }

    // The server about this client's own video: lost packets to resend (on RTX), or a keyframe wanted.
    // Opus's expected loss, which sets how much of each packet carries the one before (in-band FEC): from
    // Discord's reports on what reached it of this client's voice, up at once when packets go missing and back
    // down a point a report, within 2 to 30 %. Set on the capture thread, before the next frame is encoded.
    private volatile int _lossWanted = 5;
    private int _lossSet = 5;

    private void OnRtcp(byte[] packet)
    {
        if (_ssrc != 0 && Rtcp.LossFor(packet, _ssrc) is { } share)
        {
            var target = Math.Clamp((int)Math.Ceiling(share * 1.5), 2, 30);
            _lossWanted = target >= _lossWanted ? target : _lossWanted - 1;
        }
        if (_videoSsrc == 0 || _transport is null) return;
        // What the server says of your stream's video, early and then now and then: whether its packets reach it at all.
        if (Interlocked.Increment(ref _sent.Rtcp) is var seen && _screen && (seen <= 6 || seen % 50 == 0))
            AppLog.Info($"Discord video: the server's RTCP on your stream ({(ushort)(_videoSequence - 1)} last sent): {Rtcp.Describe(packet, _videoSsrc)}.");
        var (lost, keyframe) = Rtcp.Read(packet, _videoSsrc);
        // Under the send lock: the history is written as frames go out, and a half-written entry would resend the wrong packet.
        Interlocked.Add(ref _sent.Nacked, lost.Count);
        if (lost.Count > 0 && _rtxSsrc != 0)
        {
            var now = Environment.TickCount64;
            lock (_videoSendGate)
                foreach (var sequence in lost)
                    if (_sent.Resend(sequence, now) is { } resend)
                    {
                        Pace(new(_rtxSsrc, _hevc ? H265Rtp.RtxPayloadType : H264Rtp.RtxPayloadType, resend.Marker, _rtxSequence++, resend.Timestamp, resend.Payload), resend: true);
                        Interlocked.Increment(ref _sent.Resent);
                    }
        }
        if (keyframe) { Interlocked.Increment(ref _sent.KeyframeAsks); KeyframeRequested?.Invoke(); }
        if (_rate is { } rate)
        {
            if (Rtcp.RembBits(packet) is { } remb) rate.Remb(remb);
            if (Rtcp.LossFor(packet, _videoSsrc) is { } loss) rate.Loss(loss / 100);
            if (rate.Next(Environment.TickCount64) is { } next)
            {
                _paceRate = next;
                RateWanted?.Invoke(next);
            }
        }
    }

    // ---- sending video: paced, as libwebrtc's pacer sends it ----

    // A key frame is a hundred packets and more. Sent back to back they overran the uplink and Discord's server (a third
    // of the stream lost once a viewer joined); the pacer sends them over a few milliseconds each, at 2.5 times the
    // stream's rate as libwebrtc's does, resends ahead of new video. Calls raise Windows' timer to 1 ms (LiveAudio), so
    // its 1 ms naps are about that.
    private readonly record struct Outgoing(uint Ssrc, int Type, bool Marker, ushort Sequence, uint Timestamp, byte[] Payload);
    private readonly Lock _paceGate = new();
    private readonly Queue<Outgoing> _video = new(), _resends = new();
    private readonly AutoResetEvent _paceWake = new(false);
    private Thread? _pacer;
    private volatile int _paceRate = 8_000_000;
    private StreamRate? _rate;

    /// <summary>Go Live: from now on the stream's rate follows what Discord's server reports (start, at most most).</summary>
    internal void FollowServer(int start, int most)
    {
        _rate = new StreamRate(start, most);
        _paceRate = start;
    }

    /// <summary>The encoder should change rate (bits per second): the server takes less, or more again.</summary>
    internal event Action<int>? RateWanted;

    private void Pace(Outgoing packet, bool resend = false)
    {
        lock (_paceGate)
        {
            (resend ? _resends : _video).Enqueue(packet);
            if (_pacer is null)
            {
                _pacer = new Thread(PaceLoop) { IsBackground = true, Name = "Exo video pacer", Priority = ThreadPriority.AboveNormal };
                _pacer.Start();
            }
        }
        _paceWake.Set();
    }

    private void PaceLoop()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var last = clock.Elapsed;
        double budget = 0;
        while (!_stop.IsCancellationRequested)
        {
            Outgoing packet;
            bool resend, any;
            lock (_paceGate)
            {
                resend = _resends.TryPeek(out packet);
                any = resend || _video.TryPeek(out packet);
            }
            var now = clock.Elapsed;
            var perSecond = Math.Max(_paceRate, 500_000) * 2.5 / 8;
            // At most 20 ms of sending saved up while backlogged (a late nap); none while idle, so a new key frame starts paced.
            budget = Math.Min(budget + (now - last).TotalSeconds * perSecond, any ? perSecond * 0.02 : 2400);
            last = now;
            if (!any) { _paceWake.WaitOne(20); continue; }
            if (budget <= 0) { Thread.Sleep(1); continue; }
            lock (_paceGate) (resend ? _resends : _video).Dequeue();
            _transport?.SendVideo(packet.Ssrc, packet.Type, packet.Marker, packet.Sequence, packet.Timestamp, packet.Payload);
            budget -= packet.Payload.Length + 44; // RTP, extension, tag and nonce, UDP and IP
        }
    }

    /// <summary>The server wants a keyframe of this client's video (someone started watching, or lost one).</summary>
    internal event Action? KeyframeRequested;

    /// <summary>A whole, decrypted H.264 frame (Annex B) from a user: (userId, RTP timestamp, keyframe, data).</summary>
    /// <summary>(user, RTP time, keyframe, Annex B frame, video-orientation byte: bit 2 flip, bits 0-1 quarter turns).</summary>
    internal event Action<string, uint, bool, byte[], int, bool>? Frame;

    /// <summary>Turns this client's own video on (with its size, frame rate and top bit rate) or off (op 12).</summary>
    internal Task SetVideoAsync(int width, int height, int fps, int maxBitrate = 8_000_000) =>
        SendJsonAsync(12, Video(_ssrc, _videoSsrc, _rtxSsrc, _streamType ?? _videoType, width, height, fps, maxBitrate));

    /// <summary>op 0 IDENTIFY as Discord's client sends it (RTCControlSocket.identify): with the channel, and the video's type.</summary>
    internal static object Identify(string serverId, string channelId, string userId, string sessionId, string token, string videoType, ushort daveVersion) => new
    {
        server_id = serverId, channel_id = channelId, user_id = userId, session_id = sessionId, token,
        max_dave_protocol_version = (int)daveVersion,
        video = true,
        streams = new[] { new { type = videoType, rid = "100", quality = 100 } },
    };

    /// <summary>op 12: this client's video on its SSRCs at a size, frame rate and top bit rate, or off (width 0).</summary>
    internal static object Video(uint audio, uint video, uint rtx, string type, int width, int height, int fps, int maxBitrate) => width == 0
        ? new { audio_ssrc = audio, video_ssrc = 0u, rtx_ssrc = 0u, streams = Array.Empty<object>() }
        : new
        {
            audio_ssrc = audio, video_ssrc = video, rtx_ssrc = rtx,
            streams = new object[]
            {
                new
                {
                    type, rid = "100", ssrc = video, active = true, quality = 100, rtx_ssrc = rtx,
                    max_bitrate = maxBitrate, max_framerate = fps, max_resolution = new { type = "fixed", width, height },
                },
            },
        };

    /// <summary>Sends one of this client's frames (Annex B, H.265 when hevc): DAVE, then RTP packets on the video SSRC.</summary>
    internal void SendVideoFrame(byte[] frame, uint timestamp, bool hevc = false)
    {
        if (_transport is not { Started: true } || _dave is null || _videoSsrc == 0) return;
        // Frames in the codec the server no longer wants (Exo Capture is starting over in the new one) can't be played.
        if (hevc != _hevc) return;
        // The SPS as viewers' libwebrtc keeps it, or it rewrites it under DAVE's tag and no key frame decrypts (H.264 only).
        var sent = hevc ? frame : H264Vui.NoReordering(frame);
        var sealedFrame = new byte[sent.Length + 256];
        var length = _dave.EncryptFrame(_videoSsrc, sent, sealedFrame, video: true);
        if (length <= 0) { Interlocked.Increment(ref _sent.Unsealed); return; }
        var packets = hevc ? H265Rtp.Packetize(sealedFrame[..length]) : H264Rtp.Packetize(sealedFrame[..length]);
        Interlocked.Increment(ref _sent.Frames);
        Interlocked.Add(ref _sent.Packets, packets.Count);
        if (_screen && !_describedKeyframe && (hevc ? H265Rtp.IsKeyframe(frame) : H264Rtp.IsKeyframe(frame)))
        {
            _describedKeyframe = true;
            AppLog.Info(hevc
                ? $"Discord video: your stream's first key frame (H.265): {H265Rtp.Describe(frame)}, {length - sent.Length:+0;-0} bytes from DAVE."
                : "Discord video: your stream's first key frame: " + H264Rtp.Describe(frame) + (ReferenceEquals(sent, frame) ? "" : $", SPS rewritten to {H264Rtp.Describe(sent)}") +
                  $", {length - sent.Length:+0;-0} bytes from DAVE, SPS {H264Rtp.SpsHex(sent)}.");
        }
        // One frame at a time: frames arrive on several threads, and a frame's packets must not interleave with another's.
        lock (_videoSendGate)
        for (var i = 0; i < packets.Count; i++)
        {
            var marker = i == packets.Count - 1;
            var sequence = _videoSequence++;
            _sent.Remember(sequence, timestamp, marker, packets[i]);
            Pace(new(_videoSsrc, hevc ? H265Rtp.PayloadType : H264Rtp.PayloadType, marker, sequence, timestamp, packets[i]));
        }
    }

    private bool _describedKeyframe, _describedWatched;

    /// <summary>Exo's page decodes H.265 in hardware (WebCodecs; the page says so once it loads): connections offer to receive it.</summary>
    internal static volatile bool DecodesH265;

    /// <summary>Go Live: Exo Capture can encode HEVC, so the stream's connection offers to send H.265. Set before Start.</summary>
    internal bool EncodesH265 { get; set; }

    // The codec the server picked for this client's video (SESSION_DESCRIPTION, then SESSION_UPDATE when a viewer changes
    // what everyone can play): true for H.265. Only Go Live follows it; a call's camera is always H.264.
    private volatile bool _hevc;

    /// <summary>Go Live: the server picked the stream's codec (true: H.265), at the start and whenever it changes it.</summary>
    internal event Action<bool>? CodecChosen;

    private void UseCodec(JsonElement d)
    {
        if (!_screen || !d.TryGetProperty("video_codec", out var codec) || codec.ValueKind != JsonValueKind.String) return;
        var hevc = codec.GetString() == "H265" && EncodesH265;
        if (hevc != _hevc)
        {
            _hevc = hevc;
            _describedKeyframe = false;
            if (_videoSsrc != 0) _dave?.AssignSsrc(_videoSsrc, hevc ? Dave.CodecH265 : Dave.CodecH264);
        }
        AppLog.Info($"Discord video: your stream's codec is {(hevc ? "H.265" : "H.264")} (the server says {codec.GetString()}).");
        CodecChosen?.Invoke(hevc);
    }

    private StreamAudio? _soundshare;
    private nint _musicEncoder;

    /// <summary>Go Live's sound as music-quality Opus on this stream's audio SSRC: a window's app (its process), or this PC's sound but Exo's (0).</summary>
    internal void StartSoundshare(uint app)
    {
        if (_soundshare is not null || _transport is null || _dave is null) return;
        _musicEncoder = Opus.NewMusicEncoder(256000);
        _soundshare = new StreamAudio(app);
        _soundshare.Captured += frame =>
        {
            if (_musicEncoder == 0 || _transport is not { Started: true } || _dave is null) return;
            try
            {
                if (_lossWanted != _lossSet) Opus.SetExpectedLoss(_musicEncoder, _lossSet = _lossWanted);
                Span<byte> opus = stackalloc byte[1275];
                var length = Opus.Encode(_musicEncoder, frame, opus);
                if (length > 0) SendFrame(opus[..length]);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { AppLog.Debug("Discord video: a stream sound frame failed (" + ex.GetType().Name + ")."); }
        };
        _soundshare.Start();
    }

    /// <summary>Says this client is sending (speaking flags: 1 voice, 2 Go Live).</summary>
    internal Task SpeakingAsync(int flags) => SendJsonAsync(5, new { speaking = flags, delay = 0, ssrc = _ssrc });

    // A decoded frame to the mix (under _decodeGate): through the speaker's own noise suppression when Noise Suppression
    // for Others is on, never for a Go Live stream's sound (its music and game).
    private void Play(uint ssrc, float[] frame, float gain)
    {
        var audio = _audio!;
        // While a game runs it steps aside: a model per voice (a quarter of a core each) beside the game starved the
        // microphone's own thread, and your voice went out late, choppy or not at all.
        if (_listening || gain == 0 || !(App.Services.Settings.Shared.DiscordOthersNoiseSuppression || VoiceProcessing.ExoOn(App.Services.Settings.Shared)) || GameRunning.Now)
        {
            audio.Play(ssrc, frame, gain);
            return;
        }
        if (!_cleaners.TryGetValue(ssrc, out var cleaner))
        {
            _cleaners[ssrc] = cleaner = new InboundCleaner((cleaned, g) => audio.Play(ssrc, cleaned, g), audio.Rent);
            AppLog.Info("Discord voice: noise suppression on someone's voice (Noise Suppression for Others).");
        }
        cleaner.Play(frame, gain);
    }

    private void Forget(string user)
    {
        _dave?.RemoveUser(user);
        List<uint> gone;
        lock (_gate)
        {
            foreach (var ssrc in _videoUsers.Where(p => p.Value == user).Select(p => p.Key).ToList())
            {
                _videoUsers.Remove(ssrc);
                _receivers.Remove(ssrc);
                _orientation.Remove(ssrc);
                foreach (var rtx in _rtxOf.Where(p => p.Value == ssrc).Select(p => p.Key).ToList()) _rtxOf.Remove(rtx);
            }
            gone = [.. _ssrcUsers.Where(p => p.Value == user).Select(p => p.Key)];
            foreach (var ssrc in gone) _ssrcUsers.Remove(ssrc);
            _heardAt.Remove(user);
        }
        lock (_decodeGate)
            foreach (var ssrc in gone)
            {
                if (_decoders.Remove(ssrc, out var decoder)) Opus.DecoderDestroy(decoder);
                _lastAudioSequence.Remove(ssrc);
                if (_cleaners.Remove(ssrc, out var cleaner)) cleaner.Dispose();
            }
        foreach (var ssrc in gone) _audio?.Forget(ssrc);
    }

    /// <summary>Speakers not heard for 400 ms stop showing as speaking (called by the session every 200 ms).</summary>
    internal void Tick()
    {
        var now = Environment.TickCount64;
        List<string> quiet;
        lock (_gate)
        {
            quiet = [.. _heardAt.Where(p => now - p.Value > 400).Select(p => p.Key)];
            foreach (var user in quiet) _heardAt.Remove(user);
        }
        foreach (var user in quiet) Changed?.Invoke("speaking", new { userId = user, speaking = false });
    }

    // ---- gateway plumbing ----

    private async Task HeartbeatAsync(ClientWebSocket ws, double interval, CancellationToken ct)
    {
        try
        {
            _acked = true;
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(interval), ct).ConfigureAwait(false);
                if (!_acked) { ws.Abort(); return; }
                _acked = false;
                await SendJsonAsync(3, new { t = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), seq_ack = _seqAck }).ConfigureAwait(false);
            }
        }
        catch { }
    }

    private Task SendJsonAsync(int op, object d) =>
        SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { op, d })), WebSocketMessageType.Text);

    private Task SendBinaryAsync(byte op, byte[] payload)
    {
        var message = new byte[payload.Length + 1];
        message[0] = op;
        payload.CopyTo(message, 1);
        return SendAsync(message, WebSocketMessageType.Binary);
    }

    private async Task SendAsync(byte[] bytes, WebSocketMessageType type)
    {
        if (_ws is not { State: WebSocketState.Open } ws) return;
        await _sendGate.WaitAsync().ConfigureAwait(false);
        try { await ws.SendAsync(bytes, type, true, _stop.Token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
        finally { _sendGate.Release(); }
    }

    private static async Task<(string? Text, byte[]? Binary)> ReceiveAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return (null, null);
            message.Write(buffer, 0, result.Count);
            if (message.Length > 8 << 20) throw new IOException("Voice message too large.");
            if (!result.EndOfMessage) continue;
            return result.MessageType == WebSocketMessageType.Binary
                ? (null, message.ToArray())
                : (Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length), null);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _paceWake.Set();
        _videoTimer?.Dispose();
        _soundshare?.Dispose();
        _ws?.Abort();
        lock (_decodeGate)
        {
            foreach (var cleaner in _cleaners.Values) cleaner.Dispose();
            _cleaners.Clear();
        }
        _audio?.Dispose();
        if (!_listening) RecordedVoice.Ended(_chain);
        _chain.Dispose();
        _transport?.Dispose();
        _dave?.Dispose();
        lock (_decodeGate)
        {
            foreach (var decoder in _decoders.Values) Opus.DecoderDestroy(decoder);
            _decoders.Clear();
        }
        if (_encoder != 0) { Opus.EncoderDestroy(_encoder); _encoder = 0; }
        if (_musicEncoder != 0) { Opus.EncoderDestroy(_musicEncoder); _musicEncoder = 0; }
    }
}

/// <summary>
/// Whether a game is running, Exo's launch or one started anywhere else from the Library's folders (as the driver's
/// update checks), read off the audio threads: a background read at most every 5 s, the last answer meanwhile.
/// </summary>
internal static class GameRunning
{
    private static volatile bool _now;
    private static long _at = long.MinValue / 2;
    private static int _reading;

    internal static bool Now
    {
        get
        {
            var tick = Environment.TickCount64;
            if (tick - Interlocked.Read(ref _at) > 5000 && Interlocked.Exchange(ref _reading, 1) == 0)
            {
                Interlocked.Exchange(ref _at, tick);
                _ = Task.Run(() =>
                {
                    try { _now = App.Services.Launcher.AnyGameRunning || MainWindow.RunningLibraryGames().Count > 0; }
                    catch { /* the last answer stands */ }
                    finally { Volatile.Write(ref _reading, 0); }
                });
            }
            return _now;
        }
    }
}
