using System.Text.Json;
using ExoLauncher.Models;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// Banners: every store gets landscape art or an honestly derived wash, and the
/// Epic catalog backs off for a window instead of dying for the whole session.
/// </summary>
public sealed class WideArtCoverTests
{
    /// <summary>
    /// ReadImageSize memoises on (length, mtime), so a fixture that replaces a
    /// file in place has to change its length.
    /// </summary>
    private static byte[] FakePng(int width, int height, int extraBytes = 0)
    {
        var bytes = new byte[CoverArtService.MinCoverBytes + 64 + extraBytes];
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
    public void WideArtFileName_MatchesTheVirtualHostNameTheUiAsksFor()
    {
        Assert.Equal("hero_riot_valorant.jpg", CoverArtService.WideArtFileName("riot:valorant"));
        Assert.Equal("hero_steam_252950.jpg", CoverArtService.WideArtFileName("steam:252950"));

        var url = $"{CoverArtService.VirtualHostOrigin}/{CoverArtService.WideArtFileName("epic:Sugar")}";
        Assert.True(CoverArtService.IsUiLoadableCoverUrl(url), url);

        // The React side builds the same name from the game id. It mirrors
        // char.IsLetterOrDigit per UTF-16 code unit, so a looser regex here would
        // request a file the host never wrote. WideArtNamingTests pins the rest.
        var cover = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "src", "components", "CoverArt.tsx"));
        Assert.Contains("hero_${sanitizeCacheId(gameId)}.jpg", cover, StringComparison.Ordinal);
    }

    [Fact]
    public void IsWideArt_TakesLandscapeOnly()
    {
        var dir = Path.Combine(Path.GetTempPath(), "exo-wide-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var hero = Path.Combine(dir, "hero.png");
            var poster = Path.Combine(dir, "poster.png");
            var sliver = Path.Combine(dir, "sliver.png");
            File.WriteAllBytes(hero, FakePng(1920, 620));
            File.WriteAllBytes(poster, FakePng(600, 900));
            File.WriteAllBytes(sliver, FakePng(1920, 60));

            Assert.True(CoverArtService.IsWideArt(hero));
            Assert.False(CoverArtService.IsWideArt(poster));
            Assert.False(CoverArtService.IsWideArt(sliver));
            Assert.Equal(1.2, CoverArtService.MinWideAspect);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* */ }
        }
    }

    [Fact]
    public void ResolveWideArtUrl_UsesCachedLandscape_AndRefusesAPoster()
    {
        var game = new GameEntry
        {
            Id = "riot:wide-fixture-" + Guid.NewGuid().ToString("N"),
            Title = "Wide Fixture",
            Store = StoreKind.Riot,
            Installed = true,
        };
        var dest = Path.Combine(CoverArtService.CacheRoot, CoverArtService.WideArtFileName(game.Id));
        Directory.CreateDirectory(CoverArtService.CacheRoot);
        try
        {
            Assert.Null(CoverArtService.ResolveWideArtUrl(game));

            File.WriteAllBytes(dest, FakePng(1920, 620));
            var url = CoverArtService.ResolveWideArtUrl(game);
            Assert.Equal($"{CoverArtService.VirtualHostOrigin}/{Path.GetFileName(dest)}", url);
            Assert.True(CoverArtService.IsUiLoadableCoverUrl(url));

            // A poster in the banner slot would stretch — never offered as wide art.
            File.WriteAllBytes(dest, FakePng(600, 900, extraBytes: 512));
            Assert.Null(CoverArtService.ResolveWideArtUrl(game));
        }
        finally
        {
            try { File.Delete(dest); } catch { /* */ }
        }
    }

    [Fact]
    public void ShouldWarmWideArt_CoversWhatBannersShow_AndSkipsJunk()
    {
        Assert.True(CoverArtService.ShouldWarmWideArt(new GameEntry
        {
            Id = "riot:valorant",
            Title = "VALORANT",
            Store = StoreKind.Riot,
            Installed = true,
        }));
        Assert.True(CoverArtService.ShouldWarmWideArt(new GameEntry
        {
            Id = "gog:1",
            Title = "Pinned Not Installed",
            Store = StoreKind.Gog,
            IsFavorite = true,
        }));
        Assert.False(CoverArtService.ShouldWarmWideArt(new GameEntry
        {
            Id = "epic:owned-only",
            Title = "Owned But Never Shown Wide",
            Store = StoreKind.Epic,
            Owned = true,
        }));
        Assert.False(CoverArtService.ShouldWarmWideArt(new GameEntry
        {
            Id = "local:add",
            Title = "Add portable game",
            Store = StoreKind.Local,
        }));
        Assert.False(CoverArtService.ShouldWarmWideArt(new GameEntry
        {
            Id = "epic:plug",
            Title = "Cool Lighting Plugin",
            Store = StoreKind.Epic,
            Installed = true,
        }));
    }

    [Fact]
    public void EpicRefusals_PauseForAWindow_NotForTheWholeSession()
    {
        try
        {
            EpicCatalogArt.ResetBackoff();
            Assert.False(EpicCatalogArt.IsBlocked);

            for (var i = 1; i < EpicCatalogArt.RefusalLimit; i++)
            {
                EpicCatalogArt.NoteRefusal();
                Assert.False(EpicCatalogArt.IsBlocked);
            }
            EpicCatalogArt.NoteRefusal();
            Assert.True(EpicCatalogArt.IsBlocked);

            // Bounded windows, and one call that gets through clears the pause.
            Assert.Equal(TimeSpan.FromMinutes(5), EpicCatalogArt.RefusalBackoffFor(1));
            Assert.Equal(TimeSpan.FromMinutes(10), EpicCatalogArt.RefusalBackoffFor(2));
            Assert.Equal(TimeSpan.FromMinutes(30), EpicCatalogArt.RefusalBackoffFor(9));
            EpicCatalogArt.ResetBackoff();
            Assert.False(EpicCatalogArt.IsBlocked);
        }
        finally
        {
            EpicCatalogArt.ResetBackoff();
        }

        var source = File.ReadAllText(
            Path.Combine(RepoRoot(), "ExoLauncher", "Services", "EpicCatalogArt.cs"));
        Assert.DoesNotContain("skipping Epic art this session", source, StringComparison.Ordinal);
        Assert.Contains("EpicCatCacheArt.FindWideUrl(lookup)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void EpicCatCache_IndexesWideKeyImages_PreferringTheStoreFront()
    {
        var wide = new Dictionary<string, string>(StringComparer.Ordinal);
        EpicCatCacheArt.IndexCatalogElementWide(wide, """
            {
              "title": "Rocket League",
              "keyImages": [
                { "type": "DieselGameBoxTall", "url": "https://cdn1.epicgames.com/rl-tall.jpg" },
                { "type": "DieselGameBoxWide", "url": "https://cdn1.epicgames.com/rl-box-wide.jpg" },
                { "type": "OfferImageWide", "url": "https://cdn1.epicgames.com/rl-front.jpg" }
              ],
              "releaseInfo": [ { "appId": "Sugar" } ]
            }
            """);

        Assert.Equal(
            "https://cdn1.epicgames.com/rl-front.jpg?h=720&w=1280&resize=1&quality=high",
            wide["rocket league"]);
        Assert.Equal(wide["rocket league"], wide["sugar"]);

        var tallOnly = new Dictionary<string, string>(StringComparer.Ordinal);
        EpicCatCacheArt.IndexCatalogElementWide(tallOnly, """
            {
              "title": "Tall Only",
              "keyImages": [
                { "type": "OfferImageTall", "url": "https://cdn1.epicgames.com/tall.jpg" }
              ]
            }
            """);
        Assert.Empty(tallOnly);
    }

    [Fact]
    public void ParseGogV2BackgroundUrls_ExpandsFormatter_AndStaysOnGogStatics()
    {
        var urls = CoverArtService.ParseGogV2BackgroundUrls(
            """{"_links":{"backgroundImage":{"href":"https://images.gog-statics.com/deadbeef{formatter}.webp"}}}""");

        Assert.Contains("https://images.gog-statics.com/deadbeef_bg_crop_1920x655.webp", urls);
        Assert.Contains("https://images.gog-statics.com/deadbeef_bg_crop_1920x655.jpg", urls);
        Assert.DoesNotContain(urls, u => u.Contains("{formatter}", StringComparison.Ordinal));

        var offsite = CoverArtService.ParseGogV2BackgroundUrls(
            """{"_links":{"backgroundImage":{"href":"https://evil.example/x{formatter}.jpg"}}}""");
        Assert.Empty(offsite);
    }

    [Fact]
    public void ReadThemeWideImages_ReadsRiotManifestBackgrounds()
    {
        using var doc = JsonDocument.Parse("""
            {
              "game_library": {
                "product_card_image": "card.png",
                "background_image": "library-bg.jpg"
              },
              "splash_image": "splash.jpg"
            }
            """);

        var images = CoverArtService.ReadThemeWideImages(doc.RootElement);

        Assert.Equal("library-bg.jpg", images[0]);
        Assert.Contains("splash.jpg", images);
        Assert.DoesNotContain("card.png", images);
    }

    [Fact]
    public void HeroWash_HasANonSteamPath_AndLetterboxesShortLandscape()
    {
        var cover = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "src", "components", "CoverArt.tsx"));
        var tokens = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "src", "tokens.css"));

        // Wide art is a real chain, not "Steam app id or nothing".
        Assert.Contains("function wideArtCandidates", cover, StringComparison.Ordinal);
        Assert.Contains("for (const hero of steamHeroUrls(game)) push(hero, false)", cover, StringComparison.Ordinal);
        Assert.Contains("push(wideCacheUrl(game.id, game.artRevision), false)", cover, StringComparison.Ordinal);
        Assert.Contains("push(game.coverUrl ? withArtRevision(game.coverUrl, game.artRevision) : null, true)", cover, StringComparison.Ordinal);
        Assert.True(
            cover.IndexOf("push(wideCacheUrl(game.id), false)", StringComparison.Ordinal) <
            cover.IndexOf("for (const hero of steamHeroUrls(game))", StringComparison.Ordinal),
            "The warmed local hero must be tried before remote Steam heroes.");
        var fit = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "src", "lib", "coverFit.ts"));
        Assert.Contains("return width / height <= MAX_COVER_ASPECT", fit, StringComparison.Ordinal);
        Assert.Contains("MAX_COVER_ASPECT = 0.90", fit, StringComparison.Ordinal);
        Assert.DoesNotContain("export function coverBg", cover, StringComparison.Ordinal);
        // A portrait standing in for a banner is washed, never stretched.
        Assert.Contains("isWideBitmap", cover, StringComparison.Ordinal);
        Assert.Contains("setWashed(!wide)", cover, StringComparison.Ordinal);
        Assert.Contains("washed && 'exo-cover-derived'", cover, StringComparison.Ordinal);
        // Purpose-built wide art remains available to detail/profile surfaces.
        Assert.Contains("isHeroShaped", cover, StringComparison.Ordinal);
        Assert.Contains("is-letterbox", cover, StringComparison.Ordinal);
        Assert.Contains("return width / height >= 2.5", fit, StringComparison.Ordinal);
        Assert.DoesNotContain("object-contain", cover, StringComparison.Ordinal);
        Assert.Contains(".exo-cover.is-icon img", tokens, StringComparison.Ordinal);
        Assert.Contains(".exo-cover-derived {", tokens, StringComparison.Ordinal);
        Assert.Contains("blur(", tokens, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ExoLauncher.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
