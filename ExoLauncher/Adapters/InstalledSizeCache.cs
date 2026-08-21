using System.Collections.Concurrent;

namespace ExoLauncher.Adapters;

/// <summary>
/// Install sizes for adapters whose vendor data carries no size field. Walking
/// a Riot product tree costs hundreds of milliseconds of disk I/O, and a
/// library scan used to pay that for every product on every refresh. Reads are
/// non-blocking: the caller gets the last measurement (null before the first
/// one lands) and the walk happens off the scan thread.
/// </summary>
internal static class InstalledSizeCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);
    private static readonly ConcurrentDictionary<string, Entry> Entries =
        new(StringComparer.OrdinalIgnoreCase);

    private sealed class Entry
    {
        public long? Bytes;
        public DateTimeOffset MeasuredAtUtc;
        public int Measuring;
    }

    /// <summary>Last measured size for <paramref name="path"/>, refreshing it in the background when stale.</summary>
    public static long? Get(string? path, int maxFiles = 8000)
    {
        if (string.IsNullOrWhiteSpace(path) || IsAntiCheatPath(path)) return null;
        var entry = Entries.GetOrAdd(path, static _ => new Entry());
        if (entry.Bytes is null || DateTimeOffset.UtcNow - entry.MeasuredAtUtc > Ttl)
            ScheduleMeasure(path, entry, maxFiles);
        return entry.Bytes;
    }

    /// <summary>True for Vanguard / vgk / vgc trees. Those are never walked.</summary>
    internal static bool IsAntiCheatPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var n = path.Replace('/', '\\');
        return n.Contains(@"\Riot Vanguard", StringComparison.OrdinalIgnoreCase)
            || n.Contains(@"\Vanguard\", StringComparison.OrdinalIgnoreCase)
            || n.EndsWith(@"\Vanguard", StringComparison.OrdinalIgnoreCase)
            || n.Contains(@"\vgk\", StringComparison.OrdinalIgnoreCase)
            || n.EndsWith(@"\vgk", StringComparison.OrdinalIgnoreCase)
            || n.Contains(@"\vgc\", StringComparison.OrdinalIgnoreCase)
            || n.EndsWith(@"\vgc", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Drops the cached size so the next scan re-measures (install, update, repair, remove).</summary>
    public static void Invalidate(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        Entries.TryRemove(path, out _);
    }

    private static void ScheduleMeasure(string path, Entry entry, int maxFiles)
    {
        if (Interlocked.CompareExchange(ref entry.Measuring, 1, 0) != 0) return;
        _ = Task.Run(() =>
        {
            try
            {
                var measured = Measure(path, maxFiles);
                if (measured is not null)
                {
                    entry.Bytes = measured;
                    entry.MeasuredAtUtc = DateTimeOffset.UtcNow;
                }
            }
            catch
            {
                /* size is informational */
            }
            finally
            {
                Interlocked.Exchange(ref entry.Measuring, 0);
            }
        });
    }

    private static long? Measure(string path, int maxFiles)
    {
        if (!Directory.Exists(path) || IsAntiCheatPath(path)) return null;
        try
        {
            long total = 0;
            var seen = 0;
            var dirs = new Stack<string>();
            dirs.Push(path);
            while (dirs.Count > 0)
            {
                var dir = dirs.Pop();
                if (IsAntiCheatPath(dir)) continue;
                try
                {
                    foreach (var sub in Directory.EnumerateDirectories(dir))
                        dirs.Push(sub);
                }
                catch
                {
                    /* skip a locked or unauthorized directory */
                }

                try
                {
                    foreach (var file in Directory.EnumerateFiles(dir))
                    {
                        try { total += new FileInfo(file).Length; }
                        catch { /* skip a locked or removed file */ }
                        if (++seen >= maxFiles)
                            return total > 0 ? total : null;
                    }
                }
                catch
                {
                    /* skip a locked directory */
                }
            }

            return total > 0 ? total : null;
        }
        catch
        {
            return null;
        }
    }
}
