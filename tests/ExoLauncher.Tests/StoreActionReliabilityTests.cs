using ExoLauncher.Adapters;
using ExoLauncher.Models;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// Install / update / uninstall / repair must never report work they did not do,
/// and cancel must never delete a game the user already had. These are the exact
/// failures found while walking each store's action paths.
/// </summary>
public sealed class StoreActionReliabilityTests
{
    [Fact]
    public void CancelledSteamInstall_OnlyRollsBackWhatExoStarted()
    {
        Assert.True(SteamStateFlags.CanRollBackCancelledInstall(appManifestExistedBeforeRequest: false));
        Assert.False(SteamStateFlags.CanRollBackCancelledInstall(appManifestExistedBeforeRequest: true));
    }

    [Fact]
    public void SteamInstall_CapturesManifestPresenceBeforeAskingSteam()
    {
        var adapter = ReadRepoFile("ExoLauncher", "Adapters", "SteamAdapter.cs");
        var install = Slice(
            adapter,
            "public async Task<InstallResult> InstallAsync",
            "private static void StopFreshSteamInstall");

        // The flag has to be read before the install request; Steam writes the
        // manifest itself, so reading it later cannot tell the two cases apart.
        var captured = install.IndexOf("appManifestExistedBeforeRequest = FindAppManifestPath", StringComparison.Ordinal);
        var requested = install.IndexOf("CommandSteamIpcAsync(\"install\"", StringComparison.Ordinal);
        Assert.True(captured > 0);
        Assert.True(requested > captured, "install must be requested after the pre-state is captured");
        Assert.DoesNotContain("StopFreshSteamInstall(appId);", install, StringComparison.Ordinal);
        Assert.Equal(
            2,
            install.Split("StopFreshSteamInstall(appId, appManifestExistedBeforeRequest)", StringSplitOptions.None).Length - 1);
        Assert.Contains(
            "SteamStateFlags.CanRollBackCancelledInstall(appManifestExistedBeforeRequest)",
            adapter,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SteamIpc_TakesTheVerbAndAppIdOnly()
    {
        var ipc = ReadRepoFile("ExoLauncher", "Adapters", "SteamClientIpc.cs");
        var host = ReadRepoFile("ExoLauncher.SteamIpc", "Program.cs");
        var adapter = ReadRepoFile("ExoLauncher", "Adapters", "SteamAdapter.cs");

        // The helper never read a third argument, and Steam selects a library
        // folder by index, not by path. Resolving one cost an extra manifest
        // read on every IPC call.
        Assert.DoesNotContain("installDir", ipc, StringComparison.Ordinal);
        Assert.DoesNotContain("args[2]", host, StringComparison.Ordinal);
        Assert.Contains("SteamClientIpc.Command(action, appId)", adapter, StringComparison.Ordinal);

        // Steam stays the backend. No OCR, no synthetic input, no CDN client.
        Assert.DoesNotContain("SendInput", ipc, StringComparison.Ordinal);
        Assert.DoesNotContain("DepotDownloader", ipc, StringComparison.Ordinal);
    }

    [Fact]
    public void SteamIpc_DoesNotRetryAMissingHelper()
    {
        var adapter = ReadRepoFile("ExoLauncher", "Adapters", "SteamAdapter.cs");
        var command = Slice(
            adapter,
            "private static async Task<SteamIpcStatus> CommandSteamIpcAsync",
            "private static async Task WaitForSteamCommandListenerAsync");

        Assert.Contains("SteamIpcStatus.HostMissing", command, StringComparison.Ordinal);
        var hostMissing = command.IndexOf("last == SteamIpcStatus.HostMissing", StringComparison.Ordinal);
        var delay = command.IndexOf("await Task.Delay", StringComparison.Ordinal);
        Assert.True(hostMissing > 0, "a missing helper must be recognised");
        Assert.True(delay > hostMissing, "a missing helper must return before the retry delay");
    }

    [Fact]
    public void GogUninstall_NeverWaitsOnARecordThatDoesNotExist()
    {
        // Exo owns the files: its own registration is authoritative.
        Assert.Equal(
            GogAdapter.GogUninstallRoute.Registered,
            GogAdapter.SelectUninstallRoute(true, true, true, true));
        Assert.Equal(
            GogAdapter.GogUninstallRoute.Registered,
            GogAdapter.SelectUninstallRoute(true, false, false, false));

        // Galaxy owns the files and has a registry record to watch disappear.
        Assert.Equal(
            GogAdapter.GogUninstallRoute.GalaxyCommand,
            GogAdapter.SelectUninstallRoute(false, true, true, true));

        // No record to watch: the wait would have succeeded on its first poll
        // and reported a removal that never happened.
        Assert.Equal(
            GogAdapter.GogUninstallRoute.NotRemovable,
            GogAdapter.SelectUninstallRoute(false, true, true, false));
        Assert.Equal(
            GogAdapter.GogUninstallRoute.NotRemovable,
            GogAdapter.SelectUninstallRoute(false, true, false, true));
        Assert.Equal(
            GogAdapter.GogUninstallRoute.NotRemovable,
            GogAdapter.SelectUninstallRoute(false, false, true, true));
    }

    [Fact]
    public async Task GogUninstall_ReportsHonestlyWhenNeitherBackendOwnsTheInstall()
    {
        var root = Path.Combine(Path.GetTempPath(), "exo-gog-uninstall-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var catalog = new ExoLauncher.Services.InstalledGameCatalog(
                Path.Combine(root, "installed-games.json"));
            var adapter = new GogAdapter(null, null, catalog);
            var installPath = Path.Combine(root, "game");
            Directory.CreateDirectory(installPath);
            File.WriteAllText(Path.Combine(installPath, "keep.sav"), "player data");
            var game = new GameEntry
            {
                Id = "gog:1207658924",
                Title = "Unregistered GOG title",
                Store = StoreKind.Gog,
                Installed = true,
                Path = installPath,
            };

            var result = await adapter.UninstallAsync(game);

            Assert.False(result.Ok);
            Assert.Contains("No files were deleted", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Combine(installPath, "keep.sav")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* temp cleanup */ }
        }
    }

    [Theory]
    // A manifest plus a folder is what EGL writes when the download starts.
    [InlineData(true, true, true, false)]
    [InlineData(true, true, false, true)]
    [InlineData(true, false, false, false)]
    [InlineData(false, false, false, false)]
    public void EpicLauncherInstall_NeedsTheManifestToStopSayingIncomplete(
        bool manifestPresent,
        bool installLocationExists,
        bool updatePending,
        bool expected)
    {
        Assert.Equal(
            expected,
            EpicAdapter.IsEglInstallComplete(manifestPresent, installLocationExists, updatePending));
    }

    [Fact]
    public void EpicUninstall_CannotWedgeTheJobQueueForever()
    {
        var epic = ReadRepoFile("ExoLauncher", "Adapters", "EpicAdapter.cs");
        var uninstall = Slice(
            epic,
            "public async Task<InstallResult> UninstallAsync",
            "public InstallProgress GetDownloadProgress");

        // A CLI that never returns leaves _activeJob set, so every later
        // install/update/remove is refused until Exo restarts.
        Assert.Contains("LegendaryUninstallTimeout", uninstall, StringComparison.Ordinal);
        Assert.Contains("timeout.Token", uninstall, StringComparison.Ordinal);
        Assert.Contains("LegendaryUninstallTimeout = TimeSpan.FromMinutes(30)", epic, StringComparison.Ordinal);

        // A caller cancel stays a cancel; only the timeout becomes a failure.
        Assert.Contains(
            "catch (OperationCanceledException) when (ct.IsCancellationRequested)",
            uninstall,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SteamAgreements_RemainVisibleAndUserControlled()
    {
        var steam = ReadRepoFile("ExoLauncher", "Adapters", "SteamAdapter.cs");
        var hider = ReadRepoFile("ExoLauncher", "Adapters", "StoreWindowHider.cs");

        Assert.DoesNotContain("SteamEulaAcceptance", steam, StringComparison.Ordinal);
        Assert.DoesNotContain("StoreAgreementPromptAutomator", steam, StringComparison.Ordinal);
        Assert.Contains("if (IsAgreementWindow(hWnd))", hider, StringComparison.Ordinal);
        Assert.Contains("return;", hider[hider.IndexOf("if (IsAgreementWindow(hWnd))", StringComparison.Ordinal)..], StringComparison.Ordinal);
    }

    [Fact]
    public void EpicLauncherInstallWatch_UsesTheCompletionGuard()
    {
        var epic = ReadRepoFile("ExoLauncher", "Adapters", "EpicAdapter.cs");
        var watch = Slice(
            epic,
            "private async Task<InstallResult> WatchEpicLauncherJobAsync",
            "private static Process? TryStartInstalledGame");

        Assert.Contains("IsEglInstallComplete(", watch, StringComparison.Ordinal);
        Assert.Contains("installed?.UpdatePending == true", watch, StringComparison.Ordinal);
    }

    private static string Slice(string text, string from, string to)
    {
        var start = text.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, $"missing '{from}'");
        var end = text.IndexOf(to, start, StringComparison.Ordinal);
        Assert.True(end > start, $"missing '{to}'");
        return text[start..end];
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ExoLauncher.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string ReadRepoFile(params string[] relative) =>
        File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(relative).ToArray()));
}
