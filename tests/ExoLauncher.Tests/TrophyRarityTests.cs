using ExoLauncher.Models;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class TrophyRarityTests
{
    [Theory]
    [InlineData("bronze", 1d, false, TrophyRarity.Bronze)]
    [InlineData("silver", 1d, false, TrophyRarity.Silver)]
    [InlineData("gold", 90d, false, TrophyRarity.Gold)]
    [InlineData("platinum", 90d, false, TrophyRarity.Platinum)]
    [InlineData(null, 5d, false, TrophyRarity.Gold)]
    [InlineData(null, 5.01d, false, TrophyRarity.Silver)]
    [InlineData(null, 20d, false, TrophyRarity.Silver)]
    [InlineData(null, 20.01d, false, TrophyRarity.Bronze)]
    [InlineData(null, null, false, TrophyRarity.Unknown)]
    [InlineData("gold", 90d, true, TrophyRarity.Platinum)]
    public void ResolverUsesTierThenTrustworthyGlobalRarityThenPerfection(
        string? tier, double? globalPercent, bool perfected, TrophyRarity expected)
    {
        var definition = Definition(tier, globalPercent);

        Assert.Equal(expected, TrophyRarityResolver.Resolve(definition, perfected));
    }

    [Theory]
    [InlineData(TrophyRarity.Unknown, "UNLOCKED")]
    [InlineData(TrophyRarity.Bronze, "BRONZE")]
    [InlineData(TrophyRarity.Silver, "SILVER")]
    [InlineData(TrophyRarity.Gold, "GOLD")]
    [InlineData(TrophyRarity.Platinum, "PLATINUM")]
    public void LabelsAreStableForTheNativeAndWebPreviews(TrophyRarity rarity, string expected) =>
        Assert.Equal(expected, TrophyRarityResolver.Label(rarity));

    private static AchievementDefinition Definition(string? tier, double? globalPercent) => new()
    {
        ProviderId = "epic",
        SourceGameId = "sample",
        ExternalId = "ACH_SAMPLE",
        Name = "Sample",
        Tier = tier,
        GlobalUnlockPercent = globalPercent,
    };
}
