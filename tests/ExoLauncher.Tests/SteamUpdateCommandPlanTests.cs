using ExoLauncher.Adapters.Cli;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class SteamUpdateCommandPlanTests
{
    [Fact]
    public void QueuedZeroByteUpdate_TargetsOnlyTheSelectedApp()
    {
        var plan = SteamUpdateCommandPlan.BuildNudge("1422450");

        var request = Assert.Single(plan);
        Assert.Equal(SteamUpdateCommandPurpose.RequestInstallOrUpdate, request.Purpose);
        Assert.Equal(
            ["-silent", "-nofriendsui", "-nochatui", "steam://install/1422450"],
            request.Arguments);
        Assert.DoesNotContain("4704690", string.Join(' ', request.Arguments), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1620730", "Hell is Us")]
    [InlineData("1817070", "Marvel's Spider-Man Remastered")]
    public void SteamCatalogInstall_RequestKeepsTheExactPurchasedTitleAppId(string appId, string _)
    {
        var request = Assert.Single(SteamUpdateCommandPlan.BuildNudge(appId));

        Assert.Equal($"steam://install/{appId}", request.Arguments[^1]);
        Assert.DoesNotContain("steam://install/0", request.Arguments);
    }

    [Fact]
    public void ActiveUpdate_DoesNotNavigateAwayFromTheCurrentSteamContext()
    {
        var plan = SteamUpdateCommandPlan.BuildNudge("1422450");

        var request = Assert.Single(plan);
        Assert.Equal(SteamUpdateCommandPurpose.RequestInstallOrUpdate, request.Purpose);
        Assert.DoesNotContain(SteamProtocol.DownloadsUri(), request.Arguments);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1422450 --oops")]
    [InlineData("deadlock")]
    public void NudgePlan_RejectsAnythingExceptANumericAppId(string appId)
    {
        Assert.Throws<ArgumentException>(() =>
            SteamUpdateCommandPlan.BuildNudge(appId));
    }

    [Fact]
    public void UpdatePath_NeverUsesAForegroundSteamInvocation()
    {
        var adapter = ReadRepoFile("ExoLauncher", "Adapters", "SteamAdapter.cs");
        var update = Slice(
            adapter,
            "public async Task<InstallResult> UpdateAsync",
            "private sealed record AppManifestSnapshot");
        var nudge = Slice(
            adapter,
            "private static void NudgeSteamUpdate",
            "public async Task<InstallResult> UpdateAsync");
        var plan = ReadRepoFile(
            "ExoLauncher", "Adapters", "Cli", "SteamUpdateCommandPlan.cs");

        Assert.DoesNotContain("Start without -silent", update, StringComparison.Ordinal);
        Assert.DoesNotContain("StartProtocol", update + nudge, StringComparison.Ordinal);
        Assert.Contains("ProcessHelper.StartHidden", update + nudge, StringComparison.Ordinal);
        Assert.Contains("SteamUpdateCommandPlan", update + nudge, StringComparison.Ordinal);
        Assert.Contains("-silent", plan, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdatePath_CommandsTheRunningSteamClientWithoutOcr()
    {
        var adapter = ReadRepoFile("ExoLauncher", "Adapters", "SteamAdapter.cs");
        var update = Slice(
            adapter,
            "public async Task<InstallResult> UpdateAsync",
            "private sealed record AppManifestSnapshot");
        var nudge = Slice(
            adapter,
            "private static void NudgeSteamUpdate",
            "public async Task<InstallResult> UpdateAsync");
        var plan = ReadRepoFile(
            "ExoLauncher", "Adapters", "Cli", "SteamUpdateCommandPlan.cs");
        var ipc = ReadRepoFile("ExoLauncher", "Adapters", "SteamClientIpc.cs");

        Assert.Contains("SteamUpdateCommandPlan.BuildNudge", nudge, StringComparison.Ordinal);
        Assert.DoesNotContain("DownloadsUri", plan, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenDownloads", plan, StringComparison.Ordinal);
        Assert.DoesNotContain("SteamDownloadNowAutomator", update, StringComparison.Ordinal);
        Assert.DoesNotContain("SteamInstallDialogAutomator", update, StringComparison.Ordinal);
        Assert.DoesNotContain("SteamTargetedQueuePromotionAutomator", update, StringComparison.Ordinal);
        Assert.DoesNotContain("PromoteAsync", update, StringComparison.Ordinal);
        Assert.Contains("StoreWindowHider.ForSteam", update, StringComparison.Ordinal);
        Assert.Contains("CommandSteamIpcAsync(\"update\"", update, StringComparison.Ordinal);
        Assert.Contains("SteamClientIpc.Command", adapter, StringComparison.Ordinal);
        Assert.Contains("initial.Name", update, StringComparison.Ordinal);
        Assert.Contains("ExoLauncher.SteamIpc.exe", ipc, StringComparison.Ordinal);
        Assert.Contains("steam-ipc", ipc, StringComparison.Ordinal);
        Assert.DoesNotContain("SetCursorPos", ipc, StringComparison.Ordinal);
        Assert.DoesNotContain("SendInput", ipc, StringComparison.Ordinal);
        Assert.DoesNotContain("DepotDownloader", ipc, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdatePath_KeepsPollingReportingAndRenudgingUntilSteamShowsRealProgress()
    {
        var adapter = ReadRepoFile("ExoLauncher", "Adapters", "SteamAdapter.cs");
        var update = Slice(
            adapter,
            "public async Task<InstallResult> UpdateAsync",
            "private sealed record AppManifestSnapshot");

        Assert.Contains("while (!ct.IsCancellationRequested)", update, StringComparison.Ordinal);
        Assert.Contains("ReadAppManifestSnapshot(appId)", update, StringComparison.Ordinal);
        Assert.True(
            update.Split("Report(game.Id, progress", StringSplitOptions.None).Length - 1 >= 5,
            "The update watch must continue publishing phase/progress changes.");
        Assert.True(
            update.Split("NudgeSteamUpdate(steamExe, game, appId", StringSplitOptions.None).Length - 1 >= 2,
            "The update watch must re-nudge Steam when the manifest remains queued.");
        Assert.True(
            update.Split("CommandSteamIpcAsync(\"update\"", StringSplitOptions.None).Length - 1 >= 2,
            "The update watch must retry Steam IPC while the selected app stays queued.");
        Assert.Contains(
            "|| sawTargetManifestChange || !neededAtStart;",
            update,
            StringComparison.Ordinal);
        Assert.Contains("SnapshotNeedsUpdate(snap)", update, StringComparison.Ordinal);
        Assert.Contains("HasPendingTargetBuild", adapter, StringComparison.Ordinal);
        Assert.Contains("TargetBuildID", adapter, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdatePath_RetriesIpcWhenQueuedAndFailsHonestlyIfBytesNeverMove()
    {
        var adapter = ReadRepoFile("ExoLauncher", "Adapters", "SteamAdapter.cs");
        var update = Slice(
            adapter,
            "public async Task<InstallResult> UpdateAsync",
            "private sealed record AppManifestSnapshot");

        Assert.Contains("CommandSteamIpcAsync(\"update\"", update, StringComparison.Ordinal);
        Assert.Contains("ReleaseScheduledSteamUpdate", update, StringComparison.Ordinal);
        Assert.Contains("TryClearScheduledAutoUpdate", adapter, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Open Steam Downloads and start that game once.",
            update,
            StringComparison.Ordinal);
        Assert.DoesNotContain("SteamTargetedQueuePromotionAutomator", update, StringComparison.Ordinal);
        Assert.Contains("Steam did not start this game's update.", update, StringComparison.Ordinal);
        Assert.Contains(
            "SteamStateFlags.IsQueuedForTargetedPromotion",
            adapter,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SteamIpcHelper_UsesKnownAppManagerSlotsAndNeverClicks()
    {
        var helper = ReadRepoFile("ExoLauncher.SteamIpc", "Program.cs");
        var project = ReadRepoFile("ExoLauncher.SteamIpc", "ExoLauncher.SteamIpc.csproj");

        Assert.Contains("UseWinUI>false", project, StringComparison.Ordinal);
        Assert.Contains("CLIENTAPPMANAGER_INTERFACE_VERSION001", helper, StringComparison.Ordinal);
        Assert.Contains("EngineGetIClientAppManager = 43", helper, StringComparison.Ordinal);
        Assert.Contains("GetClientAppManagerFn(IntPtr self, int user, int pipe, string version)", helper, StringComparison.Ordinal);
        Assert.Contains("AppInstallApp = 0", helper, StringComparison.Ordinal);
        Assert.Contains("AppUninstallApp = 1", helper, StringComparison.Ordinal);
        Assert.Contains("AppGetAppInstallState = 4", helper, StringComparison.Ordinal);
        Assert.Contains("AppErrorNotInstalled = 18", helper, StringComparison.Ordinal);
        Assert.Contains("IClientAppManager", helper, StringComparison.Ordinal);
        Assert.Contains("int baseFolder", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("string? appDir", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("CLIENTENGINE_INTERFACE_VERSION006", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("SendInput", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("SetCursorPos", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("DepotDownloader", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("steamcmd", helper, StringComparison.Ordinal);
    }

    [Fact]
    public void UninstallPath_CommandsSteamIpcWithoutAnOcrPrompt()
    {
        var adapter = ReadRepoFile("ExoLauncher", "Adapters", "SteamAdapter.cs");
        var uninstall = Slice(
            adapter,
            "public async Task<InstallResult> UninstallAsync",
            "public InstallProgress GetDownloadProgress");
        var protocol = ReadRepoFile("ExoLauncher", "Adapters", "Cli", "SteamProtocol.cs");

        Assert.Contains("steam://uninstall/{appId}", protocol, StringComparison.Ordinal);
        Assert.Contains("SteamProtocol.UninstallUri(appId)", uninstall, StringComparison.Ordinal);
        Assert.Contains("ProcessHelper.StartHidden", uninstall, StringComparison.Ordinal);
        Assert.DoesNotContain("StartProtocol", uninstall, StringComparison.Ordinal);
        Assert.Contains("StoreUninstallPromptAutomator", uninstall, StringComparison.Ordinal);
        Assert.DoesNotContain("SteamUninstallPromptAutomator", uninstall, StringComparison.Ordinal);
        Assert.Contains("CommandSteamIpcAsync(", uninstall, StringComparison.Ordinal);
        Assert.Contains("\"uninstall\"", uninstall, StringComparison.Ordinal);
        Assert.Contains("retryCommandFailure: false", uninstall, StringComparison.Ordinal);
        Assert.Contains("ipc != SteamIpcStatus.Ok", uninstall, StringComparison.Ordinal);
        Assert.DoesNotContain("did not match its app manifest", uninstall, StringComparison.Ordinal);
        Assert.Contains("StoreWindowHider.ForSteam()", uninstall, StringComparison.Ordinal);
        Assert.DoesNotContain("HiddenStoreRuntime.SuspendFor", uninstall, StringComparison.Ordinal);
        Assert.DoesNotContain("RestoreStoreWindows", uninstall, StringComparison.Ordinal);
        Assert.DoesNotContain("uninstallingSeen = commanded", uninstall, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromSeconds(90)", uninstall, StringComparison.Ordinal);
        Assert.Contains("Steam did not start removing this game.", uninstall, StringComparison.Ordinal);

        var helper = ReadRepoFile("ExoLauncher.SteamIpc", "Program.cs");
        var runUninstall = Slice(
            helper,
            "private static int RunUninstall",
            "private static int RunState");
        Assert.Contains("return result == 0 ? 0 : 1;", runUninstall, StringComparison.Ordinal);
        Assert.DoesNotContain("return 0;", runUninstall, StringComparison.Ordinal);
        Assert.Contains("Steam returned NotInstalled", runUninstall, StringComparison.Ordinal);
    }

    private static string Slice(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing source marker: {startMarker}");
        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"Missing source marker: {endMarker}");
        return source[start..end];
    }

    private static string ReadRepoFile(params string[] relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ExoLauncher.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(new[] { dir!.FullName }.Concat(relative).ToArray()));
    }
}
