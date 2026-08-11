using ExoLauncher.Adapters;
using Xunit;

namespace ExoLauncher.Tests;

public class SteamStateFlagsTests
{
    [Theory]
    [InlineData("4")]
    [InlineData("6")] // FullyInstalled | UpdateRequired
    public void IsFullyInstalled_TrueWhenBitSet(string flags)
    {
        Assert.True(SteamStateFlags.IsFullyInstalled(flags));
    }

    [Fact]
    public void IsUpdateAvailable_WhenUpdateRequiredBit_True()
    {
        Assert.True(SteamStateFlags.IsUpdateAvailable("6", installed: true));
    }

    [Fact]
    public void IsUpdateAvailable_FullyInstalledOnly_False()
    {
        Assert.False(SteamStateFlags.IsUpdateAvailable("4", installed: true));
    }

    [Fact]
    public void IsBusy_WithDownloadingBits_True()
    {
        // Downloading = 65536
        Assert.True(SteamStateFlags.IsBusy("65536", bytesToDownload: null, bytesDownloaded: null));
        // FullyInstalled | Downloading
        Assert.True(SteamStateFlags.IsBusy("65540", bytesToDownload: null, bytesDownloaded: null));
        // Byte counters mid-download
        Assert.True(SteamStateFlags.IsBusy("4", bytesToDownload: 1000, bytesDownloaded: 100));
    }

    [Fact]
    public void IsBusy_CompletedCountersWithReadyFlags_False()
    {
        // Steam keeps these final counters in appmanifest after the update has
        // completed. They must not hold Exo's progress button in Updating forever.
        Assert.False(SteamStateFlags.IsBusy("4", bytesToDownload: 1000, bytesDownloaded: 1000));
    }

    [Fact]
    public void SteamProcessMatch_RequiresASeparatorBoundedInstallPath()
    {
        var root = Path.Combine(Path.GetTempPath(), "exo-steam-game");

        Assert.True(SteamProcessPath.IsWithinInstall(
            root,
            Path.Combine(root, "bin", "game.exe")));
        Assert.False(SteamProcessPath.IsWithinInstall(
            root,
            root + "-other" + Path.DirectorySeparatorChar + "game.exe"));
        Assert.False(SteamProcessPath.IsWithinInstall(
            root,
            Path.Combine(Path.GetTempPath(), "other", "game.exe")));
    }

    [Fact]
    public void SteamLaunchProcess_RequiresNewNonHelperProcessUnderInstallPath()
    {
        var root = Path.Combine(Path.GetTempPath(), "exo-steam-game");
        var seenBeforeLaunch = new HashSet<int> { 101 };

        Assert.False(SteamProcessPath.IsEligibleNewGameProcess(
            processId: 101,
            processName: "Game",
            executablePath: Path.Combine(root, "Game.exe"),
            installPath: root,
            seenBeforeLaunch));
        Assert.False(SteamProcessPath.IsEligibleNewGameProcess(
            processId: 102,
            processName: "EasyAntiCheat",
            executablePath: Path.Combine(root, "EasyAntiCheat", "EasyAntiCheat.exe"),
            installPath: root,
            seenBeforeLaunch));
        Assert.False(SteamProcessPath.IsEligibleNewGameProcess(
            processId: 102,
            processName: "BEService",
            executablePath: Path.Combine(root, "BattlEye", "BEService.exe"),
            installPath: root,
            seenBeforeLaunch));
        Assert.False(SteamProcessPath.IsEligibleNewGameProcess(
            processId: 103,
            processName: "Game",
            executablePath: root + "-other" + Path.DirectorySeparatorChar + "Game.exe",
            installPath: root,
            seenBeforeLaunch));
        Assert.True(SteamProcessPath.IsEligibleNewGameProcess(
            processId: 104,
            processName: "Game",
            executablePath: Path.Combine(root, "bin", "Game.exe"),
            installPath: root,
            seenBeforeLaunch));
    }

    [Fact]
    public void SteamLaunchProcess_RecognizesAlreadyRunningGameButExcludesExistingHelper()
    {
        var root = Path.Combine(Path.GetTempPath(), "exo-steam-game");

        Assert.False(SteamProcessPath.IsEligibleGameProcess(
            processId: 201,
            processName: "EasyAntiCheat",
            executablePath: Path.Combine(root, "EasyAntiCheat", "EasyAntiCheat.exe"),
            installPath: root));
        Assert.True(SteamProcessPath.IsEligibleGameProcess(
            processId: 202,
            processName: "Game",
            executablePath: Path.Combine(root, "bin", "Game.exe"),
            installPath: root));
    }

    [Fact]
    public void SteamLaunchProcess_RejectsFreshCandidateThatDiesBeforeConfirmation()
    {
        Assert.Null(SteamProcessPath.ConfirmFreshGameProcess(
            candidatePid: 301,
            isStillAlive: _ => false));
        Assert.Equal(302, SteamProcessPath.ConfirmFreshGameProcess(
            candidatePid: 302,
            isStillAlive: _ => true));
    }

    [Fact]
    public void SteamLaunch_ChromeSuppressionOutlivesBoundedHandoff()
    {
        Assert.True(SteamProcessPath.LaunchHandoffTimeout >= TimeSpan.FromSeconds(40));
        Assert.True(SteamProcessPath.LaunchChromeSuppressionTimeout >
                    SteamProcessPath.LaunchHandoffTimeout + SteamProcessPath.LaunchProcessConfirmationWindow);
    }
}
