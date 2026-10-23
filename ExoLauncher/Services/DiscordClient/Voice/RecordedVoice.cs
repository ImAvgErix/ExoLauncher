using System.Buffers;
using System.ComponentModel;
using System.IO.Pipes;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services.DiscordClient.Voice;

/// <summary>
/// Your voice for Exo Capture's microphone track, while it records or keeps a replay: the chosen microphone through a
/// chain of its own, a call's chain made for a recording (<see cref="VoiceChain.Recording"/>: not gated, so the voice runs
/// on whole instead of being cut between words as a call's gate cuts it, and noise turned down gently so it keeps its
/// tone, and WebRTC's AGC2 brings it to a recording's level). In a call too: a call's frames are gated for Discord, which a
/// recording heard as choppy. While a call runs, the recording takes the call's own chain's voice from before its gate
/// (<see cref="VoiceChain.Cleaned"/>) rather than run a second noise suppression on the same microphone, which doubled
/// the voice's CPU (28 % of a core each, beside a game). Each 20 ms frame goes as 16-bit stereo,
/// stamped with when its sound was captured (the chain's delay taken off, so it stays in step with the picture), down an
/// anonymous pipe only the two processes hold (<see cref="CaptureAddon.ShareHandle"/>). The voice thread never waits on
/// it: a frame the add-on can't take in time (half a second queued) is dropped.
/// </summary>
internal static class RecordedVoice
{
    // Frame packet: when (performance counter, 100 ns), sample frames, channels; then the samples.
    private const int HeaderBytes = 16, Channels = Opus.Channels;
    private const int PacketBytes = HeaderBytes + Opus.FrameSamples * Channels * sizeof(short);

    private static readonly Lock Gate = new();
    private static bool _wanted;
    private static Channel<byte[]>? _frames;
    private static AnonymousPipeServerStream? _pipe;
    private static VoiceAudio? _audio;
    private static VoiceChain? _chain;
    private static (VoiceChain Chain, Func<long> FrameAt)? _call; // the call's chain, while one captures
    private static VoiceChain? _tapped; // the call's chain this recording takes its voice from

    /// <summary>A call's chain started capturing the microphone: a recording takes its voice from it from now on.</summary>
    internal static void Call(VoiceChain chain, Func<long> frameAt)
    {
        lock (Gate)
        {
            _call = (chain, frameAt);
            if (!_wanted) return;
            StopOwn();
            Tap();
        }
    }

    /// <summary>That call's chain is going: a recording cleans the microphone itself again.</summary>
    internal static void Ended(VoiceChain chain)
    {
        lock (Gate)
        {
            if (_call?.Chain != chain) return;
            _call = null;
            Untap();
            if (_wanted && _frames is not null) StartChain();
        }
    }

    private static void Tap()
    {
        if (_call is not { } call) return;
        var gate = new RecordingGate();
        call.Chain.Cleaned = (frame, open, delay) =>
        {
            if (!VoiceProcessing.Current.NoiseSuppression) { Send(frame, call.FrameAt(), delay); return; }
            var (late, at) = gate.Push(frame, open, call.FrameAt());
            if (late is not null) Send(late, at, delay);
        };
        _tapped = call.Chain;
    }

    private static void Untap()
    {
        if (_tapped is { } tapped) tapped.Cleaned = null;
        _tapped = null;
    }

    private static void StopOwn()
    {
        var (audio, chain) = (_audio, _chain);
        (_audio, _chain) = (null, null);
        audio?.Dispose();
        chain?.Dispose();
    }

    /// <summary>Exo Capture is recording or keeping a replay (true), or stopped, failed or gone (false).</summary>
    internal static void Set(CaptureAddon addon, bool wanted)
    {
        lock (Gate)
        {
            if (wanted == _wanted) return;
            _wanted = wanted;
            if (!wanted) { Close(); return; }
            if (!Open(addon)) { _wanted = false; return; }
            if (_call is not null) Tap();
            else StartChain();
        }
    }

    private static bool Open(CaptureAddon addon)
    {
        var pipe = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None, 1 << 16);
        nint remote;
        try { remote = addon.ShareHandle(pipe.ClientSafePipeHandle); }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            AppLog.Warn("Recorded voice: Exo Capture can't take it: " + ex.Message);
            pipe.Dispose();
            return false;
        }
        pipe.DisposeLocalCopyOfClientHandle();
        var frames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(25) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest },
            dropped => ArrayPool<byte>.Shared.Return(dropped));
        (_pipe, _frames) = (pipe, frames);
        new Thread(() => Pump(pipe, frames)) { IsBackground = true, Name = "Exo recorded voice" }.Start();
        _ = addon.CallAsync("voice.feed", new JsonObject { ["pipe"] = (long)remote }, CancellationToken.None)
            .ContinueWith(t => AppLog.Warn("Recorded voice: " + t.Exception?.GetBaseException().Message), TaskContinuationOptions.OnlyOnFaulted);
        AppLog.Info("Recorded voice: Exo Capture's microphone track takes the cleaned voice.");
        return true;
    }

    private static void Close()
    {
        Untap();
        // The chain after the capture thread stopped: it is the chain's only user.
        StopOwn();
        _frames?.Writer.TryComplete();
        _pipe?.Dispose();
        (_frames, _pipe) = (null, null);
    }

    private static void StartChain()
    {
        var audio = new VoiceAudio();
        var chain = new VoiceChain { Recording = true };
        var gate = new RecordingGate();
        audio.Captured += frame =>
        {
            var processing = VoiceProcessing.Current;
            var open = chain.Process(frame, processing, audio.MicId, audio.SystemGain);
            if (!processing.NoiseSuppression) { Send(frame, audio.FrameAt, chain.DelaySamples); return; }
            var (late, at) = gate.Push(frame, open, audio.FrameAt);
            if (late is not null) Send(late, at, chain.DelaySamples);
        };
        try { audio.Start(capture: true, render: false); }
        catch (Exception ex)
        {
            AppLog.Warn("Recorded voice: the microphone didn't open: " + ex.Message);
            audio.Dispose();
            chain.Dispose();
            return;
        }
        (_audio, _chain) = (audio, chain);
    }

    private static void Send(float[] frame, long frameAt, int delaySamples)
    {
        if (_frames is not { } frames) return;
        var packet = ArrayPool<byte>.Shared.Rent(PacketBytes);
        var at = frameAt - (long)delaySamples * 10_000_000 / Opus.SampleRate;
        BitConverter.TryWriteBytes(packet.AsSpan(0, 8), at);
        BitConverter.TryWriteBytes(packet.AsSpan(8, 4), Opus.FrameSamples);
        BitConverter.TryWriteBytes(packet.AsSpan(12, 4), Channels);
        var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(packet.AsSpan(HeaderBytes, PacketBytes - HeaderBytes));
        for (var i = 0; i < samples.Length; i++) samples[i] = (short)Math.Clamp(frame[i] * 32767f, -32768f, 32767f);
        if (!frames.Writer.TryWrite(packet)) ArrayPool<byte>.Shared.Return(packet);
    }

    // The pipe's own thread: a slow or gone add-on never holds up the voice thread.
    private static void Pump(AnonymousPipeServerStream pipe, Channel<byte[]> frames)
    {
        try
        {
            while (frames.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
                while (frames.Reader.TryRead(out var packet))
                {
                    try { pipe.Write(packet, 0, PacketBytes); }
                    finally { ArrayPool<byte>.Shared.Return(packet); }
                }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            AppLog.Debug("Recorded voice: the pipe closed: " + ex.Message);
        }
    }
}

/// <summary>
/// A recording's pauses, turned down: the gate's verdict (with its 500 ms hold) as a gentle expander instead of a cut,
/// so what noise suppression leaves of a fan or the room between words sits <see cref="FloorDb"/> under the voice while
/// a breath or a quiet word still comes through. A recording can wait: each frame goes out <see cref="Ahead"/> frames
/// (100 ms) late, so the gain is already up when a word begins (no first syllable turned down) and it glides across a
/// frame, never stepping. Frames keep their own capture times, so the delay never moves the voice off the picture.
/// </summary>
internal sealed class RecordingGate
{
    internal const int Ahead = 5;
    internal const float FloorDb = -30f;
    private static readonly float Floor = MathF.Pow(10, FloorDb / 20);
    private readonly float[][] _frames = [.. Enumerable.Range(0, Ahead + 1).Select(_ => new float[VoiceAudio.FrameFloats])];
    private readonly bool[] _open = new bool[Ahead + 1];
    private readonly long[] _at = new long[Ahead + 1];
    private int _next, _count;
    private float _gain = 1f;

    /// <summary>Takes a frame and the gate's verdict on it; gives back the frame from 100 ms ago with its gain, once there is one.</summary>
    internal (float[]? Frame, long At) Push(float[] frame, bool open, long at)
    {
        frame.CopyTo(_frames[_next], 0);
        (_open[_next], _at[_next]) = (open, at);
        _next = (_next + 1) % _frames.Length;
        if (++_count <= Ahead) return (null, 0);
        // The oldest frame held, and up if it or any frame after it (the next 100 ms) is a voice.
        var oldest = _next;
        var target = _open.Any(o => o) ? 1f : Floor;
        var late = _frames[oldest];
        var step = (target - _gain) / (late.Length / 2);
        for (var i = 0; i + 1 < late.Length; i += 2)
        {
            _gain += step;
            late[i] *= _gain;
            late[i + 1] *= _gain;
        }
        _gain = target;
        return (late, _at[oldest]);
    }
}
