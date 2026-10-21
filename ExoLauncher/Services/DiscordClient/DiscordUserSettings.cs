using System.Text.Json;
using System.Text.Json.Nodes;
using static ExoLauncher.Services.DiscordClient.DiscordProto;

namespace ExoLauncher.Services.DiscordClient;

/// <summary>
/// The account settings Discord keeps in PreloadedUserSettings (users/@me/settings-proto/1) that
/// Exo's Discord settings show. Field numbers and defaults are Discord's own, read from its web
/// client (discord_protos.discord_users.v1.PreloadedUserSettings and the settings accessors,
/// October 2026). Discord's client saves a change by sending the whole top-level section it
/// changed (text_and_images, privacy, ...), copied from what it read with that one field
/// rewritten (the server replaces the sections it is sent); Exo does exactly that, so every
/// field it doesn't know, in that section and every other, stays as it was. A value that isn't
/// set reads as Discord's default.
/// </summary>
internal static class DiscordUserSettings
{
    /// <summary>How a value is stored: a google.protobuf wrapper (BoolValue, UInt32Value, StringValue),
    /// a plain enum or bool, or packed fixed64 server ids.</summary>
    internal enum Kind { Bool, UInt, Text, Enum, Flag, Ids }

    /// <summary>One setting: the page's key (Discord's own field name), its path of field numbers
    /// from the top of PreloadedUserSettings, its default, and the values it may take.</summary>
    internal sealed record Setting(string Key, Kind Kind, int[] Path, object? Default, int Max = 0, string[]? Texts = null);

    private static readonly string[] Spoilers = ["ON_CLICK", "ALWAYS", "IF_MODERATOR"];

    /// <summary>The languages Discord offers (its locale list, the enabled ones).</summary>
    internal static readonly string[] Locales =
    [
        "en-US", "en-GB", "zh-CN", "zh-TW", "cs", "da", "nl", "fr", "de", "el", "hu", "it", "ja", "ko", "pl", "pt-BR",
        "ru", "es-419", "es-ES", "sv-SE", "tr", "bg", "uk", "fi", "no", "hr", "ro", "lt", "th", "vi", "hi",
    ];

    internal static readonly Setting[] Known =
    [
        // 5 voice_and_video
        new("streamNotificationsEnabled", Kind.Bool, [5, 7], true),
        // 6 text_and_images
        new("useRichChatInput", Kind.Bool, [6, 2], true),
        new("renderSpoilers", Kind.Text, [6, 4], "ON_CLICK", Texts: Spoilers),
        new("viewImageDescriptions", Kind.Bool, [6, 7], false),
        new("inlineAttachmentMedia", Kind.Bool, [6, 9], true),
        new("inlineEmbedMedia", Kind.Bool, [6, 10], true),
        new("gifAutoPlay", Kind.Bool, [6, 11], true),
        new("renderEmbeds", Kind.Bool, [6, 12], true),
        new("renderReactions", Kind.Bool, [6, 13], true),
        new("animateEmoji", Kind.Bool, [6, 14], true),
        new("animateStickers", Kind.UInt, [6, 15], 0u, Max: 2),
        new("enableTtsCommand", Kind.Bool, [6, 16], true),
        new("messageDisplayCompact", Kind.Bool, [6, 17], false),
        // The spam filter before dm_spam_filter_v2 (0 off, 1 non-friends, 2 everyone): what an unset v2 shows.
        new("explicitContentFilter", Kind.UInt, [6, 19], 1u, Max: 2),
        new("viewNsfwGuilds", Kind.Bool, [6, 20], false),
        new("convertEmoticons", Kind.Bool, [6, 21], true),
        new("viewNsfwCommands", Kind.Bool, [6, 23], false),
        new("dmSpamFilterV2", Kind.Enum, [6, 27], 0, Max: 3),
        new("includeStickersInAutocomplete", Kind.Bool, [6, 28], false),
        new("explicitContentGuilds", Kind.Enum, [6, 29, 1], 0, Max: 3),
        new("explicitContentFriendDm", Kind.Enum, [6, 29, 2], 0, Max: 3),
        new("explicitContentNonFriendDm", Kind.Enum, [6, 29, 3], 0, Max: 3),
        new("goreContentGuilds", Kind.Enum, [6, 32, 1], 0, Max: 3),
        new("goreContentFriendDm", Kind.Enum, [6, 32, 2], 0, Max: 3),
        new("goreContentNonFriendDm", Kind.Enum, [6, 32, 3], 0, Max: 3),
        new("includeGameMentionsInAutocomplete", Kind.Bool, [6, 39], true),
        new("displayCompactAvatars", Kind.Bool, [6, 41], false),
        // 7 notifications
        new("reactionNotifications", Kind.Enum, [7, 7], 0, Max: 2),
        new("enableFriendOnlineNotifications", Kind.Bool, [7, 12], true),
        new("enableFriendAnniversaryNotifications", Kind.Bool, [7, 14], true),
        new("enableProfileUpdatesNotifications", Kind.Bool, [7, 16], true),
        new("notifyFriendsOnProfileUpdate", Kind.Bool, [7, 24], true),
        new("notifyFriendsOnComeOnline", Kind.Bool, [7, 25], true),
        new("notifyServerMembersOnGoLive", Kind.Bool, [7, 26], true),
        // 8 privacy
        new("allowActivityPartyPrivacyFriends", Kind.Bool, [8, 1], true),
        new("allowActivityPartyPrivacyVoiceChannel", Kind.Bool, [8, 2], true),
        new("restrictedGuildIds", Kind.Ids, [8, 3], null),
        new("defaultGuildsRestricted", Kind.Flag, [8, 4], false),
        new("friendSourceFlags", Kind.UInt, [8, 11], 14u, Max: 14),
        new("defaultGuildsActivityRestricted", Kind.Enum, [8, 14], 0, Max: 2),
        new("messageRequestRestrictedGuildIds", Kind.Ids, [8, 16], null),
        new("defaultMessageRequestRestricted", Kind.Bool, [8, 17], false),
        new("dropsOptedOut", Kind.Bool, [8, 18], false),
        new("allowGameFriendDmsInDiscord", Kind.Bool, [8, 26], true),
        // Unset (null) falls back to default_guilds_restricted, as Discord's client reads it.
        new("defaultGuildsRestrictedV2", Kind.Bool, [8, 27], null),
        new("defaultGuildsActivityRestrictedV2", Kind.Enum, [8, 29], 0, Max: 3),
        new("quests3pDataOptedOut", Kind.Bool, [8, 30], false),
        new("profileVisibility", Kind.Enum, [8, 32], 0, Max: 3),
        new("hideFriendRequestNotes", Kind.Bool, [8, 33], false),
        // 11 status
        new("showCurrentGame", Kind.Bool, [11, 3], true),
        // 12 localization
        new("locale", Kind.Text, [12, 1], "", Texts: Locales),
        // 13 appearance
        new("developerMode", Kind.Flag, [13, 2], false),
        new("timestampHourCycle", Kind.Enum, [13, 9], 0, Max: 2),
    ];

    private static readonly Dictionary<string, Setting> ByKey = Known.ToDictionary(s => s.Key);

    /// <summary>Every known setting's value in a PreloadedUserSettings, Discord's default where it isn't set.</summary>
    internal static JsonObject Decode(byte[] proto)
    {
        var values = new JsonObject();
        foreach (var setting in Known) values[setting.Key] = Read(setting, Leaf(proto, setting.Path));
        var users = new JsonObject();
        foreach (var (id, user) in AudioUsers(proto)) users[id] = new JsonObject { ["muted"] = user.Muted, ["volume"] = user.Volume };
        values["audioUsers"] = users;
        var streams = new JsonObject();
        foreach (var (id, stream) in AudioUsers(proto, StreamMap)) streams[id] = new JsonObject { ["muted"] = stream.Muted, ["volume"] = stream.Volume };
        values["audioStreams"] = streams;
        return values;
    }

    /// <summary>A person's call volume and local mute, Discord's AudioContextSetting: volume is 0-200, 100 as loud as they send.</summary>
    internal readonly record struct AudioUser(bool Muted, float Volume, bool SoundboardMuted = false);

    // audio_context_settings (16) > user (1) or stream (2, a person's Go Live sound): map<fixed64 user id, AudioContextSetting
    // {1 muted, 2 volume (float), 3 modified_at (fixed64 ms), 4 soundboard_muted}>. Discord keeps one only while it differs
    // from 100 and unmuted.
    internal const int UserMap = 1, StreamMap = 2;

    internal static Dictionary<string, AudioUser> AudioUsers(byte[] proto, int map = UserMap)
    {
        var users = new Dictionary<string, AudioUser>();
        foreach (var (number, wire, entry) in Fields(Embedded(proto, 16)))
        {
            if (number != map || wire != 2) continue;
            ulong? id = null;
            byte[] value = [];
            foreach (var (n, w, v) in Fields(entry))
            {
                if (n == 1 && w == 1) id = BitConverter.ToUInt64(v);
                else if (n == 2 && w == 2) value = v;
            }
            if (id is not { } key) continue;
            // proto3: a field left out is its zero (a muted person at volume 0 sends neither volume nor anything else).
            bool muted = false, board = false;
            float volume = 0;
            foreach (var (n, w, v) in Fields(value))
            {
                if (n == 1 && w == 0) muted = Varint(v) != 0;
                else if (n == 2 && w == 5) volume = BitConverter.ToSingle(v);
                else if (n == 4 && w == 0) board = Varint(v) != 0;
            }
            users[key.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new AudioUser(muted, volume, board);
        }
        return users;
    }

    // Section 16 with these people's entries set, as Discord's client writes them: stamped now, and left out once back
    // to 100 and unmuted. Everyone else's entry, and the other map, stay as they were.
    private static byte[] AudioPatch(byte[] proto, IReadOnlyDictionary<ulong, (bool? Muted, float? Volume)> changes, int map = UserMap)
    {
        var before = AudioUsers(proto, map);
        var section = new List<byte>();
        foreach (var (number, wire, value) in Fields(Embedded(proto, 16)))
        {
            var changed = number == map && wire == 2 && Fields(value).Any(f => f.Number == 1 && f.Wire == 1 && changes.ContainsKey(BitConverter.ToUInt64(f.Value)));
            if (!changed) Raw(section, number, wire, value);
        }
        var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var (id, change) in changes)
        {
            var old = before.GetValueOrDefault(id.ToString(System.Globalization.CultureInfo.InvariantCulture), new AudioUser(false, 100));
            var user = new AudioUser(change.Muted ?? old.Muted, Math.Clamp(change.Volume ?? old.Volume, 0, 200), old.SoundboardMuted);
            if (user is { Muted: false, Volume: 100, SoundboardMuted: false }) continue;
            var setting = new List<byte>();
            if (user.Muted) { WriteTag(setting, 1, 0); WriteVarint(setting, 1); }
            if (user.Volume != 0) { WriteTag(setting, 2, 5); setting.AddRange(BitConverter.GetBytes(user.Volume)); }
            WriteTag(setting, 3, 1); setting.AddRange(BitConverter.GetBytes(now));
            if (user.SoundboardMuted) { WriteTag(setting, 4, 0); WriteVarint(setting, 1); }
            var entry = new List<byte>();
            WriteTag(entry, 1, 1); entry.AddRange(BitConverter.GetBytes(id));
            WriteBytes(entry, 2, setting);
            WriteBytes(section, map, entry);
        }
        var leaf = new List<byte>();
        WriteBytes(leaf, 16, section);
        return [.. leaf];
    }

    // "audioUsers": {id: {muted?, volume?}} as the people's changes, or null when it isn't that.
    private static Dictionary<ulong, (bool? Muted, float? Volume)>? AudioChanges(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return null;
        var changes = new Dictionary<ulong, (bool?, float?)>();
        foreach (var person in value.EnumerateObject())
        {
            if (!ulong.TryParse(person.Name, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) || person.Value.ValueKind != JsonValueKind.Object) return null;
            bool? muted = person.Value.TryGetProperty("muted", out var m) && m.ValueKind is JsonValueKind.True or JsonValueKind.False ? m.GetBoolean() : null;
            // A mute alone carries no volume (null): TryGetDouble throws on anything but a number.
            float? volume = person.Value.TryGetProperty("volume", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && d is >= 0 and <= 200 ? (float)d : null;
            changes[id] = (muted, volume);
        }
        return changes;
    }

    /// <summary>One item of the server list, as Discord keeps it: a folder (Id set), or a server on its own (Id null, one server).</summary>
    internal sealed record GuildFolder(string? Id, string? Name, int? Color, List<string> Guilds);

    /// <summary>"guildFolders": [{ id, name, color, guildIds }] as the whole server list, or null when it isn't that.</summary>
    internal static List<GuildFolder>? Folders(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 1000) return null;
        var folders = new List<GuildFolder>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("guildIds", out var ids) || ids.ValueKind != JsonValueKind.Array) return null;
            var guilds = new List<string>();
            foreach (var id in ids.EnumerateArray())
            {
                if (id.ValueKind != JsonValueKind.String || !ulong.TryParse(id.GetString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _)) return null;
                guilds.Add(id.GetString()!);
            }
            string? folderId = null, name = null;
            int? color = null;
            if (item.TryGetProperty("id", out var f) && f.ValueKind != JsonValueKind.Null)
            {
                if (f.ValueKind != JsonValueKind.String || !long.TryParse(f.GetString(), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out _)) return null;
                folderId = f.GetString();
            }
            if (item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && n.GetString() is { Length: > 0 } text) name = text;
            if (item.TryGetProperty("color", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var rgb) && rgb >= 0) color = rgb;
            // A folder Discord would drop (no servers left) is left out, as Discord reads it.
            if (guilds.Count > 0) folders.Add(new GuildFolder(folderId, name, color, guilds));
        }
        return folders;
    }

    // guild_folders (14) > folders (1), each a GuildFolder {1 packed fixed64 guild_ids, 2 Int64Value id, 3 StringValue name,
    // 4 UInt64Value color}, as Discord's client writes them (a server on its own is just its id). guild_positions (2) stays.
    private static byte[] FoldersPatch(List<GuildFolder> folders)
    {
        var output = new List<byte>();
        foreach (var folder in folders)
        {
            var entry = new List<byte>();
            WriteBytes(entry, 1, [.. folder.Guilds.SelectMany(id => BitConverter.GetBytes(ulong.Parse(id, System.Globalization.CultureInfo.InvariantCulture)))]);
            if (folder.Id is { } id)
            {
                var wrapped = new List<byte>();
                var value = long.Parse(id, System.Globalization.CultureInfo.InvariantCulture);
                if (value != 0) { WriteTag(wrapped, 1, 0); WriteVarint(wrapped, (ulong)value); }
                WriteBytes(entry, 2, wrapped);
            }
            if (folder.Name is { } name)
            {
                var wrapped = new List<byte>();
                WriteString(wrapped, 1, name);
                WriteBytes(entry, 3, wrapped);
            }
            if (folder.Color is { } color)
            {
                var wrapped = new List<byte>();
                if (color != 0) { WriteTag(wrapped, 1, 0); WriteVarint(wrapped, (ulong)color); }
                WriteBytes(entry, 4, wrapped);
            }
            WriteBytes(output, 1, entry);
        }
        return [.. output];
    }

    /// <summary>The settings proto Discord's client would send for these changes: each section they
    /// touch, whole, as <paramref name="current"/> has it with the changed fields rewritten. Null when a
    /// key is unknown or a value isn't one the setting takes.</summary>
    internal static byte[]? Patch(byte[] current, IReadOnlyDictionary<string, JsonElement> changes)
    {
        if (changes.Count == 0) return null;
        var full = current;
        var sections = new HashSet<int>();
        foreach (var (key, value) in changes)
        {
            if (key is "audioUsers" or "audioStreams")
            {
                if (AudioChanges(value) is not { } people) return null;
                full = Rewrite(full, [16], 0, AudioPatch(full, people, key == "audioStreams" ? StreamMap : UserMap));
                sections.Add(16);
                continue;
            }
            if (key == "guildFolders")
            {
                if (Folders(value) is not { } folders) return null;
                full = Rewrite(full, [14, 1], 0, FoldersPatch(folders));
                sections.Add(14);
                continue;
            }
            if (!ByKey.TryGetValue(key, out var setting) || Encode(setting, value) is not { } leaf) return null;
            full = Rewrite(full, setting.Path, 0, leaf);
            sections.Add(setting.Path[0]);
        }
        var patch = new List<byte>();
        foreach (var (number, wire, value) in Fields(full))
            if (sections.Contains(number) && wire == 2) Raw(patch, number, wire, value);
        return [.. patch];
    }

    // The occurrences of the last field on a path (an embedded message's repeats merge, so its bytes are their concatenation).
    private static List<(int Wire, byte[] Value)> Leaf(byte[] message, int[] path)
    {
        var bytes = message;
        for (var depth = 0; depth < path.Length - 1; depth++) bytes = Embedded(bytes, path[depth]);
        return [.. Fields(bytes).Where(f => f.Number == path[^1]).Select(f => (f.Wire, f.Value))];
    }

    private static byte[] Embedded(byte[] message, int number) =>
        [.. Fields(message).Where(f => f.Number == number && f.Wire == 2).SelectMany(f => f.Value)];

    // A message with the field at path replaced by leaf (empty: the field left out), its other fields as they were.
    private static byte[] Rewrite(byte[] message, int[] path, int depth, byte[] leaf)
    {
        var number = path[depth];
        var last = depth == path.Length - 1;
        var output = new List<byte>();
        var inner = new List<byte>();
        foreach (var (n, wire, value) in Fields(message))
        {
            if (n != number) { Raw(output, n, wire, value); continue; }
            if (!last && wire == 2) inner.AddRange(value);
        }
        if (last) output.AddRange(leaf);
        else WriteBytes(output, number, Rewrite([.. inner], path, depth + 1, leaf));
        return [.. output];
    }

    private static JsonNode? Read(Setting setting, List<(int Wire, byte[] Value)> found)
    {
        if (setting.Kind == Kind.Ids)
            return new JsonArray([.. found.SelectMany(f => Ids(f.Wire, f.Value)).Select(id => (JsonNode)id.ToString(System.Globalization.CultureInfo.InvariantCulture))]);
        if (found.Count == 0) return Value(setting.Default);
        var (wire, value) = found[^1];
        switch (setting.Kind)
        {
            case Kind.Enum when wire == 0: return JsonValue.Create((int)(long)Varint(value));
            case Kind.Flag when wire == 0: return JsonValue.Create(Varint(value) != 0);
            case Kind.Bool or Kind.UInt or Kind.Text when wire == 2:
                // A wrapper's value is its field 1; an empty wrapper holds the type's zero.
                var inner = Fields(value).LastOrDefault(f => f.Number == 1);
                return setting.Kind switch
                {
                    Kind.Bool => JsonValue.Create(inner.Value is { } b && inner.Wire == 0 && Varint(b) != 0),
                    Kind.UInt => JsonValue.Create(inner.Value is { } u && inner.Wire == 0 ? (uint)Varint(u) : 0u),
                    _ => JsonValue.Create(inner.Value is { } t && inner.Wire == 2 ? System.Text.Encoding.UTF8.GetString(t) : ""),
                };
            default: return Value(setting.Default);
        }
    }

    private static IEnumerable<ulong> Ids(int wire, byte[] value)
    {
        if (wire == 1) { yield return BitConverter.ToUInt64(value); yield break; }
        if (wire != 2) yield break;
        for (var at = 0; at + 8 <= value.Length; at += 8) yield return BitConverter.ToUInt64(value, at);
    }

    private static JsonNode? Value(object? value) => value switch
    {
        null => null,
        bool b => JsonValue.Create(b),
        uint u => JsonValue.Create(u),
        int i => JsonValue.Create(i),
        string s => JsonValue.Create(s),
        _ => null,
    };

    // The field's bytes for a value (tag included; empty when proto3 leaves it out), or null when the value isn't allowed.
    private static byte[]? Encode(Setting setting, JsonElement value)
    {
        var number = setting.Path[^1];
        var output = new List<byte>();
        switch (setting.Kind)
        {
            case Kind.Bool when value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                WriteBytes(output, number, value.GetBoolean() ? [0x08, 0x01] : []);
                break;
            case Kind.UInt when value.TryGetUInt32(out var u) && u <= setting.Max:
                var wrapped = new List<byte>();
                if (u != 0) { WriteTag(wrapped, 1, 0); WriteVarint(wrapped, u); }
                WriteBytes(output, number, wrapped);
                break;
            case Kind.Text when value.GetString() is { } text && setting.Texts?.Contains(text) == true:
                var str = new List<byte>();
                WriteString(str, 1, text);
                WriteBytes(output, number, str);
                break;
            case Kind.Enum when value.TryGetInt32(out var e) && e >= 0 && e <= setting.Max:
                if (e != 0) { WriteTag(output, number, 0); WriteVarint(output, (ulong)e); }
                break;
            case Kind.Flag when value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                if (value.GetBoolean()) { WriteTag(output, number, 0); WriteVarint(output, 1); }
                break;
            case Kind.Ids when value.ValueKind == JsonValueKind.Array && value.GetArrayLength() <= 1000:
                var ids = new List<byte>();
                foreach (var id in value.EnumerateArray())
                {
                    if (id.GetString() is not { } text || !ulong.TryParse(DiscordRest.Id(text), out var snowflake)) return null;
                    ids.AddRange(BitConverter.GetBytes(snowflake));
                }
                if (ids.Count > 0) WriteBytes(output, number, ids);
                break;
            default:
                return null;
        }
        return [.. output];
    }

    /// <summary>The account's settings as Discord has them now, or null when Discord can't be read.</summary>
    internal static async Task<(byte[]? Proto, string? Error)> FetchAsync(DiscordRest rest, CancellationToken ct)
    {
        var reply = await rest.UserSettingsAsync(ct).ConfigureAwait(false);
        if (reply.Error is not null) return (null, reply.Error);
        return Settings(reply.Body) is { } proto ? (proto, null) : (null, "Discord didn't send your settings.");
    }

    /// <summary>Saves changes the way Discord's client does; the settings Discord now has, or the error.</summary>
    internal static async Task<(JsonObject? Values, string? Error)> ChangeAsync(DiscordRest rest, IReadOnlyDictionary<string, JsonElement> changes, CancellationToken ct)
    {
        var (current, error) = await FetchAsync(rest, ct).ConfigureAwait(false);
        if (current is null) return (null, error);
        byte[]? patch;
        try { patch = Patch(current, changes); }
        catch (InvalidDataException) { return (null, "Discord sent settings Exo can't read."); }
        catch (ArgumentException) { patch = null; }
        if (patch is null) return (null, "That isn't a setting Discord takes.");
        var reply = await rest.SaveUserSettingsAsync(Convert.ToBase64String(patch), ct).ConfigureAwait(false);
        if (reply.Error is not null) return (null, reply.Error);
        var saved = Settings(reply.Body);
        try { return (Decode(saved ?? current), null); }
        catch (InvalidDataException) { return (null, "Discord sent settings Exo can't read."); }
    }

    private static byte[]? Settings(JsonElement? body)
    {
        if (body is not { ValueKind: JsonValueKind.Object } b || !b.TryGetProperty("settings", out var text) || text.ValueKind != JsonValueKind.String) return null;
        try { return Convert.FromBase64String(text.GetString() ?? ""); }
        catch (FormatException) { return null; }
    }
}
