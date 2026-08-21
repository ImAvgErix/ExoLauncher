using System.Security.Cryptography;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// Copies one user-picked portrait into Exo-owned storage. Callers must obtain
/// <paramref name="sourcePath"/> from the native picker; browser-supplied paths
/// never cross this boundary.
/// </summary>
internal sealed class GameCoverImageStore
{
    public const long MaxBytes = 8L * 1024 * 1024;
    public const int MinSide = 64;
    public const int MaxSide = 4096;

    private const int MinBytes = 24;
    private const string Prefix = "custom-cover-";
    private readonly string _cacheRoot;

    internal sealed record ImportResult(bool Ok, string? FileName, bool Created, string? Message);

    public GameCoverImageStore(string? cacheRoot = null)
    {
        _cacheRoot = string.IsNullOrWhiteSpace(cacheRoot)
            ? CoverArtService.CacheRoot
            : Path.GetFullPath(cacheRoot);
    }

    public async Task<ImportResult> ImportAsync(string? sourcePath, CancellationToken ct = default)
    {
        var path = (sourcePath ?? string.Empty).Trim();
        if (path.Length == 0 || !Path.IsPathFullyQualified(path))
            return Failure("Pick an image file.");

        byte[] bytes;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return Failure("That file is not there any more.");
            if (info.Length < MinBytes) return Failure("That file is too small to be an image.");
            if (info.Length > MaxBytes) return Failure("Cover images have to be under 8 MB.");

            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            bytes = await ReadBoundedAsync(stream, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Failure("Exo could not open that file.");
        }

        if (bytes.Length < MinBytes) return Failure("That file is too small to be an image.");
        if (bytes.LongLength > MaxBytes) return Failure("Cover images have to be under 8 MB.");
        var extension = DetectFormat(bytes);
        if (extension is null) return Failure("Exo takes PNG and JPEG cover images.");

        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var fileName = $"{Prefix}{hash}{extension}";
        var temporaryPath = string.Empty;
        try
        {
            Directory.CreateDirectory(_cacheRoot);
            var destinationPath = OwnedPath(fileName);
            temporaryPath = Path.Combine(
                _cacheRoot,
                $"~{Path.GetFileNameWithoutExtension(fileName)}.{Guid.NewGuid():N}.tmp{extension}");
            await using (var output = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await output.WriteAsync(bytes, ct).ConfigureAwait(false);
                await output.FlushAsync(ct).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }

            var declaredSize = CoverArtService.ReadImageSize(temporaryPath);
            if (declaredSize is null)
                return DeleteTemporaryAndFail(temporaryPath, "Exo could not read that image.");
            if (declaredSize.Value.Width < MinSide || declaredSize.Value.Height < MinSide)
                return DeleteTemporaryAndFail(temporaryPath, $"Cover images must be at least {MinSide}×{MinSide}.");
            if (declaredSize.Value.Width > MaxSide || declaredSize.Value.Height > MaxSide)
                return DeleteTemporaryAndFail(temporaryPath, $"Cover images must be no larger than {MaxSide}×{MaxSide}.");
            if (declaredSize.Value.Width >= declaredSize.Value.Height ||
                declaredSize.Value.Width / (double)declaredSize.Value.Height > CoverArtService.MaxCoverAspect)
                return DeleteTemporaryAndFail(temporaryPath, "Choose a portrait cover image.");
            if (!CoverArtService.TryFullyDecodeImage(temporaryPath, MaxSide, out _))
                return DeleteTemporaryAndFail(temporaryPath, "Exo could not read that image.");

            if (File.Exists(destinationPath) && ContentMatches(destinationPath, bytes))
            {
                TryDelete(temporaryPath);
                temporaryPath = string.Empty;
                return new ImportResult(true, fileName, false, null);
            }

            // MoveFileEx replacement stays inside one volume/cache root, so
            // readers see either the previous complete file or this one.
            File.Move(temporaryPath, destinationPath, overwrite: true);
            temporaryPath = string.Empty;
            CoverArtService.NotifyOwnedArtworkWrite();
            return new ImportResult(true, fileName, true, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Debug("Custom cover storage failed: " + ex.GetType().Name);
            return Failure("Exo could not store that cover.");
        }
        finally
        {
            if (temporaryPath.Length > 0) TryDelete(temporaryPath);
        }
    }

    /// <summary>An exact content-addressed file Exo wrote, or null.</summary>
    public static string? FileName(string? value)
    {
        var name = (value ?? string.Empty).Trim();
        var extension = name.EndsWith(".png", StringComparison.Ordinal)
            ? ".png"
            : name.EndsWith(".jpg", StringComparison.Ordinal)
                ? ".jpg"
                : null;
        if (extension is null || name.Length != Prefix.Length + 64 + extension.Length) return null;
        if (!name.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        var hash = name.AsSpan(Prefix.Length, 64);
        foreach (var character in hash)
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')) return null;
        }
        return name;
    }

    public string? ResolveUrl(string? value)
    {
        var name = FileName(value);
        if (name is null) return null;
        return File.Exists(OwnedPath(name))
            ? $"{CoverArtService.VirtualHostOrigin}/{name}"
            : null;
    }

    public void Delete(string? value)
    {
        var name = FileName(value);
        if (name is not null) TryDelete(OwnedPath(name));
    }

    private string OwnedPath(string fileName)
    {
        var name = FileName(fileName) ?? throw new InvalidDataException("Invalid custom cover name.");
        var root = Path.GetFullPath(_cacheRoot).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, name));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Custom cover escaped its cache root.");
        return path;
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, CancellationToken ct)
    {
        using var buffer = new MemoryStream(capacity: (int)Math.Min(MaxBytes, Math.Max(MinBytes, stream.Length)));
        var chunk = new byte[64 * 1024];
        while (buffer.Length <= MaxBytes)
        {
            var remaining = (int)Math.Min(chunk.Length, MaxBytes + 1 - buffer.Length);
            var read = await stream.ReadAsync(chunk.AsMemory(0, remaining), ct).ConfigureAwait(false);
            if (read == 0) break;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static string? DetectFormat(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 24 &&
            bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 &&
            bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A &&
            bytes[12] == (byte)'I' && bytes[13] == (byte)'H' && bytes[14] == (byte)'D' && bytes[15] == (byte)'R')
            return ".png";
        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return ".jpg";
        return null;
    }

    private static ImportResult DeleteTemporaryAndFail(string path, string message)
    {
        TryDelete(path);
        return Failure(message);
    }

    private static ImportResult Failure(string message) => new(false, null, false, message);

    private static bool ContentMatches(string path, ReadOnlySpan<byte> expected)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != expected.Length) return false;
            var actual = File.ReadAllBytes(path);
            return actual.AsSpan().SequenceEqual(expected);
        }
        catch
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }
}
