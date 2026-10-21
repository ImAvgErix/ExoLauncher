using System.Runtime.InteropServices;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services.DiscordClient.Voice;

/// <summary>
/// Echo cancellation for a call on speakers: WebRTC's AEC3 (webrtc-audio-processing 2.1, BSD-3-Clause; Krisp's own echo
/// cancellation is AEC3 too), built with its high-pass filter and nothing else into exo_apm.dll by voice\build.ps1. It is
/// fed what Exo plays (<see cref="Render"/>, on the speakers' thread) and run on the microphone before anything else
/// (<see cref="Capture"/>, on the voice thread, so noise suppression gets the voice without the call in it), each 20 ms
/// stereo frame as two of its 10 ms mono ones. It finds the delay between the two sides itself, and follows a new device.
/// Measured on speech through a simulated room (70 ms away, 0.3 s of reverb, a slightly overdriven speaker): the echo 28 dB
/// down, a voice with nothing playing within 0.4 dB of itself (9 ms later), 0.05 ms of CPU per 10 ms.
/// </summary>
internal sealed partial class EchoCanceller : IDisposable
{
    private const int Half = Opus.FrameSamples / 2;
    private nint _apm;
    private readonly float[] _render = new float[Half], _capture = new float[Half];

    private EchoCanceller(nint apm) => _apm = apm;

    /// <summary>A canceller, or null when exo_apm.dll can't load.</summary>
    internal static EchoCanceller? Create()
    {
        try { return exo_aec_create() is var apm && apm != 0 ? new EchoCanceller(apm) : null; }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            AppLog.Info("Discord voice: echo cancellation unavailable (" + ex.GetType().Name + ").");
            return null;
        }
    }

    /// <summary>A 20 ms stereo frame Exo plays (silence when deafened: nothing reaches the speakers).</summary>
    internal unsafe void Render(ReadOnlySpan<float> stereo, bool silent)
    {
        for (var half = 0; half < 2; half++)
        {
            for (var i = 0; i < Half; i++)
                _render[i] = silent ? 0 : (stereo[(half * Half + i) * 2] + stereo[(half * Half + i) * 2 + 1]) * 0.5f;
            fixed (float* samples = _render) exo_aec_render(_apm, samples);
        }
    }

    /// <summary>Takes the echo out of a 20 ms stereo microphone frame, in place.</summary>
    internal unsafe void Capture(float[] stereo)
    {
        for (var half = 0; half < 2; half++)
        {
            var at = half * Half;
            for (var i = 0; i < Half; i++) _capture[i] = (stereo[(at + i) * 2] + stereo[(at + i) * 2 + 1]) * 0.5f;
            fixed (float* samples = _capture) exo_aec_capture(_apm, samples);
            for (var i = 0; i < Half; i++) stereo[(at + i) * 2] = stereo[(at + i) * 2 + 1] = _capture[i];
        }
    }

    /// <summary>After both threads stopped.</summary>
    public void Dispose()
    {
        if (_apm == 0) return;
        exo_aec_destroy(_apm);
        _apm = 0;
    }

    [LibraryImport("exo_apm.dll")]
    private static partial nint exo_aec_create();

    [LibraryImport("exo_apm.dll")]
    private static unsafe partial int exo_aec_render(nint apm, float* samples);

    [LibraryImport("exo_apm.dll")]
    private static unsafe partial int exo_aec_capture(nint apm, float* samples);

    [LibraryImport("exo_apm.dll")]
    private static partial void exo_aec_destroy(nint apm);
}
