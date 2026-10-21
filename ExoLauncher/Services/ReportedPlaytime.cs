using ExoLauncher.Adapters;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>
/// Which reported figure a library entry shows. One rule: the entry's own store's lifetime figure (Steam, GOG, Epic, Xbox)
/// when it has one, else GOG Galaxy's figure for that store's game, else a
/// tracker's (tracker.gg). Figures are never added together, and a lower tier
/// never shows when a higher one has hours.
/// </summary>
internal static class ReportedPlaytime
{
    internal enum Source { Store, Xbox, Galaxy, Tracker }

    internal sealed record Reading(int Minutes, Source From);

    internal const string MinecraftBedrockPfn = "Microsoft.MinecraftUWP_8wekyb3d8bbwe";

    internal static Reading? Pick(Reading? native, Reading? galaxy, Reading? tracker)
    {
        if (native is { Minutes: > 0 }) return native;
        if (galaxy is { Minutes: > 0 }) return galaxy;
        return tracker is { Minutes: > 0 } ? tracker : null;
    }

    /// <summary>
    /// Xbox's figure for an Xbox entry or Minecraft (Bedrock): the title whose
    /// package family name is the entry's package, else the title with the
    /// entry's exact title. Entries from other stores never take Xbox hours.
    /// </summary>
    internal static XboxPlaytime.TitleHours? XboxFor(
        GameEntry game, IReadOnlyList<XboxPlaytime.TitleHours> titles, string? packageIdentity)
    {
        if (titles.Count == 0) return null;
        var bedrock = game.Store == StoreKind.Minecraft &&
                      game.Id.Equals("minecraft:bedrock", StringComparison.OrdinalIgnoreCase);
        if (game.Store != StoreKind.Xbox && !bedrock) return null;

        if (bedrock) packageIdentity = MinecraftBedrockPfn.Split('_')[0];
        if (!string.IsNullOrWhiteSpace(packageIdentity))
        {
            var byPackage = titles.FirstOrDefault(title =>
                title.Pfn is not null &&
                (title.Pfn.Equals(packageIdentity, StringComparison.OrdinalIgnoreCase) ||
                 title.Pfn.StartsWith(packageIdentity + "_", StringComparison.OrdinalIgnoreCase)));
            if (byPackage is not null) return byPackage;
        }
        if (bedrock) return null;

        var key = CatalogHygiene.NormalizeTitleKey(game.Title);
        if (key.Length == 0) return null;
        return titles.Where(title => CatalogHygiene.NormalizeTitleKey(title.Name) == key).MaxBy(title => title.Minutes);
    }
}
