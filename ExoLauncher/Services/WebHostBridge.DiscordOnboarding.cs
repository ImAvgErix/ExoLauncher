using System.Text.Json;
using System.Text.Json.Nodes;
using ExoLauncher.Services.DiscordClient;

namespace ExoLauncher.Services;

/// <summary>
/// discord.admin's Onboarding page (a Community server's; Discord asks for Manage Server and Manage Roles): the
/// questions that give new members channels and roles before or after they join, the Default Channels they start
/// with, on or off and its mode, and the Server Guide (welcome sign, to-dos, resource pages). Only the fields and
/// limits Discord's own page sends pass; Discord checks the rest again.
/// </summary>
public sealed partial class WebHostBridge
{
    // Discord's limits (its client's): questions, those asked before joining, answers, and the Server Guide's lists.
    private const int MaxPrompts = 15, MaxPrejoin = 4, MaxAnswers = 50, MaxTodos = 5, MaxResources = 7, MaxDefaultChannels = 500;

    // The page's read: onboarding, the Server Guide, and which channels @everyone can see and talk in (the requirement).
    private static async Task<object> PageOnboardingAsync(AdminCall call)
    {
        var rest = call.Session.Rest;
        var onboarding = rest.OnboardingAsync(call.Guild, call.Ct);
        var guide = await rest.ServerGuideAsync(call.Guild, call.Ct).ConfigureAwait(false);
        var reply = await onboarding.ConfigureAwait(false);
        if (reply.Error is not null) return new { ok = false, message = reply.Error };
        var (seen, chats) = call.Session.Cache.EveryoneChannels(call.Guild);
        // me: the welcome sign's author when it names none yet (Discord's page asks who signs it).
        return new { ok = true, message = (string?)null, data = new { onboarding = reply.Body, guide = guide.Error is null ? guide.Body : null, seen, chats, me = call.Session.Cache.Me?.Id } };
    }

    // One part at a time, as Discord's page saves: on or off, the mode, the Default Channels or the questions.
    private static async Task<object> PageSaveOnboardingAsync(AdminCall call)
    {
        var body = new JsonObject();
        if (call.Element("enabled") is { ValueKind: JsonValueKind.True or JsonValueKind.False } enabled) body["enabled"] = enabled.GetBoolean();
        if (ReadDouble(call.P, call.HasParams, "mode") is { } mode && mode is 0 or 1) body["mode"] = (int)mode;
        if (call.Element("defaultChannelIds") is { ValueKind: JsonValueKind.Array } channels) body["default_channel_ids"] = Snowflakes(channels, MaxDefaultChannels);
        if (call.Element("prompts") is { } prompts)
        {
            if (OnboardingPrompts(prompts) is not { } list) return new { ok = false, message = "A question needs a title and answers that give a channel or role." };
            body["prompts"] = list;
        }
        if (body.Count == 0) return new { ok = false, message = "Nothing changed." };
        return Answer(await call.Session.Rest.SaveOnboardingAsync(call.Guild, body, call.Ct).ConfigureAwait(false));
    }

    private static async Task<object> PageSaveGuideAsync(AdminCall call)
    {
        if (ServerGuide(call.Element("guide"), call.Guild) is not { } guide) return new { ok = false, message = "Discord didn't take that Server Guide." };
        return Answer(await call.Session.Rest.SaveServerGuideAsync(call.Guild, guide, call.Ct).ConfigureAwait(false));
    }

    private static string? Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Flag(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    // An id Discord made (a snowflake) or a new one Discord's page makes (the time in ms): digits only.
    private static string? NumberId(string? id) => id is { Length: > 0 and <= 20 } && id.All(char.IsAsciiDigit) ? id : null;

    private static JsonArray Snowflakes(JsonElement list, int max) =>
        new([.. list.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? NumberId(e.GetString()) : null).OfType<string>().Distinct().Take(max).Select(id => (JsonNode)id)]);

    private static JsonArray Snowflakes(JsonElement owner, string name, int max) =>
        owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array ? Snowflakes(list, max) : [];

    /// <summary>
    /// Onboarding's questions as Discord saves them: up to 15 (4 asked before joining), each a title of up to 100 and up to
    /// 50 answers (a title of up to 50, a description of up to 100, the channels and roles it gives (one at least) and an
    /// emoji), shown as a dropdown from 13 answers. Null when one isn't whole, so nothing half-made reaches Discord.
    /// </summary>
    internal static JsonArray? OnboardingPrompts(JsonElement prompts)
    {
        if (prompts.ValueKind != JsonValueKind.Array || prompts.GetArrayLength() > MaxPrompts) return null;
        var result = new JsonArray();
        foreach (var p in prompts.EnumerateArray())
        {
            if (NumberId(Str(p, "id")) is not { } id || Str(p, "title")?.Trim() is not { Length: > 0 and <= 100 } title) return null;
            if (!p.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Array || options.GetArrayLength() is 0 or > MaxAnswers) return null;
            var answers = new JsonArray();
            foreach (var o in options.EnumerateArray())
            {
                if (NumberId(Str(o, "id")) is not { } optionId || Str(o, "title")?.Trim() is not { Length: > 0 and <= 50 } answer) return null;
                var channels = Snowflakes(o, "channel_ids", 500);
                var roles = Snowflakes(o, "role_ids", 250);
                if (channels.Count + roles.Count == 0) return null;
                // An answer's emoji as Discord sends it (emoji) or as its page saves it (emoji_id, emoji_name, emoji_animated).
                var emoji = o.TryGetProperty("emoji", out var e) && e.ValueKind == JsonValueKind.Object ? e : default;
                var emojiId = NumberId(Str(emoji, "id") ?? Str(o, "emoji_id"));
                answers.Add(new JsonObject
                {
                    ["id"] = optionId,
                    ["title"] = answer,
                    ["description"] = Str(o, "description") is { Length: <= 100 } description ? description : "",
                    ["channel_ids"] = channels,
                    ["role_ids"] = roles,
                    ["emoji_id"] = emojiId,
                    ["emoji_name"] = (Str(emoji, "name") ?? Str(o, "emoji_name")) is { Length: > 0 and <= 32 } name ? name : null,
                    ["emoji_animated"] = emojiId is not null && (Flag(emoji, "animated") || Flag(o, "emoji_animated")),
                });
            }
            var prejoin = Flag(p, "in_onboarding");
            result.Add(new JsonObject
            {
                ["id"] = id,
                ["title"] = title,
                ["options"] = answers,
                ["single_select"] = Flag(p, "single_select"),
                ["required"] = prejoin && Flag(p, "required"),
                ["in_onboarding"] = prejoin,
                ["type"] = answers.Count >= 13 ? 1 : 0,
            });
        }
        return result.Count(p => (bool)p!["in_onboarding"]!) > MaxPrejoin ? null : result;
    }

    /// <summary>
    /// The Server Guide as Discord saves it: the welcome sign (its author and up to 300 characters), up to 5 to-dos (a
    /// channel, what to do there (visit 0 or talk 1), a title of up to 60 and an emoji or Discord's thumbnail) and up to 7
    /// resource pages (a channel, a title of up to 30, a description of up to 200), and whether it's on. A channel at most
    /// once in each list. Null when the guide isn't one.
    /// </summary>
    internal static JsonObject? ServerGuide(JsonElement? guide, string guildId)
    {
        if (guide is not { ValueKind: JsonValueKind.Object } g) return null;
        var welcome = g.TryGetProperty("welcome_message", out var w) && w.ValueKind == JsonValueKind.Object ? w : default;
        if ((Str(welcome, "message") ?? "") is not { Length: <= 300 } message) return null;
        static JsonObject Emoji(JsonElement item)
        {
            var emoji = item.TryGetProperty("emoji", out var e) && e.ValueKind == JsonValueKind.Object ? e : default;
            var result = new JsonObject();
            if (NumberId(Str(emoji, "id")) is { } id) { result["id"] = id; result["animated"] = Flag(emoji, "animated"); }
            if (Str(emoji, "name") is { Length: > 0 and <= 32 } name) result["name"] = name;
            return result;
        }
        // Discord's thumbnail for a to-do or resource: its hash, kept as Discord sent it.
        static string? Icon(JsonElement item) => Str(item, "icon") is { Length: > 0 and <= 64 } icon && icon.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') ? icon : null;
        JsonArray? Items(string name, int max, int titleMax, bool todo)
        {
            var list = new JsonArray();
            if (!g.TryGetProperty(name, out var items) || items.ValueKind != JsonValueKind.Array) return list;
            if (items.GetArrayLength() > max) return null;
            var channels = new HashSet<string>();
            foreach (var item in items.EnumerateArray())
            {
                if (NumberId(Str(item, "channel_id")) is not { } channel || !channels.Add(channel) || Str(item, "title")?.Trim() is not { Length: > 0 } title || title.Length > titleMax) return null;
                var entry = new JsonObject { ["channel_id"] = channel, ["title"] = title, ["description"] = Str(item, "description") is { Length: <= 200 } d ? d : "", ["emoji"] = Emoji(item) };
                if (Icon(item) is { } icon) entry["icon"] = icon;
                if (todo)
                {
                    if (!item.TryGetProperty("action_type", out var type) || !type.TryGetInt32(out var kind) || kind is not (0 or 1)) return null;
                    entry["action_type"] = kind;
                }
                list.Add(entry);
            }
            return list;
        }
        if (Items("new_member_actions", MaxTodos, 60, true) is not { } todos || Items("resource_channels", MaxResources, 30, false) is not { } resources) return null;
        return new JsonObject
        {
            ["guild_id"] = guildId,
            ["welcome_message"] = new JsonObject { ["author_ids"] = Snowflakes(welcome, "author_ids", 1), ["message"] = message },
            ["new_member_actions"] = todos,
            ["resource_channels"] = resources,
            ["enabled"] = Flag(g, "enabled"),
        };
    }
}
