namespace ExoLauncher.Helpers;

/// <summary>
/// The files and folders the user chose in Windows' own pickers. A path the page names must have come from one of
/// those, once and soon: a page that is no longer trustworthy can't add a game from a path it made up.
/// </summary>
internal static class PickedPaths
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private const int Capacity = 16;

    private static readonly List<(string Path, DateTime At)> Picked = [];

    internal static void Remember(string? path, DateTime? now = null)
    {
        if (Normalize(path) is not { } key) return;
        lock (Picked)
        {
            Picked.RemoveAll(p => string.Equals(p.Path, key, StringComparison.OrdinalIgnoreCase));
            Picked.Add((key, now ?? DateTime.UtcNow));
            if (Picked.Count > Capacity) Picked.RemoveRange(0, Picked.Count - Capacity);
        }
    }

    /// <summary>True once for a path the user picked within <see cref="Lifetime"/>; the pick is used up.</summary>
    internal static bool Consume(string? path, DateTime? now = null)
    {
        if (Normalize(path) is not { } key) return false;
        var at = now ?? DateTime.UtcNow;
        lock (Picked)
        {
            Picked.RemoveAll(p => at - p.At > Lifetime);
            return Picked.RemoveAll(p => string.Equals(p.Path, key, StringComparison.OrdinalIgnoreCase)) > 0;
        }
    }

    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.GetFullPath(path.Trim()).TrimEnd('\\', '/'); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }
}
