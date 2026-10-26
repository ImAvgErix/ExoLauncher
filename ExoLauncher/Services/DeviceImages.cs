using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ExoLauncher.Helpers;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace ExoLauncher.Services;

/// <summary>
/// The Devices room's pictures: each device's official product shot, found by model in Exo's own list
/// (Assets/device-images.json holds links only: the pictures are their makers', and Exo never ships them), fetched
/// once from the maker's image server and made to look like one set. A white or flat light studio background that
/// reaches the photo's edges is cut away (whites inside the product stay) as a soft matte, a cable the photo's frame
/// cuts fades out, and the product is fitted, centered, into one transparent 1200x800 frame with the same margin on
/// every picture (never enlarged past the photo's own pixels). The result is kept as a PNG in device-images under Exo's data folder and shown
/// through the devices virtual host (MainWindow). Every failure is a quiet null: the room draws the device itself.
/// </summary>
internal static class DeviceImages
{
    internal const string VirtualHost = "devices.exo-launcher.local";
    /// <summary>Part of every kept file's name: bumped when the look changes, so pictures an older look made are made again.</summary>
    internal const int PipelineVersion = 1;
    internal const int CanvasWidth = 1200;
    internal const int CanvasHeight = 800;
    /// <summary>The clear margin on every side of the product, as a share of the frame's height (64 px).</summary>
    internal const double PaddingShare = 0.08;
    internal const int MaxDownloadBytes = 8 * 1024 * 1024;
    /// <summary>How far (largest channel difference) a pixel may be from the background's color and still be background.</summary>
    internal const int Tolerance = 28;
    /// <summary>The opacity from which a pixel is part of the product when finding its bounds (a faint shadow is not).</summary>
    internal const int SolidAlpha = 40;
    /// <summary>A larger photo is brought down to this on decode: the frame never needs more.</summary>
    private const int MaxDecodeSide = 2048;
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(30);

    internal static string CacheRoot => Path.Combine(PathHelper.AppDataDir, "device-images");

    /// <param name="Kind">mouse, keyboard, controller or audio, as devices.list reports it.</param>
    /// <param name="Variant">A controller's color or a keyboard's layout; null for a model's only picture.</param>
    /// <param name="View">How the maker shot it (for whoever edits the list).</param>
    /// <param name="Page">The maker's page the picture comes from.</param>
    /// <param name="Ids">The model ids devices.list reports for it ("dualsense-edge").</param>
    /// <param name="Aliases">Other names the product goes by, matched like <paramref name="Model"/>.</param>
    /// <param name="Cut">How this photo's background comes away, beyond the edge flood every photo gets.</param>
    internal sealed record Entry(string Kind, string Maker, string Model, string? Variant, string Url, string View, string Page,
        string[]? Ids = null, string[]? Aliases = null, Cut Cut = default);

    /// <summary>Per photo, because neither is safe for every one.</summary>
    /// <param name="Shadow">The studio's soft shadow goes too: a smooth darkening of the background's own tint, followed out from the background (a light silver cable could be taken for one, so only where a photo has such a shadow).</param>
    /// <param name="Holes">Background seen through a closed loop (an IEM's ear hook, a coiled cable) goes too; otherwise whites inside the product stay.</param>
    internal readonly record struct Cut(bool Shadow = false, bool Holes = false);

    /// <summary>What the page knows about a device: devices.image's params.</summary>
    internal sealed record Request(string? Kind = null, string? Model = null, string? Name = null, string? Maker = null,
        string? Color = null, string? Layout = null);

    private sealed record CatalogFile(Entry[]? Devices);

    private static readonly JsonSerializerOptions CatalogJson = new() { PropertyNameCaseInsensitive = true };
    private static readonly Lazy<IReadOnlyList<Entry>> Shipped =
        new(() => Load(Path.Combine(AppContext.BaseDirectory, "Assets", "device-images.json")));

    internal static IReadOnlyList<Entry> Catalog => Shipped.Value;

    /// <summary>The list at <paramref name="path"/>, less any entry whose link isn't to an approved image server; empty when unreadable.</summary>
    internal static IReadOnlyList<Entry> Load(string path)
    {
        try
        {
            var file = JsonSerializer.Deserialize<CatalogFile>(File.ReadAllText(path), CatalogJson);
            return [.. (file?.Devices ?? []).Where(e => !string.IsNullOrWhiteSpace(e.Kind) && Key(e.Model).Length > 0 &&
                                                        Key(e.Maker).Length > 0 && IsApprovedUrl(e.Url))];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            AppLog.Warn("Device pictures: the list couldn't be read: " + ex.GetType().Name);
            return [];
        }
    }

    /// <summary>
    /// The picture for a device: by its model (a devices.list id or a model's name) and variant (color, layout), else
    /// that model's default; else the longest model name or alias its maker, name and model contain ("Ziigaat Lush",
    /// "Wooting 60HE v2"). Null when the list has nothing for it.
    /// </summary>
    internal static Entry? Resolve(IReadOnlyList<Entry> catalog, Request request)
    {
        var kind = request.Kind?.Trim() ?? "";
        var pool = catalog.Where(e => kind.Length == 0 || e.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase)).ToArray();
        var model = Key(request.Model);
        if (model.Length > 0 &&
            pool.Where(e => Key(e.Model) == model || (e.Ids ?? []).Any(id => Key(id) == model)).ToArray() is { Length: > 0 } byModel)
            return Variant(byModel, request);

        // '|' never survives Key, so a name is never matched across two fields.
        var maker = Key(request.Maker);
        var said = maker + "|" + Key(request.Name) + "|" + model;
        Entry? best = null;
        var bestLength = 0;
        foreach (var entry in pool)
            foreach (var name in (string[])[entry.Model, .. entry.Aliases ?? []])
            {
                var key = Key(name);
                if (key.Length <= bestLength || !said.Contains(key, StringComparison.Ordinal)) continue;
                // The maker has to agree when one is known; without one, only a long, distinctive name counts.
                if (!said.Contains(Key(entry.Maker), StringComparison.Ordinal) && (maker.Length > 0 || key.Length < 6)) continue;
                best = entry;
                bestLength = key.Length;
            }
        return best is null ? null : Variant([.. pool.Where(e => e.Maker == best.Maker && e.Model == best.Model)], request);
    }

    /// <summary>The variant the device reports, else one of the same layout family (split spacebar or not), else the model's first.</summary>
    private static Entry Variant(Entry[] entries, Request request)
    {
        var color = Key(request.Color);
        var layout = Key(request.Layout);
        var best = entries[0];
        var bestScore = -1;
        foreach (var entry in entries)
        {
            var variant = Key(entry.Variant);
            var score = variant.Length == 0 ? 1
                : variant == color || variant == layout ? 3
                : layout.Length > 0 && IsLayout(variant) && variant.EndsWith("split", StringComparison.Ordinal) == layout.EndsWith("split", StringComparison.Ordinal) ? 2
                : 0;
            if (score <= bestScore) continue;
            best = entry;
            bestScore = score;
        }
        return best;

        static bool IsLayout(string key) =>
            key.StartsWith("ansi", StringComparison.Ordinal) || key.StartsWith("iso", StringComparison.Ordinal) || key.StartsWith("jis", StringComparison.Ordinal);
    }

    /// <summary>Lowercase letters and digits only, '+' spelled out: "60HE+" is "60heplus", "Zero:Red" "zerored".</summary>
    internal static string Key(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var key = new StringBuilder(text.Length + 4);
        foreach (var ch in text)
        {
            if (ch == '+') key.Append("plus");
            else if (char.IsAsciiLetterOrDigit(ch)) key.Append(char.ToLowerInvariant(ch));
        }
        return key.ToString();
    }

    /// <summary>https on the default port, no user info, and a host on PRIVACY.md's device pictures row.</summary>
    internal static bool IsApprovedUrl([NotNullWhen(true)] string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) &&
        NetworkDestinations.Hosts.Any(d => d.PrivacyRow == NetworkDestinations.DevicePictures && NetworkDestinations.Covers(d.Host, uri.IdnHost));

    private static readonly ConcurrentDictionary<string, Lazy<Task<bool>>> Work = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, DateTime> Failed = new(StringComparer.Ordinal);
    private static readonly SemaphoreSlim Slots = new(2);
    private static readonly Lazy<HttpClient> Http = new(CreateHttp);
    private static int _swept;

    /// <summary>The page's address of the device's picture, made and kept on the first ask; null when Exo has none for it.</summary>
    internal static async Task<string?> UrlAsync(Request request, CancellationToken cancellationToken)
    {
        if (Resolve(Catalog, request) is not { } entry) return null;
        var name = FileName(entry.Url, entry.Cut);
        var path = Path.Combine(CacheRoot, name);
        if (File.Exists(path)) return PageUrl(name);
        if (Failed.TryGetValue(name, out var failedAt) && DateTime.UtcNow - failedAt < RetryAfter) return null;
        if (Interlocked.Exchange(ref _swept, 1) == 0) Sweep();
        var work = Work.GetOrAdd(name, _ => new Lazy<Task<bool>>(() => MakeAsync(entry.Url, entry.Cut, path)));
        try
        {
            if (await work.Value.WaitAsync(cancellationToken).ConfigureAwait(false)) return PageUrl(name);
            Failed[name] = DateTime.UtcNow;
            return null;
        }
        finally
        {
            if (work.Value.IsCompleted) Work.TryRemove(new KeyValuePair<string, Lazy<Task<bool>>>(name, work));
        }
    }

    private static string PageUrl(string name) => "https://" + VirtualHost + "/" + name;

    /// <summary>The kept file for a link: its hash with how it's cut and the look's version, so any change is a new file.</summary>
    internal static string FileName(string url, Cut cut = default) =>
        $"v{PipelineVersion}-{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{url}\n{cut.Shadow}{cut.Holes}\n{PipelineVersion}"))[..12])}.png";

    /// <summary>Pictures an older look made, and leftovers of an interrupted write.</summary>
    private static void Sweep()
    {
        try
        {
            if (!Directory.Exists(CacheRoot)) return;
            foreach (var file in Directory.EnumerateFiles(CacheRoot))
                if (!Path.GetFileName(file).StartsWith($"v{PipelineVersion}-", StringComparison.Ordinal) ||
                    file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                    File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Fetch, make and keep one picture; false on any failure (logged, never shown).</summary>
    internal static async Task<bool> MakeAsync(string url, Cut cut, string path)
    {
        var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.IdnHost : "?";
        try
        {
            if (await DownloadAsync(Http.Value, url, CancellationToken.None).ConfigureAwait(false) is not { } source)
            {
                AppLog.Info($"Device picture: {host} sent no picture.");
                return false;
            }
            byte[]? png;
            await Slots.WaitAsync().ConfigureAwait(false);
            try { png = await NormalizeAsync(source, cut).ConfigureAwait(false); }
            finally { Slots.Release(); }
            if (png is null)
            {
                AppLog.Info($"Device picture: the one from {host} couldn't be made.");
                return false;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temporary, png).ConfigureAwait(false);
                File.Move(temporary, path, overwrite: true);
            }
            finally { File.Delete(temporary); }
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Info($"Device picture from {host}: {ex.GetType().Name}");
            return false;
        }
    }

    private static HttpClient CreateHttp()
    {
        // No cookies, and no redirect followed unchecked (DownloadAsync checks each hop): the request names the picture and nothing else.
        var http = ExoHttp.Create(TimeSpan.FromSeconds(20), new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) ExoLauncher/1.0");
        // PNG and JPEG first: an image server that picks the format by Accept could otherwise answer with one Windows may not decode.
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "image/png,image/jpeg;q=0.9,*/*;q=0.1");
        return http;
    }

    /// <summary>
    /// The picture's bytes: approved hosts only (every redirect hop too), at most <see cref="MaxDownloadBytes"/>, and
    /// a PNG, JPEG or WebP by its own first bytes; null for anything else (an error page, a placeholder, a huge file).
    /// </summary>
    internal static async Task<byte[]?> DownloadAsync(HttpClient http, string url, CancellationToken cancellationToken)
    {
        if (!IsApprovedUrl(url)) return null;
        var current = new Uri(url);
        for (var hop = 0; hop <= 3; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                if (response.Headers.Location is not { } location) return null;
                var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                if (!IsApprovedUrl(next.AbsoluteUri)) return null;
                current = next;
                continue;
            }
            if (!response.IsSuccessStatusCode) return null;
            var type = response.Content.Headers.ContentType?.MediaType ?? "";
            if (type.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
                type.Contains("html", StringComparison.OrdinalIgnoreCase) || type.Contains("json", StringComparison.OrdinalIgnoreCase))
                return null;
            if (response.Content.Headers.ContentLength is > MaxDownloadBytes) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var body = new MemoryStream();
            var chunk = new byte[81_920];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (body.Length + read > MaxDownloadBytes) return null;
                body.Write(chunk, 0, read);
            }
            var bytes = body.ToArray();
            return LooksLikeImage(bytes) ? bytes : null;
        }
        return null;
    }

    /// <summary>PNG, JPEG or WebP by signature.</summary>
    internal static bool LooksLikeImage(ReadOnlySpan<byte> b) =>
        b.Length >= 64 && (
            b[..8].SequenceEqual((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]) ||
            b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF ||
            b[..4].SequenceEqual("RIFF"u8) && b[8..12].SequenceEqual("WEBP"u8));

    /// <summary>Decode a PNG, JPEG or WebP and make it one of the set: a PNG of <see cref="CanvasWidth"/> x <see cref="CanvasHeight"/>; null when it can't be.</summary>
    internal static async Task<byte[]?> NormalizeAsync(byte[] source, Cut cut = default)
    {
        if (!LooksLikeImage(source)) return null;
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(source.AsBuffer());
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var width = (int)decoder.OrientedPixelWidth;
        var height = (int)decoder.OrientedPixelHeight;
        if (width < 16 || height < 16 || (long)width * height > 100_000_000) return null;
        var transform = new BitmapTransform();
        var scale = Math.Min(1, MaxDecodeSide / (double)Math.Max(width, height));
        if (scale < 1)
        {
            var scaledWidth = Math.Max(1, (int)Math.Round(width * scale));
            var scaledHeight = Math.Max(1, (int)Math.Round(height * scale));
            var rotated = decoder.OrientedPixelWidth != decoder.PixelWidth;
            transform.ScaledWidth = (uint)(rotated ? scaledHeight : scaledWidth);
            transform.ScaledHeight = (uint)(rotated ? scaledWidth : scaledHeight);
            transform.InterpolationMode = BitmapInterpolationMode.Fant;
        }
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, transform,
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyToBuffer(pixels.AsBuffer());
        return Compose(pixels, bitmap.PixelWidth, bitmap.PixelHeight, cut) is { } frame
            ? await EncodePngAsync(frame, CanvasWidth, CanvasHeight).ConfigureAwait(false)
            : null;
    }

    internal static async Task<byte[]> EncodePngAsync(byte[] bgra, int width, int height)
    {
        using var output = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, (uint)width, (uint)height, 96, 96, bgra);
        await encoder.FlushAsync();
        var bytes = new byte[output.Size];
        output.Seek(0);
        await output.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
        return bytes;
    }

    /// <summary>
    /// Straight BGRA pixels in (changed in place), the framed picture out: straight BGRA, <see cref="CanvasWidth"/> x
    /// <see cref="CanvasHeight"/>, the product's bounds fitted inside the margin (never enlarged: a small photo stays sharp and sits smaller) and centered. Null when nothing is left.
    /// </summary>
    internal static byte[]? Compose(byte[] bgra, int width, int height, Cut cut = default)
    {
        if (RemoveBackground(bgra, width, height, cut)) FadeCutEdges(bgra, width, height);
        return ContentBounds(bgra, width, height) is { } bounds ? Frame(bgra, width, height, bounds) : null;
    }

    /// <summary>
    /// Cut the background away. A photo already transparent at its edges keeps its own cut. Otherwise the border's
    /// usual color, when it is light (white, or a flat light studio color) and most of the border is it, is flooded
    /// from the edges within <see cref="Tolerance"/>; only what the flood reaches goes, so whites inside the product
    /// stay (with <see cref="Cut"/>, the studio's shadow and background seen through loops go too). The edge is then
    /// a soft matte (<see cref="Matte"/>). False: no background found, and the photo is left as it was.
    /// </summary>
    internal static bool RemoveBackground(byte[] p, int width, int height, Cut cut = default)
    {
        if (width < 2 || height < 2) return false;
        var border = BorderPixels(width, height);
        var clear = 0;
        foreach (var i in border) if (p[i * 4 + 3] < 250) clear++;
        if (clear * 4 >= border.Length) return true;

        var background = BorderColor(p, border);
        if (Math.Min(background.B, Math.Min(background.G, background.R)) < 200) return false;
        var near = 0;
        foreach (var i in border) if (Distance(p, i, background) <= Tolerance) near++;
        if (near * 10 < border.Length * 6) return false;

        // 0 product, 1 background, 6 the studio's shadow on it; 4 and 5 only while finding holes.
        var state = new byte[width * height];
        var queue = new int[width * height];
        int head = 0, tail = 0;
        var throughShadow = false;
        foreach (var i in border) Take(i, i);
        Flood();
        if (cut.Shadow)
        {
            // Out again from all the background, now also through the shadow's smooth darkening of it.
            throughShadow = true;
            head = 0;
            Flood();
        }
        if (cut.Holes) TakeHoles();
        Matte(p, width, state, background);
        return true;

        void Flood()
        {
            while (head < tail)
            {
                var i = queue[head++];
                var x = i % width;
                if (x > 0) Take(i - 1, i);
                if (x < width - 1) Take(i + 1, i);
                if (i >= width) Take(i - width, i);
                if (i < state.Length - width) Take(i + width, i);
            }
        }

        void Take(int i, int from)
        {
            if (state[i] != 0) return;
            var plain = Distance(p, i, background) <= Tolerance;
            if (!plain && !(throughShadow && IsShadow(p, i, background) && Distance(p, i, (p[from * 4], p[from * 4 + 1], p[from * 4 + 2])) <= ShadowStep))
                return;
            state[i] = plain ? (byte)1 : (byte)6;
            queue[tail++] = i;
        }

        // Closed regions of background color the edge flood can't reach; a small one is a highlight on the product and stays.
        void TakeHoles()
        {
            var smallest = Math.Max(16, width * height / 2000);
            var region = new List<int>();
            for (var start = 0; start < state.Length; start++)
            {
                if (state[start] != 0 || Distance(p, start, background) > Tolerance) continue;
                region.Clear();
                state[start] = 4;
                region.Add(start);
                for (var k = 0; k < region.Count; k++)
                {
                    var i = region[k];
                    var x = i % width;
                    if (x > 0) Grow(i - 1);
                    if (x < width - 1) Grow(i + 1);
                    if (i >= width) Grow(i - width);
                    if (i < state.Length - width) Grow(i + width);
                }
                var mark = region.Count >= smallest ? (byte)1 : (byte)5;
                foreach (var i in region) state[i] = mark;
            }
            for (var i = 0; i < state.Length; i++) if (state[i] == 5) state[i] = 0;

            void Grow(int i)
            {
                if (state[i] != 0 || Distance(p, i, background) > Tolerance) return;
                state[i] = 4;
                region.Add(i);
            }
        }
    }

    /// <summary>The largest step between neighbors the shadow follow takes: a shadow fades smoothly, a product's edge doesn't.</summary>
    private const int ShadowStep = 16;

    /// <summary>The background's own color darkened evenly (a shadow cast on it), to a quarter of it at most.</summary>
    private static bool IsShadow(byte[] p, int i, (byte B, byte G, byte R) background)
    {
        var o = i * 4;
        var k = (p[o] + p[o + 1] + p[o + 2]) / (float)(background.B + background.G + background.R);
        return k is >= 0.25f and <= 1.02f &&
               Math.Abs(p[o] - k * background.B) <= 10 && Math.Abs(p[o + 1] - k * background.G) <= 10 &&
               Math.Abs(p[o + 2] - k * background.R) <= 10;
    }

    /// <summary>
    /// The cut's edge as a soft matte rather than a hard flood boundary. The band along the cut (the background
    /// touching the product, and the product's first two pixels) gets the share of product each pixel holds: how far
    /// its color is from the background's, measured against the product's own color just inside, so a pixel half
    /// product and half background is half opaque. Its color is un-premultiplied against the background (the
    /// background's tint taken out, so neither a light nor a dark fringe is left), and the band's opacity is then
    /// smoothed with a 1 px Gaussian. The background is cleared; the product's inside is left as it was.
    /// </summary>
    private static void Matte(byte[] p, int width, byte[] state, (byte B, byte G, byte R) background)
    {
        var length = state.Length;
        // 1 and 2: the product's first two pixels; -1: plain background touching the product (a shadow never holds any of it).
        var band = new sbyte[length];
        Span<int> near = stackalloc int[4];
        for (var i = 0; i < length; i++)
        {
            if (state[i] != 0) continue;
            var n = Neighbors(i, width, length, near);
            for (var k = 0; k < n; k++)
                if (state[near[k]] is 1 or 6) { band[i] = 1; break; }
        }
        for (var i = 0; i < length; i++)
        {
            if (state[i] != 0 || band[i] != 0) continue;
            var n = Neighbors(i, width, length, near);
            for (var k = 0; k < n; k++)
                if (band[near[k]] == 1) { band[i] = 2; break; }
        }
        for (var i = 0; i < length; i++)
        {
            if (state[i] != 1) continue;
            var n = Neighbors(i, width, length, near);
            for (var k = 0; k < n; k++)
                if (state[near[k]] == 0) { band[i] = -1; break; }
        }

        var opacity = new byte[length];
        for (var i = 0; i < length; i++) opacity[i] = state[i] == 0 ? p[i * 4 + 3] : (byte)0;
        var edge = new List<int>();
        for (var i = 0; i < length; i++) if (band[i] != 0) edge.Add(i);
        var colors = new int[edge.Count];
        for (var k = 0; k < edge.Count; k++)
        {
            var i = edge[k];
            var o = i * 4;
            int b = p[o], g = p[o + 1], r = p[o + 2];
            var a = state[i] == 0 ? 1f : 0f;
            if (Inside(p, width, state, band, i, background) is { } product)
            {
                float db = product.B - background.B, dg = product.G - background.G, dr = product.R - background.R;
                var reach = db * db + dg * dg + dr * dr;
                // A product the background's own color can't be told from by color: the flood's line stands.
                if (reach >= 24 * 24)
                {
                    a = Math.Clamp(((b - background.B) * db + (g - background.G) * dg + (r - background.R) * dr) / reach, 0f, 1f);
                    if (a >= 0.25f)
                    {
                        b = Unmix(b, background.B, a);
                        g = Unmix(g, background.G, a);
                        r = Unmix(r, background.R, a);
                    }
                    else
                    {
                        // Too little product in it to read its color back: the product's own, just inside.
                        b = product.B;
                        g = product.G;
                        r = product.R;
                    }
                }
            }
            opacity[i] = (byte)MathF.Round(a * (state[i] == 0 ? p[o + 3] : 255));
            colors[k] = b | g << 8 | r << 16;
        }

        for (var i = 0; i < length; i++)
            if (state[i] != 0 && band[i] == 0)
            {
                var o = i * 4;
                p[o] = p[o + 1] = p[o + 2] = p[o + 3] = 0;
            }
        for (var k = 0; k < edge.Count; k++)
        {
            var o = edge[k] * 4;
            p[o] = (byte)colors[k];
            p[o + 1] = (byte)(colors[k] >> 8);
            p[o + 2] = (byte)(colors[k] >> 16);
            p[o + 3] = Gaussian(opacity, width, edge[k]);
        }

        static int Unmix(int c, byte background, float a) => Math.Clamp((int)MathF.Round(background + (c - background) / a), 0, 255);
    }

    /// <summary>
    /// The product's color just inside the cut near pixel <paramref name="i"/>: the mean of the product beyond the
    /// band within 4 px, or, for something thinner than the band (a cable), its pixel least like the background.
    /// </summary>
    private static (byte B, byte G, byte R)? Inside(byte[] p, int width, byte[] state, sbyte[] band, int i, (byte B, byte G, byte R) background)
    {
        const int Radius = 4;
        var height = state.Length / width;
        int x0 = i % width, y0 = i / width;
        long sb = 0, sg = 0, sr = 0;
        int count = 0, thin = -1, thinDistance = -1;
        for (var y = Math.Max(0, y0 - Radius); y <= Math.Min(height - 1, y0 + Radius); y++)
            for (var x = Math.Max(0, x0 - Radius); x <= Math.Min(width - 1, x0 + Radius); x++)
            {
                var j = y * width + x;
                if (state[j] != 0) continue;
                if (band[j] == 0)
                {
                    sb += p[j * 4];
                    sg += p[j * 4 + 1];
                    sr += p[j * 4 + 2];
                    count++;
                }
                else if (Distance(p, j, background) is var d && d > thinDistance)
                {
                    thinDistance = d;
                    thin = j;
                }
            }
        if (count > 0) return ((byte)(sb / count), (byte)(sg / count), (byte)(sr / count));
        return thin < 0 ? null : (p[thin * 4], p[thin * 4 + 1], p[thin * 4 + 2]);
    }

    /// <summary>A 1 px Gaussian (1-2-1 each way) of <paramref name="opacity"/> at pixel <paramref name="i"/>.</summary>
    private static byte Gaussian(byte[] opacity, int width, int i)
    {
        var height = opacity.Length / width;
        int x0 = i % width, y0 = i / width, sum = 0, weight = 0;
        for (var y = Math.Max(0, y0 - 1); y <= Math.Min(height - 1, y0 + 1); y++)
            for (var x = Math.Max(0, x0 - 1); x <= Math.Min(width - 1, x0 + 1); x++)
            {
                var w = (x == x0 ? 2 : 1) * (y == y0 ? 2 : 1);
                sum += opacity[y * width + x] * w;
                weight += w;
            }
        return (byte)((sum + weight / 2) / weight);
    }

    private static int Neighbors(int i, int width, int length, Span<int> into)
    {
        var n = 0;
        var x = i % width;
        if (x > 0) into[n++] = i - 1;
        if (x < width - 1) into[n++] = i + 1;
        if (i >= width) into[n++] = i - width;
        if (i < length - width) into[n++] = i + width;
        return n;
    }

    private static int[] BorderPixels(int width, int height)
    {
        var border = new List<int>(2 * (width + height));
        for (var x = 0; x < width; x++) { border.Add(x); border.Add((height - 1) * width + x); }
        for (var y = 1; y < height - 1; y++) { border.Add(y * width); border.Add(y * width + width - 1); }
        return [.. border];
    }

    /// <summary>The border's median color, channel by channel.</summary>
    private static (byte B, byte G, byte R) BorderColor(byte[] p, int[] border)
    {
        var counts = new int[3, 256];
        foreach (var i in border)
            for (var c = 0; c < 3; c++) counts[c, p[i * 4 + c]]++;
        var median = new byte[3];
        for (var c = 0; c < 3; c++)
        {
            var seen = 0;
            for (var v = 0; v < 256; v++)
            {
                seen += counts[c, v];
                if (seen * 2 < border.Length) continue;
                median[c] = (byte)v;
                break;
            }
        }
        return (median[0], median[1], median[2]);
    }

    private static int Distance(byte[] p, int i, (byte B, byte G, byte R) color) =>
        Math.Max(Math.Abs(p[i * 4] - color.B), Math.Max(Math.Abs(p[i * 4 + 1] - color.G), Math.Abs(p[i * 4 + 2] - color.R)));

    /// <summary>
    /// Something thin the photo's frame cuts (a cable running off its edge) fades out instead of ending in a hard line.
    /// A side counts when what touches it covers at most 4% of it, so a product body touching the frame stays whole.
    /// </summary>
    internal static void FadeCutEdges(byte[] p, int width, int height)
    {
        FadeSide(width, height, (along, depth) => depth * width + along);                    // top
        FadeSide(width, height, (along, depth) => (height - 1 - depth) * width + along);     // bottom
        FadeSide(height, width, (along, depth) => along * width + depth);                    // left
        FadeSide(height, width, (along, depth) => along * width + width - 1 - depth);        // right

        void FadeSide(int length, int reach, Func<int, int, int> at)
        {
            var touching = 0;
            for (var k = 0; k < length; k++) if (p[at(k, 0) * 4 + 3] >= 128) touching++;
            if (touching == 0 || touching > length * 0.04) return;
            var fade = Math.Min(reach, Math.Max(8, (int)(reach * 0.06)));
            for (var depth = 0; depth < fade; depth++)
            {
                var keep = (depth + 0.5f) / fade;
                for (var k = 0; k < length; k++)
                {
                    var o = at(k, depth) * 4 + 3;
                    p[o] = (byte)MathF.Round(p[o] * keep);
                }
            }
        }
    }

    /// <summary>The product's bounds: pixels at least <see cref="SolidAlpha"/> opaque. Null when there are none worth framing.</summary>
    internal static (int Left, int Top, int Right, int Bottom)? ContentBounds(byte[] p, int width, int height)
    {
        int left = width, top = height, right = -1, bottom = -1;
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                if (p[(row + x) * 4 + 3] < SolidAlpha) continue;
                if (x < left) left = x;
                if (x > right) right = x;
                if (y < top) top = y;
                bottom = y;
            }
        }
        return right - left < 3 || bottom - top < 3 ? null : (left, top, right, bottom);
    }

    /// <summary>Scale the bounds to fit inside the margin, never past the photo's own pixels, and center them on a transparent frame.</summary>
    private static byte[] Frame(byte[] p, int width, int height, (int Left, int Top, int Right, int Bottom) b)
    {
        var pad = (int)Math.Round(CanvasHeight * PaddingShare);
        double boundsWidth = b.Right - b.Left + 1, boundsHeight = b.Bottom - b.Top + 1;
        var scale = Math.Min(1, Math.Min((CanvasWidth - 2 * pad) / boundsWidth, (CanvasHeight - 2 * pad) / boundsHeight));
        // A margin around the bounds comes along, so a faint shadow just outside them is drawn rather than cut.
        var margin = (int)Math.Ceiling(pad / scale);
        int x0 = Math.Max(0, b.Left - margin), y0 = Math.Max(0, b.Top - margin);
        int x1 = Math.Min(width - 1, b.Right + margin), y1 = Math.Min(height - 1, b.Bottom + margin);
        int cropWidth = x1 - x0 + 1, cropHeight = y1 - y0 + 1;
        var targetWidth = Math.Max(1, (int)Math.Round(cropWidth * scale));
        var targetHeight = Math.Max(1, (int)Math.Round(cropHeight * scale));
        var scaled = Resample(p, width, x0, y0, cropWidth, cropHeight, targetWidth, targetHeight);

        var left = (int)Math.Round(CanvasWidth / 2.0 - (b.Left - x0 + boundsWidth / 2) * targetWidth / cropWidth);
        var top = (int)Math.Round(CanvasHeight / 2.0 - (b.Top - y0 + boundsHeight / 2) * targetHeight / cropHeight);
        var canvas = new byte[CanvasWidth * CanvasHeight * 4];
        var from = Math.Max(0, -left);
        var to = Math.Min(targetWidth, CanvasWidth - left);
        if (to <= from) return canvas;
        for (var y = 0; y < targetHeight; y++)
        {
            var cy = top + y;
            if (cy < 0 || cy >= CanvasHeight) continue;
            System.Buffer.BlockCopy(scaled, (y * targetWidth + from) * 4, canvas, (cy * CanvasWidth + left + from) * 4, (to - from) * 4);
        }
        return canvas;
    }

    /// <summary>
    /// Lanczos-3 scale of a region of straight BGRA pixels, on premultiplied color so a transparent pixel lends the
    /// edge none of its color; straight BGRA out.
    /// </summary>
    private static byte[] Resample(byte[] p, int stride, int x0, int y0, int width, int height, int targetWidth, int targetHeight)
    {
        var (xStart, xWeights) = CoverArtService.LanczosContributions(width, targetWidth);
        var (yStart, yWeights) = CoverArtService.LanczosContributions(height, targetHeight);
        var rowFloats = targetWidth * 4;
        var rows = new float[height * rowFloats];
        for (var y = 0; y < height; y++)
        {
            var rowIn = ((y0 + y) * stride + x0) * 4;
            for (var x = 0; x < targetWidth; x++)
            {
                float b = 0, g = 0, r = 0, a = 0;
                var weights = xWeights[x];
                var at = rowIn + xStart[x] * 4;
                for (var k = 0; k < weights.Length; k++, at += 4)
                {
                    var w = weights[k];
                    var wa = w * p[at + 3] / 255f;
                    b += p[at] * wa;
                    g += p[at + 1] * wa;
                    r += p[at + 2] * wa;
                    a += p[at + 3] * w;
                }
                var o = y * rowFloats + x * 4;
                rows[o] = b;
                rows[o + 1] = g;
                rows[o + 2] = r;
                rows[o + 3] = a;
            }
        }

        var result = new byte[targetWidth * targetHeight * 4];
        for (var y = 0; y < targetHeight; y++)
        {
            var weights = yWeights[y];
            var first = yStart[y] * rowFloats;
            for (var x = 0; x < targetWidth; x++)
            {
                float b = 0, g = 0, r = 0, a = 0;
                var at = first + x * 4;
                for (var k = 0; k < weights.Length; k++, at += rowFloats)
                {
                    var w = weights[k];
                    b += rows[at] * w;
                    g += rows[at + 1] * w;
                    r += rows[at + 2] * w;
                    a += rows[at + 3] * w;
                }
                if (a < 0.5f) continue;
                var o = (y * targetWidth + x) * 4;
                var straight = 255f / Math.Min(a, 255f);
                result[o] = ToByte(b * straight);
                result[o + 1] = ToByte(g * straight);
                result[o + 2] = ToByte(r * straight);
                result[o + 3] = ToByte(a);
            }
        }
        return result;

        static byte ToByte(float value) => (byte)Math.Clamp((int)MathF.Round(value), 0, 255);
    }
}
