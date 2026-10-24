using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace ExoLauncher.Services.DiscordClient;

/// <summary>
/// Server Settings and moderation, called the way Discord's web client calls them (read from its
/// own scripts): the server (only the changed fields), roles and their order, who has a role,
/// emoji and stickers, invites, webhooks, integrations, AutoMod rules, channel settings and
/// permission overwrites, and a member's roles, nickname and voice. Only what the person using
/// Exo asked for; nothing runs on its own.
/// </summary>
internal sealed partial class DiscordRest
{
    internal Task<Reply> SaveGuildAsync(string guildId, JsonObject changes, CancellationToken ct) =>
        SendAsync(HttpMethod.Patch, $"guilds/{Id(guildId)}", changes, ct);

    /// <summary>A server's public profile (name, icon, tag and badge, counts, description, traits), as a tag's card shows it.</summary>
    internal Task<Reply> GuildProfileAsync(string guildId, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"guilds/{Id(guildId)}/profile", null, ct);

    /// <summary>Require 2FA for moderator actions (level 1) or not (0); the owner verifies first.</summary>
    internal Task<Reply> GuildMfaAsync(string guildId, bool required, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"guilds/{Id(guildId)}/mfa", new JsonObject { ["level"] = required ? 1 : 0 }, ct);

    /// <summary>Transfer Ownership: to a member, with the emailed code when the owner has no 2FA (with 2FA the owner verifies instead).</summary>
    internal Task<Reply> TransferOwnershipAsync(string guildId, string userId, string? code, CancellationToken ct) =>
        SendAsync(HttpMethod.Patch, $"guilds/{Id(guildId)}", new JsonObject { ["owner_id"] = Id(userId), ["code"] = code }, ct);

    /// <summary>Emails the owner the code Transfer Ownership asks for (an owner without 2FA).</summary>
    internal Task<Reply> TransferCodeAsync(string guildId, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, $"guilds/{Id(guildId)}/pincode", null, ct);

    /// <summary>Delete Server, as Discord's client sends it (no body); an owner with 2FA verifies first.</summary>
    internal Task<Reply> DeleteGuildAsync(string guildId, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"guilds/{Id(guildId)}/delete", null, ct);

    /// <summary>Create Role: Discord's "new role", no color, no permissions.</summary>
    internal Task<Reply> CreateRoleAsync(string guildId, string name, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"guilds/{Id(guildId)}/roles", new JsonObject
        {
            ["name"] = name,
            ["color"] = 0,
            ["colors"] = new JsonObject { ["primary_color"] = 0, ["secondary_color"] = null, ["tertiary_color"] = null },
            ["permissions"] = "0",
        }, ct);

    internal Task<Reply> SaveRoleAsync(string guildId, string roleId, JsonObject changes, CancellationToken ct) =>
        SendAsync(HttpMethod.Patch, $"guilds/{Id(guildId)}/roles/{Id(roleId)}", changes, ct);

    internal Task<Reply> DeleteRoleAsync(string guildId, string roleId, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"guilds/{Id(guildId)}/roles/{Id(roleId)}", null, ct);

    /// <summary>The role order, as Discord saves a drag: every role but @everyone with its position.</summary>
    internal Task<Reply> RolePositionsAsync(string guildId, IEnumerable<(string Id, int Position)> positions, CancellationToken ct) =>
        SendAsync(HttpMethod.Patch, $"guilds/{Id(guildId)}/roles",
            new JsonArray([.. positions.Select(p => (JsonNode)new JsonObject { ["id"] = Id(p.Id), ["position"] = p.Position })]), ct);

    /// <summary>A role's Linked Roles requirements: groups of accounts to connect, each with its checks.</summary>
    internal Task<Reply> RoleLinksAsync(string guildId, string roleId, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"guilds/{Id(guildId)}/roles/{Id(roleId)}/connections/configuration", null, ct);

    internal Task<Reply> SaveRoleLinksAsync(string guildId, string roleId, JsonArray groups, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, $"guilds/{Id(guildId)}/roles/{Id(roleId)}/connections/configuration", groups, ct);

    internal Task<Reply> RoleMemberCountsAsync(string guildId, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"guilds/{Id(guildId)}/roles/member-counts", null, ct);

    /// <summary>Discord's member search: by name (or id), or everyone with a role.</summary>
    internal Task<Reply> SearchMembersAsync(string guildId, string? query, string? roleId, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"guilds/{Id(guildId)}/members-search", new JsonObject { ["limit"] = 100, ["or_query"] = new JsonObject(), ["and_query"] = MemberQuery(query, roleId) }, ct);

    // Discord's and_query: a name (or an id, all digits) of two letters or more, and a role.
    private static JsonObject MemberQuery(string? query, string? roleId)
    {
        var and = new JsonObject();
        if (query?.Trim() is { Length: > 1 } q)
        {
            if (q.All(char.IsAsciiDigit)) and["user_id"] = new JsonObject { ["or_query"] = new JsonArray(q) };
            else and["usernames"] = new JsonObject { ["or_query"] = new JsonArray(q) };
        }
        if (roleId is not null) and["role_ids"] = new JsonObject { ["and_query"] = new JsonArray(Id(roleId)) };
        return and;
    }

    /// <summary>Add Members to a role, as Discord's role editor does.</summary>
    internal Task<Reply> AddRoleMembersAsync(string guildId, string roleId, IEnumerable<string> userIds, CancellationToken ct) =>
        SendAsync(HttpMethod.Patch, $"guilds/{Id(guildId)}/roles/{Id(roleId)}/members",
            new JsonObject { ["member_ids"] = new JsonArray([.. userIds.Select(u => (JsonNode)Id(u))]) }, ct);

    /// <summary>Takes one role from a member, leaving their others as Discord has them now.</summary>
    internal Task<Reply> RemoveMemberRoleAsync(string guildId, string userId, string roleId, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"guilds/{Id(guildId)}/members/{Id(userId)}/roles/{Id(roleId)}", null, ct);

    /// <summary>Gives a member one role, leaving their others as Discord has them now.</summary>
    internal Task<Reply> AddMemberRoleAsync(string guildId, string userId, string roleId, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, $"guilds/{Id(guildId)}/members/{Id(userId)}/roles/{Id(roleId)}", null, ct);

    /// <summary>A member's nickname, or their voice (mute, deaf, channel_id; null disconnects).</summary>
    internal Task<Reply> SaveMemberAsync(string guildId, string userId, JsonObject changes, CancellationToken ct) =>
        SendAsync(HttpMethod.Patch, $"guilds/{Id(guildId)}/members/{Id(userId)}", changes, ct);

    /// <summary>The user's own nickname on a server.</summary>
    internal Task<Reply> SaveOwnNickAsync(string guildId, string? nick, CancellationToken ct) =>
        SendAsync(HttpMethod.Patch, $"guilds/{Id(guildId)}/members/@me", new JsonObject { ["nick"] = nick }, ct);

    internal Task<Reply> EmojisAsync(string guildId, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"guilds/{Id(guildId)}/emojis", null, ct);

    /// <summary>Upload Emoji: the picture as a data URL, the name, open to every role.</summary>
    internal Task<Reply> UploadEmojiAsync(string guildId, string name, string image, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"guilds/{Id(guildId)}/emojis", new JsonObject { ["image"] = image, ["name"] = name, ["roles"] = new JsonArray() }, ct);

    /// <summary>Renames an emoji, keeping which roles may use it.</summary>
    internal Task<Reply> RenameEmojiAsync(string guildId, string emojiId, string name, IEnumerable<string> roles, CancellationToken ct) =>
        SendAsync(HttpMethod.Patch, $"guilds/{Id(guildId)}/emojis/{Id(emojiId)}",
            new JsonObject { ["name"] = name, ["roles"] = new JsonArray([.. roles.Select(r => (JsonNode)Id(r))]) }, ct);

    internal Task<Reply> DeleteEmojiAsync(string guildId, string emojiId, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"guilds/{Id(guildId)}/emojis/{Id(emojiId)}", null, ct);

    internal Task<Reply> StickersAsync(string guildId, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"guilds/{Id(guildId)}/stickers", null, ct);

    /// <summary>Upload Sticker: Discord's form of name, related emoji (tags), description and the file.</summary>
    internal Task<Reply> UploadStickerAsync(string guildId, string name, string tags, string description, Upload file, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"guilds/{Id(guildId)}/stickers", null, ct, content: () =>
        {
            var form = new MultipartFormDataContent { { new StringContent(name), "name" }, { new StringContent(tags), "tags" }, { new StringContent(description), "description" } };
            var part = new ByteArrayContent(file.Data);
            part.Headers.ContentType = MediaTypeHeaderValue.TryParse(file.Type, out var type) ? type : new MediaTypeHeaderValue("image/png");
            form.Add(part, "file", file.Name);
            return form;
        });

    internal Task<Reply> EditStickerAsync(string guildId, string stickerId, string name, string tags, string description, CancellationToken ct) =>
        SendAsync(HttpMethod.Patch, $"guilds/{Id(guildId)}/stickers/{Id(stickerId)}", new JsonObject { ["name"] = name, ["tags"] = tags, ["description"] = description }, ct);

    internal Task<Reply> DeleteStickerAsync(string guildId, string stickerId, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"guilds/{Id(guildId)}/stickers/{Id(stickerId)}", null, ct);

    internal Task<Reply> InvitesAsync(string guildId, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"guilds/{Id(guildId)}/invites", null, ct);

    internal Task<Reply> RevokeInviteAsync(string code, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"invites/{Uri.EscapeDataString(code)}", null, ct);

    /// <summary>Discord's Create Invite Link: how long it lasts (seconds, 0 never), how many uses (0 no limit), temporary membership.</summary>
    internal Task<Reply> CreateInviteAsync(string channelId, int maxAge, int maxUses, bool temporary, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"channels/{Id(channelId)}/invites", new JsonObject { ["max_age"] = maxAge, ["max_uses"] = maxUses, ["temporary"] = temporary }, ct);

    /// <summary>Security actions: pause invites and/or DMs for some hours (both off resumes them), as Discord's sends them.</summary>
    internal Task<Reply> IncidentActionsAsync(string guildId, bool invites, bool dms, int hours, CancellationToken ct)
    {
        var until = invites || dms ? DateTimeOffset.UtcNow.AddHours(hours).ToString("o", System.Globalization.CultureInfo.InvariantCulture) : null;
        return SendAsync(HttpMethod.Put, $"guilds/{Id(guildId)}/incident-actions", new JsonObject
        {
            ["invites_disabled_until"] = invites ? until : null,
            ["dms_disabled_until"] = dms ? until : null,
            ["lockdown_duration_hours"] = invites || dms ? hours : null,
        }, ct);
    }

    /// <summary>Create Webhook in a channel, named as Discord's are (Spidey Bot or Captain Hook).</summary>
    internal Task<Reply> CreateWebhookAsync(string channelId, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"channels/{Id(channelId)}/webhooks", new JsonObject { ["name"] = Random.Shared.Next(2) == 0 ? "Spidey Bot" : "Captain Hook" }, ct);

    /// <summary>A webhook's name, picture (data URL, or null to remove) or channel.</summary>
    internal Task<Reply> EditWebhookAsync(string webhookId, JsonObject changes, CancellationToken ct) =>
        SendAsync(HttpMethod.Patch, $"webhooks/{Id(webhookId)}", changes, ct);

    /// <summary>One webhook with its token (Copy Webhook URL's).</summary>
    internal Task<Reply> WebhookAsync(string webhookId, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"webhooks/{Id(webhookId)}", null, ct);

    internal Task<Reply> WebhooksAsync(string guildId, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"guilds/{Id(guildId)}/webhooks", null, ct);

    internal Task<Reply> DeleteWebhookAsync(string webhookId, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"webhooks/{Id(webhookId)}", null, ct);

    internal Task<Reply> IntegrationsAsync(string guildId, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"guilds/{Id(guildId)}/integrations?include_applications=true&include_role_connections_metadata=true", null, ct);

    internal Task<Reply> DeleteIntegrationAsync(string guildId, string integrationId, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"guilds/{Id(guildId)}/integrations/{Id(integrationId)}", null, ct);

    internal Task<Reply> AutoModRulesAsync(string guildId, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"guilds/{Id(guildId)}/auto-moderation/rules", null, ct);

    /// <summary>Creates a rule (no id) or saves one, in Discord's rule shape.</summary>
    internal Task<Reply> SaveAutoModRuleAsync(string guildId, string? ruleId, JsonObject rule, CancellationToken ct) => ruleId is null
        ? SendAsync(HttpMethod.Post, $"guilds/{Id(guildId)}/auto-moderation/rules", rule, ct)
        : SendAsync(HttpMethod.Patch, $"guilds/{Id(guildId)}/auto-moderation/rules/{Id(ruleId)}", rule, ct);

    internal Task<Reply> DeleteAutoModRuleAsync(string guildId, string ruleId, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"guilds/{Id(guildId)}/auto-moderation/rules/{Id(ruleId)}", null, ct);

    /// <summary>Edit Channel's Overview: only the changed fields.</summary>
    internal Task<Reply> SaveChannelAsync(string channelId, JsonObject changes, CancellationToken ct) =>
        SendAsync(HttpMethod.Patch, $"channels/{Id(channelId)}", changes, ct);

    /// <summary>A role's (type 0) or member's (type 1) overwrite: what it allows and denies.</summary>
    internal Task<Reply> SaveOverwriteAsync(string channelId, string targetId, int type, ulong allow, ulong deny, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, $"channels/{Id(channelId)}/permissions/{Id(targetId)}", new JsonObject
        {
            ["id"] = Id(targetId),
            ["type"] = type,
            ["allow"] = allow.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["deny"] = deny.ToString(System.Globalization.CultureInfo.InvariantCulture),
        }, ct);

    internal Task<Reply> DeleteOverwriteAsync(string channelId, string targetId, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"channels/{Id(channelId)}/permissions/{Id(targetId)}", null, ct);
}
