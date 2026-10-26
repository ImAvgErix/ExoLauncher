using System.Runtime.InteropServices;
using ExoLauncher.Helpers;
using static ExoLauncher.Helpers.CoreAudioInterop;

namespace ExoLauncher.Services.DiscordClient.Voice;

/// <summary>
/// The call's sound on Windows: the default microphone and speakers through shared-mode WASAPI,
/// event driven, with Windows converting to and from Discord's 48 kHz stereo float (AUTOCONVERTPCM).
/// The microphone's thread only cuts its sound into 20 ms frames and hands each on (a ring of four), so it
/// never falls behind; Exo's voice thread runs what is done with them (<see cref="Captured"/>: noise
/// suppression, Voice Focus, Opus, DAVE, the socket). What the speakers play is the mix of every speaker's
/// decoded frames, each evened out to one speaking level (<see cref="VoiceLeveling"/>) and through a small jitter buffer. A call (both sides open) takes the echo of what it plays out of
/// the microphone (<see cref="EchoCanceller"/>) before the voice thread hands a frame on, when Echo Cancellation is on
/// and the speakers aren't on your head. While it runs, <see cref="LiveAudio"/> keeps Exo fit for real time.
/// </summary>
internal sealed unsafe partial class VoiceAudio : IDisposable
{
    internal const int FrameFloats = Opus.FrameSamples * Opus.Channels;
    private const uint AutoConvert = 0x80000000 | 0x08000000, EventCallback = 0x00040000;
    private const long BufferHns = 200_000; // 20 ms
    private const int MaxBufferedFrames = 10; // 200 ms per speaker before the oldest is dropped
    private const int StartFrames = 3; // 60 ms of buffer before a speaker starts playing
    private const int Slots = 4; // microphone frames handed to the voice thread, at most 80 ms

    private readonly CancellationTokenSource _stop = new();
    // Per side, so a new device reopens only that side mid-call.
    private CancellationTokenSource _captureStop = new(), _renderStop = new();
    private readonly Lock _mixGate = new();
    private readonly Dictionary<uint, Queue<float[]>> _streams = [];
    private readonly HashSet<uint> _playing = [];
    // Played frames, kept for the next decode: the call's sound allocates nothing per frame once it runs.
    private readonly Stack<float[]> _spare = new();
    private Thread? _capture, _render, _voice;
    private int _handlerFailed;
    private bool _live;
    private EchoCanceller? _echo;
    private volatile bool _onHead; // the speakers open now are headphones or a headset
    // The frames the microphone's thread has cut and the voice thread hasn't run yet: each with when its first sample
    // was captured and when it was whole. One writer (the capture thread) and one reader (the voice thread).
    private readonly float[][] _ring = [new float[FrameFloats], new float[FrameFloats], new float[FrameFloats], new float[FrameFloats]];
    private readonly long[] _ringAt = new long[Slots], _ringWhole = new long[Slots];
    private readonly AutoResetEvent _cut = new(false);
    private long _written, _read;

    /// <summary>
    /// A 20 ms stereo frame from the microphone (1920 floats), in place, on Exo's voice thread: handled before the
    /// handler returns (its array is used again for a later frame).
    /// </summary>
    internal event Action<float[]>? Captured;

    /// <summary>When the frame handed to <see cref="Captured"/> began, on the performance counter in 100 ns units.</summary>
    internal long FrameAt { get; private set; }

    /// <summary>When the frame handed to <see cref="Captured"/> was whole (its last packet read), same units.</summary>
    internal long FrameWholeAt { get; private set; }

    /// <summary>
    /// Microphone packets dropped for being late (the capture thread itself stalled), and frames dropped because the
    /// voice thread was a whole ring (80 ms) behind; counted up.
    /// </summary>
    internal int LateDrops, BehindDrops;

    /// <summary>CPU time the microphone, voice and speaker threads have used, in 100 ns units, as each last woke (for app.log).</summary>
    internal long CaptureCpu, VoiceCpu, RenderCpu;

    internal bool Deafened { get; set; }

    /// <summary>The microphone as it is, without Windows' communications processing (Voice Tune measures it so).</summary>
    internal bool Raw { get; init; }

    /// <summary>The Windows endpoint id of the microphone open now (Voice Tune keeps its tunings by it).</summary>
    internal string? MicId { get; private set; }

    internal void Start(bool capture = true, bool render = true)
    {
        if (!_live && (capture || render))
        {
            _live = true;
            LiveAudio.Begin();
        }
        if (capture && render) _echo ??= EchoCanceller.Create();
        if (capture)
        {
            _voice ??= new Thread(Hand) { IsBackground = true, Name = "Exo voice", Priority = ThreadPriority.Highest };
            if (!_voice.IsAlive) _voice.Start();
            _capture = Side(captureSide: true, _captureStop.Token);
        }
        if (render) _render = Side(captureSide: false, _renderStop.Token);
    }

    // The voice thread: each frame the microphone's thread cut, in order, through Captured.
    private void Hand()
    {
        using var mmcss = MmcssTask.Join();
        WaitHandle[] waits = [_cut, _stop.Token.WaitHandle];
        while (!_stop.IsCancellationRequested)
        {
            WaitHandle.WaitAny(waits, 200);
            Volatile.Write(ref VoiceCpu, ThreadCpu());
            for (var read = Volatile.Read(ref _read); read < Volatile.Read(ref _written) && !_stop.IsCancellationRequested; read++)
            {
                var slot = (int)(read % Slots);
                FrameAt = _ringAt[slot];
                FrameWholeAt = _ringWhole[slot];
                CancelEcho(_ring[slot]);
                // A handler that throws here would end Exo (this thread has no other catch): its frame is dropped.
                // Every 3000th failing frame still says so (a failure that starts repeating never goes silent),
                // with the message the first time.
                try { Captured?.Invoke(_ring[slot]); }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    if (_handlerFailed++ % 3000 == 0) AppLog.Warn("Discord voice: a microphone frame failed (" + ex.GetType().Name + ": " + ex.Message + ").");
                }
                Volatile.Write(ref _read, read + 1);
            }
        }
    }

    /// <summary>Whether the last microphone frame had the call's echo taken out.</summary>
    internal bool EchoCancelling { get; private set; }
    private bool _echoSaid;

    private void CancelEcho(float[] frame)
    {
        var cancel = _echo is not null && !_onHead && VoiceProcessing.Current.EchoCancellation;
        if (_echo is not null && (cancel != EchoCancelling || !_echoSaid))
        {
            (EchoCancelling, _echoSaid) = (cancel, true);
            AppLog.Info($"Discord voice: echo cancellation {(cancel ? "on" : _onHead ? "off: the speakers are on your head" : "off")}.");
        }
        if (cancel) _echo!.Capture(frame);
    }

    // On the capture thread: a whole frame to the voice thread, or dropped when it is a whole ring behind.
    private void HandOn(float[] frame, long at)
    {
        var written = _written;
        if (written - Volatile.Read(ref _read) >= Slots)
        {
            BehindDrops++;
            return;
        }
        var slot = (int)(written % Slots);
        frame.CopyTo(_ring[slot], 0);
        _ringAt[slot] = at;
        _ringWhole[slot] = QpcNow();
        Volatile.Write(ref _written, written + 1);
        _cut.Set();
    }

    /// <summary>Whether Windows turned on Automatic Gain Control for the microphone (else Exo's own runs).</summary>
    internal bool SystemGain { get; private set; }

    /// <summary>Input Device or Output Device changed: that side, when it is open, stops and opens the new one.</summary>
    internal void Reopen(bool captureSide)
    {
        if ((captureSide ? _capture : _render) is null) return;
        if (captureSide)
        {
            _captureStop.Cancel(); _capture?.Join(1000);
            _captureStop = new CancellationTokenSource();
            _capture = Side(true, _captureStop.Token);
        }
        else
        {
            _renderStop.Cancel(); _render?.Join(1000);
            _renderStop = new CancellationTokenSource();
            _render = Side(false, _renderStop.Token);
        }
    }

    private Thread Side(bool captureSide, CancellationToken side)
    {
        var thread = new Thread(() => Run(captureSide, side)) { IsBackground = true, Name = captureSide ? "Exo voice capture" : "Exo voice render", Priority = ThreadPriority.Highest };
        thread.Start();
        return thread;
    }

    /// <summary>A frame to decode into (one played before, or new).</summary>
    internal float[] Rent()
    {
        lock (_mixGate) return _spare.TryPop(out var frame) ? frame : new float[FrameFloats];
    }

    /// <summary>Whether each speaker's voice is evened out (<see cref="VoiceLeveling"/>, when Even Out Voices is on): a call's, never a watched stream's sound.</summary>
    internal bool EvensVoices { get; init; }

    private readonly VoiceLeveling _leveling = new();
    private bool _evenSaid;

    /// <summary>
    /// A speaker's decoded 20 ms frame (from <see cref="Rent"/>), queued for the mix: evened out to everyone's speaking
    /// level where Even Out Voices is on, then at their User Volume (gain).
    /// </summary>
    internal void Play(uint ssrc, float[] frame, float gain = 1f, bool level = true)
    {
        var even = level && EvensVoices && (App.Services.Settings.Shared.DiscordEvenVoices || VoiceProcessing.ExoOn(App.Services.Settings.Shared));
        // Under the lock: a speaker's frames come from the receive thread or their own cleaner's, one leveler each.
        lock (_mixGate)
        {
            if (even)
            {
                _leveling.Process(ssrc, frame);
                if (!_evenSaid) AppLog.Info("Discord voice: evening out each voice toward one speaking level (Even Out Voices).");
                _evenSaid = true;
            }
            if (gain != 1f) for (var i = 0; i < frame.Length; i++) frame[i] *= gain;
            if (!_streams.TryGetValue(ssrc, out var queue)) _streams[ssrc] = queue = new Queue<float[]>();
            queue.Enqueue(frame);
            while (queue.Count > MaxBufferedFrames) Spare(queue.Dequeue());
        }
    }

    // Under _mixGate.
    private void Spare(float[] frame)
    {
        if (_spare.Count < 64) _spare.Push(frame);
    }

    internal void Forget(uint ssrc)
    {
        lock (_mixGate) { _streams.Remove(ssrc); _playing.Remove(ssrc); _leveling.Forget(ssrc); }
    }

    // AUDCLNT_E_DEVICE_INVALIDATED: the device went away (a headset unplugged) or Windows reset it.
    private const int DeviceInvalidated = unchecked((int)0x88890004);

    private void Run(bool captureSide, CancellationToken side)
    {
        CoInitializeEx(0, 0); // MTA for this thread
        using var mmcss = MmcssTask.Join();
        // A device that goes away mid-call is followed: the side opens again on what Windows uses now, every half second
        // at first and then every 2 s for as long as the call lasts (it gave up after 10 s, and the call stayed silent
        // until it was left and joined again).
        for (var reopen = 0; RunOnce(captureSide, side, reopen > 0); reopen++)
        {
            if (reopen == 0) AppLog.Info($"Discord voice: the {(captureSide ? "microphone" : "speakers")} went away; opening the one Windows uses now.");
            if (side.WaitHandle.WaitOne(reopen < 20 ? 500 : 2000) || _stop.IsCancellationRequested) return;
        }
    }

    // One device opened and run until stopped; true when the device went away and the side should open again.
    private bool RunOnce(bool captureSide, CancellationToken side, bool retrying)
    {
        if (_stop.IsCancellationRequested || side.IsCancellationRequested) return false;
        // Let go of at once when the side stops, not whenever the finalizer runs: each holds the endpoint's stream.
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        object? clientObject = null;
        try
        {
            // A WAV as the microphone (EXO_MIC_FILE, dev builds' self-tests): frames at real-time pace through the
            // same ring and voice thread, so every test sees the chain exactly as a call does.
            if (captureSide && Environment.GetEnvironmentVariable("EXO_MIC_FILE") is { Length: > 0 } micFile && File.Exists(micFile))
                return FileCapture(micFile, side);
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            var settings = App.Services.Settings.Current;
            device = AudioDevices.Open(enumerator, captureSide, captureSide ? settings.DiscordInputDevice : settings.DiscordOutputDevice);
            if (captureSide) MicId = device is not null && device.GetId(out var mic) == 0 ? mic : null;
            else _onHead = device is not null && AudioDevices.OnYourHead(device);
            if (device is null)
            {
                if (!retrying) AppLog.Info($"Discord voice: no default {(captureSide ? "microphone" : "speakers")}.");
                return retrying;
            }
            var iid = typeof(IAudioClient).GUID;
            Marshal.ThrowExceptionForHR(device.Activate(ref iid, 23, 0, out clientObject));
            var client = (IAudioClient)clientObject;
            var format = Format();
            var processing = VoiceProcessing.Current;
            // Exo's noise suppression, when it can run; when it can't (its model not fetched yet), Windows' own takes over.
            var exoCleans = processing.NoiseSuppression && (NoiseSuppressorDownload.Ready());
            // The microphone raw: no Windows or driver "enhancements" and nothing an Equalizer APO chain (Lucid Voice) adds,
            // so Exo's noise suppression gets the voice as it is. A mic that can't give raw sound comes as Windows has it.
            // Not while Windows' noise suppression stands in: a raw stream skips it.
            if (captureSide && (exoCleans || !processing.NoiseSuppression)) RawStream(clientObject);
            Marshal.ThrowExceptionForHR(client.Initialize(0, EventCallback | AutoConvert, BufferHns, 0, ref format, 0));
            if (captureSide) Effects(client, processing.NoiseSuppression && !exoCleans);
            using var ready = new AutoResetEvent(false);
            Marshal.ThrowExceptionForHR(client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle()));
            Marshal.ThrowExceptionForHR(client.GetBufferSize(out var bufferFrames));
            return captureSide ? Capture(client, ready, side) : Render(client, ready, bufferFrames, side);
        }
        catch (COMException ex) when (ex.HResult == DeviceInvalidated) { return true; }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException)
        {
            AppLog.Info($"Discord voice: {(captureSide ? "microphone" : "speakers")} failed ({ex.GetType().Name} 0x{ex.HResult:X8}).");
            return false;
        }
        finally
        {
            Release(clientObject);
            Release(device);
            Release(enumerator);
        }
    }

    private static void Release(object? com)
    {
        if (com is not null && Marshal.IsComObject(com)) Marshal.ReleaseComObject(com);
    }

    private static void RawStream(object client)
    {
        if (client is not IAudioClient2 two) return;
        var properties = new AudioClientProperties { Size = (uint)Marshal.SizeOf<AudioClientProperties>(), Category = 0, Options = 1 }; // AUDCLNT_STREAMOPTIONS_RAW
        if (two.SetClientProperties(ref properties) != 0) AppLog.Info("Discord voice: this microphone has no raw mode; its sound comes as Windows processes it.");
    }

    // AUDIO_EFFECT_TYPE_ACOUSTIC_ECHO_CANCELLATION, _NOISE_SUPPRESSION, _AUTOMATIC_GAIN_CONTROL and (Windows 11 24H2)
    // _DEEP_NOISE_SUPPRESSION, Windows' AI one, on any vendor's PC (ksmedia.h).
    private static readonly Guid EchoEffect = new("6f64adbe-8211-11e2-8c70-2c27d7f001fa"),
        NoiseEffect = new("6f64adbf-8211-11e2-8c70-2c27d7f001fa"), GainEffect = new("6f64adc0-8211-11e2-8c70-2c27d7f001fa"),
        DeepNoiseEffect = new("6f64add0-8211-11e2-8c70-2c27d7f001fa");

    // Windows 11's own processing on this stream, all off: echo cancellation, gain control and noise suppression (Exo's
    // own do all three); Windows' noise suppression on instead when Exo's can't run. Older Windows has no effects
    // manager: nothing to set.
    private void Effects(IAudioClient client, bool windowsCleans)
    {
        SystemGain = false;
        var iid = typeof(IAudioEffectsManager).GUID;
        if (client.GetService(ref iid, out var service) != 0 || service is not IAudioEffectsManager manager)
        {
            Release(service);
            return;
        }
        try { SetEffects(manager, windowsCleans); }
        finally { Release(manager); }
    }

    private void SetEffects(IAudioEffectsManager manager, bool windowsCleans)
    {
        if (manager.GetAudioEffects(out var list, out var count) != 0) return;
        bool echo = false, gain = false, deep = false;
        try
        {
            var size = Marshal.SizeOf<AudioEffect>();
            // When Windows cleans (Exo's own can't run), its AI Deep Noise Suppression where the microphone offers it, the
            // simple one only where it doesn't: never both on one voice.
            var deepOffered = false;
            for (var i = 0; i < count; i++)
            {
                var effect = Marshal.PtrToStructure<AudioEffect>(list + i * size);
                deepOffered |= effect.Id == DeepNoiseEffect && effect.CanSetState != 0;
            }
            for (var i = 0; i < count; i++)
            {
                var effect = Marshal.PtrToStructure<AudioEffect>(list + i * size);
                // Windows' own echo cancellation, gain control and noise suppression all off: Exo does them itself.
                bool? want = effect.Id == DeepNoiseEffect ? windowsCleans
                    : effect.Id == NoiseEffect ? windowsCleans && !deepOffered
                    : effect.Id == EchoEffect || effect.Id == GainEffect ? false : null;
                if (want is not { } on) continue;
                var set = effect.CanSetState != 0 && manager.SetAudioEffectState(effect.Id, on ? 1 : 0) == 0;
                var now = set ? on : effect.State != 0;
                if (effect.Id == EchoEffect) echo = now;
                if (effect.Id == GainEffect) gain = now;
                if (effect.Id == DeepNoiseEffect) deep = now;
            }
        }
        finally { Marshal.FreeCoTaskMem(list); }
        SystemGain = gain;
        AppLog.Info($"Discord voice: Windows' echo cancellation {(echo ? "on" : "off")}, gain control {(gain ? "Windows" : "off")}{(deep ? ", AI noise suppression on" : "")}.");
    }

    /// <summary>The CPU time this thread has used, in 100 ns units.</summary>
    internal static long ThreadCpu() => GetThreadTimes(GetCurrentThread(), out _, out _, out var kernel, out var user) ? kernel + user : 0;

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentThread();

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetThreadTimes(nint thread, out long creation, out long exit, out long kernel, out long user);

    // The performance counter in 100 ns units, as WASAPI stamps captured packets.
    internal static long QpcNow() => (long)(System.Diagnostics.Stopwatch.GetTimestamp() * (10_000_000.0 / System.Diagnostics.Stopwatch.Frequency));

    // Until stopped; true when the microphone went away.
    private bool Capture(IAudioClient client, AutoResetEvent ready, CancellationToken side)
    {
        var iid = typeof(IAudioCaptureClient).GUID;
        Marshal.ThrowExceptionForHR(client.GetService(ref iid, out var serviceObject));
        var capture = (IAudioCaptureClient)serviceObject;
        var frame = new float[FrameFloats];
        var filled = 0;
        long frameAt = 0;
        // The mic's own delay: the least a packet has been old lately (followed down at once, up 1 ms a second).
        var usual = long.MaxValue;
        WaitHandle[] waits = [ready, side.WaitHandle];
        try
        {
            Marshal.ThrowExceptionForHR(client.Start());
            while (!_stop.IsCancellationRequested && !side.IsCancellationRequested)
            {
                // On a timeout too: a device that went away signals nothing more. Stopping wakes it at once.
                WaitHandle.WaitAny(waits, 200);
                Volatile.Write(ref CaptureCpu, ThreadCpu());
                // Input Volume, 0-200%. Above 100% a loud word can pass full scale: it is left to the chain's limiter
                // (Voice Focus) or its last clamp, never cut flat here, before the limiter could catch it.
                var gain = App.Services.Settings.Shared.DiscordInputVolume / 100f;
                int hr;
                while ((hr = capture.GetNextPacketSize(out var packet)) == 0 && packet > 0)
                {
                    if ((hr = capture.GetBuffer(out var data, out var frames, out var flags, out _, out var at)) != 0) break;
                    // Sound more than 40 ms later than this mic usually is (this thread stalled) is dropped, not sent late: a
                    // backlog kept instead stays in every word after it for the rest of the call (one drop at 100 ms held
                    // the call 100 ms behind). Measured against the mic's own delay, not from zero: a Bluetooth headset or
                    // a USB mic with a deep buffer delivers every packet over 40 ms old, and all of them were dropped.
                    var age = QpcNow() - (long)at;
                    if ((flags & 4) == 0) usual = age < usual ? age : usual + 100;
                    if ((flags & 4) == 0 && age > usual + 400_000)
                    {
                        capture.ReleaseBuffer(frames);
                        filled = 0;
                        LateDrops++;
                        continue;
                    }
                    var source = new ReadOnlySpan<float>((void*)data, (int)frames * Opus.Channels);
                    var silent = (flags & 2) != 0;
                    for (var used = 0; used < source.Length;)
                    {
                        if (filled == 0) frameAt = (long)at + Offset(used);
                        used += Fill(source[used..], silent, gain, frame, ref filled);
                        if (filled < FrameFloats) break;
                        HandOn(frame, frameAt);
                        filled = 0;
                    }
                    capture.ReleaseBuffer(frames);
                }
                if (hr == DeviceInvalidated) return true;
            }
        }
        finally { client.Stop(); Release(serviceObject); }
        return false;
    }

    /// <summary>
    /// Copies as much of a microphone packet as the frame being cut has room for (at Input Volume, or zeros when
    /// Windows marked it silent), one span at a time rather than a sample at a time; how many floats it took.
    /// </summary>
    internal static int Fill(ReadOnlySpan<float> source, bool silent, float gain, Span<float> frame, ref int filled)
    {
        var take = Math.Min(source.Length, frame.Length - filled);
        var into = frame.Slice(filled, take);
        if (silent) into.Clear();
        else if (gain == 1f) source[..take].CopyTo(into);
        else for (var i = 0; i < take; i++) into[i] = source[i] * gain;
        filled += take;
        return take;
    }

    /// <summary>How far into a packet (in 100 ns units) the sample at this float index is.</summary>
    internal static long Offset(int floats) => floats / Opus.Channels * 10_000_000L / Opus.SampleRate;

    // The file's frames at the pace a microphone delivers them (20 ms each), at Input Volume, until stopped.
    private bool FileCapture(string path, CancellationToken side)
    {
        var pcm = ReadWavMono(path);
        var frame = new float[FrameFloats];
        var at = 0L;
        var next = System.Diagnostics.Stopwatch.GetTimestamp();
        var ticksPerFrame = (long)(System.Diagnostics.Stopwatch.Frequency * 0.02);
        while (!_stop.IsCancellationRequested && !side.IsCancellationRequested)
        {
            var left = (next - System.Diagnostics.Stopwatch.GetTimestamp()) * 1000 / System.Diagnostics.Stopwatch.Frequency;
            if (left > 0 && side.WaitHandle.WaitOne((int)Math.Min(left, 200))) break;
            Volatile.Write(ref CaptureCpu, ThreadCpu());
            var gain = App.Services.Settings.Shared.DiscordInputVolume / 100f;
            Array.Clear(frame);
            var mono = (int)(at % pcm.Length);
            for (var i = 0; i < Opus.FrameSamples; i++)
            {
                var s = pcm[(mono + i) % pcm.Length] * gain;
                frame[2 * i] = frame[2 * i + 1] = s;
            }
            HandOn(frame, QpcNow());
            at += Opus.FrameSamples;
            next += ticksPerFrame;
            var behind = (System.Diagnostics.Stopwatch.GetTimestamp() - next) / System.Diagnostics.Stopwatch.Frequency;
            if (behind > 0.2) next = System.Diagnostics.Stopwatch.GetTimestamp();
        }
        return false;
    }

    // A mono or stereo WAV (16-bit PCM or 32-bit float, 48 kHz) as mono floats; anything else throws.
    private static float[] ReadWavMono(string path)
    {
        var b = File.ReadAllBytes(path);
        var channels = BitConverter.ToInt16(b, 22);
        var format = BitConverter.ToInt16(b, 20);
        var rate = BitConverter.ToInt32(b, 24);
        var bits = BitConverter.ToInt16(b, 34);
        var data = b.AsSpan();
        var at = b.Length;
        for (var i = 12; i + 8 <= b.Length; i += 8 + BitConverter.ToInt32(b, i + 4))
            if (b.AsSpan(i, 4).SequenceEqual("data"u8)) { at = i + 8; break; }
        if (at >= b.Length || rate != Opus.SampleRate) throw new InvalidDataException(path);
        var frames = (b.Length - at) / (bits / 8) / channels;
        var take = new float[frames];
        for (var i = 0; i < frames; i++)
        {
            double sum = 0;
            for (var c = 0; c < channels; c++)
            {
                var o = at + (i * channels + c) * (bits / 8);
                sum += format == 3 ? BitConverter.ToSingle(b, o) : BitConverter.ToInt16(b, o) / 32768f;
            }
            take[i] = (float)(sum / channels);
        }
        return take;
    }

    // Until stopped; true when the speakers went away.
    private bool Render(IAudioClient client, AutoResetEvent ready, uint bufferFrames, CancellationToken side)
    {
        var iid = typeof(IAudioRenderClient).GUID;
        Marshal.ThrowExceptionForHR(client.GetService(ref iid, out var serviceObject));
        var render = (IAudioRenderClient)serviceObject;
        var pending = new float[FrameFloats];
        var pendingAt = FrameFloats; // nothing pending
        WaitHandle[] waits = [ready, side.WaitHandle];
        try
        {
            Marshal.ThrowExceptionForHR(client.Start());
            while (!_stop.IsCancellationRequested && !side.IsCancellationRequested)
            {
                WaitHandle.WaitAny(waits, 200); // on a timeout too: a device that went away signals nothing more
                Volatile.Write(ref RenderCpu, ThreadCpu());
                var hr = client.GetCurrentPadding(out var padding);
                if (hr == DeviceInvalidated) return true;
                if (hr != 0) continue;
                var space = (int)(bufferFrames - padding);
                if (space <= 0) continue;
                hr = render.GetBuffer((uint)space, out var data);
                if (hr == DeviceInvalidated) return true;
                if (hr != 0) continue;
                var output = new Span<float>((void*)data, space * Opus.Channels);
                var written = 0;
                while (written < output.Length)
                {
                    if (pendingAt == FrameFloats) { MixInto(pending); pendingAt = 0; }
                    var take = Math.Min(FrameFloats - pendingAt, output.Length - written);
                    pending.AsSpan(pendingAt, take).CopyTo(output.Slice(written, take));
                    pendingAt += take;
                    written += take;
                }
                render.ReleaseBuffer((uint)space, Deafened ? 2u : 0u);
            }
        }
        finally { client.Stop(); Release(serviceObject); }
        return false;
    }

    // One 20 ms frame of every speaker who has enough buffered, summed and kept from clipping.
    private void MixInto(float[] mix)
    {
        Array.Clear(mix);
        lock (_mixGate)
        {
            foreach (var (ssrc, queue) in _streams)
            {
                if (!_playing.Contains(ssrc))
                {
                    if (queue.Count < StartFrames) continue;
                    _playing.Add(ssrc);
                }
                if (!queue.TryDequeue(out var frame)) { _playing.Remove(ssrc); continue; }
                for (var i = 0; i < mix.Length; i++) mix[i] += frame[i];
                Spare(frame);
            }
        }
        // Output Volume, 0-200%.
        var gain = App.Services.Settings.Shared.DiscordOutputVolume / 100f;
        if (gain != 1f) for (var i = 0; i < mix.Length; i++) mix[i] *= gain;
        for (var i = 0; i < mix.Length; i++) mix[i] = MathF.Max(-1f, MathF.Min(1f, mix[i]));
        _echo?.Render(mix, Deafened);
    }

    internal static WaveFormatExtensible Format() => new()
    {
        FormatTag = 0xFFFE,
        Channels = Opus.Channels,
        SamplesPerSec = Opus.SampleRate,
        AvgBytesPerSec = Opus.SampleRate * Opus.Channels * 4,
        BlockAlign = Opus.Channels * 4,
        BitsPerSample = 32,
        Size = 22,
        ValidBitsPerSample = 32,
        ChannelMask = 3,
        SubFormat = new Guid("00000003-0000-0010-8000-00aa00389b71"),
    };

    public void Dispose()
    {
        _stop.Cancel();
        _captureStop.Cancel();
        _renderStop.Cancel();
        _capture?.Join(1000);
        _render?.Join(1000);
        if (_voice is { IsAlive: true } voice) voice.Join(1000);
        _echo?.Dispose();
        if (_live)
        {
            _live = false;
            LiveAudio.End();
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    internal struct WaveFormatExtensible
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSec;
        public uint AvgBytesPerSec;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort Size;
        public ushort ValidBitsPerSample;
        public uint ChannelMask;
        public Guid SubFormat;
    }


    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
    internal interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, ref WaveFormatExtensible format, nint sessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int shareMode, nint format, out nint closest);
        [PreserveSig] int GetMixFormat(out nint format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(nint handle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    // IAudioClient2: IAudioClient's twelve methods (not called through this view), then its own three.
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("726778CD-F60A-4eda-82DE-E47610CD78AA")]
    private interface IAudioClient2
    {
        void Initialize(); void GetBufferSize(); void GetStreamLatency(); void GetCurrentPadding(); void IsFormatSupported(); void GetMixFormat();
        void GetDevicePeriod(); void Start(); void Stop(); void Reset(); void SetEventHandle(); void GetService();
        [PreserveSig] int IsOffloadCapable(int category, out int capable);
        [PreserveSig] int SetClientProperties(ref AudioClientProperties properties);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioClientProperties
    {
        public uint Size;
        public int IsOffload;
        public int Category;
        public int Options;
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("4460B3AE-4B44-4527-8676-7548A8ACD260")]
    private interface IAudioEffectsManager
    {
        [PreserveSig] int RegisterAudioEffectsChangedNotificationCallback(nint client);
        [PreserveSig] int UnregisterAudioEffectsChangedNotificationCallback(nint client);
        [PreserveSig] int GetAudioEffects(out nint effects, out uint count);
        [PreserveSig] int SetAudioEffectState(Guid effectId, int state);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioEffect
    {
        public Guid Id;
        public int CanSetState;
        public int State;
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
    internal interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out nint data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2")]
    private interface IAudioRenderClient
    {
        [PreserveSig] int GetBuffer(uint frames, out nint data);
        [PreserveSig] int ReleaseBuffer(uint frames, uint flags);
    }
}

/// <summary>
/// Even Out Voices (Voice & Video, on by default): each person in a call brought toward one speaking level, so one
/// friend's hot mic isn't louder than everyone else, by a <see cref="SpeechLeveler"/> of their own (12 dB either way at
/// most, over about 1.5 s of their talking) on their decoded frames, before their User Volume, which still applies on
/// top. It learns only from their voice: frames at -50 dBFS and up and 10 dB over their own quiet (<see cref="QuietFloor"/>:
/// what their mic sends between words, the room or their gate's tail; until it has heard some, nothing is learned), and
/// not a knock or a click (<see cref="KnockDetector"/>), so the noise they send never moves it, nor does a pause (Discord
/// sends nothing then). Per SSRC; nothing is allocated per frame
/// once a speaker has one.
/// </summary>
internal sealed class VoiceLeveling
{
    private readonly Dictionary<uint, (SpeechLeveler Leveler, QuietFloor Quiet, KnockDetector Knock)> _voices = [];

    /// <summary>Levels one of their decoded 20 ms stereo frames in place.</summary>
    internal void Process(uint ssrc, float[] frame)
    {
        if (!_voices.TryGetValue(ssrc, out var voice)) _voices[ssrc] = voice = (new SpeechLeveler(), new QuietFloor(), new KnockDetector());
        var db = SpeechLeveler.Level(frame);
        var quiet = voice.Quiet.Db;
        voice.Quiet.Add(db);
        var knock = voice.Knock.Hit(frame);
        voice.Leveler.Process(frame, db > -50f && db > quiet + 10f && !knock);
    }

    /// <summary>Their gain now, in dB (0 before they have talked).</summary>
    internal float GainDb(uint ssrc) => _voices.TryGetValue(ssrc, out var voice) ? voice.Leveler.GainDb : 0f;

    /// <summary>They left: the next to get their SSRC starts afresh.</summary>
    internal void Forget(uint ssrc) => _voices.Remove(ssrc);
}
