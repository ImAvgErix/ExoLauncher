using System.Text.Json;
using ExoLauncher.Models;

namespace ExoLauncher.Adapters;

/// <summary>
/// Owned / session snapshots the official client already wrote on this PC.
/// Install and update still go through that client.
/// </summary>
internal static class LocalOwnedCatalog
{
    internal static bool HasRiotSession(string? yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml)) return false;
        using var reader = new StringReader(yaml);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("refresh_token:", StringComparison.Ordinal) ||
                trimmed.StartsWith("id_token:", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    internal static IReadOnlyList<GameEntry> ParseBattleNetConfig(string? json)
    {
        var games = new List<GameEntry>();
        if (string.IsNullOrWhiteSpace(json)) return games;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!TryGetGamesObject(document.RootElement, out var gamesElement))
                return games;

            foreach (var property in gamesElement.EnumerateObject())
            {
                var uid = property.Name.Trim();
                if (string.IsNullOrWhiteSpace(uid) ||
                    uid.Equals("battle_net", StringComparison.OrdinalIgnoreCase) ||
                    uid.Equals("battlenet", StringComparison.OrdinalIgnoreCase))
                    continue;
                var title = BattleNetTitle(uid);
                games.Add(Uninstalled(
                    "battlenet:" + Sanitize(uid),
                    title,
                    StoreKind.BattleNet,
                    uid));
            }
        }
        catch (JsonException)
        {
            return Array.Empty<GameEntry>();
        }

        return games;
    }

    internal static IReadOnlyList<GameEntry> ParseNamedGameArray(
        string? json,
        StoreKind store,
        string idPrefix,
        params string[] arrayNames)
    {
        var games = new List<GameEntry>();
        if (string.IsNullOrWhiteSpace(json)) return games;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!TryGetNamedArray(document.RootElement, arrayNames, out var array))
                return games;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var id = FirstString(item, "id", "offerId", "contentId", "softwareId", "installId",
                    "productId", "spaceId", "gameId", "uid", "appId");
                var title = FirstString(item, "title", "displayName", "gameDisplayName", "name");
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title)) continue;
                var rowId = idPrefix + Sanitize(id);
                if (!seen.Add(rowId)) continue;
                games.Add(Uninstalled(rowId, title, store, id));
            }
        }
        catch (JsonException)
        {
            return Array.Empty<GameEntry>();
        }

        return games;
    }

    internal static bool HasMinecraftJavaAccount(string? launcherAccountsJson)
    {
        if (string.IsNullOrWhiteSpace(launcherAccountsJson)) return false;
        try
        {
            using var document = JsonDocument.Parse(launcherAccountsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("accounts", out var accounts) ||
                accounts.ValueKind != JsonValueKind.Object)
                return false;

            foreach (var account in accounts.EnumerateObject())
            {
                if (account.Value.ValueKind != JsonValueKind.Object) continue;
                if (account.Value.TryGetProperty("minecraftProfile", out var profile) &&
                    profile.ValueKind == JsonValueKind.Object)
                    return true;
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    internal static GameEntry MinecraftJavaOwned() =>
        Uninstalled(
            "minecraft:java",
            "Minecraft",
            StoreKind.Minecraft,
            "minecraft:java");

    private static GameEntry Uninstalled(
        string id,
        string title,
        StoreKind store,
        string launchTarget) => new()
    {
        Id = id,
        Title = title.Trim(),
        Store = store,
        Installed = false,
        Owned = false,
        EntitlementState = EntitlementState.Unverified,
        CanInstall = false,
        LaunchTarget = launchTarget,
        Status = "Ownership unverified",
        LaunchNote = "Saved client catalog entry. Check ownership in the official client.",
    };

    private static bool TryGetGamesObject(JsonElement root, out JsonElement games)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("Games", out games) && games.ValueKind == JsonValueKind.Object)
                return true;
            if (root.TryGetProperty("games", out games) && games.ValueKind == JsonValueKind.Object)
                return true;
            if (root.TryGetProperty("Client", out var client) &&
                client.ValueKind == JsonValueKind.Object &&
                client.TryGetProperty("Install", out var install) &&
                install.ValueKind == JsonValueKind.Object &&
                install.TryGetProperty("Games", out games) &&
                games.ValueKind == JsonValueKind.Object)
                return true;
        }

        games = default;
        return false;
    }

    private static bool TryGetNamedArray(JsonElement root, string[] names, out JsonElement array)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            array = root;
            return true;
        }

        foreach (var name in names)
        {
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty(name, out array) &&
                array.ValueKind == JsonValueKind.Array)
                return true;
        }

        array = default;
        return false;
    }

    private static string? FirstString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(value.GetString()))
                return value.GetString();
            if (value.ValueKind == JsonValueKind.Number)
                return value.ToString();
        }

        return null;
    }

    private static string BattleNetTitle(string uid) => uid.ToLowerInvariant() switch
    {
        "wow" => "World of Warcraft",
        "wow_classic" or "wow_classic_era" => "World of Warcraft Classic",
        "d3" => "Diablo III",
        "fenris" => "Diablo IV",
        "osi" => "Diablo II: Resurrected",
        "anbs" => "Diablo Immortal",
        "s2" => "StarCraft II",
        "w3" => "Warcraft III",
        "hs_beta" or "hs_live" or "hsb" => "Hearthstone",
        "hero" => "Heroes of the Storm",
        "prometheus" or "orisa" => "Overwatch 2",
        "odin" or "viper" or "auks" => "Call of Duty",
        _ => uid,
    };

    private static string Sanitize(string value)
    {
        var chars = value.Select(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-').ToArray();
        return new string(chars).Trim('-');
    }
}
