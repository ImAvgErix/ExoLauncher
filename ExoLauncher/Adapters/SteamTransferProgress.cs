namespace ExoLauncher.Adapters;

/// <summary>
/// Steam's own download percent. Match the Downloads row: live
/// downloaded/toDownload. Do not invent a climbing number, and do not treat
/// content_log's one-shot "update started" snapshot as a live ticker.
/// </summary>
internal static class SteamTransferProgress
{
    internal readonly record struct Sample(long? Downloaded, long? ToDownload, double? Percent);

    public static double? Percent(long? bytesDownloaded, long? bytesToDownload) =>
        Resolve(bytesDownloaded, bytesToDownload).Percent;

    public static double? Percent(
        long? bytesDownloaded,
        long? bytesToDownload,
        long? bytesStaged,
        long? bytesToStage,
        bool busy = false,
        long? baselineDownloaded = null,
        long? baselineToDownload = null,
        SteamContentLogProgress.Job? liveJob = null,
        long? diskBytes = null) =>
        Resolve(
            bytesDownloaded,
            bytesToDownload,
            bytesStaged,
            bytesToStage,
            busy,
            baselineDownloaded,
            baselineToDownload,
            liveJob,
            diskBytes).Percent;

    public static Sample Resolve(
        long? bytesDownloaded,
        long? bytesToDownload,
        long? bytesStaged = null,
        long? bytesToStage = null,
        bool busy = false,
        long? baselineDownloaded = null,
        long? baselineToDownload = null,
        SteamContentLogProgress.Job? liveJob = null,
        long? diskBytes = null)
    {
        var dl = bytesDownloaded;
        var toDl = bytesToDownload;
        var st = bytesStaged;
        var toSt = bytesToStage;

        var leftover = baselineToDownload is not null &&
                       toDl == baselineToDownload &&
                       dl == baselineDownloaded;
        var leftoverFull = leftover && Ratio(dl, toDl) is >= 100;
        var overshoot = toDl is > 0 &&
                        dl is long n &&
                        n > toDl.Value &&
                        n - toDl.Value > Math.Max(64 * 1024, toDl.Value / 500);

        if (liveJob is { BytesToDownload: > 0 } job)
        {
            var acfMissing = toDl is null or 0;
            var acfIsDifferentJob = toDl is long acfTotal && acfTotal != job.BytesToDownload;
            if (acfMissing || overshoot || leftoverFull || (busy && acfIsDifferentJob))
            {
                toDl = job.BytesToDownload;
                toSt = job.BytesToStage;
                dl = LiveDownloaded(
                    acfDownloaded: leftoverFull || overshoot || acfIsDifferentJob ? null : bytesDownloaded,
                    jobDownloaded: job.BytesDownloaded,
                    jobToDownload: job.BytesToDownload,
                    diskBytes: diskBytes);
                // content_log stage is the same one-shot snapshot. 0 is not live.
                st = job.BytesStaged > 0 && job.BytesStaged < job.BytesToStage
                    ? job.BytesStaged
                    : null;
            }
        }

        if ((dl is null or 0 || leftoverFull || overshoot) &&
            UsableDisk(diskBytes, toDl) is long liveDisk)
            dl = liveDisk;

        // Steam often leaves BytesDownloaded at 0 after InstallApp while the
        // downloading/ folder is leftover from a previous job (larger than this
        // total). 0% is not a live reading; neither is that leftover folder as 100%.
        if (busy && toDl is > 0 && dl is null or 0)
            return new Sample(null, toDl, null);

        // Live download job — keep this percent even at 99.6%. Steam's row is
        // still the download, not staging.
        if (toDl is > 0 && dl is long downloaded && downloaded < toDl.Value)
            return new Sample(downloaded, toDl, Ratio(downloaded, toDl));

        if (toSt is > 0 && st is long staged && staged < toSt.Value)
            return new Sample(staged, toSt, Ratio(staged, toSt));

        if (busy && leftoverFull &&
            dl == bytesDownloaded &&
            toDl == bytesToDownload)
            return new Sample(null, toDl, null);

        var percent = Ratio(dl, toDl) ?? Ratio(st, toSt);
        return new Sample(dl, toDl, percent);
    }

    /// <summary>
    /// content_log's downloaded count is a snapshot from "update started",
    /// usually 0. Never pin Exo to that 0 while Steam's Downloads row climbs.
    /// Prefer disk, then a non-zero snapshot, otherwise unknown.
    /// </summary>
    private static long? LiveDownloaded(
        long? acfDownloaded,
        long jobDownloaded,
        long jobToDownload,
        long? diskBytes)
    {
        if (UsableDisk(diskBytes, jobToDownload) is long disk)
            return disk;
        if (acfDownloaded is long acf && acf >= 0 && acf < jobToDownload)
            return acf;
        if (jobDownloaded > 0 && jobDownloaded < jobToDownload)
            return jobDownloaded;
        return null;
    }

    /// <summary>
    /// Only a folder still growing toward this job's total. A leftover tree
    /// bigger than toDownload is cache from last time, not 100%.
    /// </summary>
    private static long? UsableDisk(long? diskBytes, long? toDownload)
    {
        if (diskBytes is long disk && disk > 0 && toDownload is > 0 && disk < toDownload.Value)
            return disk;
        return null;
    }

    private static double? Ratio(long? done, long? total)
    {
        if (total is not > 0) return null;
        var n = done.GetValueOrDefault();
        if (done is null) return null;
        if (n < 0) n = 0;
        if (n <= total.Value)
            return n == total.Value
                ? 100
                : Math.Clamp(100.0 * n / total.Value, 0, 100);

        var slack = Math.Max(64 * 1024, total.Value / 500);
        return n - total.Value <= slack ? 100 : null;
    }
}
