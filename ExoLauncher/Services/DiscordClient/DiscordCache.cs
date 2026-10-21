using System.Globalization;
using System.Text.Json;
using GuildFolder = ExoLauncher.Services.DiscordClient.DiscordUserSettings.GuildFolder;

namespace ExoLauncher.Services.DiscordClient;

/// <summary>
/// What the gateway told Exo's Discord client: servers, their roles and channels, the user's
/// own roles, DMs, read states and mutes. Kept in memory only. Channel visibility is worked
/// out here, with Discord's own permission rules (base from @everyone and the user's roles,
/// Administrator and owner see all, then the channel's @everyone, role and member overwrites);
/// a channel without View Channel never reaches the UI, nor do its messages or typing.
/// Message text is never kept here.
/// </summary>
internal sealed partial class DiscordCache
{
    private const ulong Administrator = 1UL << 3, ViewChannel = 1UL << 10, SendMessages = 1UL << 11, ReadHistory = 1UL << 16, Connect = 1UL << 20;
    private const ulong All = ulong.MaxValue;
    private const ulong ManageChannels = 1UL << 4, ManageEvents = 1UL << 33;
    // Kick, Ban, Manage Server or Timeout: who Discord shows its Members page to.
    private const ulong Moderation = 1UL << 1 | 1UL << 2 | 1UL << 5 | 1UL << 40;

    private sealed record Overwrite(string Id, bool Member, ulong Allow, ulong Deny);

    private sealed class Channel
    {
        public required string Id;
        public string? GuildId;
        public int Type;
        public string? Name;
        public string? ParentId;
        public int Position;
        public string? Topic;
        public string? LastMessageId;
        public string? Icon;
        // Discord's channel flags; 128 is a Server Guide resource channel.
        public int Flags;
        // A voice channel's audio bitrate (64 kbps unless the server set more; boosts allow up to 384).
        public int Bitrate;
        // Channel settings: age-restricted, slowmode seconds, a voice channel's user limit.
        public bool Nsfw;
        public int RateLimit, UserLimit;
        // A thread closed to new messages (thread_metadata.locked).
        public bool Locked;
        // A forum's or media channel's tags: id, name and emoji (a server emoji's id, or a Unicode one).
        public List<(string Id, string Name, string? EmojiId, string? EmojiName)> Tags = [];
        public List<Overwrite> Overwrites = [];
        public List<string> Recipients = [];
        // A DM from someone the user hasn't accepted yet (Discord's message requests): it doesn't notify.
        public bool MessageRequest;
    }

    private sealed class Guild
    {
        public required string Id;
        public string Name = "";
        public string? Icon;
        public string? OwnerId;
        public string? RulesChannelId;
        public string? AfkChannelId;
        // Discord's server features (COMMUNITY, GUILD_SERVER_GUIDE, GUILD_ONBOARDING, ...).
        public HashSet<string> Features = [];
        public Dictionary<string, ulong> Roles = [];
        // How each role shows: color, order, name, and whether it gets its own group in the member list.
        public Dictionary<string, RoleStyle> Styles = [];
        // What Exo has seen of members: user id -> (roles, nickname).
        public Dictionary<string, (List<string> Roles, string? Nick)> Members = [];
        // Members Exo has seen timed out: user id -> until when.
        public Dictionary<string, DateTimeOffset> TimedOut = [];
        // Ids asked of Discord (op 8) and when, and ids Discord said aren't members (webhooks, people who left): never asked again.
        public Dictionary<string, long> Asked = [];
        public HashSet<string> NotFound = [];
        // The member list Discord keeps current for the open channel (GUILD_MEMBER_LIST_UPDATE), as items.
        public List<JsonElement> List = [];
        // Which list it is (Discord's id).
        public string? ListId;
        public int OnlineCount, MemberCount;
        // Member list group id -> how many are in it (the update's "groups").
        public Dictionary<string, int> GroupCounts = [];
        public Dictionary<string, Channel> Channels = [];
        public HashSet<string> MyRoles = [];
        // Whether the user's own member (and so their roles) has arrived for this server: until it has, role-only channels
        // would read as hidden, so the session asks Discord for it.
        public bool MyRolesKnown;
        public List<Emoji> Emojis = [];
        public List<(string Id, string Name, int Format)> Stickers = [];
        // What members are notified of by default: 0 all messages, 1 only mentions.
        public int DefaultNotifications;
        // When the user joined, as a snowflake: a channel never read counts as read up to here, as in Discord.
        public ulong JoinedAt;
        // Scheduled events not yet over (scheduled or live): Discord's "N Events" row.
        public HashSet<string> Events = [];
        // Who is in which of this server's voice channels: user id -> state.
        public Dictionary<string, VoiceMember> Voice = [];
    }

    private sealed record Emoji(string Id, string Name, bool Animated, bool Available);

    internal sealed record RoleStyle(int Color, int Position, string Name, bool Hoist, bool Managed = false);

    internal sealed record VoiceMember(string ChannelId, string Name, string? Avatar, bool Mute, bool Deaf, bool Video = false, bool Stream = false,
        bool ServerMute = false, bool ServerDeaf = false);

    internal sealed record User(string Id, string Name, string? Avatar, string? Username = null, Look? Look = null, bool Bot = false);

    private readonly Lock _gate = new();
    // Who is in each DM's call: channel id -> user id -> state.
    private readonly Dictionary<string, Dictionary<string, VoiceMember>> _calls = [];
    private readonly Dictionary<string, Guild> _guilds = [];
    private readonly Dictionary<string, Channel> _private = [];
    private readonly Dictionary<string, User> _users = [];
    private readonly Dictionary<string, (string? Last, int Mentions)> _read = [];
    private readonly HashSet<string> _muted = [];
    // When a timed mute ends (mute_config.end_time): server or channel id -> end. Past its end it no longer mutes, as in Discord.
    private readonly Dictionary<string, DateTimeOffset> _muteEnds = [];
    // Categories the user collapsed (their channel_overrides "collapsed"), kept across Discord apps.
    private readonly HashSet<string> _collapsed = [];
    // Channel override flags (Discord's OPT_IN_ENABLED 4096 and others), and servers in opt-in mode (guild flag 16384).
    private readonly Dictionary<string, int> _flags = [];
    private readonly Dictionary<string, int> _guildFlags = [];
    // The user's notification settings: server or channel id -> 0 all messages, 1 only mentions, 2 nothing (absent: as the
    // category, then the server, then the server's default), and the servers where @everyone and role mentions don't count.
    private readonly Dictionary<string, int> _notify = [];
    private readonly HashSet<string> _suppressEveryone = [], _suppressRoles = [];
    // Servers whose own-roles changed from a member that arrived, told to the page once the event is applied.
    private readonly HashSet<string> _myRolesMoved = [];
    internal const int OptInEnabled = 4096, OptInChannelsOn = 16384;
    private List<string> _order = [];
    // Friends and requests: user id -> type (1 friend, 3 incoming, 4 outgoing; blocked users are left out).
    private readonly Dictionary<string, int> _relationships = [];
    // People the user blocked (relationship type 2) or ignored (a relationship's user_ignored): they never notify, as in Discord.
    private readonly HashSet<string> _blocked = [], _ignored = [];
    // Presence of friends and DM people: user id -> (status, what they're doing).
    private readonly Dictionary<string, Presence> _presence = [];

    /// <summary>What Discord shows under a name: the custom status, else the activity (no verb, as Discord writes it),
    /// with its type for the icon, and the activity itself for Active Now.</summary>
    internal sealed record Presence(string Status, string? Activity, int? Kind, Rich? Rich);

    internal sealed record Rich(int Type, string Name, string? Details, string? State, long? Start, string? Image);
    private string _myStatus = "online";
    // The server list as Discord keeps it (guild_folders): folders and servers on their own, top to bottom. _order is its
    // servers that are here; servers it lacks go on top, as Discord's SortedGuildStore adds them.
    private List<GuildFolder> _rail = [];
    // Servers READY sent as unavailable (an outage): kept in the list when it is saved, as Discord keeps them.
    private readonly HashSet<string> _unavailable = [];

    internal User? Me { get; private set; }

    /// <summary>The user has Nitro, so their own server emoji work everywhere; without it, other servers' emoji go as pictures.</summary>
    internal bool Premium { get; private set; }

    internal void LoadReady(JsonElement d)
    {
        lock (_gate)
        {
            _guilds.Clear(); _private.Clear(); _users.Clear(); _read.Clear(); _muted.Clear(); _collapsed.Clear(); _flags.Clear(); _guildFlags.Clear(); _order = []; _unavailable.Clear();
            _notify.Clear(); _suppressEveryone.Clear(); _suppressRoles.Clear();
            // A new session: DM calls that ended meanwhile are gone (Discord sends a CALL_CREATE for each one still on).
            _calls.Clear();
            Me = ReadUser(d.GetProperty("user"));
            Remember(Me);
            Premium = Int(d.GetProperty("user"), "premium_type") > 0;
            foreach (var user in Array(d, "users")) Remember(ReadUser(user));
            var merged = Array(d, "merged_members").ToList();
            var index = 0;
            foreach (var g in Array(d, "guilds"))
            {
                // A server's merged members can be up to 20 people (those in its voice calls and such), not only the user:
                // theirs is picked by id. Taking the first gave someone else's roles (role-only channels hidden until the
                // member list loaded, and gone again after a reconnect). Without it the session asks Discord for it.
                JsonElement? mine = index < merged.Count && merged[index].ValueKind == JsonValueKind.Array
                    ? merged[index].EnumerateArray().Where(m => Str(m, "user_id") == Me.Id).Cast<JsonElement?>().FirstOrDefault() : null;
                index++;
                if (Bool(g, "unavailable")) { if (Str(g, "id") is { } away) _unavailable.Add(away); continue; }
                var guild = ReadGuild(g, mine);
                _guilds[guild.Id] = guild;
                _order.Add(guild.Id);
            }
            foreach (var c in Array(d, "private_channels"))
            {
                var channel = ReadChannel(c, null);
                _private[channel.Id] = channel;
            }
            foreach (var entry in Entries(d, "read_state"))
            {
                if (entry.TryGetProperty("read_state_type", out var type) && type.ValueKind == JsonValueKind.Number && type.GetInt32() != 0) continue;
                _read[Str(entry, "id") ?? ""] = (Str(entry, "last_message_id"), Int(entry, "mention_count"));
            }
            foreach (var entry in Entries(d, "user_guild_settings")) ApplyGuildSettings(entry);
            _relationships.Clear(); _blocked.Clear(); _ignored.Clear(); _presence.Clear();
            foreach (var r in Array(d, "relationships"))
            {
                if (r.TryGetProperty("user", out var ru) && ru.ValueKind == JsonValueKind.Object) Remember(ReadUser(ru));
                var type = Int(r, "type");
                if (Str(r, "id") is { } rid && type is 1 or 3 or 4) _relationships[rid] = type;
                if (type == 2 && Str(r, "id") is { } blockedId) _blocked.Add(blockedId);
                if (Bool(r, "user_ignored") && Str(r, "id") is { } ignoredId) _ignored.Add(ignoredId);
            }
            foreach (var p in Array(d, "presences")) ApplyPresence(p);
            // DEDUPE_USER_OBJECTS: presences arrive in READY's merged_presences, friends' and each server's.
            if (d.TryGetProperty("merged_presences", out var mergedPresences)) ApplyMergedPresences(mergedPresences);
            if (d.TryGetProperty("user_settings", out var us) && Str(us, "status") is { } status) _myStatus = status;
            SetRail(d.TryGetProperty("user_settings", out var settings) ? ReadRail(settings) : []);
        }
    }

    // guild_folders from user settings (READY's or a USER_SETTINGS_UPDATE), else guild_positions (an account from before folders).
    private static List<GuildFolder> ReadRail(JsonElement settings)
    {
        List<GuildFolder> rail = [.. Array(settings, "guild_folders").Select(f => new GuildFolder(Str(f, "id"), Str(f, "name"),
            f.TryGetProperty("color", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var rgb) ? rgb : null,
            [.. Array(f, "guild_ids").Select(Id).OfType<string>()]))];
        return rail.Count > 0 ? rail : [.. Array(settings, "guild_positions").Select(Id).OfType<string>().Select(id => new GuildFolder(null, null, null, [id]))];
    }

    // The list as given, with the servers here that it lacks on top (one just joined first), and _order from it.
    private void SetRail(List<GuildFolder> rail)
    {
        var placed = rail.SelectMany(f => f.Guilds).ToHashSet();
        var missing = _guilds.Keys.Where(id => !placed.Contains(id)).OrderBy(_order.IndexOf).ToList();
        _rail = [.. missing.Select(id => new GuildFolder(null, null, null, [id])), .. rail];
        _order = [.. _rail.SelectMany(f => f.Guilds).Where(_guilds.ContainsKey)];
    }

    /// <summary>Puts the server list in this order at once (a drag in the rail, saved meanwhile); returns the one it had.</summary>
    internal List<GuildFolder> ReplaceRail(List<GuildFolder> rail)
    {
        lock (_gate)
        {
            var before = _rail;
            SetRail(rail);
            return before;
        }
    }

    /// <summary>One gateway dispatch; returns the events the UI gets for it (none for what it may not see).</summary>
    internal List<(string Name, object Data)> Apply(string type, JsonElement d)
    {
        var events = new List<(string, object)>();
        lock (_gate)
        {
            switch (type)
            {
                case "MESSAGE_CREATE":
                case "MESSAGE_UPDATE":
                case "MESSAGE_DELETE":
                    OnMessage(type, d, events);
                    break;
                case "MESSAGE_ACK":
                    if (Str(d, "channel_id") is { } acked && Find(acked) is not null)
                    {
                        _read[acked] = (Str(d, "message_id"), d.TryGetProperty("mention_count", out var mc) && mc.ValueKind == JsonValueKind.Number ? mc.GetInt32() : 0);
                        events.Add(("discord.unread", UnreadNow(acked)));
                        // Read here or in another Discord app: its notifications go, as Discord's MESSAGE_ACK clears them.
                        events.Add(("discord.notifyClear", new NotifyClear(acked, null)));
                    }
                    break;
                case "TYPING_START":
                    if (Str(d, "channel_id") is { } typed && Visible(typed) && Str(d, "user_id") is { } who && who != Me?.Id)
                        events.Add(("discord.typing", new { channelId = typed, userId = who, name = TypingName(d, who) }));
                    break;
                case "GUILD_CREATE":
                case "GUILD_UPDATE":
                    if (Str(d, "id") is { } gid && !Bool(d, "unavailable"))
                    {
                        var existing = _guilds.GetValueOrDefault(gid);
                        var guild = ReadGuild(d, null, existing);
                        _guilds[gid] = guild;
                        // A server back from an outage takes its place again; one just joined goes on top.
                        _unavailable.Remove(gid);
                        SetRail(_rail);
                        events.Add(("discord.changed", new { guildId = gid }));
                    }
                    break;
                case "GUILD_SCHEDULED_EVENT_CREATE":
                case "GUILD_SCHEDULED_EVENT_UPDATE":
                case "GUILD_SCHEDULED_EVENT_DELETE":
                    if (Str(d, "guild_id") is { } scheduledGuild && _guilds.TryGetValue(scheduledGuild, out var eventGuild) && Str(d, "id") is { } eventId)
                    {
                        if (type != "GUILD_SCHEDULED_EVENT_DELETE" && Int(d, "status") is 1 or 2) eventGuild.Events.Add(eventId);
                        else eventGuild.Events.Remove(eventId);
                        events.Add(("discord.changed", new { guildId = scheduledGuild }));
                    }
                    break;
                case "GUILD_DELETE":
                    if (Str(d, "id") is { } gone && !Bool(d, "unavailable"))
                    {
                        _guilds.Remove(gone); _order.Remove(gone);
                        events.Add(("discord.changed", new { guildId = gone }));
                    }
                    break;
                case "CHANNEL_CREATE":
                case "CHANNEL_UPDATE":
                case "CHANNEL_DELETE":
                case "THREAD_CREATE":
                case "THREAD_UPDATE":
                case "THREAD_DELETE":
                    OnChannel(type, d, events);
                    break;
                case "GUILD_ROLE_CREATE":
                case "GUILD_ROLE_UPDATE":
                    if (Str(d, "guild_id") is { } rg && _guilds.TryGetValue(rg, out var rguild) && d.TryGetProperty("role", out var role))
                    {
                        rguild.Roles[Str(role, "id") ?? ""] = Perms(role, "permissions");
                        rguild.Styles[Str(role, "id") ?? ""] = ReadStyle(role);
                        events.Add(("discord.changed", new { guildId = rg }));
                    }
                    break;
                case "GUILD_ROLE_DELETE":
                    if (Str(d, "guild_id") is { } dg && _guilds.TryGetValue(dg, out var dguild) && Str(d, "role_id") is { } rid)
                    {
                        dguild.Roles.Remove(rid); dguild.Styles.Remove(rid); dguild.MyRoles.Remove(rid);
                        events.Add(("discord.changed", new { guildId = dg }));
                    }
                    break;
                case "GUILD_MEMBER_UPDATE":
                    if (Str(d, "guild_id") is { } mg && _guilds.TryGetValue(mg, out var mguild) &&
                        d.TryGetProperty("user", out var mu) && Str(mu, "id") == Me?.Id)
                    {
                        mguild.MyRoles = [.. Array(d, "roles").Select(Id).OfType<string>()];
                        mguild.MyRolesKnown = true;
                        events.Add(("discord.changed", new { guildId = mg }));
                    }
                    // Someone else's roles or nickname changed (a moderator's menu shows them).
                    else if (Str(d, "guild_id") is { } og && _guilds.TryGetValue(og, out var oguild))
                    {
                        RememberMember(oguild, d);
                        events.Add(("discord.colors", new { guildId = og }));
                    }
                    break;
                case "MESSAGE_REACTION_ADD":
                case "MESSAGE_REACTION_REMOVE":
                    if (Str(d, "channel_id") is { } rc && Visible(rc) && d.TryGetProperty("emoji", out var emoji))
                        events.Add(("discord.reaction", new
                        {
                            channelId = rc,
                            messageId = Str(d, "message_id"),
                            add = type == "MESSAGE_REACTION_ADD",
                            me = Str(d, "user_id") == Me?.Id,
                            userId = Str(d, "user_id"),
                            emoji = new { id = Str(emoji, "id"), name = Str(emoji, "name"), animated = Bool(emoji, "animated") },
                            // A super reaction, with the colors its burst plays in.
                            burst = Bool(d, "burst"),
                            burstColors = Array(d, "burst_colors").Where(c => c.ValueKind == JsonValueKind.String).Select(c => c.GetString()!).Take(8).ToArray(),
                        }));
                    break;
                case "MESSAGE_POLL_VOTE_ADD":
                case "MESSAGE_POLL_VOTE_REMOVE":
                    if (Str(d, "channel_id") is { } pc && Visible(pc) && d.TryGetProperty("answer_id", out var answer) && answer.ValueKind == JsonValueKind.Number)
                        events.Add(("discord.pollVote", new
                        {
                            channelId = pc,
                            messageId = Str(d, "message_id"),
                            answerId = answer.GetInt32(),
                            add = type == "MESSAGE_POLL_VOTE_ADD",
                            me = Str(d, "user_id") == Me?.Id,
                        }));
                    break;
                // Someone played a soundboard sound in a voice channel: each client plays it itself, as Discord's does.
                case "VOICE_CHANNEL_EFFECT_SEND":
                    // The user's own sound already played here when they pressed it.
                    // Muted in Voice settings: none play; otherwise at the sender's volume times Soundboard Volume.
                    if (Str(d, "channel_id") is { } vc && Str(d, "sound_id") is { } sid && Str(d, "user_id") != Me?.Id && !App.Services.Settings.Current.DiscordSoundboardMuted)
                        events.Add(("discord.soundboard", new
                        {
                            channelId = vc,
                            userId = Str(d, "user_id"),
                            soundId = sid,
                            volume = (d.TryGetProperty("sound_volume", out var sv) && sv.ValueKind == JsonValueKind.Number ? sv.GetDouble() : 1.0) * App.Services.Settings.Current.DiscordSoundboardVolume / 100.0,
                        }));
                    break;
                case "GUILD_EMOJIS_UPDATE":
                    if (Str(d, "guild_id") is { } eg && _guilds.TryGetValue(eg, out var eguild))
                        eguild.Emojis = ReadEmojis(d);
                    break;
                case "READY_SUPPLEMENTAL":
                    if (d.TryGetProperty("merged_presences", out var mp)) ApplyMergedPresences(mp);
                    events.Add(("discord.changed", new { guildId = (string?)null, friends = true }));
                    break;
                case "PRESENCE_UPDATE":
                    if ((Str(d, "guild_id") is null || Watched(PresenceUser(d))) && ApplyPresence(d) is { } presenceUser)
                        events.Add(("discord.presence", PresenceEvent(presenceUser)));
                    break;
                case "PRESENCES_REPLACE":
                    // Discord resyncs presences this way after READY; friends and DM people only, as everywhere.
                    if (d.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var p in d.EnumerateArray())
                            if (Watched(PresenceUser(p))) ApplyPresence(p);
                        events.Add(("discord.changed", new { guildId = (string?)null, friends = true }));
                    }
                    break;
                case "RELATIONSHIP_ADD":
                case "RELATIONSHIP_UPDATE":
                    if (d.TryGetProperty("user", out var addedUser) && addedUser.ValueKind == JsonValueKind.Object) Remember(ReadUser(addedUser));
                    if (Str(d, "id") is { } added && Int(d, "type") is var t && t is 1 or 3 or 4) _relationships[added] = t;
                    if (Str(d, "id") is { } relId) { if (Int(d, "type") == 2) { _blocked.Add(relId); _relationships.Remove(relId); } else _blocked.Remove(relId); }
                    if (Str(d, "id") is { } ignoredRel && d.TryGetProperty("user_ignored", out _)) { if (Bool(d, "user_ignored")) _ignored.Add(ignoredRel); else _ignored.Remove(ignoredRel); }
                    events.Add(("discord.changed", new { guildId = (string?)null, friends = true }));
                    break;
                case "RELATIONSHIP_REMOVE":
                    if (Str(d, "id") is { } removed) { _relationships.Remove(removed); _blocked.Remove(removed); }
                    events.Add(("discord.changed", new { guildId = (string?)null, friends = true }));
                    break;
                case "USER_UPDATE":
                    // The account itself changed (a decoration, nameplate or name style put on in any Discord app).
                    if (Str(d, "id") is { } self && self == Me?.Id)
                    {
                        Remember(ReadUser(d));
                        Me = _users[self];
                        events.Add(("discord.changed", new { guildId = (string?)null }));
                    }
                    break;
                case "USER_SETTINGS_UPDATE":
                    // The server list changed here or in another Discord app.
                    if (d.TryGetProperty("guild_folders", out _))
                    {
                        SetRail(ReadRail(d));
                        events.Add(("discord.changed", new { guildId = (string?)null }));
                    }
                    if (Str(d, "status") is { } mine)
                    {
                        _myStatus = mine;
                        events.Add(("discord.presence", new { userId = Me?.Id, status = mine, activity = (string?)null }));
                    }
                    break;
                case "GUILD_MEMBER_LIST_UPDATE":
                    if (Str(d, "guild_id") is { } lg && _guilds.TryGetValue(lg, out var lguild))
                    {
                        ApplyMemberList(lguild, d);
                        events.Add(("discord.members", new { guildId = lg }));
                    }
                    break;
                case "GUILD_MEMBERS_CHUNK":
                    if (Str(d, "guild_id") is { } cg && _guilds.TryGetValue(cg, out var cguild))
                    {
                        var arrived = 0;
                        foreach (var m in Array(d, "members")) { RememberMember(cguild, m); arrived++; }
                        foreach (var missing in Array(d, "not_found").Select(Id).OfType<string>()) cguild.NotFound.Add(missing);
                        // Only new members change a color; a chunk of none would have the room ask again, forever.
                        if (arrived > 0) events.Add(("discord.colors", new { guildId = cg }));
                    }
                    break;
                case "VOICE_STATE_UPDATE":
                    if (Str(d, "guild_id") is { } vg && _guilds.TryGetValue(vg, out var vguild) && Str(d, "user_id") is { } vu)
                    {
                        if (ReadVoice(d) is { } member) vguild.Voice[vu] = member;
                        else vguild.Voice.Remove(vu);
                        events.Add(("discord.changed", new { guildId = vg, voice = true }));
                    }
                    else if (Str(d, "guild_id") is null && Str(d, "user_id") is { } cu)
                    {
                        // A DM call: who is in it, for the call's view above the DM.
                        foreach (var call in _calls.Values) call.Remove(cu);
                        if (ReadVoice(d) is { } caller && _private.ContainsKey(caller.ChannelId))
                        {
                            if (!_calls.TryGetValue(caller.ChannelId, out var people)) _calls[caller.ChannelId] = people = [];
                            people[cu] = caller;
                        }
                        events.Add(("discord.changed", new { guildId = (string?)null, voice = true }));
                    }
                    break;
                case "USER_GUILD_SETTINGS_UPDATE":
                    ApplyGuildSettings(d);
                    events.Add(("discord.changed", new { guildId = Str(d, "guild_id") }));
                    break;
            }
            // The user's roles learned from a member: their channels and actions change with them.
            foreach (var moved in _myRolesMoved) events.Add(("discord.changed", new { guildId = moved }));
            _myRolesMoved.Clear();
        }
        return events;
    }

    private void OnMessage(string type, JsonElement d, List<(string, object)> events)
    {
        var channelId = Str(d, "channel_id");
        if (channelId is null || !Visible(channelId)) return;
        var channel = Find(channelId)!;
        var kind = type == "MESSAGE_CREATE" ? "create" : type == "MESSAGE_UPDATE" ? "update" : "delete";
        if (kind == "delete")
        {
            // A deleted message's notification goes with it.
            if (Str(d, "id") is { } gone) events.Add(("discord.notifyClear", new NotifyClear(channelId, gone)));
            events.Add(("discord.message", new { kind, channelId, id = Str(d, "id") }));
            return;
        }
        if (kind == "create")
        {
            var id = Str(d, "id");
            channel.LastMessageId = id;
            var author = d.TryGetProperty("author", out var a) ? Str(a, "id") : null;
            var read = _read.GetValueOrDefault(channelId);
            // Read already (a replay after it was read elsewhere) or notified already (a resume sending it again): no second notice.
            var fresh = id is not null && Snowflake(id) > Snowflake(read.Last ?? "0");
            var mentioned = author != Me?.Id && MentionsMe(d, channel);
            if (author == Me?.Id) _read[channelId] = (id, 0);
            else if (mentioned) _read[channelId] = (read.Last, read.Mentions + 1);
            if (d.TryGetProperty("author", out var who) && Str(who, "id") is not null) Remember(ReadUser(who));
            if (channel.GuildId is { } mg2 && _guilds.TryGetValue(mg2, out var mguild2) && d.TryGetProperty("member", out var msgMember) && author is not null)
                mguild2.Members[author] = (Array(msgMember, "roles").Select(Id).OfType<string>().ToList(), Str(msgMember, "nick"));
            events.Add(("discord.unread", UnreadNow(channelId)));
            if (fresh && Notifies(d, channel, author, mentioned) && Remembered(id!)) events.Add(("discord.notify", Notice(d, channel)));
        }
        events.Add(("discord.message", new { kind, channelId, message = d.Clone() }));
    }

    private void OnChannel(string type, JsonElement d, List<(string, object)> events)
    {
        var guildId = Str(d, "guild_id");
        var channel = ReadChannel(d, guildId);
        if (guildId is null)
        {
            if (type == "CHANNEL_DELETE") _private.Remove(channel.Id);
            else _private[channel.Id] = channel;
            foreach (var user in Array(d, "recipients")) Remember(ReadUser(user));
        }
        else if (_guilds.TryGetValue(guildId, out var guild))
        {
            if (type is "CHANNEL_DELETE" or "THREAD_DELETE") guild.Channels.Remove(channel.Id);
            else
            {
                if (guild.Channels.TryGetValue(channel.Id, out var old)) channel.LastMessageId ??= old.LastMessageId;
                guild.Channels[channel.Id] = channel;
                // A new post is a forum's newest message, as Discord counts it: the forum lights up, unless the post is the user's own.
                if (type == "THREAD_CREATE" && channel.ParentId is { } parentId && guild.Channels.TryGetValue(parentId, out var forum)
                    && forum.Type is 15 or 16 && Snowflake(channel.Id) > Snowflake(forum.LastMessageId ?? "0"))
                {
                    forum.LastMessageId = channel.Id;
                    if (Str(d, "owner_id") is { } owner && owner == Me?.Id) _read[parentId] = (channel.Id, 0);
                }
            }
        }
        events.Add(("discord.changed", new { guildId }));
    }

    /// <summary>
    /// Discord's relationship with someone (0 none, 1 friends, 2 blocked, 3 their request, 4 yours), whether they're
    /// ignored, and whether they're a bot (no friend or call rows), for the person menu.
    /// </summary>
    internal (int Type, bool Ignored, bool Bot) RelationshipOf(string userId)
    {
        lock (_gate) return (_blocked.Contains(userId) ? 2 : _relationships.GetValueOrDefault(userId), _ignored.Contains(userId), _users.GetValueOrDefault(userId)?.Bot == true);
    }

    /// <summary>Ignore or Unignore taken by Discord (its gateway says so too, as the relationship's user_ignored).</summary>
    internal void SetIgnored(string userId, bool ignored)
    {
        lock (_gate) { if (ignored) _ignored.Add(userId); else _ignored.Remove(userId); }
    }

    /// <summary>The newest message the user had read in a channel, for Discord's NEW line.</summary>
    internal string? LastRead(string channelId)
    {
        lock (_gate) return _read.GetValueOrDefault(channelId).Last;
    }

    /// <summary>
    /// Read here to the channel's newest message, as Discord acks its last_message_id (a newest message since deleted
    /// doesn't keep it unread), and never back from where it was read; returns the channel's unread state for the page.
    /// </summary>
    internal object MarkRead(string channelId, string messageId)
    {
        lock (_gate)
        {
            var newest = new[] { messageId, Find(channelId)?.LastMessageId, _read.GetValueOrDefault(channelId).Last }
                .OfType<string>().MaxBy(Snowflake)!;
            _read[channelId] = (newest, 0);
            return UnreadNow(channelId);
        }
    }

    /// <summary>Mark Unread: read up to exactly this message (the one before the message chosen).</summary>
    internal void MarkUnread(string channelId, string messageId)
    {
        lock (_gate) _read[channelId] = (messageId, 0);
    }

    /// <summary>The server a visible channel is in (null for a DM); throws for a channel the user can't see.</summary>
    internal string? GuildOf(string channelId)
    {
        lock (_gate)
            return Visible(channelId) ? Find(channelId)!.GuildId : throw new UnauthorizedAccessException("Not a channel you can see.");
    }

    internal bool CanSend(string channelId)
    {
        lock (_gate)
            return Find(channelId) is { } c && (c.GuildId is null || (Perms(c) & (ViewChannel | SendMessages)) == (ViewChannel | SendMessages));
    }

    /// <summary>Manage Messages in a server channel: others' reactions can be taken off.</summary>
    internal bool CanManageMessages(string channelId)
    {
        lock (_gate) return Find(channelId) is { GuildId: not null } c && (Perms(c) & ManageMessages) != 0;
    }

    internal object Home()
    {
        lock (_gate)
        {
            var dms = _private.Values
                .OrderByDescending(c => Snowflake(c.LastMessageId ?? c.Id))
                .Select(c => new
                {
                    id = c.Id,
                    name = DmName(c),
                    userId = c.Recipients.Count == 1 ? c.Recipients[0] : null,
                    username = c.Recipients.Count == 1 ? _users.GetValueOrDefault(c.Recipients[0])?.Username : null,
                    recipients = c.Recipients.ToArray(),
                    subtitle = c.Recipients.Count > 1 ? $"{c.Recipients.Count + 1} Members" : _presence.GetValueOrDefault(c.Recipients.FirstOrDefault() ?? "")?.Activity,
                    status = c.Recipients.Count == 1 ? _presence.GetValueOrDefault(c.Recipients[0])?.Status ?? "offline" : null,
                    look = c.Recipients.Count == 1 ? LookOf(c.Recipients[0]) : null,
                    avatar = c.Recipients.Count == 1 ? Avatar(c.Recipients[0]) : c.Icon is null ? null : $"https://cdn.discordapp.com/channel-icons/{c.Id}/{c.Icon}.webp?size=256&quality=lossless",
                    group = c.Recipients.Count > 1,
                    unread = Unread(c),
                    mentions = _read.GetValueOrDefault(c.Id).Mentions,
                    muted = IsMuted(c.Id),
                    mutedUntil = IsMuted(c.Id) && _muteEnds.TryGetValue(c.Id, out var dmMuteEnd) ? dmMuteEnd.ToUnixTimeMilliseconds() : (long?)null,
                    call = _calls.TryGetValue(c.Id, out var call) && call.Count > 0
                        ? call.Select(v => new { id = v.Key, name = v.Value.Name, avatar = v.Value.Avatar, look = LookOf(v.Key), mute = v.Value.Mute, deaf = v.Value.Deaf, video = v.Value.Video, stream = v.Value.Stream }).ToArray()
                        : null,
                })
                .ToList();
            var guilds = _order.Where(_guilds.ContainsKey).Select(id => _guilds[id]).Select(g =>
            {
                var visible = g.Channels.Values.Where(c => (Perms(c) & ViewChannel) != 0).ToList();
                return new
                {
                    id = g.Id,
                    name = g.Name,
                    icon = g.Icon is null ? null : $"https://cdn.discordapp.com/icons/{g.Id}/{g.Icon}.webp?size=256&quality=lossless",
                    muted = IsMuted(g.Id),
                    mutedUntil = IsMuted(g.Id) && _muteEnds.TryGetValue(g.Id, out var guildMuteEnd) ? guildMuteEnd.ToUnixTimeMilliseconds() : (long?)null,
                    notify = _notify.TryGetValue(g.Id, out var guildNotify) ? guildNotify : g.DefaultNotifications,
                    suppressEveryone = _suppressEveryone.Contains(g.Id),
                    suppressRoles = _suppressRoles.Contains(g.Id),
                    owner = Me is not null && g.OwnerId == Me.Id,
                    unread = !IsMuted(g.Id) && visible.Any(Unread),
                    mentions = visible.Sum(c => _read.GetValueOrDefault(c.Id).Mentions),
                };
            }).ToList();
            // The server list as Discord shows it: servers that are here or unavailable (a folder of one server is still a folder).
            var rail = _rail.Select(f => f with { Guilds = [.. f.Guilds.Where(id => _guilds.ContainsKey(id) || _unavailable.Contains(id))] })
                .Where(f => f.Guilds.Count > 0).Select(f => new { id = f.Id, name = f.Name, color = f.Color, guildIds = f.Guilds }).ToList();
            return new { me = Me is null ? null : new { id = Me.Id, name = Me.Name, avatar = Avatar(Me.Id), status = _myStatus, look = LookOf(Me.Id) }, dms, guilds, rail, blocked = _blocked.ToList(), ignored = _ignored.ToList() };
        }
    }

    /// <summary>A server's channels the user can see, in Discord's order: loose channels, then each category with its own.</summary>
    internal object Channels(string guildId)
    {
        lock (_gate)
        {
            if (!_guilds.TryGetValue(guildId, out var guild)) return new { name = (string?)null, channels = System.Array.Empty<object>() };
            var visible = guild.Channels.Values.Where(c => c.Type != 4 && IsListed(c.Type) && (Perms(c) & ViewChannel) != 0).ToList();
            var list = new List<object>();
            void Add(IEnumerable<Channel> channels)
            {
                foreach (var c in channels.OrderBy(c => IsVoice(c.Type)).ThenBy(c => c.Position).ThenBy(c => Snowflake(c.Id)))
                {
                    var perms = Perms(c);
                    var everyone = EveryonePerms(guild, c);
                    list.Add(new
                    {
                        id = c.Id,
                        name = c.Name ?? "",
                        type = c.Type,
                        kind = IsVoice(c.Type) ? "voice" : c.Type is 15 or 16 ? "forum" : "text",
                        topic = c.Topic,
                        // Seconds between the user's messages; Manage Messages or Manage Channel are exempt, as in Discord.
                        slowmode = (perms & (ManageMessages | ManageChannels)) != 0 ? 0 : c.RateLimit,
                        canRead = (perms & ReadHistory) != 0,
                        canSend = (perms & SendMessages) != 0,
                        muted = MutedChannel(c),
                        // Until when a timed mute lasts (ms), and the channel's own notification setting (null: as the level above).
                        mutedUntil = IsMuted(c.Id) && _muteEnds.TryGetValue(c.Id, out var muteEnd) ? muteEnd.ToUnixTimeMilliseconds() : (long?)null,
                        notify = _notify.TryGetValue(c.Id, out var own) ? own : (int?)null,
                        notifyDefault = InheritedNotify(c, guild),
                        unread = Unread(c),
                        mentions = _read.GetValueOrDefault(c.Id).Mentions,
                        canConnect = IsVoice(c.Type) && (perms & Connect) != 0,
                        // Moderation here: others' messages, pins, and the channel's own settings.
                        canManageMessages = (perms & ManageMessages) != 0,
                        canPin = (perms & PinPermission(guild)) != 0,
                        canEdit = (perms & (ManageChannels | ManageRoles)) != 0,
                        optedIn = OptedIn(c.Id) || (c.ParentId is { } parent && OptedIn(parent)),
                        // Opt-in mode never hides these: Browse Channels lists only what everyone can see.
                        @private = (everyone & ViewChannel) == 0,
                        // Discord's lock on the channel's icon: private, or a voice channel @everyone can't join.
                        locked = (everyone & ViewChannel) == 0 || (IsVoice(c.Type) && (everyone & Connect) == 0),
                        resource = (c.Flags & 128) != 0,
                        rules = c.Id == guild.RulesChannelId,
                        // Discord's server tooltip leaves out who is in the AFK channel.
                        afk = c.Id == guild.AfkChannelId,
                        active = c.LastMessageId is { } lastId ? (long?)((long)(Snowflake(lastId) >> 22) + 1420070400000L) : null,
                        members = IsVoice(c.Type)
                            ? guild.Voice.Where(v => v.Value.ChannelId == c.Id).Select(v => new { id = v.Key, name = v.Value.Name, avatar = v.Value.Avatar, look = LookOf(v.Key), mute = v.Value.Mute, deaf = v.Value.Deaf, video = v.Value.Video, stream = v.Value.Stream }).ToArray()
                            : null,
                    });
                }
            }
            Add(visible.Where(c => c.ParentId is null || !guild.Channels.ContainsKey(c.ParentId)));
            foreach (var category in guild.Channels.Values.Where(c => c.Type == 4).OrderBy(c => c.Position).ThenBy(c => Snowflake(c.Id)))
            {
                var children = visible.Where(c => c.ParentId == category.Id).ToList();
                if (children.Count == 0) continue;
                list.Add(new { id = category.Id, name = category.Name ?? "", type = 4, kind = "category", collapsed = _collapsed.Contains(category.Id), optedIn = OptedIn(category.Id) });
                Add(children);
            }
            var perms = GuildPerms(guild);
            var community = guild.Features.Contains("COMMUNITY");
            // Threads Exo knows (active ones from Discord, and posts opened): a notification or a jump to one opens it in its channel.
            var threads = guild.Channels.Values.Where(c => c.Type is 10 or 11 or 12 && c.ParentId is not null && (Perms(c) & ViewChannel) != 0)
                .Select(c => new { id = c.Id, name = c.Name ?? "", parentId = c.ParentId, locked = c.Locked }).ToList();
            var roles = guild.Styles.Where(r => r.Key != guild.Id)
                .ToDictionary(r => r.Key, r => new { name = r.Value.Name, color = r.Value.Color > 0 ? DiscordColors.Readable(r.Value.Color) : null });
            return new
            {
                name = guild.Name,
                threads,
                roles,
                icon = guild.Icon is { } icon ? $"https://cdn.discordapp.com/icons/{guild.Id}/{icon}.webp?size=256&quality=lossless" : null,
                channels = list,
                canManage = (perms & ManageChannels) != 0,
                canManageEvents = (perms & ManageEvents) != 0,
                // What this user may do in Server Settings and to members, from the server's permissions.
                moderation = ModerationOf(guild, perms),
                // Opt-in mode: the sidebar shows only the channels (or categories) the user follows.
                optIn = OptInMode(guild.Id),
                events = guild.Events.Count,
                // Discord's rows over the channels: which this server has, and which the user may open.
                rows = new
                {
                    guide = community,
                    events = community || (perms & ManageEvents) != 0,
                    browse = guild.Features.Contains("GUILD_ONBOARDING"),
                    members = community && (perms & Moderation) != 0,
                },
            };
        }
    }

    /// <summary>A server's roles by id: name and color, for the Members page.</summary>
    internal Dictionary<string, object> Roles(string guildId)
    {
        lock (_gate)
            return _guilds.TryGetValue(guildId, out var guild)
                ? guild.Styles.Where(s => s.Key != guildId).OrderByDescending(s => s.Value.Position)
                    .ToDictionary(s => s.Key, s => (object)new { name = s.Value.Name, color = s.Value.Color > 0 ? DiscordColors.Readable(s.Value.Color) : null, position = s.Value.Position })
                : [];
    }

    private bool OptedIn(string channelId) => (_flags.GetValueOrDefault(channelId) & OptInEnabled) != 0;

    internal int FlagsOf(string channelId)
    {
        lock (_gate) return _flags.GetValueOrDefault(channelId);
    }

    internal int GuildFlags(string guildId)
    {
        lock (_gate) return _guildFlags.GetValueOrDefault(guildId);
    }

    /// <summary>The server's listed channels the user can see, with their flags: what turning opt-in mode on keeps followed.</summary>
    internal List<(string Id, string? Parent, int Flags)> ListedChannels(string guildId)
    {
        lock (_gate)
            return _guilds.TryGetValue(guildId, out var guild)
                ? [.. guild.Channels.Values.Where(c => c.Type != 4 && IsListed(c.Type) && (Perms(c) & ViewChannel) != 0).Select(c => (c.Id, c.ParentId, _flags.GetValueOrDefault(c.Id)))]
                : [];
    }

    internal string? ParentOf(string channelId)
    {
        lock (_gate) return Find(channelId)?.ParentId;
    }

    internal bool IsCategory(string channelId)
    {
        lock (_gate) return Find(channelId)?.Type == 4;
    }

    internal bool IsThread(string channelId)
    {
        lock (_gate) return Find(channelId)?.Type is 10 or 11 or 12;
    }

    internal void SetMuted(string channelId, bool muted, DateTimeOffset? until = null)
    {
        lock (_gate)
        {
            if (muted) _muted.Add(channelId); else _muted.Remove(channelId);
            if (muted && until is { } end) _muteEnds[channelId] = end; else _muteEnds.Remove(channelId);
        }
    }

    /// <summary>A channel's own notification setting: 0 all messages, 1 only mentions, 2 nothing, 3 as its category or server.</summary>
    /// <summary>A server's Suppress @everyone and @here / Suppress All Role @mentions, once Discord took the change.</summary>
    internal void SetSuppress(string guildId, bool? everyone, bool? roles)
    {
        lock (_gate)
        {
            if (everyone is { } e) { if (e) _suppressEveryone.Add(guildId); else _suppressEveryone.Remove(guildId); }
            if (roles is { } r) { if (r) _suppressRoles.Add(guildId); else _suppressRoles.Remove(guildId); }
        }
    }

    internal void SetNotifyLevel(string channelId, int level)
    {
        lock (_gate) { if (level is >= 0 and <= 2) _notify[channelId] = level; else _notify.Remove(channelId); }
    }

    internal void SetCollapsed(string categoryId, bool collapsed)
    {
        lock (_gate) { if (collapsed) _collapsed.Add(categoryId); else _collapsed.Remove(categoryId); }
    }

    internal bool Collapsed(string categoryId)
    {
        lock (_gate) return _collapsed.Contains(categoryId);
    }

    // The user's server-wide permissions: @everyone and their roles.
    private ulong GuildPerms(Guild guild)
    {
        if (Me is null || guild.OwnerId == Me.Id) return All;
        var perms = guild.Roles.GetValueOrDefault(guild.Id);
        foreach (var role in guild.MyRoles) perms |= guild.Roles.GetValueOrDefault(role);
        return (perms & Administrator) != 0 ? All : perms;
    }

    /// <summary>A channel's unread mark and mentions, and its server's.</summary>
    internal object UnreadOf(string channelId)
    {
        lock (_gate) return UnreadNow(channelId);
    }

    // Under _gate.
    private object UnreadNow(string channelId)
    {
        var channel = Find(channelId);
        var guild = channel?.GuildId is { } gid ? _guilds.GetValueOrDefault(gid) : null;
        var visible = guild?.Channels.Values.Where(c => (Perms(c) & ViewChannel) != 0).ToList();
        return new
        {
            channelId,
            guildId = guild?.Id,
            unread = channel is not null && Unread(channel),
            mentions = _read.GetValueOrDefault(channelId).Mentions,
            guildUnread = guild is not null && !IsMuted(guild.Id) && visible!.Any(Unread),
            guildMentions = visible?.Sum(c => _read.GetValueOrDefault(c.Id).Mentions) ?? 0,
        };
    }

    /// <summary>Discord's own badge: mentions in servers plus unread messages in DMs (both counted as mentions).</summary>
    internal int Badge()
    {
        lock (_gate)
            return _private.Keys.Concat(_guilds.Values.SelectMany(g => g.Channels.Values.Where(c => (Perms(c) & ViewChannel) != 0).Select(c => c.Id)))
                .Sum(id => _read.GetValueOrDefault(id).Mentions);
    }

    /// <summary>Every server's custom emoji, and whether each can be sent here (else the picker shows it locked, as Discord does).</summary>
    internal object Emojis(string? guildId)
    {
        lock (_gate)
        {
            return new
            {
                premium = Premium,
                guilds = _order.Where(_guilds.ContainsKey).Select(id => _guilds[id]).Where(g => g.Emojis.Count > 0).Select(g => new
                {
                    id = g.Id,
                    name = g.Name,
                    // The picker's rail shows each server by its icon, as Discord's does.
                    icon = g.Icon is null ? null : $"https://cdn.discordapp.com/icons/{g.Id}/{g.Icon}.webp?size=64&quality=lossless",
                    emojis = g.Emojis.Where(e => e.Available).Select(e => new
                    {
                        id = e.Id,
                        name = e.Name,
                        animated = e.Animated,
                        // Without Nitro only this server's still emoji work as themselves.
                        native = Premium || (g.Id == guildId && !e.Animated),
                    }),
                }),
            };
        }
    }

    /// <summary>
    /// A notification: who (name and avatar), where (the channel or group, and the server; both null for a DM), the
    /// message shortened with mentions and emoji readable, which message it is (a Windows notification's tag, and
    /// its group is the channel, so reading the channel takes them all away), and a thread's channel, to open it in.
    /// </summary>
    internal sealed record Notification(string MessageId, string ChannelId, string? GuildId, string AuthorId, string Author,
        string? Avatar, string? Place, string? Server, string Body, string? ParentId = null);

    /// <summary>A channel's notifications to take away (read here or elsewhere), or one message's (deleted).</summary>
    internal sealed record NotifyClear(string ChannelId, string? MessageId);

    // Messages already notified, newest last: a resume or a second event for the same message never notifies twice.
    private readonly HashSet<string> _notified = [];
    private readonly Queue<string> _notifiedOrder = new();

    private bool Remembered(string messageId)
    {
        if (!_notified.Add(messageId)) return false;
        _notifiedOrder.Enqueue(messageId);
        if (_notifiedOrder.Count > 512) _notified.Remove(_notifiedOrder.Dequeue());
        return true;
    }

    /// <summary>
    /// Discord's own rule (its web client's shouldNotify, October 2026) for a new message: never your own, a
    /// @silent one (SUPPRESS_NOTIFICATIONS), a blocked person's, a message request's, or anything while Do Not
    /// Disturb is on. A DM or group notifies of every message unless muted or set otherwise. On a server: nothing
    /// when the server, its category or the channel (or a thread's channel) is muted, set to Nothing, or left out
    /// of an opt-in server's channels; else every message when set to All Messages (a voice channel's chat only
    /// while you are in that call), else mentions: you, @everyone and @here unless suppressed, your roles unless
    /// suppressed.
    /// </summary>
    private bool Notifies(JsonElement d, Channel channel, string? author, bool mentioned)
    {
        if (author is null || author == Me?.Id || _blocked.Contains(author) || _ignored.Contains(author) || _myStatus == "dnd") return false;
        if ((Int(d, "flags") & SuppressNotifications) != 0) return false;
        if (MutedChannel(channel)) return false;
        if (channel.GuildId is not { } guildId)
            return !channel.MessageRequest && NotifyLevel(channel) switch
            {
                0 => true,
                1 => Bool(d, "mention_everyone") || Array(d, "mentions").Any(m => Str(m, "id") == Me?.Id),
                _ => false,
            };
        if (IsMuted(guildId) || !OptedInOrOpen(channel, guildId)) return false;
        return NotifyLevel(channel) switch
        {
            0 => !IsVoice(channel.Type) || (Me is { } me && _guilds.TryGetValue(guildId, out var g) && g.Voice.GetValueOrDefault(me.Id)?.ChannelId == channel.Id) || mentioned,
            1 => mentioned,
            _ => false,
        };
    }

    private const int SuppressNotifications = 1 << 12;

    // An opt-in server (its "Show all channels" off) notifies only from the channels, or their categories, opted in.
    private bool OptedInOrOpen(Channel c, string guildId) =>
        !OptInMode(guildId) || OptedIn(c.Id) || (c.ParentId is { } p && OptedIn(p))
        || (c.Type is 10 or 11 or 12 && Find(c.ParentId ?? "") is { ParentId: { } category } && OptedIn(category));

    private Notification Notice(JsonElement d, Channel channel)
    {
        var who = d.TryGetProperty("author", out var a) ? ReadUser(a) : null;
        var author = who?.Name ?? "Someone";
        if (d.TryGetProperty("member", out var m) && Str(m, "nick") is { } nick) author = nick;
        var text = Str(d, "content") ?? "";
        foreach (var user in Array(d, "mentions"))
            if (Str(user, "id") is { } uid)
                text = text.Replace($"<@{uid}>", "@" + ReadUser(user).Name).Replace($"<@!{uid}>", "@" + ReadUser(user).Name);
        text = System.Text.RegularExpressions.Regex.Replace(text, "<a?:(\\w+):\\d+>", ":$1:");
        if (text.Length == 0) text = Array(d, "attachments").Any() ? "Sent an attachment" : Array(d, "sticker_items").Any() ? "Sent a sticker" : "Sent a message";
        if (text.Length > 180) text = text[..180] + "…";
        var guild = channel.GuildId is { } gid ? _guilds.GetValueOrDefault(gid) : null;
        var place = guild is not null ? "#" + channel.Name : channel.Type == 3 ? DmName(channel) : null;
        var authorId = who?.Id ?? "0";
        return new Notification(Str(d, "id") ?? "", channel.Id, channel.GuildId, authorId, author, Avatar(authorId), place, guild?.Name, text,
            channel.Type is 10 or 11 or 12 ? channel.ParentId : null);
    }

    // A DM or group by its name, else its people, as Discord calls it.
    private string DmName(Channel c) => c.Name ?? string.Join(", ", c.Recipients.Select(r => _users.GetValueOrDefault(r)?.Name ?? "Someone"));

    // A voice state, or null when the user left voice. Names come from the member, else the user cache.
    private VoiceMember? ReadVoice(JsonElement v)
    {
        if (Str(v, "channel_id") is not { } channel || Str(v, "user_id") is not { } user) return null;
        var name = _users.GetValueOrDefault(user)?.Name;
        if (v.TryGetProperty("member", out var m) && m.ValueKind == JsonValueKind.Object)
        {
            if (m.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object) { var known = ReadUser(u); Remember(known); name = known.Name; }
            if (Str(m, "nick") is { } nick) name = nick;
        }
        return new VoiceMember(channel, name ?? "Someone", Avatar(user),
            Bool(v, "self_mute") || Bool(v, "mute"), Bool(v, "self_deaf") || Bool(v, "deaf"), Bool(v, "self_video"), Bool(v, "self_stream"), Bool(v, "mute"), Bool(v, "deaf"));
    }

    /// <summary>The voice channel a user is in on a server (null: none).</summary>
    internal string? VoiceChannelOf(string guildId, string userId)
    {
        lock (_gate) return _guilds.GetValueOrDefault(guildId)?.Voice.GetValueOrDefault(userId)?.ChannelId;
    }

    /// <summary>Whether the user may join this voice channel.</summary>
    internal bool ChannelMuted(string channelId)
    {
        lock (_gate) return IsMuted(channelId);
    }

    /// <summary>The audio bitrate a voice channel asks for, in bits per second (64000 when it says none).</summary>
    internal int VoiceBitrate(string channelId)
    {
        lock (_gate) return Find(channelId) is { Bitrate: > 0 } c ? c.Bitrate : 64000;
    }

    /// <summary>Why the user can't join this voice channel or DM call (null: they can): no Connect, or full (Move Members still gets in, as in Discord).</summary>
    internal string? JoinRefusal(string channelId)
    {
        lock (_gate)
        {
            if (Find(channelId) is not { } c) return "You can't join this voice channel.";
            if (c.GuildId is null) return null;
            var perms = Perms(c);
            if (!IsVoice(c.Type) || (perms & Connect) == 0) return "You can't join this voice channel.";
            return Full(c, perms) ? "This voice channel is full." : null;
        }
    }

    /// <summary>A DM call began (CALL_CREATE, with who is in it) or ended (CALL_DELETE).</summary>
    internal void ApplyCall(string type, JsonElement d)
    {
        if (Str(d, "channel_id") is not { } channel) return;
        lock (_gate)
        {
            if (type == "CALL_DELETE") { _calls.Remove(channel); return; }
            if (type != "CALL_CREATE" || !d.TryGetProperty("voice_states", out _)) return;
            _calls[channel] = Array(d, "voice_states").Select(v => (Id: Str(v, "user_id"), Member: ReadVoice(v)))
                .Where(v => v.Id is not null && v.Member?.ChannelId == channel).ToDictionary(v => v.Id!, v => v.Member!);
        }
    }

    // Under _gate: the channel's user limit is reached by everyone else in it, and the user can't move members.
    private bool Full(Channel c, ulong perms) =>
        c.UserLimit > 0 && (perms & MoveMembers) == 0 && c.GuildId is { } g && _guilds.TryGetValue(g, out var guild)
        && guild.Voice.Count(v => v.Value.ChannelId == c.Id && v.Key != Me?.Id) >= c.UserLimit;

    private static List<Emoji> ReadEmojis(JsonElement g) =>
        [.. Array(g, "emojis").Select(e => new Emoji(Str(e, "id") ?? "", Str(e, "name") ?? "", Bool(e, "animated"),
            !e.TryGetProperty("available", out var av) || av.ValueKind != JsonValueKind.False)).Where(e => e.Id.Length > 0)];

    /// <summary>Friends, incoming and outgoing requests, with status and what each is doing.</summary>
    internal object Friends()
    {
        lock (_gate)
            return _relationships.Select(r =>
            {
                var user = _users.GetValueOrDefault(r.Key);
                var presence = _presence.GetValueOrDefault(r.Key);
                return new
                {
                    id = r.Key,
                    name = user?.Name ?? "Someone",
                    username = user?.Username,
                    avatar = Avatar(r.Key),
                    look = LookOf(r.Key),
                    kind = r.Value switch { 1 => "friend", 3 => "incoming", _ => "outgoing" },
                    status = presence?.Status ?? "offline",
                    activity = presence?.Activity,
                    activityType = presence?.Kind,
                    rich = presence?.Rich,
                    dmId = _private.Values.FirstOrDefault(c => c.Recipients.Count == 1 && c.Recipients[0] == r.Key)?.Id,
                };
            }).OrderBy(f => f.name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    internal string MyStatus
    {
        get { lock (_gate) return _myStatus; }
    }

    internal void SetMyStatus(string status)
    {
        lock (_gate) _myStatus = status;
    }

    /// <summary>Every channel of a server the user can see that is unread or mentions them (muted ones too), with its newest message, for marking it read.</summary>
    internal List<(string Channel, string Message)> UnreadIn(string guildId)
    {
        lock (_gate)
        {
            if (!_guilds.TryGetValue(guildId, out var guild)) return [];
            var unread = guild.Channels.Values.Where(c => (Perms(c) & ViewChannel) != 0 && (Unread(c) || _read.GetValueOrDefault(c.Id).Mentions > 0) && c.LastMessageId is not null)
                .Select(c => (c.Id, c.LastMessageId!)).ToList();
            foreach (var (channel, message) in unread) _read[channel] = (message, 0);
            return unread;
        }
    }

    /// <summary>
    /// The Inbox's Unreads: every unread channel the user can see in a server they haven't muted, the newest
    /// first, with its server, a thread's parent, where reading stopped and its mentions.
    /// </summary>
    internal List<object> Unreads(int limit)
    {
        lock (_gate)
            return [.. _guilds.Values.Where(g => !IsMuted(g.Id))
                .SelectMany(g => g.Channels.Values.Where(c => (Perms(c) & ViewChannel) != 0 && Unread(c)).Select(c => (Guild: g, Channel: c)))
                .OrderByDescending(x => Snowflake(x.Channel.LastMessageId!))
                .Take(limit)
                .Select(x => (object)new
                {
                    channelId = x.Channel.Id,
                    name = x.Channel.Name ?? "",
                    guildId = x.Guild.Id,
                    guild = x.Guild.Name,
                    icon = x.Guild.Icon is null ? null : $"https://cdn.discordapp.com/icons/{x.Guild.Id}/{x.Guild.Icon}.webp?size=64&quality=lossless",
                    parentId = x.Channel.Type is 10 or 11 or 12 ? x.Channel.ParentId : null,
                    lastRead = _read.GetValueOrDefault(x.Channel.Id).Last,
                    lastMessage = x.Channel.LastMessageId,
                    mentions = _read.GetValueOrDefault(x.Channel.Id).Mentions,
                })];
    }

    // Friends' presences, then each server's for the friends and DM people found there.
    private void ApplyMergedPresences(JsonElement merged)
    {
        foreach (var p in Array(merged, "friends")) ApplyPresence(p);
        foreach (var guildPresences in Array(merged, "guilds"))
            foreach (var p in guildPresences.ValueKind == JsonValueKind.Array ? guildPresences.EnumerateArray() : Enumerable.Empty<JsonElement>())
                if (Watched(PresenceUser(p))) ApplyPresence(p);
    }

    private static string? PresenceUser(JsonElement p) =>
        p.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object ? Str(u, "id") : Str(p, "user_id");

    // Presence is kept only for friends, requests and the people in DMs, not every member of every server.
    private bool Watched(string? id) =>
        id is not null && (_relationships.ContainsKey(id) || _private.Values.Any(c => c.Recipients.Count == 1 && c.Recipients[0] == id));

    // A friend's or DM person's presence; returns their id when it changed.
    private string? ApplyPresence(JsonElement p)
    {
        var id = PresenceUser(p);
        if (id is null) return null;
        _presence[id] = ReadPresence(p);
        return id;
    }

    private static Presence ReadPresence(JsonElement p)
    {
        var activities = Array(p, "activities").ToList();
        var custom = activities.Where(a => Int(a, "type") == 4).Select(a => string.Join(' ', new[]
        {
            a.TryGetProperty("emoji", out var em) && em.ValueKind == JsonValueKind.Object && Str(em, "id") is null ? Str(em, "name") : null,
            Str(a, "state"),
        }.Where(x => !string.IsNullOrEmpty(x)))).FirstOrDefault(x => x.Length > 0);
        var rich = activities.Where(a => Int(a, "type") is >= 0 and <= 5 and not 4 && Str(a, "name") is not null).Select(ReadRich).FirstOrDefault();
        // Discord's line: the custom status, else Spotify's artist, else the activity's name.
        var line = custom ?? (rich is null ? null : rich.Type == 2 && rich.State is { Length: > 0 } artist ? artist : rich.Name);
        return new Presence(Str(p, "status") ?? "offline", line, custom is not null ? 4 : rich?.Type, rich);
    }

    /// <summary>
    /// What a profile shows beyond Discord's profile reply: the member's roles on this server, highest first with
    /// their colors, and what they're doing now (a friend's, or the member list's). The reply's server member is
    /// remembered, so the person menu knows their roles and rank without waiting on a member request.
    /// </summary>
    internal object ProfileOn(string userId, string? guildId, JsonElement? profile)
    {
        lock (_gate)
        {
            var guild = guildId is null ? null : _guilds.GetValueOrDefault(guildId);
            if (guild is not null && profile is { ValueKind: JsonValueKind.Object } body && body.TryGetProperty("guild_member", out var member) && member.ValueKind == JsonValueKind.Object)
                RememberMember(guild, member);
            var roles = guild is not null && guild.Members.TryGetValue(userId, out var known)
                ? known.Roles.Select(id => (Id: id, Style: guild.Styles.GetValueOrDefault(id))).Where(r => r.Style is not null && r.Id != guild.Id)
                    .OrderByDescending(r => r.Style!.Position)
                    .Select(r => new { id = r.Id, name = r.Style!.Name, color = r.Style.Color > 0 ? DiscordColors.Readable(r.Style.Color) : null }).ToArray()
                : [];
            var presence = _presence.GetValueOrDefault(userId) ?? ListedPresence(guild, userId);
            return new
            {
                roles,
                presence = presence is null ? null : new { status = presence.Status, activity = presence.Activity, activityType = presence.Kind, rich = presence.Rich },
                // Discord's relationship with you: 0 none, 1 friends, 2 blocked, 3 their request, 4 yours (the profile's friend button).
                relationship = _blocked.Contains(userId) ? 2 : _relationships.GetValueOrDefault(userId),
            };
        }
    }

    // A member's presence as the server's member list last sent it.
    private static Presence? ListedPresence(Guild? guild, string userId)
    {
        foreach (var item in guild?.List ?? [])
            if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("member", out var m) && m.TryGetProperty("user", out var u) && Str(u, "id") == userId)
                return m.TryGetProperty("presence", out var p) && p.ValueKind == JsonValueKind.Object ? ReadPresence(p) : null;
        return null;
    }

    private static Rich ReadRich(JsonElement a)
    {
        long? start = a.TryGetProperty("timestamps", out var ts) && ts.ValueKind == JsonValueKind.Object && ts.TryGetProperty("start", out var st) && st.TryGetInt64(out var ms) ? ms : null;
        var large = a.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Object ? Str(assets, "large_image") : null;
        // Spotify art, Discord's media proxy, or the application's own asset.
        var image = large switch
        {
            null => null,
            _ when large.StartsWith("spotify:", StringComparison.Ordinal) => "https://i.scdn.co/image/" + large[8..],
            _ when large.StartsWith("mp:", StringComparison.Ordinal) => "https://media.discordapp.net/" + large[3..],
            _ when Str(a, "application_id") is { } app => $"https://cdn.discordapp.com/app-assets/{app}/{large}.png",
            _ => null,
        };
        return new Rich(Int(a, "type"), Str(a, "name") ?? "", Str(a, "details"), Str(a, "state"), start, image);
    }

    private object PresenceEvent(string userId)
    {
        var p = _presence[userId];
        return new { userId, status = p.Status, activity = p.Activity, activityType = p.Kind, rich = p.Rich };
    }

    /// <summary>The member list Discord sent for the open channel: role groups with counts, then members with their color and status.</summary>
    internal object MemberList(string guildId)
    {
        lock (_gate)
        {
            if (!_guilds.TryGetValue(guildId, out var guild)) return new { items = System.Array.Empty<object>(), online = 0, total = 0 };
            var items = new List<object>();
            foreach (var item in guild.List)
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (item.TryGetProperty("group", out var group))
                {
                    var id = Str(group, "id") ?? "";
                    var name = id switch { "online" => "Online", "offline" => "Offline", _ => guild.Styles.GetValueOrDefault(id)?.Name ?? "Members" };
                    items.Add(new { group = name, count = guild.GroupCounts.GetValueOrDefault(id, Int(group, "count")) });
                }
                else if (item.TryGetProperty("member", out var member) && member.TryGetProperty("user", out var u))
                {
                    var user = ReadUser(u);
                    var roles = Array(member, "roles").Select(Id).OfType<string>().ToList();
                    var presence = member.TryGetProperty("presence", out var p) ? p : default;
                    items.Add(new
                    {
                        id = user.Id,
                        name = Str(member, "nick") ?? user.Name,
                        avatar = user.Avatar is { } hash ? $"https://cdn.discordapp.com/avatars/{user.Id}/{hash}.webp?size=256&quality=lossless" : Avatar(user.Id),
                        color = ColorOf(guild, roles),
                        look = LookOf(user.Id, ReadLook(member)),
                        bot = Bool(u, "bot"),
                        status = presence.ValueKind == JsonValueKind.Object ? Str(presence, "status") ?? "offline" : "offline",
                        activity = presence.ValueKind == JsonValueKind.Object ? Array(presence, "activities").Select(a => Int(a, "type") == 4 ? Str(a, "state") : Str(a, "name")).FirstOrDefault(a => a is not null) : null,
                    });
                }
            }
            // Every row Discord's list has (each group and its members), so the room knows to ask for more as it scrolls.
            var rows = guild.GroupCounts.Count + guild.GroupCounts.Values.Sum();
            return new { items, online = guild.OnlineCount, total = guild.MemberCount, rows = Math.Max(rows, guild.List.Count) };
        }
    }

    /// <summary>How many rows of a server's member list Discord has sent so far (where the next range starts).</summary>
    internal int MemberRows(string guildId)
    {
        lock (_gate) return _guilds.TryGetValue(guildId, out var guild) ? guild.List.Count : 0;
    }

    // An unanswered ask (the reply lost to a reconnect) may go again after this long.
    internal const long AskAgainMs = 60_000;

    /// <summary>
    /// Name colors for these users (their highest colored role), and the ids to ask Discord for: not seen as
    /// members, not asked in the last minute, and not ones Discord already said aren't members. Those are
    /// counted as asked now (<paramref name="now"/> is Environment.TickCount64).
    /// </summary>
    internal (Dictionary<string, string?> Colors, List<string> Unknown) Colors(string guildId, IEnumerable<string> userIds, long now)
    {
        lock (_gate)
        {
            var colors = new Dictionary<string, string?>();
            var unknown = new List<string>();
            if (!_guilds.TryGetValue(guildId, out var guild)) return (colors, unknown);
            foreach (var id in userIds.Distinct())
            {
                if (guild.Members.TryGetValue(id, out var member)) colors[id] = ColorOf(guild, member.Roles);
                else if (!guild.NotFound.Contains(id) && (!guild.Asked.TryGetValue(id, out var at) || now - at >= AskAgainMs))
                {
                    guild.Asked[id] = now;
                    unknown.Add(id);
                }
            }
            return (colors, unknown);
        }
    }

    private static string? ColorOf(Guild guild, IEnumerable<string> roles)
    {
        var top = roles.Select(r => guild.Styles.GetValueOrDefault(r)).Where(r => r is { Color: > 0 }).MaxBy(r => r!.Position);
        return top is null ? null : DiscordColors.Readable(top.Color);
    }

    private static RoleStyle ReadStyle(JsonElement r) =>
        new(Int(r, "color"), Int(r, "position"), Str(r, "name") ?? "", Bool(r, "hoist"), Bool(r, "managed"));

    private void RememberMember(Guild guild, JsonElement m)
    {
        if (!m.TryGetProperty("user", out var u) || Str(u, "id") is not { } id) return;
        Remember(ReadUser(u));
        guild.Members[id] = (Array(m, "roles").Select(Id).OfType<string>().ToList(), Str(m, "nick"));
        // The user's own member, wherever it arrives: a server Discord sends after READY carries no roles of theirs,
        // and without them they'd have @everyone's permissions only (no role channels, no moderator actions).
        if (id == Me?.Id) guild.MyRolesKnown = true;
        if (id == Me?.Id && !guild.MyRoles.SetEquals(guild.Members[id].Roles))
        {
            guild.MyRoles = [.. guild.Members[id].Roles];
            _myRolesMoved.Add(guild.Id);
        }
        if (DateTimeOffset.TryParse(Str(m, "communication_disabled_until"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var until)) guild.TimedOut[id] = until;
        else guild.TimedOut.Remove(id);
    }

    // Discord's member list operations on the list: SYNC a range, INSERT, UPDATE, DELETE, INVALIDATE. The list keeps
    // each row at Discord's index (rows not sent yet are empty); a range no longer subscribed keeps what it showed.
    private void ApplyMemberList(Guild guild, JsonElement d)
    {
        // Another channel's list replaces the one shown when it arrives (the shown one stays until then, as in Discord's
        // own client); channels that show the same people share a list, which then stays as it is.
        if (Str(d, "id") is var listId && listId != guild.ListId)
        {
            guild.List.Clear();
            guild.ListId = listId;
        }
        guild.OnlineCount = Int(d, "online_count");
        if (d.TryGetProperty("groups", out _))
            guild.GroupCounts = Array(d, "groups").Where(g => Str(g, "id") is not null).ToDictionary(g => Str(g, "id")!, g => Int(g, "count"));
        guild.MemberCount = Int(d, "member_count");
        // The rows this update brought: their members are remembered (the rest were when they came).
        var arrived = new List<JsonElement>();
        foreach (var op in Array(d, "ops"))
        {
            var index = Int(op, "index");
            switch (Str(op, "op"))
            {
                case "SYNC":
                    var start = op.GetProperty("range")[0].GetInt32();
                    var items = Array(op, "items").Select(i => i.Clone()).ToList();
                    while (guild.List.Count < start + items.Count) guild.List.Add(default);
                    for (var i = 0; i < items.Count; i++) guild.List[start + i] = items[i];
                    arrived.AddRange(items);
                    break;
                case "INSERT":
                    var inserted = op.GetProperty("item").Clone();
                    guild.List.Insert(Math.Min(index, guild.List.Count), inserted);
                    arrived.Add(inserted);
                    break;
                case "UPDATE":
                    if (index >= guild.List.Count) break;
                    guild.List[index] = op.GetProperty("item").Clone();
                    arrived.Add(guild.List[index]);
                    break;
                case "DELETE":
                    if (index < guild.List.Count) guild.List.RemoveAt(index);
                    break;
                case "INVALIDATE":
                    break;
            }
        }
        foreach (var item in arrived)
            if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("member", out var member)) RememberMember(guild, member);
    }

    /// <summary>
    /// Stickers as Discord's picker groups them: this server's first, then Discord's own packs (anyone
    /// can send those), then every other server's, each with its name. Another server's can be sent with
    /// Nitro only (usable false shows them locked, as Discord does).
    /// </summary>
    internal object Stickers(string? guildId, IReadOnlyList<object> packs, List<string>? favorites)
    {
        lock (_gate)
        {
            object Group(Guild g) => new
            {
                id = g.Id,
                name = g.Name,
                icon = g.Icon is null ? null : $"https://cdn.discordapp.com/icons/{g.Id}/{g.Icon}.webp?size=64&quality=lossless",
                usable = g.Id == guildId || Premium,
                stickers = g.Stickers.Select(st => new { id = st.Id, name = st.Name, format = st.Format }),
            };
            var here = guildId is null ? null : _guilds.GetValueOrDefault(guildId);
            var mine = here is { Stickers.Count: > 0 } ? new[] { Group(here) } : [];
            var others = _guilds.Values.Where(g => g.Id != guildId && g.Stickers.Count > 0).Select(Group);
            return new { premium = Premium, groups = mine.Concat(packs).Concat(others).ToArray(), favorites };
        }
    }

    /// <summary>Threads Discord sent (a forum's posts, a channel's threads): kept so they open like channels, with their parent's permissions.</summary>
    internal void AddThreads(string guildId, JsonElement threads)
    {
        lock (_gate)
        {
            if (!_guilds.TryGetValue(guildId, out var guild)) return;
            foreach (var t in threads.ValueKind == JsonValueKind.Array ? threads.EnumerateArray() : Enumerable.Empty<JsonElement>())
            {
                var thread = ReadChannel(t, guildId);
                if (thread.Type is 10 or 11 or 12 && thread.ParentId is not null) guild.Channels[thread.Id] = thread;
            }
        }
    }

    // ---- permissions ----

    internal List<string> RecipientsOf(string channelId)
    {
        lock (_gate) return _private.TryGetValue(channelId, out var dm) ? [.. dm.Recipients] : [];
    }

    /// <summary>Where a message is, for search, pins and the Inbox: its channel's name, server, and a thread's parent.</summary>
    internal object? PlaceOf(string channelId)
    {
        lock (_gate)
        {
            if (Find(channelId) is not { } c || (c.GuildId is not null && (Perms(c) & ViewChannel) == 0)) return null;
            var guild = c.GuildId is { } g ? _guilds.GetValueOrDefault(g) : null;
            var name = DmName(c);
            return new { name, guildId = c.GuildId, guild = guild?.Name, parentId = c.Type is 10 or 11 or 12 ? c.ParentId : null };
        }
    }

    /// <summary>
    /// People in a server whose name, nickname or username starts with (or has) the text, best first, for
    /// the composer's @ list: from the members Exo has seen (messages, the member list, Discord's replies).
    /// </summary>
    internal List<object> FindMembers(string guildId, string query, int limit)
    {
        lock (_gate)
        {
            if (!_guilds.TryGetValue(guildId, out var guild) || query.Length == 0) return [];
            var q = query.ToLowerInvariant();
            return guild.Members
                .Select(m => (Id: m.Key, Nick: m.Value.Nick, User: _users.GetValueOrDefault(m.Key)))
                .Where(m => m.User is not null)
                .Select(m => (m.Id, m.Nick, User: m.User!, Names: new[] { m.Nick, m.User!.Name, m.User!.Username }.OfType<string>().Select(n => n.ToLowerInvariant()).ToArray()))
                .Select(m => (m.Id, m.Nick, m.User, Rank: m.Names.Any(n => n.StartsWith(q, StringComparison.Ordinal)) ? 0 : m.Names.Any(n => n.Contains(q, StringComparison.Ordinal)) ? 1 : 2))
                .Where(m => m.Rank < 2)
                .OrderBy(m => m.Rank).ThenBy(m => m.Nick ?? m.User.Name, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .Select(m => (object)new { id = m.Id, username = m.User.Username ?? m.User.Name, global_name = m.Nick ?? m.User.Name, avatar = m.User.Avatar })
                .ToList();
        }
    }

    /// <summary>A visible forum's tags, for its post list.</summary>
    internal object[] ForumTags(string channelId)
    {
        lock (_gate)
            return Visible(channelId) && Find(channelId) is { } c
                ? [.. c.Tags.Select(t => (object)new { id = t.Id, name = t.Name, emojiId = t.EmojiId, emojiName = t.EmojiName })]
                : [];
    }

    internal string? ChannelName(string channelId)
    {
        lock (_gate) return Find(channelId)?.Name;
    }

    internal string? LastMessageOf(string channelId)
    {
        lock (_gate) return Find(channelId)?.LastMessageId;
    }

    /// <summary>A channel's Discord type (2 voice, 13 stage), or null when Exo doesn't know it.</summary>
    internal int? ChannelType(string channelId)
    {
        lock (_gate) return Find(channelId)?.Type;
    }

    internal bool CanSee(string channelId)
    {
        lock (_gate) return Visible(channelId);
    }

    private bool Visible(string channelId) =>
        Find(channelId) is { } c && (c.GuildId is null || (Perms(c) & ViewChannel) != 0);

    /// <summary>Servers whose roles for the user haven't arrived yet (an owner needs none): the session asks for them.</summary>
    internal IReadOnlyList<string> GuildsWithoutMyRoles()
    {
        lock (_gate) return [.. _guilds.Values.Where(g => !g.MyRolesKnown && g.OwnerId != Me?.Id).Select(g => g.Id)];
    }

    private ulong Perms(Channel channel)
    {
        if (channel.GuildId is null || !_guilds.TryGetValue(channel.GuildId, out var guild) || Me is null) return All;
        if (guild.OwnerId == Me.Id) return All;
        var perms = guild.Roles.GetValueOrDefault(guild.Id);
        foreach (var role in guild.MyRoles) perms |= guild.Roles.GetValueOrDefault(role);
        if ((perms & Administrator) != 0) return All;
        // A thread or a channel without its own overwrites follows its parent.
        var source = channel;
        if (channel.Type is 10 or 11 or 12 && channel.ParentId is { } parent && guild.Channels.TryGetValue(parent, out var p)) source = p;
        if (source.Overwrites.FirstOrDefault(o => !o.Member && o.Id == guild.Id) is { } everyone)
            perms = (perms & ~everyone.Deny) | everyone.Allow;
        ulong allow = 0, deny = 0;
        foreach (var o in source.Overwrites.Where(o => !o.Member && guild.MyRoles.Contains(o.Id)))
        {
            allow |= o.Allow;
            deny |= o.Deny;
        }
        perms = (perms & ~deny) | allow;
        if (source.Overwrites.FirstOrDefault(o => o.Member && o.Id == Me.Id) is { } member)
            perms = (perms & ~member.Deny) | member.Allow;
        // A voice channel's text chat needs Connect too (Discord answers Missing Access without it).
        if (IsVoice(channel.Type) && (perms & Connect) == 0) perms &= ~(SendMessages | ReadHistory);
        return (perms & ViewChannel) == 0 ? 0 : perms;
    }

    // What @everyone alone may do in the channel: a private channel is one it can't view.
    private static ulong EveryonePerms(Guild guild, Channel channel)
    {
        var perms = guild.Roles.GetValueOrDefault(guild.Id);
        if ((perms & Administrator) != 0) return All;
        var source = channel.Type is 10 or 11 or 12 && channel.ParentId is { } parent && guild.Channels.TryGetValue(parent, out var p) ? p : channel;
        if (source.Overwrites.FirstOrDefault(o => !o.Member && o.Id == guild.Id) is { } everyone)
            perms = (perms & ~everyone.Deny) | everyone.Allow;
        return perms;
    }

    // As Discord counts a mention: the user; @everyone and @here unless the server suppresses them; one of their roles unless it suppresses those.
    private bool MentionsMe(JsonElement d, Channel channel)
    {
        if (channel.GuildId is null) return true;
        if (Array(d, "mentions").Any(m => Str(m, "id") == Me?.Id)) return true;
        if (Bool(d, "mention_everyone") && !_suppressEveryone.Contains(channel.GuildId)) return true;
        if (_suppressRoles.Contains(channel.GuildId)) return false;
        var mine = _guilds.GetValueOrDefault(channel.GuildId)?.MyRoles;
        return mine is not null && Array(d, "mention_roles").Select(Id).Any(r => r is not null && mine.Contains(r));
    }

    // A thread counts once the user has read in it (joined), as in Discord; other active threads don't light up the server.
    private bool Unread(Channel c) =>
        !IsVoice(c.Type) && c.Type != 4 && !MutedChannel(c) && c.LastMessageId is { } last &&
        (c.Type is not (10 or 11 or 12) || _read.ContainsKey(c.Id)) &&
        Snowflake(last) > ReadMark(c) && (c.Type is 10 or 11 or 12 || Followed(c, Snowflake(last)));

    // Where the user has read to; a server channel never read counts as read up to when they joined (Discord's getAckTimestamp).
    private ulong ReadMark(Channel c) =>
        _read.GetValueOrDefault(c.Id).Last is { } read ? Snowflake(read)
        : c.GuildId is { } guildId && _guilds.TryGetValue(guildId, out var guild) ? guild.JoinedAt : 0;

    // An opt-in server lights up only for channels its list shows (followed, or private), read in the last three days, or
    // with a mention, and for none quiet for a week, as Discord's canBeUnread.
    private bool Followed(Channel c, ulong last)
    {
        if (c.GuildId is not { } guildId || !OptInMode(guildId)) return true;
        if (last < SnowflakeAt(DateTimeOffset.UtcNow.AddDays(-7))) return false;
        if (OptedIn(c.Id) || (c.ParentId is { } p && OptedIn(p)) || _read.GetValueOrDefault(c.Id).Mentions > 0) return true;
        if (_read.GetValueOrDefault(c.Id).Last is { } read && Snowflake(read) > SnowflakeAt(DateTimeOffset.UtcNow.AddDays(-3))) return true;
        return _guilds.TryGetValue(guildId, out var guild) && (EveryonePerms(guild, c) & ViewChannel) == 0;
    }

    // Discord's opt-in mode: the user's "Show all channels" off in a community server.
    private bool OptInMode(string guildId) =>
        (_guildFlags.GetValueOrDefault(guildId) & OptInChannelsOn) != 0 && _guilds.TryGetValue(guildId, out var guild) && guild.Features.Contains("COMMUNITY");

    // Discord's resolved notification setting: the channel's, else its category's, else the server's, else the server's default.
    // A thread notifies of mentions at most; a DM of every message unless set otherwise.
    private int NotifyLevel(Channel c)
    {
        if (c.GuildId is not { } guildId || !_guilds.TryGetValue(guildId, out var guild)) return _notify.GetValueOrDefault(c.Id, 0);
        var thread = c.Type is 10 or 11 or 12;
        var source = thread && c.ParentId is { } p && guild.Channels.TryGetValue(p, out var parent) ? parent : c;
        var level = _notify.TryGetValue(source.Id, out var own) ? own : InheritedNotify(source, guild);
        return thread ? Math.Max(1, level) : level;
    }

    // What a channel without its own setting follows: its category's, else the server's, else the server's default.
    private int InheritedNotify(Channel c, Guild guild) =>
        c.ParentId is { } category && _notify.TryGetValue(category, out var folder) ? folder
        : _notify.TryGetValue(guild.Id, out var server) ? server : guild.DefaultNotifications;

    // 3 (or none) is Discord's "as the level above".
    private void SetNotify(string id, JsonElement entry)
    {
        if (entry.TryGetProperty("message_notifications", out var n) && n.ValueKind == JsonValueKind.Number && n.GetInt32() is >= 0 and <= 2 and var level) _notify[id] = level;
        else _notify.Remove(id);
    }

    // A channel muted itself or by its category; a thread also by its channel's category.
    private bool MutedChannel(Channel c) => IsMuted(c.Id) || (c.ParentId is { } p && (IsMuted(p)
        || (c.Type is 10 or 11 or 12 && Find(p) is { ParentId: { } category } && IsMuted(category))));

    private bool IsMuted(string id) => _muted.Contains(id) && (!_muteEnds.TryGetValue(id, out var end) || end > DateTimeOffset.UtcNow);

    // A server's or channel's mute from its settings entry: muted, and until when (no end: until turned back on).
    private void ReadMute(string id, JsonElement entry)
    {
        if (Bool(entry, "muted")) _muted.Add(id); else _muted.Remove(id);
        if (entry.TryGetProperty("mute_config", out var config) && config.ValueKind == JsonValueKind.Object &&
            DateTimeOffset.TryParse(Str(config, "end_time"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var end))
            _muteEnds[id] = end;
        else _muteEnds.Remove(id);
    }

    private Channel? Find(string channelId)
    {
        if (_private.TryGetValue(channelId, out var dm)) return dm;
        foreach (var guild in _guilds.Values)
            if (guild.Channels.TryGetValue(channelId, out var channel)) return channel;
        return null;
    }

    private static bool IsVoice(int type) => type is 2 or 13;
    private static bool IsListed(int type) => type is 0 or 2 or 5 or 13 or 15 or 16;

    // ---- reading Discord's objects ----

    private Guild ReadGuild(JsonElement g, JsonElement? mine, Guild? existing = null)
    {
        var props = g.TryGetProperty("properties", out var p) && p.ValueKind == JsonValueKind.Object ? p : g;
        var guild = new Guild
        {
            Id = Str(g, "id") ?? "",
            Name = Str(props, "name") ?? existing?.Name ?? "",
            Icon = Str(props, "icon") ?? (props.TryGetProperty("icon", out _) ? null : existing?.Icon),
            OwnerId = Str(props, "owner_id") ?? existing?.OwnerId,
            RulesChannelId = props.TryGetProperty("rules_channel_id", out _) ? Str(props, "rules_channel_id") : existing?.RulesChannelId,
            AfkChannelId = props.TryGetProperty("afk_channel_id", out _) ? Str(props, "afk_channel_id") : existing?.AfkChannelId,
            Features = props.TryGetProperty("features", out _) ? [.. Array(props, "features").Select(f => f.GetString()).OfType<string>()] : existing?.Features ?? [],
            DefaultNotifications = props.TryGetProperty("default_message_notifications", out var dn) && dn.ValueKind == JsonValueKind.Number ? dn.GetInt32() : existing?.DefaultNotifications ?? 0,
            JoinedAt = DateTimeOffset.TryParse(Str(g, "joined_at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var joined)
                ? SnowflakeAt(joined) : existing?.JoinedAt ?? 0,
            MyRoles = existing?.MyRoles ?? [],
            MyRolesKnown = existing?.MyRolesKnown ?? false,
        };
        guild.Roles = g.TryGetProperty("roles", out _)
            ? Array(g, "roles").ToDictionary(r => Str(r, "id") ?? "", r => Perms(r, "permissions"))
            : existing?.Roles ?? [];
        guild.Styles = g.TryGetProperty("roles", out _)
            ? Array(g, "roles").ToDictionary(r => Str(r, "id") ?? "", ReadStyle)
            : existing?.Styles ?? [];
        guild.Members = existing?.Members ?? [];
        // GUILD_UPDATE carries no member list; the subscribed one stays until Discord's next SYNC.
        guild.List = existing?.List ?? [];
        guild.ListId = existing?.ListId;
        guild.GroupCounts = existing?.GroupCounts ?? [];
        guild.OnlineCount = existing?.OnlineCount ?? 0;
        guild.MemberCount = existing?.MemberCount ?? 0;
        guild.TimedOut = existing?.TimedOut ?? [];
        guild.Asked = existing?.Asked ?? [];
        guild.NotFound = existing?.NotFound ?? [];
        guild.Events = g.TryGetProperty("guild_scheduled_events", out _)
            ? [.. Array(g, "guild_scheduled_events").Where(e => Int(e, "status") is 1 or 2).Select(e => Str(e, "id")).OfType<string>()]
            : existing?.Events ?? [];
        guild.Emojis = g.TryGetProperty("emojis", out _) ? ReadEmojis(g) : existing?.Emojis ?? [];
        guild.Stickers = g.TryGetProperty("stickers", out _)
            ? [.. Array(g, "stickers").Where(st => !st.TryGetProperty("available", out var av) || av.ValueKind != JsonValueKind.False)
                .Select(st => (Str(st, "id") ?? "", Str(st, "name") ?? "", Int(st, "format_type")))]
            : existing?.Stickers ?? [];
        guild.Voice = g.TryGetProperty("voice_states", out _)
            ? Array(g, "voice_states").Select(v => (Id: Str(v, "user_id"), Member: ReadVoice(v))).Where(v => v.Id is not null && v.Member is not null).ToDictionary(v => v.Id!, v => v.Member!)
            : existing?.Voice ?? [];
        guild.Channels = g.TryGetProperty("channels", out _)
            ? Array(g, "channels").Select(c => ReadChannel(c, guild.Id)).ToDictionary(c => c.Id)
            : existing?.Channels ?? [];
        // The active threads Discord sends with the server: their messages arrive and notify like a channel's.
        foreach (var t in Array(g, "threads").Select(t => ReadChannel(t, guild.Id)).Where(t => t.Type is 10 or 11 or 12 && t.ParentId is not null))
            guild.Channels.TryAdd(t.Id, t);
        var member = mine ?? Array(g, "members").FirstOrDefault(m =>
            (m.TryGetProperty("user", out var u) ? Str(u, "id") : Str(m, "user_id")) == Me?.Id);
        if (member is { ValueKind: JsonValueKind.Object } own)
        {
            guild.MyRoles = [.. Array(own, "roles").Select(Id).OfType<string>()];
            guild.MyRolesKnown = true;
        }
        return guild;
    }

    private Channel ReadChannel(JsonElement c, string? guildId)
    {
        var channel = new Channel
        {
            Id = Str(c, "id") ?? "",
            GuildId = guildId ?? Str(c, "guild_id"),
            Type = Int(c, "type"),
            Name = Str(c, "name"),
            ParentId = Str(c, "parent_id"),
            Position = Int(c, "position"),
            Topic = Str(c, "topic"),
            LastMessageId = Str(c, "last_message_id"),
            Icon = Str(c, "icon"),
            Flags = Int(c, "flags"),
            Bitrate = Int(c, "bitrate"),
            Nsfw = Bool(c, "nsfw"),
            RateLimit = Int(c, "rate_limit_per_user"),
            UserLimit = Int(c, "user_limit"),
            Locked = c.TryGetProperty("thread_metadata", out var meta) && Bool(meta, "locked"),
            MessageRequest = Bool(c, "is_message_request"),
            Tags = [.. Array(c, "available_tags").Select(t => (Str(t, "id") ?? "", Str(t, "name") ?? "", Str(t, "emoji_id"), Str(t, "emoji_name")))],
        };
        foreach (var o in Array(c, "permission_overwrites"))
        {
            var t = o.GetProperty("type");
            var member = t.ValueKind == JsonValueKind.Number ? t.GetInt32() == 1 : t.GetString() == "member";
            channel.Overwrites.Add(new Overwrite(Str(o, "id") ?? "", member, Perms(o, "allow"), Perms(o, "deny")));
        }
        channel.Recipients = [.. Array(c, "recipient_ids").Select(Id).OfType<string>()];
        foreach (var r in Array(c, "recipients"))
        {
            var user = ReadUser(r);
            Remember(user);
            channel.Recipients.Add(user.Id);
        }
        return channel;
    }

    private void ApplyGuildSettings(JsonElement entry)
    {
        var guildId = Str(entry, "guild_id");
        if (guildId is not null)
        {
            ReadMute(guildId, entry);
            if (entry.TryGetProperty("flags", out _)) _guildFlags[guildId] = Int(entry, "flags");
            SetNotify(guildId, entry);
            if (Bool(entry, "suppress_everyone")) _suppressEveryone.Add(guildId); else _suppressEveryone.Remove(guildId);
            if (Bool(entry, "suppress_roles")) _suppressRoles.Add(guildId); else _suppressRoles.Remove(guildId);
            // The entry carries all of the server's overrides: a channel left out is no longer muted or collapsed.
            if (entry.TryGetProperty("channel_overrides", out _) && _guilds.TryGetValue(guildId, out var guild))
                foreach (var channelId in guild.Channels.Keys)
                {
                    _muted.Remove(channelId);
                    _muteEnds.Remove(channelId);
                    _collapsed.Remove(channelId);
                    _flags.Remove(channelId);
                    _notify.Remove(channelId);
                }
        }
        foreach (var o in Array(entry, "channel_overrides"))
            if (Str(o, "channel_id") is { } id)
            {
                ReadMute(id, o);
                if (Bool(o, "collapsed")) _collapsed.Add(id); else _collapsed.Remove(id);
                _flags[id] = Int(o, "flags");
                SetNotify(id, o);
            }
    }

    private string TypingName(JsonElement d, string userId)
    {
        if (d.TryGetProperty("member", out var m) && m.ValueKind == JsonValueKind.Object)
        {
            if (Str(m, "nick") is { } nick) return nick;
            if (m.TryGetProperty("user", out var u)) return ReadUser(u).Name;
        }
        return _users.GetValueOrDefault(userId)?.Name ?? "Someone";
    }

    private void Remember(User user)
    {
        if (user.Id.Length == 0) return;
        // A payload without collectibles (a trimmed user) keeps the ones known.
        _users[user.Id] = user.Look is null && _users.GetValueOrDefault(user.Id)?.Look is { } known ? user with { Look = known } : user;
    }

    private string? Avatar(string userId) =>
        _users.GetValueOrDefault(userId) is { Avatar: { } hash } ? $"https://cdn.discordapp.com/avatars/{userId}/{hash}.webp?size=256&quality=lossless"
        : userId == Me?.Id && Me.Avatar is { } mine ? $"https://cdn.discordapp.com/avatars/{userId}/{mine}.webp?size=256&quality=lossless"
        : $"https://cdn.discordapp.com/embed/avatars/{(Snowflake(userId) >> 22) % 6}.png";

    private static User ReadUser(JsonElement u) =>
        new(Str(u, "id") ?? "", Str(u, "global_name") ?? Str(u, "username") ?? "Someone", Str(u, "avatar"), Str(u, "username"), ReadLook(u), Bool(u, "bot"));

    private static IEnumerable<JsonElement> Array(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray() : [];

    private static IEnumerable<JsonElement> Entries(JsonElement d, string name)
    {
        if (!d.TryGetProperty(name, out var value)) return [];
        return value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : Array(value, "entries");
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number => v.GetRawText(), _ => null }
            : null;

    private static string? Id(JsonElement e) =>
        e.ValueKind switch { JsonValueKind.String => e.GetString(), JsonValueKind.Number => e.GetRawText(), _ => null };

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;

    private static bool Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static ulong Perms(JsonElement e, string name) =>
        ulong.TryParse(Str(e, name), NumberStyles.None, CultureInfo.InvariantCulture, out var p) ? p : 0;

    private static ulong Snowflake(string id) =>
        ulong.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : 0;

    // The smallest snowflake of a moment (Discord's epoch is 2015-01-01).
    private static ulong SnowflakeAt(DateTimeOffset at) =>
        at.ToUnixTimeMilliseconds() is var ms and > 1420070400000L ? (ulong)(ms - 1420070400000L) << 22 : 0;
}
