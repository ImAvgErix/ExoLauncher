using ExoLauncher.Adapters.Cli;
using ExoLauncher.Models;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// Drives shipped CLI helpers — argv builders and progress parsers.
/// No network; pure unit coverage of real code under Adapters/Cli.
/// </summary>
public class CliHelperTests
{
    [Fact]
    public void Legendary_InstallArgs_IncludeAppAndYes()
    {
        var args = LegendaryCli.InstallArgs("Control", @"C:\Games\Epic");
        Assert.Equal(["install", "Control", "-y", "--base-path", @"C:\Games\Epic"], args);
    }

    [Fact]
    public void Legendary_UpdateArgs_UseUpdateOnly()
    {
        var args = LegendaryCli.UpdateArgs("Control");
        Assert.Contains("--update-only", args);
        Assert.Equal("install", args[0]);
    }

    [Fact]
    public void Legendary_LaunchArgs_AreCorrect()
    {
        Assert.Equal(["launch", "Control"], LegendaryCli.LaunchArgs("Control"));
    }

    [Fact]
    public void Legendary_ParseProgress_PercentAndSpeed()
    {
        var line = "[DLManager] INFO: = Progress: 45.23%, Running for 00:02:15, ETA: 00:03:00";
        Assert.True(LegendaryCli.TryParseProgressLine(line, out var pct, out _, out _));
        Assert.NotNull(pct);
        Assert.InRange(pct!.Value, 45.2, 45.3);

        var speedLine = "[DLManager] INFO:  - Download\t- 12.34 MiB/s (raw)";
        Assert.True(LegendaryCli.TryParseProgressLine(speedLine, out _, out var bps, out _));
        Assert.NotNull(bps);
        Assert.True(bps > 12 * 1024 * 1024);
    }

    [Fact]
    public void Legendary_ToProgress_MapsGameId()
    {
        var p = LegendaryCli.ToProgress("epic:Control", "Progress: 10%");
        Assert.Equal("epic:Control", p.GameId);
        Assert.Equal(InstallPhase.Downloading, p.Phase);
        Assert.Equal(10, p.Percent);
    }

    [Fact]
    public void Gogdl_DownloadArgs_IncludePlatformAndPath()
    {
        var args = GogdlCli.DownloadArgs("1234567890", @"C:\Games\GOG\title");
        Assert.Equal("download", args[0]);
        Assert.Contains("1234567890", args);
        Assert.Contains("--path", args);
        Assert.Contains(@"C:\Games\GOG\title", args);
    }

    [Fact]
    public void Gogdl_ParseProgress_BracketPercent()
    {
        Assert.True(GogdlCli.TryParseProgressLine("[12.5%] downloading", out var pct, out _, out _));
        Assert.Equal(12.5, pct);
    }

    [Fact]
    public void Riot_LaunchArgs_UseOfficialFlags()
    {
        var args = RiotCli.LaunchArgs("valorant");
        Assert.Contains("--launch-product=valorant", args);
        Assert.Contains("--launch-patchline=live", args);
    }

    [Fact]
    public void Riot_UninstallArgs_UseOfficialFlags()
    {
        var args = RiotCli.UninstallArgs("league_of_legends");
        Assert.Contains("--uninstall-product=league_of_legends", args);
        Assert.Contains("--uninstall-patchline=live", args);
    }

    [Fact]
    public void Riot_BootstrapArgs_SkipToInstall()
    {
        Assert.Equal("--skip-to-install", RiotCli.BootstrapInstallArgs());
    }

    [Fact]
    public void Riot_ProtectedProcesses_IncludeVanguard()
    {
        Assert.True(RiotCli.IsProtectedProcess("vgk"));
        Assert.True(RiotCli.IsProtectedProcess("vgc"));
        Assert.False(RiotCli.IsProtectedProcess("RiotClientUx"));
    }

    [Fact]
    public void Riot_FixedCatalog_ContainsValorantAndLol()
    {
        Assert.Contains(RiotCli.FixedCatalog, c => c.ProductId == "valorant");
        Assert.Contains(RiotCli.FixedCatalog, c => c.ProductId == "league_of_legends");
        Assert.True(RiotCli.IsKnownProduct("bacon"));
    }

    [Fact]
    public void Steam_Protocol_Uris()
    {
        Assert.Equal("steam://rungameid/570", SteamProtocol.RunGameUri("570"));
        Assert.Equal("steam://install/570", SteamProtocol.InstallUri("570"));
    }

    [Fact]
    public void Steam_ParseAppManifest_RealShape()
    {
        const string acf = """
            "AppState"
            {
            	"appid"		"570"
            	"name"		"Dota 2"
            	"installdir"		"dota 2 beta"
            	"SizeOnDisk"		"1234567890"
            	"StateFlags"		"4"
            }
            """;

        Assert.True(SteamProtocol.TryParseAppManifest(acf, out var appId, out var name, out var dir, out var size));
        Assert.Equal("570", appId);
        Assert.Equal("Dota 2", name);
        Assert.Equal("dota 2 beta", dir);
        Assert.Equal(1234567890, size);
    }
}
