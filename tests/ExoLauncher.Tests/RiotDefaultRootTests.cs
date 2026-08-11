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
}
