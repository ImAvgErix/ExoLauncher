using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services.DiscordClient;

/// <summary>
/// Discord's HTTP API (discord.com/api/v9) as Discord's own web client calls it: the same
/// User-Agent, X-Super-Properties and locale headers Chrome on Windows sends, and the token
/// as Authorization. Only the calls a person makes by using the client exist here (read a
/// channel, send, edit or delete their own message, mark read, typing); nothing runs on a
/// timer or for anyone else. Discord asking for a captcha is passed on as a plain line and
/// never answered for the user. Message text is never logged.
/// </summary>
internal sealed partial class DiscordRest : IDisposable
{
    internal const string Api = "https://discord.com/api/v9/";
    // Discord's web client build when this was written (October 2026); replaced by the live one
    // read from discord.com's own scripts at start.
    private const int FallbackBuild = 641004;
    private static int? _build;

    // As Chrome talks to Discord: HTTP/2 on one warm connection (no new TLS handshake per call or after a minute idle)
    // and compressed answers (a page of messages is several times smaller). The connection is pinged while in use, so
    // one that died quietly (a network change, a sleep) is dropped within seconds instead of holding every call until
    // its 30 s timeout (profiles, channels and messages all hung together for up to 30 s, then landed at once).
    private readonly HttpClient _http = ExoHttp.Create(TimeSpan.FromSeconds(30), new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
        EnableMultipleHttp2Connections = true,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        KeepAlivePingDelay = TimeSpan.FromSeconds(10),
        KeepAlivePingTimeout = TimeSpan.FromSeconds(5),
        KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
    });
    private readonly string? _token;

    internal DiscordRest(string? token)
    {
        _token = token;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _http.DefaultRequestHeaders.Add("Origin", "https://discord.com");
        _http.DefaultRequestHeaders.Referrer = new Uri("https://discord.com/channels/@me");
        _http.DefaultRequestHeaders.Add("X-Discord-Locale", "en-US");
        _http.DefaultRequestHeaders.Add("X-Discord-Timezone", TimeZoneName());
        _http.DefaultRequestHeaders.Add("X-Super-Properties", SuperProperties());
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
    }

    internal static string UserAgent { get; } =
        $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{ChromeMajor()}.0.0.0 Safari/537.36";

    internal static int Build => _build ?? FallbackBuild;

    /// <summary>The properties Discord's web client identifies with, on the gateway and (base64) on every request.</summary>
    internal static JsonObject Properties() => new()
    {
        ["os"] = "Windows",
        ["browser"] = "Chrome",
        ["device"] = "",
        ["system_locale"] = "en-US",
        ["has_client_mods"] = false,
        ["browser_user_agent"] = UserAgent,
        ["browser_version"] = $"{ChromeMajor()}.0.0.0",
        ["os_version"] = "10",
        ["referrer"] = "",
        ["referring_domain"] = "",
        ["referrer_current"] = "",
        ["referring_domain_current"] = "",
        ["release_channel"] = "stable",
        ["client_build_number"] = Build,
        ["client_event_source"] = null,
    };

    private static string SuperProperties() =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(Properties().ToJsonString()));

    /// <summary>Reads the build number of Discord's web client from discord.com's own scripts, once per run.</summary>
    internal static async Task LoadBuildAsync(CancellationToken ct)
    {
        if (_build is not null) return;
        try
        {
            using var http = ExoHttp.Create(TimeSpan.FromSeconds(20));
            http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            var page = await http.GetStringAsync("https://discord.com/login", ct).ConfigureAwait(false);
            foreach (var script in ScriptRegex().Matches(page).Select(m => m.Groups[1].Value).Reverse().Take(8))
            {
                using var response = await http.GetAsync("https://discord.com" + script, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 24_000_000) continue;
                var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (BuildRegex().Match(text) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var build))
                {
                    _build = build;
                    AppLog.Info($"Discord client: web build {build}.");
                    return;
                }
            }
            AppLog.Info("Discord client: web build not found; using " + FallbackBuild + ".");
        }
        // The HttpClient's own timeout is a cancellation too: only the caller's ends this quietly.
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            AppLog.Info("Discord client: web build not read (" + ex.GetType().Name + "); using " + FallbackBuild + ".");
        }
    }

    /// <summary>Discord's answer, or why not; Mfa when Discord wants the person to verify first (<see cref="FinishMfaAsync"/>).</summary>
    internal sealed record Reply(JsonElement? Body, string? Error, MfaAsk? Mfa = null);

    /// <summary>50 messages: the newest, or those before, after or around one (a jump to a message).</summary>
    internal Task<Reply> MessagesAsync(string channelId, string? before, CancellationToken ct, string? after = null, string? around = null) =>
        SendAsync(HttpMethod.Get, $"channels/{Id(channelId)}/messages?limit=50" + (before is null ? "" : "&before=" + Id(before))
            + (after is null ? "" : "&after=" + Id(after)) + (around is null ? "" : "&around=" + Id(around)), null, ct);

    /// <summary>A file going with a message; Description is its alt text (Modify Attachment), up to 1024 characters.</summary>
    internal sealed record Upload(string Name, string Type, byte[] Data, string? Description = null);

    /// <summary>A message forwarded from another conversation: Discord's forward is a reference of type 1, plus an optional note after it.</summary>
    internal sealed record Forward(string ChannelId, string MessageId, string? GuildId);

    internal Task<Reply> PostMessageAsync(string channelId, string content, string? replyTo, string? guildId, CancellationToken ct, IReadOnlyList<Upload>? files = null,
        bool replyPing = true, Forward? forward = null, bool quiet = false)
    {
        // Discord's @silent: a message starting with it goes without notifications (SUPPRESS_NOTIFICATIONS, 4096), the word left out.
        var (silent, text) = Silent(content);
        silent |= quiet;
        var body = new JsonObject
        {
            ["content"] = text,
            ["nonce"] = Nonce(),
            ["tts"] = false,
            ["flags"] = silent ? 4096 : 0,
        };
        if (replyTo is not null)
        {
            body["message_reference"] = new JsonObject { ["message_id"] = Id(replyTo), ["channel_id"] = Id(channelId), ["guild_id"] = guildId };
            // Discord's @ ON/OFF on a reply: whether the person replied to is pinged.
            if (!replyPing) body["allowed_mentions"] = new JsonObject { ["parse"] = new JsonArray("users", "roles", "everyone"), ["replied_user"] = false };
        }
        else if (forward is not null)
            body["message_reference"] = new JsonObject { ["type"] = 1, ["message_id"] = Id(forward.MessageId), ["channel_id"] = Id(forward.ChannelId), ["guild_id"] = forward.GuildId };
        if (files is { Count: > 0 })
        {
            body["attachments"] = new JsonArray([.. files.Select((f, i) =>
            {
                var attachment = new JsonObject { ["id"] = i, ["filename"] = f.Name };
                if (f.Description is { Length: > 0 } alt) attachment["description"] = alt;
                return (JsonNode)attachment;
            })]);
            return SendAsync(HttpMethod.Post, $"channels/{Id(channelId)}/messages", body, ct, files);
        }
        return SendAsync(HttpMethod.Post, $"channels/{Id(channelId)}/messages", body, ct);
    }

    /// <summary>A message starting with @silent (then a space, or nothing): Discord sends it silently, without the word.</summary>
    internal static (bool Silent, string Content) Silent(string content) =>
        content.StartsWith("@silent", StringComparison.Ordinal) && (content.Length == 7 || char.IsWhiteSpace(content[7])) ? (true, content[7..].Trim()) : (false, content);

    internal Task<Reply> EditMessageAsync(string channelId, string messageId, string content, CancellationToken ct) =>
        SendAsync(HttpMethod.Patch, $"channels/{Id(channelId)}/messages/{Id(messageId)}", new JsonObject { ["content"] = content }, ct);

    internal Task<Reply> DeleteMessageAsync(string channelId, string messageId, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"channels/{Id(channelId)}/messages/{Id(messageId)}", null, ct);

    internal Task<Reply> AckAsync(string channelId, string messageId, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"channels/{Id(channelId)}/messages/{Id(messageId)}/ack", new JsonObject { ["token"] = null, ["last_viewed"] = LastViewed() }, ct);

    /// <summary>Mark Unread: read up to the message before the one chosen, as Discord's own sends it (manual).</summary>
    internal Task<Reply> MarkUnreadAsync(string channelId, string beforeId, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"channels/{Id(channelId)}/messages/{Id(beforeId)}/ack", new JsonObject { ["manual"] = true, ["mention_count"] = 0 }, ct);

    /// <summary>A thread from a message (Create Thread), or a forum or media channel's new post with its first message and tags.</summary>
    internal Task<Reply> StartThreadAsync(string channelId, string? messageId, string name, string? content, IReadOnlyList<string> tags, CancellationToken ct)
    {
        if (messageId is not null)
            return SendAsync(HttpMethod.Post, $"channels/{Id(channelId)}/messages/{Id(messageId)}/threads", new JsonObject { ["name"] = name, ["auto_archive_duration"] = 4320 }, ct);
        return SendAsync(HttpMethod.Post, $"channels/{Id(channelId)}/threads", new JsonObject
        {
            ["name"] = name,
            ["auto_archive_duration"] = 4320,
            ["applied_tags"] = new JsonArray([.. tags.Select(t => (JsonNode)Id(t))]),
            ["message"] = new JsonObject { ["content"] = content ?? "" },
        }, ct);
    }

    /// <summary>A group DM's Edit Group: its name and icon (a data URI, or null to take it off); null leaves one as it is.</summary>
    internal Task<Reply> EditGroupAsync(string channelId, string? name, string? icon, bool clearIcon, CancellationToken ct)
    {
        var body = new JsonObject();
        if (name is not null) body["name"] = name;
        if (icon is not null) body["icon"] = icon;
        else if (clearIcon) body["icon"] = null;
        return SendAsync(HttpMethod.Patch, $"channels/{Id(channelId)}", body, ct);
    }

    /// <summary>The group's owner hands it to another member (Make Group Owner).</summary>
    internal Task<Reply> GroupOwnerAsync(string channelId, string userId, CancellationToken ct) =>
        SendAsync(HttpMethod.Patch, $"channels/{Id(channelId)}", new JsonObject { ["owner"] = Id(userId) }, ct);

    /// <summary>Remove From Group: the owner takes someone out of the group DM.</summary>
    internal Task<Reply> RemoveFromGroupAsync(string channelId, string userId, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"channels/{Id(channelId)}/recipients/{Id(userId)}", null, ct);

    internal Task<Reply> TypingAsync(string channelId, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"channels/{Id(channelId)}/typing", null, ct);

    internal Task<Reply> RingAsync(string channelId, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"channels/{Id(channelId)}/call/ring", new JsonObject { ["recipients"] = null }, ct);

    /// <summary>Decline: stops this call ringing this user (Discord's own Decline sends no recipients).</summary>
    internal Task<Reply> StopRingingAsync(string channelId, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"channels/{Id(channelId)}/call/stop-ringing", new JsonObject(), ct);

    /// <summary>Add Friend (or accept theirs) by id, as Discord's person menu sends it; a request from someone you share no server with answers 80013 until it is sent again confirmed, as Discord's own confirm does.</summary>
    internal Task<Reply> AcceptFriendAsync(string userId, bool confirmStranger, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, $"users/@me/relationships/{Id(userId)}", confirmStranger ? new JsonObject { ["confirm_stranger_request"] = true } : new JsonObject(), ct);

    internal const int StrangerRequest = 80013;

    /// <summary>Block: the relationship becomes type 2 (Unblock removes it).</summary>
    internal Task<Reply> BlockAsync(string userId, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, $"users/@me/relationships/{Id(userId)}", new JsonObject { ["type"] = 2 }, ct);

    /// <summary>Ignore or Unignore: Discord hides their messages and notifications without telling them.</summary>
    internal Task<Reply> IgnoreAsync(string userId, bool ignore, CancellationToken ct) =>
        SendAsync(ignore ? HttpMethod.Put : HttpMethod.Delete, $"users/@me/relationships/{Id(userId)}/ignore", null, ct);

    internal Task<Reply> RemoveRelationshipAsync(string userId, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"users/@me/relationships/{Id(userId)}", null, ct);

    internal Task<Reply> AddFriendAsync(string username, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "users/@me/relationships", new JsonObject { ["username"] = username, ["discriminator"] = null }, ct);

    /// <summary>Close DM (Discord's ×): the conversation leaves the list; a group DM is left.</summary>
    internal Task<Reply> CloseDmAsync(string channelId, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"channels/{Id(channelId)}", null, ct);

    /// <summary>Add Friends to DM: into a group DM, or a new group with the people of a 1:1 DM.</summary>
    internal Task<Reply> AddToDmAsync(string channelId, IReadOnlyList<string> existing, string userId, bool group, CancellationToken ct) => group
        ? SendAsync(HttpMethod.Put, $"channels/{Id(channelId)}/recipients/{Id(userId)}", new JsonObject(), ct)
        : SendAsync(HttpMethod.Post, "users/@me/channels", new JsonObject { ["recipients"] = new JsonArray([.. existing.Append(userId).Select(id => (JsonNode)Id(id))]) }, ct);

    /// <summary>Your DM with someone, or a new group DM with several people (up to 9, as Discord allows).</summary>
    internal Task<Reply> OpenDmAsync(IReadOnlyList<string> userIds, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "users/@me/channels", new JsonObject { ["recipients"] = new JsonArray([.. userIds.Select(id => (JsonNode)Id(id))]) }, ct);

    internal Task<Reply> AckBulkAsync(IEnumerable<(string Channel, string Message)> reads, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "read-states/ack-bulk", new JsonObject
        {
            ["read_states"] = new JsonArray([.. reads.Select(r => (JsonNode)new JsonObject { ["channel_id"] = Id(r.Channel), ["message_id"] = Id(r.Message), ["read_state_type"] = 0 })]),
        }, ct);

    internal Task<Reply> SetStatusAsync(string status, CancellationToken ct) =>
        SendAsync(HttpMethod.Patch, "users/@me/settings", new JsonObject { ["status"] = status }, ct);

    /// <summary>The newest 50 pins, as Discord's client asks for them: items of the message and when it was pinned.</summary>
    internal Task<Reply> PinsAsync(string channelId, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"channels/{Id(channelId)}/messages/pins?limit=50", null, ct);

    internal Task<Reply> PinAsync(string channelId, string messageId, bool pin, CancellationToken ct) =>
        SendAsync(pin ? HttpMethod.Put : HttpMethod.Delete, $"channels/{Id(channelId)}/messages/pins/{Id(messageId)}", null, ct);

    /// <summary>Discord's GIF picker (Tenor through Discord): trending, or a search.</summary>
    internal Task<Reply> GifsAsync(string? query, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, string.IsNullOrWhiteSpace(query)
            ? "gifs/trending-gifs?media_format=mp4&provider=tenor&locale=en-US"
            : $"gifs/search?q={Uri.EscapeDataString(query)}&media_format=mp4&provider=tenor&locale=en-US", null, ct);

    /// <summary>The account's frecency and favorites settings (protobuf, base64 in "settings"): see <see cref="DiscordFavoriteGifs"/>.</summary>
    internal Task<Reply> FrecencyAsync(CancellationToken ct) =>
        SendAsync(HttpMethod.Get, "users/@me/settings-proto/2", null, ct);

    /// <summary>The frecency settings protobuf's bytes; null when Discord can't be read.</summary>
    internal async Task<byte[]?> FrecencyProtoAsync(CancellationToken ct)
    {
        var reply = await FrecencyAsync(ct).ConfigureAwait(false);
        if (reply.Error is not null || reply.Body is not { ValueKind: JsonValueKind.Object } body) return null;
        if (!body.TryGetProperty("settings", out var text) || text.ValueKind != JsonValueKind.String) return null;
        try { return Convert.FromBase64String(text.GetString() ?? ""); }
        catch (FormatException) { return null; }
    }

    /// <summary>Saves the fields present in a frecency settings protobuf (base64), as Discord's client does.</summary>
    internal Task<Reply> SaveFrecencyAsync(string settings, CancellationToken ct) =>
        SendAsync(HttpMethod.Patch, "users/@me/settings-proto/2", new JsonObject { ["settings"] = settings }, ct);

    /// <summary>An invite's server, channel and counts, as a message's invite card shows them.</summary>
    internal Task<Reply> InviteInfoAsync(string code, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"invites/{Uri.EscapeDataString(code)}?with_counts=true&with_expiration=true", null, ct);

    internal Task<Reply> JoinInviteAsync(string code, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"invites/{Uri.EscapeDataString(code)}", new JsonObject(), ct);

    internal Task<Reply> LeaveGuildAsync(string guildId, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"users/@me/guilds/{Id(guildId)}", new JsonObject { ["lurking"] = false }, ct);

    /// <summary>Saves channel override flags (and, with guildFlags, the server's), as Discord's Browse Channels does.</summary>
    internal Task<Reply> ChannelFlagsAsync(string guildId, IReadOnlyDictionary<string, int> flags, int? guildFlags, CancellationToken ct)
    {
        var overrides = new JsonObject();
        foreach (var (id, value) in flags) overrides[Id(id)] = new JsonObject { ["flags"] = value };
        var body = new JsonObject { ["channel_overrides"] = overrides };
        if (guildFlags is { } g) body["flags"] = g;
        return SendAsync(HttpMethod.Patch, $"users/@me/guilds/{Id(guildId)}/settings", body, ct);
    }

    /// <summary>A server's own settings (Mute Server, Notification Settings), as the rail's right-click menu saves them.</summary>
    internal Task<Reply> GuildSettingsAsync(string guildId, JsonObject body, CancellationToken ct) =>
        SendAsync(HttpMethod.Patch, $"users/@me/guilds/{Id(guildId)}/settings", body, ct);

    /// <summary>Mute or unmute a server, for a while when until is set (Discord's 15 minutes to 24 hours).</summary>
    internal static JsonObject MuteBody(bool mute, DateTimeOffset? until)
    {
        var body = new JsonObject { ["muted"] = mute };
        body["mute_config"] = mute && until is { } end ? new JsonObject
        {
            ["selected_time_window"] = (int)Math.Round((end - DateTimeOffset.UtcNow).TotalSeconds),
            ["end_time"] = end.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        } : null;
        return body;
    }

    internal Task<Reply> CollapseAsync(string guildId, string categoryId, bool collapsed, CancellationToken ct) =>
        SendAsync(HttpMethod.Patch, $"users/@me/guilds/{Id(guildId)}/settings", new JsonObject
        {
            ["channel_overrides"] = new JsonObject { [Id(categoryId)] = new JsonObject { ["collapsed"] = collapsed } },
        }, ct);

    /// <summary>Accepts a message request (consent ACCEPTED), as Discord's Accept does; then it's a DM like any other.</summary>
    internal Task<Reply> AcceptRequestAsync(string channelId, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, $"channels/{Id(channelId)}/recipients/@me", new JsonObject { ["consent_status"] = 2 }, ct);

    /// <summary>Ignores a message request (Discord's Ignore): it leaves your requests.</summary>
    internal Task<Reply> IgnoreRequestAsync(string channelId, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"channels/{Id(channelId)}/recipients/@me", null, ct);

    /// <summary>A voice channel's status (Set Channel Status; empty clears it).</summary>
    internal Task<Reply> VoiceStatusAsync(string channelId, string status, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, $"channels/{Id(channelId)}/voice-status", new JsonObject { ["status"] = status }, ct);

    /// <summary>The Server Guide: the welcome message, new member to-dos and resource channels.</summary>
    internal Task<Reply> ServerGuideAsync(string guildId, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"guilds/{Id(guildId)}/new-member-welcome", null, ct);

    /// <summary>
    /// Discord's Members page: a chunk of 250 members in a sort (1 joined newest, 2 joined oldest, 3 account newest,
    /// 4 account oldest: Discord's order_by), matching a name or id, after the last member of the chunk before
    /// (their join time and id, Discord's cursor), with the total that match.
    /// </summary>
    internal Task<Reply> MembersSearchAsync(string guildId, int sort, string? query, (long JoinedAt, string UserId)? after, CancellationToken ct)
    {
        var body = new JsonObject { ["limit"] = 250, ["sort"] = sort, ["or_query"] = new JsonObject(), ["and_query"] = MemberQuery(query, null) };
        if (after is { } a) body["after"] = new JsonObject { ["guild_joined_at"] = a.JoinedAt, ["user_id"] = Id(a.UserId) };
        return SendAsync(HttpMethod.Post, $"guilds/{Id(guildId)}/members-search", body, ct);
    }

    /// <summary>A channel's first message: what Discord's Server Guide shows for a resource without a description.</summary>
    internal Task<Reply> FirstMessageAsync(string channelId, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"channels/{Id(channelId)}/messages?after=0&limit=1", null, ct);

    /// <summary>Prune: with count only, how many members inactive for days would go; else remove them.</summary>
    internal Task<Reply> PruneAsync(string guildId, int days, bool countOnly, CancellationToken ct) => countOnly
        ? SendAsync(HttpMethod.Get, $"guilds/{Id(guildId)}/prune?days={days}", null, ct)
        : SendAsync(HttpMethod.Post, $"guilds/{Id(guildId)}/prune", new JsonObject { ["days"] = days, ["compute_prune_count"] = true }, ct);

    internal Task<Reply> DeleteChannelAsync(string channelId, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"channels/{Id(channelId)}", null, ct);

    /// <summary>Kick, with the reason as Discord's client sends it (in the query).</summary>
    internal Task<Reply> KickAsync(string guildId, string userId, string? reason, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"guilds/{Id(guildId)}/members/{Id(userId)}" + (string.IsNullOrWhiteSpace(reason) ? "" : "?reason=" + Uri.EscapeDataString(reason.Trim())), null, ct);

    /// <summary>Discord's Delete Message History choices when banning, in seconds.</summary>
    internal static readonly int[] BanDeleteWindows = [0, 3600, 21600, 43200, 86400, 259200, 604800];

    /// <summary>Bans, deleting the member's messages from one of <see cref="BanDeleteWindows"/>, with the reason for the Audit Log.</summary>
    internal Task<Reply> BanAsync(string guildId, string userId, int deleteSeconds, string? reason, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, $"guilds/{Id(guildId)}/bans/{Id(userId)}",
            new JsonObject { ["delete_message_seconds"] = BanDeleteWindows.Contains(deleteSeconds) ? deleteSeconds : 0 }, ct, reason: reason);

    internal Task<Reply> UnbanAsync(string guildId, string userId, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"guilds/{Id(guildId)}/bans/{Id(userId)}", null, ct);

    /// <summary>The most bans Discord sends at once.</summary>
    internal const int BanPage = 1000;

    /// <summary>A page of the server's bans (after a banned user's id), each with the user and the reason.</summary>
    internal Task<Reply> BansAsync(string guildId, string? after, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"guilds/{Id(guildId)}/bans?limit={BanPage}" + (after is null ? "" : "&after=" + Id(after)), null, ct);

    /// <summary>Timeout: no messages, reactions or voice until a time (null lifts it), as Discord's Timeout does.</summary>
    internal Task<Reply> TimeoutAsync(string guildId, string userId, DateTimeOffset? until, string? reason, CancellationToken ct) =>
        SendAsync(HttpMethod.Patch, $"guilds/{Id(guildId)}/members/{Id(userId)}",
            new JsonObject { ["communication_disabled_until"] = until?.ToUniversalTime().ToString("o", System.Globalization.CultureInfo.InvariantCulture) }, ct, reason: reason);

    /// <summary>50 Audit Log entries (older than before), optionally by one person or of one action, as Discord's filters ask.</summary>
    internal Task<Reply> AuditLogAsync(string guildId, string? before, string? userId, int? action, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"guilds/{Id(guildId)}/audit-logs?limit=50"
            + (before is null ? "" : "&before=" + Id(before)) + (userId is null ? "" : "&user_id=" + Id(userId)) + (action is { } a ? "&action_type=" + a : ""), null, ct);

    internal Task<Reply> RolesAsync(string guildId, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"guilds/{Id(guildId)}/roles", null, ct);

    /// <summary>The server with Discord's own counts: members, and how many are online.</summary>
    internal Task<Reply> GuildCountsAsync(string guildId, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"guilds/{Id(guildId)}?with_counts=true", null, ct);

    /// <summary>Discord's Create Event: in a stage (entity 1), a voice channel (2) or somewhere else (3, which needs an end: the one given, else an hour on).</summary>
    internal Task<Reply> CreateEventAsync(string guildId, string? eventId, string name, string? description, DateTimeOffset start, DateTimeOffset? end,
        string? channelId, bool stage, string? location, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["name"] = name,
            ["privacy_level"] = 2,
            ["scheduled_start_time"] = start.UtcDateTime.ToString("o"),
            ["entity_type"] = channelId is null ? 3 : stage ? 1 : 2,
        };
        if (!string.IsNullOrWhiteSpace(description)) body["description"] = description;
        if (channelId is not null) body["channel_id"] = Id(channelId);
        else
        {
            body["entity_metadata"] = new JsonObject { ["location"] = location ?? "" };
            body["scheduled_end_time"] = (end is { } e && e > start ? e : start.AddHours(1)).UtcDateTime.ToString("o");
        }
        return eventId is null
            ? SendAsync(HttpMethod.Post, $"guilds/{Id(guildId)}/scheduled-events", body, ct)
            : SendAsync(HttpMethod.Patch, $"guilds/{Id(guildId)}/scheduled-events/{Id(eventId)}", body, ct);
    }

    /// <summary>Start Event (2), End Event (3) or Cancel Event (4), as Discord's client sends them.</summary>
    internal Task<Reply> EventStatusAsync(string guildId, string eventId, int status, CancellationToken ct) =>
        SendAsync(HttpMethod.Patch, $"guilds/{Id(guildId)}/scheduled-events/{Id(eventId)}", new JsonObject { ["status"] = status }, ct);

    internal Task<Reply> EventAsync(string guildId, string eventId, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"guilds/{Id(guildId)}/scheduled-events/{Id(eventId)}", null, ct);

    /// <summary>Interested (response 1) or not, as Discord's client sends it.</summary>
    internal Task<Reply> RsvpAsync(string guildId, string eventId, bool interested, CancellationToken ct) => interested
        ? SendAsync(HttpMethod.Put, $"guilds/{Id(guildId)}/scheduled-events/{Id(eventId)}/users/@me", new JsonObject { ["response"] = 1 }, ct)
        : SendAsync(HttpMethod.Delete, $"guilds/{Id(guildId)}/scheduled-events/{Id(eventId)}/users/@me", null, ct);

    /// <summary>The user's own answers to a server's events (each with its event id and response), one call, as Discord's client asks.</summary>
    internal Task<Reply> MyEventRsvpsAsync(string guildId, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"users/@me/scheduled-events?guild_ids={Id(guildId)}", null, ct);

    internal Task<Reply> EventsAsync(string guildId, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"guilds/{Id(guildId)}/scheduled-events?with_user_count=true", null, ct);

    /// <summary>Create Channel: a text (0) or voice (2) channel, in a category or loose.</summary>
    internal Task<Reply> CreateChannelAsync(string guildId, string name, int type, string? parentId, CancellationToken ct)
    {
        var body = new JsonObject { ["name"] = name, ["type"] = type };
        if (parentId is not null) body["parent_id"] = Id(parentId);
        return SendAsync(HttpMethod.Post, $"guilds/{Id(guildId)}/channels", body, ct);
    }

    /// <summary>Mutual Friends on a full profile.</summary>
    internal Task<Reply> MutualFriendsAsync(string userId, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"users/{Id(userId)}/relationships", null, ct);

    internal Task<Reply> ProfileAsync(string userId, string? guildId, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"users/{Id(userId)}/profile?with_mutual_guilds=true&with_mutual_friends_count=true" + (guildId is null ? "" : $"&guild_id={Id(guildId)}"), null, ct);

    /// <summary>The profile's Note (only visible to you); Discord answers 404 when there is none.</summary>
    internal Task<Reply> NoteAsync(string userId, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"users/@me/notes/{Id(userId)}", null, ct);

    internal Task<Reply> SaveNoteAsync(string userId, string note, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, $"users/@me/notes/{Id(userId)}", new JsonObject { ["note"] = note }, ct);

    /// <summary>A forum's (or channel's) open threads, newest activity first, as Discord's forum view asks for them.</summary>
    /// <summary>A forum's posts or a channel's threads, 25 at a time, newest activity first: the open ones, or the archived ones from offset.</summary>
    internal Task<Reply> ThreadsAsync(string channelId, bool archived, int offset, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"channels/{Id(channelId)}/threads/search?archived={(archived ? "true" : "false")}&sort_by=last_message_time&sort_order=desc&limit=25&tag_setting=match_some&offset={offset}", null, ct);

    internal Task<Reply> PostStickerAsync(string channelId, string stickerId, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"channels/{Id(channelId)}/messages", new JsonObject { ["sticker_ids"] = new JsonArray(Id(stickerId)), ["nonce"] = Nonce(), ["tts"] = false, ["flags"] = 0 }, ct);

    /// <summary>Mute a channel (for a while when until is set, as Discord's 15 minutes to 24 hours), or unmute it.</summary>
    internal Task<Reply> MuteChannelAsync(string guildId, string channelId, bool mute, CancellationToken ct, DateTimeOffset? until = null)
    {
        var entry = new JsonObject { ["muted"] = mute };
        if (mute && until is { } end)
            entry["mute_config"] = new JsonObject
            {
                ["selected_time_window"] = (int)Math.Round((end - DateTimeOffset.UtcNow).TotalSeconds),
                ["end_time"] = end.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            };
        else entry["mute_config"] = null;
        return SendAsync(HttpMethod.Patch, $"users/@me/guilds/{Id(guildId)}/settings", new JsonObject { ["channel_overrides"] = new JsonObject { [Id(channelId)] = entry } }, ct);
    }

    /// <summary>Mute @user or Mute Conversation: a DM's override lives in the DM settings (guild "@me"), as Discord saves it.</summary>
    internal Task<Reply> MuteDmAsync(string channelId, bool mute, DateTimeOffset? until, CancellationToken ct) =>
        SendAsync(HttpMethod.Patch, "users/@me/guilds/@me/settings",
            new JsonObject { ["channel_overrides"] = new JsonObject { [Id(channelId)] = MuteBody(mute, until) } }, ct);

    /// <summary>A channel's notification setting: 0 all messages, 1 only mentions, 2 nothing, 3 as its category or server.</summary>
    internal Task<Reply> ChannelNotifyAsync(string guildId, string channelId, int level, CancellationToken ct) =>
        SendAsync(HttpMethod.Patch, $"users/@me/guilds/{Id(guildId)}/settings", new JsonObject
        {
            ["channel_overrides"] = new JsonObject { [Id(channelId)] = new JsonObject { ["message_notifications"] = level } },
        }, ct);

    /// <summary>Discord's Invite People: a 7-day invite to a channel.</summary>
    internal Task<Reply> LogoutAsync(CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "auth/logout", new JsonObject { ["provider"] = null, ["voip_provider"] = null }, ct);

    /// <summary>Exchanges a QR sign-in ticket for the encrypted token (<see cref="DiscordRemoteAuth"/>).</summary>
    internal Task<Reply> RemoteAuthLoginAsync(string ticket, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "users/@me/remote-auth/login", new JsonObject { ["ticket"] = ticket }, ct);

    // reason: the Audit Log's reason (X-Audit-Log-Reason); content: a form other than a message's (made again on a retry);
    // mfaToken: the verified check's token, on the one retry after Discord asked for it.
    private async Task<Reply> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken ct, IReadOnlyList<Upload>? files = null,
        string? reason = null, Func<HttpContent>? content = null, string? mfaToken = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, Api + path) { Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionOrLower };
            if (_token is not null) request.Headers.TryAddWithoutValidation("Authorization", _token);
            if (!string.IsNullOrWhiteSpace(reason)) request.Headers.TryAddWithoutValidation("X-Audit-Log-Reason", Uri.EscapeDataString(reason.Trim()));
            if (mfaToken is not null) request.Headers.TryAddWithoutValidation("X-Discord-MFA-Authorization", mfaToken);
            if (content is not null) request.Content = content();
            else if (files is not null)
            {
                var form = new MultipartFormDataContent();
                form.Add(new StringContent(body?.ToJsonString() ?? "{}", Encoding.UTF8, "application/json"), "payload_json");
                for (var i = 0; i < files.Count; i++)
                {
                    var part = new ByteArrayContent(files[i].Data);
                    part.Headers.ContentType = MediaTypeHeaderValue.TryParse(files[i].Type, out var type) ? type : new MediaTypeHeaderValue("application/octet-stream");
                    form.Add(part, $"files[{i}]", files[i].Name);
                }
                request.Content = form;
            }
            else if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            HttpResponseMessage response;
            var sent = System.Diagnostics.Stopwatch.GetTimestamp();
            try { response = await _http.SendAsync(request, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                // A read on a connection that turned out dead goes once more, on a fresh one.
                if (attempt == 0 && method == HttpMethod.Get && ex is HttpRequestException) continue;
                return new Reply(null, "Discord could not be reached.");
            }
            using (response)
            {
                var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                // A slow answer says whether Discord took long (on the wire) or something before it did.
                var wire = System.Diagnostics.Stopwatch.GetElapsedTime(sent).TotalMilliseconds;
                if (wire >= 700) AppLog.Info($"PERF discord rest {method} {Route(path)} wireMs={wire:F0} http={response.Version}");
                JsonElement? json = null;
                if (text.Length > 0)
                {
                    try { using var doc = JsonDocument.Parse(text); json = doc.RootElement.Clone(); }
                    catch (JsonException) { }
                }
                // Search answers 202 while Discord is still indexing, with when to ask again.
                if (response.StatusCode == HttpStatusCode.Accepted && json is { ValueKind: JsonValueKind.Object } indexing && indexing.TryGetProperty("retry_after", out var wait))
                {
                    if (attempt < 3 && wait.TryGetDouble(out var waitSeconds) && waitSeconds <= 10)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(Math.Max(0.5, waitSeconds)), ct).ConfigureAwait(false);
                        continue;
                    }
                    return new Reply(null, path.Contains("members-search", StringComparison.Ordinal) ? MembersIndexing : "Discord is still getting these messages ready to search. Try again in a moment.");
                }
                if (response.IsSuccessStatusCode) return new Reply(json, null);
                if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < 2 &&
                    json is { ValueKind: JsonValueKind.Object } limited &&
                    limited.TryGetProperty("retry_after", out var after) && after.TryGetDouble(out var seconds) && seconds <= 10)
                {
                    await Task.Delay(TimeSpan.FromSeconds(seconds + 0.1), ct).ConfigureAwait(false);
                    continue;
                }
                AppLog.Info($"Discord client: {method} {Route(path)} answered {(int)response.StatusCode}.");
                // Discord wants the person to verify first: the request waits here for the code (once; a second ask is refused, as Discord's client does).
                if (mfaToken is null && ReadMfa(response.StatusCode, json) is { } challenge)
                    return AskMfa(challenge, json, (token, again) => SendAsync(method, path, body, again, files, reason, content, token));
                return new Reply(json, ErrorLine(response.StatusCode, json));
            }
        }
    }

    private static string ErrorLine(HttpStatusCode status, JsonElement? json)
    {
        if (json is { ValueKind: JsonValueKind.Object } body)
        {
            if (body.TryGetProperty("captcha_key", out _))
                return "Discord wants to check you're human here. Do this once in the Discord app, then try again.";
            // Request entity too large: over the account's upload size (10 MB without Nitro).
            if (body.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number && code.GetInt32() == 40005)
                return TooLarge;
            if (body.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                return "Discord: " + message.GetString();
        }
        return status switch
        {
            HttpStatusCode.Unauthorized => "Discord signed you out.",
            HttpStatusCode.Forbidden => "You don't have permission to do that here.",
            HttpStatusCode.NotFound => "That isn't on Discord anymore.",
            HttpStatusCode.TooManyRequests => "Discord says slow down. Try again in a moment.",
            HttpStatusCode.RequestEntityTooLarge => TooLarge,
            _ => "Discord didn't take that. Try again.",
        };
    }

    /// <summary>A server's member search answered "still indexing" (a new server's, for a while): the page asks again.</summary>
    internal const string MembersIndexing = "Discord is still getting this server's members ready.";

    private const string TooLarge = "That's too big for Discord. Files can be 10 MB without Nitro.";

    // The path without ids, so the log shows which call answered without whose channel it was.
    private static string Route(string path) => IdRegex().Replace(path.Split('?')[0], ":id");

    /// <summary>A Discord snowflake id, or an exception: ids from the UI go into URLs.</summary>
    internal static string Id(string id) =>
        id.Length is > 0 and <= 20 && id.All(char.IsAsciiDigit) ? id : throw new ArgumentException("Not a Discord id.");

    private static string Nonce() =>
        (((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1420070400000UL) << 22).ToString(System.Globalization.CultureInfo.InvariantCulture);

    // Days since Discord's epoch (2015-01-01), as Discord's client sends on marking read.
    private static long LastViewed() => (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1420070400000L) / 86400000L;

    private static string TimeZoneName()
    {
        try { return TimeZoneInfo.TryConvertWindowsIdToIanaId(TimeZoneInfo.Local.Id, out var iana) ? iana : "UTC"; }
        catch { return "UTC"; }
    }

    private static int ChromeMajor()
    {
        try
        {
            var version = Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString();
            if (version is not null && int.TryParse(version.Split('.')[0], out var major) && major > 100) return major;
        }
        catch { }
        return 141;
    }

    public void Dispose() => _http.Dispose();

    [GeneratedRegex("<script[^>]+src=\"(/assets/[A-Za-z0-9._-]+\\.js)\"")]
    private static partial Regex ScriptRegex();

    [GeneratedRegex("build_?[Nn]umber\\D{1,12}(\\d{6,7})")]
    private static partial Regex BuildRegex();

    [GeneratedRegex("\\d{5,20}")]
    private static partial Regex IdRegex();
}
