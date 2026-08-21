using ExoLauncher.Models;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

public class CoverArtMachineValorantTests
{
    private static GameEntry Valorant(string? coverUrl = null) => new()
    {
        Id = "riot:valorant",
        Title = "VALORANT",
        Store = StoreKind.Riot,
        Installed = true,
        LaunchTarget = "valorant",
        CoverUrl = coverUrl,
    };

    /// <summary>
    /// Riot has no public cover endpoint, so Exo never fetches art for it. Art
    /// already warmed into Exo's own cache is still shown — dropping it left real
    /// tiles blank. Whatever is emitted must be loadable under the shipped CSP.
    /// </summary>
    [Fact]
    public void Machine_ValorantCover_UsesCachedArtWhenPresent()
    {
        var preferred = CoverArtService.ResolvePreferredUrl(Valorant());

        // Art lands as riot_valorant.<ext> (Epic portrait) or riot_valorant_card.png
        // (Riot's own theme art), so match the whole family.
        var cached = Directory.Exists(CoverArtService.CacheRoot)
                     && Directory.EnumerateFiles(CoverArtService.CacheRoot, "riot_valorant*").Any(
                         CoverArtService.IsValidImageFile);

        if (!cached)
        {
            Assert.Null(preferred);
            return;
        }

        Assert.NotNull(preferred);
        Assert.True(CoverArtService.IsUiLoadableCoverUrl(preferred),
            $"Cover URL must be loadable by native Image, got: {preferred}");
    }

    [Fact]
    public void Machine_ValorantCover_NeverEmitsRawCdnUrl()
    {
        var with = CoverArtService.WithCover(Valorant("https://example.invalid/art.jpg"));

        Assert.False(CoverArtService.IsUnreliableCoverUrl(with.CoverUrl),
            $"Raw CDN URLs must never reach the UI, got: {with.CoverUrl}");
        if (with.CoverUrl is not null)
            Assert.True(CoverArtService.IsUiLoadableCoverUrl(with.CoverUrl));
    }
}
