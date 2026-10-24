using System.Globalization;
using System.Text.Json;
using ExoLauncher.Services.DiscordClient;

namespace ExoLauncher.Services;

/// <summary>
/// The Discord room's search bar, Inbox (For You, Unreads, Mentions), reactions (who reacted, super
/// reactions, taking someone's off) and text file previews. Every channel is one the user can see.
/// </summary>
public sealed partial class WebHostBridge
{
    private static async Task<object> DiscordSearchAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        if (Channel(p, hasParams) is not { } c || !hasParams || !p.TryGetProperty("filters", out var f) || f.ValueKind != JsonValueKind.Object)
            return new { ok = false };
        DiscordRest.SearchFilters filters;
        try { filters = SearchFiltersOf(f); }
        catch (ArgumentException) { return new { ok = false, message = "Discord can't search for that." }; }
        // Only channels the user can see narrow a search (in:).
        if (filters.ChannelIds.Any(id => !c.Session.Cache.CanSee(id))) return new { ok = false, message = "Discord can't search for that." };
        var reply = await c.Session.Rest.SearchAsync(c.Guild, c.Id, filters, ct).ConfigureAwait(false);
        JsonElement? found = null;
        if (reply.Body is { ValueKind: JsonValueKind.Object } body)
        {
            // Results in threads (archived ones too) then open in their channel like any thread Exo knows.
            if (c.Guild is not null && body.TryGetProperty("threads", out var threads)) c.Session.Cache.AddThreads(c.Guild, threads);
            if (body.TryGetProperty("messages", out var messages)) found = messages;
        }
        return new { ok = reply.Error is null, message = reply.Error, result = reply.Body, places = PlacesOf(c.Session, found) };
    }

    /// <summary>The search bar's filters as the page sends them; an id that isn't one throws.</summary>
    internal static DiscordRest.SearchFilters SearchFiltersOf(JsonElement f)
    {
        List<string> List(string name) => f.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? [.. v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Take(10)] : [];
        string? Text(string name) => f.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        string? IdOf(string name) => Text(name) is { } id ? DiscordRest.Id(id) : null;
        return new DiscordRest.SearchFilters(
            Text("content"),
            [.. List("authorIds").Select(DiscordRest.Id)],
            [.. List("mentions").Select(DiscordRest.Id)],
            List("has"),
            [.. List("channelIds").Select(DiscordRest.Id)],
            List("authorTypes"),
            f.TryGetProperty("pinned", out var pin) && pin.ValueKind is JsonValueKind.True or JsonValueKind.False ? pin.GetBoolean() : null,
            IdOf("minId"),
            IdOf("maxId"),
            Text("sort") ?? "newest",
            f.TryGetProperty("offset", out var o) && o.TryGetInt32(out var offset) ? Math.Clamp(offset, 0, DiscordRest.SearchMaxOffset) : 0);
    }

    /// <summary>The Inbox's Mentions: a page older than before, in one server or all, with role and @everyone mentions or not; or the × on one.</summary>
    private static async Task<object> DiscordMentionsAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        if (DiscordSession.Current is not { } session) return new { ok = false };
        if (ReadString(p, hasParams, "remove") is { } remove)
        {
            var removed = await session.Rest.RemoveMentionAsync(DiscordRest.Id(remove), ct).ConfigureAwait(false);
            return new { ok = removed.Error is null, message = removed.Error };
        }
        string? Id(string name) => ReadString(p, hasParams, name) is { } id ? DiscordRest.Id(id) : null;
        var reply = await session.Rest.MentionsAsync(Id("before"), Id("guildId"), ReadBool(p, hasParams, "roles") != false, ReadBool(p, hasParams, "everyone") != false, ct).ConfigureAwait(false);
        return new { ok = reply.Error is null, message = reply.Error, messages = reply.Body, places = PlacesOf(session, reply.Body) };
    }

    /// <summary>
    /// Bookmarks and reminders (Discord's saved messages): what is saved (state: the reminders and the bookmarked ids),
    /// a page of bookmarks, or saving (a reminder when due is given) and removing one.
    /// </summary>
    private static async Task<object> DiscordSavedAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        if (DiscordSession.Current is not { } session) return new { ok = false, message = "Discord isn't connected." };
        switch (ReadString(p, hasParams, "action"))
        {
            case "state":
                var state = await session.Rest.SavedAsync(ct).ConfigureAwait(false);
                var saved = state.Body is { ValueKind: JsonValueKind.Object } s ? s : default;
                var reminders = saved.ValueKind == JsonValueKind.Object && saved.TryGetProperty("reminders", out var r) && r.ValueKind == JsonValueKind.Array ? r : default(JsonElement?);
                session.RemindAt(reminders);
                return new
                {
                    ok = state.Error is null || NothingSaved(state), message = NothingSaved(state) ? null : state.Error, reminders,
                    bookmarkIds = saved.ValueKind == JsonValueKind.Object && saved.TryGetProperty("bookmark_ids", out var ids) ? ids : default(JsonElement?),
                    places = PlacesOf(session, SavedMessages(reminders)),
                };
            case "bookmarks":
                var before = DateTimeOffset.TryParse(ReadString(p, hasParams, "before"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var b) ? b : (DateTimeOffset?)null;
                var page = await session.Rest.BookmarksAsync(before, ct).ConfigureAwait(false);
                var body = page.Body is { ValueKind: JsonValueKind.Object } o ? o : default;
                var results = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("results", out var list) && list.ValueKind == JsonValueKind.Array ? list : default(JsonElement?);
                return new
                {
                    ok = page.Error is null || NothingSaved(page), message = NothingSaved(page) ? null : page.Error, results,
                    hasMore = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("has_more", out var more) && more.ValueKind == JsonValueKind.True,
                    places = PlacesOf(session, SavedMessages(results)),
                };
            case "save" or "remove" when Channel(p, hasParams) is { } c && ReadString(p, hasParams, "messageId") is { } raw:
                if (ReadString(p, hasParams, "action") == "remove")
                {
                    var removed = await c.Session.Rest.UnsaveMessageAsync(c.Id, DiscordRest.Id(raw), ct).ConfigureAwait(false);
                    return new { ok = removed.Error is null, message = removed.Error };
                }
                // A reminder: up to a year ahead, as Discord's picker allows.
                DateTimeOffset? due = ReadDouble(p, hasParams, "dueAt") is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds((long)ms) : null;
                if (due is { } at && (at < DateTimeOffset.UtcNow.AddMinutes(-1) || at > DateTimeOffset.UtcNow.AddDays(366))) return new { ok = false, message = "Pick a time in the next year." };
                var put = await c.Session.Rest.SaveMessageAsync(c.Id, DiscordRest.Id(raw), due, ct).ConfigureAwait(false);
                return new { ok = put.Error is null, message = put.Error };
            default:
                return new { ok = false };
        }
    }

    // Someone who never saved a message has no saved messages yet: Discord answers Unknown Saved Messages (a 10xxx code).
    private static bool NothingSaved(DiscordRest.Reply reply) =>
        reply.Error is not null && reply.Body is { ValueKind: JsonValueKind.Object } body && body.TryGetProperty("code", out var code)
        && code.TryGetInt32(out var n) && n is >= 10000 and < 11000;

    // The messages inside saved messages ({ message, save_data }), for where each one is.
    private static JsonElement? SavedMessages(JsonElement? saved) =>
        saved is { ValueKind: JsonValueKind.Array } list
            ? JsonSerializer.SerializeToElement(list.EnumerateArray().Where(i => i.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.Object).Select(i => i.GetProperty("message")).ToList())
            : null;

    /// <summary>The Inbox's For You: Discord's notification center, a page older than before.</summary>
    private static async Task<object> DiscordForYouAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        if (DiscordSession.Current is not { } session) return new { ok = false };
        var reply = await session.Rest.ForYouAsync(ReadString(p, hasParams, "before") is { } b ? DiscordRest.Id(b) : null, ct).ConfigureAwait(false);
        var body = reply.Body is { ValueKind: JsonValueKind.Object } o ? o : default;
        return new
        {
            ok = reply.Error is null,
            message = reply.Error,
            items = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("items", out var items) ? items : default(JsonElement?),
            hasMore = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("has_more", out var more) && more.ValueKind == JsonValueKind.True,
        };
    }

    /// <summary>The Inbox's Unreads: the unread channels of servers, newest first.</summary>
    private static object DiscordUnreads() =>
        DiscordSession.Current is { } session ? new { channels = session.Cache.Unreads(50) } : new { channels = new List<object>() };

    /// <summary>
    /// A reaction: add or take back the user's own (a super reaction needs Nitro), or with userId take someone
    /// else's off (Manage Messages).
    /// </summary>
    private static async Task<object> DiscordReactAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        if (Channel(p, hasParams) is not { } c || ReadString(p, hasParams, "messageId") is not { } id || ReadString(p, hasParams, "emoji") is not { Length: > 0 and <= 80 } emoji)
            return new { ok = false };
        var burst = ReadBool(p, hasParams, "burst") == true;
        DiscordRest.Reply reply;
        if (ReadString(p, hasParams, "userId") is { } user)
        {
            if (!c.Session.Cache.CanManageMessages(c.Id)) return new { ok = false, message = "You don't have permission to do that here." };
            reply = await c.Session.Rest.RemoveReactionAsync(c.Id, DiscordRest.Id(id), emoji, DiscordRest.Id(user), burst, ct).ConfigureAwait(false);
        }
        else
        {
            var add = ReadBool(p, hasParams, "add") != false;
            if (add && burst && !c.Session.Cache.Premium) return new { ok = false, message = "Super Reactions come with Nitro." };
            reply = await c.Session.Rest.ReactAsync(c.Id, DiscordRest.Id(id), emoji, add, burst, ct).ConfigureAwait(false);
        }
        return new { ok = reply.Error is null, message = reply.Error };
    }

    /// <summary>
    /// A message's menu actions Discord keeps for its author or moderators: Publish (an announcement channel's message
    /// to the servers following it), Remove Embeds, and Remove All Reactions (Manage Messages).
    /// </summary>
    private static async Task<object> DiscordMessageActionAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        if (Channel(p, hasParams) is not { } c || ReadString(p, hasParams, "messageId") is not { } raw) return new { ok = false };
        var id = DiscordRest.Id(raw);
        var manage = c.Session.Cache.CanManageMessages(c.Id);
        var mine = ReadBool(p, hasParams, "mine") == true;
        DiscordRest.Reply reply;
        switch (ReadString(p, hasParams, "action"))
        {
            case "publish" when c.Session.Cache.ChannelType(c.Id) == 5 && (mine || manage):
                reply = await c.Session.Rest.CrosspostAsync(c.Id, id, ct).ConfigureAwait(false);
                break;
            case "removeEmbeds" when mine || manage:
                var flags = ReadDouble(p, hasParams, "flags") is { } f && f is >= 0 and < 1 << 30 ? (long)f : 0;
                reply = await c.Session.Rest.SuppressEmbedsAsync(c.Id, id, flags, ct).ConfigureAwait(false);
                break;
            case "clearReactions" when manage:
                reply = await c.Session.Rest.ClearReactionsAsync(c.Id, id, ct).ConfigureAwait(false);
                break;
            default:
                return new { ok = false, message = "You don't have permission to do that here." };
        }
        return new { ok = reply.Error is null, message = reply.Error };
    }

    /// <summary>Who reacted with an emoji (normal or super), a page after the last person of the one before.</summary>
    private static async Task<object> DiscordReactorsAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        if (Channel(p, hasParams) is not { } c || ReadString(p, hasParams, "messageId") is not { } id || ReadString(p, hasParams, "emoji") is not { Length: > 0 and <= 80 } emoji)
            return new { ok = false };
        var reply = await c.Session.Rest.ReactorsAsync(c.Id, DiscordRest.Id(id), emoji, ReadBool(p, hasParams, "burst") == true,
            ReadString(p, hasParams, "after") is { } after ? DiscordRest.Id(after) : null, ReadInt(p, "limit", 1, DiscordRest.ReactorsPage, DiscordRest.ReactorsPage), ct).ConfigureAwait(false);
        return new { ok = reply.Error is null, message = reply.Error, users = reply.Body, canRemove = c.Session.Cache.CanManageMessages(c.Id) };
    }

    /// <summary>
    /// Copy Image (the picture on the clipboard as a bitmap) or Save Image (into Downloads; only the file's name
    /// comes back), for a picture on Discord's own servers. Continues on the UI thread, which owns the clipboard.
    /// </summary>
    private static async Task<object> DiscordImageAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        var action = ReadString(p, hasParams, "action");
        if (DiscordSession.Current is not { } session || action is not ("copy" or "save")
            || ReadString(p, hasParams, "url") is not { } raw || !Uri.TryCreate(raw, UriKind.Absolute, out var url))
            return new { ok = false };
        var (bytes, type, error) = await session.Rest.ImageAsync(url, ct).ConfigureAwait(true);
        if (bytes is null) return new { ok = false, message = error };
        if (action == "copy") return await DiscordImages.CopyAsync(bytes).ConfigureAwait(true) ? new { ok = true } : new { ok = false, message = "Unable to copy image" };
        var name = await Task.Run(() => DiscordImages.Save(bytes, url, type), ct).ConfigureAwait(true);
        return name is null ? new { ok = false, message = "Unable to save image" } : new { ok = true, name };
    }

    /// <summary>A text file's preview: its first 50 KB from Discord's file server, and how much is left.</summary>
    private static async Task<object> DiscordFileTextAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        if (DiscordSession.Current is not { } session || ReadString(p, hasParams, "url") is not { } raw || !Uri.TryCreate(raw, UriKind.Absolute, out var url))
            return new { ok = false };
        var (text, left, error) = await session.Rest.AttachmentTextAsync(url, ct).ConfigureAwait(false);
        return new { ok = error is null, message = error, text, left };
    }
}
