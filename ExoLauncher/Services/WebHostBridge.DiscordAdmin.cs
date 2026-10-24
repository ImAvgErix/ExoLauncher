using System.Text.Json;
using System.Text.Json.Nodes;
using ExoLauncher.Services.DiscordClient;
using static ExoLauncher.Services.DiscordClient.DiscordCache;

namespace ExoLauncher.Services;

/// <summary>
/// discord.admin: Server Settings, Edit Channel and the person menu. Every action names its server
/// (or channel) and is checked against what the user may do there before it reaches Discord: the
/// permission, the role order for people and roles, and only the fields Discord's own settings
/// change. Pictures come in through discord.upload and go to Discord as Discord's client sends them.
/// </summary>
public sealed partial class WebHostBridge
{
    private sealed record AdminCall(DiscordSession Session, string Guild, ulong Perms, JsonElement P, bool HasParams, CancellationToken Ct)
    {
        internal string? Text(string name) => ReadString(P, HasParams, name);
        internal string? IdOf(string name) => Text(name) is { Length: > 0 } id ? DiscordRest.Id(id) : null;
        internal bool Can(ulong permission) => (Perms & permission) != 0;
        internal JsonElement? Element(string name) => HasParams && P.TryGetProperty(name, out var e) ? e : null;
    }

    private static readonly object NotAllowed = new { ok = false, message = "You don't have permission to do that here." };

    private static async Task<object> DiscordAdminAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        if (DiscordSession.Current is not { } session) return new { ok = false, message = "Discord isn't connected." };
        var action = ReadString(p, hasParams, "action") ?? "";
        // Edit Channel's actions name a channel; everything else names a server.
        if (action.StartsWith("channel", StringComparison.Ordinal)) return await DiscordChannelAdminAsync(session, action, p, hasParams, ct).ConfigureAwait(false);
        // The answer to Discord's "it's really you" check: it finishes the request that asked for it.
        if (action == "mfa") return await AdminMfaAsync(session, p, hasParams, ct).ConfigureAwait(false);
        if (ReadString(p, hasParams, "guildId") is not { Length: > 0 } rawGuild) return new { ok = false, message = "Pick a server." };
        var guild = DiscordRest.Id(rawGuild);
        if (session.Cache.ServerPermissions(guild) is not { } perms) return new { ok = false, message = "That server isn't one of yours." };
        var call = new AdminCall(session, guild, perms, p, hasParams, ct);
        return action switch
        {
            "person" => AdminPerson(call),
            "guild" => Answer(await session.Rest.GuildCountsAsync(guild, ct).ConfigureAwait(false)),
            "saveGuild" => call.Can(ManageGuild) ? await AdminSaveGuildAsync(call).ConfigureAwait(false) : NotAllowed,
            "guildMfa" or "pruneAdmins" or "deleteGuild" or "transfer" or "transferCode" => await AdminOwnerAsync(call, action).ConfigureAwait(false),
            "raidAlerts" => call.Can(ManageGuild) ? await AdminRaidAlertsAsync(call).ConfigureAwait(false) : NotAllowed,
            _ when PageActions.Contains(action) => await AdminPagesAsync(call, action).ConfigureAwait(false),
            "roles" or "createRole" or "saveRole" or "deleteRole" or "moveRoles" or "roleMembers" or "searchMembers" or "addRoleMembers" or "roleLinks" or "saveRoleLinks" => await AdminRolesAsync(call, action).ConfigureAwait(false),
            "addRole" or "removeRole" or "nick" or "timeout" or "kick" or "ban" or "voice" => await AdminPeopleAsync(call, action).ConfigureAwait(false),
            "emojis" or "uploadEmoji" or "renameEmoji" or "deleteEmoji" or "stickers" or "uploadSticker" or "editSticker" or "deleteSticker" =>
                call.Can(ManageExpressions | CreateExpressions) ? await AdminExpressionsAsync(call, action).ConfigureAwait(false) : NotAllowed,
            _ => await AdminModerationAsync(call, action).ConfigureAwait(false),
        };
    }

    // mfa: Discord wants the person to verify first; the page answers with action "mfa" and this id.
    private static object Answer(DiscordRest.Reply reply) => new
    {
        ok = reply.Error is null,
        message = reply.Error,
        data = reply.Body,
        mfa = reply.Mfa is { } ask ? new { id = ask.Id, methods = ask.Methods } : null,
    };

    // Verify with a code (step finish), text one to the phone (sms), or give up (cancel). The ticket and token stay in DiscordRest.
    private static async Task<object> AdminMfaAsync(DiscordSession session, JsonElement p, bool hasParams, CancellationToken ct)
    {
        if (ReadString(p, hasParams, "id") is not { Length: 32 } id || !id.All(char.IsAsciiHexDigit)) return new { ok = false, message = "That check ran out of time. Try again." };
        switch (ReadString(p, hasParams, "step"))
        {
            case "cancel":
                session.Rest.CancelMfa(id);
                return new { ok = true, message = (string?)null };
            case "sms":
                return Answer(await session.Rest.SendMfaSmsAsync(id, ct).ConfigureAwait(false));
            case "finish" when ReadString(p, hasParams, "method") is { } method && ReadString(p, hasParams, "code")?.Trim() is { Length: > 0 and <= 128 } code:
                return Answer(await session.Rest.FinishMfaAsync(id, method, code, ct).ConfigureAwait(false));
        }
        return new { ok = false, message = "Type the code first." };
    }

    // What only the owner may do, as Discord offers it: require 2FA of moderators (with 2FA on their own account),
    // restrict prune to admins (not on a Community server) and delete the server.
    private static async Task<object> AdminOwnerAsync(AdminCall call, string action)
    {
        var cache = call.Session.Cache;
        if (cache.ServerOf(call.Guild) is not { Owner: true } server) return new { ok = false, message = "Only the server owner can do that." };
        var rest = call.Session.Rest;
        var on = ReadBool(call.P, call.HasParams, "on") == true;
        switch (action)
        {
            case "guildMfa" when !cache.MfaEnabled:
                return new { ok = false, message = "To change this setting, first enable 2FA on your account." };
            case "guildMfa":
                return Answer(await rest.GuildMfaAsync(call.Guild, on, call.Ct).ConfigureAwait(false));
            case "pruneAdmins" when !server.Features.Contains("COMMUNITY"):
                // Discord saves it as a server feature: the features it has, with this one added or taken.
                if (on) server.Features.Add("PRUNE_REQUIRES_ADMIN"); else server.Features.Remove("PRUNE_REQUIRES_ADMIN");
                var features = new JsonArray([.. server.Features.Order(StringComparer.Ordinal).Select(f => (JsonNode)f)]);
                return Answer(await rest.SaveGuildAsync(call.Guild, new JsonObject { ["features"] = features }, call.Ct).ConfigureAwait(false));
            case "deleteGuild":
                return Answer(await rest.DeleteGuildAsync(call.Guild, call.Ct).ConfigureAwait(false));
            case "transferCode":
                return Answer(await rest.TransferCodeAsync(call.Guild, call.Ct).ConfigureAwait(false));
            case "transfer" when call.IdOf("userId") is { } heir && heir != cache.Me?.Id:
                var pin = call.Text("code")?.Trim() is { Length: > 0 and <= 16 } c ? c : null;
                return Answer(await rest.TransferOwnershipAsync(call.Guild, heir, pin, call.Ct).ConfigureAwait(false));
        }
        return NotAllowed;
    }

    // The person menu: what Exo knows of them, at once. Someone not seen as a member yet is asked of the gateway, as
    // Discord's client asks for a member it lacks; the member arrives as discord.colors (a tenth of a second, not a
    // profile's round trip) and the menu adds their roles and the moderator actions that need their rank.
    private static object AdminPerson(AdminCall call)
    {
        if (call.IdOf("userId") is not { } user) return new { ok = false };
        var person = call.Session.Cache.Person(call.Guild, user, out var unknown);
        if (unknown) call.Session.RequestMembers(call.Guild, [user]);
        return new { ok = true, message = (string?)null, data = person };
    }

    // Server Profile, Engagement and Safety Setup: only the fields they change, each as Discord takes it.
    private static async Task<object> AdminSaveGuildAsync(AdminCall call)
    {
        if (call.Element("changes") is not { ValueKind: JsonValueKind.Object } changes) return new { ok = false, message = "Nothing changed." };
        var body = new JsonObject();
        foreach (var field in changes.EnumerateObject())
        {
            var v = field.Value;
            switch (field.Name)
            {
                case "name" when v.GetString()?.Trim() is { Length: >= 2 and <= 100 } name: body["name"] = name; break;
                case "description" when v.ValueKind == JsonValueKind.Null || v.GetString() is { Length: <= 120 }: body["description"] = v.ValueKind == JsonValueKind.Null ? null : v.GetString(); break;
                case "icon" or "banner":
                    // null removes it; an upload id is a new picture.
                    if (v.ValueKind == JsonValueKind.Null) body[field.Name] = null;
                    else if (TakeUpload(v.GetString()) is { } upload) body[field.Name] = DataUrl(upload);
                    break;
                case "afk_channel_id" or "system_channel_id" or "safety_alerts_channel_id" or "rules_channel_id" or "public_updates_channel_id":
                    body[field.Name] = v.ValueKind == JsonValueKind.Null ? null : DiscordRest.Id(v.GetString() ?? "");
                    break;
                case "afk_timeout" when v.TryGetInt32(out var afk) && afk is 60 or 300 or 900 or 1800 or 3600: body["afk_timeout"] = afk; break;
                case "system_channel_flags" when v.TryGetInt32(out var flags) && flags >= 0: body["system_channel_flags"] = flags; break;
                case "default_message_notifications" when v.TryGetInt32(out var n) && n is 0 or 1: body["default_message_notifications"] = n; break;
                case "verification_level" when v.TryGetInt32(out var level) && level is >= 0 and <= 4: body["verification_level"] = level; break;
                case "explicit_content_filter" when v.TryGetInt32(out var filter) && filter is >= 0 and <= 2: body["explicit_content_filter"] = filter; break;
                // Access's Age-Restricted Server: the owner's content level, age restricted (3) or not (0).
                case "owner_configured_content_level" when v.TryGetInt32(out var content) && content is 0 or 3: body["owner_configured_content_level"] = content; break;
            }
        }
        if (body.Count == 0) return new { ok = false, message = "Nothing changed." };
        return Answer(await call.Session.Rest.SaveGuildAsync(call.Guild, body, call.Ct).ConfigureAwait(false));
    }

    private static async Task<object> AdminRolesAsync(AdminCall call, string action)
    {
        var rest = call.Session.Rest;
        var cache = call.Session.Cache;
        if (action == "roles")
        {
            // Both at once: the page waits for one round trip, not two.
            var rolesCall = rest.RolesAsync(call.Guild, call.Ct);
            var counts = await rest.RoleMemberCountsAsync(call.Guild, call.Ct).ConfigureAwait(false);
            var roles = await rolesCall.ConfigureAwait(false);
            // top: the user's highest role position (owner above all), so only roles below it can be dragged or edited.
            return new { ok = roles.Error is null, message = roles.Error, data = new { roles = roles.Body, counts = counts.Body, top = cache.TopOf(call.Guild) } };
        }
        var role = call.IdOf("roleId");
        // Edit Channel's Add members needs Manage Permissions there (Manage Roles in that channel), not on the server.
        var inChannel = call.IdOf("channelId") is { } channel && cache.CanSee(channel) && cache.GuildOf(channel) == call.Guild && (cache.ChannelPermissions(channel) & ManageRoles) != 0;
        if (!call.Can(ManageRoles) && !(action == "searchMembers" && inChannel)) return NotAllowed;
        switch (action)
        {
            case "createRole":
                return Answer(await rest.CreateRoleAsync(call.Guild, call.Text("name") is { Length: > 0 and <= 100 } name ? name : "new role", call.Ct).ConfigureAwait(false));
            case "deleteRole" when role is not null && cache.CanAssign(call.Guild, role):
                return Answer(await rest.DeleteRoleAsync(call.Guild, role, call.Ct).ConfigureAwait(false));
            case "saveRole" when role is not null && cache.CanEditRole(call.Guild, role):
                return await AdminSaveRoleAsync(call, role).ConfigureAwait(false);
            case "roleLinks" when role is not null:
                return Answer(await rest.RoleLinksAsync(call.Guild, role, call.Ct).ConfigureAwait(false));
            case "saveRoleLinks" when role is not null && role != call.Guild && cache.CanEditRole(call.Guild, role):
                return Answer(await rest.SaveRoleLinksAsync(call.Guild, role, RoleLinks(call.Element("groups")), call.Ct).ConfigureAwait(false));
            case "moveRoles" when call.Element("positions") is { ValueKind: JsonValueKind.Array } list:
                var positions = list.EnumerateArray().Select(e => (Id: DiscordRest.Id(e.GetProperty("id").GetString() ?? ""), Position: e.GetProperty("position").GetInt32()))
                    .Where(e => e.Id != call.Guild).ToList();
                return Answer(await rest.RolePositionsAsync(call.Guild, positions, call.Ct).ConfigureAwait(false));
            case "roleMembers" or "searchMembers" when role is not null || call.Text("query") is { Length: > 1 }:
                var found = await rest.SearchMembersAsync(call.Guild, call.Text("query"), role, call.Ct).ConfigureAwait(false);
                if (found.Body is { } body) cache.RememberMembers(call.Guild, body);
                return Answer(found);
            case "addRoleMembers" when role is not null && cache.CanAssign(call.Guild, role) && call.Element("userIds") is { ValueKind: JsonValueKind.Array } ids:
                var users = ids.EnumerateArray().Select(e => DiscordRest.Id(e.GetString() ?? "")).Take(30).ToList();
                return Answer(await rest.AddRoleMembersAsync(call.Guild, role, users, call.Ct).ConfigureAwait(false));
        }
        return NotAllowed;
    }

    // Linked Roles' requirements, as Discord's Links tab saves them: groups (one for All, one per account for Any) of up
    // to 10 requirements: an account to connect (Discord's connection type, or "application" with the app's id) and, for
    // one of its numbers or checks (Discord's, or an app's own keys), how it compares (1 equal, 2 not equal, 3 less than,
    // 4 greater than) with what.
    internal static JsonArray RoleLinks(JsonElement? groups)
    {
        static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var result = new JsonArray();
        var count = 0;
        if (groups is not { ValueKind: JsonValueKind.Array } list) return result;
        foreach (var group in list.EnumerateArray().Where(g => g.ValueKind == JsonValueKind.Array))
        {
            var items = new JsonArray();
            foreach (var item in group.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object))
            {
                if (count >= 10 || Text(item, "connection_type") is not { Length: >= 2 and <= 40 } type || !type.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '_' or '-')) continue;
                var named = Text(item, "connection_metadata_field");
                var field = named is { Length: > 0 and <= 50 } f && f.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_') ? f : null;
                // A key that isn't one leaves the requirement out, rather than asking for the account alone.
                if (named is not null && field is null) continue;
                var op = item.TryGetProperty("operator", out var o) && o.TryGetInt32(out var oi) && oi is >= 1 and <= 4 ? oi : (int?)null;
                var value = Text(item, "value") is { Length: > 0 and <= 20 } v && v.TrimStart('-') is { Length: > 0 } digits && digits.All(char.IsAsciiDigit) && v.LastIndexOf('-') <= 0 ? v : null;
                var app = Text(item, "application_id") is { Length: > 0 and <= 20 } a && a.All(char.IsAsciiDigit) ? a : null;
                // A check needs all three: the field, how it compares and the value; an app, its id.
                if ((field is not null && (op is null || value is null)) || (type == "application" && app is null)) continue;
                items.Add(new JsonObject { ["connection_type"] = type, ["connection_metadata_field"] = field, ["operator"] = field is null ? null : op, ["value"] = field is null ? null : value, ["application_id"] = app });
                count++;
            }
            if (items.Count > 0) result.Add(items);
        }
        return result;
    }

    // A role's name, color, Display separately, Allow anyone to @mention and permissions.
    private static async Task<object> AdminSaveRoleAsync(AdminCall call, string role)
    {
        if (call.Element("changes") is not { ValueKind: JsonValueKind.Object } changes) return new { ok = false, message = "Nothing changed." };
        var body = new JsonObject();
        foreach (var field in changes.EnumerateObject())
        {
            var v = field.Value;
            switch (field.Name)
            {
                // An app's or Server Booster's role keeps the name Discord gave it.
                case "name" when role != call.Guild && !call.Session.Cache.IsManagedRole(call.Guild, role) && v.GetString()?.Trim() is { Length: > 0 and <= 100 } name: body["name"] = name; break;
                case "color" when v.TryGetInt32(out var color) && color is >= 0 and <= 0xFFFFFF:
                    body["color"] = color;
                    body["colors"] = new JsonObject { ["primary_color"] = color, ["secondary_color"] = null, ["tertiary_color"] = null };
                    break;
                case "hoist" or "mentionable" when v.ValueKind is JsonValueKind.True or JsonValueKind.False: body[field.Name] = v.GetBoolean(); break;
                case "permissions" when ulong.TryParse(v.GetString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var perms):
                    body["permissions"] = perms.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    break;
            }
        }
        if (body.Count == 0) return new { ok = false, message = "Nothing changed." };
        return Answer(await call.Session.Rest.SaveRoleAsync(call.Guild, role, body, call.Ct).ConfigureAwait(false));
    }

    // What a moderator does to one person: their roles, nickname, a timeout, kick, ban, and their voice.
    private static async Task<object> AdminPeopleAsync(AdminCall call, string action)
    {
        if (call.IdOf("userId") is not { } user) return new { ok = false };
        var cache = call.Session.Cache;
        var rest = call.Session.Rest;
        var me = user == cache.Me?.Id;
        var outranks = cache.Outranks(call.Guild, user);
        var reason = call.Text("reason") is { Length: > 0 and <= 512 } r ? r : null;
        switch (action)
        {
            // One role given or taken (Discord's single-role endpoints), so a change never sends back a stale list.
            case "removeRole" when call.Can(ManageRoles) && (me || outranks) && call.IdOf("roleId") is { } taken && cache.CanAssign(call.Guild, taken):
                var removed = await rest.RemoveMemberRoleAsync(call.Guild, user, taken, call.Ct).ConfigureAwait(false);
                if (removed.Error is null && cache.RolesOf(call.Guild, user) is { } current) cache.SetMember(call.Guild, user, [.. current.Where(id => id != taken)], null, false);
                return Answer(removed);
            case "addRole" when call.Can(ManageRoles) && (me || outranks) && call.IdOf("roleId") is { } given && cache.CanAssign(call.Guild, given):
                var added = await rest.AddMemberRoleAsync(call.Guild, user, given, call.Ct).ConfigureAwait(false);
                if (added.Error is null && cache.RolesOf(call.Guild, user) is { } held && !held.Contains(given)) cache.SetMember(call.Guild, user, [.. held, given], null, false);
                return Answer(added);
            case "nick" when me ? call.Can(ChangeNickname | ManageNicknames) : outranks && call.Can(ManageNicknames):
                var nick = call.Text("nick")?.Trim() is { Length: > 0 and <= 32 } n ? n : null;
                var named = me
                    ? await rest.SaveOwnNickAsync(call.Guild, nick, call.Ct).ConfigureAwait(false)
                    : await rest.SaveMemberAsync(call.Guild, user, new JsonObject { ["nick"] = nick }, call.Ct).ConfigureAwait(false);
                if (named.Error is null) cache.SetMember(call.Guild, user, null, nick, true);
                return Answer(named);
            case "timeout" when outranks && call.Can(ModerateMembers):
                // Discord's lengths (60 seconds to a week); 0 lifts the timeout.
                var minutes = (int)(ReadDouble(call.P, call.HasParams, "minutes") ?? -1);
                if (minutes is not (0 or 1 or 5 or 10 or 60 or 1440 or 10080)) return new { ok = false, message = "Choose a length." };
                var until = minutes == 0 ? (DateTimeOffset?)null : DateTimeOffset.UtcNow.AddMinutes(minutes);
                var timed = await rest.TimeoutAsync(call.Guild, user, until, reason, call.Ct).ConfigureAwait(false);
                if (timed.Error is null) cache.SetTimeout(call.Guild, user, until);
                return Answer(timed);
            case "kick" when outranks && call.Can(Kick):
                return Answer(await rest.KickAsync(call.Guild, user, reason, call.Ct).ConfigureAwait(false));
            case "ban" when outranks && call.Can(Ban):
                return Answer(await rest.BanAsync(call.Guild, user, (int)(ReadDouble(call.P, call.HasParams, "deleteSeconds") ?? 0), reason, call.Ct).ConfigureAwait(false));
            case "voice":
                return await AdminVoiceAsync(call, user).ConfigureAwait(false);
        }
        return NotAllowed;
    }

    // Server Mute, Server Deafen, Disconnect and Move to, each with that permission where the person is (and Connect where they go).
    private static async Task<object> AdminVoiceAsync(AdminCall call, string user)
    {
        var cache = call.Session.Cache;
        if (cache.VoiceChannelOf(call.Guild, user) is not { } where) return new { ok = false, message = "They aren't in a voice channel." };
        var here = cache.ChannelPermissions(where);
        var body = new JsonObject();
        if (ReadBool(call.P, call.HasParams, "mute") is { } mute && (here & MuteMembers) != 0) body["mute"] = mute;
        if (ReadBool(call.P, call.HasParams, "deaf") is { } deaf && (here & DeafenMembers) != 0) body["deaf"] = deaf;
        if (ReadBool(call.P, call.HasParams, "disconnect") == true && (here & MoveMembers) != 0) body["channel_id"] = null;
        if (call.IdOf("channelId") is { } to && (here & MoveMembers) != 0 && (cache.ChannelPermissions(to) & (MoveMembers | (1UL << 20))) == (MoveMembers | (1UL << 20)))
            body["channel_id"] = to;
        if (body.Count == 0) return NotAllowed;
        return Answer(await call.Session.Rest.SaveMemberAsync(call.Guild, user, body, call.Ct).ConfigureAwait(false));
    }

    // Emoji and Stickers: the lists, Upload (from discord.upload), rename or edit, and delete.
    private static async Task<object> AdminExpressionsAsync(AdminCall call, string action)
    {
        var rest = call.Session.Rest;
        var name = call.Text("name")?.Trim() ?? "";
        switch (action)
        {
            case "emojis": return Answer(await rest.EmojisAsync(call.Guild, call.Ct).ConfigureAwait(false));
            case "stickers": return Answer(await rest.StickersAsync(call.Guild, call.Ct).ConfigureAwait(false));
            case "uploadEmoji" when name.Length is >= 2 and <= 32 && TakeUpload(call.Text("uploadId")) is { } picture:
                return Answer(await rest.UploadEmojiAsync(call.Guild, name, DataUrl(picture), call.Ct).ConfigureAwait(false));
            case "renameEmoji" when name.Length is >= 2 and <= 32 && call.IdOf("emojiId") is { } emoji:
                var roles = call.Element("roles") is { ValueKind: JsonValueKind.Array } r ? r.EnumerateArray().Select(e => DiscordRest.Id(e.GetString() ?? "")).ToList() : [];
                return Answer(await rest.RenameEmojiAsync(call.Guild, emoji, name, roles, call.Ct).ConfigureAwait(false));
            case "deleteEmoji" when call.IdOf("emojiId") is { } gone:
                return Answer(await rest.DeleteEmojiAsync(call.Guild, gone, call.Ct).ConfigureAwait(false));
            case "uploadSticker" when name.Length is >= 2 and <= 30 && TakeUpload(call.Text("uploadId")) is { } file:
                return Answer(await rest.UploadStickerAsync(call.Guild, name, call.Text("tags") ?? "", call.Text("description") ?? "", file, call.Ct).ConfigureAwait(false));
            case "editSticker" when name.Length is >= 2 and <= 30 && call.IdOf("stickerId") is { } sticker:
                return Answer(await rest.EditStickerAsync(call.Guild, sticker, name, call.Text("tags") ?? "", call.Text("description") ?? "", call.Ct).ConfigureAwait(false));
            case "deleteSticker" when call.IdOf("stickerId") is { } removed:
                return Answer(await rest.DeleteStickerAsync(call.Guild, removed, call.Ct).ConfigureAwait(false));
        }
        return new { ok = false, message = "Give it a name of 2 to 32 letters, numbers or underscores." };
    }

    // Invites, Integrations, AutoMod, the Audit Log and Bans.
    private static async Task<object> AdminModerationAsync(AdminCall call, string action)
    {
        var rest = call.Session.Rest;
        switch (action)
        {
            case "invites" when call.Can(ManageGuild): return Answer(await rest.InvitesAsync(call.Guild, call.Ct).ConfigureAwait(false));
            case "revokeInvite" when call.Can(ManageGuild) && call.Text("code") is { Length: > 1 and <= 32 } code && code.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'):
                return Answer(await rest.RevokeInviteAsync(code, call.Ct).ConfigureAwait(false));
            case "integrations" when call.Can(ManageGuild | ManageWebhooks):
                var webhooks = call.Can(ManageWebhooks) ? await rest.WebhooksAsync(call.Guild, call.Ct).ConfigureAwait(false) : null;
                var integrations = call.Can(ManageGuild) ? await rest.IntegrationsAsync(call.Guild, call.Ct).ConfigureAwait(false) : null;
                var failed = webhooks?.Error ?? integrations?.Error;
                // A webhook's token is its password: it stays here (Copy Webhook URL copies it natively).
                return new { ok = failed is null, message = failed, data = new { webhooks = WithoutTokens(webhooks?.Body), integrations = integrations?.Body } };
            case "deleteWebhook" when call.Can(ManageWebhooks) && call.IdOf("webhookId") is { } hook:
                return Answer(await rest.DeleteWebhookAsync(hook, call.Ct).ConfigureAwait(false));
            case "createWebhook" when call.IdOf("channelId") is { } hookChannel && InGuild(call, hookChannel) && (call.Session.Cache.ChannelPermissions(hookChannel) & ManageWebhooks) != 0:
                var created = await rest.CreateWebhookAsync(hookChannel, call.Ct).ConfigureAwait(false);
                return new { ok = created.Error is null, message = created.Error, data = WithoutTokens(created.Body) };
            case "editWebhook" when call.Can(ManageWebhooks) && call.IdOf("webhookId") is { } edited:
                return await AdminEditWebhookAsync(call, edited).ConfigureAwait(false);
            case "copyWebhook" when call.Can(ManageWebhooks) && call.IdOf("webhookId") is { } copied:
                // Still on the UI thread, which owns the clipboard.
                var one = await rest.WebhookAsync(copied, call.Ct).ConfigureAwait(true);
                if (one.Body is not { ValueKind: JsonValueKind.Object } hookBody || !hookBody.TryGetProperty("token", out var token) || token.GetString() is not { Length: > 0 } secret)
                    return new { ok = false, message = one.Error ?? "Discord didn't send this webhook's link." };
                var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
                package.SetText($"https://discord.com/api/webhooks/{copied}/{secret}");
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
                Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
                return new { ok = true, message = (string?)null };
            case "createInvite" when call.IdOf("channelId") is { } inviteChannel && InGuild(call, inviteChannel) && (call.Session.Cache.ChannelPermissions(inviteChannel) & 1UL) != 0:
                // Discord's choices: expire after 30 minutes to 7 days or never, up to 100 uses or no limit.
                var maxAge = (int)(ReadDouble(call.P, call.HasParams, "maxAge") ?? 604800);
                var maxUses = (int)(ReadDouble(call.P, call.HasParams, "maxUses") ?? 0);
                if (maxAge is not (1800 or 3600 or 21600 or 43200 or 86400 or 604800 or 0) || maxUses is not (0 or 1 or 5 or 10 or 25 or 50 or 100)) return new { ok = false, message = "Pick one of Discord's choices." };
                return Answer(await rest.CreateInviteAsync(inviteChannel, maxAge, maxUses, ReadBool(call.P, call.HasParams, "temporary") == true, call.Ct).ConfigureAwait(false));
            case "incidents" when call.Can(ManageGuild):
                var hours = (int)(ReadDouble(call.P, call.HasParams, "hours") ?? 2);
                if (hours is not (1 or 2 or 4 or 6 or 12 or 24)) return new { ok = false, message = "Pick how long." };
                return Answer(await rest.IncidentActionsAsync(call.Guild, ReadBool(call.P, call.HasParams, "invites") == true, ReadBool(call.P, call.HasParams, "dms") == true, hours, call.Ct).ConfigureAwait(false));
            case "deleteIntegration" when call.Can(ManageGuild) && call.IdOf("integrationId") is { } integration:
                return Answer(await rest.DeleteIntegrationAsync(call.Guild, integration, call.Ct).ConfigureAwait(false));
            case "automod" when call.Can(ManageGuild): return Answer(await rest.AutoModRulesAsync(call.Guild, call.Ct).ConfigureAwait(false));
            case "saveAutomod" when call.Can(ManageGuild) && call.Element("rule") is { ValueKind: JsonValueKind.Object } rule:
                return await AdminSaveAutoModAsync(call, rule).ConfigureAwait(false);
            case "deleteAutomod" when call.Can(ManageGuild) && call.IdOf("ruleId") is { } ruleId:
                return Answer(await rest.DeleteAutoModRuleAsync(call.Guild, ruleId, call.Ct).ConfigureAwait(false));
            case "audit" when call.Can(ViewAuditLog):
                var type = (int?)ReadDouble(call.P, call.HasParams, "actionType");
                return Answer(await rest.AuditLogAsync(call.Guild, call.IdOf("before"), call.IdOf("userId"), type is > 0 ? type : null, call.Ct).ConfigureAwait(false));
            case "bans" when call.Can(Ban): return await AdminBansAsync(call).ConfigureAwait(false);
            case "unban" when call.Can(Ban) && call.IdOf("userId") is { } banned:
                return Answer(await rest.UnbanAsync(call.Guild, banned, call.Ct).ConfigureAwait(false));
        }
        return NotAllowed;
    }

    // Activity Alerts, as Discord saves the switch: a Community server turns RAID_ALERTS_DISABLED off to have them,
    // any other server turns NON_COMMUNITY_RAID_ALERTS on.
    private static async Task<object> AdminRaidAlertsAsync(AdminCall call)
    {
        if (call.Session.Cache.ServerOf(call.Guild) is not { } server) return NotAllowed;
        var on = ReadBool(call.P, call.HasParams, "on") == true;
        var features = server.Features;
        if (features.Contains("COMMUNITY")) { if (on) features.Remove("RAID_ALERTS_DISABLED"); else features.Add("RAID_ALERTS_DISABLED"); }
        else if (on) features.Add("NON_COMMUNITY_RAID_ALERTS"); else features.Remove("NON_COMMUNITY_RAID_ALERTS");
        var list = new JsonArray([.. features.Order(StringComparer.Ordinal).Select(f => (JsonNode)f)]);
        return Answer(await call.Session.Rest.SaveGuildAsync(call.Guild, new JsonObject { ["features"] = list }, call.Ct).ConfigureAwait(false));
    }

    private static bool InGuild(AdminCall call, string channelId) =>
        call.Session.Cache.CanSee(channelId) && call.Session.Cache.GuildOf(channelId) == call.Guild;

    // Discord's webhooks without their tokens (one webhook, or a list).
    private static JsonNode? WithoutTokens(JsonElement? body)
    {
        if (body is not { } b) return null;
        var node = JsonNode.Parse(b.GetRawText());
        foreach (var hook in node is JsonArray list ? list.OfType<JsonObject>() : node is JsonObject one ? [one] : [])
            hook.Remove("token");
        return node;
    }

    // Edit Webhook: its name, picture (an upload, or none) and channel, each checked.
    private static async Task<object> AdminEditWebhookAsync(AdminCall call, string webhookId)
    {
        var body = new JsonObject();
        if (call.Text("name")?.Trim() is { Length: > 0 and <= 80 } name) body["name"] = name;
        if (ReadBool(call.P, call.HasParams, "removeAvatar") == true) body["avatar"] = null;
        else if (TakeUpload(call.Text("uploadId")) is { } picture) body["avatar"] = DataUrl(picture);
        if (call.IdOf("channelId") is { } channel && InGuild(call, channel) && (call.Session.Cache.ChannelPermissions(channel) & ManageWebhooks) != 0) body["channel_id"] = channel;
        if (body.Count == 0) return new { ok = false, message = "Nothing changed." };
        var saved = await call.Session.Rest.EditWebhookAsync(webhookId, body, call.Ct).ConfigureAwait(false);
        return new { ok = saved.Error is null, message = saved.Error, data = WithoutTokens(saved.Body) };
    }

    // Every ban, a thousand at a time after the last one (Discord's page), up to ten thousand.
    private static async Task<object> AdminBansAsync(AdminCall call)
    {
        var all = new JsonArray();
        string? after = null;
        for (var page = 0; page < 10; page++)
        {
            var reply = await call.Session.Rest.BansAsync(call.Guild, after, call.Ct).ConfigureAwait(false);
            if (reply.Error is not null || reply.Body is not { ValueKind: JsonValueKind.Array } bans) return page == 0 ? Answer(reply) : new { ok = true, message = (string?)null, data = all };
            foreach (var ban in bans.EnumerateArray()) all.Add(JsonNode.Parse(ban.GetRawText()));
            if (bans.GetArrayLength() < DiscordRest.BanPage) break;
            after = bans[bans.GetArrayLength() - 1].TryGetProperty("user", out var user) && user.TryGetProperty("id", out var id) ? id.GetString() : null;
            if (after is null) break;
        }
        return new { ok = true, message = (string?)null, data = all };
    }

    // An AutoMod rule in Discord's shape: its kind and what it watches, what it does, who and where it skips.
    private static async Task<object> AdminSaveAutoModAsync(AdminCall call, JsonElement rule)
    {
        var body = new JsonObject { ["guild_id"] = call.Guild };
        foreach (var field in rule.EnumerateObject())
            if (field.Name is "name" or "event_type" or "trigger_type" or "trigger_metadata" or "actions" or "enabled" or "exempt_roles" or "exempt_channels")
                body[field.Name] = JsonNode.Parse(field.Value.GetRawText());
        if (body["trigger_type"] is not JsonValue kind || !kind.TryGetValue<int>(out var trigger) || trigger is not (1 or 3 or 4 or 5 or 6))
            return new { ok = false, message = "That isn't a rule Discord has." };
        var id = rule.TryGetProperty("id", out var existing) && existing.GetString() is { Length: > 0 } raw ? DiscordRest.Id(raw) : null;
        if (id is not null) body["id"] = id;
        return Answer(await call.Session.Rest.SaveAutoModRuleAsync(call.Guild, id, body, call.Ct).ConfigureAwait(false));
    }

    // Edit Channel: its settings and overwrites, saving the Overview, an overwrite, removing one.
    private static async Task<object> DiscordChannelAdminAsync(DiscordSession session, string action, JsonElement p, bool hasParams, CancellationToken ct)
    {
        if (Channel(p, hasParams) is not { Guild: not null } c) return new { ok = false, message = "That channel isn't one you can see." };
        var perms = session.Cache.ChannelPermissions(c.Id);
        var target = ReadString(p, hasParams, "targetId") is { Length: > 0 } t ? DiscordRest.Id(t) : null;
        switch (action)
        {
            case "channelSettings":
                return new { ok = true, message = (string?)null, data = session.Cache.ChannelSettings(c.Id) };
            case "channelSave" when (perms & (1UL << 4)) != 0 && hasParams && p.TryGetProperty("changes", out var changes) && changes.ValueKind == JsonValueKind.Object:
                return await AdminSaveChannelAsync(session, c.Id, changes, ct).ConfigureAwait(false);
            case "channelOverwrite" when (perms & ManageRoles) != 0 && target is not null:
                var type = (int)(ReadDouble(p, hasParams, "type") ?? 0);
                if (type is not (0 or 1) || !TryPerms(ReadString(p, hasParams, "allow"), out var allow) || !TryPerms(ReadString(p, hasParams, "deny"), out var deny)) return new { ok = false };
                return Answer(await session.Rest.SaveOverwriteAsync(c.Id, target, type, allow, deny, ct).ConfigureAwait(false));
            case "channelClearOverwrite" when (perms & ManageRoles) != 0 && target is not null:
                return Answer(await session.Rest.DeleteOverwriteAsync(c.Id, target, ct).ConfigureAwait(false));
        }
        return NotAllowed;
    }

    private static bool TryPerms(string? text, out ulong perms) =>
        ulong.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out perms);

    // Name, topic, slowmode, age-restricted, user limit and bitrate, as Discord's Overview saves them.
    private static async Task<object> AdminSaveChannelAsync(DiscordSession session, string channelId, JsonElement changes, CancellationToken ct)
    {
        var body = new JsonObject();
        foreach (var field in changes.EnumerateObject())
        {
            var v = field.Value;
            switch (field.Name)
            {
                case "name" when v.GetString()?.Trim() is { Length: > 0 and <= 100 } name: body["name"] = name; break;
                case "topic" when v.ValueKind == JsonValueKind.Null || v.GetString() is { Length: <= 1024 }: body["topic"] = v.ValueKind == JsonValueKind.Null ? null : v.GetString(); break;
                case "rate_limit_per_user" when v.TryGetInt32(out var slow) && slow is >= 0 and <= 21600: body["rate_limit_per_user"] = slow; break;
                case "nsfw" when v.ValueKind is JsonValueKind.True or JsonValueKind.False: body["nsfw"] = v.GetBoolean(); break;
                case "user_limit" when v.TryGetInt32(out var limit) && limit is >= 0 and <= 99: body["user_limit"] = limit; break;
                case "bitrate" when v.TryGetInt32(out var bitrate) && bitrate is >= 8000 and <= 384000: body["bitrate"] = bitrate; break;
            }
        }
        if (body.Count == 0) return new { ok = false, message = "Nothing changed." };
        return Answer(await session.Rest.SaveChannelAsync(channelId, body, ct).ConfigureAwait(false));
    }

    private static DiscordRest.Upload? TakeUpload(string? uploadId)
    {
        if (uploadId is null) return null;
        lock (Uploads)
        {
            if (!Uploads.Remove(uploadId, out var upload)) return null;
            using (upload.Data) return new DiscordRest.Upload(upload.Name, upload.Type, upload.Data.ToArray());
        }
    }

    private static string DataUrl(DiscordRest.Upload upload) => $"data:{upload.Type};base64,{Convert.ToBase64String(upload.Data)}";
}
