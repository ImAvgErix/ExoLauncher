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
    public void UpdatePath_UsesOnlyExactIdentityVerifiedQueuePromotion()
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
        var targetedAutomation = ReadRepoFile(
            "ExoLauncher", "Adapters", "SteamTargetedQueuePromotionAutomator.cs");

        Assert.Contains("SteamUpdateCommandPlan.BuildNudge", nudge, StringComparison.Ordinal);
        Assert.DoesNotContain("DownloadsUri", plan, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenDownloads", plan, StringComparison.Ordinal);
        Assert.DoesNotContain("SteamDownloadNowAutomator", update, StringComparison.Ordinal);
        Assert.DoesNotContain("SteamInstallDialogAutomator", update, StringComparison.Ordinal);
        Assert.Contains("StoreWindowHider.ForSteam", update, StringComparison.Ordinal);
        Assert.Contains("SteamTargetedQueuePromotionAutomator.PromoteAsync", update, StringComparison.Ordinal);
        Assert.Contains("initial.Name", update, StringComparison.Ordinal);
        Assert.Contains("SteamQueuePromotionSelector.Select", targetedAutomation, StringComparison.Ordinal);
        Assert.Contains("targetIsStillQueued", targetedAutomation, StringComparison.Ordinal);
        Assert.True(
            targetedAutomation.Split("if (!targetIsStillQueued())", StringSplitOptions.None).Length - 1 >= 2,
            "The selected appmanifest must be checked both before selection and at click time.");
        Assert.Contains("BeginOffscreenAutomationWindow", targetedAutomation, StringComparison.Ordinal);
        Assert.Contains("SteamProtocol.DownloadsUri()", targetedAutomation, StringComparison.Ordinal);
        Assert.Contains("Chrome_WidgetWin_1", targetedAutomation, StringComparison.Ordinal);
        Assert.DoesNotContain("first Download", targetedAutomation, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SetCursorPos", targetedAutomation, StringComparison.Ordinal);
        Assert.DoesNotContain("SendInput", targetedAutomation, StringComparison.Ordinal);
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
            update.Split("NudgeSteamUpdate(steamExe, game, appId", StringSplitOptions.None).Length - 1 >= 3,
            "The update watch must re-nudge Steam when the manifest remains queued.");
        Assert.Contains(
            "|| sawTargetManifestChange || !neededAtStart;",
            update,
            StringComparison.Ordinal);
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
