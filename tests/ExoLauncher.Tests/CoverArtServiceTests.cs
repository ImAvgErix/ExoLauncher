using ExoLauncher.Models;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// Drives the shipped CoverArtService on real temp files — no mocks of the resolver.
/// </summary>
public class CoverArtServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _prevCache;

    public CoverArtServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "exo-cover-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        // Point cache root via reflection-safe approach: write into real CacheRoot is fine,
        // but use isolated files under a subfolder we control by writing into CacheRoot
        // with known app ids and cleaning up after.
        Directory.CreateDirectory(CoverArtService.CacheRoot);
        _prevCache = CoverArtService.CacheRoot;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }
        catch { /* */ }
    }

    private static byte[] MinimalJpeg()
    {
        // Real 60×90 JPEG (portrait) so CoverArtService can read dimensions.
        // Padded past MinCoverBytes (12KB) — trailing bytes after EOI are ignored by the size reader.
        var encoded = Convert.FromBase64String(
            "/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDAAMCAgMCAgMDAwMEAwMEBQgFBQQEBQoHBwYIDAoMDAsKCwsNDhIQDQ4RDgsLEBYQERMUFRUVDA8XGBYUGBIUFRT/2wBDAQMEBAUEBQkFBQkUDQsNFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBT/wAARCABaADwDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwD4XooorrICiiigAooooAKKKKACiiigAooooAKKKKACiiigAooooAKKKKACiiigAooooAKKKKACiiigAooooAKKKKACiiigAooooAKKKKACiiigAooooAKKKKACiiigAooooA//2Q==");
        var padded = new byte[CoverArtService.MinCoverBytes + 64];
        Buffer.BlockCopy(encoded, 0, padded, 0, encoded.Length);
        return padded;
    }

    [Fact]
    public void ColdStartCoverWarm_UsesShortIdleDeferralAndBoundedConcurrency()
    {
        // Prefer filling covers quickly over long first-paint idle deferral.
        Assert.Equal(TimeSpan.FromMilliseconds(50), CoverArtService.FirstPaintCoverWarmDelay);
        Assert.Equal(8, CoverArtService.BackgroundWarmConcurrency);
        Assert.Equal(16, CoverArtService.RequestedWarmConcurrency);
    }

    [Fact]
    public void TryDataUrl_ReturnsDataUrl_ForOnDiskJpeg()
    {
        var path = Path.Combine(_dir, "sample.jpg");
        File.WriteAllBytes(path, MinimalJpeg());

        var url = CoverArtService.TryDataUrl(path);

        Assert.NotNull(url);
        Assert.StartsWith("data:image/jpeg;base64,", url);
        Assert.True(url!.Length > 40);
    }

    [Fact]
    public void TryDataUrl_ReturnsNull_ForMissingFile()
    {
        Assert.Null(CoverArtService.TryDataUrl(Path.Combine(_dir, "nope.jpg")));
    }

    [Fact]
    public void TryDataUrl_ReturnsNull_ForHtmlMasqueradingAsImage()
    {
        var path = Path.Combine(_dir, "fake.jpg");
        var html = System.Text.Encoding.UTF8.GetBytes("<!DOCTYPE html><html>" + new string('x', 900));
        File.WriteAllBytes(path, html);
        Assert.Null(CoverArtService.TryDataUrl(path));
    }

    [Fact]
    public void IsUnreliableCoverUrl_AllowsSteamCdn_DataAndVirtualHost()
    {
        // Steam CDN is allowlisted so tiles can paint while disk cache warms.
        Assert.False(CoverArtService.IsUnreliableCoverUrl(
            "https://cdn.cloudflare.steamstatic.com/steam/apps/730/library_600x900.jpg"));
        Assert.False(CoverArtService.IsUnreliableCoverUrl(
            "https://covers.exo-launcher.local/730.jpg"));
        Assert.False(CoverArtService.IsUnreliableCoverUrl("data:image/jpeg;base64,abc"));
        Assert.False(CoverArtService.IsUnreliableCoverUrl(null));
        Assert.True(CoverArtService.IsUnreliableCoverUrl("https://evil.example/x.jpg"));
    }

    [Fact]
    public void ExtractSteamAppIdFromUrl_ParsesSteamCdn()
    {
        Assert.Equal("730", CoverArtService.ExtractSteamAppIdFromUrl(
            "https://cdn.cloudflare.steamstatic.com/steam/apps/730/library_600x900_2x.jpg"));
        Assert.Null(CoverArtService.ExtractSteamAppIdFromUrl("https://example.com/nope"));
        Assert.Null(CoverArtService.ExtractSteamAppIdFromUrl(null));
    }

    [Fact]
    public void WithCover_UsesVirtualHost_WhenSteamCacheExists()
    {
        const string appId = "730001199";
        var dest = Path.Combine(CoverArtService.CacheRoot, appId + ".jpg");
        try
        {
            File.WriteAllBytes(dest, MinimalJpeg());
            var game = new GameEntry
            {
                Id = "steam:" + appId,
                Title = "Fixture Game",
                Store = StoreKind.Steam,
                Installed = true,
                LaunchTarget = appId,
                // Provisional CDN — disk cache must win.
                CoverUrl = $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/library_600x900.jpg",
            };

            var resolved = CoverArtService.WithCover(game);

            Assert.Equal($"{CoverArtService.VirtualHostOrigin}/{appId}.jpg", resolved.CoverUrl);
        }
        finally
        {
            try { File.Delete(dest); } catch { /* */ }
        }
    }

    [Fact]
    public async Task WarmCache_DoesNotRepublishLibrary_WhenPosterIsAlreadyCached()
    {
        var appId = "cached-noop-" + Guid.NewGuid().ToString("N");
        var dest = Path.Combine(CoverArtService.CacheRoot, appId + ".jpg");
        try
        {
            File.WriteAllBytes(dest, MinimalJpeg());
            var callbacks = 0;
            var game = new GameEntry
            {
                Id = "steam:" + appId,
                Title = "Already Cached",
                Store = StoreKind.Steam,
                LaunchTarget = appId,
            };

            await CoverArtService.WarmCacheAsync(
                [game],
                () => Interlocked.Increment(ref callbacks));

            Assert.Equal(0, callbacks);
        }
        finally
        {
            try { File.Delete(dest); } catch { /* */ }
        }
    }

    [Fact]
    public void PreferLocalArt_LargeFile_EmitsCspAllowedVirtualHost_NotBareCdn()
    {
        var path = Path.Combine(_dir, "big.jpg");
        // Larger than MaxDataUrlBytes but valid JPEG header
        var bytes = new byte[CoverArtService.MaxDataUrlBytes + 5000];
        bytes[0] = 0xFF; bytes[1] = 0xD8; bytes[2] = 0xFF; bytes[3] = 0xE0;
        bytes[^2] = 0xFF; bytes[^1] = 0xD9;
        File.WriteAllBytes(path, bytes);

        var dest = Path.Combine(CoverArtService.CacheRoot, "big-test-cover.jpg");
        try
        {
            File.Copy(path, dest, overwrite: true);
            var url = CoverArtService.PreferLocalArt(dest, "big-test-cover.jpg");
            Assert.NotNull(url);
            // Must be UI-loadable under shipped CSP (data: or covers.exo-launcher.local) — never CDN.
            Assert.True(
                CoverArtService.IsUiLoadableCoverUrl(url),
                "PreferLocalArt returned a URL the WebView CSP would block: " + url);
            Assert.Equal($"{CoverArtService.VirtualHostOrigin}/big-test-cover.jpg", url);
            Assert.DoesNotContain("steamstatic", url, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { File.Delete(dest); } catch { /* */ }
        }
    }

    [Fact]
    public void PreferLocalArt_PrefersSiblingJpeg_WhenPngTooLargeForDataUrl()
    {
        var png = Path.Combine(CoverArtService.CacheRoot, "sibling-cover-test.png");
        var jpg = Path.Combine(CoverArtService.CacheRoot, "sibling-cover-test.jpg");
        try
        {
            // Huge PNG (over data-URL budget)
            var big = new byte[CoverArtService.MaxDataUrlBytes + 8000];
            big[0] = 0x89; big[1] = 0x50; big[2] = 0x4E; big[3] = 0x47;
            File.WriteAllBytes(png, big);

            // Compact JPEG sibling
            var small = MinimalJpeg();
            File.WriteAllBytes(jpg, small);

            var url = CoverArtService.PreferLocalArt(png, "sibling-cover-test.png");
            Assert.NotNull(url);
            // Grid prefers virtual host over inlining posters into RPC payloads.
            Assert.Equal($"{CoverArtService.VirtualHostOrigin}/sibling-cover-test.jpg", url);
            Assert.True(CoverArtService.IsUiLoadableCoverUrl(url));
        }
        finally
        {
            try { File.Delete(png); } catch { /* */ }
            try { File.Delete(jpg); } catch { /* */ }
        }
    }

    [Fact]
    public void IsUiLoadableCoverUrl_MatchesShippedCspImgSrc()
    {
        Assert.True(CoverArtService.IsUiLoadableCoverUrl("data:image/jpeg;base64,abc"));
        Assert.True(CoverArtService.IsUiLoadableCoverUrl($"{CoverArtService.VirtualHostOrigin}/730.jpg"));
        Assert.True(CoverArtService.IsUiLoadableCoverUrl(
            "https://cdn.cloudflare.steamstatic.com/steam/apps/730/library_600x900_2x.jpg"));
        Assert.True(CoverArtService.IsUiLoadableCoverUrl(
            "https://cdn1.epicgames.com/offer/fn/portrait.jpg"));
        Assert.True(CoverArtService.IsUiLoadableCoverUrl(
            "https://images.gog-statics.com/abc_product_tile_2560x1440.jpg"));
        Assert.False(CoverArtService.IsUiLoadableCoverUrl(
            "https://cdn.cloudflare.steamstatic.com/steam/apps/730/library_hero.jpg"));
        Assert.False(CoverArtService.IsUiLoadableCoverUrl("https://evil.example/gog-statics.com/x.jpg"));
        Assert.False(CoverArtService.IsAllowlistedCdnCover("https://evil.example/gog-statics.com/x.jpg"));
        Assert.False(CoverArtService.IsUiLoadableCoverUrl($"{CoverArtService.VirtualHostOrigin}/../secret"));
        Assert.False(CoverArtService.IsUiLoadableCoverUrl(null));

        // Shipped CSP in ui/index.html must allow virtual host + Steam portrait CDNs.
        string? indexHtml = null;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "ui", "index.html");
            if (File.Exists(candidate)) { indexHtml = candidate; break; }
            candidate = Path.Combine(dir.FullName, "ExoLauncher", "wwwroot", "index.html");
            if (File.Exists(candidate)) { indexHtml = candidate; break; }
        }
        Assert.False(string.IsNullOrEmpty(indexHtml), "Could not locate ui/index.html or wwwroot/index.html to audit CSP");
        var html = File.ReadAllText(indexHtml!);
        Assert.Contains("covers.exo-launcher.local", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("steamstatic.com", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("img-src", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("data:", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IsUnreliableCoverUrl_AllowsVirtualHost_AndSteamCdn()
    {
        Assert.False(CoverArtService.IsUnreliableCoverUrl(
            $"https://{CoverArtService.VirtualHost}/730.jpg"));
        Assert.False(CoverArtService.IsUnreliableCoverUrl(
            "https://cdn.cloudflare.steamstatic.com/steam/apps/730/library_600x900.jpg"));
        Assert.True(CoverArtService.IsUnreliableCoverUrl("https://random-cdn.example/x.jpg"));
    }

    [Fact]
    public void WithCover_EmitsProvisionalSteamCdn_WhenDiskCacheEmpty()
    {
        const string appId = "999000111";
        var dest = Path.Combine(CoverArtService.CacheRoot, appId + ".jpg");
        var dest2x = Path.Combine(CoverArtService.CacheRoot, appId + "_2x.jpg");
        try { if (File.Exists(dest)) File.Delete(dest); } catch { /* */ }
        try { if (File.Exists(dest2x)) File.Delete(dest2x); } catch { /* */ }

        var game = new GameEntry
        {
            Id = "steam:" + appId,
            Title = "No Art Yet",
            Store = StoreKind.Steam,
            Installed = true,
            LaunchTarget = appId,
        };

        var resolved = CoverArtService.WithCover(game);

        Assert.Equal(CoverArtService.SteamPortraitCdnUrl(appId), resolved.CoverUrl);
        Assert.True(CoverArtService.IsUiLoadableCoverUrl(resolved.CoverUrl));
    }

    [Fact]
    public void ResolvePreferredUrl_DoesNotEmitRawCdnWithoutCache()
    {
        const string appId = "888000222";
        var dest = Path.Combine(CoverArtService.CacheRoot, appId + ".jpg");
        var dest2x = Path.Combine(CoverArtService.CacheRoot, appId + "_2x.jpg");
        try { if (File.Exists(dest)) File.Delete(dest); } catch { /* */ }
        try { if (File.Exists(dest2x)) File.Delete(dest2x); } catch { /* */ }

        var game = new GameEntry
        {
            Id = "steam:" + appId,
            Title = "Empty",
            Store = StoreKind.Steam,
            LaunchTarget = appId,
        };

        var url = CoverArtService.ResolvePreferredUrl(game);
        Assert.Null(url);
    }

    [Fact]
    public void WithCover_UsesMappedSteamPoster_ForKnownMultiStoreTitles()
    {
        // Epic-listed titles that still have official Steam library posters.
        var game = new GameEntry
        {
            Id = "epic:seeded-title",
            Title = "MECCHA CHAMELEON",
            Store = StoreKind.Epic,
            Installed = true,
        };

        var resolved = CoverArtService.WithCover(game);

        Assert.NotNull(resolved.CoverUrl);
        Assert.True(CoverArtService.IsUiLoadableCoverUrl(resolved.CoverUrl));
        // Disk cache or provisional CDN — both are official Steam poster for 4704690.
        Assert.Contains("4704690", resolved.CoverUrl, StringComparison.Ordinal);
    }

    [Fact]
    public void WithCover_StripsUnknownHttpsCover_ForUnmappedTitle()
    {
        var game = new GameEntry
        {
            Id = "epic:unknown-thing",
            Title = "Definitely Not A Seeded Cover Title Zz",
            Store = StoreKind.Epic,
            CoverUrl = "https://evil.example/not-a-poster.jpg",
        };

        var resolved = CoverArtService.WithCover(game);

        Assert.Null(resolved.CoverUrl);
    }

    [Fact]
    public void SteamAppId_ReadsFromIdAndLaunchTarget()
    {
        var byTarget = new GameEntry
        {
            Id = "steam:x",
            Title = "T",
            Store = StoreKind.Steam,
            LaunchTarget = "1145360",
        };
        Assert.Equal("1145360", CoverArtService.SteamAppId(byTarget));

        var byId = new GameEntry
        {
            Id = "steam:570",
            Title = "T",
            Store = StoreKind.Steam,
        };
        Assert.Equal("570", CoverArtService.SteamAppId(byId));
    }

    [Fact]
    public void EpicCatalogArt_ProductSlug_AndSeeds_CoverRiotTitles()
    {
        Assert.Equal("valorant", EpicCatalogArt.ProductSlug("VALORANT"));
        Assert.Equal("league-of-legends", EpicCatalogArt.ProductSlug("League of Legends"));
    }

    [Fact]
    public void ReadImageSize_PrefersLargestJpegFrame_NotExifThumbnail()
    {
        // Steam library capsules often embed a tiny SOF before the real 600×900.
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ExoLauncher", "covers", "3472040_2x.jpg");
        if (!File.Exists(path)) return;

        var size = CoverArtService.ReadImageSize(path);
        Assert.NotNull(size);
        Assert.True(size.Value.Width >= 300, $"expected main frame, got {size}");
        Assert.True(size.Value.Height >= 450, $"expected main frame, got {size}");
        Assert.True(CoverArtService.IsPortraitCover(path));
    }

    [Fact]
    public void BuildSteamLibraryCapsuleUrls_UsesHashedStoreItemAssets()
    {
        var urls = CoverArtService.BuildSteamLibraryCapsuleUrls(
            "steam/apps/2001760/${FILENAME}?t=1785818525",
            "f257ca2dc23c5590ade297fca68f3cab2e4edb4e/library_capsule_2x.jpg",
            "f257ca2dc23c5590ade297fca68f3cab2e4edb4e/library_capsule.jpg");

        Assert.Contains(
            "https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/2001760/f257ca2dc23c5590ade297fca68f3cab2e4edb4e/library_capsule_2x.jpg?t=1785818525",
            urls);
        Assert.Contains(
            "https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/2001760/f257ca2dc23c5590ade297fca68f3cab2e4edb4e/library_capsule.jpg?t=1785818525",
            urls);
        Assert.All(urls, u => Assert.DoesNotContain("library_hero", u, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ShouldWarmLibraryCover_IncludesOwnedAndEveryStore_SkipsJunk()
    {
        Assert.True(CoverArtService.ShouldWarmLibraryCover(new GameEntry
        {
            Id = "xbox:halo",
            Title = "Halo Infinite",
            Store = StoreKind.Xbox,
            Installed = true,
        }));
        Assert.True(CoverArtService.ShouldWarmLibraryCover(new GameEntry
        {
            Id = "gog:1",
            Title = "Stardew Valley",
            Store = StoreKind.Gog,
            Installed = false,
            Owned = true,
            CanInstall = true,
        }));
        Assert.True(CoverArtService.ShouldWarmLibraryCover(new GameEntry
        {
            Id = "ea:bf",
            Title = "Battlefield 1",
            Store = StoreKind.Ea,
            Installed = true,
        }));
        Assert.True(CoverArtService.ShouldWarmLibraryCover(new GameEntry
        {
            Id = "ubisoft:ac",
            Title = "Assassin's Creed Valhalla",
            Store = StoreKind.Ubisoft,
            Installed = true,
        }));
        Assert.False(CoverArtService.ShouldWarmLibraryCover(new GameEntry
        {
            Id = "local:add",
            Title = "Add portable game",
            Store = StoreKind.Local,
            Owned = true,
            CanInstall = true,
        }));
        Assert.False(CoverArtService.ShouldWarmLibraryCover(new GameEntry
        {
            Id = "epic:plug",
            Title = "Cool Lighting Plugin",
            Store = StoreKind.Epic,
            Owned = true,
        }));
    }

    [Fact]
    public void GogCoverCandidateUrls_PrefersAdapterCover_OverInventedIdTile()
    {
        var game = new GameEntry
        {
            Id = "gog:1207658787",
            Title = "The Witcher 3",
            Store = StoreKind.Gog,
            CoverUrl = "https://images.gog-statics.com/abc123_glx_vertical_cover.webp",
        };

        var urls = CoverArtService.GogCoverCandidateUrls(game);

        Assert.Equal(game.CoverUrl, urls[0]);
        Assert.DoesNotContain(
            urls,
            u => u.Contains("1207658787_product_tile", StringComparison.Ordinal));
    }

    [Fact]
    public void ParseGogV2CoverUrls_ExpandsFormatterIntoVerticalCover()
    {
        const string json =
            """{"_links":{"boxArtImage":{"href":"https://images.gog-statics.com/deadbeef{formatter}.jpg"}}}""";

        var urls = CoverArtService.ParseGogV2CoverUrls(json);

        Assert.Contains(
            "https://images.gog-statics.com/deadbeef_glx_vertical_cover.jpg",
            urls);
        Assert.DoesNotContain(urls, u => u.Contains("{formatter}", StringComparison.Ordinal));
    }

    [Fact]
    public void ScoreTitleMatch_CompactXboxFolderEqualsSpacedSteamName()
    {
        Assert.True(CoverArtService.ScoreTitleMatch("haloinfinite", "halo infinite") >= 90);
        Assert.True(CoverArtService.ScoreTitleMatch("forzahorizon5", "forza horizon 5") >= 90);
    }

    [Fact]
    public void IsCoverImageBytes_AcceptsJpegPngAndWebp()
    {
        Assert.True(CoverArtService.IsCoverImageBytes([0xFF, 0xD8, 0xFF]));
        Assert.True(CoverArtService.IsCoverImageBytes([0x89, 0x50, 0x4E, 0x47]));
        var webp = new byte[12];
        webp[0] = (byte)'R';
        webp[8] = (byte)'W';
        Assert.True(CoverArtService.IsCoverImageBytes(webp));
        Assert.False(CoverArtService.IsCoverImageBytes([(byte)'<', (byte)'h']));
    }
}
