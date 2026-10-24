using System.Text.Json;
using System.Text.Json.Nodes;
using ExoLauncher.Services.DiscordClient;
using static ExoLauncher.Services.DiscordClient.DiscordCache;

namespace ExoLauncher.Services;

/// <summary>
/// discord.admin's Server Settings pages beyond the server's own fields: Server Tag, Soundboard, Server Template,
/// Widget, Custom Invite Link, Access's rules screening, Welcome Screen, Enable Community and Onboarding. Each checks the
/// permission Discord asks for and only the fields and values Discord's own pages send.
/// </summary>
public sealed partial class WebHostBridge
{
    internal static readonly string[] PageActions =
    [
        "profile", "saveProfile", "sounds", "uploadSound", "editSound", "deleteSound", "templates", "createTemplate", "syncTemplate", "deleteTemplate",
        "widget", "saveWidget", "vanity", "saveVanity", "verification", "saveForm", "pending", "joinMode", "discovery", "saveDiscovery",
        "welcome", "saveWelcome", "community", "insights", "badgeArt", "onboarding", "saveOnboarding", "saveGuide",
    ];

    private static async Task<object> AdminPagesAsync(AdminCall call, string action)
    {
        var rest = call.Session.Rest;
        var expressions = call.Can(ManageExpressions | CreateExpressions);
        var manage = call.Can(ManageGuild);
        switch (action)
        {
            case "profile" when manage: return Answer(await rest.GuildProfileAsync(call.Guild, call.Ct).ConfigureAwait(false));
            // The Server Tag picker's badges, drawn from Discord's own client (null: the picker shows their names).
            case "badgeArt" when manage: return new { ok = true, message = (string?)null, data = await DiscordBadgeArt.ArtAsync(call.Ct).ConfigureAwait(false) };
            case "saveProfile" when manage: return await PageSaveProfileAsync(call).ConfigureAwait(false);
            case "sounds" when expressions: return Answer(await rest.SoundsAsync(call.Guild, call.Ct).ConfigureAwait(false));
            case "uploadSound" or "editSound" when expressions: return await PageSoundAsync(call, action == "editSound").ConfigureAwait(false);
            case "deleteSound" when expressions && call.IdOf("soundId") is { } sound:
                return Answer(await rest.DeleteSoundAsync(call.Guild, sound, call.Ct).ConfigureAwait(false));
            case "templates" when manage: return Answer(await rest.TemplatesAsync(call.Guild, call.Ct).ConfigureAwait(false));
            case "createTemplate" when manage && call.Text("name")?.Trim() is { Length: > 0 and <= 100 } name:
                var description = call.Text("description")?.Trim() is { Length: > 0 and <= 120 } d ? d : null;
                return Answer(await rest.CreateTemplateAsync(call.Guild, name, description, call.Ct).ConfigureAwait(false));
            case "syncTemplate" when manage && TemplateCode(call.Text("code")) is { } synced:
                return Answer(await rest.SyncTemplateAsync(call.Guild, synced, call.Ct).ConfigureAwait(false));
            case "deleteTemplate" when manage && TemplateCode(call.Text("code")) is { } deleted:
                return Answer(await rest.DeleteTemplateAsync(call.Guild, deleted, call.Ct).ConfigureAwait(false));
            case "widget" when manage: return Answer(await rest.WidgetAsync(call.Guild, call.Ct).ConfigureAwait(false));
            case "saveWidget" when manage:
                var widgetChannel = call.IdOf("channelId") is { } wc && InGuild(call, wc) ? wc : null;
                return Answer(await rest.SaveWidgetAsync(call.Guild, ReadBool(call.P, call.HasParams, "enabled") == true, widgetChannel, call.Ct).ConfigureAwait(false));
            case "vanity" when manage: return Answer(await rest.VanityAsync(call.Guild, call.Ct).ConfigureAwait(false));
            case "saveVanity" when manage && call.Text("code")?.Trim().ToLowerInvariant() is { Length: >= 2 and <= 32 } vanity && vanity.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-'):
                return Answer(await rest.SaveVanityAsync(call.Guild, vanity, call.Ct).ConfigureAwait(false));
            case "verification" when manage: return Answer(await rest.MemberVerificationAsync(call.Guild, call.Ct).ConfigureAwait(false));
            case "saveForm" when manage: return await PageSaveFormAsync(call).ConfigureAwait(false);
            case "pending" when manage:
                var pending = await rest.PendingApplicationsAsync(call.Guild, call.Ct).ConfigureAwait(false);
                var total = pending.Body is { ValueKind: JsonValueKind.Object } pb && pb.TryGetProperty("total", out var t) && t.TryGetInt32(out var n) ? n : 0;
                return new { ok = pending.Error is null, message = pending.Error, data = new { total } };
            case "joinMode" when manage: return await PageJoinModeAsync(call).ConfigureAwait(false);
            case "discovery" when manage: return await PageDiscoveryAsync(call).ConfigureAwait(false);
            case "saveDiscovery" when manage: return await PageSaveDiscoveryAsync(call).ConfigureAwait(false);
            case "insights" when call.Can(ViewInsights) || manage: return await PageInsightsAsync(call).ConfigureAwait(false);
            case "welcome" when manage:
                // A server that never set one answers Unknown Guild Welcome Screen (10069): an empty one, as Discord's page shows.
                var screen = await rest.WelcomeScreenAsync(call.Guild, call.Ct).ConfigureAwait(false);
                if (screen.Body is { ValueKind: JsonValueKind.Object } none && none.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number && code.GetInt32() == 10069)
                    return new { ok = true, message = (string?)null, data = new { description = (string?)null, welcome_channels = Array.Empty<object>() } };
                return Answer(screen);
            case "saveWelcome" when manage: return await PageSaveWelcomeAsync(call).ConfigureAwait(false);
            case "community" when manage: return await PageEnableCommunityAsync(call).ConfigureAwait(false);
            case "onboarding" when manage: return await PageOnboardingAsync(call).ConfigureAwait(false);
            // Onboarding changes channels and roles members get: Discord asks for Manage Roles too.
            case "saveOnboarding" when manage && call.Can(ManageRoles): return await PageSaveOnboardingAsync(call).ConfigureAwait(false);
            case "saveGuide" when manage && call.Can(ManageRoles): return await PageSaveGuideAsync(call).ConfigureAwait(false);
        }
        return NotAllowed;
    }

    private static string? TemplateCode(string? code) => code is { Length: > 1 and <= 32 } && code.All(char.IsAsciiLetterOrDigit) ? code : null;

    private static bool IsHex(string? color) => color is { Length: 7 } && color[0] == '#' && color[1..].All(char.IsAsciiHexDigit);

    // Server Tag: up to 4 letters, one of Discord's 41 badges and two colors (Discord picks them in pairs).
    private static async Task<object> PageSaveProfileAsync(AdminCall call)
    {
        var body = new JsonObject();
        if (call.Text("tag")?.Trim() is { Length: >= 2 and <= 4 } tag) body["tag"] = tag;
        if (ReadDouble(call.P, call.HasParams, "badge") is { } badge && badge is >= 0 and <= 40) body["badge"] = (int)badge;
        if (IsHex(call.Text("primary"))) body["badge_color_primary"] = call.Text("primary");
        if (IsHex(call.Text("secondary"))) body["badge_color_secondary"] = call.Text("secondary");
        // Access's Apply from Profile: a public profile (1) or one people can apply from (3).
        if (ReadDouble(call.P, call.HasParams, "visibility") is { } visibility && (visibility == 1 || visibility == 3)) body["visibility"] = (int)visibility;
        if (body.Count == 0) return new { ok = false, message = "Nothing changed." };
        return Answer(await call.Session.Rest.SaveGuildProfileAsync(call.Guild, body, call.Ct).ConfigureAwait(false));
    }

    // Upload Sound or edit one: a name of 2 to 32, volume 0 to 1, an emoji (a server one's id, or a Unicode one), and
    // for an upload the clip (MP3 or OGG up to 512 KB, Discord's limit).
    private static async Task<object> PageSoundAsync(AdminCall call, bool edit)
    {
        var body = new JsonObject();
        if (call.Text("name")?.Trim() is { Length: >= 2 and <= 32 } name) body["name"] = name;
        if (ReadDouble(call.P, call.HasParams, "volume") is { } volume && volume is >= 0 and <= 1) body["volume"] = volume;
        body["emoji_id"] = call.IdOf("emojiId");
        body["emoji_name"] = call.Text("emojiName") is { Length: > 0 and <= 32 } emoji ? emoji : null;
        var rest = call.Session.Rest;
        if (edit)
            return call.IdOf("soundId") is { } sound ? Answer(await rest.EditSoundAsync(call.Guild, sound, body, call.Ct).ConfigureAwait(false)) : NotAllowed;
        if (!body.ContainsKey("name")) return new { ok = false, message = "Give the sound a name of 2 to 32 characters." };
        if (TakeUpload(call.Text("uploadId")) is not { } clip || clip.Data.Length > 512 * 1024 || clip.Type is not ("audio/mpeg" or "audio/ogg"))
            return new { ok = false, message = "Pick an MP3 or OGG file up to 512 KB." };
        body["sound"] = DataUrl(clip);
        return Answer(await rest.UploadSoundAsync(call.Guild, body, call.Ct).ConfigureAwait(false));
    }

    // Access's rules and application, as Discord's page saves them: up to 5 fields with Discord's limits (Server Rules
    // once, 16 rules of 300; Short Answer and Paragraph questions of 300; Multiple Choice with 8 choices of 150), on or
    // off, and what becomes of applications waiting when Apply to Join ends (all approved or all rejected).
    private static async Task<object> PageSaveFormAsync(AdminCall call)
    {
        static JsonArray Strings(JsonElement field, string name, int count, int length) =>
            field.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
                ? new JsonArray([.. list.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "")
                    .Where(v => v.Length > 0 && v.Length <= length).Take(count).Select(v => (JsonNode)v)])
                : [];
        var fields = new JsonArray();
        if (call.Element("fields") is { ValueKind: JsonValueKind.Array } given)
            foreach (var field in given.EnumerateArray().Where(f => f.ValueKind == JsonValueKind.Object).Take(5))
            {
                var type = field.TryGetProperty("field_type", out var t) ? t.GetString() : null;
                var label = field.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString()!.Trim() : "";
                var question = label.Length is > 0 and <= 300;
                switch (type)
                {
                    case "TERMS" when !fields.Any(f => (string?)f!["field_type"] == "TERMS") && Strings(field, "values", 16, 300) is { Count: > 0 } rules:
                        fields.Add(new JsonObject { ["field_type"] = "TERMS", ["label"] = "Read and agree to the server rules", ["values"] = rules, ["required"] = true });
                        break;
                    case "TEXT_INPUT" or "PARAGRAPH" when question:
                        fields.Add(new JsonObject { ["field_type"] = type, ["label"] = label, ["required"] = true });
                        break;
                    case "MULTIPLE_CHOICE" when question && Strings(field, "choices", 8, 150) is { Count: > 0 } choices:
                        fields.Add(new JsonObject { ["field_type"] = type, ["label"] = label, ["choices"] = choices, ["required"] = true });
                        break;
                }
            }
        var body = new JsonObject { ["form_fields"] = fields, ["enabled"] = ReadBool(call.P, call.HasParams, "enabled") == true && fields.Count > 0 };
        if (call.Text("bulkAction") is "APPROVED" or "REJECTED") body["bulk_action"] = call.Text("bulkAction");
        return Answer(await call.Session.Rest.SaveMemberVerificationAsync(call.Guild, body, call.Ct).ConfigureAwait(false));
    }

    // Access's join mode, as Discord's page saves it: Discoverable is the DISCOVERABLE feature (with the description and
    // language Discovery shows) and ends Apply to Join; Invite Only and Apply to Join leave Discovery. Only an
    // administrator turns Server Discovery on or off, and it needs Community.
    private static async Task<object> PageJoinModeAsync(AdminCall call)
    {
        if (call.Session.Cache.ServerOf(call.Guild) is not { } server) return NotAllowed;
        var features = new HashSet<string>(server.Features, StringComparer.Ordinal);
        var discoverable = call.Text("mode") == "discoverable";
        if (discoverable != features.Contains("DISCOVERABLE") && !call.Can(Administrator))
            return new { ok = false, message = discoverable ? "Only server administrators can turn on Server Discovery." : "Only server administrators can turn off Server Discovery." };
        var body = new JsonObject();
        if (discoverable)
        {
            if (!features.Contains("COMMUNITY")) return new { ok = false, message = "Enable Community to access Server Discovery." };
            features.Add("DISCOVERABLE");
            features.Remove("MEMBER_VERIFICATION_MANUAL_APPROVAL");
            if (call.Text("description")?.Trim() is { Length: > 0 and <= 120 } description) body["description"] = description;
            if (call.Text("locale") is { Length: >= 2 and <= 6 } locale && locale.All(c => char.IsAsciiLetter(c) || char.IsAsciiDigit(c) || c == '-')) body["preferred_locale"] = locale;
        }
        else features.Remove("DISCOVERABLE");
        body["features"] = new JsonArray([.. features.Order(StringComparer.Ordinal).Select(f => (JsonNode)f)]);
        return Answer(await call.Session.Rest.SaveGuildAsync(call.Guild, body, call.Ct).ConfigureAwait(false));
    }

    // Discoverable's page: Discovery's checklist for this server, what it already set (category, tags, emoji
    // information) and Discord's categories, read together.
    private static async Task<object> PageDiscoveryAsync(AdminCall call)
    {
        var rest = call.Session.Rest;
        var requirements = rest.DiscoveryRequirementsAsync(call.Guild, call.Ct);
        var metadata = rest.DiscoveryMetadataAsync(call.Guild, call.Ct);
        var categories = rest.DiscoveryCategoriesAsync(call.Ct);
        var (r, m, c) = (await requirements.ConfigureAwait(false), await metadata.ConfigureAwait(false), await categories.ConfigureAwait(false));
        var error = r.Error ?? m.Error ?? c.Error;
        return new { ok = error is null, message = error, data = new { requirements = r.Body, metadata = m.Body, categories = c.Body } };
    }

    // Discoverable's category (one of Discord's primary ones), up to 10 tags of 30 and whether a server emoji or sticker
    // shows where it's from when pressed.
    private static async Task<object> PageSaveDiscoveryAsync(AdminCall call)
    {
        var body = new JsonObject();
        if (ReadDouble(call.P, call.HasParams, "category") is { } category && category is >= 0 and <= 10000) body["primary_category_id"] = (int)category;
        if (call.Element("keywords") is { ValueKind: JsonValueKind.Array } keywords)
            body["keywords"] = new JsonArray([.. keywords.EnumerateArray().Select(k => k.ValueKind == JsonValueKind.String ? k.GetString()!.Trim() : "")
                .Where(k => k.Length is > 0 and <= 30).Distinct(StringComparer.OrdinalIgnoreCase).Take(10).Select(k => (JsonNode)k)]);
        if (ReadBool(call.P, call.HasParams, "emoji") is { } emoji) body["emoji_discoverability_enabled"] = emoji;
        if (body.Count == 0) return new { ok = false, message = "Nothing changed." };
        return Answer(await call.Session.Rest.SaveDiscoveryMetadataAsync(call.Guild, body, call.Ct).ConfigureAwait(false));
    }

    // Server Insights' overview, as Discord's settings page reads it: this week's and last week's visitors and
    // communicators, new members, and new members' retention. A server too small for insights says so (30029).
    private static async Task<object> PageInsightsAsync(AdminCall call)
    {
        var rest = call.Session.Rest;
        var end = DateTimeOffset.UtcNow;
        var start = end.AddDays(-((int)end.DayOfWeek + 1) - 42);
        var engagement = rest.InsightsAsync(call.Guild, "engagement/overview", start, end, call.Ct);
        var growth = rest.InsightsAsync(call.Guild, "growth-activation/overview", start, end, call.Ct);
        var retention = rest.InsightsAsync(call.Guild, "growth-activation/retention", start, end, call.Ct);
        var replies = new[] { await engagement.ConfigureAwait(false), await growth.ConfigureAwait(false), await retention.ConfigureAwait(false) };
        static JsonElement? Weeks(DiscordRest.Reply reply) => reply.Error is null && reply.Body is { ValueKind: JsonValueKind.Array } weeks ? weeks : null;
        var small = replies.Any(r => r.Body is { ValueKind: JsonValueKind.Object } b && b.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number && code.GetInt32() == 30029);
        var error = small ? null : replies.Select(r => r.Error).FirstOrDefault(e => e is not null);
        return new { ok = error is null, message = error, data = new { small, engagement = Weeks(replies[0]), growth = Weeks(replies[1]), retention = Weeks(replies[2]) } };
    }

    // Welcome Screen: on or off, the server's description (140), and up to 5 channels with a line (50) and an emoji each.
    private static async Task<object> PageSaveWelcomeAsync(AdminCall call)
    {
        var channels = new JsonArray();
        if (call.Element("channels") is { ValueKind: JsonValueKind.Array } list)
            foreach (var c in list.EnumerateArray().Take(5))
            {
                if (c.ValueKind != JsonValueKind.Object || !c.TryGetProperty("channel_id", out var id) || id.GetString() is not { } channelId || !InGuild(call, DiscordRest.Id(channelId))) continue;
                channels.Add(new JsonObject
                {
                    ["channel_id"] = channelId,
                    ["description"] = c.TryGetProperty("description", out var line) && line.GetString() is { Length: <= 50 } text ? text : "",
                    ["emoji_id"] = c.TryGetProperty("emoji_id", out var eid) && eid.GetString() is { } emojiId ? DiscordRest.Id(emojiId) : null,
                    ["emoji_name"] = c.TryGetProperty("emoji_name", out var en) && en.GetString() is { Length: <= 32 } emojiName ? emojiName : null,
                });
            }
        var body = new JsonObject
        {
            ["enabled"] = ReadBool(call.P, call.HasParams, "enabled") == true,
            ["description"] = call.Text("description") is { Length: <= 140 } d ? d : null,
            ["welcome_channels"] = channels,
        };
        return Answer(await call.Session.Rest.SaveWelcomeScreenAsync(call.Guild, body, call.Ct).ConfigureAwait(false));
    }

    // Enable Community, as Discord's setup saves it: COMMUNITY with its rules and updates channels ("1" makes a new one,
    // as Discord's choice does), a verification level above None, the filter for everyone and @mentions by default.
    private static async Task<object> PageEnableCommunityAsync(AdminCall call)
    {
        if (call.Session.Cache.ServerOf(call.Guild) is not { } server) return NotAllowed;
        static JsonNode? Channel(AdminCall call, string name) => call.Text(name) is "1" ? "1" : call.IdOf(name) is { } id && InGuild(call, id) ? id : null;
        if (Channel(call, "rulesChannelId") is not { } rules || Channel(call, "updatesChannelId") is not { } updates)
            return new { ok = false, message = "Pick a rules channel and a community updates channel." };
        server.Features.Add("COMMUNITY");
        var level = (int)(ReadDouble(call.P, call.HasParams, "verificationLevel") ?? 1);
        var body = new JsonObject
        {
            ["features"] = new JsonArray([.. server.Features.Order(StringComparer.Ordinal).Select(f => (JsonNode)f)]),
            ["rules_channel_id"] = rules,
            ["public_updates_channel_id"] = updates,
            ["verification_level"] = Math.Clamp(level, 1, 4),
            ["explicit_content_filter"] = 2,
            ["default_message_notifications"] = 1,
        };
        return Answer(await call.Session.Rest.SaveGuildAsync(call.Guild, body, call.Ct).ConfigureAwait(false));
    }
}
