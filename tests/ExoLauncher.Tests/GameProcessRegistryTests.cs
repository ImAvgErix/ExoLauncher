using ExoLauncher.Models;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class GameProcessRegistryTests
{
    [Theory]
    [InlineData("steam")]
    [InlineData("EpicGamesLauncher")]
    [InlineData("GameOverlayUI")]
    [InlineData("RiotClientServices")]
    [InlineData("LeagueClient")]
    [InlineData("vgc")]
    [InlineData("vgk")]
    [InlineData("EasyAntiCheat_EOS")]
    [InlineData("start_protected_game")]
    [InlineData("UnityCrashHandler64")]
    [InlineData("updater")]
    public void ReservedProcesses_AreNeverEligibleForStop(string processName) =>
        Assert.True(GameProcessRegistry.IsReservedProcessName(processName));

    [Fact]
    public void OrdinaryGameExecutableName_IsNotReserved() =>
        Assert.False(GameProcessRegistry.IsReservedProcessName("Game-Win64-Shipping"));

    [Fact]
    public void StopEligibilityRequiresAnExecutableInsideTheExactInstallRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "exo-stop", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var game = Game(root, StoreKind.Steam, "123");

            Assert.True(GameProcessRegistry.IsEligibleExecutableForStop(
                game, "Game-Win64-Shipping", Path.Combine(root, "bin", "Game-Win64-Shipping.exe")));
            Assert.False(GameProcessRegistry.IsEligibleExecutableForStop(
                game, "Game-Win64-Shipping", root + "-other\\Game-Win64-Shipping.exe"));
            Assert.False(GameProcessRegistry.IsEligibleExecutableForStop(
                game, "EasyAntiCheat_EOS", Path.Combine(root, "EasyAntiCheat_EOS.exe")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData(StoreKind.Steam)]
    [InlineData(StoreKind.Epic)]
    [InlineData(StoreKind.Gog)]
    [InlineData(StoreKind.Riot)]
    public void SupportedStoreStopsShareTheExactInstallRootBoundary(StoreKind store)
    {
        var root = Path.Combine(Path.GetTempPath(), "exo-stop-store", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var game = Game(root, store, store == StoreKind.Riot ? "valorant" : "catalog-id");
            var processName = store == StoreKind.Riot
                ? "VALORANT-Win64-Shipping"
                : "Game-Win64-Shipping";
            var executable = Path.Combine(root, "bin", processName + ".exe");

            Assert.True(GameProcessRegistry.IsEligibleExecutableForStop(
                game, processName, executable));
            Assert.False(GameProcessRegistry.IsEligibleExecutableForStop(
                game, processName, root + "-other\\" + processName + ".exe"));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void RiotStopAllowsTheGameButNeverThePersistentLeagueClient()
    {
        var root = Path.Combine(Path.GetTempPath(), "exo-stop-riot", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var game = Game(root, StoreKind.Riot, "league_of_legends");

            Assert.True(GameProcessRegistry.IsEligibleExecutableForStop(
                game, "League of Legends", Path.Combine(root, "Game", "League of Legends.exe")));
            Assert.False(GameProcessRegistry.IsEligibleExecutableForStop(
                game, "LeagueClient", Path.Combine(root, "LeagueClient.exe")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void LocalStopAllowsOnlyTheRegisteredExecutable_NotAnyInRootProcess()
    {
        var root = Path.Combine(Path.GetTempPath(), "exo-stop-local", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var target = Path.Combine(root, "PortableGame.exe");
            var game = Game(root, StoreKind.Local, target);

            Assert.True(GameProcessRegistry.IsEligibleExecutableForStop(
                game, "PortableGame", target));
            Assert.False(GameProcessRegistry.IsEligibleExecutableForStop(
                game, "OtherProgram", Path.Combine(root, "tools", "OtherProgram.exe")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void LocalStopRejectsSystemDirectoryEvenWhenTheTargetMatches()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var target = Path.Combine(windows, "notepad.exe");
        var game = Game(windows, StoreKind.Local, target);

        Assert.False(GameProcessRegistry.IsEligibleExecutableForStop(game, "notepad", target));
    }

    [Fact]
    public void LocalStopRejectsVolumeRootEvenWhenTheTargetMatches()
    {
        var root = Path.GetPathRoot(Environment.SystemDirectory)!;
        var target = Path.Combine(root, "portable-game.exe");
        var game = Game(root, StoreKind.Local, target);

        Assert.False(GameProcessRegistry.IsEligibleExecutableForStop(game, "portable-game", target));
    }

    [Fact]
    public void StopImplementationNeverExpandsToAnUnverifiedProcessTree()
    {
        var source = File.ReadAllText(FindRepoFile(
            Path.Combine("ExoLauncher", "Services", "GameProcessRegistry.cs")));

        Assert.Contains("Kill(entireProcessTree: false)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Kill(entireProcessTree: true)", source, StringComparison.Ordinal);
        Assert.Contains("StartedUtcTicks == expected.StartedUtcTicks", source, StringComparison.Ordinal);
    }

    private static GameEntry Game(string root, StoreKind store, string launchTarget) => new()
    {
        Id = $"{store.ToString().ToLowerInvariant()}:test",
        Title = "Test game",
        Store = store,
        Installed = true,
        Path = root,
        LaunchTarget = launchTarget,
    };

    private static string FindRepoFile(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relative);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new FileNotFoundException(relative);
    }
}
