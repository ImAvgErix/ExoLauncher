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
        Assert.Equal(
            ["launch", "Control", "--skip-version-check"],
            LegendaryCli.LaunchArgs("Control"));
        Assert.Equal(
            ["launch", "Control", "--skip-version-check", "--", "-dx11"],
            LegendaryCli.LaunchArgs("Control", "-dx11"));
    }

    [Fact]
    public void Legendary_RepairAndImportArgs_StayOfficial()
    {
        Assert.Equal(["install", "Control", "-y", "--repair"], LegendaryCli.RepairArgs("Control"));
        Assert.Equal(["verify", "Control"], LegendaryCli.VerifyArgs("Control"));
        Assert.Equal(
            ["import", "Control", @"D:\Games\Control"],
            LegendaryCli.ImportArgs("Control", @"D:\Games\Control"));
        Assert.Equal(["egl-sync", "--one-shot", "--import-only"], LegendaryCli.EglImportOnlyArgs());
        Assert.DoesNotContain("--import", LegendaryCli.AuthArgs());
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
    public void Gogdl_AuthCodeArgs_UseExplicitConfigBeforeCommand()
    {
        var args = GogdlCli.AuthCodeArgs(
            @"C:\Users\Test User\AppData\Local\ExoLauncher\gogdl\credentials.json",
            "opaque/code+value");

        Assert.Equal(
        [
            "--auth-config-path",
            @"C:\Users\Test User\AppData\Local\ExoLauncher\gogdl\credentials.json",
            "auth",
            "--code",
            "opaque/code+value",
        ], args);
    }

    [Fact]
    public void Gogdl_CallbackParser_AcceptsOnlyTrustedGogRedirect()
    {
        Assert.True(GogdlCli.TryExtractAuthorizationCode(
            "https://embed.gog.com/on_login_success?origin=client&code=abc%2F123%2Bxyz",
            out var code));
        Assert.Equal("abc/123+xyz", code);

        Assert.False(GogdlCli.TryExtractAuthorizationCode(
            "https://embed.gog.com.evil.example/on_login_success?code=stolen",
            out _));
        Assert.False(GogdlCli.TryExtractAuthorizationCode(
            "https://embed.gog.com/on_login_success?origin=client",
            out _));
        Assert.False(GogdlCli.TryExtractAuthorizationCode(
            "https://embed.gog.com/on_login_success?origin=wrong&code=stolen",
            out _));
        Assert.False(GogdlCli.TryExtractAuthorizationCode(
            "http://embed.gog.com/on_login_success?origin=client&code=stolen",
            out _));
        Assert.False(GogdlCli.TryExtractAuthorizationCode(
            "https://embed.gog.com:444/on_login_success?origin=client&code=stolen",
            out _));
    }

    [Theory]
    [InlineData("null", false)]
    [InlineData("{\"error\":true}", false)]
    [InlineData("{\"access_token\":\"a\",\"refresh_token\":\"r\"}", false)]
    [InlineData("{\"access_token\":\"a\",\"refresh_token\":\"r\",\"user_id\":\"u\"}", true)]
    public void Gogdl_CredentialParser_RequiresCompleteSuccessfulPayload(string json, bool expected)
    {
        Assert.Equal(expected, GogdlCli.HasAuthenticatedCredentials(json));
    }

    [Fact]
    public void Gogdl_LaunchArgs_UseRequiredPlatformAndPositionalPath()
    {
        Assert.Equal(
        [
            "launch",
            @"C:\Games\GOG Library\Celeste",
            "1423049311",
            "--platform",
            "windows",
        ], GogdlCli.LaunchArgs("1423049311", @"C:\Games\GOG Library\Celeste"));
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
        Assert.Equal("steam://store/570", SteamProtocol.StoreUri("570"));
    }

    [Theory]
    [InlineData("570", true)]
    [InlineData("252950", true)]
    [InlineData("0", false)]
    [InlineData("570?applaunch=730", false)]
    [InlineData(" 570", false)]
    [InlineData("12345678901", false)]
    [InlineData("not-an-app-id", false)]
    public void Steam_AppIdValidation_RequiresPositiveAsciiDecimalId(string appId, bool expected)
    {
        Assert.Equal(expected, SteamProtocol.IsValidAppId(appId));
    }

    [Fact]
    public void Steam_ParseAppManifest_RejectsNonNumericAppId()
    {
        const string acf = """
            "AppState"
            {
                "appid" "570?applaunch=730"
                "name" "Malformed title"
            }
            """;

        Assert.False(SteamProtocol.TryParseAppManifest(acf, out _, out _, out _, out _));
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

    [Fact]
    public void Legendary_ListOwnedArgs_UsesListJson()
    {
        Assert.Equal(["list", "--json"], LegendaryCli.ListOwnedArgs());
    }

    [Fact]
    public void Legendary_AuthArgs_UseNormalInteractiveFlowWithoutImport()
    {
        Assert.Equal(["auth"], LegendaryCli.AuthArgs());
    }

    [Theory]
    [InlineData(0, "[]", true)]
    [InlineData(0, "{\"games\":[]}", true)]
    [InlineData(1, "[]", false)]
    [InlineData(0, "", false)]
    [InlineData(0, "null", false)]
    [InlineData(0, "{}", false)]
    [InlineData(0, "{\"error\":\"authentication required\"}", false)]
    [InlineData(0, "not json", false)]
    public void Legendary_AuthValidation_RequiresSuccessfulLibraryJson(
        int exitCode,
        string stdout,
        bool expected)
    {
        Assert.Equal(expected, LegendaryCli.IsAuthenticatedLibraryResponse(exitCode, stdout));
    }

    [Fact]
    public void Legendary_ParseAndMerge_OwnedNotInstalled_StaysInstallable()
    {
        const string ownedJson = """
            [
              { "app_name": "Control", "title": "Control" },
              { "app_name": "Hades", "title": "Hades" }
            ]
            """;
        const string installedJson = """
            [
              { "app_name": "Hades", "title": "Hades", "install_path": "C:/Games/Hades", "install_size": 100 }
            ]
            """;

        var owned = LegendaryCli.ParseLibraryJson(ownedJson, forceInstalled: false);
        var installed = LegendaryCli.ParseLibraryJson(installedJson, forceInstalled: true);
        var merged = LegendaryCli.MergeOwnedAndInstalled(owned, installed);

        Assert.Equal(2, merged.Count);
        var control = Assert.Single(merged, r => r.AppName == "Control");
        Assert.False(control.Installed);
        var hades = Assert.Single(merged, r => r.AppName == "Hades");
        Assert.True(hades.Installed);
        Assert.Equal("C:/Games/Hades", hades.InstallPath);
    }

    [Fact]
    public void Legendary_ParseLibrary_CarriesCategoriesAndOfficialTallKeyArt()
    {
        const string json = """
            [{
              "app_name": "Fortnite",
              "title": "Fortnite",
              "metadata": { "categories": ["games", "applications"] },
              "keyImages": [{
                "type": "DieselGameBoxTall",
                "url": "https://cdn2.unrealengine.com/fortnite-1200x1600.jpg"
              }]
            }]
            """;

        var row = Assert.Single(LegendaryCli.ParseLibraryJson(json, forceInstalled: false));

        Assert.Equal(["games", "applications"], row.Categories);
        Assert.Equal("https://cdn2.unrealengine.com/fortnite-1200x1600.jpg", row.CoverUrl);
    }

    [Fact]
    public void Gogdl_ParseOwnedLibrary_AndMerge()
    {
        const string owned = """
            { "games": [
              { "id": "1207659012", "title": "Disco Elysium" },
              { "id": "1423049311", "title": "Celeste" }
            ]}
            """;
        const string installed = """
            [
              { "id": "1423049311", "title": "Celeste", "path": "C:\\\\GOG\\\\Celeste", "installed": true }
            ]
            """;

        var o = GogdlCli.ParseOwnedLibraryJson(owned);
        var i = GogdlCli.ParseOwnedLibraryJson(installed);
        var merged = GogdlCli.MergeOwnedAndInstalled(o, i);

        Assert.Equal(2, merged.Count);
        var disco = Assert.Single(merged, g => g.Id == "1207659012");
        Assert.False(disco.Installed);
        var celeste = Assert.Single(merged, g => g.Id == "1423049311");
        Assert.True(celeste.Installed);
    }

    [Fact]
    public void Gogdl_HeroicLibraryCachePath_UsesCurrentRoamingStoreCacheLocation()
    {
        var path = GogdlCli.HeroicLibraryCachePath(@"C:\Users\Player One\AppData\Roaming");

        Assert.Equal(
            @"C:\Users\Player One\AppData\Roaming\heroic\store_cache\gog_library.json",
            path);
    }

    [Fact]
    public void Gogdl_ParseOwnedLibrary_AcceptsCurrentHeroicStoreCacheShape()
    {
        const string heroicCache = """
            {
              "games": [
                {
                  "app_name": "1423049311",
                  "title": "Cyberpunk 2077",
                  "runner": "gog",
                  "is_installed": false,
                  "install": {}
                }
              ]
            }
            """;

        var games = GogdlCli.ParseOwnedLibraryJson(heroicCache);

        var game = Assert.Single(games);
        Assert.Equal("1423049311", game.Id);
        Assert.Equal("Cyberpunk 2077", game.Title);
        Assert.False(game.Installed);
        Assert.Null(game.InstallPath);
    }

    [Theory]
    [InlineData("{\"games\":{}}")]
    [InlineData("{\"games\":[null,{}]}")]
    public void Gogdl_ParseOwnedLibrary_GracefullySkipsMalformedHeroicCacheRows(string heroicCache)
    {
        Assert.Empty(GogdlCli.ParseOwnedLibraryJson(heroicCache));
    }
}
