using ExoLauncher.Adapters;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class SteamTransferProgressTests
{
    [Fact]
    public void Percent_MatchesSteamByteCounters()
    {
        Assert.Equal(0, SteamTransferProgress.Percent(0, 38_358_400));
        Assert.Equal(50, SteamTransferProgress.Percent(19_179_200, 38_358_400));
        Assert.Equal(100, SteamTransferProgress.Percent(38_358_400, 38_358_400));
    }

    [Fact]
    public void Percent_UnknownTotalsStayNull()
    {
        Assert.Null(SteamTransferProgress.Percent(null, null));
        Assert.Null(SteamTransferProgress.Percent(100, 0));
        Assert.Null(SteamTransferProgress.Percent(100, null));
    }

    [Fact]
    public void Percent_SmallOvershootIs100()
    {
        Assert.Equal(100, SteamTransferProgress.Percent(8000, 8000));
        Assert.Equal(100, SteamTransferProgress.Percent(8010, 8000));
    }

    [Fact]
    public void Percent_StaleLeftoverVersusNewJobIsUnknown()
    {
        // Previous full-game counters still sitting in the ACF while Steam's
        // Downloads row is a 36 MB patch. Showing 100% here is the "way off" bug.
        Assert.Null(SteamTransferProgress.Percent(37_000_000_000, 38_358_400));
    }

    [Fact]
    public void Percent_UsesStageWhenDownloadFinishedAndStageIsStillMoving()
    {
        var pct = SteamTransferProgress.Percent(
            bytesDownloaded: 1000,
            bytesToDownload: 1000,
            bytesStaged: 400,
            bytesToStage: 1000);
        Assert.Equal(40, pct);
    }

    [Fact]
    public void Percent_KeepsLiveDownloadInsteadOfJumpingToStaging()
    {
        var pct = SteamTransferProgress.Percent(
            bytesDownloaded: 996,
            bytesToDownload: 1000,
            bytesStaged: 400,
            bytesToStage: 1000);
        Assert.Equal(99.6, pct!.Value, 5);
    }

    [Fact]
    public void Percent_IgnoresUnchangedLeftoverTotalsWhileBusy()
    {
        var pct = SteamTransferProgress.Percent(
            bytesDownloaded: 2_631_031_328,
            bytesToDownload: 2_631_031_328,
            bytesStaged: 2_690_302_593,
            bytesToStage: 2_690_302_593,
            busy: true,
            baselineDownloaded: 2_631_031_328,
            baselineToDownload: 2_631_031_328);
        Assert.Null(pct);
    }

    [Fact]
    public void Percent_StartSnapshotIsUnknownWhenAcfIsLeftoverFromAPreviousJob()
    {
        var job = new SteamContentLogProgress.Job(0, 38_358_400, 0, 882_703_683);
        var pct = SteamTransferProgress.Percent(
            bytesDownloaded: 2_631_031_328,
            bytesToDownload: 2_631_031_328,
            bytesStaged: 2_690_302_593,
            bytesToStage: 2_690_302_593,
            busy: true,
            baselineDownloaded: 2_631_031_328,
            baselineToDownload: 2_631_031_328,
            liveJob: job);
        Assert.Null(pct);
    }

    [Fact]
    public void Percent_UsesDownloadingFolderWhenContentLogIsAStartSnapshot()
    {
        var job = new SteamContentLogProgress.Job(0, 38_358_400, 0, 882_703_683);
        var pct = SteamTransferProgress.Percent(
            bytesDownloaded: 2_631_031_328,
            bytesToDownload: 2_631_031_328,
            bytesStaged: 2_690_302_593,
            bytesToStage: 2_690_302_593,
            busy: true,
            baselineDownloaded: 2_631_031_328,
            baselineToDownload: 2_631_031_328,
            liveJob: job,
            diskBytes: 19_179_200);
        Assert.Equal(50, pct);
    }

    [Fact]
    public void Percent_TracksNewJobAfterBaselineCountersChange()
    {
        var pct = SteamTransferProgress.Percent(
            bytesDownloaded: 19_179_200,
            bytesToDownload: 38_358_400,
            bytesStaged: 0,
            bytesToStage: 0,
            busy: true,
            baselineDownloaded: 2_631_031_328,
            baselineToDownload: 2_631_031_328);
        Assert.Equal(50, pct);
    }

    [Fact]
    public void Percent_BusyZeroAcfIsUnknownNotZero()
    {
        // MECCHA live: ACF sat at 0 / 2.9 GB after InstallApp. 0% paints an
        // invisible bar. That is not a reading.
        Assert.Null(SteamTransferProgress.Percent(
            bytesDownloaded: 0,
            bytesToDownload: 2_907_668_000,
            bytesStaged: 0,
            bytesToStage: 0,
            busy: true));
    }

    [Fact]
    public void Percent_LeftoverDownloadingFolderLargerThanJobIsUnknown()
    {
        Assert.Null(SteamTransferProgress.Percent(
            bytesDownloaded: 0,
            bytesToDownload: 2_907_668_000,
            bytesStaged: 0,
            bytesToStage: 0,
            busy: true,
            diskBytes: 3_112_277_777));
    }

    [Fact]
    public void Percent_BusyZeroUsesDiskWhenItIsUnderTheJobTotal()
    {
        Assert.Equal(50, SteamTransferProgress.Percent(
            bytesDownloaded: 0,
            bytesToDownload: 38_358_400,
            bytesStaged: 0,
            bytesToStage: 0,
            busy: true,
            diskBytes: 19_179_200));
    }

    [Fact]
    public void ContentLog_ParsesSteamLiveDownloadTotals()
    {
        const string log =
            "[2026-08-13 00:20:49] AppID 4704690 update started : download 0/2631031328, store 0/0, reuse 0/0, delta 0/0, stage 0/2690302593 \n" +
            "[2026-08-11 01:32:36] AppID 1620730 update started : download 493598144/20826243776, store 0/0, reuse 0/0, delta 0/0, stage 833202025/25248532739 \n";
        var job = SteamContentLogProgress.TryParseLatest(log, "4704690");
        Assert.NotNull(job);
        Assert.Equal(0, job.Value.BytesDownloaded);
        Assert.Equal(2_631_031_328, job.Value.BytesToDownload);
        Assert.Equal(2_690_302_593, job.Value.BytesToStage);

        var resumed = SteamContentLogProgress.TryParseLatest(log, "1620730");
        Assert.NotNull(resumed);
        Assert.Equal(493_598_144, resumed.Value.BytesDownloaded);
        Assert.Equal(20_826_243_776, resumed.Value.BytesToDownload);
    }

    [Fact]
    public void DownloadingFolder_SumsLiveJobBytes()
    {
        var root = Path.Combine(Path.GetTempPath(), "exo-steam-dl-" + Guid.NewGuid().ToString("N"));
        var job = Path.Combine(root, "steamapps", "downloading", "4704690");
        Directory.CreateDirectory(job);
        try
        {
            using (var fs = new FileStream(Path.Combine(job, "chunk.bin"), FileMode.Create, FileAccess.Write))
                fs.SetLength(4096);
            Assert.Equal(4096, SteamContentLogProgress.TryReadDownloadingBytes(root, "4704690"));
            Assert.Null(SteamContentLogProgress.TryReadDownloadingBytes(root, "1"));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* tmp */ }
        }
    }

    [Fact]
    public void AdapterWatch_UsesTransferPercentWithoutFakeCreep()
    {
        var adapter = File.ReadAllText(Path.Combine(RepoRoot(), "ExoLauncher", "Adapters", "SteamAdapter.cs"));
        Assert.Contains("SteamTransferProgress.Resolve", adapter, StringComparison.Ordinal);
        Assert.Contains("SteamContentLogProgress.TryReadLatest", adapter, StringComparison.Ordinal);
        Assert.Contains("TryReadDownloadingBytes", adapter, StringComparison.Ordinal);
        Assert.Contains("BytesToStage", adapter, StringComparison.Ordinal);
        Assert.Contains("Task.Delay(400", adapter, StringComparison.Ordinal);
        Assert.DoesNotContain("Task.Delay(2000", adapter, StringComparison.Ordinal);
        Assert.DoesNotContain("10 + (done * 85.0", adapter, StringComparison.Ordinal);
        Assert.DoesNotContain("15 + done * 80.0", adapter, StringComparison.Ordinal);
        Assert.DoesNotContain("lastPct + 0.15", adapter, StringComparison.Ordinal);
        Assert.DoesNotContain("lastPct + 0.4", adapter, StringComparison.Ordinal);
        Assert.DoesNotContain("elapsed / 8", adapter, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ExoLauncher.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
