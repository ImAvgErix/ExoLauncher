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
}
