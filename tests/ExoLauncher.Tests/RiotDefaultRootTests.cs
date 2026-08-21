using ExoLauncher.Adapters;
using Xunit;

namespace ExoLauncher.Tests;

public class RiotDefaultRootTests
{
    [Fact]
    public void DefaultRoots_DetectValorantAndLeague()
    {
        var v = RiotInstallProbe.FindInstalledProduct("valorant");
        var l = RiotInstallProbe.FindInstalledProduct("league_of_legends");
        var r = RiotInstallProbe.FindRiotClientServices();
        Assert.True(v != null, "valorant not found via DefaultRootCandidates");
        Assert.True(l != null, "league not found via DefaultRootCandidates");
        Assert.True(r != null, "RiotClientServices not found");
        Assert.Contains("VALORANT", v!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("League", l!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RiotAdapter_GetLibrary_MarksInstalledProductsReady()
    {
        var adapter = new RiotAdapter();
        var games = await adapter.GetLibraryAsync();
        Assert.NotEmpty(games);

        var valo = Assert.Single(games, g => g.Id == "riot:valorant");
        var league = Assert.Single(games, g => g.Id == "riot:league_of_legends");

        // Machine has C:\Riot Games products — adapter must surface Play, not Install.
        Assert.True(valo.Installed, "VALORANT should be Installed=true on this machine");
        Assert.Equal("play", valo.PrimaryAction);
        Assert.Equal("Ready", valo.Status);

        Assert.True(league.Installed, "League should be Installed=true on this machine");
        Assert.Equal("play", league.PrimaryAction);
        Assert.Equal("Ready", league.Status);
    }

    [Fact]
    public async Task RiotAdapter_GetLibrary_InstalledTitlesCarrySize_UninstalledDoNot()
    {
        var games = await new RiotAdapter().GetLibraryAsync();
        Assert.NotEmpty(games);

        foreach (var game in games)
        {
            if (game.Installed)
            {
                Assert.True(game.SizeBytes is > 0, $"{game.Title} is installed but SizeBytes is {game.SizeBytes}");
                Assert.False(string.IsNullOrWhiteSpace(game.Path));
                Assert.False(InstalledSizeCache.IsAntiCheatPath(game.Path));
            }
            else
            {
                Assert.Null(game.SizeBytes);
            }
        }

        Assert.Contains(games, game => game.Installed && game.SizeBytes > 0);
        Assert.Contains(games, game => !game.Installed && game.SizeBytes is null);

        var valorant = Assert.Single(games, game => game.Id == "riot:valorant");
        var league = Assert.Single(games, game => game.Id == "riot:league_of_legends");
        var registryValorant = RiotInstallProbe.TryReadInstallSizeBytes("valorant");
        var registryLeague = RiotInstallProbe.TryReadInstallSizeBytes("league_of_legends");
        Assert.Equal(registryValorant, valorant.SizeBytes);
        Assert.Equal(registryLeague, league.SizeBytes);
    }
}
