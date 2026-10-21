using System.Collections.Concurrent;
using System.Text.Json;
using ExoLauncher.Services.DiscordClient;

namespace ExoLauncher.Services;

/// <summary>
/// Stickers, polls and the soundboard. discord.stickerData is an animated (Lottie) sticker's
/// animation, for Exo to draw: Discord serves it from its CDN as JSON with no CORS header, so the page
/// can't fetch it itself; the host fetches it once (by id, which is all that goes out) and keeps the
/// last few in memory.
/// </summary>
public sealed partial class WebHostBridge
{
    private static readonly HttpClient StickerHttp = ExoHttp.Create(TimeSpan.FromSeconds(15));
    private static readonly ConcurrentDictionary<string, string> StickerJson = new(StringComparer.Ordinal);
    private const int MaxStickerBytes = 2 * 1024 * 1024;
    private static IReadOnlyList<object>? _stickerPacks;

    /// <summary>
    /// discord.stickers: the picker's groups, with Discord's packs read once per run, and the account's favorites;
    /// action add or remove favorites or unfavorites stickerId first.
    /// </summary>
    private static async Task<object> DiscordStickersAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        if (DiscordSession.Current is not { } session) return new { premium = false, groups = Array.Empty<object>() };
        var guildId = ReadString(p, hasParams, "guildId") is { Length: > 0 } g ? DiscordRest.Id(g) : null;
        var action = ReadString(p, hasParams, "action");
        var sticker = ReadString(p, hasParams, "stickerId") is { Length: > 0 and <= 20 } s && s.All(char.IsAsciiDigit) && ulong.TryParse(s, out var id) ? id : (ulong?)null;
        var favorites = action is "add" or "remove" && sticker is { } chosen
            ? DiscordEmojiSettings.FavoriteStickerAsync(session.Rest, chosen, action == "add", ct)
            : DiscordEmojiSettings.FavoriteStickersAsync(session.Rest, ct);
        // Kept once they came: a failed read asks again next time instead of hiding the packs all run.
        var packs = _stickerPacks ?? await ReadPacksAsync(session, ct).ConfigureAwait(false);
        if (packs.Count > 0) _stickerPacks = packs;
        return session.Cache.Stickers(guildId, packs, await favorites.ConfigureAwait(false));
    }

    private static async Task<IReadOnlyList<object>> ReadPacksAsync(DiscordSession session, CancellationToken ct)
    {
        var reply = await session.Rest.StickerPacksAsync(ct).ConfigureAwait(false);
        if (reply.Body is not { } body || !body.TryGetProperty("sticker_packs", out var packs) || packs.ValueKind != JsonValueKind.Array) return [];
        var list = new List<object>();
        foreach (var pack in packs.EnumerateArray())
        {
            if (!pack.TryGetProperty("stickers", out var stickers) || stickers.ValueKind != JsonValueKind.Array) continue;
            var items = stickers.EnumerateArray()
                .Where(st => st.TryGetProperty("id", out _) && st.TryGetProperty("name", out _))
                .Select(st => new
                {
                    id = st.GetProperty("id").GetString(),
                    name = st.GetProperty("name").GetString(),
                    format = st.TryGetProperty("format_type", out var f) && f.ValueKind == JsonValueKind.Number ? f.GetInt32() : 1,
                }).ToArray();
            if (items.Length == 0) continue;
            list.Add(new { id = "pack:" + pack.GetProperty("id").GetString(), name = pack.TryGetProperty("name", out var n) ? n.GetString() : "Discord", icon = (string?)null, usable = true, stickers = items });
        }
        return list;
    }

    private static async Task<object> DiscordStickerDataAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        string id;
        try { id = DiscordRest.Id(ReadString(p, hasParams, "id") ?? ""); }
        catch (ArgumentException) { return new { ok = false, message = "Not a sticker." }; }
        if (StickerJson.TryGetValue(id, out var kept)) return new { ok = true, json = kept };
        try
        {
            using var response = await StickerHttp.GetAsync($"https://cdn.discordapp.com/stickers/{id}.json", HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return new { ok = false, message = "Discord has no animation for it." };
            if (response.Content.Headers.ContentLength is > MaxStickerBytes) return new { ok = false, message = "Too large." };
            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (json.Length > MaxStickerBytes) return new { ok = false, message = "Too large." };
            using (JsonDocument.Parse(json)) { } // only well-formed JSON goes to the page
            if (StickerJson.Count >= 64) StickerJson.Clear();
            StickerJson[id] = json;
            return new { ok = true, json };
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new { ok = false, message = "Discord did not answer." };
        }
    }

    /// <summary>discord.poll: create one, vote (the answers chosen; none takes the vote back) or end your own.</summary>
    private static async Task<object> DiscordPollAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        if (Channel(p, hasParams) is not { } c) return new { ok = false, message = "Discord isn't connected." };
        var message = ReadString(p, hasParams, "messageId") ?? "";
        if (ReadString(p, hasParams, "action") == "create")
        {
            if (!c.Session.Cache.CanSend(c.Id)) return new { ok = false, message = "You can't send messages here." };
            var question = (ReadString(p, hasParams, "question") ?? "").Trim();
            var texts = new List<string>();
            if (hasParams && p.TryGetProperty("texts", out var given) && given.ValueKind == JsonValueKind.Array)
                foreach (var text in given.EnumerateArray())
                    if (text.ValueKind == JsonValueKind.String && text.GetString()?.Trim() is { Length: > 0 and <= 55 } answer && texts.Count < 10) texts.Add(answer);
            var hours = (int)(ReadDouble(p, hasParams, "hours") ?? 24);
            if (question.Length is 0 or > 300 || texts.Count < 2 || hours is not (1 or 4 or 8 or 24 or 72 or 168 or 336))
                return new { ok = false, message = "A question and at least two answers." };
            var posted = await c.Session.Rest.PostPollAsync(c.Id, question, texts, hours, ReadBool(p, hasParams, "multiple") == true, ct).ConfigureAwait(false);
            return new { ok = posted.Error is null, message = posted.Error };
        }
        if (message.Length == 0) return new { ok = false, message = "No poll." };
        var messageId = DiscordRest.Id(message);
        if (ReadString(p, hasParams, "action") == "end")
        {
            var ended = await c.Session.Rest.PollEndAsync(c.Id, messageId, ct).ConfigureAwait(false);
            return new { ok = ended.Error is null, message = ended.Error };
        }
        var answers = new List<int>();
        if (hasParams && p.TryGetProperty("answers", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var answer in list.EnumerateArray())
                if (answer.ValueKind == JsonValueKind.Number && answer.TryGetInt32(out var id) && id is > 0 and < 100 && answers.Count < 10) answers.Add(id);
        var reply = await c.Session.Rest.PollVoteAsync(c.Id, messageId, answers, ct).ConfigureAwait(false);
        return new { ok = reply.Error is null, message = reply.Error };
    }

    /// <summary>
    /// discord.soundboard: list (Discord's default sounds and this server's; with Nitro, every server's)
    /// or play one in the voice channel you're in.
    /// </summary>
    private static async Task<object> DiscordSoundboardAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        if (DiscordSession.Current is not { } session) return new { ok = false, message = "Discord isn't connected." };
        if (ReadString(p, hasParams, "action") == "play")
        {
            if (Channel(p, hasParams) is not { } c || ReadString(p, hasParams, "soundId") is not { } sound) return new { ok = false, message = "Join a voice channel first." };
            var played = await session.Rest.SendSoundAsync(c.Id, sound, ReadString(p, hasParams, "guildId"), ReadString(p, hasParams, "emojiId"), ReadString(p, hasParams, "emojiName"), ct).ConfigureAwait(false);
            return new { ok = played.Error is null, message = played.Error };
        }
        var defaults = await session.Rest.SoundboardDefaultsAsync(ct).ConfigureAwait(false);
        var guildId = ReadString(p, hasParams, "guildId") is { Length: > 0 } g ? DiscordRest.Id(g) : null;
        var guild = guildId is null ? null : await session.Rest.SoundboardAsync(guildId, ct).ConfigureAwait(false);
        return new { ok = defaults.Error is null, message = defaults.Error, defaults = defaults.Body, guild = guild?.Body };
    }
}
