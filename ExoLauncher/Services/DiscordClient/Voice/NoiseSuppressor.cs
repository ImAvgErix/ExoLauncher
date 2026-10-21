using System.Diagnostics;
using System.Runtime.InteropServices;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services.DiscordClient.Voice;

/// <summary>Fetching DeepFilterNet's plugin (outside the unsafe class: it awaits).</summary>
internal static class NoiseSuppressorDownload
{
    // The plugin file last found whole (its size and time): hashing 50 MB again is skipped while it's unchanged.
    private static (long Size, DateTime Written, bool Whole)? _checked;

    /// <summary>Whether the plugin is here and whole; hashes it only when the file changed since it last checked.</summary>
    internal static bool Ready()
    {
        var file = new FileInfo(NoiseSuppressor.PluginPath);
        if (!file.Exists) return false;
        if (_checked is { } last && last.Size == file.Length && last.Written == file.LastWriteTimeUtc) return last.Whole;
        var whole = VerifiedGitHubReleaseDownloader.IsPinnedAssetFile(NoiseSuppressor.Plugin, NoiseSuppressor.PluginPath, _ => true);
        _checked = (file.Length, file.LastWriteTimeUtc, whole);
        return whole;
    }

    /// <summary>Fetches the plugin if it isn't here yet (verified before it is kept); quiet when offline. Never on the caller's thread.</summary>
    internal static Task EnsureAsync(CancellationToken ct) => Task.Run(() => FetchAsync(ct), ct);

    private static async Task FetchAsync(CancellationToken ct)
    {
        if (Ready()) return;
        try
        {
            await VerifiedGitHubReleaseDownloader.Shared.DownloadPinnedAsync(NoiseSuppressor.Plugin, NoiseSuppressor.PluginPath, _ => true, ct).ConfigureAwait(false);
            AppLog.Info("Discord voice: noise suppression ready.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Info("Discord voice: noise suppression not fetched (" + ex.GetType().Name + ").");
        }
    }
}

/// <summary>
/// Noise suppression for the microphone: DeepFilterNet 3 (Rikorose/DeepFilterNet v0.5.6, MIT/Apache-2.0),
/// its own prebuilt Windows plugin, fetched once from the project's GitHub release (pinned size and
/// SHA-256, <see cref="VerifiedGitHubReleaseDownloader"/>) when Exo's Discord client starts, so its
/// 50 MB model stays out of the installer for everyone who never uses it. Driven through the
/// plugin's LADSPA C interface: the mono descriptor at 48 kHz, audio in and out. A 20 ms stereo frame is mixed
/// to mono, cleaned and spread back to both channels. When the plugin can't load, frames pass through unchanged.
/// It turns noise down by at most <see cref="MostDb"/> (Voice Tune's strength, else <see cref="GentleDb"/>):
/// without a limit, and with its post-filter, it takes parts of the voice with the noise and sounds muffled.
/// Its delay, from v0.5.6's source (ladspa/src/lib.rs, libDF/src/tract.rs): the plugin carries the low-latency
/// model (no lookahead), so 10 ms of STFT (960-sample window, 480-sample hop), plus the buffer its worker thread
/// keeps ahead of each run, which starts at one hop (10 ms), grows a hop whenever a run comes back later than real
/// time and shrinks a hop after about 20 s of quick runs; plus the time a run waits on the worker, which polls every
/// 2 ms (<see cref="LiveAudio"/> keeps Windows' timer at 1 ms so that is 2 ms, not 15.6). <see cref="DelaySamples"/>
/// measures the first two from the sound itself.
/// </summary>
internal sealed unsafe class NoiseSuppressor : IDisposable
{
    /// <summary>
    /// How far noise suppression turns noise down (dB): all the way (100), DeepFilterNet's own default and the setting it is
    /// measured at. A cap left typing and the room heard (18-36); the muffled sound came from its post-filter (off) and the
    /// stages Exo stacked after it (gone), not from full strength.
    /// </summary>
    internal const int GentleDb = 100, MostDb = 100;

    private const int PortInput = 1, PortControl = 4, PortAudio = 8;
    private const int WarmSamples = Opus.SampleRate / 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct Descriptor
    {
        public uint UniqueId; // LADSPA's unsigned long: 32 bits on Windows
        public nint Label;
        public int Properties;
        public nint Name, Maker, Copyright;
        public uint PortCount;
        public nint PortDescriptors, PortNames, PortRangeHints, ImplementationData;
        public nint Instantiate, ConnectPort, Activate, Run, RunAdding, SetRunAddingGain, Deactivate, Cleanup;
    }

    internal static readonly GitHubReleaseAsset Plugin = new(
        "Rikorose", "DeepFilterNet", "v0.5.6", "deep_filter_ladspa-0.5.6-x86_64-pc-windows-msvc.dll",
        50671104, "f97e34275eb884b6076a96d261683d2a43c6b2d97ddf9e346bc06f7c8057eb04", PredatesGitHubDigests: true);

    internal static string PluginPath { get; set; } = Path.Combine(PathHelper.AppDataDir, "voice", "deep_filter.dll");

    // The plugin keeps one model for all its instances (a static), and each instance's worker runs a clone of it whose
    // feature buffers are the same memory (libDF's DfTract clones share its tensors and write them through raw pointers).
    // Two instances running at once corrupted each other: with Noise Suppression for Others on, the microphone and everyone
    // heard went silent, and the broken instances stayed broken in Idle until Exo restarted. So each instance gets its own
    // copy of the plugin file, loaded as a module of its own (Windows loads a hard link to the same file as the same module)
    // with its own model, and an instance is only ever run by one user at a time (Idle). Copies stay for the next run.
    private static readonly List<nint> Copies = [];

    private static nint LoadCopy()
    {
        lock (Copies)
        {
            if (!NoiseSuppressorDownload.Ready()) return 0;
            var n = Copies.Count;
            var path = n == 0 ? PluginPath : Path.Combine(Path.GetDirectoryName(PluginPath)!, $"deep_filter.{n}.dll");
            try
            {
                // One left by an earlier run is used when it is the same file (its size and time); else copied again.
                var (main, copy) = (new FileInfo(PluginPath), new FileInfo(path));
                if (n > 0 && (!copy.Exists || copy.Length != main.Length || copy.LastWriteTimeUtc != main.LastWriteTimeUtc))
                    File.Copy(PluginPath, path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Info($"Discord voice: noise suppression's copy {n} could not be made ({ex.GetType().Name}).");
                return 0;
            }
            if (!NativeLibrary.TryLoad(path, out var library)) return 0;
            Copies.Add(library);
            return library;
        }
    }

    private readonly Descriptor _d;
    private readonly nint _instance;
    private readonly float* _input, _output;
    private readonly float* _controls;
    private readonly int _frame;
    private readonly int _limit = -1; // the attenuation limit's control port
    private readonly int _df = -1; // the deep filtering stage's threshold port (see Load)
    private readonly DelayTracker _delay = new();

    /// <summary>How long its runs take, and so whether it runs light (<see cref="Overload"/>).</summary>
    internal Overload Load { get; } = new();

    /// <summary>How far behind its input the cleaned sound runs, in samples at 48 kHz, as last measured (see <see cref="DelayTracker"/>).</summary>
    internal int DelaySamples => _delay.Samples;

    /// <summary>The level (dBFS) of the microphone's sound the last cleaned frame came from, once known (see <see cref="DelayTracker.InputDb"/>).</summary>
    internal float? InputDb => _delay.InputDb;

    // Its controls by the names v0.5.6 gives them, at the settings Exo runs: the post-filter off, no extra buffer, and
    // DeepFilterNet's own runtime thresholds for skipping its stages (no processing on silence, none beyond the first
    // stage on clean speech) where the plugin's defaults would run every stage on every frame.
    private static float? Setting(string name) => name switch
    {
        "Post Filter Beta" => 0f,
        "Min Processing Buffer (frames)" => 0f,
        "Min processing threshold (dB)" => -10f,
        "Max ERB processing threshold (dB)" => 30f,
        MaxDf => FullDf,
        _ => null,
    };

    // Light: the deep filtering stage's threshold at its lowest, so no frame runs it (DeepFilterNet's own advice for a CPU
    // too slow for it). Its first stage, the ERB gains, still takes the noise down, at the same delay, in the same instance.
    private const string MaxDf = "Max DF processing threshold (dB)";
    private const float FullDf = 20f, LightDf = -15f;

    private NoiseSuppressor(Descriptor d, nint instance, int frame)
    {
        _d = d;
        _instance = instance;
        _frame = frame;
        // Room for the warm-up's half second; a frame uses the start.
        _input = (float*)NativeMemory.AllocZeroed(WarmSamples, sizeof(float));
        _output = (float*)NativeMemory.AllocZeroed(WarmSamples, sizeof(float));
        _controls = (float*)NativeMemory.AllocZeroed(d.PortCount, sizeof(float));
        var connect = (delegate* unmanaged[Cdecl]<nint, uint, float*, void>)d.ConnectPort;
        var kinds = (int*)d.PortDescriptors;
        var hints = (RangeHint*)d.PortRangeHints;
        for (uint port = 0; port < d.PortCount; port++)
        {
            var kind = kinds[port];
            if ((kind & PortAudio) != 0) connect(instance, port, (kind & PortInput) != 0 ? _input : _output);
            else if ((kind & PortControl) != 0)
            {
                var name = Marshal.PtrToStringUTF8(((nint*)d.PortNames)[port]) ?? "";
                _controls[port] = Setting(name) ?? Default(hints[port]);
                if (name.Contains("Attenuation", StringComparison.OrdinalIgnoreCase)) { _controls[port] = GentleDb; _limit = (int)port; }
                if (name == MaxDf) _df = (int)port;
                connect(instance, port, _controls + port);
            }
        }
        if (d.Activate != 0) ((delegate* unmanaged[Cdecl]<nint, void>)d.Activate)(instance);
    }

    // The plugin runs each instance on a worker thread of its own that polls every 2 ms and never ends: deactivate and
    // cleanup leave it running, with its own copy of the model. So an instance is never cleaned up; one let go of waits
    // here for the next call, Voice Check or Voice Tune, instead of each leaving another thread waking 500 times a second.
    private static readonly Stack<NoiseSuppressor> Idle = new();
    private int _inUse = 1;

    /// <summary>A suppressor for 20 ms mono frames at 48 kHz (one let go of before, or new), or null when the plugin isn't there.</summary>
    internal static NoiseSuppressor? Create()
    {
        NoiseSuppressor? idle;
        lock (Idle) Idle.TryPop(out idle);
        if (idle is not null)
        {
            idle._inUse = 1;
            idle.Flush();
            return idle;
        }
        try
        {
            var library = LoadCopy();
            if (library == 0 || !NativeLibrary.TryGetExport(library, "ladspa_descriptor", out var entry)) return null;
            var descriptor = ((delegate* unmanaged[Cdecl]<uint, Descriptor*>)entry)(0);
            if (descriptor == null) return null;
            var d = *descriptor;
            var instance = ((delegate* unmanaged[Cdecl]<Descriptor*, uint, nint>)d.Instantiate)(descriptor, Opus.SampleRate);
            if (instance == 0) return null;
            RaiseWorkers(library);
            var suppressor = new NoiseSuppressor(d, instance, Opus.FrameSamples);
            suppressor.WarmUp();
            return suppressor;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            AppLog.Info("Discord voice: noise suppression unavailable (" + ex.GetType().Name + ").");
            return null;
        }
    }

    /// <summary>
    /// Cleans a 20 ms stereo frame in place, turning noise down by up to <paramref name="limitDb"/> (at most
    /// <see cref="MostDb"/>; Voice Tune sets less for a quiet room, which sounds more natural).
    /// </summary>
    internal void Process(float[] stereo, int limitDb = GentleDb)
    {
        if (_limit >= 0) _controls[_limit] = Math.Clamp(limitDb, 0, MostDb);
        if (_df >= 0) _controls[_df] = Load.Light ? LightDf : FullDf;
        for (var i = 0; i < _frame; i++) _input[i] = (stereo[i * 2] + stereo[i * 2 + 1]) * 0.5f;
        var started = Stopwatch.GetTimestamp();
        ((delegate* unmanaged[Cdecl]<nint, uint, void>)_d.Run)(_instance, (uint)_frame);
        Load.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        _delay.Add(new ReadOnlySpan<float>(_input, _frame), new ReadOnlySpan<float>(_output, _frame));
        for (var i = 0; i < _frame; i++) stereo[i * 2] = stereo[i * 2 + 1] = _output[i];
    }

    // The plugin's worker threads (each started in its own code) run at the capture thread's priority: with a game
    // taking the CPU, a worker left at normal priority keeps every run, and so the voice, waiting.
    private static void RaiseWorkers(nint library)
    {
        try
        {
            using var self = System.Diagnostics.Process.GetCurrentProcess();
            var module = self.Modules.Cast<ProcessModule>().FirstOrDefault(m => m.BaseAddress == library);
            if (module is null) return;
            var (from, to) = (library, library + module.ModuleMemorySize);
            foreach (ProcessThread thread in self.Threads)
            {
                try
                {
                    if (thread.StartAddress >= from && thread.StartAddress < to && thread.PriorityLevel != ThreadPriorityLevel.Highest)
                        thread.PriorityLevel = ThreadPriorityLevel.Highest;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { } // ended, or not ours to set
                finally { thread.Dispose(); }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            AppLog.Info("Discord voice: noise suppression's threads kept their priority (" + ex.GetType().Name + ").");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RangeHint
    {
        public int Hints;
        public float Lower, Upper;
    }

    // LADSPA default hints: the middle of the range, its bounds, or 0, 1, 100, 440.
    private static float Default(RangeHint h) => (h.Hints & 0x3C0) switch
    {
        0x40 => h.Lower,
        0x80 => h.Lower * 0.75f + h.Upper * 0.25f,
        0xC0 => (h.Lower + h.Upper) / 2,
        0x100 => h.Lower * 0.25f + h.Upper * 0.75f,
        0x140 => h.Upper,
        0x200 => 0,
        0x240 => 1,
        0x280 => 100,
        0x2C0 => 440,
        _ => h.Lower,
    };

    // A run that comes back later than real time grows the plugin's buffer by a hop (10 ms more delay) for the next
    // 20 s or so, and a new instance's first inference is the slow one. So it runs first on half a second of faint
    // noise in one go, which it can't be late for, before any frame of a call reaches it.
    private void WarmUp()
    {
        var noise = new Span<float>(_input, WarmSamples);
        var random = new Random(1);
        for (var i = 0; i < noise.Length; i++) noise[i] = 0.001f * (float)(random.NextDouble() * 2 - 1);
        ((delegate* unmanaged[Cdecl]<nint, uint, void>)_d.Run)(_instance, WarmSamples);
        Flush();
    }

    // 100 ms of silence through, so what the plugin still holds of its last user's sound (its 10 ms of processing delay,
    // more after a slow spell, and the 50 ms of past spectrum deep filtering reads) never comes out in the next one's frames.
    private void Flush()
    {
        new Span<float>(_input, _frame).Clear();
        for (var i = 0; i < 5; i++) ((delegate* unmanaged[Cdecl]<nint, uint, void>)_d.Run)(_instance, (uint)_frame);
        _delay.Reset();
        Load.Reset();
    }

    /// <summary>Lets go of it for the next user (see <see cref="Idle"/>); a second call does nothing.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _inUse, 0) == 0) return;
        lock (Idle) Idle.Push(this);
    }
}

/// <summary>
/// How far behind its input DeepFilterNet's output runs, measured from the sound: a whole number of its 10 ms hops (its
/// STFT's one, and however many its buffer holds), from 1 to <see cref="MostHops"/>. Each loud frame's output is
/// compared with the input that many hops back; once 10 loud frames agree on one, it is the delay. Pitch repeats
/// in a voice, so one frame can't tell a hop from the next; ten frames of a voice's pitch moving can. Until then
/// it is the plugin's starting delay, two hops. Nothing is allocated per frame.
/// </summary>
internal sealed class DelayTracker
{
    internal const int Hop = 480, MostHops = 4, Start = 2 * Hop;
    private const int Frame = Opus.FrameSamples, Votes = 10;
    private const float Loud = 1e-5f; // -50 dBFS mean power

    // The input of the last MostHops hops before this frame, then this frame's.
    private readonly float[] _history = new float[MostHops * Hop + Frame];
    private readonly double[] _score = new double[MostHops];
    private int _votes;

    /// <summary>The delay in samples at 48 kHz.</summary>
    internal int Samples { get; private set; } = Start;

    /// <summary>
    /// The level (dBFS) of the input the last output came from: the frame <see cref="Samples"/> back, so it lines up
    /// with what came out (the frame that went in is that much ahead of it). Null until that much has gone in since the
    /// start (what it holds before then is silence, or the last user's sound).
    /// </summary>
    internal float? InputDb { get; private set; }

    private int _frames; // since the start, counted up to MostHops

    /// <summary>One frame's input and the output it gave back.</summary>
    internal void Add(ReadOnlySpan<float> input, ReadOnlySpan<float> output)
    {
        var at = MostHops * Hop;
        input.CopyTo(_history.AsSpan(at));
        InputDb = null;
        if (_frames * Frame >= Samples)
        {
            double behind = 0;
            for (var n = at - Samples; n < at - Samples + Frame; n++) behind += _history[n] * _history[n];
            InputDb = MathF.Max(-100f, 10 * MathF.Log10((float)(behind / Frame) + 1e-10f));
        }
        if (_frames < MostHops) _frames++;
        double inPower = 0, outPower = 0;
        for (var n = 0; n < Frame; n++) { inPower += input[n] * input[n]; outPower += output[n] * output[n]; }
        if (inPower / Frame > Loud && outPower / Frame > Loud / 100)
        {
            for (var k = 0; k < MostHops; k++)
            {
                var from = at - (k + 1) * Hop;
                double xy = 0, xx = 0;
                for (var n = 0; n < Frame; n++)
                {
                    double x = _history[from + n];
                    xy += x * output[n];
                    xx += x * x;
                }
                _score[k] += xy / Math.Sqrt(xx * outPower + 1e-30);
            }
            if (++_votes == Votes) Decide();
        }
        _history.AsSpan(Frame).CopyTo(_history);
    }

    /// <summary>A new user: back to the plugin's starting delay until measured again.</summary>
    internal void Reset()
    {
        Array.Clear(_score);
        _votes = 0;
        _frames = 0;
        Samples = Start;
    }

    // The hop count the ten frames matched best, when it clearly did (its average correlation over 0.5, and 0.1 over the next).
    private void Decide()
    {
        int best = 0, next = -1;
        for (var k = 1; k < MostHops; k++) if (_score[k] > _score[best]) best = k;
        for (var k = 0; k < MostHops; k++) if (k != best && (next < 0 || _score[k] > _score[next])) next = k;
        if (_score[best] / Votes > 0.5 && (_score[best] - _score[next]) / Votes > 0.1) Samples = (best + 1) * Hop;
        Array.Clear(_score);
        _votes = 0;
    }
}

/// <summary>
/// Whether DeepFilterNet runs light, from how long each 20 ms run takes. A run waits on the plugin's worker for one
/// 10 ms hop's inference and its 2 ms polls: 4 to 6 ms at rest on a Ryzen 5 5600X, and no slower with every core busy
/// (24 burners at normal priority, or 12 at high), since Exo runs the worker above them. A worker that can't keep up
/// makes runs come back slow, and a late one makes the plugin add 10 ms of delay for 20 s or more (and at a second of it,
/// abort). So, as Krisp swaps its model for a smaller one once 20 frames in a row run over its line, it goes light when
/// <see cref="SlowRuns"/> of the last <see cref="Window"/> runs took over <see cref="SlowMs"/> (half the frame: the worker
/// at three quarters of its hop or more); one stall alone (a garbage collection, a busy moment elsewhere in Exo) never
/// does, which a late-run rule did at rest. Light is about 30% less CPU (25% of a core to 18% on that Ryzen, a fan behind
/// speech) at the same delay. It goes back once a span of runs had almost none slow (<see cref="SlowShare"/>): the span is
/// <see cref="Settle"/> runs (20 s, the plugin's own wait before it takes its added delay back) the first two times, then
/// longer each time it had to go light again, as Krisp's wait grows (Fibonacci: 20, 20, 40, 60, 100 s, at most 13 spans,
/// about 4 minutes), so a PC that stays busy doesn't swap back and forth; a new call starts over (<see cref="Reset"/>).
/// </summary>
internal sealed class Overload
{
    internal const int Window = 20, SlowRuns = 15, Settle = 1000;
    internal const double SlowMs = 10, SlowShare = 0.01;
    private readonly bool[] _last = new bool[Window];
    private int _at, _slowInWindow, _settled, _slow, _times;

    internal bool Light { get; private set; }

    /// <summary>How many runs it waits, light, before going back: <see cref="Settle"/> times a Fibonacci number of the times it went light.</summary>
    internal int SettleRuns
    {
        get
        {
            int a = 1, b = 1;
            for (var i = 1; i < _times; i++) (a, b) = (b, Math.Min(a + b, 13));
            return Settle * a;
        }
    }

    /// <summary>One run's time in milliseconds.</summary>
    internal void Add(double ms)
    {
        var slow = ms > SlowMs;
        if (Light)
        {
            if (slow) _slow++;
            if (++_settled < SettleRuns) return;
            if (_slow <= _settled * SlowShare) Back();
            (_settled, _slow) = (0, 0);
            return;
        }
        if (_last[_at]) _slowInWindow--;
        _last[_at] = slow;
        if (slow) _slowInWindow++;
        _at = (_at + 1) % Window;
        if (_slowInWindow < SlowRuns) return;
        Light = true;
        _times++;
        (_settled, _slow) = (0, 0);
    }

    private void Back()
    {
        Light = false;
        Array.Clear(_last);
        _slowInWindow = 0;
    }

    /// <summary>A new call: full again, and the first wait.</summary>
    internal void Reset()
    {
        Back();
        (_settled, _slow, _times) = (0, 0, 0);
    }
}
