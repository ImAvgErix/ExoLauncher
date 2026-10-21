namespace ExoLauncher.Adapters;

/// <summary>Validated manifest fallback. Folder lengths and start-log snapshots are not live transfer counters.</summary>
internal static class SteamTransferProgress
{
    internal readonly record struct Sample(long? Downloaded, long? ToDownload, double? Percent, string Phase = "downloading");

    public static Sample Resolve(long? bytesDownloaded, long? bytesToDownload, long? bytesStaged = null, long? bytesToStage = null,
        bool busy = false, long? baselineDownloaded = null, long? baselineToDownload = null,
        SteamContentLogProgress.Job? liveJob = null,
        long? baselineStaged = null, long? baselineToStage = null, long? stateFlags = null)
    {
        var stageMoved = baselineToStage is not null && (baselineToStage != bytesToStage || baselineStaged != bytesStaged);
        var unchanged = baselineToDownload is not null && baselineToDownload == bytesToDownload && baselineDownloaded == bytesDownloaded && !stageMoved;
        var downloading = stateFlags is long state && (state & SteamStateFlags.Downloading) != 0;
        var installing = !downloading && stateFlags is long installState &&
            (installState & (SteamStateFlags.Staging | SteamStateFlags.Committing | SteamStateFlags.Validating | SteamStateFlags.Preallocating)) != 0;
        var phase = installing ? "installing" : "downloading";
        var differentJob = liveJob is { BytesToDownload: > 0 } job && job.BytesToDownload != bytesToDownload;
        if (busy && (unchanged || differentJob))
            return new(null, liveJob?.BytesToDownload ?? bytesToDownload, null, phase);
        // A manifest zero with no movement can be a placeholder. IPC may report
        // a trustworthy zero; the fallback has no such observation guarantee.
        if (busy && bytesDownloaded is null or 0 && bytesStaged is null or 0)
            return new(null, bytesToDownload, null, phase);
        var reading = SteamProgressCounters.Resolve(bytesDownloaded, bytesToDownload, bytesStaged, bytesToStage, installing, downloading);
        return new(reading.Done, reading.Total, reading.Percent, reading.Phase);
    }
}
