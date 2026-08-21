using ExoLauncher.Models;

namespace ExoLauncher.Adapters;

/// <summary>
/// Official storefront destinations. Exo never hosts checkout. Local / portable
/// titles have no purchasable equivalent.
///
/// One native source of truth for storefront destinations. React receives the
/// resulting URL through the bounded game DTO and does not rebuild store links.
/// </summary>
internal static class Storefront
{
    /// <summary>
    /// URL/protocol the Buy action should open, or null when Buy must not appear.
    /// Owned titles use their library action instead. An installed title is
    /// purchasable only when current-account ownership was explicitly rejected;
    /// unavailable verification must not be mislabeled as a refund.
    /// </summary>
    public static string? BuyUrl(GameEntry game)
    {
        ArgumentNullException.ThrowIfNull(game);
        // CanInstall is a capability, not proof of entitlement. A stale local
        // row may carry CanInstall=true after a refund; unowned titles must
        // remain purchasable instead of hiding Buy behind a false Download.
        if (game.EntitlementState == EntitlementState.Unverified) return null;
        if (game.EntitlementState == EntitlementState.NotOwned) return Destination(game);
        if (game.Installed || game.Owned) return null;
        return Destination(game);
    }

    /// <summary>Official destination even when Buy is hidden (tests / Settings).</summary>
    public static string? Destination(GameEntry game)
    {
        ArgumentNullException.ThrowIfNull(game);
        var target = (game.LaunchTarget ?? "").Trim();
        var title = (game.Title ?? "").Trim();
        return game.Store switch
        {
            StoreKind.Steam => SteamDestination(target, game.Id),
            StoreKind.Epic => EpicDestination(game.Id, target),
            StoreKind.Gog => GogDestination(target),
            StoreKind.Riot => RiotDestination(target, game.Id),
            StoreKind.Xbox => XboxDestination(target, title),
            StoreKind.Ea => SearchOrHome(
                title, "https://www.ea.com/games", "https://www.ea.com/search?q="),
            StoreKind.Ubisoft => UbisoftDestination(target, title),
            StoreKind.BattleNet => SearchOrHome(
                title, "https://us.shop.battle.net/", "https://us.shop.battle.net/en-us/search?q="),
            StoreKind.Amazon => SearchOrHome(
                title, "https://gaming.amazon.com/", "https://gaming.amazon.com/home?query="),
            StoreKind.Rockstar => SearchOrHome(
                title, "https://store.rockstargames.com/", "https://store.rockstargames.com/search?query="),
            StoreKind.Itch => ItchDestination(title),
            StoreKind.Minecraft => "https://www.minecraft.net/get-minecraft",
            StoreKind.Roblox => "https://www.roblox.com/",
            StoreKind.Paradox => SearchOrHome(
                title,
                "https://www.paradoxinteractive.com/games",
                "https://www.paradoxinteractive.com/games?search="),
            StoreKind.Wargaming => SearchOrHome(
                title, "https://wargaming.com/", "https://wargaming.com/en/search/?q="),
            StoreKind.Local => null,
            _ => null,
        };
    }

    public static bool HasPurchasableStorefront(StoreKind store) => store != StoreKind.Local;

    public static string? UnavailableReason(StoreKind store) =>
        store == StoreKind.Local
            ? "Portable games are not sold through a storefront."
            : null;

    internal static string? SteamDestination(string launchTarget, string id)
    {
        if (LooksLikeSteamAppId(launchTarget))
            return "steam://store/" + launchTarget;
        if (id.StartsWith("steam:", StringComparison.OrdinalIgnoreCase))
        {
            var tail = id["steam:".Length..];
            if (LooksLikeSteamAppId(tail)) return "steam://store/" + tail;
        }
        return "https://store.steampowered.com/";
    }

    internal static string? EpicDestination(string id, string launchTarget)
    {
        if (id.StartsWith("epic:catalog:", StringComparison.OrdinalIgnoreCase) && launchTarget.Length > 0)
            return "https://store.epicgames.com/en-US/p/" + Uri.EscapeDataString(launchTarget);
        if (launchTarget.Length > 0 && !LooksLikeFilesystemTarget(launchTarget))
            return "https://store.epicgames.com/en-US/p/" + Uri.EscapeDataString(launchTarget);
        return "https://store.epicgames.com/";
    }

    internal static string? GogDestination(string launchTarget) =>
        launchTarget.Length > 0 && !LooksLikeFilesystemTarget(launchTarget)
            ? "https://www.gog.com/en/game/" + Uri.EscapeDataString(launchTarget)
            : "https://www.gog.com/";

    internal static string RiotDestination(string launchTarget, string id)
    {
        var product = launchTarget;
        if (string.IsNullOrWhiteSpace(product) &&
            id.StartsWith("riot:", StringComparison.OrdinalIgnoreCase))
            product = id["riot:".Length..];
        return product.Trim().ToLowerInvariant() switch
        {
            "valorant" => "https://playvalorant.com/",
            "league_of_legends" => "https://www.leagueoflegends.com/",
            "bacon" => "https://playruneterra.com/",
            "lion" => "https://2xko.riotgames.com/",
            _ => "https://www.riotgames.com/",
        };
    }

    internal static string XboxDestination(string launchTarget, string title)
    {
        if (LooksLikeMicrosoftStoreId(launchTarget))
            return "ms-windows-store://pdp/?ProductId=" + Uri.EscapeDataString(launchTarget);
        return SearchOrHome(title, "ms-windows-store://", "ms-windows-store://search/?query=");
    }

    internal static string UbisoftDestination(string launchTarget, string title)
        => SearchOrHome(title, "https://store.ubisoft.com/", "https://store.ubisoft.com/search?q=");

    internal static string ItchDestination(string title) =>
        SearchOrHome(title, "https://itch.io/", "https://itch.io/search?q=");

    internal static bool LooksLikeSteamAppId(string value) =>
        value.Length is >= 1 and <= 10 && value.All(char.IsDigit);

    internal static bool LooksLikeMicrosoftStoreId(string value) =>
        value.Length is >= 12 and <= 16 &&
        (value[0] is '9' or 'X') &&
        value.All(char.IsLetterOrDigit);

    internal static bool LooksLikeFilesystemTarget(string value) =>
        value.IndexOfAny(['\\', '/']) >= 0 ||
        value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("shell:", StringComparison.OrdinalIgnoreCase);

    private static string SearchOrHome(string title, string home, string searchPrefix) =>
        string.IsNullOrWhiteSpace(title) ? home : searchPrefix + Uri.EscapeDataString(title);
}
