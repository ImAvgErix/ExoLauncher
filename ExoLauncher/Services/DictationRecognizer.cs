using System.Runtime.InteropServices;
using System.Text;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// Dictation's recognizer: Parakeet (<see cref="DictationModel"/>) through sherpa-onnx's C interface, the same
/// sherpa-onnx-c-api.dll and onnxruntime.dll beside Exo that Only My Voice uses, on the CPU. Loaded as a take starts, so
/// it is ready when the take ends (a few seconds, and about a gigabyte of memory while loaded: the encoder alone is
/// 620 MB), and let go of after two minutes unused. One transcription at a time.
/// </summary>
internal static unsafe class DictationRecognizer
{
    private static readonly Lock Gate = new();
    private static nint _recognizer;
    private static Timer? _idle;
    private static readonly TimeSpan IdleFor = TimeSpan.FromMinutes(2);

    private static delegate* unmanaged[Cdecl]<nint, void> _destroyRecognizer, _destroyStream, _destroyResult;
    private static delegate* unmanaged[Cdecl]<nint, nint> _createStream, _result;
    private static delegate* unmanaged[Cdecl]<nint, int, float*, int, void> _accept;
    private static delegate* unmanaged[Cdecl]<nint, nint, void> _decode;

    /// <summary>Whether dictation can run on this PC: sherpa-onnx and ONNX Runtime are beside Exo (x64 builds ship them).</summary>
    internal static bool Available { get; } = RuntimeInformation.ProcessArchitecture == Architecture.X64
        && File.Exists(Path.Combine(AppContext.BaseDirectory, "onnxruntime.dll"))
        && File.Exists(Path.Combine(AppContext.BaseDirectory, "sherpa-onnx-c-api.dll"));

    /// <summary>The recognizer, loaded if it isn't (the model must be here and whole); whether it is loaded now.</summary>
    internal static bool Load()
    {
        lock (Gate) return TryLoadLocked();
    }

    private static bool TryLoadLocked()
    {
        try { return LoadLocked(); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            AppLog.Info("Dictation: the recognizer is unavailable (" + ex.GetType().Name + ").");
            return false;
        }
    }

    private static bool LoadLocked()
    {
        if (_recognizer != 0) { Touch(); return true; }
        if (!Available || !DictationModel.Ready()) return false;
        // Windows 11's own onnxruntime.dll (Windows ML) has another interface: the one beside Exo loads first, by full path.
        if (!NativeLibrary.TryLoad(Path.Combine(AppContext.BaseDirectory, "onnxruntime.dll"), out _)
            || !NativeLibrary.TryLoad(Path.Combine(AppContext.BaseDirectory, "sherpa-onnx-c-api.dll"), out var lib)) return false;
        if (!NativeLibrary.TryGetExport(lib, "SherpaOnnxCreateOfflineRecognizer", out var create)
            || !NativeLibrary.TryGetExport(lib, "SherpaOnnxDestroyOfflineRecognizer", out var destroyRecognizer)
            || !NativeLibrary.TryGetExport(lib, "SherpaOnnxCreateOfflineStream", out var createStream)
            || !NativeLibrary.TryGetExport(lib, "SherpaOnnxDestroyOfflineStream", out var destroyStream)
            || !NativeLibrary.TryGetExport(lib, "SherpaOnnxAcceptWaveformOffline", out var accept)
            || !NativeLibrary.TryGetExport(lib, "SherpaOnnxDecodeOfflineStream", out var decode)
            || !NativeLibrary.TryGetExport(lib, "SherpaOnnxGetOfflineStreamResult", out var result)
            || !NativeLibrary.TryGetExport(lib, "SherpaOnnxDestroyOfflineRecognizerResult", out var destroyResult))
            return false;
        _destroyRecognizer = (delegate* unmanaged[Cdecl]<nint, void>)destroyRecognizer;
        _createStream = (delegate* unmanaged[Cdecl]<nint, nint>)createStream;
        _destroyStream = (delegate* unmanaged[Cdecl]<nint, void>)destroyStream;
        _accept = (delegate* unmanaged[Cdecl]<nint, int, float*, int, void>)accept;
        _decode = (delegate* unmanaged[Cdecl]<nint, nint, void>)decode;
        _result = (delegate* unmanaged[Cdecl]<nint, nint>)result;
        _destroyResult = (delegate* unmanaged[Cdecl]<nint, void>)destroyResult;

        // SherpaOnnxOfflineRecognizerConfig (sherpa-onnx 1.13.8's c-api.h) on x64: feat_config {sample_rate, feature_dim}
        // at 0; model_config from 8: transducer {encoder, decoder, joiner} at 8/16/24, paraformer 32, nemo_ctc 40, whisper
        // 48 to 96 (four pointers, three ints, padded), tdnn 96, tokens 104, num_threads 112, debug 116, provider 120,
        // model_type 128, then the other models, the language model and decoding options. Everything left zero is
        // sherpa-onnx's default (16 kHz, greedy search; the NeMo model brings its own features). A page of zeros holds it
        // with room to spare.
        const int Size = 4096;
        var config = (byte*)NativeMemory.AllocZeroed(Size);
        try
        {
            fixed (byte* encoder = Utf8(DictationModel.PathOf(DictationModel.Encoder)))
            fixed (byte* decoder = Utf8(DictationModel.PathOf(DictationModel.Decoder)))
            fixed (byte* joiner = Utf8(DictationModel.PathOf(DictationModel.Joiner)))
            fixed (byte* tokens = Utf8(DictationModel.PathOf(DictationModel.Tokens)))
            fixed (byte* cpu = "cpu\0"u8)
            fixed (byte* type = "nemo_transducer\0"u8)
            {
                *(byte**)(config + 8) = encoder;
                *(byte**)(config + 16) = decoder;
                *(byte**)(config + 24) = joiner;
                *(byte**)(config + 104) = tokens;
                *(int*)(config + 112) = Threads;
                *(byte**)(config + 120) = cpu;
                *(byte**)(config + 128) = type;
                var started = Environment.TickCount64;
                _recognizer = ((delegate* unmanaged[Cdecl]<byte*, nint>)create)(config);
                if (_recognizer == 0) { AppLog.Info("Dictation: the speech model wouldn't load."); return false; }
                AppLog.Info($"Dictation: the speech model loaded in {Environment.TickCount64 - started} ms on {Threads} threads.");
            }
        }
        finally { NativeMemory.Free(config); }
        Touch();
        return true;
    }

    /// <summary>Half the cores, 2 to 8: a short take transcribes in about a second without taking the whole CPU.</summary>
    internal static int Threads => Math.Clamp(Environment.ProcessorCount / 2, 2, 8);

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text + "\0");

    // Two minutes unused: let go of (the next take loads it again).
    private static void Touch()
    {
        _idle ??= new Timer(_ => Unload(), null, Timeout.Infinite, Timeout.Infinite);
        _idle.Change(IdleFor, Timeout.InfiniteTimeSpan);
    }

    private static void Unload()
    {
        lock (Gate)
        {
            if (_recognizer == 0) return;
            _destroyRecognizer(_recognizer);
            _recognizer = 0;
            AppLog.Info("Dictation: the speech model was let go of after two minutes unused.");
        }
    }

    /// <summary>The words in 16 kHz mono sound, as the model writes them (punctuated, cased); null when it can't run.</summary>
    internal static string? Transcribe(ReadOnlySpan<float> voice16k)
    {
        lock (Gate)
        {
            if (!TryLoadLocked()) return null;
            var stream = _createStream(_recognizer);
            if (stream == 0) return null;
            try
            {
                fixed (float* samples = voice16k) _accept(stream, 16000, samples, voice16k.Length);
                _decode(_recognizer, stream);
                var result = _result(stream);
                if (result == 0) return null;
                try { return Marshal.PtrToStringUTF8(*(nint*)result) ?? ""; } // its first field: const char* text
                finally { _destroyResult(result); }
            }
            finally
            {
                _destroyStream(stream);
                Touch();
            }
        }
    }
}
