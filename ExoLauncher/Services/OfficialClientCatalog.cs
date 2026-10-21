using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>
/// Minecraft and Roblox are official clients Exo can hand off to even when
/// they are not in the local library yet. This is the only catalog surface
/// the host keeps: no live store search, no shelf.
/// </summary>
internal static class OfficialClientCatalog
{
    internal static bool IsOfficialClientCatalogInstall(StoreKind store, string? id, bool canInstall) =>
        canInstall &&
        store is StoreKind.Minecraft or StoreKind.Roblox &&
        id is "minecraft:java" or "minecraft:bedrock" or "roblox:player";

    internal static bool IsOfficialClientCatalogInstall(GameEntry game) =>
        IsOfficialClientCatalogInstall(game.Store, game.Id, game.CanInstall) &&
        !game.Installed &&
        game.EntitlementState is not (EntitlementState.NotOwned or EntitlementState.Unverified);

    internal static GameEntry? TrySynthesizeOfficialClientInstall(string gameId, string? title)
    {
        var id = (gameId ?? "").Trim();
        if (id.Equals("minecraft:java", StringComparison.OrdinalIgnoreCase))
        {
            return new GameEntry
            {
                Id = "minecraft:java",
                Title = string.IsNullOrWhiteSpace(title) || title == id ? "Minecraft" : title.Trim(),
                Store = StoreKind.Minecraft,
                Installed = false,
                Owned = false,
                CanInstall = true,
                LaunchTarget = "minecraft:java",
                Status = "Catalog",
                LaunchNote = "Opens the official Minecraft download.",
            };
        }
        if (id.Equals("minecraft:bedrock", StringComparison.OrdinalIgnoreCase))
        {
            return new GameEntry
            {
                Id = "minecraft:bedrock",
                Title = string.IsNullOrWhiteSpace(title) || title == id ? "Minecraft Bedrock" : title.Trim(),
                Store = StoreKind.Minecraft,
                Installed = false,
                Owned = false,
                CanInstall = true,
                LaunchTarget = "minecraft:bedrock",
                Status = "Catalog",
                LaunchNote = "Opens Minecraft Bedrock in the Microsoft Store.",
            };
        }
        if (id.Equals("roblox:player", StringComparison.OrdinalIgnoreCase))
        {
            return new GameEntry
            {
                Id = "roblox:player",
                Title = string.IsNullOrWhiteSpace(title) || title == id ? "Roblox" : title.Trim(),
                Store = StoreKind.Roblox,
                Installed = false,
                Owned = false,
                CanInstall = true,
                LaunchTarget = "9PMF91N3LZ3M",
                Status = "Catalog",
                LaunchNote = "Opens Roblox in the Microsoft Store.",
            };
        }
        return null;
    }
}
