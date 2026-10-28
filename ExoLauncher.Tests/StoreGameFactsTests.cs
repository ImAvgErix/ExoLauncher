using System.Text.Json;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways a store page's facts could read wrong without anything failing (answers are Steam's and CheapShark's own,
/// trimmed, as seen on Oct 27, 2026):
/// - the DRM notice keeps Steam's &lt;br&gt; as a tag, or runs its two notices together;
/// - the account notice keeps Steam's aside about linking, so the cell is cut before the account;
/// - the full-audio footnote counts as a language, or the asterisks stay on the names;
/// - partial controller support reads as full, or PlayStation support named only by its category ids is missed;
/// - a game with none of these shows a fact anyway;
/// - Recent reviews under ten (Steam gives no score: "3 user reviews") read as a verdict, the share is rounded up past
///   Steam's own, or an answer without a summary reads as no reviews instead of no answer;
/// - CheapShark's lowest price is read in the wrong unit, or a game without one gets a $0 lowest.
/// </summary>
public sealed class StoreGameFactsTests
{
    private const string Shadows = """
        {
          "name": "Assassin's Creed Shadows",
          "drm_notice": "Denuvo Anti-tamper<br>5 a day machine activation limit",
          "ext_user_account_notice": "Ubisoft Account (Supports Linking to Steam Account)",
          "supported_languages": "English<strong>*</strong>, French<strong>*</strong>, Arabic, Japanese<strong>*</strong>, Simplified Chinese<br><strong>*</strong>languages with full audio support",
          "controller_support": null,
          "categories": [{ "id": 2, "description": "Single-player" }, { "id": 57, "description": "DualSense Controller Support" }, { "id": 18, "description": "Partial Controller Support" }]
        }
        """;

    private const string EldenRing = """
        {
          "name": "ELDEN RING",
          "drm_notice": null,
          "supported_languages": "English<strong>*</strong>, French, Thai<br><strong>*</strong>languages with full audio support",
          "controller_support": "full",
          "categories": [{ "id": 28, "description": "Full controller support" }, { "id": 23, "description": "Steam Cloud" }]
        }
        """;

    private static StoreService.GameDetail Detail(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return StoreService.ReadDetail("1", doc.RootElement.Clone(), null, new Dictionary<int, string>());
    }

    private static T? Parse<T>(string json, Func<JsonElement, T?> read) where T : class
    {
        using var doc = JsonDocument.Parse(json);
        return read(doc.RootElement);
    }

    [Fact]
    public void ShadowsNamesItsDrmAccountPartialControllerAndLanguages()
    {
        var game = Detail(Shadows);

        Assert.Equal("Denuvo Anti-tamper, 5 a day machine activation limit", game.Drm);
        Assert.Equal("Ubisoft Account", game.Account);
        Assert.Equal(("partial", true), (game.Controller, game.PlayStation));
        Assert.Equal(["English", "French", "Arabic", "Japanese", "Simplified Chinese"], game.Languages);
    }

    [Fact]
    public void EldenRingHasFullControllerSupportAndNoNotices()
    {
        var game = Detail(EldenRing);

        Assert.Null(game.Drm);
        Assert.Null(game.Account);
        Assert.Equal(("full", false), (game.Controller, game.PlayStation));
        Assert.Equal(["English", "French", "Thai"], game.Languages);
    }

    [Fact]
    public void AGameSteamSaysNothingAboutHasNoFacts()
    {
        var game = Detail("""{ "name": "Quiet", "drm_notice": "  ", "ext_user_account_notice": "" }""");

        Assert.Null(game.Drm);
        Assert.Null(game.Account);
        Assert.Null(game.Controller);
        Assert.False(game.PlayStation);
        Assert.Empty(game.Languages);
    }

    [Fact]
    public void RecentReviewsTakeSteamsWordsAndRoundDown()
    {
        var recent = Parse("""{ "response": { "query_summary": { "num_reviews": 1, "review_score": 8, "review_score_desc": "Very Positive", "total_positive": 874, "total_negative": 58, "total_reviews": 932 } } }""",
            StoreService.ReadRecentReviews);

        // 874 of 932 is 93.8%: Steam says 93.
        Assert.Equal(new StoreService.RecentReviews("Very Positive", 93, 932), recent);
    }

    [Fact]
    public void FewRecentReviewsHaveNoScoreAndNoneAreNoneNotMissing()
    {
        var few = Parse("""{ "response": { "query_summary": { "review_score": 0, "review_score_desc": "3 user reviews", "total_positive": 2, "total_negative": 1, "total_reviews": 3 } } }""",
            StoreService.ReadRecentReviews);
        var none = Parse("""{ "response": { "query_summary": { "review_score": 0, "review_score_desc": "No user reviews", "total_positive": 0, "total_negative": 0, "total_reviews": 0 } } }""",
            StoreService.ReadRecentReviews);
        var noSummary = Parse("""{ "response": { "query_summary": { "num_reviews": 1 }, "reviews": [] } }""", StoreService.ReadRecentReviews);

        Assert.Equal(new StoreService.RecentReviews(null, 66, 3), few);
        Assert.Equal(new StoreService.RecentReviews(null, 0, 0), none);
        Assert.Null(noSummary);
    }

    [Fact]
    public void CheapSharksLowestIsInCentsWithItsDay()
    {
        var lowest = Parse("""{ "info": { "title": "Hades II" }, "cheapestPriceEver": { "price": "20.99", "date": 1782413542 }, "deals": [] }""", StoreService.ReadLowest);
        var none = Parse("""{ "info": { "title": "Hades II" }, "deals": [] }""", StoreService.ReadLowest);
        var free = Parse("""{ "cheapestPriceEver": { "price": "0.00", "date": 1782413542 } }""", StoreService.ReadLowest);

        Assert.Equal(new StoreService.Lowest(2099, DateTimeOffset.FromUnixTimeSeconds(1782413542)), lowest);
        Assert.Null(none);
        Assert.Null(free);
    }
}
