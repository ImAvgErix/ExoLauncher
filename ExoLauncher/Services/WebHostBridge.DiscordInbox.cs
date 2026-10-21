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
