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
    private readonly string _endpoint, _serverId, _userId, _sessionId, _token;
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
    private VoiceStages _loggedStages;
    private int _gateFrames;
    private nint _encoder;
    private uint _ssrc, _videoSsrc, _rtxSsrc;
    private ushort _videoSequence;
    private readonly bool _listening;
    private readonly int _bitrate;
    private int _seqAck = -1;
    private bool _acked = true;
    private bool _speaking;
    private int _silence;
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

    internal VoiceConnection(string endpoint, string serverId, string channelId, string userId, string sessionId, string token, bool listening = false, int bitrate = 64000)
    {
        _bitrate = Math.Clamp(bitrate, 8000, 384000);
        _endpoint = endpoint;
        _serverId = serverId;
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
            var outcome = await ConnectOnceAsync(resume).ConfigureAwait(false);
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
            while (ws.State == WebSocketState.Open)
            {
                var (text, binary) = await ReceiveAsync(ws, connection.Token).ConfigureAwait(false);
                if (text is null && binary is null) break;
                if (binary is not null) OnBinary(binary);
                else await OnTextAsync(text!, resume, ws, connection.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return "ended"; }
        catch (Exception ex) when (ex is WebSocketException or IOException or JsonException or TimeoutException)
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
                    await SendJsonAsync(0, new
                    {
                        server_id = _serverId, user_id = _userId, session_id = _sessionId, token = _token,
                        max_dave_protocol_version = (int)DaveSession.MaxVersion,
                        video = true,
                        streams = new[] { new { type = "video", rid = "100", quality = 100 } },
                    }).ConfigureAwait(false);
                break;
            case 6: _acked = true; break;
            case 2: await OnReadyAsync(d, ct).ConfigureAwait(false); break;
            case 4: OnSessionDescription(d); break;
            case 9: Changed?.Invoke("state", new { state = "connected" }); break;
            case 5:
                if (d.TryGetProperty("user_id", out var su) && d.TryGetProperty("ssrc", out var ss))
                    lock (_gate) _ssrcUsers[ss.GetUInt32()] = su.GetString() ?? "";
                break;
            case 11:
                foreach (var id in d.GetProperty("user_ids").EnumerateArray())
                    if (id.GetString() is { } user) _dave?.AddUser(user);
                break;
            case 13:
                if (d.GetProperty("user_id").GetString() is { } gone) Forget(gone);
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
        }
        var modes = d.GetProperty("modes").EnumerateArray().Select(m => m.GetString()).ToList();
        var mode = modes.Contains(VoiceTransport.Aes) ? VoiceTransport.Aes : VoiceTransport.XChaCha;
        _transport?.Dispose();
        _transport = new VoiceTransport(d.GetProperty("ip").GetString()!, d.GetProperty("port").GetInt32(), _ssrc);
        _transport.Received += OnPacket;
        _transport.RtcpReceived += OnRtcp;
        _videoTimer ??= new Timer(_ => PollVideo(), null, 20, 20);
        var (address, port) = await _transport.DiscoverAsync(ct).ConfigureAwait(false);
        _dave ??= new DaveSession(_userId, _groupId, (op, payload) => _ = SendBinaryAsync(op, payload), (op, body) => _ = SendJsonAsync(op, body));
        _dave.AssignSsrc(_ssrc);
        if (_videoSsrc != 0) _dave.AssignSsrc(_videoSsrc, Dave.CodecH264);
        await SendJsonAsync(1, new
        {
            protocol = "udp",
            data = new { address, port, mode },
            codecs = new object[]
            {
                new { name = "opus", type = "audio", priority = 1000, payload_type = 120 },
                new { name = "H264", type = "video", priority = 1000, payload_type = H264Rtp.PayloadType, rtx_payload_type = H264Rtp.RtxPayloadType, encode = true, decode = true },
            },
        }).ConfigureAwait(false);
    }

    private void OnSessionDescription(JsonElement d)
    {
        var key = d.GetProperty("secret_key").EnumerateArray().Select(b => (byte)b.GetInt32()).ToArray();
        _transport!.Start(d.GetProperty("mode").GetString() ?? VoiceTransport.Aes, key);
        var version = d.TryGetProperty("dave_protocol_version", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetUInt16() : (ushort)0;
        _dave!.OnSessionDescription(version);
        if (_audio is null)
        {
            _encoder = Opus.NewEncoder(_bitrate);
            _audio = new VoiceAudio { Deafened = _deafened };
            if (!_listening) _audio.Captured += SendCaptured;
            _audio.Start(capture: !_listening);
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
        if (Muted) { _chain.Mute(); voiced = false; }
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
        Span<byte> opus = stackalloc byte[1275];
        int length;
        fixed (float* pcm = frame)
        fixed (byte* o = opus)
            length = Opus.EncodeFloat(_encoder, pcm, Opus.FrameSamples, o, opus.Length);
        if (length > 0 && SendFrame(opus[..length])) MeasureSend(audio, started, processed);
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

    private void OnPacket(VoiceTransport.Packet packet)
    {
        if (packet.TransportSequence >= 0) Arrived(packet);
        if (packet.PayloadType == H264Rtp.PayloadType) OnVideoPacket(packet.Ssrc, packet.Sequence, packet);
        else if (packet.PayloadType == H264Rtp.RtxPayloadType) OnResent(packet);
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
    private void OnVideoPacket(uint ssrc, ushort sequence, VoiceTransport.Packet packet, int skip = 0)
    {
        H264Rtp.H264Receiver? receiver;
        lock (_gate)
        {
            if (!_videoUsers.TryGetValue(ssrc, out var user)) return;
            if (!_receivers.TryGetValue(ssrc, out receiver)) _receivers[ssrc] = receiver = NewReceiver(ssrc, user);
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
        OnVideoPacket(ssrc, BinaryPrimitives.ReadUInt16BigEndian(packet.Payload.Span), packet, skip: 2);
    }

    private H264Rtp.H264Receiver NewReceiver(uint ssrc, string user)
    {
        var receiver = new H264Rtp.H264Receiver();
        receiver.Nack += lost => _transport?.SendRtcp(Rtcp.Nack(_ssrc, ssrc, lost));
        receiver.KeyframeNeeded += () => _transport?.SendRtcp(Rtcp.Pli(_ssrc, ssrc));
        receiver.Frame += (timestamp, frame) =>
        {
            if (_dave is null) return;
            var plain = new byte[frame.Length + 64];
            var length = _dave.DecryptFrame(user, frame, plain, video: true);
            // Not decrypted (a key change under way): the frames after it can't be shown until a keyframe, which is
            // asked for at most twice a second, not on every frame that fails.
            if (length <= 0) { receiver.Lost(Environment.TickCount64); return; }
            var data = plain.AsSpan(0, length).ToArray();
            int orientation;
            lock (_gate) orientation = _orientation.GetValueOrDefault(ssrc);
            Frame?.Invoke(user, timestamp, H264Rtp.IsKeyframe(data), data, orientation);
        };
        return receiver;
    }

    // ---- receiving: telling the server what arrives, so it sends the stream at the quality this connection can take ----

    // Arrival times (microseconds) by unwrapped transport sequence number, not yet reported; what came in this second.
    private readonly SortedDictionary<long, long> _arrivals = [];
    private long _lastTransport = -1, _nextReported = -1, _feedbackAt, _rembAt, _received, _receivedSince, _receivedPerSecond;
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
            if (packet.PayloadType == H264Rtp.PayloadType) _media = packet.Ssrc;
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
                for (var s = from; s <= to; s++) times.Add(_arrivals.Remove(s, out var at) ? at : null);
                foreach (var stale in _arrivals.Keys.Where(k => k <= to).ToList()) _arrivals.Remove(stale);
                _nextReported = to + 1;
                feedback = Rtcp.TransportFeedback(_ssrc, _media, (ushort)from, _feedbackCount++, times);
            }
            if (now - _rembAt >= 1000)
            {
                _receivedPerSecond = _rembAt == 0 ? 0 : (_received - _receivedSince) * 8 * 1000 / Math.Max(1, now - _rembAt);
                _receivedSince = _received;
                _rembAt = now;
                if (_media != 0) remb = Rtcp.Remb(_ssrc, Math.Max(10_000_000, _receivedPerSecond * 2), [_media]);
                // What a watched stream or camera comes in at, once a minute, so its quality can be read back.
                if (_media != 0 && ++_secondsLogged % 60 == 0) AppLog.Info($"Discord video: receiving {_receivedPerSecond / 1000} kbit/s.");
            }
        }
        if (feedback is not null) _transport?.SendRtcp(feedback);
        if (remb is not null) _transport?.SendRtcp(remb);
    }

    // On a timer thread, which has no handler of its own.
    private void PollVideo()
    {
        var now = Environment.TickCount64;
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
        var (lost, keyframe) = Rtcp.Read(packet, _videoSsrc);
        // Under the send lock: the history is written as frames go out, and a half-written entry would resend the wrong packet.
        if (lost.Count > 0 && _rtxSsrc != 0)
            lock (_videoSendGate)
                foreach (var sequence in lost)
                    if (_sent.Resend(sequence) is { } resend)
                        _transport.SendVideo(_rtxSsrc, H264Rtp.RtxPayloadType, resend.Marker, _rtxSequence++, resend.Timestamp, resend.Payload);
        if (keyframe) KeyframeRequested?.Invoke();
    }

    /// <summary>The server wants a keyframe of this client's video (someone started watching, or lost one).</summary>
    internal event Action? KeyframeRequested;

    /// <summary>A whole, decrypted H.264 frame (Annex B) from a user: (userId, RTP timestamp, keyframe, data).</summary>
    /// <summary>(user, RTP time, keyframe, Annex B frame, video-orientation byte: bit 2 flip, bits 0-1 quarter turns).</summary>
    internal event Action<string, uint, bool, byte[], int>? Frame;

    /// <summary>Turns this client's own video on (with its size, frame rate and top bit rate) or off (op 12).</summary>
    internal Task SetVideoAsync(int width, int height, int fps, int maxBitrate = 8_000_000) =>
        SendJsonAsync(12, width == 0
            ? new { audio_ssrc = _ssrc, video_ssrc = 0u, rtx_ssrc = 0u, streams = Array.Empty<object>() }
            : new
            {
                audio_ssrc = _ssrc, video_ssrc = _videoSsrc, rtx_ssrc = _rtxSsrc,
                streams = new object[]
                {
                    new
                    {
                        type = "video", rid = "100", ssrc = _videoSsrc, active = true, quality = 100, rtx_ssrc = _rtxSsrc,
                        max_bitrate = maxBitrate, max_framerate = fps, max_resolution = new { type = "fixed", width, height },
                    },
                },
            });

    /// <summary>Sends one of this client's H.264 frames (Annex B): DAVE, then RTP packets on the video SSRC.</summary>
    internal void SendVideoFrame(byte[] frame, uint timestamp)
    {
        if (_transport is not { Started: true } || _dave is null || _videoSsrc == 0) return;
        var sealedFrame = new byte[frame.Length + 256];
        var length = _dave.EncryptFrame(_videoSsrc, frame, sealedFrame, video: true);
        if (length <= 0) return;
        var packets = H264Rtp.Packetize(sealedFrame[..length]);
        // One frame at a time: frames arrive on several threads, and a frame's packets must not interleave with another's.
        lock (_videoSendGate)
        for (var i = 0; i < packets.Count; i++)
        {
            var marker = i == packets.Count - 1;
            var sequence = _videoSequence++;
            _sent.Remember(sequence, timestamp, marker, packets[i]);
            _transport.SendVideo(_videoSsrc, H264Rtp.PayloadType, marker, sequence, timestamp, packets[i]);
        }
    }

    private StreamAudio? _soundshare;
    private nint _musicEncoder;

    /// <summary>Go Live's sound as music-quality Opus on this stream's audio SSRC: a window's app (its process), or this PC's sound but Exo's (0).</summary>
    internal void StartSoundshare(uint app)
    {
        if (_soundshare is not null || _transport is null || _dave is null) return;
        _musicEncoder = Opus.NewMusicEncoder();
        _soundshare = new StreamAudio(app);
        _soundshare.Captured += frame =>
        {
            if (_musicEncoder == 0 || _transport is not { Started: true } || _dave is null) return;
            try
            {
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
        if (_listening || gain == 0 || !App.Services.Settings.Shared.DiscordOthersNoiseSuppression) { audio.Play(ssrc, frame, gain); return; }
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
        _videoTimer?.Dispose();
        _soundshare?.Dispose();
        _ws?.Abort();
        lock (_decodeGate)
        {
            foreach (var cleaner in _cleaners.Values) cleaner.Dispose();
            _cleaners.Clear();
        }
        _audio?.Dispose();
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
