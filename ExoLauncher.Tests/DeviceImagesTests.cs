using System.Net;
using System.Text.Json;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the Devices room's pictures can go wrong:
/// - a device resolves to another model or color (the Edge to the plain DualSense, Midnight Black to White, the split
///   60HE v2 to the ANSI one) or to nothing (Lucid's "Ziigaat Lush"), because devices.list reports a family or a size,
///   not a model;
/// - a name match is too loose: the 60HE+ takes the 60HE's picture, a Truthear Zero the Zero:Red's, a short alias
///   matches without its maker, a headset takes a mouse's picture;
/// - a link in the list points at a host PRIVACY.md doesn't name, so the list drops it (or Exo would contact it unannounced);
/// - removing the background eats whites inside the product, leaves the background, or leaves a hard light edge;
/// - the shadow and hole options run when not asked for, or don't when asked;
/// - a cable cut by the photo's frame ends in a hard line, or a product body touching the frame is faded;
/// - the framing drifts (another size, another margin, off center), so the pictures stop looking like one set;
/// - a bad download (an error page, a file over the cap with or without a length, a redirect elsewhere, bytes that
///   aren't an image) is kept.
/// </summary>
public sealed class DeviceImagesTests
{
    private static readonly IReadOnlyList<DeviceImages.Entry> Catalog =
        DeviceImages.Load(Repo.PathOf("ExoLauncher", "Assets", "device-images.json"));

    private static DeviceImages.Entry? Resolve(string? kind, string? model, string? name, string? maker, string? color = null, string? layout = null) =>
        DeviceImages.Resolve(Catalog, new DeviceImages.Request(kind, model, name, maker, color, layout));

    // This PC's four, as devices.list and Lucid name them.
    [Theory]
    [InlineData("controller", "dualsense-edge", "DualSense Edge Wireless Controller", "Sony", "Midnight Black", null, "DualSense Edge", "Midnight Black")]
    [InlineData("keyboard", "60", "Wooting 60HE v2", "Wooting", null, "ansi-split", "60HE v2", "ansi-split")]
    [InlineData("mouse", null, "Finalmouse Starlight X", "Finalmouse", null, null, "Starlight X", null)]
    [InlineData("audio", "headphones", "Ziigaat Lush", "Ziigaat", null, null, "Lush", null)]
    public void ThisPcsDevicesResolveExactly(string kind, string? model, string name, string maker, string? color, string? layout,
        string expectedModel, string? expectedVariant)
    {
        var entry = Resolve(kind, model, name, maker, color, layout);
        Assert.NotNull(entry);
        Assert.Equal(expectedModel, entry.Model);
        Assert.Equal(expectedVariant, entry.Variant);
    }

    [Theory]
    // A variant falls back to the model's first picture, a layout to its family (split or not).
    [InlineData("controller", "dualsense-edge", "DualSense Edge Wireless Controller", "Sony", null, null, "DualSense Edge", "White")]
    [InlineData("controller", "dualsense", "DualSense Wireless Controller", "Sony", "Fortnite", null, "DualSense", "White")]
    [InlineData("controller", "dualsense", "DualSense Wireless Controller", "Sony", "Cosmic Red", null, "DualSense", "Cosmic Red")]
    [InlineData("controller", null, "DualSense Edge Wireless Controller", "Sony", "Midnight Black", null, "DualSense Edge", "Midnight Black")]
    [InlineData("keyboard", "60", "Wooting 60HE v2", "Wooting", null, "iso-split", "60HE v2", "ansi-split")]
    [InlineData("keyboard", "60", "Wooting 60HE v2", "Wooting", null, "iso", "60HE v2", "ansi")]
    [InlineData("keyboard", "60", "Wooting 60HE v2", "Wooting", null, null, "60HE v2", "ansi")]
    // The longest name wins: 60HE+ is not the 60HE, the 60HE (ARM) is.
    [InlineData("keyboard", "60", "Wooting 60HE+", "Wooting", null, "ansi", "60HE+", null)]
    [InlineData("keyboard", "60", "Wooting 60HE (ARM)", "Wooting", null, null, "60HE", null)]
    [InlineData("keyboard", "tkl", "Wooting 80HE", "Wooting", null, null, "80HE", null)]
    // devices.list's model ids.
    [InlineData("controller", "xbox", "Xbox Wireless Controller", "Microsoft", null, null, "Xbox Wireless Controller", "Carbon Black")]
    [InlineData("controller", "xbox", "Xbox Wireless Controller", "Microsoft", "Robot White", null, "Xbox Wireless Controller", "Robot White")]
    [InlineData("controller", "dualshock4", "Wireless Controller", "Sony", null, null, "DualShock 4", "Jet Black")]
    [InlineData("controller", "switch-pro", "Pro Controller", "Nintendo", null, null, "Switch Pro Controller", null)]
    // Lucid's names, and the kind keeping a headset from a mouse of a similar name.
    [InlineData("audio", "Zero:Red", "Truthear Zero:Red", "Truthear", null, null, "Zero:Red", null)]
    [InlineData("audio", null, "7Hz Zero", "7Hz", null, null, "Salnotes Zero", null)]
    [InlineData("audio", null, "PRO X 2 LIGHTSPEED", "Logitech", null, null, "Pro X 2 Lightspeed", null)]
    [InlineData("mouse", null, "PRO X2 SUPERSTRIKE", "Logitech", null, null, "Pro X2 Superstrike", null)]
    [InlineData("audio", null, "Sennheiser HD 650", "Sennheiser", null, null, "HD 650", null)]
    public void ModelsVariantsAndNamesResolve(string kind, string? model, string name, string maker, string? color, string? layout,
        string expectedModel, string? expectedVariant)
    {
        var entry = Resolve(kind, model, name, maker, color, layout);
        Assert.NotNull(entry);
        Assert.Equal(expectedModel, entry.Model);
        Assert.Equal(expectedVariant, entry.Variant);
    }

    [Theory]
    [InlineData("audio", null, "Truthear Zero", "Truthear")]           // the blue Zero is not the Zero:Red
    [InlineData("audio", null, "Zero", null)]                          // a short alias needs its maker
    [InlineData("audio", null, "Lush", "Moondrop")]                    // another maker's Lush is not Ziigaat's
    [InlineData("keyboard", "full", "Razer Huntsman V3 Pro", "Razer")] // not in the list
    [InlineData("keyboard", "60", "Wooting Two", "Wooting")]           // the Two is not the Two HE
    [InlineData("mouse", null, "Wooting 60HE v2", "Wooting")]          // the kind has to agree
    public void NothingLooseMatches(string kind, string? model, string name, string? maker) =>
        Assert.Null(Resolve(kind, model, name, maker));

    [Fact]
    public void EveryListedPictureIsKeptAndComplete()
    {
        using var raw = JsonDocument.Parse(File.ReadAllText(Repo.PathOf("ExoLauncher", "Assets", "device-images.json")));
        Assert.Equal(raw.RootElement.GetProperty("devices").GetArrayLength(), Catalog.Count);
        foreach (var entry in Catalog)
        {
            Assert.True(DeviceImages.IsApprovedUrl(entry.Url), entry.Url);
            Assert.StartsWith("https://", entry.Page);
            Assert.False(string.IsNullOrWhiteSpace(entry.View), entry.Model);
            Assert.Contains(entry.Kind, new[] { "mouse", "keyboard", "controller", "audio" });
        }
        Assert.Equal(Catalog.Count, Catalog.Select(e => (e.Maker, e.Model, e.Variant)).Distinct().Count());
    }

    [Theory]
    [InlineData("https://gmedia.playstation.com/is/image/SIEPDC/x$en?$1600px--t$&bfc=off", true)]
    [InlineData("https://img.openmouse.app/finalmouse-slx.png", true)]
    [InlineData("http://img.openmouse.app/finalmouse-slx.png", false)]
    [InlineData("https://img.openmouse.app:8443/finalmouse-slx.png", false)]
    [InlineData("https://user@img.openmouse.app/finalmouse-slx.png", false)]
    [InlineData("https://img.openmouse.app.evil.example/x.png", false)]
    [InlineData("https://cdn.cloudflare.steamstatic.com/x.png", false)] // listed, but on the cover art row
    public void OnlyTheDevicePicturesRowsHostsAreFetched(string url, bool expected) =>
        Assert.Equal(expected, DeviceImages.IsApprovedUrl(url));

    // Pixels: straight BGRA.
    private static byte[] Fill(int width, int height, byte r, byte g, byte b, byte a = 255)
    {
        var p = new byte[width * height * 4];
        for (var i = 0; i < p.Length; i += 4) { p[i] = b; p[i + 1] = g; p[i + 2] = r; p[i + 3] = a; }
        return p;
    }

    private static void Rect(byte[] p, int width, int x0, int y0, int x1, int y1, byte r, byte g, byte b, byte a = 255)
    {
        for (var y = y0; y <= y1; y++)
            for (var x = x0; x <= x1; x++)
            {
                var o = (y * width + x) * 4;
                p[o] = b; p[o + 1] = g; p[o + 2] = r; p[o + 3] = a;
            }
    }

    private static byte Alpha(byte[] p, int width, int x, int y) => p[(y * width + x) * 4 + 3];
    private static byte Red(byte[] p, int width, int x, int y) => p[(y * width + x) * 4 + 2];

    /// <summary>A dark product (x 10..49, y 10..29) with a pure white square inside it (x 25..34, y 15..24).</summary>
    private static byte[] ProductWithWhiteInside(byte r, byte g, byte b)
    {
        var p = Fill(60, 40, r, g, b);
        Rect(p, 60, 10, 10, 49, 29, 30, 30, 30);
        Rect(p, 60, 25, 15, 34, 24, 255, 255, 255);
        return p;
    }

    [Theory]
    [InlineData(255, 255, 255)] // white
    [InlineData(224, 227, 235)] // Wooting's flat studio color
    public void BackgroundGoesWhiteInsideStaysEdgeIsSoft(byte r, byte g, byte b)
    {
        var p = ProductWithWhiteInside(r, g, b);
        Assert.True(DeviceImages.RemoveBackground(p, 60, 40));

        Assert.Equal(0, Alpha(p, 60, 0, 0));
        Assert.Equal(0, Alpha(p, 60, 5, 20));
        Assert.Equal(0, Alpha(p, 60, 55, 35));
        Assert.Equal(255, Alpha(p, 60, 15, 20));
        // The white inside the product is the product's.
        Assert.Equal(255, Alpha(p, 60, 29, 19));
        Assert.Equal(255, Red(p, 60, 29, 19));
        // A soft edge across the cut (the background's last pixel to the product's second), opaque past it, and no
        // light fringe: the soft pixels carry the product's color, not the background's.
        var outside = Alpha(p, 60, 9, 20);
        var first = Alpha(p, 60, 10, 20);
        Assert.InRange(outside, 1, 127);
        Assert.InRange(first, 128, 254);
        Assert.InRange(Alpha(p, 60, 11, 20), first, (byte)255);
        Assert.Equal(255, Alpha(p, 60, 12, 20));
        Assert.True(Red(p, 60, 9, 20) <= 40, $"soft edge color {Red(p, 60, 9, 20)}");
        Assert.True(Red(p, 60, 10, 20) <= 40, $"edge color {Red(p, 60, 10, 20)}");
    }

    [Fact]
    public void AHalfCoveredPixelIsHalfOpaque()
    {
        // A product edge anti-aliased by the camera: one column half product (30) and half white.
        var p = Fill(60, 40, 255, 255, 255);
        Rect(p, 60, 10, 10, 49, 29, 30, 30, 30);
        Rect(p, 60, 9, 10, 9, 29, 142, 142, 142);
        DeviceImages.RemoveBackground(p, 60, 40);
        // Before the 1 px Gaussian it is 50%; after, between its neighbors' and still well short of opaque.
        Assert.InRange(Alpha(p, 60, 9, 20), 80, 175);
        Assert.True(Red(p, 60, 9, 20) <= 45, $"un-premultiplied color {Red(p, 60, 9, 20)}");
    }

    [Fact]
    public void AColoredOrDarkBackgroundIsLeftAlone()
    {
        var p = ProductWithWhiteInside(90, 90, 90);
        Assert.False(DeviceImages.RemoveBackground(p, 60, 40));
        Assert.All(Enumerable.Range(0, 60 * 40), i => Assert.Equal(255, p[i * 4 + 3]));
    }

    [Fact]
    public void ATransparentPhotoKeepsItsOwnCut()
    {
        var p = Fill(60, 40, 0, 0, 0, 0);
        Rect(p, 60, 10, 10, 49, 29, 255, 255, 255);
        Assert.True(DeviceImages.RemoveBackground(p, 60, 40));
        Assert.Equal(255, Alpha(p, 60, 10, 10));
        Assert.Equal(255, Red(p, 60, 10, 10));
    }

    [Fact]
    public void HolesGoOnlyWhenAsked()
    {
        var kept = ProductWithWhiteInside(255, 255, 255);
        DeviceImages.RemoveBackground(kept, 60, 40);
        Assert.Equal(255, Alpha(kept, 60, 29, 19));

        var cut = ProductWithWhiteInside(255, 255, 255);
        DeviceImages.RemoveBackground(cut, 60, 40, new DeviceImages.Cut(Holes: true));
        Assert.Equal(0, Alpha(cut, 60, 29, 19));
        Assert.Equal(255, Alpha(cut, 60, 15, 20));
    }

    /// <summary>White, a dark product (y 10..19), and under it a shadow fading from 120 back to white over 20 rows.</summary>
    private static byte[] ProductWithShadow()
    {
        var p = Fill(40, 50, 255, 255, 255);
        Rect(p, 40, 5, 10, 34, 19, 20, 20, 20);
        for (var y = 20; y < 40; y++)
        {
            var v = (byte)(120 + (y - 20) * 7);
            Rect(p, 40, 5, y, 34, y, v, v, v);
        }
        return p;
    }

    [Fact]
    public void TheShadowGoesOnlyWhenAsked()
    {
        var kept = ProductWithShadow();
        DeviceImages.RemoveBackground(kept, 40, 50);
        Assert.Equal(255, Alpha(kept, 40, 20, 24));

        var cut = ProductWithShadow();
        DeviceImages.RemoveBackground(cut, 40, 50, new DeviceImages.Cut(Shadow: true));
        Assert.Equal(0, Alpha(cut, 40, 20, 24));
        Assert.Equal(0, Alpha(cut, 40, 20, 21));
        // The product's own hard edge stops the follow.
        Assert.Equal(255, Alpha(cut, 40, 20, 15));
        Assert.Equal(255, Alpha(cut, 40, 20, 12));
    }

    [Fact]
    public void ACableTheFrameCutsFadesAndABodyTouchingItDoesNot()
    {
        var p = Fill(200, 200, 0, 0, 0, 0);
        Rect(p, 200, 60, 40, 140, 120, 30, 30, 30);   // body
        Rect(p, 200, 99, 121, 100, 199, 30, 30, 30);  // cable to the bottom edge
        Rect(p, 200, 0, 60, 199, 70, 30, 30, 30);     // a band across, touching left and right fully
        DeviceImages.FadeCutEdges(p, 200, 200);
        Assert.True(Alpha(p, 200, 99, 199) < 40, $"cable end {Alpha(p, 200, 99, 199)}");
        Assert.Equal(255, Alpha(p, 200, 99, 150));
        Assert.Equal(255, Alpha(p, 200, 0, 65));
        Assert.Equal(255, Alpha(p, 200, 199, 65));
    }

    [Theory]
    [InlineData(2400, 1200, 2000, 400)] // wide: fills the width inside the margin
    [InlineData(1200, 2000, 300, 1600)] // tall: fills the height inside the margin
    public void EveryPictureGetsTheSameFrameAndMargin(int width, int height, int productWidth, int productHeight)
    {
        var p = Fill(width, height, 255, 255, 255);
        int x0 = width / 2 - productWidth / 2, y0 = height / 2 - productHeight / 2;
        Rect(p, width, x0, y0, x0 + productWidth - 1, y0 + productHeight - 1, 30, 30, 30);

        var frame = DeviceImages.Compose(p, width, height);

        Assert.NotNull(frame);
        Assert.Equal(DeviceImages.CanvasWidth * DeviceImages.CanvasHeight * 4, frame.Length);
        Assert.Equal(0, Alpha(frame, DeviceImages.CanvasWidth, 0, 0));
        var bounds = DeviceImages.ContentBounds(frame, DeviceImages.CanvasWidth, DeviceImages.CanvasHeight);
        Assert.NotNull(bounds);
        var (left, top, right, bottom) = bounds.Value;
        const int pad = 64; // 8% of 800
        if (productWidth > productHeight)
        {
            Assert.InRange(left, pad - 2, pad + 2);
            Assert.InRange(right, DeviceImages.CanvasWidth - 1 - pad - 2, DeviceImages.CanvasWidth - 1 - pad + 2);
            Assert.InRange((top + bottom) / 2.0, DeviceImages.CanvasHeight / 2.0 - 3, DeviceImages.CanvasHeight / 2.0 + 2);
        }
        else
        {
            Assert.InRange(top, pad - 2, pad + 2);
            Assert.InRange(bottom, DeviceImages.CanvasHeight - 1 - pad - 2, DeviceImages.CanvasHeight - 1 - pad + 2);
            Assert.InRange((left + right) / 2.0, DeviceImages.CanvasWidth / 2.0 - 3, DeviceImages.CanvasWidth / 2.0 + 2);
        }
    }

    [Fact]
    public void ASmallPhotoIsNeverEnlarged()
    {
        var p = Fill(300, 200, 255, 255, 255);
        Rect(p, 300, 100, 90, 199, 109, 30, 30, 30);

        var frame = DeviceImages.Compose(p, 300, 200);

        Assert.NotNull(frame);
        var (left, top, right, bottom) = DeviceImages.ContentBounds(frame, DeviceImages.CanvasWidth, DeviceImages.CanvasHeight)!.Value;
        Assert.InRange(right - left + 1, 99, 103);
        Assert.InRange(bottom - top + 1, 19, 23);
        Assert.InRange((left + right) / 2.0, DeviceImages.CanvasWidth / 2.0 - 2, DeviceImages.CanvasWidth / 2.0 + 2);
        Assert.InRange((top + bottom) / 2.0, DeviceImages.CanvasHeight / 2.0 - 2, DeviceImages.CanvasHeight / 2.0 + 2);
    }

    [Fact]
    public void ABlankPhotoMakesNoPicture() =>
        Assert.Null(DeviceImages.Compose(Fill(120, 80, 255, 255, 255), 120, 80));

    [Fact]
    public async Task APhotoBecomesAFramedPng()
    {
        var p = Fill(300, 200, 255, 255, 255);
        Rect(p, 300, 100, 80, 199, 119, 30, 30, 30);
        var source = await DeviceImages.EncodePngAsync(p, 300, 200);

        var png = await DeviceImages.NormalizeAsync(source);

        Assert.NotNull(png);
        Assert.True(DeviceImages.LooksLikeImage(png));
        // IHDR: width and height, big-endian, at bytes 16 and 20.
        Assert.Equal(DeviceImages.CanvasWidth, (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19]);
        Assert.Equal(DeviceImages.CanvasHeight, (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23]);
    }

    private const string Picture = "https://img.openmouse.app/finalmouse-slx.png";

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(respond(request));
        }
    }

    /// <summary>A body of unknown length, so only the read loop can enforce the cap.</summary>
    private sealed class EndlessContent(long length) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var chunk = new byte[64 * 1024];
            chunk[0] = 0x89; chunk[1] = 0x50; chunk[2] = 0x4E; chunk[3] = 0x47;
            for (long sent = 0; sent < length; sent += chunk.Length) await stream.WriteAsync(chunk);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private static async Task<(byte[]? Bytes, int Calls)> DownloadAsync(Func<HttpRequestMessage, HttpResponseMessage> respond, string url = Picture)
    {
        var handler = new StubHandler(respond);
        using var http = ExoHttp.Create(TimeSpan.FromSeconds(10), handler);
        return (await DeviceImages.DownloadAsync(http, url, CancellationToken.None), handler.Calls);
    }

    private static HttpResponseMessage Ok(HttpContent content, string? type = null)
    {
        if (type is not null) content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(type);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    [Fact]
    public async Task AnImageComesThrough()
    {
        var png = await DeviceImages.EncodePngAsync(Fill(16, 16, 30, 30, 30), 16, 16);
        var (bytes, _) = await DownloadAsync(_ => Ok(new ByteArrayContent(png), "image/png"));
        Assert.Equal(png, bytes);
    }

    [Fact]
    public async Task AnErrorPageIsNoPicture()
    {
        var html = "<!doctype html><html><body>Not found</body></html>" + new string(' ', 200);
        Assert.Null((await DownloadAsync(_ => Ok(new StringContent(html), "text/html"))).Bytes);
        // Mislabeled as an image, it still isn't one.
        Assert.Null((await DownloadAsync(_ => Ok(new StringContent(html), "image/png"))).Bytes);
    }

    [Fact]
    public async Task AFileOverTheCapIsNoPicture()
    {
        var big = new byte[DeviceImages.MaxDownloadBytes + 1];
        big[0] = 0x89; big[1] = 0x50; big[2] = 0x4E; big[3] = 0x47;
        Assert.Null((await DownloadAsync(_ => Ok(new ByteArrayContent(big), "image/png"))).Bytes);
        Assert.Null((await DownloadAsync(_ => Ok(new EndlessContent(DeviceImages.MaxDownloadBytes + 65_536), "image/png"))).Bytes);
    }

    [Fact]
    public async Task OnlyApprovedHostsAreAsked()
    {
        var (bytes, calls) = await DownloadAsync(_ => throw new InvalidOperationException("asked"), "https://evil.example/x.png");
        Assert.Null(bytes);
        Assert.Equal(0, calls);

        var redirect = await DownloadAsync(_ => new HttpResponseMessage(HttpStatusCode.Found)
        {
            Headers = { Location = new Uri("https://evil.example/x.png") },
        });
        Assert.Null(redirect.Bytes);
        Assert.Equal(1, redirect.Calls);
    }
}
