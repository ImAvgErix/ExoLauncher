using System.Text.Json;
using ExoLauncher.Helpers;
using ExoLauncher.Services.DiscordClient;

namespace ExoLauncher.Services;

/// <summary>
/// The Discord room's own client (<see cref="DiscordSession"/>): sign-in by QR code, the
/// server and channel lists the user may see, a channel's messages, and sending, editing,
/// deleting and marking read. Channel ids from the page are checked against what the user
/// can see before any call reaches Discord.
/// </summary>
public sealed partial class WebHostBridge
{
    private bool _discordEvents;

    private void DiscordEvents()
    {
        if (_discordEvents) return;
        _discordEvents = true;
        DiscordSession.Event += PostEvent;
        DiscordSession.VideoFrame += PostVideoFrame;
    }

    // Discord video reaches the page as shared memory beside a small JSON note (WebView2's shared buffers; the page releases
    // each once decoded): as base64 inside JSON, 60 frames a second per stream were serialized on Exo's UI thread and parsed
    // again in the page, a third bigger than the video itself.
    private void PostVideoFrame(object note, byte[] frame)
    {
        var web = _web;
        if (web is null || frame.Length == 0) return;
        var json = JsonSerializer.Serialize(note);
        void Send()
        {
            try
            {
                using var buffer = web.Environment.CreateSharedBuffer((ulong)frame.Length);
                using (var stream = System.IO.WindowsRuntimeStreamExtensions.AsStreamForWrite(buffer.OpenStream(), 0)) stream.Write(frame);
                web.PostSharedBufferToScript(buffer, Microsoft.Web.WebView2.Core.CoreWebView2SharedBufferAccess.ReadOnly, json);
            }
            catch { }
        }
        if (!_queue.HasThreadAccess) _queue.TryEnqueue(Send);
        else Send();
    }

    /// <summary>A Discord notification was clicked: the room opens that conversation (a thread in its channel).</summary>
    internal void PostDiscordOpen(string? guildId, string channelId, string? parentId, string? name) =>
        PostEvent("discord.open", new { guildId, channelId, parentId, name });

    private object DiscordSignIn(JsonElement p, bool hasParams)
    {
        DiscordEvents();
        if (ReadBool(p, hasParams, "cancel") == true) DiscordSession.CancelSignIn();
        else DiscordSession.SignIn();
        return new { ok = true };
    }

    // The client's state; connect starts it again after it stopped (Try again on its error).
    private static object DiscordStatus(JsonElement p, bool hasParams)
    {
        if (ReadBool(p, hasParams, "connect") == true) DiscordSession.Start();
        return DiscordSession.Status();
    }

    private static async Task<object> DiscordSignOutAsync()
    {
        await DiscordSession.SignOutAsync().ConfigureAwait(false);
        return new { ok = true };
    }

    private object DiscordHome()
    {
        DiscordEvents();
        return DiscordSession.Current is { } session ? session.Cache.Home() : new { me = (object?)null, dms = Array.Empty<object>(), guilds = Array.Empty<object>() };
    }

    private static object DiscordChannels(JsonElement p, bool hasParams)
    {
        if (DiscordSession.Current is not { } session || ReadString(p, hasParams, "guildId") is not { } guildId) return new { name = (string?)null, channels = Array.Empty<object>() };
        var id = DiscordRest.Id(guildId);
        // The server's voice channel statuses come with its channels the first time they show, as in Discord.
        session.AskVoiceStatuses(id);
        return session.Cache.Channels(id);
    }

    private static async Task<object> DiscordMessagesAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        if (Channel(p, hasParams) is not { } c) return new { ok = false, message = "Discord isn't connected." };
        // peek: the Inbox's Unreads and a hover read a channel without opening it.
        if (ReadBool(p, hasParams, "peek") != true) SubscribeOpen(c);
        string? At(string name) => ReadString(p, hasParams, name) is { } id ? DiscordRest.Id(id) : null;
        var reply = await c.Session.Rest.MessagesAsync(c.Id, At("before"), ct, At("after"), At("around")).ConfigureAwait(false);
        return new { ok = reply.Error is null, message = reply.Error, messages = reply.Body, lastRead = c.Session.Cache.LastRead(c.Id) };
    }

    private static async Task<object> DiscordSendAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        if (Channel(p, hasParams) is not { } c) return new { ok = false, message = "Discord isn't connected." };
        var content = ReadString(p, hasParams, "content")?.Trim() ?? "";
        if (!c.Session.Cache.CanSend(c.Id)) return new { ok = false, message = "You can't send messages here." };
        // Forward: the message (from a conversation the user can see) goes first, the note after it, as Discord sends them.
        if (hasParams && p.TryGetProperty("forward", out var fwd) && fwd.ValueKind == JsonValueKind.Object)
        {
            if (Channel(fwd, true) is not { } source || ReadString(fwd, true, "messageId") is not { } fid || content.Length > 4000)
                return new { ok = false, message = "That message can't be forwarded." };
            // A note starting with @silent sends the forward silently too (the note goes silent as well, which Discord's own skips).
            var note = DiscordRest.Silent(content);
            var forwarded = await c.Session.Rest.PostMessageAsync(c.Id, "", null, c.Guild, ct, forward: new DiscordRest.Forward(source.Id, DiscordRest.Id(fid), source.Guild), quiet: note.Silent).ConfigureAwait(false);
            if (forwarded.Error is null && note.Content.Length > 0) forwarded = await c.Session.Rest.PostMessageAsync(c.Id, content, null, c.Guild, ct).ConfigureAwait(false);
            return new { ok = forwarded.Error is null, message = forwarded.Error };
        }
        var files = TakeUploads(p, hasParams);
        if (content.Length > 4000) return new { ok = false, message = "Your message is too long." };
        if (DiscordRest.Silent(content).Content.Length == 0 && files.Count == 0) return new { ok = false, message = "Write something first." };
        var reply = await c.Session.Rest.PostMessageAsync(c.Id, content, ReadString(p, hasParams, "replyTo") is { } r ? DiscordRest.Id(r) : null, c.Guild, ct, files,
            replyPing: ReadBool(p, hasParams, "ping") != false).ConfigureAwait(false);
        return new { ok = reply.Error is null, message = reply.Error, sent = reply.Error is null ? reply.Body : null };
    }

    private static async Task<object> DiscordEditAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        if (Channel(p, hasParams) is not { } c || ReadString(p, hasParams, "messageId") is not { } id) return new { ok = false };
        var content = ReadString(p, hasParams, "content")?.Trim() ?? "";
        if (content.Length is 0 or > 4000) return new { ok = false, message = "Write something first." };
        var reply = await c.Session.Rest.EditMessageAsync(c.Id, DiscordRest.Id(id), content, ct).ConfigureAwait(false);
        return new { ok = reply.Error is null, message = reply.Error };
    }

    private static async Task<object> DiscordDeleteAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        if (Channel(p, hasParams) is not { } c || ReadString(p, hasParams, "messageId") is not { } id) return new { ok = false };
        var reply = await c.Session.Rest.DeleteMessageAsync(c.Id, DiscordRest.Id(id), ct).ConfigureAwait(false);
        return new { ok = reply.Error is null, message = reply.Error };
    }

    // Read at once here; Discord hears of it a moment later, the newest message only (DiscordSession.Ack).
    private static async Task<object> DiscordAckAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        if (Channel(p, hasParams) is not { } c || ReadString(p, hasParams, "messageId") is not { } id) return new { ok = false };
        // Mark Unread: messageId is the one before the message chosen; read stops there, here and on Discord.
        if (ReadBool(p, hasParams, "unread") == true)
        {
            var error = await c.Session.MarkUnreadAsync(c.Id, DiscordRest.Id(id), ct).ConfigureAwait(false);
            return new { ok = error is null, message = error, unread = c.Session.Cache.UnreadOf(c.Id) };
        }
        c.Session.Ack(c.Id, DiscordRest.Id(id));
        return new { ok = true, message = (string?)null, unread = c.Session.Cache.UnreadOf(c.Id) };
    }

    /// <summary>Create Thread from a message, or a new post in a forum or media channel (a title, its first message, tags).</summary>
    private static async Task<object> DiscordThreadAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        if (Channel(p, hasParams) is not { Guild: not null } c || ReadString(p, hasParams, "name")?.Trim() is not { Length: > 0 and <= 100 } name)
            return new { ok = false, message = "Give it a name first." };
        if (!c.Session.Cache.CanSend(c.Id)) return new { ok = false, message = "You can't start threads here." };
        var messageId = ReadString(p, hasParams, "messageId") is { } m ? DiscordRest.Id(m) : null;
        var content = ReadString(p, hasParams, "content")?.Trim();
        if (messageId is null && content is not { Length: > 0 and <= 4000 }) return new { ok = false, message = "Write the post first." };
        var tags = hasParams && p.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array
            ? t.EnumerateArray().Select(x => x.GetString()).OfType<string>().Take(5).ToList() : [];
        var reply = await c.Session.Rest.StartThreadAsync(c.Id, messageId, name, content, tags, ct).ConfigureAwait(false);
        if (reply.Body is { ValueKind: JsonValueKind.Object } thread && reply.Error is null)
        {
            c.Session.Cache.AddThreads(c.Guild, JsonSerializer.SerializeToElement(new[] { thread }));
            return new { ok = true, message = (string?)null, thread = new { id = thread.GetProperty("id").GetString(), name } };
        }
        return new { ok = false, message = reply.Error };
    }

    private static async Task<object> DiscordTypingAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        if (Channel(p, hasParams) is not { } c || !c.Session.Cache.CanSend(c.Id)) return new { ok = false };
        var reply = await c.Session.Rest.TypingAsync(c.Id, ct).ConfigureAwait(false);
        return new { ok = reply.Error is null };
    }

    private static object DiscordEmojis(JsonElement p, bool hasParams) =>
        DiscordSession.Current is { } session
            ? session.Cache.Emojis(ReadString(p, hasParams, "guildId") is { } g ? DiscordRest.Id(g) : null)
            : new { premium = false, guilds = Array.Empty<object>() };

    private static object DiscordView(JsonElement p, bool hasParams)
    {
        DiscordSession.Viewing = ReadString(p, hasParams, "channelId") is { Length: > 0 } id ? DiscordRest.Id(id) : null;
        // A conversation shown from what the room kept (or a hover's read) asks no messages: it subscribes here.
        if (Channel(p, hasParams) is { } c) SubscribeOpen(c);
        // Opened on screen: its notifications go, as Discord's CHANNEL_SELECT takes them.
        if (App.MainAppWindow?.WindowIsActive == true && DiscordSession.Viewing is { } open) DiscordSession.ClearNotices(open);
        return new { ok = true };
    }

    // Files on their way into a message: the page sends each in base64 pieces (the bridge takes 1 MB a call),
    // kept here in memory until discord.send takes them; 25 MB at most at once, never written to disk.
    private const int UploadLimit = 25 * 1024 * 1024;
    private static readonly Dictionary<string, (string Name, string Type, MemoryStream Data, long Started)> Uploads = [];

    private static object DiscordUpload(JsonElement p, bool hasParams)
    {
        lock (Uploads)
        {
            var id = ReadString(p, hasParams, "uploadId");
            if (ReadBool(p, hasParams, "cancel") == true)
            {
                if (id is not null && Uploads.Remove(id, out var gone)) gone.Data.Dispose();
                return new { ok = true };
            }
            byte[] chunk;
            try { chunk = Convert.FromBase64String(ReadString(p, hasParams, "data") ?? ""); }
            catch (FormatException) { return new { ok = false, message = "That file couldn't be read." }; }
            // A file nothing took or cancelled (the page reloaded while sending) goes after ten minutes, so it can't hold the 25 MB.
            var now = Environment.TickCount64;
            foreach (var stale in Uploads.Where(u => now - u.Value.Started > 600_000).Select(u => u.Key).ToList())
                if (Uploads.Remove(stale, out var old)) old.Data.Dispose();
            if (Uploads.Values.Sum(u => u.Data.Length) + chunk.Length > UploadLimit)
                return new { ok = false, message = "Files can be 25 MB at most." };
            if (id is null || !Uploads.TryGetValue(id, out var upload))
            {
                id = Guid.NewGuid().ToString("N");
                var name = Path.GetFileName(ReadString(p, hasParams, "name") ?? "file");
                upload = (name.Length is > 0 and <= 200 ? name : "file", ReadString(p, hasParams, "type") ?? "application/octet-stream", new MemoryStream(), now);
                Uploads[id] = upload;
            }
            upload.Data.Write(chunk);
            return new { ok = true, uploadId = id };
        }
    }

    private static List<DiscordRest.Upload> TakeUploads(JsonElement p, bool hasParams)
    {
        var files = new List<DiscordRest.Upload>();
        if (!hasParams || !p.TryGetProperty("uploads", out var ids) || ids.ValueKind != JsonValueKind.Array) return files;
        // Each file's alt text (Modify Attachment's Description), by its upload id.
        var described = p.TryGetProperty("descriptions", out var d) && d.ValueKind == JsonValueKind.Object ? d : default;
        lock (Uploads)
            foreach (var id in ids.EnumerateArray().Select(e => e.GetString()).OfType<string>().Take(10))
                if (Uploads.Remove(id, out var upload))
                {
                    var alt = described.ValueKind == JsonValueKind.Object && described.TryGetProperty(id, out var text) && text.ValueKind == JsonValueKind.String ? text.GetString()?.Trim() : null;
                    files.Add(new DiscordRest.Upload(upload.Name, upload.Type, upload.Data.ToArray(), alt is { Length: > 0 and <= 1024 } ? alt : null));
                    upload.Data.Dispose();
                }
        return files;
    }

    private static object DiscordVoice(JsonElement p, bool hasParams)
    {
        if (DiscordSession.Current is not { } session) return new { ok = false, message = "Discord isn't connected." };
        switch (ReadString(p, hasParams, "action"))
        {
            case "join":
                if (ReadString(p, hasParams, "channelId") is not { } channel) return new { ok = false };
                var guild = ReadString(p, hasParams, "guildId") is { Length: > 0 } g ? DiscordRest.Id(g) : null;
                var error = session.JoinVoice(guild, DiscordRest.Id(channel));
                return new { ok = error is null, message = error, voice = session.VoiceStatus() };
            case "leave":
                session.LeaveVoice();
                break;
            case "set":
                session.SetVoice(ReadBool(p, hasParams, "muted"), ReadBool(p, hasParams, "deafened"));
                break;
            case "user":
                if (ReadString(p, hasParams, "userId") is not { } user) return new { ok = false };
                session.SetAudioUser(DiscordRest.Id(user), ReadBool(p, hasParams, "muted"), ReadDouble(p, hasParams, "volume") is { } v ? (float)Math.Clamp(v, 0, 200) : null,
                    stream: ReadBool(p, hasParams, "stream") == true);
                break;
        }
        return new { ok = true, voice = session.VoiceStatus() };
    }

    /// <summary>The user panel's device menus: the microphones and speakers, the chosen ones and the volumes; with values,
    /// saves them and switches the call in progress (the input profile is discord.clientSettings).</summary>
    private static object DiscordAudio(JsonElement p, bool hasParams)
    {
        var input = ReadString(p, hasParams, "input");
        var output = ReadString(p, hasParams, "output");
        var inputVolume = ReadDouble(p, hasParams, "inputVolume");
        var outputVolume = ReadDouble(p, hasParams, "outputVolume");
        var soundboardVolume = ReadDouble(p, hasParams, "soundboardVolume");
        var soundboardMuted = ReadBool(p, hasParams, "soundboardMuted");
        if (input is not null || output is not null || inputVolume is not null || outputVolume is not null || soundboardVolume is not null || soundboardMuted is not null)
        {
            App.Services.Settings.UpdateProfile(s =>
            {
                if (input is not null) s.DiscordInputDevice = input.Length == 0 ? null : input;
                if (output is not null) s.DiscordOutputDevice = output.Length == 0 ? null : output;
                if (inputVolume is { } iv) s.DiscordInputVolume = (int)Math.Clamp(iv, 0, 200);
                if (outputVolume is { } ov) s.DiscordOutputVolume = (int)Math.Clamp(ov, 0, 200);
                if (soundboardVolume is { } sv) s.DiscordSoundboardVolume = (int)Math.Clamp(sv, 0, 100);
                if (soundboardMuted is { } sm) s.DiscordSoundboardMuted = sm;
            });
            if (input is not null) { DiscordSession.Current?.ReopenAudio(capture: true); ExoLauncher.Services.DiscordClient.Voice.MicTest.Reopen(); }
            if (output is not null) { DiscordSession.Current?.ReopenAudio(capture: false); ExoLauncher.Services.DiscordClient.Voice.MicTest.OutputChanged(); }
        }
        var settings = App.Services.Settings.Current;
        return new
        {
            inputs = ExoLauncher.Services.DiscordClient.Voice.AudioDevices.List(capture: true).Select(d => new { id = d.Id, name = d.Name }),
            outputs = ExoLauncher.Services.DiscordClient.Voice.AudioDevices.List(capture: false).Select(d => new { id = d.Id, name = d.Name }),
            input = settings.DiscordInputDevice,
            output = settings.DiscordOutputDevice,
            inputVolume = settings.DiscordInputVolume,
            outputVolume = settings.DiscordOutputVolume,
            soundboardVolume = settings.DiscordSoundboardVolume,
            soundboardMuted = settings.DiscordSoundboardMuted,
        };
    }

    private static async Task<object> DiscordRingAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        if (Channel(p, hasParams) is not { Guild: null } c) return new { ok = false };
        var reply = ReadBool(p, hasParams, "decline") == true
            ? await c.Session.DeclineCallAsync(c.Id, ct).ConfigureAwait(false)
            : await c.Session.RingAsync(c.Id, ct).ConfigureAwait(false);
        return new { ok = reply.Error is null, message = reply.Error };
    }

    // Friends and requests; restricted: the people blocked and ignored (Ignore & Block).
    private static object DiscordFriends() =>
        DiscordSession.Current is { } session ? new { friends = session.Cache.Friends(), restricted = session.Cache.Restricted() } : new { friends = Array.Empty<object>(), restricted = (object?)null };

    /// <summary>Friend requests: add or accept by id, ignore or cancel, remove, add by username; block, ignore and unignore; or open a DM with someone.</summary>
    private static async Task<object> DiscordFriendAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        if (DiscordSession.Current is not { } session) return new { ok = false, message = "Discord isn't connected." };
        var user = ReadString(p, hasParams, "userId");
        DiscordRest.Reply reply;
        switch (ReadString(p, hasParams, "action"))
        {
            case "accept" when user is not null:
                reply = await session.Rest.AcceptFriendAsync(user, ReadBool(p, hasParams, "confirm") == true, ct).ConfigureAwait(false);
                if (reply.Body is { ValueKind: JsonValueKind.Object } refused && refused.TryGetProperty("code", out var code) &&
                    code.ValueKind == JsonValueKind.Number && code.GetInt32() == DiscordRest.StrangerRequest)
                    return new { ok = false, stranger = true };
                break;
            case "remove" when user is not null: reply = await session.Rest.RemoveRelationshipAsync(user, ct).ConfigureAwait(false); break;
            case "block" when user is not null: reply = await session.Rest.BlockAsync(user, ct).ConfigureAwait(false); break;
            case "ignore" or "unignore" when user is not null:
                var ignore = ReadString(p, hasParams, "action") == "ignore";
                reply = await session.Rest.IgnoreAsync(user, ignore, ct).ConfigureAwait(false);
                if (reply.Error is null) session.Cache.SetIgnored(DiscordRest.Id(user), ignore);
                break;
            // The person menu's friend, block and ignore rows, from what Discord sent (no request).
            case "state" when user is not null:
                var (relationship, ignored, bot) = session.Cache.RelationshipOf(DiscordRest.Id(user));
                return new { ok = true, relationship, ignored, bot };
            case "add" when ReadString(p, hasParams, "username")?.Trim().TrimStart('@') is { Length: >= 2 and <= 32 } name:
                reply = await session.Rest.AddFriendAsync(name, ct).ConfigureAwait(false); break;
            // A DM with someone (userId), or a group with several friends (userIds, 2 to 9).
            case "dm" when user is not null || GroupOf(p, hasParams) is { Count: >= 2 }:
                reply = await session.Rest.OpenDmAsync(user is not null ? [user] : GroupOf(p, hasParams)!, ct).ConfigureAwait(false);
                var channel = reply.Body is { ValueKind: JsonValueKind.Object } body && body.TryGetProperty("id", out var id) ? id.GetString() : null;
                return new { ok = channel is not null, message = reply.Error, channelId = channel };
            default: return new { ok = false };
        }
        return new { ok = reply.Error is null, message = reply.Error };
    }

    // The friends picked for a new group DM: up to 9 ids, each a snowflake.
    private static List<string>? GroupOf(JsonElement p, bool hasParams) =>
        hasParams && p.TryGetProperty("userIds", out var ids) && ids.ValueKind == JsonValueKind.Array
            ? [.. ids.EnumerateArray().Select(i => i.GetString()).OfType<string>().Where(i => i.Length is > 0 and <= 20 && i.All(char.IsAsciiDigit)).Distinct().Take(9)]
            : null;

    private static async Task<object> DiscordMarkReadAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        if (DiscordSession.Current is not { } session || ReadString(p, hasParams, "guildId") is not { } guild) return new { ok = false };
        return new { ok = await session.MarkServerReadAsync(DiscordRest.Id(guild), ct).ConfigureAwait(false) is null };
    }

    private static async Task<object> DiscordSetStatusAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        if (DiscordSession.Current is not { } session) return new { ok = false };
        var error = await session.SetStatusAsync(ReadString(p, hasParams, "status") ?? "", ct).ConfigureAwait(false);
        return new { ok = error is null, message = error };
    }

    // The member list; at asks Discord for the rows from there on (they arrive as discord.members).
    private static object DiscordMembers(JsonElement p, bool hasParams)
    {
        if (DiscordSession.Current is not { } session || ReadString(p, hasParams, "guildId") is not { } raw)
            return new { items = Array.Empty<object>(), online = 0, total = 0, rows = 0 };
        var guild = DiscordRest.Id(raw);
        if (hasParams && ReadInt(p, "at", 0, 1_000_000, -1) is var at and >= 0) session.MembersAt(guild, at);
        return session.Cache.MemberList(guild);
    }

    /// <summary>The composer's @ list: members Exo has seen whose names match; ask also asks Discord for more (they arrive as discord.colors).</summary>
    private static object DiscordMentionable(JsonElement p, bool hasParams)
    {
        if (DiscordSession.Current is not { } session || ReadString(p, hasParams, "guildId") is not { } raw ||
            ReadString(p, hasParams, "query")?.Trim() is not { Length: > 0 and <= 32 } query)
            return new { users = Array.Empty<object>() };
        var guild = DiscordRest.Id(raw);
        // # in the message box: channels. @: people (Discord asked for more as you type), then roles.
        if (ReadBool(p, hasParams, "channels") == true) return new { users = Array.Empty<object>(), channels = session.Cache.FindChannels(guild, query, 8) };
        if (ReadBool(p, hasParams, "ask") == true) session.SearchMembers(guild, query);
        return new { users = session.Cache.FindMembers(guild, query, 8), roles = session.Cache.FindRoles(guild, query, 5) };
    }

    // Where each message is (channel, server, a thread's parent), for the rows of search, pins and the Inbox.
    private static Dictionary<string, object?> PlacesOf(DiscordSession session, JsonElement? messages)
    {
        var places = new Dictionary<string, object?>();
        void Add(JsonElement m)
        {
            if (m.ValueKind == JsonValueKind.Object && m.TryGetProperty("channel_id", out var c) && c.GetString() is { } id && !places.ContainsKey(id))
                places[id] = session.Cache.PlaceOf(id);
        }
        if (messages is { ValueKind: JsonValueKind.Array } list)
            foreach (var m in list.EnumerateArray())
                if (m.ValueKind == JsonValueKind.Array) foreach (var inner in m.EnumerateArray()) Add(inner);
                else Add(m);
        return places;
    }

    /// <summary>
    /// Name colors and server pictures for message authors; authors not seen yet are asked of Discord and arrive as
    /// discord.colors.
    /// </summary>
    private static object DiscordColors(JsonElement p, bool hasParams)
    {
        if (DiscordSession.Current is not { } session || ReadString(p, hasParams, "guildId") is not { } raw ||
            !p.TryGetProperty("userIds", out var ids) || ids.ValueKind != JsonValueKind.Array)
            return new { colors = new Dictionary<string, string?>() };
        var guild = DiscordRest.Id(raw);
        var people = ids.EnumerateArray().Select(i => i.GetString()).OfType<string>().Select(DiscordRest.Id).Take(200).ToList();
        var (colors, unknown) = session.Cache.Colors(guild, people, Environment.TickCount64);
        session.RequestMembers(guild, unknown);
        return new { colors, avatars = session.Cache.MemberAvatars(guild, people), icons = session.Cache.RoleIcons(guild, people) };
    }

    private static async Task<object> DiscordPinsAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        if (Channel(p, hasParams) is not { } c) return new { ok = false };
        if (ReadString(p, hasParams, "messageId") is { } id)
        {
            var set = await c.Session.Rest.PinAsync(c.Id, DiscordRest.Id(id), ReadBool(p, hasParams, "pin") != false, ct).ConfigureAwait(false);
            return new { ok = set.Error is null, message = set.Error };
        }
        var reply = await c.Session.Rest.PinsAsync(c.Id, ct).ConfigureAwait(false);
        // Discord sends { items: [{ message, pinned_at }], has_more }: the messages, newest pin first.
        JsonElement? messages = reply.Body is { ValueKind: JsonValueKind.Object } body && body.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array
            ? JsonSerializer.SerializeToElement(items.EnumerateArray().Where(i => i.TryGetProperty("message", out _)).Select(i => i.GetProperty("message")).ToList())
            : null;
        return new { ok = reply.Error is null, message = reply.Error, messages, places = PlacesOf(c.Session, messages) };
    }

    private static async Task<object> DiscordGifsAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        if (DiscordSession.Current is not { } session) return new { ok = false };
        var reply = await session.Rest.GifsAsync(ReadString(p, hasParams, "query"), ct).ConfigureAwait(false);
        return new { ok = reply.Error is null, gifs = reply.Body };
    }

    /// <summary>The camera from this client: start (size and frame rate), one encoded frame, or stop.</summary>
    private static object DiscordCamera(JsonElement p, bool hasParams)
    {
        if (DiscordSession.Current is not { } session) return new { ok = false, message = (string?)null };
        switch (ReadString(p, hasParams, "action"))
        {
            case "frame" when ReadString(p, hasParams, "data") is { Length: > 0 } data:
                byte[] frame;
                try { frame = Convert.FromBase64String(data); }
                catch (FormatException) { return new { ok = false, message = (string?)null }; }
                session.SendCameraFrame(frame, ReadDouble(p, hasParams, "timestamp") ?? 0);
                return new { ok = true, message = (string?)null };
            case "start":
                var why = session.StartCamera(ReadInt(p, "width", 160, 3840, 1280), ReadInt(p, "height", 90, 2160, 720), ReadInt(p, "fps", 5, 60, 30));
                return new { ok = why is null, message = why };
            case "stop":
                session.StopCamera();
                return new { ok = true, message = (string?)null };
            default:
                return new { ok = false, message = (string?)null };
        }
    }

    private static int ReadInt(JsonElement p, string name, int min, int max, int fallback) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? Math.Clamp(n, min, max) : fallback;

    /// <summary>
    /// Go Live from this client (docs/architecture/discord-video.md): what can be shared (screens and
    /// windows with their pictures and icons, the running game marked), a fresh picture of one (or of
    /// the stream itself while live), start (a source, height 0 for its own, frame rate, sound) and stop.
    /// </summary>
    private static async Task<object> DiscordGoLiveAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        if (DiscordSession.Current is not { } session) return new { ok = false, message = (string?)null };
        var action = ReadString(p, hasParams, "action");
        if (action == "stop")
        {
            session.StopGoLive();
            return new { ok = true, message = (string?)null };
        }
        if (action == "self")
        {
            session.WatchOwnStream(ReadBool(p, hasParams, "on") == true);
            return new { ok = true, message = (string?)null };
        }
        if (App.MainAppWindow?.StreamCapture is not { } addon)
            return new { ok = false, message = (string?)"Exo Capture isn't installed, so Exo can't share your screen." };
        var games = await Task.Run(RunningGames, ct).ConfigureAwait(true);
        switch (action)
        {
            case "sources":
                var sources = await Task.Run(() => GoLiveSources.List(games), ct).ConfigureAwait(true);
                Dictionary<string, string> pictures;
                try { pictures = await GoLiveCapture.PicturesAsync(addon, sources, 400, ct).ConfigureAwait(true); }
                catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
                {
                    AppLog.Info("Discord video: Go Live pictures failed: " + ex.Message);
                    pictures = [];
                }
                var icons = await Task.Run(() => sources.Select(s => GoLiveSources.Icon(s.Exe)).ToArray(), ct).ConfigureAwait(true);
                return new
                {
                    ok = true,
                    message = (string?)null,
                    sources = sources.Select((s, i) => new
                    {
                        id = s.Id, kind = s.Kind, title = s.Title, app = s.App, icon = icons[i],
                        thumb = pictures.GetValueOrDefault(s.Id), game = s.Game, height = s.Height,
                    }),
                };
            case "preview":
                var width = ReadInt(p, "width", 160, 1920, 640);
                if (ReadString(p, hasParams, "source") is not { } id)
                {
                    var live = await session.GoLivePreviewAsync(width).ConfigureAwait(true);
                    return new { ok = live is not null, message = (string?)null, thumb = live };
                }
                if (GoLiveSources.Find(id, games) is not { } source) return new { ok = false, message = (string?)"That window closed.", thumb = (string?)null };
                try
                {
                    var picture = (await GoLiveCapture.PicturesAsync(addon, new[] { source }, width, ct).ConfigureAwait(true)).GetValueOrDefault(source.Id);
                    return new { ok = picture is not null, message = (string?)null, thumb = picture };
                }
                catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
                {
                    return new { ok = false, message = (string?)null, thumb = (string?)null };
                }
            case "start":
                if (GoLiveSources.Find(ReadString(p, hasParams, "source"), games) is not { } chosen)
                    return new { ok = false, message = (string?)"That window closed. Pick it again." };
                var sound = ReadBool(p, hasParams, "sound") != false;
                var why = await session.StartGoLiveAsync(chosen, ReadInt(p, "height", 0, 2160, 1080), ReadInt(p, "fps", 15, 120, 30), sound, addon, ct).ConfigureAwait(true);
                return new { ok = why is null, message = why };
            default:
                return new { ok = false, message = (string?)null };
        }
    }

    /// <summary>Running library games: title and install folder, for the Game shortcut.</summary>
    private static List<(string Title, string Folder)> RunningGames()
    {
        try
        {
            return MainWindow.RunningLibraryGames().Keys
                .Select(id => App.Services.Library.Find(id))
                .Where(g => g is not null && !string.IsNullOrWhiteSpace(g.Path))
                .Select(g => (g!.Title, g.Path!))
                .ToList();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Debug("Discord video: running games unknown: " + ex.GetType().Name);
            return [];
        }
    }

    /// <summary>Go Live: watch someone's stream in the call this client is in, or stop watching; frames: whether the page shows call video now.</summary>
    private static object DiscordStream(JsonElement p, bool hasParams)
    {
        var action = ReadString(p, hasParams, "action");
        // The page says once, as it loads, often before Discord has signed in: never lost for want of a session.
        if (action == "decode")
        {
            // The page decodes H.265 in hardware: connections from now on offer to receive it.
            DiscordClient.Voice.VoiceConnection.DecodesH265 = ReadBool(p, hasParams, "hevc") == true;
            return new { ok = true, message = (string?)null };
        }
        if (DiscordSession.Current is not { } session) return new { ok = false };
        switch (action)
        {
            case "frames":
                session.WantFrames(ReadBool(p, hasParams, "on") == true);
                return new { ok = true, message = (string?)null };
            case "watch" when ReadString(p, hasParams, "userId") is { Length: > 0 and <= 24 } user && user.All(char.IsAsciiDigit):
                var why = session.WatchStream(user);
                return new { ok = why is null, message = why };
            case "stop":
                // One person's stream, or every stream with no one named.
                session.StopWatching(ReadString(p, hasParams, "userId") is { Length: > 0 and <= 24 } who && who.All(char.IsAsciiDigit) ? who : null);
                return new { ok = true, message = (string?)null };
            default:
                return new { ok = false, message = (string?)null };
        }
    }

    /// <summary>The account's favorite GIFs (the same list Discord shows everywhere): list, add one, or remove one.</summary>
    private static async Task<object> DiscordGifFavoritesAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        if (DiscordSession.Current is not { } session) return new { ok = false };
        var action = ReadString(p, hasParams, "action");
        List<DiscordFavoriteGifs.Gif>? gifs;
        if (action == "add")
        {
            var url = ReadString(p, hasParams, "url");
            var src = ReadString(p, hasParams, "src");
            if (!SafeGifUrl(url) || !SafeGifUrl(src)) return new { ok = false };
            var width = hasParams && p.TryGetProperty("width", out var w) && w.TryGetUInt32(out var wv) ? wv : 0u;
            var height = hasParams && p.TryGetProperty("height", out var h) && h.TryGetUInt32(out var hv) ? hv : 0u;
            var video = System.Text.RegularExpressions.Regex.IsMatch(src!, @"\.(mp4|webm)(\?|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            gifs = await DiscordFavoriteGifs.ChangeAsync(session.Rest, new DiscordFavoriteGifs.Gif(url!, video ? 2 : 1, src!, width, height, 0), null, ct).ConfigureAwait(false);
        }
        else if (action == "remove" && ReadString(p, hasParams, "url") is { Length: > 0 and <= 2048 } remove)
            gifs = await DiscordFavoriteGifs.ChangeAsync(session.Rest, null, remove, ct).ConfigureAwait(false);
        else gifs = await DiscordFavoriteGifs.ListAsync(session.Rest, ct).ConfigureAwait(false);
        return gifs is null ? new { ok = false, gifs = (object?)null } : new { ok = true, gifs = (object?)DiscordFavoriteGifs.ToJson(gifs) };
    }

    /// <summary>
    /// The emoji picker's Favorites and Frequently Used (Discord's own, from the account): list them, favorite or
    /// unfavorite one, or count one use (a custom emoji's id, or a Unicode emoji's Discord name).
    /// </summary>
    private static async Task<object> DiscordEmojiFavoritesAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        if (DiscordSession.Current is not { } session) return new { ok = false };
        var action = ReadString(p, hasParams, "action");
        var emoji = ReadString(p, hasParams, "emoji") is { Length: > 0 and <= 64 } e ? e : null;
        if (action == "use" && emoji is not null)
        {
            DiscordEmojiSettings.Use(session.Rest, emoji, ReadBool(p, hasParams, "reaction") == true);
            return new { ok = true };
        }
        var state = action is "add" or "remove" && emoji is not null
            ? await DiscordEmojiSettings.FavoriteAsync(session.Rest, emoji, action == "add", ct).ConfigureAwait(false)
            : await DiscordEmojiSettings.ReadAsync(session.Rest, ct).ConfigureAwait(false);
        return state is null
            ? new { ok = false }
            : new { ok = true, favorites = (object?)state.Favorites, frequent = (object?)state.Frequent, reactions = (object?)state.FrequentReactions };
    }

    private static bool SafeGifUrl(string? url) =>
        url is { Length: > 0 and <= 2048 } && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    /// <summary>Server actions: join with an invite (link or code), or leave a server.</summary>
    private static async Task<object> DiscordServerAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        if (DiscordSession.Current is not { } session) return new { ok = false };
        DiscordRest.Reply reply;
        switch (ReadString(p, hasParams, "action"))
        {
            case "join" when InviteCode(ReadString(p, hasParams, "invite")) is { } code:
                reply = await session.Rest.JoinInviteAsync(code, ct).ConfigureAwait(false);
                // The invite's server and channel (a group DM's invite has no server), which the room opens as Discord's app does.
                var accepted = reply.Body is { ValueKind: JsonValueKind.Object } body ? body : default;
                var guild = accepted.ValueKind == JsonValueKind.Object && accepted.TryGetProperty("guild", out var g) && g.TryGetProperty("id", out var gid) ? gid.GetString() : null;
                var invited = accepted.ValueKind == JsonValueKind.Object && accepted.TryGetProperty("channel", out var ch) && ch.TryGetProperty("id", out var cid) ? cid.GetString() : null;
                return new { ok = reply.Error is null, message = reply.Error, guildId = guild, channelId = invited };
            // A message's invite card: the server (or group DM), its counts and whether you're in it; invalid when
            // Discord says the invite is unknown or expired, so the card says so.
            case "invite" when InviteCode(ReadString(p, hasParams, "invite")) is { } info:
                return await DiscordInviteAsync(session, info, ct).ConfigureAwait(false);
            // A server tag's card: the server's public profile, which Discord shows for any server, joined or not.
            case "profile" when ReadString(p, hasParams, "guildId") is { } profiled:
                reply = await session.Rest.GuildProfileAsync(DiscordRest.Id(profiled), ct).ConfigureAwait(false);
                return new { ok = reply.Error is null, message = reply.Error, profile = reply.Body };
            // Set Channel Status (in the voice channel, up to 500 characters) or Clear Channel Status (Manage Channels too).
            case "voiceStatus" when ReadString(p, hasParams, "channelId") is { } statusChannel && (ReadString(p, hasParams, "text") ?? "").Trim() is { Length: <= 500 } status:
                if (!session.Cache.CanSetVoiceStatus(DiscordRest.Id(statusChannel), status.Length == 0)) return new { ok = false, message = "You can't set this channel's status." };
                reply = await session.Rest.VoiceStatusAsync(DiscordRest.Id(statusChannel), status, ct).ConfigureAwait(false);
                return new { ok = reply.Error is null, message = reply.Error };
            case "leave" when ReadString(p, hasParams, "guildId") is { } leave:
                reply = await session.Rest.LeaveGuildAsync(DiscordRest.Id(leave), ct).ConfigureAwait(false);
                return new { ok = reply.Error is null, message = reply.Error };
            // The rail's right-click menu: Mute Server (minutes: 15 minutes to 24 hours; absent, until turned back on),
            // Notification Settings (level 0 all, 1 only mentions, 2 nothing) and the two Suppress switches.
            case "mute" when ReadString(p, hasParams, "guildId") is { } muteGuild:
                var muteId = DiscordRest.Id(muteGuild);
                var mute = ReadBool(p, hasParams, "muted") ?? true;
                DateTimeOffset? until = ReadInt(p, "minutes", 0, 1440, 0) is > 0 and var minutes ? DateTimeOffset.UtcNow.AddMinutes(minutes) : null;
                reply = await session.Rest.GuildSettingsAsync(muteId, DiscordRest.MuteBody(mute, until), ct).ConfigureAwait(false);
                if (reply.Error is null) { session.Cache.SetMuted(muteId, mute, until); session.PublishBadge(); }
                return new { ok = reply.Error is null, message = reply.Error };
            case "notify" when ReadString(p, hasParams, "guildId") is { } notifyGuild:
                var notifyId = DiscordRest.Id(notifyGuild);
                var level = ReadInt(p, "level", 0, 2, -1);
                var everyone = ReadBool(p, hasParams, "suppressEveryone");
                var roles = ReadBool(p, hasParams, "suppressRoles");
                var hideMuted = ReadBool(p, hasParams, "hideMuted");
                var change = new System.Text.Json.Nodes.JsonObject();
                if (level >= 0) change["message_notifications"] = level;
                if (everyone is { } e) change["suppress_everyone"] = e;
                if (roles is { } r) change["suppress_roles"] = r;
                if (hideMuted is { } h) change["hide_muted_channels"] = h;
                if (change.Count == 0) return new { ok = false, message = (string?)null };
                reply = await session.Rest.GuildSettingsAsync(notifyId, change, ct).ConfigureAwait(false);
                if (reply.Error is null) { if (level >= 0) session.Cache.SetNotifyLevel(notifyId, level); session.Cache.SetSuppress(notifyId, everyone, roles, hideMuted); }
                return new { ok = reply.Error is null, message = reply.Error };
            case "showAll" when ReadString(p, hasParams, "guildId") is { } showAll:
                // Discord's Show All Channels: opt-in mode off (all) or on (followed only), the guild flag alone.
                var guildId = DiscordRest.Id(showAll);
                var now = session.Cache.GuildFlags(guildId);
                var all = ReadBool(p, hasParams, "on") ?? true;
                reply = await session.Rest.ChannelFlagsAsync(guildId, new Dictionary<string, int>(), all ? now & ~DiscordCache.OptInChannelsOn : now | DiscordCache.OptInChannelsOn, ct).ConfigureAwait(false);
                return new { ok = reply.Error is null, message = reply.Error };
            case "guide" when ReadString(p, hasParams, "guildId") is { } guide:
                return await DiscordGuideAsync(session, DiscordRest.Id(guide), ct).ConfigureAwait(false);
            case "prune" when ReadString(p, hasParams, "guildId") is { } prune:
                // Discord's Prune needs Kick Members and Manage Server, to count as well as to remove.
                if (!session.Cache.CanPrune(DiscordRest.Id(prune))) return new { ok = false, message = "You don't have permission to do that here." };
                var days = (int)(ReadDouble(p, hasParams, "days") ?? 7);
                reply = await session.Rest.PruneAsync(prune, days is 7 or 30 ? days : 7, ReadBool(p, hasParams, "countOnly") ?? true, ct).ConfigureAwait(false);
                var pruned = reply.Body is { ValueKind: JsonValueKind.Object } pb && pb.TryGetProperty("pruned", out var pn) && pn.TryGetInt32(out var pc) ? pc : (int?)null;
                return new { ok = reply.Error is null, message = reply.Error, pruned };
            case "members" when ReadString(p, hasParams, "guildId") is { } members:
                // The next chunk starts after the last member of the one before: their join time and id.
                var after = ReadDouble(p, hasParams, "afterJoined") is { } joined && ReadString(p, hasParams, "afterUser") is { Length: > 0 } afterUser
                    ? ((long)joined, afterUser) : ((long, string)?)null;
                reply = await session.Rest.MembersSearchAsync(members, Math.Clamp((int)(ReadDouble(p, hasParams, "sort") ?? 1), 1, 4),
                    ReadString(p, hasParams, "query") is { Length: <= 100 } query ? query : null, after, ct).ConfigureAwait(false);
                // The person menu on this page then knows each member's roles.
                if (reply.Body is { } found) session.Cache.RememberMembers(DiscordRest.Id(members), found);
                return new { ok = reply.Error is null, message = reply.Error, indexing = reply.Error == DiscordRest.MembersIndexing, members = reply.Body, roles = session.Cache.Roles(DiscordRest.Id(members)) };
            case "events" when ReadString(p, hasParams, "guildId") is { } events:
                return await DiscordEventsAsync(session, DiscordRest.Id(events), ct).ConfigureAwait(false);
            case "createEvent" when ReadString(p, hasParams, "guildId") is { } eventGuild && ReadString(p, hasParams, "name") is { Length: > 0 } eventName
                && DateTimeOffset.TryParse(ReadString(p, hasParams, "start"), out var start):
                var eventChannel = ReadString(p, hasParams, "channelId");
                var stage = eventChannel is { Length: > 0 } && session.Cache.ChannelType(DiscordRest.Id(eventChannel)) == 13;
                var end = DateTimeOffset.TryParse(ReadString(p, hasParams, "end"), out var endAt) ? endAt : (DateTimeOffset?)null;
                reply = await session.Rest.CreateEventAsync(eventGuild, ReadString(p, hasParams, "eventId"), eventName, ReadString(p, hasParams, "description"), start, end,
                    eventChannel, stage, ReadString(p, hasParams, "location"), ct).ConfigureAwait(false);
                return new { ok = reply.Error is null, message = reply.Error };
            case "rsvp" when ReadString(p, hasParams, "guildId") is { } eg && ReadString(p, hasParams, "eventId") is { } eventId:
                reply = await session.Rest.RsvpAsync(eg, eventId, ReadBool(p, hasParams, "interested") ?? true, ct).ConfigureAwait(false);
                return new { ok = reply.Error is null, message = reply.Error };
            case "eventStatus" when ReadString(p, hasParams, "guildId") is { } sg && ReadString(p, hasParams, "eventId") is { } statusEvent
                && (int)(ReadDouble(p, hasParams, "status") ?? 0) is var status and >= 2 and <= 4:
                reply = await session.Rest.EventStatusAsync(sg, statusEvent, status, ct).ConfigureAwait(false);
                return new { ok = reply.Error is null, message = reply.Error };
            case "ics" when ReadString(p, hasParams, "guildId") is { } ig && ReadString(p, hasParams, "eventId") is { } icsEvent:
                return await DiscordEventIcsAsync(session, ig, icsEvent, ct).ConfigureAwait(false);
            // A text or voice channel (in a category, or none), or a category of its own.
            case "createChannel" when ReadString(p, hasParams, "guildId") is { } inGuild && ReadString(p, hasParams, "name") is { Length: > 0 } name:
                var kind = ReadString(p, hasParams, "kind") switch { "voice" => 2, "category" => 4, _ => 0 };
                reply = await session.Rest.CreateChannelAsync(inGuild, name, kind, kind == 4 ? null : ReadString(p, hasParams, "parentId"), ct).ConfigureAwait(false);
                var created = reply.Body is { ValueKind: JsonValueKind.Object } made && made.TryGetProperty("id", out var mid) ? mid.GetString() : null;
                // Known at once, so the room opens it instead of waiting for the gateway's CHANNEL_CREATE.
                if (created is not null && reply.Body is { } channel) session.Cache.Apply("CHANNEL_CREATE", channel);
                return new { ok = reply.Error is null, message = reply.Error, channelId = created };
        }
        return new { ok = false, message = "That isn't an invite link." };
    }

    // A message's invite card: the server (or group DM), its counts and whether you're in it. invalid: Discord says the
    // invite is unknown or expired (10006), so the card says so; any other failure shows no card.
    private static async Task<object> DiscordInviteAsync(DiscordSession session, string code, CancellationToken ct)
    {
        var reply = await session.Rest.InviteInfoAsync(code, ct).ConfigureAwait(false);
        var body = reply.Body is { ValueKind: JsonValueKind.Object } b ? b : default;
        if (reply.Error is not null || body.ValueKind != JsonValueKind.Object)
            return new { ok = false, message = reply.Error, invalid = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("code", out var c) && c.TryGetInt32(out var n) && n == 10006 };
        static JsonElement Part(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : default;
        static string? Text(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        static int? Count(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : null;
        var guild = Part(body, "guild");
        var channel = Part(body, "channel");
        var features = guild.ValueKind == JsonValueKind.Object && guild.TryGetProperty("features", out var f) && f.ValueKind == JsonValueKind.Array
            ? f.EnumerateArray().Select(x => x.GetString()).ToHashSet() : [];
        var guildId = Text(guild, "id");
        return new
        {
            ok = true,
            invite = new
            {
                guildId, channelId = Text(channel, "id"), name = Text(guild, "name") ?? Text(channel, "name"), icon = guildId is not null && Text(guild, "icon") is { } icon ? $"https://cdn.discordapp.com/icons/{guildId}/{icon}.webp?size=128&quality=lossless" : null,
                verified = features.Contains("VERIFIED"), partnered = features.Contains("PARTNERED"),
                online = Count(body, "approximate_presence_count"), members = Count(body, "approximate_member_count"),
                joined = guildId is not null && session.Cache.InGuild(guildId),
            },
        };
    }

    // Discord's Server Guide: its settings with the resources the user can see, each previewed by its
    // description or else the channel's first message (as Discord's client does), and the server's counts.
    private static async Task<object> DiscordGuideAsync(DiscordSession session, string guildId, CancellationToken ct)
    {
        var reply = await session.Rest.ServerGuideAsync(guildId, ct).ConfigureAwait(false);
        var counts = await session.Rest.GuildCountsAsync(guildId, ct).ConfigureAwait(false);
        int? Count(string name) => counts.Body is { ValueKind: JsonValueKind.Object } c && c.TryGetProperty(name, out var n) && n.TryGetInt32(out var v) ? v : null;
        var resources = new List<object>();
        if (reply.Body is { ValueKind: JsonValueKind.Object } body && body.TryGetProperty("resource_channels", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var r in list.EnumerateArray())
            {
                var channelId = r.TryGetProperty("channel_id", out var cid) ? cid.GetString() : null;
                if (channelId is null || !session.Cache.CanSee(channelId)) continue;
                var description = r.TryGetProperty("description", out var d) ? d.GetString() : null;
                JsonElement? preview = null;
                if (string.IsNullOrEmpty(description) &&
                    (await session.Rest.FirstMessageAsync(channelId, ct).ConfigureAwait(false)).Body is { ValueKind: JsonValueKind.Array } first && first.GetArrayLength() > 0)
                    preview = first[0];
                var icon = r.TryGetProperty("icon", out var i) ? i.GetString() : null;
                resources.Add(new
                {
                    channelId,
                    title = r.TryGetProperty("title", out var t) ? t.GetString() : null,
                    description,
                    icon = icon is null ? null : $"https://cdn.discordapp.com/resource-channels/{channelId}/{icon}.png?size=256",
                    preview,
                });
            }
        return new { ok = reply.Error is null, message = reply.Error, guide = reply.Body, resources, online = Count("approximate_presence_count"), total = Count("approximate_member_count") };
    }

    // Discord's "Download ICS file": the event as a calendar file in the user's Downloads folder.
    private static async Task<object> DiscordEventIcsAsync(DiscordSession session, string guildId, string eventId, CancellationToken ct)
    {
        var reply = await session.Rest.EventAsync(guildId, eventId, ct).ConfigureAwait(false);
        if (reply.Body is not { ValueKind: JsonValueKind.Object } e) return new { ok = false, message = reply.Error };
        string Text(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        static string Escape(string s) => s.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\r", "").Replace("\n", "\\n");
        static string Stamp(DateTimeOffset t) => t.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        if (!DateTimeOffset.TryParse(Text("scheduled_start_time"), out var start)) return new { ok = false };
        var end = DateTimeOffset.TryParse(Text("scheduled_end_time"), out var e2) ? e2 : start.AddHours(1);
        var location = e.TryGetProperty("entity_metadata", out var m) && m.ValueKind == JsonValueKind.Object && m.TryGetProperty("location", out var loc) ? loc.GetString() ?? ""
            : Text("channel_id") is { Length: > 0 } voice ? session.Cache.ChannelName(voice) ?? "" : "";
        var ics = string.Join("\r\n",
            "BEGIN:VCALENDAR", "VERSION:2.0", "PRODID:-//Exo Launcher//Discord//EN", "BEGIN:VEVENT",
            $"UID:{eventId}@discord.com", $"DTSTAMP:{Stamp(DateTimeOffset.UtcNow)}", $"DTSTART:{Stamp(start)}", $"DTEND:{Stamp(end)}",
            $"SUMMARY:{Escape(Text("name"))}", $"DESCRIPTION:{Escape(Text("description"))}", $"LOCATION:{Escape(location)}",
            $"URL:https://discord.com/events/{guildId}/{eventId}", "END:VEVENT", "END:VCALENDAR", "");
        var name = string.Concat(Text("name").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        if (name.Length == 0) name = "event";
        // Beside a file of the same name, as a browser saves a download ("name (2).ics"), never over it.
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        var path = Path.Combine(folder, name + ".ics");
        for (var n = 2; File.Exists(path); n++) path = Path.Combine(folder, $"{name} ({n}).ics");
        await File.WriteAllTextAsync(path, ics, ct).ConfigureAwait(false);
        // The path stays here: the room only says it's in Downloads.
        return new { ok = true };
    }

    // A DM's ×, Add Friends to DM, and its right-click menu's Mark As Read and Mute.
    private static async Task<object> DiscordDmActionAsync(DiscordChannelRef c, JsonElement p, bool hasParams, CancellationToken ct)
    {
        switch (ReadString(p, hasParams, "action"))
        {
            case "read":
                if (c.Session.Cache.LastMessageOf(c.Id) is { } last) c.Session.Ack(c.Id, last);
                return new { ok = true };
            case "mute":
                var mute = ReadBool(p, hasParams, "muted") ?? !c.Session.Cache.ChannelMuted(c.Id);
                DateTimeOffset? until = ReadInt(p, "minutes", 0, 1440, 0) is > 0 and var minutes ? DateTimeOffset.UtcNow.AddMinutes(minutes) : null;
                var set = await c.Session.Rest.MuteDmAsync(c.Id, mute, until, ct).ConfigureAwait(false);
                if (set.Error is null) { c.Session.Cache.SetMuted(c.Id, mute, until); c.Session.PublishBadge(); }
                return new { ok = set.Error is null, message = set.Error, muted = mute };
            case "close":
                var closed = await c.Session.Rest.CloseDmAsync(c.Id, ct).ConfigureAwait(false);
                return new { ok = closed.Error is null, message = closed.Error };
            // A message request (or Discord's spam): Accept makes it a DM; Ignore takes it away.
            case "accept" or "ignore" when c.Session.Cache.IsRequest(c.Id):
                var accept = ReadString(p, hasParams, "action") == "accept";
                var answered = accept ? await c.Session.Rest.AcceptRequestAsync(c.Id, ct).ConfigureAwait(false) : await c.Session.Rest.IgnoreRequestAsync(c.Id, ct).ConfigureAwait(false);
                if (answered.Error is null)
                {
                    c.Session.Cache.Answer(c.Id, accept);
                    DiscordSession.Publish("discord.changed", new { guildId = (string?)null });
                }
                return new { ok = answered.Error is null, message = answered.Error };
            // A group DM: anyone in it may Edit Group (name, icon); its owner alone Remove From Group and Make Group Owner.
            case "editGroup" when c.Guild is null && c.Session.Cache.RecipientsOf(c.Id).Count > 1:
            case "removeMember" or "makeOwner" when c.Guild is null && c.Session.Cache.OwnsGroup(c.Id):
                return await DiscordGroupAsync(c, ReadString(p, hasParams, "action")!, p, hasParams, ct).ConfigureAwait(false);
            case "add" when ReadString(p, hasParams, "userId") is { } user:
                var people = c.Session.Cache.RecipientsOf(c.Id);
                var added = await c.Session.Rest.AddToDmAsync(c.Id, people, DiscordRest.Id(user), people.Count > 1, ct).ConfigureAwait(false);
                var id = added.Body is { ValueKind: JsonValueKind.Object } b && b.TryGetProperty("id", out var i) ? i.GetString() : c.Id;
                return new { ok = added.Error is null, message = added.Error, channelId = id };
        }
        return new { ok = false };
    }

    // Browse Channels' checkbox and Follow Category. With opt-in mode off, unfollowing turns it on and
    // follows every other channel first, so only this one leaves the sidebar (Discord's add_many_and_enable_guild).
    // A channel shows when it or its category is followed, so unfollowing one in a followed category unfollows
    // the category and keeps its siblings followed; unfollowing a category unfollows its channels too.
    private static async Task<object> DiscordOptInAsync(DiscordSession session, string guildId, string channelId, bool optedIn, CancellationToken ct)
    {
        static int With(int flags, bool on) => on ? flags | DiscordCache.OptInEnabled : flags & ~DiscordCache.OptInEnabled;
        var cache = session.Cache;
        var flags = new Dictionary<string, int> { [channelId] = With(cache.FlagsOf(channelId), optedIn) };
        int? guildFlags = null;
        var guildNow = cache.GuildFlags(guildId);
        var listed = cache.ListedChannels(guildId);
        var category = cache.IsCategory(channelId);
        var parent = cache.ParentOf(channelId);
        if ((guildNow & DiscordCache.OptInChannelsOn) == 0)
        {
            if (optedIn) return new { ok = true };
            foreach (var (id, p, f) in listed)
                if (id != channelId) flags[id] = With(f, true);
            guildFlags = guildNow | DiscordCache.OptInChannelsOn;
        }
        if (!optedIn && category)
            foreach (var (id, p, f) in listed)
                if (p == channelId) flags[id] = With(f, false);
        if (!optedIn && !category && parent is not null && (cache.FlagsOf(parent) & DiscordCache.OptInEnabled) != 0)
        {
            flags[parent] = With(cache.FlagsOf(parent), false);
            foreach (var (id, p, f) in listed)
                if (p == parent && id != channelId) flags[id] = With(f, true);
        }
        var reply = await session.Rest.ChannelFlagsAsync(guildId, flags, guildFlags, ct).ConfigureAwait(false);
        return new { ok = reply.Error is null, message = reply.Error };
    }

    // The server's events, each marked with whether the user said they're interested.
    private static async Task<object> DiscordEventsAsync(DiscordSession session, string guildId, CancellationToken ct)
    {
        var reply = await session.Rest.EventsAsync(guildId, ct).ConfigureAwait(false);
        var events = new List<object>();
        if (reply.Body is { ValueKind: JsonValueKind.Array } list)
        {
            // Interested (response 1) in the event itself, not only one time of a recurring one.
            var rsvps = await session.Rest.MyEventRsvpsAsync(guildId, ct).ConfigureAwait(false);
            var interested = rsvps.Body is { ValueKind: JsonValueKind.Array } mine
                ? mine.EnumerateArray().Where(r => r.TryGetProperty("response", out var answer) && answer.ValueKind == JsonValueKind.Number && answer.GetInt32() == 1
                    && (!r.TryGetProperty("guild_scheduled_event_exception_id", out var exception) || exception.ValueKind == JsonValueKind.Null))
                    .Select(r => r.TryGetProperty("guild_scheduled_event_id", out var e) ? e.GetString() : null).OfType<string>().ToHashSet()
                : [];
            foreach (var e in list.EnumerateArray())
                events.Add(new { @event = e, interested = e.TryGetProperty("id", out var id) && id.GetString() is { } eventId && interested.Contains(eventId) });
        }
        return new { ok = reply.Error is null, message = reply.Error, events };
    }

    // discord.gg/abc, discord.com/invite/abc or just abc.
    private static string? InviteCode(string? invite)
    {
        var text = invite?.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(text)) return null;
        var code = text.Contains('/') ? text[(text.LastIndexOf('/') + 1)..] : text;
        return code.Length is > 1 and <= 32 && code.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') ? code : null;
    }

    private static async Task<object> DiscordProfileAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        if (DiscordSession.Current is not { } session || ReadString(p, hasParams, "userId") is not { } user) return new { ok = false };
        var guild = ReadString(p, hasParams, "guildId") is { Length: > 0 } g ? DiscordRest.Id(g) : null;
        if (ReadBool(p, hasParams, "mutualFriends") == true)
        {
            var friends = await session.Rest.MutualFriendsAsync(DiscordRest.Id(user), ct).ConfigureAwait(false);
            return new { ok = friends.Error is null, message = friends.Error, friends = friends.Body };
        }
        if (ReadString(p, hasParams, "note") is { } text)
        {
            if (text.Length > 256) return new { ok = false, message = "Notes can be up to 256 characters." };
            var saved = await session.Rest.SaveNoteAsync(DiscordRest.Id(user), text, ct).ConfigureAwait(false);
            return new { ok = saved.Error is null, message = saved.Error };
        }
        var noteReply = session.Rest.NoteAsync(DiscordRest.Id(user), ct);
        var reply = await session.Rest.ProfileAsync(DiscordRest.Id(user), guild, ct).ConfigureAwait(false);
        var note = await noteReply.ConfigureAwait(false);
        return new
        {
            ok = reply.Error is null,
            message = reply.Error,
            profile = reply.Error is null ? reply.Body : null,
            extras = session.Cache.ProfileOn(DiscordRest.Id(user), guild, reply.Body),
            note = note.Body is { ValueKind: JsonValueKind.Object } n && n.TryGetProperty("note", out var nt) ? nt.GetString() : null,
        };
    }

    private static async Task<object> DiscordThreadsAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        if (Channel(p, hasParams) is not { Guild: { } guild } c) return new { ok = false };
        var reply = await c.Session.Rest.ThreadsAsync(c.Id, ReadBool(p, hasParams, "archived") == true, ReadInt(p, "offset", 0, 10_000, 0), ct).ConfigureAwait(false);
        if (reply.Body is { ValueKind: JsonValueKind.Object } body && body.TryGetProperty("threads", out var threads))
        {
            c.Session.Cache.AddThreads(guild, threads);
            return new
            {
                ok = true,
                threads,
                firstMessages = body.TryGetProperty("first_messages", out var first) ? first : default(JsonElement?),
                hasMore = body.TryGetProperty("has_more", out var more) && more.ValueKind == JsonValueKind.True,
                tags = c.Session.Cache.ForumTags(c.Id),
            };
        }
        return new { ok = false, message = reply.Error };
    }


    private static async Task<object> DiscordSendStickerAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        if (Channel(p, hasParams) is not { } c || ReadString(p, hasParams, "stickerId") is not { } sticker || !c.Session.Cache.CanSend(c.Id)) return new { ok = false };
        var reply = await c.Session.Rest.PostStickerAsync(c.Id, DiscordRest.Id(sticker), ct).ConfigureAwait(false);
        return new { ok = reply.Error is null, message = reply.Error, sent = reply.Error is null ? reply.Body : null };
    }

    /// <summary>A server channel's bell and right-click menu: mute, collapse, invite, mark as read, delete.</summary>
    private static async Task<object> DiscordChannelActionAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        if (Channel(p, hasParams) is not { } c) return new { ok = false };
        if (c.Guild is null) return await DiscordDmActionAsync(c, p, hasParams, ct).ConfigureAwait(false);
        var guild = c.Guild;
        switch (ReadString(p, hasParams, "action"))
        {
            case "mute":
                var mute = ReadBool(p, hasParams, "muted") ?? !c.Session.Cache.ChannelMuted(c.Id);
                // minutes: Discord's timed mutes (15 minutes to 24 hours); absent, until turned back on.
                DateTimeOffset? until = ReadInt(p, "minutes", 0, 1440, 0) is > 0 and var minutes ? DateTimeOffset.UtcNow.AddMinutes(minutes) : null;
                var set = await c.Session.Rest.MuteChannelAsync(guild, c.Id, mute, ct, until).ConfigureAwait(false);
                if (set.Error is null) c.Session.Cache.SetMuted(c.Id, mute, until);
                return new { ok = set.Error is null, message = set.Error, muted = mute };
            case "notify" when ReadInt(p, "level", 0, 3, -1) is >= 0 and var level:
                var notified = await c.Session.Rest.ChannelNotifyAsync(guild, c.Id, level, ct).ConfigureAwait(false);
                if (notified.Error is null) c.Session.Cache.SetNotifyLevel(c.Id, level);
                return new { ok = notified.Error is null, message = notified.Error };
            case "collapse":
                var collapse = ReadBool(p, hasParams, "collapsed") ?? !c.Session.Cache.Collapsed(c.Id);
                var folded = await c.Session.Rest.CollapseAsync(guild, c.Id, collapse, ct).ConfigureAwait(false);
                if (folded.Error is null) c.Session.Cache.SetCollapsed(c.Id, collapse);
                return new { ok = folded.Error is null, message = folded.Error, collapsed = collapse };
            case "optin":
                return await DiscordOptInAsync(c.Session, guild, c.Id, ReadBool(p, hasParams, "optedIn") ?? true, ct).ConfigureAwait(false);
            // Mark As Read: the channel's dot and mentions go at once, and Discord hears of it as with any read (DiscordSession.Ack).
            case "read":
                if (c.Session.Cache.LastMessageOf(c.Id) is { } last) c.Session.Ack(c.Id, last);
                return new { ok = true };
            case "delete":
                var deleted = await c.Session.Rest.DeleteChannelAsync(c.Id, ct).ConfigureAwait(false);
                return new { ok = deleted.Error is null, message = deleted.Error };
            // An invite to the channel: 7 days and no use limit, or Edit Invite Link's choices (Discord's only).
            case "invite":
                var maxAge = (int)(ReadDouble(p, hasParams, "maxAge") ?? 604800);
                var maxUses = (int)(ReadDouble(p, hasParams, "maxUses") ?? 0);
                if (maxAge is not (1800 or 3600 or 21600 or 43200 or 86400 or 604800 or 0) || maxUses is not (0 or 1 or 5 or 10 or 25 or 50 or 100))
                    return new { ok = false, message = "Pick one of Discord's choices." };
                var invite = await c.Session.Rest.CreateInviteAsync(c.Id, maxAge, maxUses, ReadBool(p, hasParams, "temporary") == true, ct).ConfigureAwait(false);
                var code = invite.Body is { ValueKind: JsonValueKind.Object } body && body.TryGetProperty("code", out var cd) ? cd.GetString() : null;
                return new { ok = code is not null, message = invite.Error, url = code is null ? null : "https://discord.gg/" + code };
        }
        return new { ok = false };
    }

    // The group owner's actions; a new icon is a PNG, JPEG, GIF or WebP data URI of up to 10 MB, as Discord takes one.
    private static async Task<object> DiscordGroupAsync(DiscordChannelRef c, string action, JsonElement p, bool hasParams, CancellationToken ct)
    {
        var member = ReadString(p, hasParams, "userId") is { } u ? DiscordRest.Id(u) : null;
        DiscordRest.Reply reply;
        switch (action)
        {
            case "editGroup":
                var name = ReadString(p, hasParams, "name")?.Trim();
                if (name is { Length: > 100 }) return new { ok = false, message = "Group names can be up to 100 characters." };
                var icon = ReadString(p, hasParams, "icon");
                if (icon is not null && (icon.Length > 14_000_000 || !System.Text.RegularExpressions.Regex.IsMatch(icon, "^data:image/(png|jpeg|gif|webp);base64,")))
                    return new { ok = false, message = "Pick a PNG, JPEG, GIF or WebP picture." };
                reply = await c.Session.Rest.EditGroupAsync(c.Id, name, icon, ReadBool(p, hasParams, "removeIcon") == true, ct).ConfigureAwait(false);
                break;
            case "removeMember" when member is not null && member != c.Session.Cache.Me?.Id:
                reply = await c.Session.Rest.RemoveFromGroupAsync(c.Id, member, ct).ConfigureAwait(false);
                break;
            case "makeOwner" when member is not null && c.Session.Cache.RecipientsOf(c.Id).Contains(member):
                reply = await c.Session.Rest.GroupOwnerAsync(c.Id, member, ct).ConfigureAwait(false);
                break;
            default:
                return new { ok = false };
        }
        return new { ok = reply.Error is null, message = reply.Error };
    }

    private sealed record DiscordChannelRef(DiscordSession Session, string Id, string? Guild);

    // The channel named in the call, when the client is connected and the user can see it.
    // An open channel of a server: its live messages, typing and member list (a thread's member list is its channel's).
    private static void SubscribeOpen(DiscordChannelRef c)
    {
        if (c.Guild is not null) c.Session.Subscribe(c.Guild, c.Session.Cache.IsThread(c.Id) && c.Session.Cache.ParentOf(c.Id) is { } parent ? parent : c.Id);
    }

    private static DiscordChannelRef? Channel(JsonElement p, bool hasParams)
    {
        if (DiscordSession.Current is not { } session || ReadString(p, hasParams, "channelId") is not { } raw) return null;
        var id = DiscordRest.Id(raw);
        try { return new DiscordChannelRef(session, id, session.Cache.GuildOf(id)); }
        catch (UnauthorizedAccessException) { return null; }
    }
}
