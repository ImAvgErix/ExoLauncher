using ExoLauncher.Adapters;
using ExoLauncher.Models;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class OfficialInstalledLibraryTests
{
    [Fact]
    public void EaInstallDat_RequiresARealFolderAndContentId()
    {
        var json = """
            {"installInfos":[
              {"baseInstallPath":"C:\\Games\\Apex","contentId":"Origin.OFR.50.0000001","displayName":"Apex Legends"},
              {"baseInstallPath":"C:\\Games\\Missing","contentId":"Origin.OFR.50.0000002","displayName":"Ghost"}
            ]}
            """;

        var games = OfficialInstalledLibraries.ParseEaInstallDat(
            json, path => path.Equals(@"C:\Games\Apex", StringComparison.OrdinalIgnoreCase));

        var game = Assert.Single(games);
        Assert.Equal("Apex Legends", game.Title);
        Assert.Equal(StoreKind.Ea, game.Store);
        Assert.True(game.Installed);
        Assert.Equal("Origin.OFR.50.0000001", game.LaunchTarget);
        Assert.StartsWith("ea:", game.Id, StringComparison.Ordinal);
    }

    [Fact]
    public void UbisoftInstalls_SkipMissingFolders()
    {
        var records = new OfficialInstalledLibraries.UbisoftInstallRecord[]
        {
            new("1234", @"D:\Ubisoft\Assassins", "Assassin's Creed"),
            new("9999", @"D:\Ubisoft\Gone", "Gone"),
        };

        var games = OfficialInstalledLibraries.ParseUbisoftInstalls(
            records, path => path.Contains("Assassins", StringComparison.OrdinalIgnoreCase));

        var game = Assert.Single(games);
        Assert.Equal("Assassin's Creed", game.Title);
        Assert.Equal("1234", game.LaunchTarget);
        Assert.Equal(StoreKind.Ubisoft, game.Store);
    }

    [Fact]
    public void XboxGamesFolder_RequiresContentExecutable()
    {
        var root = Path.Combine(Path.GetTempPath(), "exo-xbox-" + Guid.NewGuid().ToString("N"));
        var content = Path.Combine(root, "Forza Horizon 5", "Content");
        Directory.CreateDirectory(content);
        var exe = Path.Combine(content, "ForzaHorizon5.exe");
        File.WriteAllText(exe, "MZ");
        File.WriteAllText(
            Path.Combine(content, "MicrosoftGame.config"),
            """<Game><ExecutableList><Executable Name="ForzaHorizon5.exe" /></ExecutableList></Game>""");
        Directory.CreateDirectory(Path.Combine(root, "EmptyTitle", "Content"));
        try
        {
            var games = OfficialInstalledLibraries.ScanXboxGamesFolders(
                [root], Directory.Exists, File.Exists);
            var game = Assert.Single(games);
            Assert.Equal("Forza Horizon 5", game.Title);
            Assert.Equal(StoreKind.Xbox, game.Store);
            Assert.Equal(exe, game.LaunchTarget);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void BattleNetUninstall_RequiresUidAndRealFolder()
    {
        var records = new OfficialInstalledLibraries.BattleNetInstallRecord[]
        {
            new("prometheus", @"C:\Games\Overwatch", "Overwatch 2"),
            new("wow", @"C:\Games\Missing", "World of Warcraft"),
        };

        var games = OfficialInstalledLibraries.ParseBattleNetInstalls(
            records, path => path.Equals(@"C:\Games\Overwatch", StringComparison.OrdinalIgnoreCase));

        var game = Assert.Single(games);
        Assert.Equal("Overwatch 2", game.Title);
        Assert.Equal(StoreKind.BattleNet, game.Store);
        Assert.Equal("prometheus", game.LaunchTarget);
        Assert.StartsWith("battlenet:", game.Id, StringComparison.Ordinal);
    }

    [Fact]
    public void AmazonFuel_RequiresExistingCommand()
    {
        var root = Path.Combine(Path.GetTempPath(), "exo-amazon-" + Guid.NewGuid().ToString("N"));
        var gameDir = Path.Combine(root, "abc-123");
        Directory.CreateDirectory(gameDir);
        var exe = Path.Combine(gameDir, "Game.exe");
        File.WriteAllText(exe, "MZ");
        File.WriteAllText(Path.Combine(gameDir, "fuel.json"), """
            {"schemaVersion":"0.1","label":"Hades","main":{"command":"Game.exe"}}
            """);
        Directory.CreateDirectory(Path.Combine(root, "empty"));
        try
        {
            var games = OfficialInstalledLibraries.ScanAmazonFuelFolders(
                [root], Directory.Exists, File.Exists, File.ReadAllText);
            var game = Assert.Single(games);
            Assert.Equal("Hades", game.Title);
            Assert.Equal(StoreKind.Amazon, game.Store);
            Assert.Equal(exe, game.LaunchTarget);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void RockstarInstall_SkipsLauncherFolderAndRequiresGameExe()
    {
        var root = Path.Combine(Path.GetTempPath(), "exo-rockstar-" + Guid.NewGuid().ToString("N"));
        var gta = Path.Combine(root, "Grand Theft Auto V");
        var launcher = Path.Combine(root, "Launcher");
        Directory.CreateDirectory(gta);
        Directory.CreateDirectory(launcher);
        var exe = Path.Combine(gta, "GTA5.exe");
        File.WriteAllText(exe, "MZ");
        File.WriteAllText(Path.Combine(launcher, "Launcher.exe"), "MZ");
        try
        {
            var games = OfficialInstalledLibraries.ParseRockstarInstalls(
                [
                    new("Grand Theft Auto V", gta),
                    new("Launcher", launcher),
                ],
                Directory.Exists,
                File.Exists);
            var game = Assert.Single(games);
            Assert.Equal("Grand Theft Auto V", game.Title);
            Assert.Equal(StoreKind.Rockstar, game.Store);
            Assert.Equal(exe, game.LaunchTarget);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void InstallAndUninstallProtocols_MatchTheOfficialClients()
    {
        Assert.Equal(
            "origin2://game/launch/?offerIds=Origin.OFR.50.0000001",
            OfficialInstalledLibraries.InstallProtocol(StoreKind.Ea, "Origin.OFR.50.0000001"));
        Assert.Equal(
            "uplay://install/1234",
            OfficialInstalledLibraries.InstallProtocol(StoreKind.Ubisoft, "1234"));
        Assert.Equal(
            "uplay://uninstall/1234",
            OfficialInstalledLibraries.UninstallProtocol(StoreKind.Ubisoft, "1234"));
        Assert.Equal(
            "battlenet://wow/",
            OfficialInstalledLibraries.InstallProtocol(StoreKind.BattleNet, "wow"));
        Assert.Null(OfficialInstalledLibraries.InstallProtocol(StoreKind.Xbox, "anything"));
        Assert.Equal(
            "ms-windows-store://pdp/?ProductId=9NBLGGH4R2R6",
            OfficialInstalledLibraries.InstallProtocol(StoreKind.Xbox, "9NBLGGH4R2R6"));
        Assert.Equal(
            "wgc://open/game/wot.eu.production",
            OfficialInstalledLibraries.InstallProtocol(StoreKind.Wargaming, "wot.eu.production"));
        Assert.Equal(
            "https://www.minecraft.net/get-minecraft",
            OfficialInstalledLibraries.InstallProtocol(StoreKind.Minecraft, "minecraft:java"));
        Assert.StartsWith(
            "ms-windows-store://pdp/?PFN=",
            OfficialInstalledLibraries.InstallProtocol(StoreKind.Minecraft, "minecraft:bedrock"));
        Assert.Equal(
            "ms-windows-store://pdp/?ProductId=9PMF91N3LZ3M",
            OfficialInstalledLibraries.InstallProtocol(StoreKind.Roblox, "9PMF91N3LZ3M"));
        Assert.Null(OfficialInstalledLibraries.InstallProtocol(StoreKind.Xbox, @"D:\XboxGames\Forza\Content\Forza.exe"));
        Assert.Null(OfficialInstalledLibraries.InstallProtocol(StoreKind.Itch, @"C:\Games\Celeste\Celeste.exe"));
    }

    [Fact]
    public void PathsRelated_AcceptsContentFolderUnderTheInstallRoot()
    {
        Assert.True(OfficialInstalledLibraries.PathsRelated(@"D:\XboxGames\Starfield", @"D:\XboxGames\Starfield\Content"));
        Assert.True(OfficialInstalledLibraries.PathsRelated(@"C:\Games\Apex", @"C:\Games\Apex"));
        Assert.False(OfficialInstalledLibraries.PathsRelated(@"C:\Games\Apex", @"C:\Games\FIFA"));
    }

    [Fact]
    public void SplitCommand_KeepsQuotedExeAndTrailingArgs()
    {
        var split = OfficialInstalledLibraries.SplitCommand(
            @"""C:\Program Files (x86)\Battle.net\Battle.net Uninstaller.exe"" --lang=enUS --uid=wow");
        Assert.Equal(@"C:\Program Files (x86)\Battle.net\Battle.net Uninstaller.exe", split.FileName);
        Assert.Equal("--lang=enUS --uid=wow", split.Arguments);
    }

    [Fact]
    public void EaInstallDat_SetsUpdateAvailableFromProvenLocalFlag()
    {
        var json = """
            {"installInfos":[
              {"baseInstallPath":"C:\\Games\\Apex","contentId":"Origin.OFR.50.0000001","displayName":"Apex Legends","updateAvailable":true},
              {"baseInstallPath":"C:\\Games\\FIFA","contentId":"Origin.OFR.50.0000003","displayName":"EA Sports FC","updateAvailable":false}
            ]}
            """;

        var games = OfficialInstalledLibraries.ParseEaInstallDat(
            json, path => path.Contains("Apex", StringComparison.OrdinalIgnoreCase)
                          || path.Contains("FIFA", StringComparison.OrdinalIgnoreCase));

        var apex = Assert.Single(games, g => g.Title == "Apex Legends");
        Assert.True(apex.UpdateAvailable);
        Assert.Equal("Update", apex.Status);
        var fifa = Assert.Single(games, g => g.Title == "EA Sports FC");
        Assert.False(fifa.UpdateAvailable);
        Assert.Equal("Ready", fifa.Status);
    }

    [Fact]
    public void UbisoftInstalls_CarryRegistryUpdateFlag()
    {
        var records = new OfficialInstalledLibraries.UbisoftInstallRecord[]
        {
            new("1234", @"D:\Ubisoft\Assassins", "Assassin's Creed", UpdateAvailable: true),
        };

        var game = Assert.Single(OfficialInstalledLibraries.ParseUbisoftInstalls(
            records, path => path.Contains("Assassins", StringComparison.OrdinalIgnoreCase)));
        Assert.True(game.UpdateAvailable);
        Assert.Equal("Update", game.Status);
    }

    [Fact]
    public void PlanUpdate_DoesNotNoOpWhenTheTitleIsAlreadyInstalled()
    {
        var game = new GameEntry
        {
            Id = "ea:apex",
            Title = "Apex Legends",
            Store = StoreKind.Ea,
            Installed = true,
            LaunchTarget = "Origin.OFR.50.0000001",
            Path = @"C:\Games\Apex",
        };

        var installed = OfficialInstalledLibraries.PlanUpdate(game, "EA app", stillInstalled: true);
        Assert.False(installed.UseInstallPath);
        Assert.StartsWith("origin2://", installed.Protocol);
        Assert.Contains("update", installed.Message, StringComparison.OrdinalIgnoreCase);

        var missing = OfficialInstalledLibraries.PlanUpdate(game, "EA app", stillInstalled: false);
        Assert.True(missing.UseInstallPath);
    }

    [Fact]
    public void UpdateProtocol_HandsOffKnownOfficialClientsAndLeavesXboxToTheApp()
    {
        Assert.Equal(
            "origin2://game/launch/?offerIds=Origin.OFR.50.0000001",
            OfficialInstalledLibraries.UpdateProtocol(StoreKind.Ea, "Origin.OFR.50.0000001"));
        Assert.Equal(
            "uplay://install/1234",
            OfficialInstalledLibraries.UpdateProtocol(StoreKind.Ubisoft, "1234"));
        Assert.Equal(
            "battlenet://wow/",
            OfficialInstalledLibraries.UpdateProtocol(StoreKind.BattleNet, "wow"));
        Assert.Null(OfficialInstalledLibraries.UpdateProtocol(StoreKind.Xbox, @"D:\XboxGames\Forza\Content\Forza.exe"));
        Assert.Null(OfficialInstalledLibraries.UpdateProtocol(StoreKind.Amazon, @"D:\Amazon\Hades\Game.exe"));
        Assert.Null(OfficialInstalledLibraries.UpdateProtocol(StoreKind.Rockstar, @"D:\Rockstar\GTA5\GTA5.exe"));
    }

    [Fact]
    public void OfficialAdapterUpdate_IsNotInstallAsyncNoOp()
    {
        var src = File.ReadAllText(FindOfficialAdapterSource());
        Assert.Contains(
            "OfficialInstalledLibraries.UpdateAsync(game, DisplayName, ct)",
            src,
            StringComparison.Ordinal);
        var updateIdx = src.IndexOf("public Task<InstallResult> UpdateAsync", StringComparison.Ordinal);
        Assert.True(updateIdx >= 0);
        var snippet = src.Substring(updateIdx, Math.Min(280, src.Length - updateIdx));
        Assert.Contains("OfficialInstalledLibraries.UpdateAsync", snippet, StringComparison.Ordinal);
        Assert.DoesNotContain("OfficialInstalledLibraries.InstallAsync", snippet, StringComparison.Ordinal);
    }

    private static string FindOfficialAdapterSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "ExoLauncher", "Adapters", "AgentPresentAdapters.cs");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("AgentPresentAdapters.cs");
    }
}
