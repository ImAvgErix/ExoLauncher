using ExoLauncher.Models;

namespace ExoLauncher.Adapters;

/// <summary>Reads official-client owned catalogs from their usual Windows paths.</summary>
internal static class LocalOwnedSources
{
    internal static IReadOnlyList<GameEntry> BattleNet() =>
        LocalOwnedCatalog.ParseBattleNetConfig(FirstExistingText(
            Roaming("Battle.net", "Battle.net.config"),
            Local("Battle.net", "Battle.net.config")));

    internal static IReadOnlyList<GameEntry> Ea() =>
        FirstNonEmpty(
            LocalOwnedCatalog.ParseNamedGameArray(
                FirstExistingText(
                    Local("Electronic Arts", "EA Desktop", "entitlements.json"),
                    ProgramData("EA Desktop", "entitlements.json"),
                    ProgramData("Electronic Arts", "EA Desktop", "entitlements.json")),
                StoreKind.Ea,
                "ea:",
                "entitlements", "offers", "games"));

    internal static IReadOnlyList<GameEntry> Ubisoft() =>
        LocalOwnedCatalog.ParseNamedGameArray(
            FirstExistingText(
                Local("Ubisoft Game Launcher", "ownership.json"),
                Local("Ubisoft Game Launcher", "cache", "ownership.json")),
            StoreKind.Ubisoft,
            "ubisoft:",
            "games", "ownership", "titles");

    internal static IReadOnlyList<GameEntry> Xbox() =>
        LocalOwnedCatalog.ParseNamedGameArray(
            FirstExistingText(XboxCatalogCandidates()),
            StoreKind.Xbox,
            "xbox:",
            "games", "titles", "products");

    internal static IReadOnlyList<GameEntry> Minecraft()
    {
        var accounts = LocalStoreSessions.ReadTextIfExists(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            ".minecraft",
            "launcher_accounts.json");
        return LocalOwnedCatalog.HasMinecraftJavaAccount(accounts)
            ? [LocalOwnedCatalog.MinecraftJavaOwned()]
            : Array.Empty<GameEntry>();
    }

    internal static IReadOnlyList<GameEntry> Paradox() =>
        LocalOwnedCatalog.ParseNamedGameArray(
            FirstExistingText(
                Local("Paradox Interactive", "launcher-v2", "game-data.json"),
                Local("Paradox Interactive", "launcher", "game-data.json")),
            StoreKind.Paradox,
            "paradox:",
            "games", "owned", "titles");

    internal static IReadOnlyList<GameEntry> Rockstar() =>
        LocalOwnedCatalog.ParseNamedGameArray(
            FirstExistingText(
                Local("Rockstar Games", "Launcher", "titles.json"),
                Local("Rockstar Games", "Launcher", "title.json")),
            StoreKind.Rockstar,
            "rockstar:",
            "titles", "games");

    internal static IReadOnlyList<GameEntry> Wargaming() =>
        LocalOwnedCatalog.ParseNamedGameArray(
            FirstExistingText(
                Local("Wargaming.net", "GameCenter", "library.json"),
                ProgramData("Wargaming.net", "GameCenter", "library.json")),
            StoreKind.Wargaming,
            "wargaming:",
            "games", "titles");

    private static IReadOnlyList<GameEntry> FirstNonEmpty(IReadOnlyList<GameEntry> rows) => rows;

    private static IEnumerable<string> XboxCatalogCandidates()
    {
        var packages = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Packages");
        if (!Directory.Exists(packages)) yield break;
        foreach (var prefix in new[] { "Microsoft.GamingApp_*", "Microsoft.XboxApp_*" })
        {
            string[] dirs;
            try { dirs = Directory.GetDirectories(packages, prefix); }
            catch { continue; }
            foreach (var dir in dirs)
            {
                yield return Path.Combine(dir, "LocalState", "owned.json");
                yield return Path.Combine(dir, "LocalState", "library.json");
            }
        }
    }

    private static string? FirstExistingText(params string[] paths)
    {
        foreach (var path in paths)
        {
            var text = LocalStoreSessions.ReadTextIfExists(path);
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }

        return null;
    }

    private static string? FirstExistingText(IEnumerable<string> paths) =>
        FirstExistingText(paths.ToArray());

    private static string Roaming(params string[] parts) =>
        Path.Combine(new[] { Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) }.Concat(parts).ToArray());

    private static string Local(params string[] parts) =>
        Path.Combine(new[] { Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) }.Concat(parts).ToArray());

    private static string ProgramData(params string[] parts) =>
        Path.Combine(new[] { Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData) }.Concat(parts).ToArray());
}
