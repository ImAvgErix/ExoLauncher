using System.Security.Cryptography;

namespace ExoLauncher.Services;

/// <summary>
/// Avatar and banner pictures the user picked from this PC.
///
/// The file is checked by its own header, then copied into Exo's cover cache and
/// served back through the cover virtual host — so a picture that later moves or
/// is deleted cannot break the profile, and the WebView never needs file://
/// access. This local store never uploads a picked file by itself.
/// </summary>
internal static class ProfileImageStore
{
    /// <summary>A picture, not a wallpaper collection. Anything larger is refused.</summary>
    public const long MaxBytes = 8L * 1024 * 1024;
    /// <summary>Below this an "image" is an icon, and it renders as mush at avatar size.</summary>
    public const int MinSide = 64;
    public const int AvatarMinSide = 256;
    // WebView decodes the source bitmap before CSS can scale it. A 4K-side cap
    // keeps expressive banners crisp while avoiding 8K decode/memory spikes.
    public const int MaxSide = 4096;

    private const long MinBytes = 128;
    private const string Prefix = "profile-";

    public static readonly string[] Slots = [
        "avatar", "banner", "gallery0", "gallery1", "gallery2", "gallery3", "gallery4", "gallery5",
    ];

    /// <summary>A stored file name, or the one honest reason the pick was refused.</summary>
    public sealed record Stored(string? FileName, string? Message);

    public static string? NormalizeSlot(string? kind)
    {
        var key = (kind ?? string.Empty).Trim().ToLowerInvariant();
        return Array.Exists(Slots, slot => slot == key) ? key : null;
    }

    /// <summary>
    /// Copies a picked PNG, JPEG, WebP, or GIF into the cover cache. The caller must have
    /// gotten <paramref name="sourcePath"/> from the host's own file picker — a
    /// path typed by the UI is never accepted.
    /// </summary>
    public static Stored Save(string? sourcePath, string? kind)
    {
        var slot = NormalizeSlot(kind);
        if (slot is null) return new Stored(null, "Unknown image slot.");

        var path = (sourcePath ?? string.Empty).Trim();
        if (path.Length == 0 || !Path.IsPathFullyQualified(path))
            return new Stored(null, "Pick an image file.");

        FileInfo info;
        try
        {
            info = new FileInfo(path);
        }
        catch
        {
            return new Stored(null, "Exo could not open that file.");
        }

        if (!info.Exists) return new Stored(null, "That file is not there any more.");
        if (info.Length < MinBytes) return new Stored(null, "That file is too small to be an image.");
        if (info.Length > MaxBytes)
            return new Stored(null, $"Images have to be under {MaxBytes / 1024 / 1024} MB.");

        var temporaryPath = string.Empty;
        try
        {
            Directory.CreateDirectory(CoverArtService.CacheRoot);
            temporaryPath = Path.Combine(
                CoverArtService.CacheRoot,
                $"~profile-{slot}.{Guid.NewGuid():N}.tmp");
            using (var input = new FileStream(
                       path,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read,
                       64 * 1024,
                       FileOptions.SequentialScan))
            using (var output = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       64 * 1024,
                       FileOptions.SequentialScan | FileOptions.WriteThrough))
            {
                var buffer = new byte[64 * 1024];
                long written = 0;
                while (true)
                {
                    var read = input.Read(buffer, 0, buffer.Length);
                    if (read == 0) break;
                    written += read;
                    if (written > MaxBytes)
                        return new Stored(null, $"Images have to be under {MaxBytes / 1024 / 1024} MB.");
                    output.Write(buffer, 0, read);
                }
                output.Flush(flushToDisk: true);
                if (written < MinBytes)
                    return new Stored(null, "That file is too small to be an image.");
            }

            var extension = ReadFormat(temporaryPath);
            if (extension is null) return new Stored(null, "Exo takes PNG, JPEG, WebP, and GIF.");
            var typedTemporaryPath = temporaryPath + extension;
            File.Move(temporaryPath, typedTemporaryPath);
            temporaryPath = typedTemporaryPath;
            var declaredSize = CoverArtService.ReadImageSize(temporaryPath);
            if (declaredSize is null) return new Stored(null, "Exo could not read that image.");
            var minimumSide = slot == "avatar" ? AvatarMinSide : MinSide;
            if (declaredSize.Value.Width < minimumSide || declaredSize.Value.Height < minimumSide)
                return new Stored(null, $"That image is under {minimumSide}×{minimumSide}.");
            if (declaredSize.Value.Width > MaxSide || declaredSize.Value.Height > MaxSide)
                return new Stored(null, $"That image is over {MaxSide}×{MaxSide}.");
            if (!CoverArtService.TryFullyDecodeImage(temporaryPath, MaxSide, out _))
                return new Stored(null, "Exo could not read that image.");

            // The name carries a content hash so replacing a picture cannot be
            // served from the WebView's cache of the old one.
            var name = $"{Prefix}{slot}-{ContentHash(temporaryPath)}{extension}";
            File.Move(
                temporaryPath,
                Path.Combine(CoverArtService.CacheRoot, name),
                overwrite: true);
            temporaryPath = string.Empty;
            CoverArtService.NotifyOwnedArtworkWrite();
            return new Stored(name, null);
        }
        catch
        {
            return new Stored(null, "Exo could not store that image.");
        }
        finally
        {
            try { if (temporaryPath.Length > 0 && File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch { }
        }
    }

    /// <summary>A stored name Exo wrote itself, or null. Never a path, never traversal.</summary>
    public static string? FileName(string? value)
    {
        var name = (value ?? string.Empty).Trim();
        if (name.Length == 0 || name.Length > 128) return null;
        if (!name.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        if (name.Contains("..", StringComparison.Ordinal)) return null;
        if (name.Contains('/') || name.Contains('\\') || name.Contains(':')) return null;
        return name;
    }

    /// <summary>Virtual-host URL for a stored picture that is still on disk, else null.</summary>
    public static string? ResolveUrl(string? value)
    {
        var name = FileName(value);
        if (name is null) return null;
        return File.Exists(Path.Combine(CoverArtService.CacheRoot, name))
            ? $"{CoverArtService.VirtualHostOrigin}/{name}"
            : null;
    }

    /// <summary>Removes a stored picture. Only ever touches files Exo wrote itself.</summary>
    public static void Delete(string? value)
    {
        var name = FileName(value);
        if (name is null) return;
        try
        {
            var path = Path.GetFullPath(Path.Combine(CoverArtService.CacheRoot, name));
            var root = Path.GetFullPath(CoverArtService.CacheRoot)
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return;
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // A picture Exo could not delete is stale, not fatal.
        }
    }

    /// <summary>The real format from the file's own bytes. The extension is not evidence.</summary>
    private static string? ReadFormat(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> head = stackalloc byte[12];
            if (stream.Read(head) < 12) return null;
            if (head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47
                && head[4] == 0x0D && head[5] == 0x0A && head[6] == 0x1A && head[7] == 0x0A)
                return ".png";
            if (head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF) return ".jpg";
            if (head[0] == (byte)'G' && head[1] == (byte)'I' && head[2] == (byte)'F' &&
                head[3] == (byte)'8' && (head[4] == (byte)'7' || head[4] == (byte)'9') && head[5] == (byte)'a')
                return ".gif";
            if (head[0] == (byte)'R' && head[1] == (byte)'I' && head[2] == (byte)'F' && head[3] == (byte)'F' &&
                head[8] == (byte)'W' && head[9] == (byte)'E' && head[10] == (byte)'B' && head[11] == (byte)'P')
                return ".webp";
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string ContentHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream))[..16].ToLowerInvariant();
    }
}
