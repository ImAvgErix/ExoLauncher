using System.Diagnostics;
using ExoLauncher.Adapters;
using ExoLauncher.Models;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// The store action paths were measured on a real library before these were
/// written. Each fact pins the expensive choice that made install, launch, stop
/// or "close the other launchers" slower than it needed to be.
/// </summary>
public sealed class StoreActionCostTests
{
    [Fact]
    public void PinnedToolVerification_HashesEachBinaryOncePerRevision()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "exo-pinned-tool-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllText(path, "not the pinned asset");
        try
        {
            var asset = new ExoLauncher.Services.GitHubReleaseAsset(
                "derrod",
                "legendary",
                "0.21.0",
                "legendary_windows_x64.exe",
                ExpectedSize: new FileInfo(path).Length,
                ExpectedSha256: new string('a', 64));

            var validations = 0;
            bool Validate(string _)
            {
                validations++;
                return true;
            }

            Assert.False(PinnedToolCache.IsPinnedAsset(asset, path, Validate));
            Assert.False(PinnedToolCache.IsPinnedAsset(asset, path, Validate));
            Assert.False(PinnedToolCache.IsPinnedAsset(asset, path, Validate));

            // Verifying legendary.exe is a SHA-256 of 17 MB (17 ms measured) and
            // every library projection asks once per game.
            Assert.Equal(1, validations);

            // A replaced binary is re-verified, not trusted from the cache. Same
            // length on purpose: only the file identity may decide that.
            File.WriteAllText(path, "NOT THE PINNED ASSET");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
            Assert.Equal(asset.ExpectedSize, new FileInfo(path).Length);
            Assert.False(PinnedToolCache.IsPinnedAsset(asset, path, Validate));
            Assert.Equal(2, validations);
        }
        finally
        {
            try { File.Delete(path); } catch { /* temp cleanup */ }
        }
    }

    [Fact]
    public void ToolResolution_NeverHashesInline()
    {
        // ShellController projects canRepair per game, and CanRepair resolves the
        // backend. The inline hash made that O(games) SHA-256 passes.
        foreach (var adapter in new[] { "EpicAdapter.cs", "GogAdapter.cs" })
        {
            var text = ReadRepoFile("ExoLauncher", "Adapters", adapter);
            Assert.DoesNotContain(
                "VerifiedGitHubReleaseDownloader.IsPinnedAssetFile",
                text,
                StringComparison.Ordinal);
            Assert.Contains("PinnedToolCache.IsPinnedAsset", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task InstalledSize_IsNeverMeasuredInsideAScan()
    {
        var root = Path.Combine(Path.GetTempPath(), "exo-size-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(Path.Combine(root, "game.pak"), new byte[4096]);

            // First read must not block: walking a Riot product tree measured
            // 58 ms for VALORANT and 400 ms for League on the author's machine.
            var watch = Stopwatch.StartNew();
            var first = InstalledSizeCache.Get(root);
            watch.Stop();
            Assert.Null(first);
            Assert.True(watch.ElapsedMilliseconds < 50, $"scan read took {watch.ElapsedMilliseconds} ms");

            var deadline = DateTime.UtcNow.AddSeconds(5);
            long? measured = null;
            while (DateTime.UtcNow < deadline && measured is null)
            {
                measured = InstalledSizeCache.Get(root);
                if (measured is null) await Task.Delay(25);
            }

            Assert.Equal(4096, measured);
            InstalledSizeCache.Invalidate(root);
            Assert.Null(InstalledSizeCache.Get(root));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* temp cleanup */ }
        }
    }

    [Fact]
    public void InstalledSize_NeverWalksVanguard()
    {
        Assert.True(InstalledSizeCache.IsAntiCheatPath(@"C:\Program Files\Riot Vanguard"));
        Assert.True(InstalledSizeCache.IsAntiCheatPath(@"C:\Users\Erix\AppData\Local\Riot Games\Riot Vanguard"));
        Assert.True(InstalledSizeCache.IsAntiCheatPath(@"C:\Windows\System32\drivers\vgk"));
        Assert.False(InstalledSizeCache.IsAntiCheatPath(@"C:\Riot Games\VALORANT"));
        Assert.Null(InstalledSizeCache.Get(@"C:\Program Files\Riot Vanguard"));
    }

    [Fact]
    public void RiotScan_DoesNotWalkProductTrees()
    {
        var riot = ReadRepoFile("ExoLauncher", "Adapters", "RiotAdapter.cs");
        var scan = Slice(
            riot,
            "public Task<IReadOnlyList<GameEntry>> GetLibraryAsync",
            "public async Task<InstallResult> InstallAsync");

        Assert.Contains("InstalledSizeCache.Get(installedPath)", scan, StringComparison.Ordinal);
        Assert.Contains("TryReadInstallSizeBytes(productId)", scan, StringComparison.Ordinal);
        Assert.DoesNotContain("EnumerateFiles", scan, StringComparison.Ordinal);
        Assert.DoesNotContain("TryDirSize", riot, StringComparison.Ordinal);
    }

    [Fact]
    public void SteamInstallWatch_DoesNotWalkDirectoriesEveryTick()
    {
        var steam = ReadRepoFile("ExoLauncher", "Adapters", "SteamAdapter.cs");
        var install = Slice(
            steam,
            "public async Task<InstallResult> InstallAsync",
            "private static void StopFreshSteamInstall");

        // The 400 ms watch used to walk the install folder and the downloading
        // folder and re-read the content log on every tick, against the same
        // drive Steam was writing the download to.
        Assert.Contains("sampler.ReadInstalledSize(hit.Value.Path)", install, StringComparison.Ordinal);
        Assert.Contains("sampler.ReadTransfer(", install, StringComparison.Ordinal);
        Assert.DoesNotContain("TryDirSize(hit.Value.Path)", install, StringComparison.Ordinal);
        Assert.Contains("SteamContentLogProgress.TryReadLatest", steam, StringComparison.Ordinal);
        Assert.Contains("TryReadDownloadingBytes", steam, StringComparison.Ordinal);
    }

    [Fact]
    public void SteamPathResolution_IsCachedAcrossWatchTicks()
    {
        var steam = ReadRepoFile("ExoLauncher", "Adapters", "SteamAdapter.cs");

        // Every tick resolved the registry root and re-parsed libraryfolders.vdf
        // twice before it could read the manifest it was actually polling.
        Assert.Contains("SteamPathTtl", steam, StringComparison.Ordinal);
        Assert.Contains("private static string? ReadSteamRoot()", steam, StringComparison.Ordinal);
        Assert.Contains("private static List<string> ReadLibraryFolders(", steam, StringComparison.Ordinal);
    }

    [Fact]
    public void StoreChromeGuard_TakesOneProcessSnapshotPerPass()
    {
        var hider = ReadRepoFile("ExoLauncher", "Adapters", "StoreWindowHider.cs");

        // The session guard polls up to 25 process names four times a second.
        // Per-name GetProcessesByName snapshots the whole process table each
        // time: 22 names measured 21 ms per pass versus 2 ms for one snapshot.
        Assert.Contains("SnapshotNamedProcesses", hider, StringComparison.Ordinal);
        Assert.Equal(
            1,
            hider.Split("Process.GetProcesses()", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("Process.GetProcessesByName", hider, StringComparison.Ordinal);
    }

    [Fact]
    public void LaunchPath_DoesNotHideSiblingChromeOnTheCallerThread()
    {
        var orchestrator = ReadRepoFile("ExoLauncher", "Services", "LaunchOrchestrator.cs");
        var cleanup = Slice(
            orchestrator,
            "private static Task CloseUnusedStoreClientsAsync",
            "private async Task ScheduleCleanupAsync");

        // HideUnused walks every store's processes and windows. As a plain async
        // method it ran inline on the launch (and RPC) thread up to its first
        // await.
        Assert.Contains("Task.Run(async () =>", cleanup, StringComparison.Ordinal);
        Assert.Contains("StoreClientCleanup.HideUnused(keep)", cleanup, StringComparison.Ordinal);
    }

    [Fact]
    public void StopBinding_DoesNotWaitSecondsBeforeItStarts()
    {
        var orchestrator = ReadRepoFile("ExoLauncher", "Services", "LaunchOrchestrator.cs");

        Assert.Contains("HandoffSettle = TimeSpan.FromMilliseconds(400)", orchestrator, StringComparison.Ordinal);
        Assert.Contains("await Task.Delay(HandoffSettle", orchestrator, StringComparison.Ordinal);
        Assert.DoesNotContain("await Task.Delay(2500", orchestrator, StringComparison.Ordinal);

        // While the bound identity is alive, rebinding must not re-enumerate
        // every process on the machine for the whole session.
        var rebind = Slice(
            orchestrator,
            "private async Task RebindStopTargetLoopAsync",
            "private void CompleteGameSession");
        Assert.Contains("_runningGames.GetState(game).IsRunning", rebind, StringComparison.Ordinal);
    }

    [Fact]
    public void RunStateForALaunchedGame_DoesNotEnumerateEveryProcess()
    {
        var registry = ReadRepoFile("ExoLauncher", "Services", "GameProcessRegistry.cs");
        var state = Slice(
            registry,
            "public GameRunState GetState(",
            "public void ObserveLaunch(");

        Assert.Contains("MatchesLaunchedIdentity(game)", state, StringComparison.Ordinal);
        var fastPath = state.IndexOf("MatchesLaunchedIdentity(game)", StringComparison.Ordinal);
        var scan = state.IndexOf("FindCandidates(game)", StringComparison.Ordinal);
        Assert.True(fastPath > 0 && scan > fastPath, "the identity check must come before the scan");
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
