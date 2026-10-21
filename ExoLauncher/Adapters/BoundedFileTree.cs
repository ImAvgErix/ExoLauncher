namespace ExoLauncher.Adapters;

internal static class BoundedFileTree
{
    internal const int CatalogEntryLimit = 20_000;

    internal static IEnumerable<string> EnumerateFiles(string root, int maxEntries, int maxDepth,
        CancellationToken ct = default, bool failOnLimit = false, bool ignoreInaccessible = true)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEntries);
        ArgumentOutOfRangeException.ThrowIfNegative(maxDepth);
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((root, 0));
        var visited = 0;
        while (pending.TryPop(out var current))
        {
            ct.ThrowIfCancellationRequested();
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = false, IgnoreInaccessible = ignoreInaccessible,
                AttributesToSkip = FileAttributes.ReparsePoint, ReturnSpecialDirectories = false,
            };
            using var entries = Directory.EnumerateFileSystemEntries(current.Path, "*", options).GetEnumerator();
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (!entries.MoveNext()) break;
                if (++visited > maxEntries)
                {
                    if (failOnLimit) throw new IOException($"The folder exceeds the {maxEntries:N0}-entry copy limit.");
                    yield break;
                }
                var entry = entries.Current;
                FileAttributes attributes;
                try { attributes = File.GetAttributes(entry); }
                catch (Exception ex) when (ignoreInaccessible && ex is IOException or UnauthorizedAccessException) { continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (current.Depth >= maxDepth)
                    {
                        if (failOnLimit) throw new IOException($"The folder exceeds the {maxDepth}-level copy limit.");
                        continue;
                    }
                    pending.Push((entry, current.Depth + 1));
                }
                else
                {
                    yield return entry;
                }
            }
        }
    }
}
