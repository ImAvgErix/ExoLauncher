using System.IO.Compression;
using System.Security.Cryptography;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// Keeps notification artwork local so an unlock toast does not depend on a
/// store CDN being reachable at the exact moment it appears.
/// </summary>
public sealed class AchievementIconCache
{
    private const int MaxImageBytes = 2 * 1024 * 1024;
    private const int MaxDimension = 4096;
    private const int MaxDecodedImageBytes = 32 * 1024 * 1024;
    private static readonly HashSet<string> ApprovedProviderImageHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "cdn.akamai.steamstatic.com",
        "cdn.cloudflare.steamstatic.com",
        "shared.steamstatic.com",
        "shared.akamai.steamstatic.com",
        "shared.cloudflare.steamstatic.com",
        "steamcdn-a.akamaihd.net",
        "shared-static-prod.epicgames.com",
        "cdn1.epicgames.com",
        "cdn2.unrealengine.com",
        "images.gog.com",
        "images.gog-statics.com",
    };
    private readonly string _root;
    private readonly HttpClient _http;

    public AchievementIconCache()
        : this(Path.Combine(PathHelper.AppDataDir, "achievement-icons"), null)
    {
    }

    internal AchievementIconCache(string root, HttpClient? http)
    {
        _root = root;
        _http = http ?? CreateHttpClient();
    }

    public async Task<string?> CacheAsync(string? source, CancellationToken cancellationToken = default)
    {
        if (!TryGetHttpsUri(source, out var uri)) return null;
        var key = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(uri.AbsoluteUri)))[..32].ToLowerInvariant();
        var cached = GetCachedPath(key);
        if (cached is not null) return cached;

        string? temporary = null;
        try
        {
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode ||
                response.RequestMessage?.RequestUri is not { } finalUri ||
                !TryGetHttpsUri(finalUri.AbsoluteUri, out _))
                return null;
            if (response.Content.Headers.ContentLength is > MaxImageBytes) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var block = new byte[16_384];
            while (true)
            {
                var read = await stream.ReadAsync(block, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                if (buffer.Length + read > MaxImageBytes) return null;
                buffer.Write(block, 0, read);
            }

            var bytes = buffer.ToArray();
            if (!TryValidateImage(bytes, out var extension)) return null;
            Directory.CreateDirectory(_root);
            var destination = Path.Combine(_root, key + extension);
            temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, destination, overwrite: true);
            temporary = null;
            return destination;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            AppLog.Debug("Achievement icon cache miss: " + ex.GetType().Name);
            return null;
        }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); } catch { }
            }
        }
    }

    internal static bool TryGetHttpsUri(string? source, out Uri uri)
    {
        if (Uri.TryCreate(source?.Trim(), UriKind.Absolute, out var parsed) &&
            parsed.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            parsed.IsDefaultPort &&
            string.IsNullOrEmpty(parsed.UserInfo) &&
            ApprovedProviderImageHosts.Contains(parsed.IdnHost))
        {
            uri = parsed;
            return true;
        }
        uri = null!;
        return false;
    }

    /// <summary>
    /// Canonicalizes provider-owned achievement art through the same exact
    /// host policy used by the native cache. Untrusted legacy values become
    /// null before persistence or WebView projection.
    /// </summary>
    internal static string? SanitizeProviderImageUrl(string? source) =>
        source is { Length: <= 2_048 } && TryGetHttpsUri(source, out var uri)
            ? uri.AbsoluteUri
            : null;

    private static HttpClient CreateHttpClient() => new(
        new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(3),
    };

    internal static bool TryValidateImage(ReadOnlySpan<byte> bytes, out string extension)
    {
        extension = string.Empty;
        if (bytes.Length >= 24 &&
            bytes[0] == 0x89 && bytes[1] == (byte)'P' && bytes[2] == (byte)'N' && bytes[3] == (byte)'G' &&
            bytes[4] == 0x0d && bytes[5] == 0x0a && bytes[6] == 0x1a && bytes[7] == 0x0a)
        {
            if (!TryValidatePng(bytes)) return false;
            extension = ".png";
            return true;
        }

        if (!TryValidateJpeg(bytes)) return false;
        extension = ".jpg";
        return true;
    }

    private static bool TryValidatePng(ReadOnlySpan<byte> bytes)
    {
        var offset = 8;
        var sawIhdr = false;
        var sawIdat = false;
        var width = 0;
        var height = 0;
        var bitDepth = 0;
        var colorType = 0;
        using var idat = new MemoryStream();

        while (offset <= bytes.Length - 12)
        {
            var length = ReadBigEndianUInt32(bytes[offset..]);
            if (length > MaxImageBytes || length > bytes.Length - offset - 12) return false;
            var chunkLength = (int)length;
            var dataOffset = offset + 8;
            var crcOffset = dataOffset + chunkLength;
            if (!CrcMatches(bytes, offset + 4, 4 + chunkLength, crcOffset)) return false;

            if (ChunkIs(bytes, offset + 4, "IHDR"))
            {
                if (sawIhdr || sawIdat || chunkLength != 13) return false;
                width = ReadBigEndianInt32(bytes.Slice(dataOffset, 4));
                height = ReadBigEndianInt32(bytes.Slice(dataOffset + 4, 4));
                bitDepth = bytes[dataOffset + 8];
                colorType = bytes[dataOffset + 9];
                if (!IsSafeDimensions(width, height) ||
                    bytes[dataOffset + 10] != 0 || bytes[dataOffset + 11] != 0 || bytes[dataOffset + 12] != 0 ||
                    !IsSupportedPngFormat(bitDepth, colorType))
                    return false;
                sawIhdr = true;
            }
            else if (ChunkIs(bytes, offset + 4, "IDAT"))
            {
                if (!sawIhdr || chunkLength == 0 || idat.Length + chunkLength > MaxImageBytes) return false;
                idat.Write(bytes.Slice(dataOffset, chunkLength));
                sawIdat = true;
            }
            else if (ChunkIs(bytes, offset + 4, "IEND"))
            {
                if (!sawIhdr || !sawIdat || chunkLength != 0 || crcOffset + 4 != bytes.Length) return false;
                return TryValidatePngPixels(idat.GetBuffer().AsSpan(0, (int)idat.Length), width, height, bitDepth, colorType);
            }
            else if ((bytes[offset + 4] & 0x20) == 0)
            {
                // Unknown critical chunks are not safe to pretend we understand.
                return false;
            }
            offset = crcOffset + 4;
        }
        return false;
    }

    private static bool TryValidatePngPixels(ReadOnlySpan<byte> compressed, int width, int height, int bitDepth, int colorType)
    {
        var samples = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
        if (samples == 0) return false;
        var rowBytes = ((long)width * samples * bitDepth + 7) / 8;
        var expectedLength = (rowBytes + 1) * height;
        if (expectedLength is <= 0 or > MaxDecodedImageBytes) return false;

        try
        {
            using var compressedStream = new MemoryStream(compressed.ToArray(), writable: false);
            using var zlib = new ZLibStream(compressedStream, CompressionMode.Decompress);
            var buffer = new byte[16_384];
            long decoded = 0;
            while (true)
            {
                var read = zlib.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                decoded += read;
                if (decoded > expectedLength) return false;
            }
            return decoded == expectedLength;
        }
        catch (InvalidDataException) { return false; }
    }

    private static bool TryValidateJpeg(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 12 || bytes[0] != 0xff || bytes[1] != 0xd8 ||
            bytes[^2] != 0xff || bytes[^1] != 0xd9)
            return false;

        var sawSof = false;
        var sawSos = false;
        for (var offset = 2; offset < bytes.Length - 1;)
        {
            if (bytes[offset] != 0xff) return false;
            while (offset < bytes.Length && bytes[offset] == 0xff) offset++;
            if (offset >= bytes.Length) return false;
            var marker = bytes[offset++];
            if (marker == 0xd9) return sawSof && sawSos && offset == bytes.Length;
            if (marker is 0xd8 or 0x01 || marker is >= 0xd0 and <= 0xd7) continue;
            if (offset + 2 > bytes.Length) return false;
            var length = (bytes[offset] << 8) | bytes[offset + 1];
            if (length < 2 || offset + length > bytes.Length) return false;
            if (marker is >= 0xc0 and <= 0xc3 or >= 0xc5 and <= 0xc7 or >= 0xc9 and <= 0xcb or >= 0xcd and <= 0xcf)
            {
                if (length < 8) return false;
                var height = (bytes[offset + 3] << 8) | bytes[offset + 4];
                var width = (bytes[offset + 5] << 8) | bytes[offset + 6];
                if (!IsSafeDimensions(width, height)) return false;
                sawSof = true;
            }
            if (marker == 0xda)
            {
                if (!sawSof || length < 8 || offset + length >= bytes.Length) return false;
                sawSos = true;
                // Entropy data may contain byte-stuffed 0xFF values; only the
                // terminal EOI marker is structurally relevant after SOS.
                return bytes[^2] == 0xff && bytes[^1] == 0xd9;
            }
            offset += length;
        }
        return false;
    }

    private static bool IsSupportedPngFormat(int bitDepth, int colorType) => colorType switch
    {
        0 => bitDepth is 1 or 2 or 4 or 8 or 16,
        2 or 4 or 6 => bitDepth is 8 or 16,
        3 => bitDepth is 1 or 2 or 4 or 8,
        _ => false,
    };

    private static bool ChunkIs(ReadOnlySpan<byte> bytes, int offset, string id) =>
        bytes[offset] == id[0] && bytes[offset + 1] == id[1] && bytes[offset + 2] == id[2] && bytes[offset + 3] == id[3];

    private static bool CrcMatches(ReadOnlySpan<byte> bytes, int start, int length, int crcOffset) =>
        ReadBigEndianUInt32(bytes[crcOffset..]) == Crc32(bytes.Slice(start, length));

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xffff_ffffu;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xedb8_8320u);
        }
        return ~crc;
    }

    private string? GetCachedPath(string key)
    {
        foreach (var extension in new[] { ".png", ".jpg" })
        {
            var candidate = Path.Combine(_root, key + extension);
            try
            {
                if (!File.Exists(candidate)) continue;
                var info = new FileInfo(candidate);
                if (info.Length is > 0 and <= MaxImageBytes &&
                    TryValidateImage(File.ReadAllBytes(candidate), out var detectedExtension) &&
                    string.Equals(extension, detectedExtension, StringComparison.OrdinalIgnoreCase))
                    return candidate;

                // A partial cache write must never become a permanent visual
                // failure. This exact cache candidate is safe to replace.
                File.Delete(candidate);
            }
            catch { }
        }
        return null;
    }

    private static bool IsSafeDimensions(int width, int height) =>
        width is > 0 and <= MaxDimension && height is > 0 and <= MaxDimension && (long)width * height <= 16_000_000;

    private static int ReadBigEndianInt32(ReadOnlySpan<byte> value) =>
        (value[0] << 24) | (value[1] << 16) | (value[2] << 8) | value[3];

    private static uint ReadBigEndianUInt32(ReadOnlySpan<byte> value) =>
        ((uint)value[0] << 24) | ((uint)value[1] << 16) | ((uint)value[2] << 8) | value[3];
}
