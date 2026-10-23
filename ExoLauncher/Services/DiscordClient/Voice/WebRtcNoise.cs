using System.Runtime.InteropServices;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services.DiscordClient.Voice;

/// <summary>
/// WebRTC's own noise suppression (exo_apm.dll), Discord's "Standard": a statistical noise estimate taken off each band,
/// gentler on a voice than a neural model and with no delay. At <see cref="Level"/> high, as Chrome runs it. 20 ms stereo
/// frames, as two 10 ms mono calls, from one thread. <see cref="InputDb"/> is the frame's level going in, for the gate.
/// </summary>
internal sealed partial class WebRtcNoise : IDisposable
{
    private const int Half = Opus.FrameSamples / 2, Level = 2;
    private nint _ns;
    private readonly float[] _mono = new float[Half];

    private WebRtcNoise(nint ns) => _ns = ns;

    /// <summary>The level (dBFS) of the last frame before suppression.</summary>
    internal float InputDb { get; private set; } = -100f;

    /// <summary>A new one; null when exo_apm.dll can't load.</summary>
    internal static WebRtcNoise? Create()
    {
        try
        {
            var ns = exo_ns_create(Level);
            return ns != 0 ? new WebRtcNoise(ns) : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            AppLog.Info("Discord voice: WebRTC's noise suppression unavailable (" + ex.GetType().Name + ").");
            return null;
        }
    }

    /// <summary>Cleans a 20 ms stereo frame in place (both channels get the result: the voice is mono).</summary>
    internal unsafe void Process(float[] stereo)
    {
        double sum = 0;
        for (var half = 0; half < 2; half++)
        {
            var at = half * Half;
            for (var i = 0; i < Half; i++)
            {
                var s = _mono[i] = (stereo[(at + i) * 2] + stereo[(at + i) * 2 + 1]) * 0.5f;
                sum += s * (double)s;
            }
            fixed (float* samples = _mono) exo_ns_process(_ns, samples);
            for (var i = 0; i < Half; i++) stereo[(at + i) * 2] = stereo[(at + i) * 2 + 1] = _mono[i];
        }
        InputDb = MathF.Max(-100f, 10 * MathF.Log10((float)(sum / Opus.FrameSamples) + 1e-10f));
    }

    public void Dispose()
    {
        if (_ns == 0) return;
        exo_ns_destroy(_ns);
        _ns = 0;
    }

    [LibraryImport("exo_apm.dll")]
    private static partial nint exo_ns_create(int level);

    [LibraryImport("exo_apm.dll")]
    private static unsafe partial int exo_ns_process(nint ns, float* samples);

    [LibraryImport("exo_apm.dll")]
    private static partial void exo_ns_destroy(nint ns);
}
