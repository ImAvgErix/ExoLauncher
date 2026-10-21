using ExoLauncher.Models;

namespace ExoLauncher.Adapters;

/// <summary>Installed rows win. Owned-uninstalled rows fill the gaps.</summary>
internal static class OwnedLibraryMerge
{
    internal static IReadOnlyList<GameEntry> Merge(
        IReadOnlyList<GameEntry> installed,
        IEnumerable<GameEntry> owned)
    {
        var rows = new List<GameEntry>(installed);
        var seen = new HashSet<string>(installed.Select(game => game.Id), StringComparer.OrdinalIgnoreCase);
        foreach (var game in owned)
        {
            if (string.IsNullOrWhiteSpace(game.Id) || !seen.Add(game.Id)) continue;
            rows.Add(game);
        }

        return rows;
    }
}
