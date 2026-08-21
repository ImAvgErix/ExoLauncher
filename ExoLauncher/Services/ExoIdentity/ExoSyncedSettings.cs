using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>
/// Portable fields that may leave this PC, split the way
/// <c>services/exo-id/CONTRACT.md</c> splits them: <c>/v1/profile</c> vs
/// <c>/v1/sync</c>. Everything else on <see cref="AppSettings"/> stays here —
/// including install roots, launch working directories, window/trophy pixel
/// anchors, and local filenames. Unclassified new settings keys default to
/// the denylist. <c>profileHandle</c> is server-owned.
/// </summary>
internal static class ExoSyncedSettings
{
    /// <summary>Server keys for PUT/GET <c>/v1/profile</c>.</summary>
    internal static readonly string[] ProfileKeys =
    [
        "displayName",
        "pronouns",
        "statusText",
        "bio",
        "accent",
        "layout",
        "bannerHeight",
        "showcaseStyle",
        "sections",
        "hiddenSections",
        "showcase",
        "avatarGameId",
        "bannerGameId",
    ];

    /// <summary>Server keys for PUT/GET <c>/v1/sync</c>.</summary>
    internal static readonly string[] SyncKeys =
    [
        "sortMode",
        "trophyNotificationsEnabled",
        "trophyNotificationPosition",
        "trophyNotificationPreset",
        "trophyNotificationSound",
        "trophyNotificationSoundCue",
    ];

    private static readonly Dictionary<string, string> ProfileAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["displayName"] = "displayName",
        ["profileName"] = "displayName",
        ["pronouns"] = "pronouns",
        ["profilePronouns"] = "pronouns",
        ["statusText"] = "statusText",
        ["profileStatusText"] = "statusText",
        ["bio"] = "bio",
        ["profileBio"] = "bio",
        ["accent"] = "accent",
        ["profileAccent"] = "accent",
        ["layout"] = "layout",
        ["profileLayout"] = "layout",
        ["bannerHeight"] = "bannerHeight",
        ["profileBannerHeight"] = "bannerHeight",
        ["showcaseStyle"] = "showcaseStyle",
        ["profileShowcaseStyle"] = "showcaseStyle",
        ["sections"] = "sections",
        ["profileSections"] = "sections",
        ["hiddenSections"] = "hiddenSections",
        ["profileHiddenSections"] = "hiddenSections",
        ["showcase"] = "showcase",
        ["profileShowcase"] = "showcase",
        ["avatarGameId"] = "avatarGameId",
        ["profileAvatarGameId"] = "avatarGameId",
        ["bannerGameId"] = "bannerGameId",
        ["profileBannerGameId"] = "bannerGameId",
    };

    private static readonly Dictionary<string, string> SyncAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sortMode"] = "sortMode",
        ["trophyNotificationsEnabled"] = "trophyNotificationsEnabled",
        ["trophyNotificationPosition"] = "trophyNotificationPosition",
        ["trophyNotificationPreset"] = "trophyNotificationPreset",
        ["trophyNotificationSound"] = "trophyNotificationSound",
        ["trophyNotificationSoundCue"] = "trophyNotificationSoundCue",
    };

    private static readonly HashSet<string> ProfileKeySet = new(ProfileKeys, StringComparer.Ordinal);
    private static readonly HashSet<string> SyncKeySet = new(SyncKeys, StringComparer.Ordinal);

    internal static readonly HashSet<string> ProfileSettingsKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "profileName",
        "profilePronouns",
        "profileStatusText",
        "profileBio",
        "profileAccent",
        "profileLayout",
        "profileBannerHeight",
        "profileShowcaseStyle",
        "profileSections",
        "profileHiddenSections",
        "profileShowcase",
        "profileAvatarGameId",
        "profileBannerGameId",
    };

    internal static readonly HashSet<string> SyncSettingsKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "sortMode",
        "trophyNotificationsEnabled",
        "trophyNotificationPosition",
        "trophyNotificationPreset",
        "trophyNotificationSound",
        "trophyNotificationSoundCue",
    };

    internal static readonly HashSet<string> Deny;

    static ExoSyncedSettings()
    {
        Deny = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in AllSettingsKeys())
        {
            if (ProfileSettingsKeys.Contains(name) || SyncSettingsKeys.Contains(name))
                continue;
            Deny.Add(name);
        }
    }

    internal static IReadOnlyList<string> AllSettingsKeys() =>
        typeof(AppSettings)
            .GetProperties()
            .Select(property => JsonNamingPolicy.CamelCase.ConvertName(property.Name))
            .ToArray();

    internal static JsonObject ExtractProfile(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var result = new JsonObject();
        result["displayName"] = settings.ProfileName ?? "";
        result["pronouns"] = settings.ProfilePronouns ?? "";
        result["statusText"] = settings.ProfileStatusText ?? "";
        result["bio"] = settings.ProfileBio ?? "";
        result["accent"] = settings.ProfileAccent ?? "";
        result["layout"] = settings.ProfileLayout ?? "";
        result["bannerHeight"] = settings.ProfileBannerHeight ?? "";
        result["showcaseStyle"] = settings.ProfileShowcaseStyle ?? "";
        result["sections"] = ToArray(settings.ProfileSections);
        result["hiddenSections"] = ToArray(settings.ProfileHiddenSections);
        result["showcase"] = ToArray(settings.ProfileShowcase);
        result["avatarGameId"] = settings.ProfileAvatarGameId;
        result["bannerGameId"] = settings.ProfileBannerGameId;
        return result;
    }

    internal static JsonObject ExtractSync(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var result = new JsonObject();
        result["sortMode"] = settings.SortMode ?? "";
        result["trophyNotificationsEnabled"] = settings.TrophyNotificationsEnabled;
        result["trophyNotificationPosition"] = settings.TrophyNotificationPosition ?? "";
        result["trophyNotificationPreset"] = settings.TrophyNotificationPreset ?? "";
        result["trophyNotificationSound"] = settings.TrophyNotificationSound;
        result["trophyNotificationSoundCue"] = settings.TrophyNotificationSoundCue ?? "";
        return result;
    }

    internal static JsonObject FilterProfile(JsonElement element) =>
        FilterByAlias(element, ProfileAliases, ProfileKeySet);

    internal static JsonObject FilterSync(JsonElement element) =>
        FilterByAlias(element, SyncAliases, SyncKeySet);

    internal static JsonObject FieldVector(JsonObject values, string deviceId, DateTimeOffset updatedAt)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (string.IsNullOrWhiteSpace(deviceId))
            throw new ArgumentException("deviceId is required.", nameof(deviceId));

        var stamp = updatedAt.ToUniversalTime()
            .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        var fields = new JsonObject();
        foreach (var pair in values)
        {
            fields[pair.Key] = new JsonObject
            {
                ["value"] = pair.Value is null ? null : pair.Value.DeepClone(),
                ["updatedAt"] = stamp,
            };
        }

        return new JsonObject
        {
            ["deviceId"] = deviceId,
            ["fields"] = fields,
        };
    }

    internal static bool HasOlderDiscard(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("discarded", out var discarded) ||
            discarded.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var row in discarded.EnumerateArray())
        {
            if (row.ValueKind == JsonValueKind.Object &&
                row.TryGetProperty("reason", out var reason) &&
                reason.ValueKind == JsonValueKind.String &&
                string.Equals(reason.GetString(), "older", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    internal static void ApplyProfile(AppSettings settings, JsonElement portable)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ApplyMapped(settings, portable, ProfileAliases, profile: true);
    }

    internal static void ApplySync(AppSettings settings, JsonElement portable)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ApplyMapped(settings, portable, SyncAliases, profile: false);
    }

    internal static void Apply(AppSettings settings, JsonElement portable)
    {
        ApplyProfile(settings, portable);
        ApplySync(settings, portable);
    }

    private static JsonObject FilterByAlias(
        JsonElement element,
        Dictionary<string, string> aliases,
        HashSet<string> allow)
    {
        var result = new JsonObject();
        if (element.ValueKind != JsonValueKind.Object)
            return result;

        if (element.TryGetProperty("values", out var values) && values.ValueKind == JsonValueKind.Object)
            return FilterByAlias(values, aliases, allow);
        if (element.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Object)
            return FilterByAlias(fields, aliases, allow);

        foreach (var property in element.EnumerateObject())
        {
            if (!aliases.TryGetValue(property.Name, out var key) || !allow.Contains(key))
                continue;
            result[key] = CloneValue(property.Value);
        }

        return result;
    }

    private static void ApplyMapped(
        AppSettings settings,
        JsonElement portable,
        Dictionary<string, string> aliases,
        bool profile)
    {
        if (portable.ValueKind != JsonValueKind.Object)
            return;

        var source = portable;
        if (portable.TryGetProperty("values", out var values) && values.ValueKind == JsonValueKind.Object)
            source = values;
        else if (portable.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Object)
            source = fields;

        foreach (var property in source.EnumerateObject())
        {
            if (!aliases.TryGetValue(property.Name, out var key))
                continue;
            var value = UnwrapField(property.Value);
            if (profile)
                ApplyProfileKey(settings, key, value);
            else
                ApplySyncKey(settings, key, value);
        }
    }

    private static void ApplyProfileKey(AppSettings settings, string key, JsonElement value)
    {
        switch (key)
        {
            case "displayName":
                settings.ProfileName = ReadOptionalString(value);
                break;
            case "pronouns":
                settings.ProfilePronouns = ReadOptionalString(value);
                break;
            case "statusText":
                settings.ProfileStatusText = ReadOptionalString(value);
                break;
            case "bio":
                settings.ProfileBio = ReadOptionalString(value);
                break;
            case "accent":
                if (value.ValueKind == JsonValueKind.String)
                    settings.ProfileAccent = value.GetString() ?? settings.ProfileAccent;
                break;
            case "layout":
                if (value.ValueKind == JsonValueKind.String)
                    settings.ProfileLayout = value.GetString() ?? settings.ProfileLayout;
                break;
            case "bannerHeight":
                if (value.ValueKind == JsonValueKind.String)
                    settings.ProfileBannerHeight = value.GetString() ?? settings.ProfileBannerHeight;
                break;
            case "showcaseStyle":
                if (value.ValueKind == JsonValueKind.String)
                    settings.ProfileShowcaseStyle = value.GetString() ?? settings.ProfileShowcaseStyle;
                break;
            case "sections":
                settings.ProfileSections = ReadStringList(value);
                break;
            case "hiddenSections":
                settings.ProfileHiddenSections = ReadStringList(value);
                break;
            case "showcase":
                settings.ProfileShowcase = ReadStringList(value);
                break;
            case "avatarGameId":
                settings.ProfileAvatarGameId = ReadOptionalString(value);
                break;
            case "bannerGameId":
                settings.ProfileBannerGameId = ReadOptionalString(value);
                break;
        }
    }

    private static void ApplySyncKey(AppSettings settings, string key, JsonElement value)
    {
        switch (key)
        {
            case "sortMode":
                if (value.ValueKind == JsonValueKind.String)
                    settings.SortMode = value.GetString() ?? settings.SortMode;
                break;
            case "trophyNotificationsEnabled":
                if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    settings.TrophyNotificationsEnabled = value.GetBoolean();
                break;
            case "trophyNotificationPosition":
                if (value.ValueKind == JsonValueKind.String)
                    settings.TrophyNotificationPosition = value.GetString() ?? settings.TrophyNotificationPosition;
                break;
            case "trophyNotificationPreset":
                if (value.ValueKind == JsonValueKind.String)
                    settings.TrophyNotificationPreset = value.GetString() ?? settings.TrophyNotificationPreset;
                break;
            case "trophyNotificationSound":
                if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    settings.TrophyNotificationSound = value.GetBoolean();
                break;
            case "trophyNotificationSoundCue":
                if (value.ValueKind == JsonValueKind.String)
                    settings.TrophyNotificationSoundCue = value.GetString() ?? settings.TrophyNotificationSoundCue;
                break;
        }
    }

    private static JsonElement UnwrapField(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty("value", out var nested))
            return nested;
        return element;
    }

    private static JsonNode? CloneValue(JsonElement element)
    {
        element = UnwrapField(element);
        if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        return JsonNode.Parse(element.GetRawText());
    }

    private static string? ReadOptionalString(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null)
            return null;
        return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
    }

    private static List<string> ReadStringList(JsonElement element)
    {
        var list = new List<string>();
        if (element.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var value = item.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                    list.Add(value);
            }
        }

        return list;
    }

    private static JsonArray ToArray(IEnumerable<string>? items)
    {
        var array = new JsonArray();
        if (items is null)
            return array;
        foreach (var item in items)
        {
            if (!string.IsNullOrWhiteSpace(item))
                array.Add(item);
        }

        return array;
    }
}
