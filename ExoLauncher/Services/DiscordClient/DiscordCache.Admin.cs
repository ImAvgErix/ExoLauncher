namespace ExoLauncher.Services.DiscordClient;

/// <summary>
/// What a moderator may do, worked out as Discord's client works it out: server permissions from
/// @everyone and the user's roles (owner and Administrator have all), channel permissions with the
/// channel's overwrites, and the role order (only someone whose highest role is above another
/// person's may kick, ban, time out, rename or re-role them, and only roles below one's own can be
/// given). Discord checks all of it again; this decides what the menus offer.
/// </summary>
internal sealed partial class DiscordCache
{
    internal const ulong Kick = 1UL << 1, Ban = 1UL << 2, ManageGuild = 1UL << 5, ViewAuditLog = 1UL << 7, ManageMessages = 1UL << 13,
        MuteMembers = 1UL << 22, DeafenMembers = 1UL << 23, MoveMembers = 1UL << 24, ChangeNickname = 1UL << 26, ManageNicknames = 1UL << 27,
        ManageRoles = 1UL << 28, ManageWebhooks = 1UL << 29, ManageExpressions = 1UL << 30, ModerateMembers = 1UL << 40,
        CreateExpressions = 1UL << 43, PinMessages = 1UL << 51, ViewInsights = 1UL << 19;

    // Pinning became its own permission; servers Discord hasn't moved over yet still pin with Manage Messages.
    private static ulong PinPermission(Guild guild) =>
        guild.Features.Contains("PIN_PERMISSION_MIGRATION_COMPLETE") ? PinMessages : ManageMessages;

    private object ModerationOf(Guild guild, ulong perms)
    {
        bool Has(ulong p) => (perms & p) != 0;
        return new
        {
            kick = Has(Kick),
            ban = Has(Ban),
            manageGuild = Has(ManageGuild),
            auditLog = Has(ViewAuditLog),
            manageRoles = Has(ManageRoles),
            timeout = Has(ModerateMembers),
            manageChannels = Has(ManageChannels),
            manageExpressions = Has(ManageExpressions | CreateExpressions),
            manageWebhooks = Has(ManageWebhooks),
            manageNicknames = Has(ManageNicknames),
            prune = Has(Kick) && Has(ManageGuild),
            administrator = Has(Administrator),
            insights = Has(ViewInsights),
            owner = guild.OwnerId == Me?.Id,
            mfa = MfaEnabled,
        };
    }

    /// <summary>Your servers (any may have a tag; its profile says), and the one whose tag you wear.</summary>
    internal (List<(string Id, string Name)> Servers, string? Mine) TagServers()
    {
        lock (_gate)
            return ([.. _guilds.Values.Select(g => (g.Id, g.Name))], Me?.Look?.TagGuild);
    }

    /// <summary>The server's features as Discord last sent them, and whether the user owns it; null when it isn't one of theirs.</summary>
    internal (HashSet<string> Features, bool Owner)? ServerOf(string guildId)
    {
        lock (_gate) return _guilds.TryGetValue(guildId, out var guild) ? ([.. guild.Features], guild.OwnerId == Me?.Id) : null;
    }

    /// <summary>The user's permissions on a server, or null when it isn't one of theirs.</summary>
    internal ulong? ServerPermissions(string guildId)
    {
        lock (_gate) return _guilds.TryGetValue(guildId, out var guild) ? GuildPerms(guild) : null;
    }

    /// <summary>Whether the user may count and prune inactive members: Kick Members and Manage Server, as Discord asks.</summary>
    internal bool CanPrune(string guildId) => ServerPermissions(guildId) is { } perms && (perms & Kick) != 0 && (perms & ManageGuild) != 0;

    /// <summary>The user's permissions in a server channel (0 for one they can't see or a DM).</summary>
    internal ulong ChannelPermissions(string channelId)
    {
        lock (_gate) return Find(channelId) is { GuildId: not null } c ? Perms(c) : 0;
    }

    // The position of someone's highest role (@everyone is 0).
    private static int Top(Guild guild, IEnumerable<string> roles) =>
        roles.Select(r => guild.Styles.GetValueOrDefault(r)?.Position ?? 0).DefaultIfEmpty(0).Max();

    private int MyTop(Guild guild) => guild.OwnerId == Me?.Id ? int.MaxValue : Top(guild, guild.MyRoles);

    /// <summary>The position of the user's highest role on a server (the owner is above every role).</summary>
    internal int TopOf(string guildId)
    {
        lock (_gate) return _guilds.TryGetValue(guildId, out var guild) ? MyTop(guild) : 0;
    }

    /// <summary>
    /// Onboarding's counts, as Discord's client counts them: the channels and categories @everyone can see (a voice
    /// channel: and join), and of those the channels it can talk in (Send Messages, or in a forum Send Messages in Threads).
    /// </summary>
    internal (List<string> Seen, List<string> Chats) EveryoneChannels(string guildId)
    {
        const ulong SendInThreads = 1UL << 38;
        lock (_gate)
        {
            if (!_guilds.TryGetValue(guildId, out var guild)) return ([], []);
            List<string> seen = [], chats = [];
            foreach (var c in guild.Channels.Values.Where(c => c.Type is not (10 or 11 or 12)))
            {
                var perms = EveryonePerms(guild, c);
                var need = IsVoice(c.Type) ? ViewChannel | Connect : ViewChannel;
                if ((perms & need) != need) continue;
                seen.Add(c.Id);
                if (c.Type != 4 && (perms & (c.Type is 15 or 16 ? SendInThreads : SendMessages)) != 0) chats.Add(c.Id);
            }
            return (seen, chats);
        }
    }

    /// <summary>Members Discord's member search sent (their roles and nicknames), so the menus know them.</summary>
    internal void RememberMembers(string guildId, System.Text.Json.JsonElement search)
    {
        lock (_gate)
        {
            if (!_guilds.TryGetValue(guildId, out var guild)) return;
            foreach (var row in Array(search, "members"))
                if (row.TryGetProperty("member", out var member)) RememberMember(guild, member);
        }
    }

    /// <summary>Whether the user may give or take this role: Manage Roles and the role below their highest one.</summary>
    internal bool CanAssign(string guildId, string roleId)
    {
        lock (_gate)
            return _guilds.TryGetValue(guildId, out var guild) && roleId != guildId && guild.Styles.TryGetValue(roleId, out var role) && !role.Managed
                && (GuildPerms(guild) & ManageRoles) != 0 && role.Position < MyTop(guild);
    }

    /// <summary>
    /// Whether the user may edit this role in the role editor: Manage Roles and the role below their highest one.
    /// Unlike giving it, an app's or Server Booster's role counts (its color, display and permissions, not its name),
    /// and @everyone's default permissions do too.
    /// </summary>
    internal bool CanEditRole(string guildId, string roleId)
    {
        lock (_gate)
            return _guilds.TryGetValue(guildId, out var guild) && (GuildPerms(guild) & ManageRoles) != 0
                && (roleId == guildId || (guild.Styles.TryGetValue(roleId, out var role) && role.Position < MyTop(guild)));
    }

    /// <summary>Whether this is an app's or Server Booster's role (Discord names it; nobody gives or takes it).</summary>
    internal bool IsManagedRole(string guildId, string roleId)
    {
        lock (_gate) return _guilds.GetValueOrDefault(guildId)?.Styles.GetValueOrDefault(roleId)?.Managed == true;
    }

    /// <summary>After a timeout the user gave or lifted: until when (null lifted it).</summary>
    internal void SetTimeout(string guildId, string userId, DateTimeOffset? until)
    {
        lock (_gate)
        {
            if (!_guilds.TryGetValue(guildId, out var guild)) return;
            if (until is { } u) guild.TimedOut[userId] = u; else guild.TimedOut.Remove(userId);
        }
    }

    /// <summary>Whether the user outranks this member (not the owner, not themselves, highest role above theirs).</summary>
    internal bool Outranks(string guildId, string userId)
    {
        lock (_gate)
            return _guilds.TryGetValue(guildId, out var guild) && userId != Me?.Id && userId != guild.OwnerId
                && guild.Members.TryGetValue(userId, out var member) && MyTop(guild) > Top(guild, member.Roles);
    }

    /// <summary>A member's roles as Exo last saw them, or null when it hasn't.</summary>
    internal List<string>? RolesOf(string guildId, string userId)
    {
        lock (_gate) return _guilds.GetValueOrDefault(guildId)?.Members.TryGetValue(userId, out var m) == true ? [.. m.Roles] : null;
    }

    /// <summary>After a change the user made: the member's roles (and nickname) as they are now.</summary>
    internal void SetMember(string guildId, string userId, List<string>? roles, string? nick, bool nickChanged)
    {
        lock (_gate)
        {
            if (!_guilds.TryGetValue(guildId, out var guild)) return;
            var had = guild.Members.TryGetValue(userId, out var m) ? m : (Roles: new List<string>(), Nick: (string?)null);
            guild.Members[userId] = (roles ?? had.Roles, nickChanged ? nick : had.Nick);
            if (userId == Me?.Id && roles is not null) guild.MyRoles = [.. roles];
        }
    }

    /// <summary>
    /// What the person menu offers for someone on a server: their roles (known or not yet), the roles the user may
    /// give, whether they're outranked, their voice state with what may be done to it, and where they can be moved.
    /// </summary>
    internal object Person(string guildId, string userId, out bool unknown)
    {
        lock (_gate)
        {
            unknown = false;
            if (!_guilds.TryGetValue(guildId, out var guild) || Me is null) return new { ok = false };
            var known = guild.Members.TryGetValue(userId, out var member);
            unknown = !known;
            var perms = GuildPerms(guild);
            var myTop = MyTop(guild);
            var me = userId == Me.Id;
            var outranks = !me && userId != guild.OwnerId && known && myTop > Top(guild, member.Roles);
            var voice = guild.Voice.GetValueOrDefault(userId);
            var voiceChannel = voice is null ? null : guild.Channels.GetValueOrDefault(voice.ChannelId);
            var here = voiceChannel is null ? 0UL : Perms(voiceChannel);
            return new
            {
                ok = true,
                known,
                me,
                owner = userId == guild.OwnerId,
                // Your own 2FA: Transfer Ownership then verifies with it instead of an emailed code.
                mfa = MfaEnabled,
                outranks,
                nick = known ? member.Nick : null,
                // Remove Timeout shows only for someone timed out, as in Discord.
                timedOut = guild.TimedOut.TryGetValue(userId, out var until) && until > DateTimeOffset.UtcNow,
                name = _users.GetValueOrDefault(userId)?.Name,
                username = _users.GetValueOrDefault(userId)?.Username,
                roles = known ? member.Roles.Where(guild.Styles.ContainsKey).ToArray() : [],
                choices = guild.Styles.Where(s => s.Key != guildId).OrderByDescending(s => s.Value.Position).Select(s => new
                {
                    id = s.Key,
                    name = s.Value.Name,
                    color = s.Value.Color > 0 ? $"#{s.Value.Color:x6}" : null,
                    assignable = !s.Value.Managed && (perms & ManageRoles) != 0 && s.Value.Position < myTop,
                }).ToArray(),
                can = new
                {
                    roles = (perms & ManageRoles) != 0 && (me || outranks || guild.OwnerId == Me.Id),
                    nick = me ? (perms & (ChangeNickname | ManageNicknames)) != 0 : outranks && (perms & ManageNicknames) != 0,
                    timeout = outranks && (perms & ModerateMembers) != 0,
                    kick = outranks && (perms & Kick) != 0,
                    ban = outranks && (perms & Ban) != 0,
                    // Transfer Ownership: the owner, to any other member who isn't a bot, as Discord offers it.
                    transfer = !me && known && guild.OwnerId == Me.Id && _users.GetValueOrDefault(userId)?.Bot != true,
                    mute = voice is not null && (here & MuteMembers) != 0,
                    deafen = voice is not null && (here & DeafenMembers) != 0,
                    move = voice is not null && !me && (here & MoveMembers) != 0,
                },
                voice = voice is null ? null : new { channelId = voice.ChannelId, mute = voice.ServerMute, deaf = voice.ServerDeaf },
                // Move to: the server's other voice channels the user may move people into (Move Members and Connect there).
                moveTo = voice is null ? [] : guild.Channels.Values
                    .Where(c => IsVoice(c.Type) && c.Id != voice.ChannelId && (Perms(c) & (MoveMembers | Connect)) == (MoveMembers | Connect))
                    .OrderBy(c => c.Position).Select(c => new { id = c.Id, name = c.Name ?? "" }).ToArray(),
            };
        }
    }

    /// <summary>A server channel's settings and overwrites, with the server's roles, for Edit Channel.</summary>
    internal object? ChannelSettings(string channelId)
    {
        lock (_gate)
        {
            if (Find(channelId) is not { GuildId: { } guildId } c || !_guilds.TryGetValue(guildId, out var guild)) return null;
            var perms = Perms(c);
            return new
            {
                id = c.Id,
                guildId,
                name = c.Name ?? "",
                type = c.Type,
                topic = c.Topic,
                nsfw = c.Nsfw,
                rateLimit = c.RateLimit,
                userLimit = c.UserLimit,
                bitrate = c.Bitrate,
                parentId = c.ParentId,
                canManage = (perms & ManageChannels) != 0,
                canPermissions = (perms & ManageRoles) != 0,
                // Discord's bitrate ceiling for the server's boost level, in bits per second.
                maxBitrate = guild.Features.Contains("VIP_REGIONS") ? 384000
                    : guild.Features.Contains("AUDIO_BITRATE_384_KBPS") ? 384000
                    : guild.Features.Contains("AUDIO_BITRATE_256_KBPS") ? 256000
                    : guild.Features.Contains("AUDIO_BITRATE_128_KBPS") ? 128000 : 96000,
                overwrites = c.Overwrites.Select(o => new { id = o.Id, type = o.Member ? 1 : 0, allow = o.Allow.ToString(), deny = o.Deny.ToString() }).ToArray(),
                roles = guild.Styles.OrderByDescending(s => s.Value.Position).Select(s => new
                {
                    id = s.Key,
                    name = s.Key == guildId ? "@everyone" : s.Value.Name,
                    color = s.Value.Color > 0 ? $"#{s.Value.Color:x6}" : null,
                }).ToArray(),
                // Names for member overwrites, where Exo knows them.
                members = c.Overwrites.Where(o => o.Member).Select(o => new
                {
                    id = o.Id,
                    name = guild.Members.GetValueOrDefault(o.Id).Nick ?? _users.GetValueOrDefault(o.Id)?.Name ?? "Someone",
                    avatar = Avatar(o.Id),
                }).ToArray(),
            };
        }
    }
}
