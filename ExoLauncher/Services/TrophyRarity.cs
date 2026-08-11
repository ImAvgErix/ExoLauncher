using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>
/// A small, source-agnostic trophy hierarchy. Provider tiers win over global
/// rarity; a completed catalog is the only synthetic Platinum promotion.
/// </summary>
public enum TrophyRarity
{
    Unknown,
    Bronze,
    Silver,
    Gold,
    Platinum,
}

public static class TrophyRarityResolver
{
    public static TrophyRarity Resolve(AchievementDefinition definition, bool isPerfected)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (isPerfected) return TrophyRarity.Platinum;

        var tier = definition.Tier?.Trim().ToLowerInvariant();
        var fromTier = tier switch
        {
            "bronze" => TrophyRarity.Bronze,
            "silver" => TrophyRarity.Silver,
            "gold" => TrophyRarity.Gold,
            "platinum" => TrophyRarity.Platinum,
            _ => TrophyRarity.Unknown,
        };
        if (fromTier != TrophyRarity.Unknown) return fromTier;

        return definition.GlobalUnlockPercent switch
        {
            <= 5d => TrophyRarity.Gold,
            <= 20d => TrophyRarity.Silver,
            >= 0d => TrophyRarity.Bronze,
            _ => TrophyRarity.Unknown,
        };
    }

    public static string Label(TrophyRarity rarity) => rarity switch
    {
        TrophyRarity.Bronze => "BRONZE",
        TrophyRarity.Silver => "SILVER",
        TrophyRarity.Gold => "GOLD",
        TrophyRarity.Platinum => "PLATINUM",
        _ => "UNLOCKED",
    };
}
