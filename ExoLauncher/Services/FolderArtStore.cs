using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using ExoLauncher.Helpers;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace ExoLauncher.Services;

/// <summary>
/// A Library folder's own picture. The folder sheet crops it to the folder's 2:3 shape and sends it here; Exo takes
/// a PNG, JPEG or WebP under 15 MB, decodes it in full and writes it again as a JPEG into its cover cache, so only
/// pixels it made itself are stored. Named for its folder and its content (folder-art-&lt;folder id&gt;-&lt;16 hex&gt;.jpg),
/// so a new picture has a new address; served from https://covers.exo-launcher.local/ like a custom cover, never
/// evicted by cache maintenance, and deleted once no folder holds it (the folders' save, or the next picture).
/// </summary>
internal sealed class FolderArtStore
{
    public const long MaxBytes = 15L * 1024 * 1024;
    public const int MinSide = 64;
    public const int MaxSide = 4096;
    internal const string Prefix = "folder-art-";
    private const int HashLength = 16;
    private const string Extension = ".jpg";
    private readonly string _root;

    internal sealed record SaveResult(bool Ok, string? FileName, string? Message);

    public FolderArtStore(string? root = null)
    {
        _root = Path.GetFullPath(string.IsNullOrWhiteSpace(root) ? CoverArtService.CacheRoot : root);
    }

    /// <summary>A folder id that can name a file: letters, digits, '-' and '_', 64 at most (Exo makes 8 of a–z and 0–9).</summary>
    internal static bool IsFolderId(string? id) =>
        id is { Length: > 0 and <= 64 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    public async Task<SaveResult> SaveAsync(string? folderId, byte[]? bytes, CancellationToken ct = default)
    {
        if (!IsFolderId(folderId)) return Failure("That folder can't take a picture.");
        if (bytes is null || bytes.Length < 24) return Failure("That file isn't a picture.");
        if (bytes.LongLength > MaxBytes) return Failure("Pictures have to be under 15 MB.");
        var decoderId = DecoderFor(bytes);
        if (decoderId is null) return Failure("Exo takes PNG, JPEG and WebP pictures.");

        byte[] encoded;
        try
        {
            encoded = await ReencodeAsync(bytes, decoderId.Value).ConfigureAwait(false) ?? throw new InvalidDataException();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (ShapeException ex) { return Failure(ex.Message); }
        catch (Exception ex)
        {
            AppLog.Debug("Folder picture could not be decoded: " + ex.GetType().Name);
            return Failure("Exo could not read that picture.");
        }

        var name = $"{Prefix}{folderId}-{Convert.ToHexStringLower(SHA256.HashData(encoded))[..HashLength]}{Extension}";
        var temporary = Path.Combine(_root, $"~{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(_root);
            await File.WriteAllBytesAsync(temporary, encoded, ct).ConfigureAwait(false);
            File.Move(temporary, Path.Combine(_root, name), overwrite: true);
            CoverArtService.NotifyOwnedArtworkWrite();
            return new SaveResult(true, name, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            AppLog.Debug("Folder picture could not be stored: " + ex.GetType().Name);
            return Failure("Exo could not store that picture.");
        }
        finally
        {
            try { File.Delete(temporary); } catch { }
        }
    }

    /// <summary>A folder picture's file name, from the name itself or its address on the covers host; null for anything else.</summary>
    internal static string? FileName(string? value)
    {
        var name = (value ?? "").Trim();
        var origin = CoverArtService.VirtualHostOrigin + "/";
        if (name.StartsWith(origin, StringComparison.OrdinalIgnoreCase)) name = name[origin.Length..];
        var idLength = name.Length - Prefix.Length - 1 - HashLength - Extension.Length;
        if (idLength < 1 || !name.StartsWith(Prefix, StringComparison.Ordinal) || !name.EndsWith(Extension, StringComparison.Ordinal) ||
            name[Prefix.Length + idLength] != '-' || !IsFolderId(name.Substring(Prefix.Length, idLength)))
            return null;
        foreach (var c in name.AsSpan(name.Length - Extension.Length - HashLength, HashLength))
            if (c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')) return null;
        return name;
    }

    /// <summary>The picture's file name when it is this folder's own; null otherwise.</summary>
    internal static string? FileNameFor(string? folderId, string? value) =>
        FileName(value) is { } name && IsFolderId(folderId) && name.Length == Prefix.Length + folderId!.Length + 1 + HashLength + Extension.Length &&
        name.AsSpan(Prefix.Length).StartsWith(folderId + "-", StringComparison.Ordinal)
            ? name
            : null;

    /// <summary>Its address for the page, or null when the file is gone (the folder shows its games' covers).</summary>
    public string? ResolveUrl(string? value) =>
        FileName(value) is { } name && File.Exists(Path.Combine(_root, name)) ? $"{CoverArtService.VirtualHostOrigin}/{name}" : null;

    /// <summary>Deletes every folder picture not named in <paramref name="keep"/> (a deleted folder's, a replaced or removed one).</summary>
    public void DeleteUnreferenced(IEnumerable<string?> keep)
    {
        var kept = keep.Select(FileName).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!Directory.Exists(_root)) return;
            foreach (var path in Directory.EnumerateFiles(_root, Prefix + "*" + Extension))
            {
                var name = Path.GetFileName(path);
                if (FileName(name) is null || kept.Contains(name)) continue;
                try { File.Delete(path); } catch (Exception ex) { AppLog.Debug("Folder picture could not be deleted: " + ex.GetType().Name); }
            }
        }
        catch (Exception ex) { AppLog.Debug("Folder pictures could not be listed: " + ex.GetType().Name); }
    }

    /// <summary>The decoder for the bytes' own signature (never the file's claim): PNG, JPEG or WebP.</summary>
    internal static Guid? DecoderFor(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 16 && bytes[..8].SequenceEqual((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]) &&
            bytes[12..16].SequenceEqual("IHDR"u8))
            return BitmapDecoder.PngDecoderId;
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return BitmapDecoder.JpegDecoderId;
        if (bytes.Length >= 16 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8))
            return BitmapDecoder.WebpDecoderId;
        return null;
    }

    /// <summary>The picture as a fresh JPEG (sRGB, quality 92, transparency over black), if it is a 2:3 picture of a sane size.</summary>
    private static async Task<byte[]?> ReencodeAsync(byte[] bytes, Guid decoderId)
    {
        using var input = new InMemoryRandomAccessStream();
        await input.WriteAsync(bytes.AsBuffer());
        input.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(decoderId, input);
        var width = (int)decoder.OrientedPixelWidth;
        var height = (int)decoder.OrientedPixelHeight;
        if (width < MinSide || height < MinSide) throw new ShapeException($"Choose a picture at least {MinSide} pixels on each side.");
        if (width > MaxSide || height > MaxSide) throw new ShapeException($"Choose a picture no larger than {MaxSide} pixels on a side.");
        if (Math.Abs(width / (double)height - 2.0 / 3.0) > 0.02) throw new ShapeException("Crop the picture to the folder's shape.");

        using var decoded = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, new BitmapTransform(),
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
        if (decoded.PixelWidth != width || decoded.PixelHeight != height) return null;
        // Premultiplied pixels read as opaque are the picture over black.
        var pixels = new byte[width * height * 4];
        decoded.CopyToBuffer(pixels.AsBuffer());
        using var opaque = SoftwareBitmap.CreateCopyFromBuffer(pixels.AsBuffer(), BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Ignore);
        using var output = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output, new BitmapPropertySet
        {
            { "ImageQuality", new BitmapTypedValue(0.92f, Windows.Foundation.PropertyType.Single) },
        });
        encoder.SetSoftwareBitmap(opaque);
        await encoder.FlushAsync();
        var encoded = new byte[output.Size];
        await output.GetInputStreamAt(0).ReadAsync(encoded.AsBuffer(), (uint)encoded.Length, InputStreamOptions.None);
        return encoded;
    }

    private static SaveResult Failure(string message) => new(false, null, message);

    /// <summary>A picture that decodes but is the wrong size or shape: its message reaches the sheet as is.</summary>
    private sealed class ShapeException(string message) : Exception(message);
}
