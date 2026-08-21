using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using ExoLauncher.Helpers;
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

    public CoverArtServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "exo-cover-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        // Cache-root tests use unique app ids and clean up their files explicitly.
        Directory.CreateDirectory(CoverArtService.CacheRoot);
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

    private static byte[] FakePng(int width, int height)
    {
        var bytes = new byte[CoverArtService.MinCoverBytes + 64];
        bytes[0] = 0x89;
        bytes[1] = 0x50;
        bytes[2] = 0x4E;
        bytes[3] = 0x47;
        bytes[4] = 0x0D;
        bytes[5] = 0x0A;
        bytes[6] = 0x1A;
        bytes[7] = 0x0A;
        bytes[16] = (byte)(width >> 24);
        bytes[17] = (byte)(width >> 16);
        bytes[18] = (byte)(width >> 8);
        bytes[19] = (byte)width;
        bytes[20] = (byte)(height >> 24);
        bytes[21] = (byte)(height >> 16);
        bytes[22] = (byte)(height >> 8);
        bytes[23] = (byte)height;
        return bytes;
    }

    [Fact]
    public void ColdStartCoverWarm_UsesShortIdleDeferralAndBoundedConcurrency()
    {
        // Prefer filling covers quickly over long first-paint idle deferral.
        Assert.Equal(TimeSpan.FromMilliseconds(50), CoverArtService.FirstPaintCoverWarmDelay);
        Assert.Equal(4, CoverArtService.BackgroundWarmConcurrency);
        Assert.Equal(4, CoverArtService.RequestedWarmConcurrency);
        Assert.Equal(4, CoverArtService.RequestedWarmNotificationBatchSize);
    }

    [Fact]
    public async Task WarmCacheAsync_CacheOnlyResultDoesNotNotify()
    {
        var digits = string.Concat(Guid.NewGuid().ToString("N").Where(char.IsDigit));
        var appId = ("9" + digits).PadRight(10, '7')[..10];
        var game = new GameEntry
        {
            Id = "steam:" + appId,
            Title = "Cached Art Fixture",
            Store = StoreKind.Steam,
            Installed = true,
            LaunchTarget = appId,
        };
        var portrait = Path.Combine(CoverArtService.CacheRoot, appId + ".jpg");
        var slugPortrait = Path.Combine(CoverArtService.CacheRoot, "steam_" + appId + ".jpg");
        var wide = Path.Combine(CoverArtService.CacheRoot, CoverArtService.WideArtFileName(game.Id));
        var notifications = 0;

        try
        {
            File.WriteAllBytes(portrait, FakePng(600, 900));
            File.WriteAllBytes(wide, FakePng(1920, 620));

            await CoverArtService.WarmCacheAsync(
                    [game],
                    () => Interlocked.Increment(ref notifications),
                    requested: true,
                    deferForFirstPaint: false)
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(0, notifications);
        }
        finally
        {
            try { File.Delete(portrait); } catch { /* */ }
            try { File.Delete(slugPortrait); } catch { /* */ }
            try { File.Delete(wide); } catch { /* */ }
        }
    }

    [Fact]
    public void NeedsBackgroundWarm_VisibleTitleWithPortraitStillNeedsMissingWideArt()
    {
        var digits = string.Concat(Guid.NewGuid().ToString("N").Where(char.IsDigit));
        var appId = ("8" + digits).PadRight(10, '6')[..10];
        var game = new GameEntry
        {
            Id = "steam:" + appId,
            Title = "Portrait Only Fixture",
            Store = StoreKind.Steam,
            Installed = true,
            LaunchTarget = appId,
        };
        var portrait = Path.Combine(CoverArtService.CacheRoot, appId + ".jpg");
        var wide = Path.Combine(CoverArtService.CacheRoot, CoverArtService.WideArtFileName(game.Id));

        try
        {
            File.WriteAllBytes(portrait, FakePng(600, 900));
            Assert.True(CoverArtService.NeedsBackgroundWarm(game));

            File.WriteAllBytes(wide, FakePng(1920, 620));
            Assert.False(CoverArtService.NeedsBackgroundWarm(game));
        }
        finally
        {
            try { File.Delete(portrait); } catch { /* */ }
            try { File.Delete(wide); } catch { /* */ }
        }
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
    public void ProvisionalStorePosterUrl_FortniteAndValorant_AreOfficialEpicCdn()
    {
        var fortnite = CoverArtService.ProvisionalStorePosterUrl(new GameEntry
        {
            Id = "epic:Fortnite-anyone",
            Title = "Fortnite",
            Store = StoreKind.Epic,
            Installed = true,
            LaunchTarget = "Fortnite",
        });
        var valorant = CoverArtService.ProvisionalStorePosterUrl(new GameEntry
        {
            Id = "riot:valorant-anyone",
            Title = "VALORANT",
            Store = StoreKind.Riot,
            Installed = true,
            LaunchTarget = "valorant",
        });

        Assert.True(CoverArtService.IsOfficialEpicPortraitCdn(fortnite), fortnite);
        Assert.True(CoverArtService.IsOfficialEpicPortraitCdn(valorant), valorant);
        Assert.True(CoverArtService.IsOfficialEpicPortraitCdn(
            EpicCatalogArt.TrySeedPortraitUrl("Teamfight Tactics")));
        Assert.True(CoverArtService.IsOfficialEpicPortraitCdn(
            EpicCatalogArt.TrySeedPortraitUrl("Legends of Runeterra")));
        Assert.True(CoverArtService.IsOfficialEpicPortraitCdn(
            "https://cdn1.unrealengine.com/egs-example-s2-1200x1600-aaaaaaaaaaaa.jpg"));
        Assert.True(CoverArtService.IsOfficialEpicPortraitCdn(
            "https://cdn2.epicgames.com/item/fn/example-1200x1600-bbbbbbbbbbbb.jpg"));
    }

    [Fact]
    public void WithCover_Fortnite_GetsLoadableArtWithoutExoDiskCache()
    {
        var with = CoverArtService.WithCover(new GameEntry
        {
            Id = "epic:Fortnite-anyone-nocache",
            Title = "Fortnite",
            Store = StoreKind.Epic,
            Installed = true,
            LaunchTarget = "Fortnite",
        });

        Assert.False(string.IsNullOrWhiteSpace(with.CoverUrl));
        Assert.True(CoverArtService.IsUiLoadableCoverUrl(with.CoverUrl), with.CoverUrl);
    }

    [Fact]
    public void TryImportSteamLibraryCachePoster_CopiesLibrary600x900()
    {
        const string appId = "999001199";
        var steam = Path.Combine(_dir, "Steam");
        Directory.CreateDirectory(Path.Combine(steam, "appcache", "librarycache", appId));
        File.WriteAllBytes(
            Path.Combine(steam, "appcache", "librarycache", appId, "library_600x900.jpg"),
            MinimalJpeg());
        var dest = Path.Combine(_dir, "imported.jpg");
        var dest2x = Path.Combine(_dir, "imported_2x.jpg");

        var ok = CoverArtService.TryImportSteamLibraryCachePoster(appId, dest2x, dest, steam);

        Assert.True(ok);
        Assert.True(File.Exists(dest));
        Assert.True(CoverArtService.IsPortraitCover(dest));
    }

    [Fact]
    public void TryImportSteamLibraryCachePoster_CopiesHashedPortraitWhenNamedPosterMissing()
    {
        const string appId = "999001188";
        var steam = Path.Combine(_dir, "Steam");
        var folder = Path.Combine(steam, "appcache", "librarycache", appId);
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "header.jpg"), MinimalJpeg());
        File.WriteAllBytes(
            Path.Combine(folder, "7807d6dcd71d8161465619b4f041794b0353a6d0.jpg"),
            MinimalJpeg());
        var dest = Path.Combine(_dir, "hashed.jpg");
        var dest2x = Path.Combine(_dir, "hashed_2x.jpg");

        var ok = CoverArtService.TryImportSteamLibraryCachePoster(appId, dest2x, dest, steam);

        Assert.True(ok);
        Assert.True(File.Exists(dest));
        Assert.True(CoverArtService.IsPortraitCover(dest));
    }

    [Fact]
    public void WithCover_KeepsStoreVariantsAndPlaytime()
    {
        var card = new GameEntry
        {
            Id = "steam:252950",
            Title = "Rocket League",
            Store = StoreKind.Steam,
            Installed = true,
            LaunchTarget = "252950",
            PlaytimeMinutes = 12,
            CanonicalTitleKey = "rocketleague",
            SelectedVariantId = "steam:252950",
            Variants =
            [
                new GameVariant
                {
                    Id = "steam:252950",
                    Store = StoreKind.Steam,
                    Installed = true,
                    LaunchTarget = "252950",
                    PlaytimeMinutes = 12,
                },
                new GameVariant
                {
                    Id = "epic:Sugar",
                    Store = StoreKind.Epic,
                    Installed = true,
                    LaunchTarget = "Sugar",
                    PlaytimeMinutes = 11_307,
                },
            ],
        };

        var with = CoverArtService.WithCover(card);

        Assert.Equal(2, with.Variants.Count);
        Assert.Equal(11_307, with.Variants.First(v => v.Id == "epic:Sugar").PlaytimeMinutes);
        Assert.Equal(12, with.PlaytimeMinutes);
        Assert.Equal("rocketleague", with.CanonicalTitleKey);
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
        const string appId = "999001266";
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
            Assert.Equal(appId, CoverArtService.SteamAppId(game));

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
                "PreferLocalArt returned a URL native Image would refuse: " + url);
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
        Assert.True(CoverArtService.IsAllowlistedCdnCover(
            "https://cdn.cloudflare.steamstatic.com/steam/apps/730/library_600x900_2x.jpg"));
        Assert.False(CoverArtService.IsUiLoadableCoverUrl(
            "https://cdn.cloudflare.steamstatic.com/steam/apps/730/library_600x900_2x.jpg"));
        Assert.True(CoverArtService.IsProvisionalSteamPosterCdn(
            "https://cdn.cloudflare.steamstatic.com/steam/apps/730/library_600x900.jpg"));
        Assert.True(CoverArtService.IsUiLoadableCoverUrl(
            "https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/3513350/abc/library_capsule.jpg"));
        Assert.True(CoverArtService.IsUiLoadableCoverUrl(
            "https://cdn1.epicgames.com/offer/fn/portrait.jpg"));
        Assert.True(CoverArtService.IsUiLoadableCoverUrl(
            "https://images.gog-statics.com/abc_product_tile_2560x1440.jpg"));
        Assert.True(CoverArtService.IsAllowlistedCdnCover(
            "https://store-images.s-microsoft.com/image/cover.jpg"));
        Assert.True(CoverArtService.IsAllowlistedCdnCover(
            "https://cdn.playvalorant.com/cover.jpg"));
        Assert.False(CoverArtService.IsUiLoadableCoverUrl(
            "https://cdn.cloudflare.steamstatic.com/steam/apps/730/library_hero.jpg"));
        Assert.False(CoverArtService.IsUiLoadableCoverUrl("https://evil.example/gog-statics.com/x.jpg"));
        Assert.False(CoverArtService.IsAllowlistedCdnCover("https://evil.example/gog-statics.com/x.jpg"));
        Assert.False(CoverArtService.IsUiLoadableCoverUrl($"{CoverArtService.VirtualHostOrigin}/../secret"));
        Assert.False(CoverArtService.IsUiLoadableCoverUrl(null));
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
        Assert.True(CoverArtService.IsOfficialSteamPortraitCdn(resolved.CoverUrl));
        Assert.True(CoverArtService.IsAllowlistedCdnCover(resolved.CoverUrl));
        Assert.False(CoverArtService.IsUiLoadableCoverUrl(resolved.CoverUrl));
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
        Assert.True(
            CoverArtService.IsUiLoadableCoverUrl(resolved.CoverUrl) ||
            CoverArtService.IsOfficialSteamPortraitCdn(resolved.CoverUrl),
            resolved.CoverUrl);
        // Disk cache or provisional CDN — both are official Steam poster for 4704690.
        Assert.Contains("4704690", resolved.CoverUrl, StringComparison.Ordinal);
    }

    [Fact]
    public void WithCover_UsesSouthOfMidnightsActualSteamPoster()
    {
        var resolved = CoverArtService.WithCover(new GameEntry
        {
            Id = "xbox:south-of-midnight-seed-regression",
            Title = "South of Midnight",
            Store = StoreKind.Xbox,
            Installed = true,
        });

        Assert.Contains("1934570", resolved.CoverUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WarmCache_UpgradesStalePersistedPerGameMap_ForSouthOfMidnight()
    {
        const string gameId = "xbox:south-of-midnight-persisted-regression";
        const string staleAppId = "2138720";
        const string correctedAppId = "1934570";
        const string slugFileName = "xbox_south_of_midnight_persisted_regression.jpg";
        var priorDataDir = Environment.GetEnvironmentVariable(PathHelper.DataDirOverrideVariable);
        var titleMap = GetTitleSteamMap();
        var priorTitleMap = titleMap.ToArray();

        try
        {
            titleMap.Clear();
            Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, _dir);
            Directory.CreateDirectory(CoverArtService.CacheRoot);
            var mapPath = Path.Combine(CoverArtService.CacheRoot, "title-steam-map.json");
            File.WriteAllText(mapPath, JsonSerializer.Serialize(new Dictionary<string, string>
            {
                [gameId] = staleAppId,
            }));
            // Mirror the persisted row in memory so unrelated fire-and-forget
            // cover warms cannot make EnsureTitleMapLoaded skip this fixture.
            titleMap[gameId] = staleAppId;
            File.WriteAllBytes(Path.Combine(CoverArtService.CacheRoot, staleAppId + ".jpg"), MinimalJpeg());
            File.WriteAllBytes(Path.Combine(CoverArtService.CacheRoot, correctedAppId + ".jpg"), MinimalJpeg());
            var staleSlugPath = Path.Combine(CoverArtService.CacheRoot, slugFileName);
            File.WriteAllBytes(staleSlugPath, MinimalJpeg());

            var game = new GameEntry
            {
                Id = gameId,
                Title = "South of Midnight",
                Store = StoreKind.Xbox,
                Installed = true,
            };

            await CoverArtService.WarmCacheAsync([game]);

            using var persisted = JsonDocument.Parse(File.ReadAllText(mapPath));
            Assert.Equal(correctedAppId, persisted.RootElement.GetProperty(gameId).GetString());
            Assert.False(File.Exists(staleSlugPath));
            Assert.Contains(correctedAppId, CoverArtService.WithCover(game).CoverUrl, StringComparison.Ordinal);
        }
        finally
        {
            titleMap.Clear();
            foreach (var entry in priorTitleMap)
                titleMap[entry.Key] = entry.Value;
            Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, priorDataDir);
        }
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
        Assert.Equal("fortnite", EpicCatalogArt.ProductSlug("Fortnite Battle Royale"));
        Assert.Equal("teamfight-tactics", EpicCatalogArt.ProductSlug("Teamfight Tactics"));
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
    public void SplitCamelTitle_SplitsLetterDigitAndCamelCase()
    {
        var forza = CoverArtService.SplitCamelTitle("ForzaHorizon5");
        var tokens = forza.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("Forza", tokens);
        Assert.Contains("Horizon", tokens);
        Assert.Contains("5", tokens);
        Assert.Equal("Halo Infinite", CoverArtService.SplitCamelTitle("HaloInfinite"));
        Assert.Equal("Forza Horizon 5", CoverArtService.CleanSearchTitle("ForzaHorizon5"));
    }

    [Fact]
    public void CleanSearchTitle_StripsEditionJunk_AndKeepsBaseTitle()
    {
        Assert.Equal("Forza Horizon 5", CoverArtService.CleanSearchTitle("Forza Horizon 5 Premium Edition"));
        Assert.Equal("Halo Infinite", CoverArtService.CleanSearchTitle("HaloInfinite Windows Edition"));
        Assert.Equal("Forza Horizon 5", CoverArtService.CleanSearchTitle("ForzaHorizon5 Game of the Year Edition"));
        Assert.Equal("Halo Infinite", CoverArtService.CleanSearchTitle("Halo Infinite Legendary Edition"));
    }

    [Fact]
    public void AcceptSteamTitleScore_Uses82ForMultiToken_90ForShort()
    {
        Assert.Equal(82, CoverArtService.AcceptSteamTitleScore("Forza Horizon 5"));
        Assert.Equal(82, CoverArtService.AcceptSteamTitleScore("Halo Infinite"));
        Assert.Equal(90, CoverArtService.AcceptSteamTitleScore("Hades"));
        Assert.Equal(TimeSpan.FromHours(18), CoverArtService.NegativeTitleMapTtl);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Assert.True(CoverArtService.IsActiveNegativeCache("0:" + (now - 3600)));
        Assert.False(CoverArtService.IsActiveNegativeCache("0:" + (now - 19 * 3600)));
        Assert.False(CoverArtService.IsActiveNegativeCache("0"));
    }

    [Fact]
    public void PickBestArt_PrefersPortrait()
    {
        var portrait = Path.Combine(_dir, "poster.png");
        var landscape = Path.Combine(_dir, "hero.png");
        File.WriteAllBytes(portrait, FakePng(600, 900));
        File.WriteAllBytes(landscape, FakePng(1920, 620));

        Assert.Equal(portrait, CoverArtService.PickBestArt([landscape, portrait]));
    }

    [Fact]
    public void IsPortraitCover_AcceptsPosterAndRejectsSquareArt()
    {
        var poster = Path.Combine(_dir, "classifier-poster.png");
        var square = Path.Combine(_dir, "classifier-square.png");
        var nearSquare = Path.Combine(_dir, "classifier-near-square.png");
        File.WriteAllBytes(poster, FakePng(600, 900));
        File.WriteAllBytes(square, FakePng(900, 900));
        File.WriteAllBytes(nearSquare, FakePng(950, 1000));

        Assert.Equal(0.90, CoverArtService.MaxCoverAspect);
        Assert.True(CoverArtService.IsPortraitCover(poster));
        Assert.False(CoverArtService.IsPortraitCover(square));
        Assert.False(CoverArtService.IsPortraitCover(nearSquare));
        Assert.Equal(poster, CoverArtService.PickBestArt([square, nearSquare, poster]));
    }

    [Fact]
    public void PickBestArt_RejectsLandscapeWhenNoPortraitExists()
    {
        var landscape = Path.Combine(_dir, "hero.png");
        File.WriteAllBytes(landscape, FakePng(1920, 620));

        Assert.Null(CoverArtService.PickBestArt([landscape]));
    }

    [Fact]
    public void DiscardIfLandscape_RemovesLandscapeFromPortraitCache()
    {
        var landscape = Path.Combine(_dir, "hero.png");
        File.WriteAllBytes(landscape, FakePng(1920, 620));

        CoverArtService.DiscardIfLandscape(landscape);

        Assert.False(File.Exists(landscape));
    }

    [Fact]
    public void ResolvePreferredUrl_RejectsLandscapeInPortraitCache()
    {
        const string appId = "730009911";
        var dest = Path.Combine(CoverArtService.CacheRoot, appId + ".jpg");
        try
        {
            File.WriteAllBytes(dest, FakePng(1920, 620));
            var url = CoverArtService.ResolvePreferredUrl(new GameEntry
            {
                Id = "steam:" + appId,
                Title = "Hero Only",
                Store = StoreKind.Steam,
                LaunchTarget = appId,
            });
            Assert.Null(url);
            Assert.False(File.Exists(dest));
        }
        finally
        {
            try { File.Delete(dest); } catch { /* */ }
        }
    }

    [Fact]
    public void TryImportSteamLibraryCachePoster_RejectsHeaderWhenNoPortrait()
    {
        const string appId = "999001177";
        var steam = Path.Combine(_dir, "SteamHero");
        Directory.CreateDirectory(Path.Combine(steam, "appcache", "librarycache", appId));
        File.WriteAllBytes(
            Path.Combine(steam, "appcache", "librarycache", appId, "header.jpg"),
            FakePng(1920, 620));
        var dest = Path.Combine(_dir, "hero-import.jpg");
        var dest2x = Path.Combine(_dir, "hero-import_2x.jpg");

        var ok = CoverArtService.TryImportSteamLibraryCachePoster(appId, dest2x, dest, steam);

        Assert.False(ok);
        Assert.False(File.Exists(dest));
        Assert.False(File.Exists(dest2x));
    }

    [Fact]
    public void WithCover_UsesSeededSteamPoster_ForCamelXboxFolderTitle()
    {
        var resolved = CoverArtService.WithCover(new GameEntry
        {
            Id = "xbox:forza-camel-cover",
            Title = "ForzaHorizon5",
            Store = StoreKind.Xbox,
            Installed = true,
        });

        Assert.False(string.IsNullOrWhiteSpace(resolved.CoverUrl));
        Assert.Contains("1551360", resolved.CoverUrl, StringComparison.Ordinal);
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

    [Fact]
    public void ResolvePreferredUrl_UsesPlatedExecutableIcon_WhenNoStoreArt()
    {
        var notepad = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
        if (!File.Exists(notepad)) return;

        var dest = Path.Combine(CoverArtService.CacheRoot, GameIconArt.CacheFileName("local:icon-test"));
        try
        {
            if (File.Exists(dest)) File.Delete(dest);
            Assert.True(GameIconArt.TryExtractFromExecutable(notepad, dest));
            Assert.True(GameIconArt.IsValidPlate(dest));

            var game = new GameEntry
            {
                Id = "local:icon-test",
                Title = "Icon Test",
                Store = StoreKind.Local,
                Installed = true,
                Path = Path.GetDirectoryName(notepad),
                LaunchTarget = notepad,
            };
            var url = CoverArtService.ResolvePreferredUrl(game);
            Assert.NotNull(url);
            Assert.Contains("icon_local_icon_test.png", url, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("icon", CoverArtService.WithCover(game).CoverSource);
        }
        finally
        {
            try { if (File.Exists(dest)) File.Delete(dest); } catch { /* */ }
        }
    }

    [Fact]
    public void WithCover_RebindsMismatchedPerGameMapToCurrentNormalizedTitle()
    {
        const string gameId = "xbox:title-binding-regression";
        const string staleAppId = "99110001";
        const string correctedAppId = "99110002";
        const string title = "Current Binding Fixture";
        var priorDataDir = Environment.GetEnvironmentVariable(PathHelper.DataDirOverrideVariable);
        var titleMap = GetTitleSteamMap();
        var priorTitleMap = titleMap.ToArray();

        try
        {
            titleMap.Clear();
            Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, _dir);
            Directory.CreateDirectory(CoverArtService.CacheRoot);
            titleMap[gameId] = staleAppId;
            titleMap[CoverArtService.GameTitleBindingKey(gameId)] = "different old title";
            titleMap[CoverArtService.NormalizedTitleBinding(title)] = correctedAppId;
            File.WriteAllBytes(
                Path.Combine(CoverArtService.CacheRoot, correctedAppId + ".jpg"),
                MinimalJpeg());
            var staleSlug = Path.Combine(
                CoverArtService.CacheRoot,
                "xbox_title_binding_regression.jpg");
            File.WriteAllBytes(staleSlug, MinimalJpeg());

            var resolved = CoverArtService.WithCover(new GameEntry
            {
                Id = gameId,
                Title = title,
                Store = StoreKind.Xbox,
                Installed = true,
            });

            Assert.Contains(correctedAppId, resolved.CoverUrl, StringComparison.Ordinal);
            Assert.Equal(correctedAppId, titleMap[gameId]);
            Assert.Equal(
                CoverArtService.NormalizedTitleBinding(title),
                titleMap[CoverArtService.GameTitleBindingKey(gameId)]);
            Assert.False(File.Exists(staleSlug));
        }
        finally
        {
            titleMap.Clear();
            foreach (var entry in priorTitleMap)
                titleMap[entry.Key] = entry.Value;
            Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, priorDataDir);
        }
    }

    [Fact]
    public void WithCover_MigratesOnlyLegacyPerGameMapConfirmedByTitleEntry()
    {
        const string confirmedGameId = "xbox:legacy-binding-confirmed";
        const string rejectedGameId = "xbox:legacy-binding-rejected";
        const string confirmedAppId = "99220001";
        const string rejectedAppId = "99220002";
        const string confirmedTitle = "Legacy Binding Confirmed Fixture";
        const string rejectedTitle = "Legacy Binding Rejected Fixture";
        var priorDataDir = Environment.GetEnvironmentVariable(PathHelper.DataDirOverrideVariable);
        var titleMap = GetTitleSteamMap();
        var priorTitleMap = titleMap.ToArray();

        try
        {
            titleMap.Clear();
            Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, _dir);
            Directory.CreateDirectory(CoverArtService.CacheRoot);
            titleMap[confirmedGameId] = confirmedAppId;
            titleMap[CoverArtService.NormalizedTitleBinding(confirmedTitle)] = confirmedAppId;
            titleMap[rejectedGameId] = rejectedAppId;
            File.WriteAllBytes(
                Path.Combine(CoverArtService.CacheRoot, confirmedAppId + ".jpg"),
                MinimalJpeg());
            var rejectedSlug = Path.Combine(
                CoverArtService.CacheRoot,
                "xbox_legacy_binding_rejected.jpg");
            File.WriteAllBytes(rejectedSlug, MinimalJpeg());

            var confirmed = CoverArtService.WithCover(new GameEntry
            {
                Id = confirmedGameId,
                Title = confirmedTitle,
                Store = StoreKind.Xbox,
                Installed = true,
            });
            var rejected = CoverArtService.WithCover(new GameEntry
            {
                Id = rejectedGameId,
                Title = rejectedTitle,
                Store = StoreKind.Xbox,
                Installed = true,
            });

            Assert.Contains(confirmedAppId, confirmed.CoverUrl, StringComparison.Ordinal);
            Assert.Equal(
                CoverArtService.NormalizedTitleBinding(confirmedTitle),
                titleMap[CoverArtService.GameTitleBindingKey(confirmedGameId)]);
            Assert.Null(rejected.CoverUrl);
            Assert.False(titleMap.ContainsKey(rejectedGameId));
            Assert.False(titleMap.ContainsKey(CoverArtService.GameTitleBindingKey(rejectedGameId)));
            Assert.False(File.Exists(rejectedSlug));
        }
        finally
        {
            titleMap.Clear();
            foreach (var entry in priorTitleMap)
                titleMap[entry.Key] = entry.Value;
            Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, priorDataDir);
        }
    }

    private static ConcurrentDictionary<string, string> GetTitleSteamMap()
    {
        var field = typeof(CoverArtService).GetField(
            "TitleSteamMap",
            BindingFlags.NonPublic | BindingFlags.Static);
        return Assert.IsType<ConcurrentDictionary<string, string>>(field?.GetValue(null));
    }
}
