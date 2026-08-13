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
    public void IsInstalledPresence_FolderWithoutFullyInstalledIsNotInstalled()
    {
        Assert.False(SteamStateFlags.IsInstalledPresence(true, "1"));
        Assert.False(SteamStateFlags.IsInstalledPresence(true, "65536"));
        Assert.True(SteamStateFlags.IsInstalledPresence(true, "4"));
        Assert.True(SteamStateFlags.IsInstalledPresence(true, "6"));
        Assert.False(SteamStateFlags.IsInstalledPresence(false, "4"));
        Assert.True(SteamStateFlags.IsInstalledPresence(true, null));
    }

    [Fact]
    public void IsUpdateAvailable_WhenUpdateRequiredBit_True()
    {
        Assert.True(SteamStateFlags.IsUpdateAvailable("6", installed: true));
    }

    [Fact]
    public void IsUpdateAvailable_PendingByteDelta_True()
    {
        Assert.True(SteamStateFlags.IsUpdateAvailable("4", installed: true, bytesToDownload: 8000, bytesDownloaded: 100));
        Assert.True(SteamStateFlags.IsUpdateAvailable("4", installed: true, bytesToDownload: 8000, bytesDownloaded: null));
    }

    [Fact]
    public void IsUpdateAvailable_FinishedByteCounters_False()
    {
        Assert.False(SteamStateFlags.IsUpdateAvailable("4", installed: true, bytesToDownload: 8000, bytesDownloaded: 8000));
    }

    [Fact]
    public void HasPendingTargetBuild_WhenInstalledBuildLagsTarget_True()
    {
        Assert.True(SteamStateFlags.HasPendingTargetBuild("100", "101"));
        Assert.True(SteamStateFlags.HasPendingTargetBuild(null, "101"));
        Assert.False(SteamStateFlags.HasPendingTargetBuild("100", "100"));
        Assert.False(SteamStateFlags.HasPendingTargetBuild("100", "0"));
        Assert.False(SteamStateFlags.HasPendingTargetBuild("100", null));
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
    public void IsQueuedForTargetedPromotion_ZeroByteQueuedPatch_TrueEvenAfterUpdateStartedBit()
    {
        // Deadlock-shaped queue: FullyInstalled|UpdateRequired, 36 MB pending, 0 downloaded.
        Assert.True(SteamStateFlags.IsQueuedForTargetedPromotion(
            "6",
            bytesToDownload: 38_358_400,
            bytesDownloaded: 0,
            buildId: "24659809",
            targetBuildId: "24702347"));

        // After steam://install Steam often sets UpdateStarted (512) before any
        // bytes move. That is still a scheduled row — OCR promotion must run.
        Assert.True(SteamStateFlags.IsQueuedForTargetedPromotion(
            "518",
            bytesToDownload: 38_358_400,
            bytesDownloaded: 0,
            buildId: "24659809",
            targetBuildId: "24702347"));
        Assert.True(SteamStateFlags.IsBusy("518", 38_358_400, 0));
    }

    [Fact]
    public void IsQueuedForTargetedPromotion_BytesAlreadyMoving_False()
    {
        Assert.False(SteamStateFlags.IsQueuedForTargetedPromotion(
            "65540",
            bytesToDownload: 38_358_400,
            bytesDownloaded: 1_024_000,
            buildId: "24659809",
            targetBuildId: "24702347"));
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
