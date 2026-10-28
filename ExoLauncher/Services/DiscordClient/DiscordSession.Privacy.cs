using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services.DiscordClient;

/// <summary>
/// Discord's data and privacy switches as Exo keeps them for every account, set each time Discord connects and changed
/// only where the account has one otherwise: nothing about you goes to Discord to improve, personalize or advertise with
/// (Data &amp; Privacy's two consents and its sponsored-content switches), your activity isn't shared (Share my activity),
/// your messages stay off Discord's web pages, friends aren't told when you change your profile, and friends from games
/// can't message you through Discord. The account's language and time format follow this PC's. Exo's Discord settings
/// show none of them: there is nothing to change.
/// </summary>
internal sealed partial class DiscordSession
{
    /// <summary>The account settings Exo keeps, by Discord's own field names.</summary>
    internal static readonly IReadOnlyDictionary<string, bool> KeptOff = new Dictionary<string, bool>
    {
        ["showCurrentGame"] = false,
        ["dropsOptedOut"] = true,
        ["quests3pDataOptedOut"] = true,
        ["swpMessagePromotionOptedOut"] = true,
        ["notifyFriendsOnProfileUpdate"] = false,
        ["allowGameFriendDmsInDiscord"] = false,
    };

    /// <summary>The consents Data &amp; Privacy asks for (usage statistics, personalization), all kept refused.</summary>
    private static readonly string[] Consents = ["usage_statistics", "personalization"];

    /// <summary>What the account should hold that it doesn't: Exo's kept switches, and this PC's language and time format.</summary>
    internal static Dictionary<string, JsonElement> PrivacyChanges(JsonObject values, string uiCulture, string shortTimePattern)
    {
        var wanted = KeptOff.ToDictionary(kept => kept.Key, kept => (JsonNode?)JsonValue.Create(kept.Value));
        wanted["locale"] = JsonValue.Create(LocaleFor(uiCulture));
        wanted["timestampHourCycle"] = JsonValue.Create(HourCycleFor(shortTimePattern));
        return wanted.Where(want => !JsonNode.DeepEquals(values[want.Key], want.Value))
            .ToDictionary(want => want.Key, want => JsonSerializer.SerializeToElement(want.Value));
    }

    /// <summary>
    /// The Discord language nearest this PC's display language: the same one, else its language's (Discord's Spanish for
    /// Latin America for every Spanish but Spain's, its Brazilian Portuguese for every Portuguese, British English for
    /// English outside the US), else US English.
    /// </summary>
    internal static string LocaleFor(string culture)
    {
        var locales = DiscordUserSettings.Locales;
        if (locales.FirstOrDefault(l => l.Equals(culture, StringComparison.OrdinalIgnoreCase)) is { } exact) return exact;
        var parts = culture.Split('-');
        var language = parts[0].ToLowerInvariant();
        return language switch
        {
            "en" => "en-GB",
            "es" => "es-419",
            "pt" => "pt-BR",
            "sv" => "sv-SE",
            "nb" or "nn" or "no" => "no",
            "zh" => parts.Skip(1).Any(p => p.Equals("Hant", StringComparison.OrdinalIgnoreCase) || p.Equals("TW", StringComparison.OrdinalIgnoreCase)
                || p.Equals("HK", StringComparison.OrdinalIgnoreCase) || p.Equals("MO", StringComparison.OrdinalIgnoreCase)) ? "zh-TW" : "zh-CN",
            _ => locales.FirstOrDefault(l => l.Equals(language, StringComparison.OrdinalIgnoreCase)) ?? "en-US",
        };
    }

    /// <summary>Discord's time format for this PC's: 24-hour when Windows writes hours with H, else 12-hour.</summary>
    internal static int HourCycleFor(string shortTimePattern) => shortTimePattern.Contains('H', StringComparison.Ordinal) ? 2 : 1;

    private async Task KeepPrivacyAsync()
    {
        try
        {
            var (values, error) = await UserSettingsAsync(null, _stop.Token).ConfigureAwait(false);
            if (values is null) { AppLog.Info("Discord client: privacy defaults not checked (" + error + ")."); return; }
            var changes = PrivacyChanges(values, CultureInfo.CurrentUICulture.Name, CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern);
            if (changes.Count > 0)
            {
                var (_, saveError) = await UserSettingsAsync(changes, _stop.Token).ConfigureAwait(false);
                AppLog.Info(saveError is null
                    ? "Discord client: kept off or set for this PC: " + string.Join(", ", changes.Keys) + "."
                    : "Discord client: privacy defaults not saved (" + saveError + ").");
            }
            var consent = await Rest.ConsentAsync(_stop.Token).ConfigureAwait(false);
            if (consent.Body is { ValueKind: JsonValueKind.Object } body)
            {
                var given = Consents.Where(kind => body.TryGetProperty(kind, out var c) && c.ValueKind == JsonValueKind.Object
                    && c.TryGetProperty("consented", out var on) && on.ValueKind == JsonValueKind.True).ToList();
                if (given.Count > 0)
                {
                    var revoked = await Rest.SetConsentAsync([], given, _stop.Token).ConfigureAwait(false);
                    AppLog.Info(revoked.Error is null ? "Discord client: data use refused (" + string.Join(", ", given) + ")." : "Discord client: data use not refused (" + revoked.Error + ").");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException) { AppLog.Info("Discord client: privacy defaults not kept (" + ex.GetType().Name + ")."); }
    }
}
