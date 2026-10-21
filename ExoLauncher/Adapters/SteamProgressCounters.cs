namespace ExoLauncher.Adapters;

/// <summary>Pure phase selection shared by the native IPC helper and manifest fallback.</summary>
internal static class SteamProgressCounters
{
    internal readonly record struct Reading(string Phase, long? Done, long? Total, double? Percent);

    internal static double? Ratio(long? done, long? total) =>
        done is >= 0 && total is > 0 && done <= total ? 100d * done.Value / total.Value : null;

    internal static Reading Resolve(long? downloaded, long? downloadTotal, long? processed, long? processTotal,
        bool installing = false, bool downloading = false)
    {
        var download = Ratio(downloaded, downloadTotal);
        var processing = Ratio(processed, processTotal);
        if (downloading && download is not >= 100)
            return new("downloading", download is null ? null : downloaded, downloadTotal is > 0 ? downloadTotal : null, download);
        // Steam can stage concurrently with download. An explicit processing
        // state wins; otherwise incomplete transfer remains the active phase.
        if (installing || processing is < 100 && (download is null or >= 100 || downloaded == 0 && processed > 0))
            return new("installing", processing is null ? null : processed, processTotal is > 0 ? processTotal : null, processing);
        if (download is not null)
            return new("downloading", downloaded, downloadTotal, download);
        if (processing is not null)
            return new("installing", processed, processTotal, processing);
        return new("downloading", null, downloadTotal is > 0 ? downloadTotal : null, null);
    }
}
