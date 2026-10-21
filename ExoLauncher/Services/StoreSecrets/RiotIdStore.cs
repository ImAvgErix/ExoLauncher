using ExoLauncher.Adapters;

namespace ExoLauncher.Services;

/// <summary>Public Riot ID (Name#Tag) used to fetch tracker.gg hours.</summary>
internal static class RiotIdStore
{
    internal const string FileName = "riot-id.txt";

    internal static string? TryRead()
    {
        var stored = StoreSecretStore.TryRead(FileName, Normalize);
        if (stored is not null) return stored;
        return RiotIdLocator.TryReadLocal();
    }

    internal static bool Save(string? raw) => StoreSecretStore.Save(FileName, raw, Normalize);

    internal static string? Normalize(string? raw)
    {
        var trimmed = (raw ?? string.Empty).Trim();
        var hash = trimmed.IndexOf('#');
        if (hash <= 0 || hash >= trimmed.Length - 2) return null;
        var name = trimmed[..hash].Trim();
        var tag = trimmed[(hash + 1)..].Trim();
        if (name.Length is < 1 or > 22 || tag.Length is < 2 or > 8) return null;
        if (name.Contains('#') || tag.Contains('#')) return null;
        return name + "#" + tag;
    }
}
