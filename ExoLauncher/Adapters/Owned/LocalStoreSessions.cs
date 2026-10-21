namespace ExoLauncher.Adapters;

/// <summary>
/// Detects a persisted official-client login without reading token values.
/// </summary>
internal static class LocalStoreSessions
{
    internal static bool HasSession(string storeId) =>
        storeId.ToLowerInvariant() switch
        {
            "riot" => RiotPrivateSettingsExist(),
            "itch" => Services.ItchApiKeyStore.HasKey(),
            // A client folder, config file, or cookie container does not prove
            // a current account. These clients keep account verification in their own UI.
            "battlenet" or "ea" or "ubisoft" or "xbox" or "minecraft"
                or "paradox" or "rockstar" or "roblox" or "wargaming" => false,
            _ => false,
        };

    internal static string? ReadTextIfExists(params string[] parts)
    {
        var path = Path.Combine(parts);
        try { return File.Exists(path) && new FileInfo(path).Length <= 8 * 1024 * 1024 ? File.ReadAllText(path) : null; }
        catch { return null; }
    }

    internal static bool RiotPrivateSettingsExist()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Riot Games", "Riot Client", "Data", "RiotGamesPrivateSettings.yaml");
        try
        {
            if (!File.Exists(path)) return false;
            return LocalOwnedCatalog.HasRiotSession(File.ReadAllText(path));
        }
        catch
        {
            return false;
        }
    }

}
