using ExoLauncher.Adapters;
using ExoLauncher.Adapters.Cli;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// Hits the real on-disk store data on this PC. Skips when a store is not installed.
/// SteamAdapter is stubbed in this test project, so Steam is scanned via appmanifests.
/// </summary>
public sealed class LiveLibraryScanTests
{
    [Fact]
    public void Steam_AppManifests_IncludeInstalledCounterStrike()
    {
        var steam = Microsoft.Win32.Registry.GetValue(
            @"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        if (string.IsNullOrWhiteSpace(steam)) return;
        var steamApps = Path.Combine(steam.Replace('/', Path.DirectorySeparatorChar), "steamapps");
        var acf = Path.Combine(steamApps, "appmanifest_730.acf");
        if (!File.Exists(acf)) return;

        Assert.True(SteamProtocol.TryParseAppManifest(
            File.ReadAllText(acf), out var appId, out var name, out var installDir, out _));
        Assert.Equal("730", appId);
        Assert.Contains("Counter-Strike", name, StringComparison.OrdinalIgnoreCase);
        var path = Path.Combine(steamApps, "common", installDir ?? "");
        Assert.True(Directory.Exists(path));
    }

    [Fact]
    public void Epic_NativeScan_FindsRocketLeagueWhenEglManifestExists()
    {
        var item = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(item) || !Directory.EnumerateFiles(item, "*.item").Any())
            return;

        var games = EpicAdapter.ReadNativeInstalledLibrary(hasLegendary: false);
        Assert.Contains(games, g =>
            string.Equals(g.LaunchTarget, "Sugar", StringComparison.OrdinalIgnoreCase) &&
            g.Installed);
    }

    [Fact]
    public async Task Riot_MarksInstalledValorantAndLeague()
    {
        if (!Directory.Exists(@"C:\Riot Games")) return;

        var games = await new RiotAdapter().GetLibraryAsync();
        var valorant = Assert.Single(games, g => g.Id == "riot:valorant");
        var league = Assert.Single(games, g => g.Id == "riot:league_of_legends");
        Assert.True(valorant.Installed);
        Assert.True(league.Installed);
        Assert.False(string.IsNullOrWhiteSpace(valorant.Path));
        Assert.False(string.IsNullOrWhiteSpace(league.Path));
    }
}
