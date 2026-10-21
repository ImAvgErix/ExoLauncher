using System.Collections.Concurrent;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using ExoLauncher.Helpers;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace ExoLauncher.Services;

/// <summary>
/// Grid thumbnails. Library cards draw at ~150–310 CSS px, but the cached
/// posters are up to 1200×1800, so every scroll decoded and uploaded the full
/// bitmap. A thumbnail is a downscale of the exact file the card would have
/// shown (same bytes, same art), keyed by that file's length and write time,
/// so a replaced or custom cover gets a fresh thumbnail on its next request.
/// Generated lazily off the UI thread; stored beside the covers, where the
/// normal cover-cache maintenance ages and evicts them.
///
/// The UI asks for twice the card's device pixels, so Chromium finishes with a
/// clean halving; the scale here is Lanczos-3 and the JPEG keeps full-resolution
/// color (4:4:4) at quality 95. WIC's Fant filter with 4:2:0 chroma at 1.3×
/// the card left covers visibly softer than the original drawn at card size.
/// </summary>
public static partial class CoverArtService
{
    public const string ThumbnailHost = "thumbs.exo-launcher.local";
    private const string ThumbnailPrefix = "thumb-";

    /// <summary>Widths the UI may ask for: 120 px steps up to the widest card at any DPR worth scaling.</summary>
    internal static bool IsThumbnailWidth(int width) => width is >= 240 and <= 720 && width % 120 == 0;

    private static readonly ConcurrentDictionary<string, Lazy<Task<string?>>> ThumbnailWork = new(StringComparer.OrdinalIgnoreCase);
    // Thumbnail names carry the source stamp, so "keep the original" is a stable answer.
    private static readonly ConcurrentDictionary<string, byte> ThumbnailNotNeeded = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim ThumbnailSlots = new(Math.Clamp(Environment.ProcessorCount / 2, 1, 4));

    internal readonly record struct ThumbnailBody(byte[] Bytes, string ContentType);

    /// <summary>
    /// Resolve https://thumbs.exo-launcher.local/&lt;cover file&gt;?w=&lt;width&gt; to bytes.
    /// Null means the cover itself is missing (the card moves to its next candidate).
    /// A source that is already small enough, not a JPEG, or fails to scale is served as is.
    /// </summary>
    internal static async Task<ThumbnailBody?> ReadThumbnailAsync(string coverName, int width)
    {
        if (!IsSafeCacheFileName(coverName) || coverName.StartsWith(ThumbnailPrefix, StringComparison.OrdinalIgnoreCase) ||
            IsTemporaryCacheName(coverName) || !IsThumbnailWidth(width))
            return null;
        var root = Path.GetFullPath(CacheRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var source = Path.GetFullPath(Path.Combine(root, coverName));
        if (!source.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;

        FileInfo info;
        try
        {
            info = new FileInfo(source);
            if (!info.Exists || info.Length < MinCoverBytes) return null;
        }
        catch { return null; }

        string? thumb = null;
        if (IsThumbnailSource(source))
        {
            var name = ThumbnailFileName(coverName, width, info.Length, info.LastWriteTimeUtc);
            var path = Path.Combine(root, name);
            if (File.Exists(path)) thumb = path;
            else if (!ThumbnailNotNeeded.ContainsKey(name))
            {
                var work = ThumbnailWork.GetOrAdd(name, key => new Lazy<Task<string?>>(() => CreateThumbnailAsync(source, path, width)));
                try { thumb = await work.Value.ConfigureAwait(false); }
                finally { ThumbnailWork.TryRemove(new KeyValuePair<string, Lazy<Task<string?>>>(name, work)); }
                if (thumb is null && ThumbnailNotNeeded.Count < 4096) ThumbnailNotNeeded.TryAdd(name, 0);
            }
        }

        var served = thumb ?? source;
        try
        {
            var bytes = await File.ReadAllBytesAsync(served).ConfigureAwait(false);
            return new ThumbnailBody(bytes, served == thumb ? "image/jpeg" : ContentTypeFor(served));
        }
        catch { return null; }
    }

    internal static string ContentTypeFor(string path) =>
        path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png"
        : path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ? "image/webp"
        : path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) ? "image/gif"
        : "image/jpeg";

    // JPEG only: PNG/GIF/WebP covers can carry transparency or animation a JPEG would lose.
    private static bool IsThumbnailSource(string path) =>
        path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);

    /// <summary>Bumped when the scaler changes, so thumbnails an older one made are replaced, not reused.</summary>
    private const int ThumbnailFormat = 2;

    internal static string ThumbnailFileName(string coverName, int width, long length, DateTime lastWriteUtc)
    {
        Span<byte> key = stackalloc byte[20];
        BitConverter.TryWriteBytes(key[..8], length);
        BitConverter.TryWriteBytes(key[8..16], lastWriteUtc.Ticks);
        BitConverter.TryWriteBytes(key[16..], ThumbnailFormat);
        var stamp = Convert.ToHexStringLower(SHA256.HashData(key)[..8]);
        return $"{ThumbnailPrefix}{width}-{stamp}-{Path.GetFileNameWithoutExtension(coverName)}.jpg";
    }

    private static async Task<string?> CreateThumbnailAsync(string source, string destination, int width)
    {
        await ThumbnailSlots.WaitAsync().ConfigureAwait(false);
        var temp = Path.Combine(Path.GetDirectoryName(destination)!, "~" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var size = ReadImageSize(source);
            if (size is null || !DimensionsAreSafe(size.Value.Width, size.Value.Height, MaximumDecodedImageSide))
                return null;

            byte[] encoded;
            using (var input = File.OpenRead(source))
            using (var stream = input.AsRandomAccessStream())
            {
                var decoder = await BitmapDecoder.CreateAsync(stream);
                var sourceWidth = (int)decoder.OrientedPixelWidth;
                var sourceHeight = (int)decoder.OrientedPixelHeight;
                if (sourceWidth < 1 || sourceHeight < 1) return null;
                // Cover the card in both directions: a poster wider than 2:3 is
                // cropped to the card's height, so height needs 1.5× the width.
                var scale = Math.Max(width / (double)sourceWidth, width * 1.5 / sourceHeight);
                // Within a quarter of the original, the original is the sharper answer.
                if (scale >= 0.75) return null;
                // The UI's poster test is an aspect threshold; never let rounding move a cover across it.
                var aspect = sourceWidth / (double)sourceHeight;
                if (Math.Abs(aspect - MaxCoverAspect) < 0.01) return null;

                var targetWidth = Math.Max(1, (int)Math.Round(sourceWidth * scale));
                var targetHeight = Math.Max(1, (int)Math.Round(sourceHeight * scale));
                // Decode at full size (oriented, color-managed to sRGB, as Chromium
                // draws the original). A huge source is first brought down to twice
                // the target, which the Lanczos pass then halves.
                var transform = new BitmapTransform();
                var decodedWidth = sourceWidth;
                var decodedHeight = sourceHeight;
                if ((long)sourceWidth * sourceHeight > 4096L * 4096)
                {
                    var rotated = decoder.OrientedPixelWidth != decoder.PixelWidth;
                    decodedWidth = targetWidth * 2;
                    decodedHeight = targetHeight * 2;
                    transform.ScaledWidth = (uint)(rotated ? decodedHeight : decodedWidth);
                    transform.ScaledHeight = (uint)(rotated ? decodedWidth : decodedHeight);
                    transform.InterpolationMode = BitmapInterpolationMode.Fant;
                }
                using var decoded = await decoder.GetSoftwareBitmapAsync(
                    BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform,
                    ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
                if (decoded.PixelWidth != decodedWidth || decoded.PixelHeight != decodedHeight) return null;
                var pixels = new byte[decodedWidth * decodedHeight * 4];
                decoded.CopyToBuffer(pixels.AsBuffer());
                var scaled = LanczosResample(pixels, decodedWidth, decodedHeight, targetWidth, targetHeight);
                using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(
                    scaled.AsBuffer(), BitmapPixelFormat.Bgra8, targetWidth, targetHeight, BitmapAlphaMode.Ignore);
                using var output = new InMemoryRandomAccessStream();
                var encoder = await CreateThumbnailEncoderAsync(output);
                encoder.SetSoftwareBitmap(bitmap);
                await encoder.FlushAsync();
                encoded = new byte[output.Size];
                await output.GetInputStreamAt(0).ReadAsync(encoded.AsBuffer(), (uint)encoded.Length, InputStreamOptions.None);
            }

            await File.WriteAllBytesAsync(temp, encoded).ConfigureAwait(false);
            File.Move(temp, destination, overwrite: true);
            DeleteOlderThumbnails(destination);
            NotifyOwnedArtworkWrite();
            return destination;
        }
        catch (Exception ex)
        {
            AppLog.Debug("Cover thumbnail failed for " + Path.GetFileName(source) + ": " + ex.Message);
            return null;
        }
        finally
        {
            try { File.Delete(temp); } catch { }
            ThumbnailSlots.Release();
        }
    }

    /// <summary>
    /// A small 2:3 JPEG of a cached cover (centre-cropped to fill the box, Fant,
    /// sRGB), for pages that can only take art inline. Null when it cannot be decoded.
    /// </summary>
    internal static async Task<byte[]?> EncodePosterAsync(string source, int width, int height, float quality)
    {
        try
        {
            var size = ReadImageSize(source);
            if (size is null || !DimensionsAreSafe(size.Value.Width, size.Value.Height, MaximumDecodedImageSide)) return null;
            await ThumbnailSlots.WaitAsync().ConfigureAwait(false);
            try
            {
                using var input = File.OpenRead(source);
                using var stream = input.AsRandomAccessStream();
                var decoder = await BitmapDecoder.CreateAsync(stream);
                var sourceWidth = (int)decoder.PixelWidth;
                var sourceHeight = (int)decoder.PixelHeight;
                if (sourceWidth < 1 || sourceHeight < 1) return null;
                var scale = Math.Max(width / (double)sourceWidth, height / (double)sourceHeight);
                var scaledWidth = Math.Max(width, (int)Math.Round(sourceWidth * scale));
                var scaledHeight = Math.Max(height, (int)Math.Round(sourceHeight * scale));
                var transform = new BitmapTransform
                {
                    ScaledWidth = (uint)scaledWidth,
                    ScaledHeight = (uint)scaledHeight,
                    InterpolationMode = BitmapInterpolationMode.Fant,
                    Bounds = new BitmapBounds
                    {
                        X = (uint)((scaledWidth - width) / 2), Y = (uint)((scaledHeight - height) / 2),
                        Width = (uint)width, Height = (uint)height,
                    },
                };
                using var bitmap = await decoder.GetSoftwareBitmapAsync(
                    BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform,
                    ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.ColorManageToSRgb);
                using var output = new InMemoryRandomAccessStream();
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output, new BitmapPropertySet
                {
                    { "ImageQuality", new BitmapTypedValue(quality, Windows.Foundation.PropertyType.Single) },
                });
                encoder.SetSoftwareBitmap(bitmap);
                await encoder.FlushAsync();
                var encoded = new byte[output.Size];
                await output.GetInputStreamAt(0).ReadAsync(encoded.AsBuffer(), (uint)encoded.Length, InputStreamOptions.None);
                return encoded;
            }
            finally { ThumbnailSlots.Release(); }
        }
        catch (Exception ex)
        {
            AppLog.Debug("Cover poster failed for " + Path.GetFileName(source) + ": " + ex.Message);
            return null;
        }
    }

    /// <summary>JPEG at quality 95 with unsubsampled chroma; quality alone if this WIC build refuses the chroma option.</summary>
    private static async Task<BitmapEncoder> CreateThumbnailEncoderAsync(IRandomAccessStream output)
    {
        var quality = new BitmapTypedValue(0.95f, Windows.Foundation.PropertyType.Single);
        try
        {
            return await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output, new BitmapPropertySet
            {
                { "ImageQuality", quality },
                // WICJpegYCrCbSubsampling444
                { "JpegYCrCbSubsampling", new BitmapTypedValue((byte)3, Windows.Foundation.PropertyType.UInt8) },
            });
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.Runtime.InteropServices.COMException)
        {
            AppLog.Debug("Cover thumbnail: JPEG 4:4:4 unavailable, using the encoder default: " + ex.Message);
            output.Size = 0;
            return await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output, new BitmapPropertySet { { "ImageQuality", quality } });
        }
    }

    /// <summary>
    /// Separable Lanczos-3 downscale of opaque BGRA8 pixels (alpha is written
    /// opaque). Done here rather than in WIC: WinRT's BitmapTransform offers
    /// Fant at best, and a JPEG decoder may shortcut it with DCT scaling; both
    /// soften the poster.
    /// </summary>
    private static byte[] LanczosResample(byte[] source, int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        var (xStart, xWeights) = LanczosContributions(sourceWidth, targetWidth);
        var (yStart, yWeights) = LanczosContributions(sourceHeight, targetHeight);

        // Horizontal pass: sourceHeight rows × targetWidth columns, three channels.
        var rows = new float[sourceHeight * targetWidth * 3];
        for (var y = 0; y < sourceHeight; y++)
        {
            var rowIn = y * sourceWidth * 4;
            var rowOut = y * targetWidth * 3;
            for (var x = 0; x < targetWidth; x++)
            {
                float b = 0, g = 0, r = 0;
                var weights = xWeights[x];
                var at = rowIn + xStart[x] * 4;
                for (var k = 0; k < weights.Length; k++, at += 4)
                {
                    var w = weights[k];
                    b += source[at] * w;
                    g += source[at + 1] * w;
                    r += source[at + 2] * w;
                }
                var o = rowOut + x * 3;
                rows[o] = b;
                rows[o + 1] = g;
                rows[o + 2] = r;
            }
        }

        // Vertical pass into opaque BGRA8.
        var result = new byte[targetWidth * targetHeight * 4];
        var stride = targetWidth * 3;
        for (var y = 0; y < targetHeight; y++)
        {
            var weights = yWeights[y];
            var first = yStart[y] * stride;
            for (var x = 0; x < targetWidth; x++)
            {
                float b = 0, g = 0, r = 0;
                var at = first + x * 3;
                for (var k = 0; k < weights.Length; k++, at += stride)
                {
                    var w = weights[k];
                    b += rows[at] * w;
                    g += rows[at + 1] * w;
                    r += rows[at + 2] * w;
                }
                var o = (y * targetWidth + x) * 4;
                result[o] = ToByte(b);
                result[o + 1] = ToByte(g);
                result[o + 2] = ToByte(r);
                result[o + 3] = 255;
            }
        }
        return result;

        static byte ToByte(float value) => (byte)Math.Clamp((int)MathF.Round(value), 0, 255);
    }

    /// <summary>First source index and normalised Lanczos-3 weights for each output index.</summary>
    private static (int[] Start, float[][] Weights) LanczosContributions(int sourceSize, int targetSize)
    {
        const double Lobes = 3;
        var scale = targetSize / (double)sourceSize;
        var filterScale = Math.Min(1, scale);
        var support = Lobes / filterScale;
        var start = new int[targetSize];
        var weights = new float[targetSize][];
        for (var i = 0; i < targetSize; i++)
        {
            var center = (i + 0.5) / scale;
            var first = Math.Max(0, (int)Math.Floor(center - support));
            var last = Math.Min(sourceSize - 1, (int)Math.Ceiling(center + support));
            var taps = new double[last - first + 1];
            var sum = 0.0;
            for (var j = first; j <= last; j++)
            {
                var w = Lanczos((j + 0.5 - center) * filterScale);
                taps[j - first] = w;
                sum += w;
            }
            var normalised = new float[taps.Length];
            for (var k = 0; k < taps.Length; k++) normalised[k] = (float)(sum == 0 ? 0 : taps[k] / sum);
            start[i] = first;
            weights[i] = normalised;
        }
        return (start, weights);

        static double Lanczos(double x)
        {
            x = Math.Abs(x);
            if (x < 1e-8) return 1;
            if (x >= Lobes) return 0;
            var px = Math.PI * x;
            return Lobes * Math.Sin(px) * Math.Sin(px / Lobes) / (px * px);
        }
    }

    /// <summary>A cover rewritten in place leaves its previous thumbnail behind; drop it now rather than at eviction.</summary>
    private static void DeleteOlderThumbnails(string current)
    {
        try
        {
            var name = Path.GetFileName(current);
            // thumb-<w>-<16 hex>-<cover>.jpg
            var widthEnd = name.IndexOf('-', ThumbnailPrefix.Length);
            if (widthEnd < 0) return;
            var head = name[..(widthEnd + 1)];
            var tail = name[(widthEnd + 1 + 16)..];
            foreach (var path in Directory.EnumerateFiles(Path.GetDirectoryName(current)!, head + "*" + tail, SearchOption.TopDirectoryOnly))
            {
                var other = Path.GetFileName(path);
                if (other.Length != name.Length || other.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                try { File.Delete(path); } catch { }
            }
        }
        catch { }
    }
}
