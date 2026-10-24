using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace ExoLauncher.Services.DiscordClient;

/// <summary>Discord's search, Inbox (For You and Mentions), who reacted, and a text file's preview, as Discord's own client asks for them.</summary>
internal sealed partial class DiscordRest
{
    /// <summary>
    /// What Discord's search bar sends: the words left over, then each filter's answers (from:, mentions:, has:,
    /// in:, authorType:, pinned:, before:/after:/during: as ids), the sort and the page. A has: or authorType:
    /// answer may start with - (Discord's "not").
    /// </summary>
    internal sealed record SearchFilters(
        string? Content,
        IReadOnlyList<string> AuthorIds,
        IReadOnlyList<string> Mentions,
        IReadOnlyList<string> Has,
        IReadOnlyList<string> ChannelIds,
        IReadOnlyList<string> AuthorTypes,
        bool? Pinned,
        string? MinId,
        string? MaxId,
        string Sort,
        int Offset);

    /// <summary>Discord's saved messages: the reminders (each with when it's due) and the ids of bookmarked messages.</summary>
    internal Task<Reply> SavedAsync(CancellationToken ct) =>
        SendAsync(HttpMethod.Get, "users/@me/saved-messages?bookmark_ids=true", null, ct);

    /// <summary>Bookmarks, 25 a page, the latest saved first; before: the oldest saved_at of the page before.</summary>
    internal Task<Reply> BookmarksAsync(DateTimeOffset? before, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, "users/@me/saved-messages/bookmarks?limit=25"
            + (before is { } b ? "&before=" + Uri.EscapeDataString(b.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)) : ""), null, ct);

    /// <summary>Bookmark Message (no due time) or Remind Me (due): the message saved for later, as the message menu sends it.</summary>
    internal Task<Reply> SaveMessageAsync(string channelId, string messageId, DateTimeOffset? due, CancellationToken ct)
    {
        var body = new JsonObject { ["source"] = "context_menu" };
        if (due is { } at) body["due_at"] = at.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
        return SendAsync(HttpMethod.Put, $"users/@me/saved-messages/{Id(channelId)}/{Id(messageId)}", body, ct);
    }

    /// <summary>Remove Bookmark, Remove Reminder or Mark Complete.</summary>
    internal Task<Reply> UnsaveMessageAsync(string channelId, string messageId, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"users/@me/saved-messages/{Id(channelId)}/{Id(messageId)}", null, ct);

    /// <summary>The has: answers Discord's search takes (snapshot is Forwarded).</summary>
    internal static readonly string[] SearchHas = ["link", "embed", "poll", "snapshot", "file", "video", "image", "sound", "sticker"];
    internal static readonly string[] SearchAuthorTypes = ["user", "bot", "webhook"];

    /// <summary>A page of search results is 25 messages; Discord stops paging at offset 9975.</summary>
    internal const int SearchPage = 25, SearchMaxOffset = 9975;

    /// <summary>The query string of a search, as Discord's search bar builds it; values it doesn't take are left out.</summary>
    internal static string SearchQuery(SearchFilters f)
    {
        var q = new List<string>();
        void Add(string key, string value) => q.Add(key + "=" + Uri.EscapeDataString(value));
        foreach (var id in f.AuthorIds.Take(10)) Add("author_id", Id(id));
        foreach (var id in f.Mentions.Take(10)) Add("mentions", Id(id));
        foreach (var has in f.Has.Where(h => SearchHas.Contains(h.TrimStart('-'))).Distinct().Take(10)) Add("has", has);
        foreach (var id in f.ChannelIds.Take(10)) Add("channel_id", Id(id));
        foreach (var type in f.AuthorTypes.Where(t => SearchAuthorTypes.Contains(t.TrimStart('-'))).Distinct().Take(3)) Add("author_type", type);
        if (f.Pinned is { } pinned) Add("pinned", pinned ? "true" : "false");
        if (f.MinId is { } min) Add("min_id", Id(min));
        if (f.MaxId is { } max) Add("max_id", Id(max));
        if (f.Content?.Trim() is { Length: > 0 } content) Add("content", content.Length > 500 ? content[..500] : content);
        var (by, order) = f.Sort switch
        {
            "relevance" => ("relevance", "desc"),
            "oldest" => ("timestamp", "asc"),
            _ => ("timestamp", "desc"),
        };
        Add("sort_by", by);
        Add("sort_order", order);
        if (f.Offset > 0) Add("offset", (Math.Min(f.Offset, SearchMaxOffset) / SearchPage * SearchPage).ToString(CultureInfo.InvariantCulture));
        q.Add("include_nsfw=true");
        return string.Join('&', q);
    }

    /// <summary>A search of a whole server (its channels picked by in:), or of a DM.</summary>
    internal Task<Reply> SearchAsync(string? guildId, string channelId, SearchFilters filters, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, (guildId is null ? $"channels/{Id(channelId)}" : $"guilds/{Id(guildId)}") + "/messages/search?" + SearchQuery(filters), null, ct);

    /// <summary>The Inbox's Mentions: 25 at a time (older than before), in one server or all, with role and @everyone mentions or not.</summary>
    internal Task<Reply> MentionsAsync(string? before, string? guildId, bool roles, bool everyone, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, "users/@me/mentions?limit=25" + (before is null ? "" : "&before=" + Id(before))
            + (guildId is null ? "" : "&guild_id=" + Id(guildId)) + $"&roles={(roles ? "true" : "false")}&everyone={(everyone ? "true" : "false")}", null, ct);

    /// <summary>The Inbox's × on a mention: it leaves the list (the message stays).</summary>
    internal Task<Reply> RemoveMentionAsync(string messageId, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"users/@me/mentions/{Id(messageId)}", null, ct);

    /// <summary>The Inbox's For You: Discord's notification center (friend requests accepted, events starting, Go Live and more), older than before.</summary>
    internal Task<Reply> ForYouAsync(string? before, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, "users/@me/notification-center/items?limit=20&with_mentions=false" + (before is null ? "" : "&before=" + Id(before)), null, ct);

    /// <summary>Adds or takes back the user's own reaction, a super reaction (burst) or a normal one.</summary>
    internal Task<Reply> ReactAsync(string channelId, string messageId, string emoji, bool add, bool burst, CancellationToken ct) => add
        ? SendAsync(HttpMethod.Put, $"{ReactionPath(channelId, messageId, emoji)}/@me?location=Message&type={(burst ? 1 : 0)}", null, ct)
        : RemoveReactionAsync(channelId, messageId, emoji, null, burst, ct);

    /// <summary>Takes back a reaction: the user's own, or (with Manage Messages) someone else's, from the Reactions list.</summary>
    internal Task<Reply> RemoveReactionAsync(string channelId, string messageId, string emoji, string? userId, bool burst, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"{ReactionPath(channelId, messageId, emoji)}/{(burst ? 1 : 0)}/{(userId is null ? "@me" : Id(userId))}?location=Message&burst={(burst ? "true" : "false")}", null, ct);

    /// <summary>Remove All Reactions (Manage Messages): every reaction on the message.</summary>
    internal Task<Reply> ClearReactionsAsync(string channelId, string messageId, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"channels/{Id(channelId)}/messages/{Id(messageId)}/reactions", null, ct);

    /// <summary>Publish: an announcement channel's message goes to every server following the channel.</summary>
    internal Task<Reply> CrosspostAsync(string channelId, string messageId, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"channels/{Id(channelId)}/messages/{Id(messageId)}/crosspost", null, ct);

    /// <summary>Remove All Embeds: the message keeps its flags with SUPPRESS_EMBEDS (4) added, as Discord's client sends it.</summary>
    internal Task<Reply> SuppressEmbedsAsync(string channelId, string messageId, long flags, CancellationToken ct) =>
        SendAsync(HttpMethod.Patch, $"channels/{Id(channelId)}/messages/{Id(messageId)}", new JsonObject { ["flags"] = flags | 4 }, ct);

    /// <summary>The most people Discord sends per page of who reacted.</summary>
    internal const int ReactorsPage = 100;

    /// <summary>Who reacted with an emoji (normal or super), a page at a time after the last person of the page before.</summary>
    internal Task<Reply> ReactorsAsync(string channelId, string messageId, string emoji, bool burst, string? after, int limit, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"{ReactionPath(channelId, messageId, emoji)}?limit={Math.Clamp(limit, 1, ReactorsPage)}&type={(burst ? 1 : 0)}"
            + (after is null ? "" : "&after=" + Id(after)), null, ct);

    /// <summary>A reaction's path: the emoji exactly as Discord's own list writes it (no VS16 it lacks, a skin tone or keycap kept), or name:id, percent-encoded.</summary>
    internal static string ReactionPath(string channelId, string messageId, string emoji) =>
        $"channels/{Id(channelId)}/messages/{Id(messageId)}/reactions/{Uri.EscapeDataString(emoji)}";

    /// <summary>How much of a text file Discord's preview reads (its own Range, bytes 0 to 50000).</summary>
    internal const int TextPreviewBytes = 50_001;

    /// <summary>
    /// The start of a text file sent to Discord, for its preview: the first 50 KB of an attachment on Discord's
    /// file server, and how many bytes are left after it. Never the token: the file server needs none.
    /// </summary>
    internal async Task<(string? Text, long Left, string? Error)> AttachmentTextAsync(Uri url, CancellationToken ct)
    {
        if (!IsAttachmentUrl(url)) return (null, 0, "That isn't a file on Discord.");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, TextPreviewBytes - 1);
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return (null, 0, response.StatusCode == HttpStatusCode.NotFound ? "That file isn't on Discord anymore." : "Discord didn't send the file.");
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var buffer = new byte[TextPreviewBytes];
            var read = 0;
            while (read < buffer.Length && await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false) is > 0 and var n) read += n;
            var total = response.Content.Headers.ContentRange?.Length ?? response.Content.Headers.ContentLength ?? read;
            return (DecodePreview(buffer.AsSpan(0, read), total > read), Math.Max(0, total - read), null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return (null, 0, "Discord could not be reached.");
        }
    }

    /// <summary>A picture from Discord's own servers, for Copy Image and Save Image.</summary>
    internal Task<(byte[]? Bytes, string? ContentType, string? Error)> ImageAsync(Uri url, CancellationToken ct) =>
        DiscordImages.DownloadAsync(_http, url, ct);

    /// <summary>Only attachments on Discord's own file servers are read for a preview.</summary>
    internal static bool IsAttachmentUrl(Uri url) =>
        url.Scheme == Uri.UriSchemeHttps && url.IsDefaultPort && url.Host is "cdn.discordapp.com" or "media.discordapp.net"
        && url.AbsolutePath.StartsWith("/attachments/", StringComparison.Ordinal);

    /// <summary>UTF-8 text of a file's first bytes; a cut file drops its last, maybe half, character, as Discord's preview does.</summary>
    internal static string DecodePreview(ReadOnlySpan<byte> bytes, bool cut)
    {
        var text = Encoding.UTF8.GetString(bytes);
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        return cut && text.Length > 0 ? text[..^1] : text;
    }
}
