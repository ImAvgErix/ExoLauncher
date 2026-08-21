using System.Security.Cryptography;
using System.Text;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>A browser-safe reference to validated profile media stored by Exo.</summary>
internal sealed record ExoProfileMediaLocalRef(
    string FileName,
    string Url,
    string ContentType,
    long Size,
    string Sha256);

/// <summary>
/// Validates downloaded profile media before atomically promoting it into a
/// bounded local cache. The public-facing result never contains a native path
/// or the source download URL.
/// </summary>
internal sealed class ExoProfileMediaCache
{
    public const string DirectoryName = "online-profile-media";
    public const string VirtualHost = "profile-media.exo-launcher.local";
    public const long MaxAvatarBytes = 4L * 1024 * 1024;
    public const long MaxBannerBytes = 8L * 1024 * 1024;
    public const long MaxCacheBytes = 96L * 1024 * 1024;

    public static string VirtualHostOrigin => $"https://{VirtualHost}";

    private const string FilePrefix = "profile-";
    private const int HashCharacters = 64;
    private const int MaxIdentityCharacters = 1024;
    private static readonly TimeSpan StaleTemporaryFileAge = TimeSpan.FromHours(1);

    private readonly object _gate = new();
    private readonly string _root;

    internal ExoProfileMediaCache()
        : this(Path.Combine(PathHelper.AppDataDir, DirectoryName))
    {
    }

    internal ExoProfileMediaCache(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
    }

    internal async Task<ExoProfileMediaLocalRef?> TryStoreAsync(
        string immutableUserId,
        string kind,
        string version,
        Stream content,
        ExoProfileMediaMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(metadata);

        var normalizedKind = NormalizeKind(kind);
        var format = MediaFormat.FromContentType(metadata.ContentType);
        if (!ValidIdentityPart(immutableUserId) ||
            normalizedKind is null ||
            !ValidIdentityPart(version) ||
            format is null ||
            !content.CanRead ||
            metadata.Size <= 0 ||
            !MetadataMatches(metadata, normalizedKind, version))
            return null;

        var sizeLimit = normalizedKind == "avatar" ? MaxAvatarBytes : MaxBannerBytes;
        if (metadata.Size > sizeLimit ||
            !TryParseExpectedSha256(metadata.Sha256, out var expectedSha256))
            return null;

        var fileName = FileName(immutableUserId, normalizedKind, version, format.Extension);
        string temporary;
        lock (_gate)
        {
            EnsureRoot();
            CleanupTemporaryFiles();
            temporary = Path.Combine(
                _root,
                $".{Path.GetFileNameWithoutExtension(fileName)}.{Guid.NewGuid():N}.tmp");
        }

        byte[]? actualSha256 = null;
        try
        {
            var header = new byte[12];
            var headerLength = 0;
            long actualSize = 0;
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var destination = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough))
            {
                var buffer = new byte[64 * 1024];
                while (true)
                {
                    var read = await content.ReadAsync(buffer.AsMemory(), cancellationToken)
                        .ConfigureAwait(false);
                    if (read == 0)
                        break;
                    if (actualSize > sizeLimit - read)
                        return null;

                    if (headerLength < header.Length)
                    {
                        var copy = Math.Min(read, header.Length - headerLength);
                        Buffer.BlockCopy(buffer, 0, header, headerLength, copy);
                        headerLength += copy;
                    }

                    hasher.AppendData(buffer, 0, read);
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                        .ConfigureAwait(false);
                    actualSize += read;
                }

                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                destination.Flush(flushToDisk: true);
            }

            actualSha256 = hasher.GetHashAndReset();
            if (actualSize != metadata.Size ||
                !format.Matches(header.AsSpan(0, headerLength)) ||
                (expectedSha256 is not null &&
                 !CryptographicOperations.FixedTimeEquals(actualSha256, expectedSha256)))
                return null;

            var destinationPath = Path.Combine(_root, fileName);
            lock (_gate)
            {
                File.Move(temporary, destinationPath, overwrite: true);
                Prune(destinationPath);
            }

            var actualHex = Convert.ToHexString(actualSha256).ToLowerInvariant();
            return new ExoProfileMediaLocalRef(
                fileName,
                $"{VirtualHostOrigin}/{fileName}",
                format.ContentType,
                actualSize,
                actualHex);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (actualSha256 is not null)
                CryptographicOperations.ZeroMemory(actualSha256);
            if (expectedSha256 is not null)
                CryptographicOperations.ZeroMemory(expectedSha256);
            TryDelete(temporary);
        }
    }

    /// <summary>
    /// Returns a last-good entry only after revalidating its declared size,
    /// format signature, and optional server SHA-256. No source stream or
    /// native path crosses this cache boundary.
    /// </summary>
    internal ExoProfileMediaLocalRef? TryGet(
        string immutableUserId,
        string kind,
        string version,
        ExoProfileMediaMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        var normalizedKind = NormalizeKind(kind);
        var format = MediaFormat.FromContentType(metadata.ContentType);
        if (!ValidIdentityPart(immutableUserId) ||
            normalizedKind is null ||
            !ValidIdentityPart(version) ||
            format is null ||
            metadata.Size <= 0 ||
            !MetadataMatches(metadata, normalizedKind, version))
            return null;

        var sizeLimit = normalizedKind == "avatar" ? MaxAvatarBytes : MaxBannerBytes;
        if (metadata.Size > sizeLimit ||
            !TryParseExpectedSha256(metadata.Sha256, out var expectedSha256))
            return null;

        byte[]? actualSha256 = null;
        try
        {
            var fileName = FileName(immutableUserId, normalizedKind, version, format.Extension);
            lock (_gate)
            {
                var path = ResolvePath(fileName);
                if (path is null)
                    return null;

                using (var stream = new FileStream(
                           path,
                           FileMode.Open,
                           FileAccess.Read,
                           FileShare.Read | FileShare.Delete,
                           64 * 1024,
                           FileOptions.SequentialScan))
                {
                    if (stream.Length != metadata.Size || stream.Length > sizeLimit)
                        return null;

                    Span<byte> header = stackalloc byte[12];
                    var headerLength = 0;
                    while (headerLength < header.Length)
                    {
                        var read = stream.Read(header[headerLength..]);
                        if (read == 0)
                            break;
                        headerLength += read;
                    }
                    if (!format.Matches(header[..headerLength]))
                        return null;

                    stream.Position = 0;
                    actualSha256 = SHA256.HashData(stream);
                }

                if (expectedSha256 is not null &&
                    !CryptographicOperations.FixedTimeEquals(actualSha256, expectedSha256))
                    return null;

                try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); }
                catch { /* A valid fallback does not depend on an LRU timestamp touch. */ }
                return new ExoProfileMediaLocalRef(
                    fileName,
                    $"{VirtualHostOrigin}/{fileName}",
                    format.ContentType,
                    metadata.Size,
                    Convert.ToHexString(actualSha256).ToLowerInvariant());
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (actualSha256 is not null)
                CryptographicOperations.ZeroMemory(actualSha256);
            if (expectedSha256 is not null)
                CryptographicOperations.ZeroMemory(expectedSha256);
        }
    }

    /// <summary>
    /// Resolves one virtual-host request to a contained native path. Only names
    /// emitted by this cache are accepted; encoded or literal traversal fails.
    /// </summary>
    internal string? ResolvePath(string? requestPath)
    {
        var value = (requestPath ?? string.Empty).Trim();
        if (value.StartsWith('/'))
            value = value[1..];
        try { value = Uri.UnescapeDataString(value); }
        catch { return null; }
        if (!IsSafeFileName(value))
            return null;

        try
        {
            var root = Path.GetFullPath(_root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var candidate = Path.GetFullPath(Path.Combine(root, value));
            if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return null;
            return File.Exists(candidate) ? candidate : null;
        }
        catch
        {
            return null;
        }
    }

    internal void Clear()
    {
        lock (_gate)
        {
            if (!Directory.Exists(_root))
                return;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(_root, "*", SearchOption.TopDirectoryOnly).ToArray(); }
            catch { return; }
            foreach (var path in files)
            {
                var name = Path.GetFileName(path);
                if (IsSafeFileName(name) ||
                    name.StartsWith(".profile-", StringComparison.Ordinal) &&
                    name.EndsWith(".tmp", StringComparison.Ordinal))
                    TryDelete(path);
            }
        }
    }

    private static bool MetadataMatches(
        ExoProfileMediaMetadata metadata,
        string kind,
        string version) =>
        (string.IsNullOrWhiteSpace(metadata.Kind) ||
         string.Equals(metadata.Kind.Trim(), kind, StringComparison.OrdinalIgnoreCase)) &&
        (string.IsNullOrWhiteSpace(metadata.Version) ||
         string.Equals(metadata.Version, version, StringComparison.Ordinal));

    private static string? NormalizeKind(string value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized is "avatar" or "banner") return normalized;
        return normalized is { Length: 8 } &&
               normalized.StartsWith("gallery", StringComparison.Ordinal) &&
               normalized[7] is >= '0' and <= '5'
            ? normalized
            : null;
    }

    private static bool ValidIdentityPart(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= MaxIdentityCharacters &&
        !value.Contains('\0');

    private static string FileName(
        string immutableUserId,
        string kind,
        string version,
        string extension)
    {
        var material = Encoding.UTF8.GetBytes(immutableUserId + "\0" + kind + "\0" + version);
        try
        {
            var hash = Convert.ToHexString(SHA256.HashData(material)).ToLowerInvariant();
            return FilePrefix + hash + extension;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(material);
        }
    }

    private static bool TryParseExpectedSha256(string? value, out byte[]? expected)
    {
        expected = null;
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0)
            return true;
        if (text.StartsWith("sha256-", StringComparison.OrdinalIgnoreCase))
            text = text[7..];

        try
        {
            expected = text.Length == HashCharacters
                ? Convert.FromHexString(text)
                : Convert.FromBase64String(text);
            if (expected.Length == SHA256.HashSizeInBytes)
                return true;
            CryptographicOperations.ZeroMemory(expected);
            expected = null;
            return false;
        }
        catch (FormatException)
        {
            expected = null;
            return false;
        }
    }

    private void EnsureRoot()
    {
        Directory.CreateDirectory(_root);
    }

    private void Prune(string protectedPath)
    {
        List<FileInfo> files;
        try
        {
            files = Directory.EnumerateFiles(_root, "*", SearchOption.TopDirectoryOnly)
                .Where(path => IsSafeFileName(Path.GetFileName(path)))
                .Select(path => new FileInfo(path))
                .OrderBy(info => info.LastWriteTimeUtc)
                .ToList();
        }
        catch
        {
            return;
        }

        var bytes = files.Sum(SafeLength);
        foreach (var file in files)
        {
            if (bytes <= MaxCacheBytes)
                break;
            if (string.Equals(file.FullName, protectedPath, StringComparison.OrdinalIgnoreCase))
                continue;
            var length = SafeLength(file);
            if (TryDelete(file.FullName))
                bytes = Math.Max(0, bytes - length);
        }
    }

    private void CleanupTemporaryFiles()
    {
        IEnumerable<string> temporaryFiles;
        try
        {
            temporaryFiles = Directory.EnumerateFiles(
                _root,
                ".profile-*.tmp",
                SearchOption.TopDirectoryOnly);
        }
        catch
        {
            return;
        }

        foreach (var path in temporaryFiles)
        {
            try
            {
                if (File.GetLastWriteTimeUtc(path) > DateTime.UtcNow - StaleTemporaryFileAge)
                    continue;
            }
            catch
            {
                continue;
            }
            TryDelete(path);
        }
    }

    private static bool IsSafeFileName(string value)
    {
        if (!value.StartsWith(FilePrefix, StringComparison.Ordinal) ||
            value.Contains('/') || value.Contains('\\') || value.Contains(':') ||
            value.Contains("..", StringComparison.Ordinal))
            return false;

        var extension = Path.GetExtension(value);
        if (extension is not ".png" and not ".jpg" and not ".webp" and not ".gif")
            return false;
        var hash = value.AsSpan(FilePrefix.Length, value.Length - FilePrefix.Length - extension.Length);
        if (hash.Length != HashCharacters)
            return false;
        foreach (var character in hash)
        {
            if (!IsLowerHex(character))
                return false;
        }
        return true;
    }

    private static bool IsLowerHex(char value) =>
        value is >= '0' and <= '9' or >= 'a' and <= 'f';

    private static long SafeLength(FileInfo file)
    {
        try { return file.Length; }
        catch { return 0; }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
            return !File.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    private sealed record MediaFormat(string ContentType, string Extension)
    {
        internal static MediaFormat? FromContentType(string? value)
        {
            var contentType = (value ?? string.Empty).Split(';', 2)[0].Trim().ToLowerInvariant();
            return contentType switch
            {
                "image/png" => new MediaFormat("image/png", ".png"),
                "image/jpeg" => new MediaFormat("image/jpeg", ".jpg"),
                "image/webp" => new MediaFormat("image/webp", ".webp"),
                "image/gif" => new MediaFormat("image/gif", ".gif"),
                _ => null,
            };
        }

        internal bool Matches(ReadOnlySpan<byte> header) => ContentType switch
        {
            "image/png" => header.Length >= 8 &&
                           header[0] == 0x89 && header[1] == 0x50 &&
                           header[2] == 0x4e && header[3] == 0x47 &&
                           header[4] == 0x0d && header[5] == 0x0a &&
                           header[6] == 0x1a && header[7] == 0x0a,
            "image/jpeg" => header.Length >= 3 &&
                            header[0] == 0xff && header[1] == 0xd8 && header[2] == 0xff,
            "image/webp" => header.Length >= 12 &&
                            header[0] == (byte)'R' && header[1] == (byte)'I' &&
                            header[2] == (byte)'F' && header[3] == (byte)'F' &&
                            header[8] == (byte)'W' && header[9] == (byte)'E' &&
                            header[10] == (byte)'B' && header[11] == (byte)'P',
            "image/gif" => header.Length >= 6 &&
                           header[0] == (byte)'G' && header[1] == (byte)'I' &&
                           header[2] == (byte)'F' && header[3] == (byte)'8' &&
                           (header[4] == (byte)'7' || header[4] == (byte)'9') && header[5] == (byte)'a',
            _ => false,
        };
    }
}
