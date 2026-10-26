using System.Runtime.InteropServices;
using System.Text;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services.DiscordClient.Voice;

/// <summary>
/// NVIDIA's TitaNet-small speaker embedding (NeMo, CC-BY-4.0), as sherpa-onnx publishes it in ONNX, for
/// <see cref="MyVoice"/>: fetched once from sherpa-onnx's release (pinned size and SHA-256) and run through
/// sherpa-onnx's C interface (sherpa-onnx-c-api.dll and onnxruntime.dll beside Exo, from the
/// org.k2fsa.sherpa.onnx.runtime.win-x64 package). One extractor serves every chain, one embedding at a time;
/// each takes about 30 ms of one core for 1.5 s of voice, off the audio thread.
/// </summary>
internal static class SpeakerModel
{
    internal static readonly GitHubReleaseAsset Model = new(
        "k2-fsa", "sherpa-onnx", "speaker-recongition-models", "nemo_en_titanet_small.onnx",
        40257283, "ad4a1802485d8b34c722d2a9d04249662f2ece5d28a7a039063ca22f515a789e", PredatesGitHubDigests: true);

    internal static string ModelPath { get; set; } = Path.Combine(PathHelper.AppDataDir, "voice", "titanet_small.onnx");

    /// <summary>TitaNet as one of Exo's parts (<see cref="ExoComponents"/>): setup fetches it, so a teach never waits on it.</summary>
    internal static readonly ExoComponent Component = new("TitaNet voice model", "Only My Voice", Model.ExpectedSize, 0, Ready,
        (progress, ct) => VerifiedGitHubReleaseDownloader.Shared.DownloadPinnedAsync(Model, ModelPath, _ => true, ct, done => progress(done, unpacking: false)),
        () => ModelPath);

    private static (long Size, DateTime Written, bool Whole)? _checked;

    /// <summary>Whether the model is here and whole; hashes it only when the file changed since it last checked.</summary>
    internal static bool Ready()
    {
        var file = new FileInfo(ModelPath);
        if (!file.Exists) return false;
        if (_checked is { } last && last.Size == file.Length && last.Written == file.LastWriteTimeUtc) return last.Whole;
        var whole = VerifiedGitHubReleaseDownloader.IsPinnedAssetFile(Model, ModelPath, _ => true);
        _checked = (file.Length, file.LastWriteTimeUtc, whole);
        return whole;
    }

    /// <summary>Fetches the model if it isn't here (verified before it is kept); whether it is here now.</summary>
    internal static async Task<bool> EnsureAsync(CancellationToken ct)
    {
        if (Ready()) return true;
        try
        {
            await VerifiedGitHubReleaseDownloader.Shared.DownloadPinnedAsync(Model, ModelPath, _ => true, ct).ConfigureAwait(false);
            return Ready();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Info("Discord voice: the voice model wasn't fetched (" + ex.GetType().Name + ").");
            return false;
        }
    }

    private static readonly Lock Gate = new();
    private static nint _extractor;
    private static int _dim;
    private static unsafe delegate* unmanaged[Cdecl]<nint, nint> _createStream;
    private static unsafe delegate* unmanaged[Cdecl]<nint, int, float*, int, void> _accept;
    private static unsafe delegate* unmanaged[Cdecl]<nint, void> _finish, _destroyStream, _destroyEmbedding;
    private static unsafe delegate* unmanaged[Cdecl]<nint, nint, int> _ready;
    private static unsafe delegate* unmanaged[Cdecl]<nint, nint, float*> _compute;

    // Loads sherpa-onnx and the model once; under Gate.
    private static unsafe bool Load()
    {
        if (_extractor != 0) return true;
        // Windows 11 has its own, other onnxruntime.dll in System32 (Windows ML): the one beside Exo loads first, by full
        // path, so sherpa-onnx binds to it and not to Windows' (whose interface differs: it crashed the first run).
        if (!Ready() || !NativeLibrary.TryLoad(Path.Combine(AppContext.BaseDirectory, "onnxruntime.dll"), out _)
            || !NativeLibrary.TryLoad(Path.Combine(AppContext.BaseDirectory, "sherpa-onnx-c-api.dll"), out var lib)) return false;
        if (!NativeLibrary.TryGetExport(lib, "SherpaOnnxCreateSpeakerEmbeddingExtractor", out var create)
            || !NativeLibrary.TryGetExport(lib, "SherpaOnnxSpeakerEmbeddingExtractorDim", out var dim)
            || !NativeLibrary.TryGetExport(lib, "SherpaOnnxSpeakerEmbeddingExtractorCreateStream", out var createStream)
            || !NativeLibrary.TryGetExport(lib, "SherpaOnnxOnlineStreamAcceptWaveform", out var accept)
            || !NativeLibrary.TryGetExport(lib, "SherpaOnnxOnlineStreamInputFinished", out var finish)
            || !NativeLibrary.TryGetExport(lib, "SherpaOnnxSpeakerEmbeddingExtractorIsReady", out var ready)
            || !NativeLibrary.TryGetExport(lib, "SherpaOnnxSpeakerEmbeddingExtractorComputeEmbedding", out var compute)
            || !NativeLibrary.TryGetExport(lib, "SherpaOnnxSpeakerEmbeddingExtractorDestroyEmbedding", out var destroyEmbedding)
            || !NativeLibrary.TryGetExport(lib, "SherpaOnnxDestroyOnlineStream", out var destroyStream))
            return false;
        // SherpaOnnxSpeakerEmbeddingExtractorConfig: model (char*), num_threads, debug, provider (char*).
        var model = Encoding.UTF8.GetBytes(ModelPath + "\0");
        var config = stackalloc byte[24];
        fixed (byte* path = model)
        fixed (byte* cpu = "cpu"u8)
        {
            *(byte**)config = path;
            *(int*)(config + 8) = 1;
            *(int*)(config + 12) = 0;
            *(byte**)(config + 16) = cpu;
            _extractor = ((delegate* unmanaged[Cdecl]<byte*, nint>)create)(config);
        }
        if (_extractor == 0) { AppLog.Info("Discord voice: the voice model wouldn't load."); return false; }
        _dim = ((delegate* unmanaged[Cdecl]<nint, int>)dim)(_extractor);
        _createStream = (delegate* unmanaged[Cdecl]<nint, nint>)createStream;
        _accept = (delegate* unmanaged[Cdecl]<nint, int, float*, int, void>)accept;
        _finish = (delegate* unmanaged[Cdecl]<nint, void>)finish;
        _ready = (delegate* unmanaged[Cdecl]<nint, nint, int>)ready;
        _compute = (delegate* unmanaged[Cdecl]<nint, nint, float*>)compute;
        _destroyEmbedding = (delegate* unmanaged[Cdecl]<nint, void>)destroyEmbedding;
        _destroyStream = (delegate* unmanaged[Cdecl]<nint, void>)destroyStream;
        return true;
    }

    /// <summary>The voiceprint of 16 kHz mono voice, unit length; null when the model isn't here or can't judge it.</summary>
    internal static unsafe float[]? Embed(ReadOnlySpan<float> voice16k)
    {
        lock (Gate)
        {
            try
            {
                if (!Load()) return null;
                var stream = _createStream(_extractor);
                if (stream == 0) return null;
                try
                {
                    fixed (float* samples = voice16k) _accept(stream, 16000, samples, voice16k.Length);
                    _finish(stream);
                    if (_ready(_extractor, stream) == 0) return null;
                    var embedding = _compute(_extractor, stream);
                    if (embedding is null) return null;
                    try
                    {
                        var print = new ReadOnlySpan<float>(embedding, _dim).ToArray();
                        var length = MathF.Sqrt(print.Sum(x => x * x));
                        if (!(length > 0)) return null;
                        for (var i = 0; i < print.Length; i++) print[i] /= length;
                        return print;
                    }
                    finally { _destroyEmbedding((nint)embedding); }
                }
                finally { _destroyStream(stream); }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                AppLog.Info("Discord voice: the voice model is unavailable (" + ex.GetType().Name + ").");
                return null;
            }
        }
    }

}
