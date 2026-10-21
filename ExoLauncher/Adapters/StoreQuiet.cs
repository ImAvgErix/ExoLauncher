using System.Text.Json;
using System.Text.Json.Nodes;

namespace ExoLauncher.Adapters;

/// <summary>
/// Settings > Game stores > Notifications and sounds: Steam's own switches for its pop-ups and sounds, all off, for
/// every account on this PC. Where Steam keeps them (its steamui settings pages and SteamUI.dll's names):
/// - userdata/&lt;id&gt;/7/remote/sharedconfig.vdf › UserRoamingConfigStore: Settings > Notifications (toasts never
///   shown, no sound with one, each client toast and sound: achievements, controllers, other users signed in), and
///   Software/Valve/Steam/FriendsUI › FriendsUIJSON, the friends list's settings as JSON;
/// - userdata/&lt;id&gt;/config/localconfig.vdf › UserLocalConfigStore: friends (the same friends switches as Steam
///   reads them before its friends list loads; ChatFlashMode 2 never flashes the taskbar), system (a screenshot's
///   toast and sound) and news › NotifyAvailableGames (new releases and changes to your games).
/// Steam holds both files in memory and writes them as it exits, so they are written while it is closed. The rest of
/// each file is kept byte for byte.
/// </summary>
internal static class StoreQuiet
{
    private static readonly (string[] Path, (string Key, string Value)[] Values)[] Shared =
    [
        (["UserRoamingConfigStore"],
        [
            ("DisableAllToasts", "1"), ("DisableToastsInGame", "1"), ("PlaySoundOnToast", "0"),
            ("AchievementNotificationToast", "0"), ("AchievementNotificationSound", "0"),
            ("ControllerConnectNotificationToast", "0"), ("ControllerConnectNotificationSound", "0"),
            ("ControllerLowBatteryNotificationToast", "0"), ("ControllerLowBatteryNotificationSound", "0"),
            ("LoggedInAsUserNotificationToast", "0"), ("LoggedInAsUserNotificationSound", "0"),
        ]),
    ];

    private static readonly (string[] Path, (string Key, string Value)[] Values)[] Local =
    [
        (["UserLocalConfigStore", "friends"],
        [
            ("Notifications_ShowIngame", "0"), ("Notifications_ShowOnline", "0"), ("Notifications_ShowMessage", "0"),
            ("Notifications_EventsAndAnnouncements", "0"), ("Sounds_PlayIngame", "0"), ("Sounds_PlayOnline", "0"),
            ("Sounds_PlayMessage", "0"), ("Sounds_EventsAndAnnouncements", "0"), ("ChatFlashMode", "2"),
        ]),
        (["UserLocalConfigStore", "system"], [("InGameOverlayScreenshotNotification", "0"), ("InGameOverlayScreenshotPlaySound", "0")]),
        (["UserLocalConfigStore", "news"], [("NotifyAvailableGames", "0")]),
    ];

    private static readonly string[] FriendsUi = ["UserRoamingConfigStore", "Software", "Valve", "Steam", "FriendsUI"];

    /// <summary>Each account's file that isn't quiet yet, with its quiet text.</summary>
    internal static List<(string Path, string Text)> SteamEdits(string root)
    {
        var edits = new List<(string, string)>();
        string[] accounts;
        try { accounts = Directory.GetDirectories(Path.Combine(root, "userdata")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return edits; }
        foreach (var account in accounts.Where(dir => Path.GetFileName(dir).All(char.IsAsciiDigit)))
        {
            Add(Path.Combine(account, "7", "remote", "sharedconfig.vdf"), QuietShared);
            Add(Path.Combine(account, "config", "localconfig.vdf"), QuietLocal);
        }
        return edits;

        void Add(string path, Func<string, string> quiet)
        {
            if (SteamPlaytime.ReadSharedText(path) is not { Length: > 0 } text) return;
            try
            {
                var next = quiet(text);
                if (next != text) edits.Add((path, next));
            }
            catch (InvalidDataException) { /* a file Steam left half-written: Steam writes it whole as it next exits */ }
        }
    }

    internal static string QuietShared(string text)
    {
        text = Apply(text, Shared);
        if (SteamKeyValues.ParseText(text).Section(string.Join('/', FriendsUi)).Value("FriendsUIJSON") is { Length: > 0 } json &&
            QuietFriendsJson(json) is { } quiet)
            text = SteamOverlayConfig.SetValue(text, FriendsUi, "FriendsUIJSON", quiet.Replace("\\", "\\\\").Replace("\"", "\\\""));
        return text;
    }

    internal static string QuietLocal(string text) => Apply(text, Local);

    private static string Apply(string text, (string[] Path, (string Key, string Value)[] Values)[] sections)
    {
        var parsed = SteamKeyValues.ParseText(text);
        foreach (var (path, values) in sections)
        {
            var section = parsed.Section(string.Join('/', path));
            foreach (var (key, value) in values)
                if (section.Value(key) != value) text = SteamOverlayConfig.SetValue(text, path, key, value);
        }
        return text;
    }

    /// <summary>The friends list's settings with every notification and sound off and no taskbar flashing; null when they already are.</summary>
    internal static string? QuietFriendsJson(string json)
    {
        JsonObject settings;
        try
        {
            if (JsonNode.Parse(json) is not JsonObject parsed) return null;
            settings = parsed;
        }
        catch (JsonException) { return null; }
        var changed = false;
        foreach (var key in settings.Select(entry => entry.Key).ToList())
        {
            if (!key.StartsWith("bNotifications_", StringComparison.Ordinal) && !key.StartsWith("bSounds_", StringComparison.Ordinal)) continue;
            if (settings[key] is JsonValue value && value.TryGetValue<bool>(out var on) && !on) continue;
            settings[key] = false;
            changed = true;
        }
        if (settings["nChatFlashMode"] is not JsonValue mode || !mode.TryGetValue<int>(out var flash) || flash != 2)
        {
            settings["nChatFlashMode"] = 2;
            changed = true;
        }
        return changed ? settings.ToJsonString() : null;
    }
}
