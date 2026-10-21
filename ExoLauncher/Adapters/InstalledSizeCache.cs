using System.Collections.Concurrent;
using System.Diagnostics;

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
    private static readonly SemaphoreSlim MeasurementSlots = new(2, 2);
    internal const int MaximumEntries = 2048;
    internal static event Action? MeasurementUpdated;

    private sealed class Entry
    {
        public Measurement? Result;
        public int Measuring;
    }

    internal sealed record Measurement(long? Bytes, bool Complete, DateTimeOffset MeasuredAtUtc);

    internal static Measurement? Peek(string? path) =>
        path is not null && Entries.TryGetValue(path, out var entry) ? Volatile.Read(ref entry.Result) : null;

    /// <summary>Last measured size for <paramref name="path"/>, refreshing it in the background when stale.</summary>
    public static long? Get(string? path, int maxFiles = 8000)
    {
        if (string.IsNullOrWhiteSpace(path) || IsAntiCheatPath(path)) return null;
        var entry = Entries.GetOrAdd(path, static _ => new Entry());
        if (Entries.Count > MaximumEntries)
            foreach (var key in Entries.Keys.Where(key => !string.Equals(key, path, StringComparison.OrdinalIgnoreCase)).Take(Entries.Count - MaximumEntries))
                Entries.TryRemove(key, out _);
        var result = Volatile.Read(ref entry.Result);
        if (result is null || DateTimeOffset.UtcNow - result.MeasuredAtUtc > Ttl)
            ScheduleMeasure(path, entry, maxFiles);
        return result is { Complete: true } ? result.Bytes : null;
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
    internal static void Clear() => Entries.Clear();

    private static void ScheduleMeasure(string path, Entry entry, int maxFiles)
    {
        if (Interlocked.CompareExchange(ref entry.Measuring, 1, 0) != 0) return;
        _ = Task.Run(async () =>
        {
            await MeasurementSlots.WaitAsync().ConfigureAwait(false);
            var previous = Volatile.Read(ref entry.Result);
            Measurement next;
            try
            {
                try
                {
                    next = Measure(path, maxFiles);
                }
                catch
                {
                    next = new Measurement(null, false, DateTimeOffset.UtcNow);
                }
                Volatile.Write(ref entry.Result, next);
            }
            finally
            {
                Interlocked.Exchange(ref entry.Measuring, 0);
                MeasurementSlots.Release();
            }
            // A TTL re-measure of an unchanged tree must not repaint the library.
            if (next.Bytes == previous?.Bytes && next.Complete == (previous?.Complete ?? false)) return;
            try { MeasurementUpdated?.Invoke(); } catch { /* Cache readers must not break measurement completion. */ }
        });
    }

    internal static Measurement Measure(string path, int maxFiles = 8000, int maxDirectories = 2048,
        int maxDepth = 64, TimeSpan? budget = null, CancellationToken cancellationToken = default)
    {
        Measurement Result(long? bytes, bool complete) => new(bytes, complete, DateTimeOffset.UtcNow);
        if (!Directory.Exists(path) || IsAntiCheatPath(path)) return Result(null, false);
        var clock = Stopwatch.StartNew();
        var limit = budget ?? TimeSpan.FromSeconds(2);
        long total = 0;
        var complete = true;
        try
        {
            var seen = 0;
            var directoryCount = 0;
            var dirs = new Stack<(string Path, int Depth)>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            dirs.Push((System.IO.Path.GetFullPath(path), 0));
            while (dirs.Count > 0)
            {
                if (cancellationToken.IsCancellationRequested || clock.Elapsed >= limit || ++directoryCount > maxDirectories)
                    return Result(total, false);
                var (dir, depth) = dirs.Pop();
                if (!visited.Add(dir)) continue;
                try
                {
                    if (IsAntiCheatPath(dir) || (File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
                    {
                        complete = false;
                        continue;
                    }
                    foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
                    {
                        if (cancellationToken.IsCancellationRequested || clock.Elapsed >= limit)
                            return Result(total, false);
                        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            complete = false;
                            continue;
                        }
                        if (entry is DirectoryInfo)
                        {
                            if (depth >= maxDepth || directoryCount + dirs.Count >= maxDirectories)
                                return Result(total, false);
                            dirs.Push((entry.FullName, depth + 1));
                        }
                        else if (entry is FileInfo file)
                        {
                            if (++seen > maxFiles) return Result(total, false);
                            try { total = checked(total + file.Length); }
                            catch { complete = false; }
                        }
                    }
                }
                catch
                {
                    complete = false;
                }
            }
            return Result(total, complete);
        }
        catch
        {
            return Result(total, false);
        }
    }
}
