using System.Net;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.RegularExpressions;
using ExoLauncher.Helpers;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace ExoLauncher.Services.DiscordClient;

/// <summary>
/// Discord's Copy Image and Save Image: a picture from Discord's own servers onto the Windows clipboard as a
/// bitmap, or into Downloads under the name Discord's app would give it.
/// </summary>
internal static partial class DiscordImages
{
    /// <summary>Discord's own pictures only: its CDN and its media proxies (attachments, embeds, emoji, avatars).</summary>
    internal static bool IsImageUrl(Uri url) =>
        url.Scheme == Uri.UriSchemeHttps && url.IsDefaultPort
        && (url.Host == "cdn.discordapp.com" || url.Host.EndsWith(".discordapp.net", StringComparison.OrdinalIgnoreCase));

    private const long MaxBytes = 100L * 1024 * 1024;

    internal static async Task<(byte[]? Bytes, string? ContentType, string? Error)> DownloadAsync(HttpClient http, Uri url, CancellationToken ct)
    {
        if (!IsImageUrl(url)) return (null, null, "That isn't a picture on Discord.");
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return (null, null, response.StatusCode == HttpStatusCode.NotFound ? "That picture isn't on Discord anymore." : "Discord didn't send the picture.");
            if (response.Content.Headers.ContentLength > MaxBytes) return (null, null, "That picture is too big.");
            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            return bytes.Length > MaxBytes ? (null, null, "That picture is too big.") : (bytes, response.Content.Headers.ContentType?.MediaType, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return (null, null, "Discord could not be reached.");
        }
    }

    /// <summary>
    /// The picture on the clipboard as a bitmap (and as PNG, which keeps transparency), the way Discord's app copies
    /// it: any format Windows reads (PNG, JPEG, WebP) becomes PNG. Runs on the UI thread, which owns the clipboard.
    /// </summary>
    internal static async Task<bool> CopyAsync(byte[] bytes)
    {
        try
        {
            using var source = new InMemoryRandomAccessStream();
            await source.WriteAsync(bytes.AsBuffer());
            source.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(source);
            using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            var png = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, png);
            encoder.SetSoftwareBitmap(bitmap);
            await encoder.FlushAsync();
            png.Seek(0);
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            var reference = RandomAccessStreamReference.CreateFromStream(png);
            package.SetBitmap(reference);
            package.SetData("PNG", reference);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Discord Copy Image: {ex.Message}");
            return false;
        }
    }

    /// <summary>Saves into Downloads, a number added when the name is taken; returns the name it got, or null.</summary>
    internal static string? Save(byte[] bytes, Uri url, string? contentType)
    {
        try
        {
            var folder = DownloadsFolder();
            Directory.CreateDirectory(folder);
            var name = FileName(url, contentType);
            var stem = Path.GetFileNameWithoutExtension(name);
            var extension = Path.GetExtension(name);
            var path = Path.Combine(folder, name);
            for (var i = 1; File.Exists(path); i++) path = Path.Combine(folder, $"{stem} ({i}){extension}");
            File.WriteAllBytes(path, bytes);
            return Path.GetFileName(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"Discord Save Image: {ex.Message}");
            return null;
        }
    }

    private static string DownloadsFolder()
    {
        try { return Windows.Storage.UserDataPaths.GetDefault().Downloads; }
        catch (Exception) { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"); }
    }

    /// <summary>
    /// Discord's app's name for a saved picture: the address's last part, decoded, with what follows the extension
    /// after a colon dropped, "name@png" read as "name.png", and characters Windows refuses as "_"; the proxy's
    /// format= sets the extension, and a name with none gets the picture's own type (else png).
    /// </summary>
    internal static string FileName(Uri url, string? contentType)
    {
        var last = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "unknown";
        string name;
        try { name = Uri.UnescapeDataString(last); }
        catch (UriFormatException) { name = last; }
        name = AfterExtension().Replace(name, "$1");
        name = AtExtension().Replace(name, "$1.$2");
        name = Refused().Replace(name, "_");
        var format = System.Web.HttpUtility.ParseQueryString(url.Query).Get("format");
        var cleanFormat = format is null ? "" : NotAlphanumeric().Replace(format, "").ToLowerInvariant();
        if (cleanFormat.Length > 0) name = $"{Path.GetFileNameWithoutExtension(name)}.{cleanFormat}";
        else if (!name.Contains('.', StringComparison.Ordinal)) name = $"{name}.{TypeExtension(contentType)}";
        return name.Trim('.', ' ').Length == 0 ? "image.png" : name;
    }

    private static string TypeExtension(string? contentType) => contentType switch
    {
        "image/jpeg" => "jpg",
        "image/gif" => "gif",
        "image/webp" => "webp",
        "image/avif" => "avif",
        "image/bmp" => "bmp",
        _ => "png",
    };

    [GeneratedRegex(@"(\.[a-zA-Z0-9]+):[^.]*$")]
    private static partial Regex AfterExtension();

    [GeneratedRegex(@"(.+)@([a-zA-Z0-9]+)$")]
    private static partial Regex AtExtension();

    [GeneratedRegex(@"[<>:""/\\|?*@\x00-\x1f]")]
    private static partial Regex Refused();

    [GeneratedRegex("[^a-zA-Z0-9]")]
    private static partial Regex NotAlphanumeric();
}
