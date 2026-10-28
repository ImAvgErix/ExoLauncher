using System.Text.Json.Nodes;
using ExoLauncher.Services.DiscordClient;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways keeping Discord's privacy switches off can go wrong:
/// - a switch already off is written again on every connect (Discord's settings saved for nothing), or one left on;
/// - a switch is kept the wrong way round (an opt-out set to false shares instead of refusing);
/// - this PC's language maps to one Discord doesn't offer (it refuses the save), or to the wrong one of two (Spain's
///   Spanish for Mexico's, simplified Chinese for Hong Kong's);
/// - a 24-hour Windows clock reads as 12-hour.
/// </summary>
public sealed class DiscordPrivacyTests
{
    private static JsonObject Values(params (string Key, JsonNode? Value)[] values) =>
        new(values.Select(v => KeyValuePair.Create(v.Key, v.Value)));

    [Fact]
    public void OnlyWhatTheAccountHasOtherwiseChanges()
    {
        var shared = Values(("showCurrentGame", true), ("dropsOptedOut", false), ("quests3pDataOptedOut", false), ("swpMessagePromotionOptedOut", false),
            ("notifyFriendsOnProfileUpdate", true), ("allowGameFriendDmsInDiscord", true), ("locale", "en-US"), ("timestampHourCycle", 0));
        var changes = DiscordSession.PrivacyChanges(shared, "en-US", "h:mm tt");
        Assert.False(changes["showCurrentGame"].GetBoolean());
        Assert.True(changes["dropsOptedOut"].GetBoolean()); // an opt-out: true refuses
        Assert.True(changes["quests3pDataOptedOut"].GetBoolean());
        Assert.True(changes["swpMessagePromotionOptedOut"].GetBoolean());
        Assert.False(changes["notifyFriendsOnProfileUpdate"].GetBoolean());
        Assert.False(changes["allowGameFriendDmsInDiscord"].GetBoolean());
        Assert.Equal(1, changes["timestampHourCycle"].GetInt32());
        Assert.False(changes.ContainsKey("locale")); // already this PC's

        var kept = Values([.. DiscordSession.KeptOff.Select(k => (k.Key, (JsonNode?)JsonValue.Create(k.Value))), ("locale", "de"), ("timestampHourCycle", 2)]);
        Assert.Empty(DiscordSession.PrivacyChanges(kept, "de-DE", "HH:mm"));
    }

    [Theory]
    [InlineData("en-US", "en-US")]
    [InlineData("en-AU", "en-GB")]
    [InlineData("es-ES", "es-ES")]
    [InlineData("es-MX", "es-419")]
    [InlineData("pt-PT", "pt-BR")]
    [InlineData("zh-CN", "zh-CN")]
    [InlineData("zh-HK", "zh-TW")]
    [InlineData("zh-Hant-TW", "zh-TW")]
    [InlineData("nb-NO", "no")]
    [InlineData("de-AT", "de")]
    [InlineData("fr-CA", "fr")]
    [InlineData("sv-FI", "sv-SE")]
    [InlineData("ar-SA", "en-US")]
    [InlineData("", "en-US")]
    public void ThisPcsLanguageAsDiscordOffersIt(string culture, string locale)
    {
        Assert.Equal(locale, DiscordSession.LocaleFor(culture));
        Assert.Contains(locale, DiscordUserSettings.Locales);
    }

    [Theory]
    [InlineData("h:mm tt", 1)]
    [InlineData("hh:mm tt", 1)]
    [InlineData("HH:mm", 2)]
    [InlineData("H:mm", 2)]
    public void WindowsClockToDiscordsTimeFormat(string pattern, int cycle) => Assert.Equal(cycle, DiscordSession.HourCycleFor(pattern));
}
