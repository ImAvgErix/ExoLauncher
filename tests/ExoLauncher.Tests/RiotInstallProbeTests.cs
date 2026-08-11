using ExoLauncher.Adapters;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// Drives shipped RiotInstallProbe against real machine paths when present,
/// and against temp fixtures that mirror Riot layout.
/// </summary>
public class RiotInstallProbeTests : IDisposable
{
    private readonly string _fixtureRoot;

    public RiotInstallProbeTests()
    {
        _fixtureRoot = Path.Combine(Path.GetTempPath(), "exo-riot-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_fixtureRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_fixtureRoot, recursive: true); } catch { /* */ }
    }

    [Fact]
    public void LooksInstalled_Valorant_RequiresLiveExe()
    {
        var dir = Path.Combine(_fixtureRoot, "VALORANT");
        Directory.CreateDirectory(Path.Combine(dir, "live"));
        Assert.False(RiotInstallProbe.LooksInstalled("valorant", dir));

        File.WriteAllText(Path.Combine(dir, "live", "VALORANT.exe"), "MZ");
        Assert.True(RiotInstallProbe.LooksInstalled("valorant", dir));
    }

    [Fact]
    public void LooksInstalled_League_RequiresClientExe()
    {
        var dir = Path.Combine(_fixtureRoot, "League of Legends");
        Directory.CreateDirectory(dir);
        Assert.False(RiotInstallProbe.LooksInstalled("league_of_legends", dir));

        File.WriteAllText(Path.Combine(dir, "LeagueClient.exe"), "MZ");
        Assert.True(RiotInstallProbe.LooksInstalled("league_of_legends", dir));
    }

    [Fact]
    public void LooksInstalled_RejectsUnknownTinyExeTree()
    {
        var dir = Path.Combine(_fixtureRoot, "VALORANT");
        Directory.CreateDirectory(Path.Combine(dir, "live"));
        File.WriteAllBytes(Path.Combine(dir, "live", "bootstrap.exe"), new byte[12 * 1024 * 1024]);

        Assert.False(RiotInstallProbe.LooksInstalled("valorant", dir));
    }

    [Fact]
    public void FindInstalledProduct_FindsValorantUnderFixtureRoot()
    {
        var valo = Path.Combine(_fixtureRoot, "VALORANT", "live");
        Directory.CreateDirectory(valo);
        File.WriteAllText(Path.Combine(valo, "VALORANT.exe"), "MZ");

        var hit = RiotInstallProbe.FindInstalledProduct("valorant", new[] { _fixtureRoot });
        Assert.NotNull(hit);
        Assert.True(Directory.Exists(hit));
        Assert.Contains("VALORANT", hit, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FindInstalledProduct_ReturnsNull_WhenMissing()
    {
        Assert.Null(RiotInstallProbe.FindInstalledProduct("valorant", new[] { _fixtureRoot }));
        Assert.Null(RiotInstallProbe.FindInstalledProduct("league_of_legends", new[] { _fixtureRoot }));
    }

    [Fact]
    public void FindRiotClientServices_FindsUnderFixtureRoot()
    {
        var client = Path.Combine(_fixtureRoot, "Riot Client");
        Directory.CreateDirectory(client);
        var exe = Path.Combine(client, "RiotClientServices.exe");
        File.WriteAllText(exe, "MZ");

        var hit = RiotInstallProbe.FindRiotClientServices(new[] { _fixtureRoot });
        Assert.Equal(exe, hit);
    }

    [Fact]
    public void Machine_CRiotGames_DetectsValorantAndLeague_WhenPresent()
    {
        // Real machine path used by this install (not Program Files).
        const string machineRoot = @"C:\Riot Games";
        if (!Directory.Exists(machineRoot))
        {
            // Skip-style: still assert probe is honest on missing root.
            Assert.Null(RiotInstallProbe.FindInstalledProduct("valorant", new[] { machineRoot }));
            return;
        }

        var valo = RiotInstallProbe.FindInstalledProduct("valorant", new[] { machineRoot });
        var league = RiotInstallProbe.FindInstalledProduct("league_of_legends", new[] { machineRoot });
        var rcs = RiotInstallProbe.FindRiotClientServices(new[] { machineRoot });

        // This workspace has both products + client under C:\Riot Games.
        Assert.True(File.Exists(Path.Combine(machineRoot, "VALORANT", "live", "VALORANT.exe")),
            "Machine expected VALORANT.exe for probe verification");
        Assert.NotNull(valo);
        Assert.True(RiotInstallProbe.IsProductInstalled("valorant", new[] { machineRoot }));

        Assert.True(File.Exists(Path.Combine(machineRoot, "League of Legends", "LeagueClient.exe")));
        Assert.NotNull(league);
        Assert.True(RiotInstallProbe.IsProductInstalled("league_of_legends", new[] { machineRoot }));

        Assert.NotNull(rcs);
        Assert.True(File.Exists(rcs));
    }

    [Fact]
    public void GameEntry_PrimaryAction_Play_WhenInstalled()
    {
        var installed = new ExoLauncher.Models.GameEntry
        {
            Id = "riot:valorant",
            Title = "VALORANT",
            Store = ExoLauncher.Models.StoreKind.Riot,
            Installed = true,
            Owned = true,
            CanInstall = false,
        };
        Assert.Equal("play", installed.PrimaryAction);

        var missing = new ExoLauncher.Models.GameEntry
        {
            Id = "riot:valorant",
            Title = "VALORANT",
            Store = ExoLauncher.Models.StoreKind.Riot,
            Installed = false,
            Owned = true,
            CanInstall = true,
        };
        Assert.Equal("install", missing.PrimaryAction);
    }
}
