using System.Diagnostics;
using System.Security.Cryptography;
using ExoLauncher.Adapters;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// Dictation's speech model: NVIDIA's Parakeet TDT 0.6B v3 (NeMo, CC-BY-4.0; 25 European languages, with punctuation and
/// casing), as sherpa-onnx publishes it in ONNX with int8 weights. One archive from sherpa-onnx's asr-models release
/// (pinned size and SHA-256), unpacked with Windows' own tar into Exo's models folder, and each of the four files it
/// keeps checked against its own pinned size and SHA-256 before it is used. One of Exo's parts (<see cref="ExoComponents"/>):
/// setup fetches it.
/// </summary>
internal static class DictationModel
{
    internal static readonly GitHubReleaseAsset Archive = new(
        "k2-fsa", "sherpa-onnx", "asr-models", "sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8.tar.bz2",
        487_170_055, "5793d0fd397c5778d2cf2126994d58e9d56b1be7c04d13c7a15bb1b4eafb16bf");

    /// <summary>One file Exo keeps from the archive, pinned by its size and SHA-256.</summary>
    internal sealed record PinnedFile(string Name, long Size, string Sha256);

    internal static readonly PinnedFile Encoder = new("encoder.int8.onnx", 652_184_281, "acfc2b4456377e15d04f0243af540b7fe7c992f8d898d751cf134c3a55fd2247");
    internal static readonly PinnedFile Decoder = new("decoder.int8.onnx", 11_845_275, "179e50c43d1a9de79c8a24149a2f9bac6eb5981823f2a2ed88d655b24248db4e");
    internal static readonly PinnedFile Joiner = new("joiner.int8.onnx", 6_355_277, "3164c13fc2821009440d20fcb5fdc78bff28b4db2f8d0f0b329101719c0948b3");
    internal static readonly PinnedFile Tokens = new("tokens.txt", 93_939, "d58544679ea4bc6ac563d1f545eb7d474bd6cfa467f0a6e2c1dc1c7d37e3c35d");
    internal static readonly PinnedFile[] Files = [Encoder, Decoder, Joiner, Tokens];

    // The archive's own folder its files are in (the test sounds beside them are left in it).
    private const string ArchiveFolder = "sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8";

    internal static string Folder => Path.Combine(PathHelper.AppDataDir, "models", "parakeet-tdt-0.6b-v3");

    internal static string PathOf(PinnedFile file) => Path.Combine(Folder, file.Name);

    internal static readonly ExoComponent Component = new(
        "Parakeet speech model", "Dictation", Archive.ExpectedSize, Files.Sum(f => f.Size),
        () => Verify(Folder, Files), FetchAsync, () => Folder);

    /// <summary>Whether the model is here and whole.</summary>
    internal static bool Ready() => Verify(Folder, Files);

    // The stamp of each file last found whole is kept beside them (verified.txt), so a start doesn't read 640 MB again;
    // any change to a file (its bytes, size, times or identity) has it hashed again before it's trusted.
    private const string StampFile = "verified.txt";

    /// <summary>
    /// Whether every pinned file is in <paramref name="folder"/> at its size and SHA-256. Fails closed: a missing,
    /// short, long or changed file, or one that can't be read, is not ready.
    /// </summary>
    internal static bool Verify(string folder, IReadOnlyList<PinnedFile> files)
    {
        try
        {
            var stampPath = Path.Combine(folder, StampFile);
            var known = new Dictionary<string, string>(StringComparer.Ordinal);
            if (File.Exists(stampPath))
                foreach (var line in File.ReadLines(stampPath))
                    if (line.Split('\t') is [var name, var value]) known[name] = value;
            var now = new List<string>();
            var hashed = false;
            foreach (var file in files)
            {
                var path = Path.Combine(folder, file.Name);
                if (!File.Exists(path) || new FileInfo(path).Length != file.Size || PinnedToolCache.StampOf(path) is not { } stamp) return false;
                var key = file.Sha256 + " " + stamp;
                if (known.GetValueOrDefault(file.Name) != key)
                {
                    if (!Whole(path, file)) return false;
                    hashed = true;
                }
                now.Add(file.Name + "\t" + key);
            }
            if (hashed) File.WriteAllLines(stampPath, now);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Whether the file at <paramref name="path"/> is the pinned file, byte for byte.</summary>
    internal static bool Whole(string path, PinnedFile file)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
            if (stream.Length != file.Size) return false;
            return CryptographicOperations.FixedTimeEquals(SHA256.HashData(stream), Convert.FromHexString(file.Sha256));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            return false;
        }
    }

    // The archive (verified as it downloads), its four files unpacked into a folder of their own, each verified, then moved
    // into place; the archive and the unpacked rest go. A file that doesn't match its pin is never kept.
    private static async Task FetchAsync(ComponentProgress progress, CancellationToken ct)
    {
        var folder = Folder;
        Directory.CreateDirectory(folder);
        var archive = Path.Combine(folder, "parakeet.tar.bz2");
        var unpacked = Path.Combine(folder, ".unpack-" + Guid.NewGuid().ToString("N"));
        try
        {
            await VerifiedGitHubReleaseDownloader.Large.DownloadPinnedAsync(Archive, archive, _ => true, ct, done => progress(done, unpacking: false)).ConfigureAwait(false);
            Directory.CreateDirectory(unpacked);
            await UnpackAsync(archive, unpacked, progress, ct).ConfigureAwait(false);
            foreach (var file in Files)
                if (!Whole(Path.Combine(unpacked, ArchiveFolder, file.Name), file))
                    throw new InvalidDataException($"The speech model's {file.Name} didn't match its pin.");
            foreach (var file in Files)
                File.Move(Path.Combine(unpacked, ArchiveFolder, file.Name), PathOf(file), overwrite: true);
        }
        finally
        {
            try { if (File.Exists(archive)) File.Delete(archive); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            try { if (Directory.Exists(unpacked)) Directory.Delete(unpacked, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    // Windows' own tar (bsdtar, with bzip2): only the four files, named; the unpacked bytes so far are the measure.
    private static async Task UnpackAsync(string archive, string into, ComponentProgress progress, CancellationToken ct)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "tar.exe")) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in (string[])["-xjf", archive, "-C", into]) info.ArgumentList.Add(arg);
        foreach (var file in Files) info.ArgumentList.Add(ArchiveFolder + "/" + file.Name);
        using var tar = Process.Start(info) ?? throw new InvalidOperationException("Windows' tar didn't start.");
        var exited = tar.WaitForExitAsync(ct);
        while (await Task.WhenAny(exited, Task.Delay(250, ct)).ConfigureAwait(false) != exited)
            progress(Files.Sum(f => SizeOf(Path.Combine(into, ArchiveFolder, f.Name))), unpacking: true);
        await exited.ConfigureAwait(false);
        if (tar.ExitCode != 0) throw new InvalidDataException($"The speech model didn't unpack (tar {tar.ExitCode}).");
        progress(Files.Sum(f => f.Size), unpacking: true);
    }

    // Read from the file itself: a folder listing shows a file tar is still writing at its size when it was opened.
    private static long SizeOf(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch (IOException) { return 0; }
    }
}
