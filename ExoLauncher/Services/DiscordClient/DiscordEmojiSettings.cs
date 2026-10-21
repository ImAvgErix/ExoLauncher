using ExoLauncher.Helpers;
using static ExoLauncher.Services.DiscordClient.DiscordProto;

namespace ExoLauncher.Services.DiscordClient;

/// <summary>
/// The emoji picker's Favorites and Frequently Used, from the account's FrecencyUserSettings
/// (users/@me/settings-proto/2), as Discord's web client keeps them (seen in its
/// discord_protos.discord_users.v1 messages and its emoji store):
/// field 5 favorite_emojis { repeated string emojis = 1 } (at most 250), field 6 emoji_frecency
/// and field 13 emoji_reaction_frecency, each { map&lt;string, FrecencyItem&gt; emojis = 1 } with
/// FrecencyItem { uint32 total_uses = 1; repeated uint64 recent_uses = 2; int32 frecency = 3; int32 score = 4 }.
/// A custom emoji is keyed by its id, a Unicode one by Discord's first name for it ("thumbsup").
/// Frequently Used is Discord's own ranking: each of the last 10 uses scores 100 within 3 days, 70 within
/// 15, 50 within 30, 30 within 45, 10 within 80, else 1; frecency is ceil(total uses x score / recent uses);
/// the top 42. Discord saves a field by sending a FrecencyUserSettings holding only that field (the server
/// replaces it), so Exo does the same and never touches the others.
/// The sticker picker's Favorites are field 3 favorite_stickers { repeated fixed64 sticker_ids = 1 } (packed, at most 250).
/// </summary>
internal static class DiscordEmojiSettings
{
    private const int MaxFavorites = 250;
    private const int FrequentCount = 42;
    private const int MaxSamples = 10;
    internal const int StickerFavoritesField = 3, FavoritesField = 5, FrecencyField = 6, ReactionFrecencyField = 13;

    internal sealed record State(List<string> Favorites, List<string> Frequent, List<string> FrequentReactions);

    internal sealed class Item
    {
        internal uint TotalUses;
        internal List<ulong> RecentUses = [];
        internal byte[] Unknown = [];
    }

    /// <summary>One frecency map as read: its entries in order, and what else the message held.</summary>
    internal sealed record Frecency(List<(string Key, Item Item)> Entries, byte[] Unknown);

    // A change reads, edits and sends a whole field: two at once would both start from the same list and one would be lost.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    internal static async Task<State?> ReadAsync(DiscordRest rest, CancellationToken ct)
    {
        if (await rest.FrecencyProtoAsync(ct).ConfigureAwait(false) is not { } proto) return null;
        try { return StateOf(proto, DateTimeOffset.UtcNow); }
        catch (InvalidDataException) { return null; }
    }

    internal static State StateOf(byte[] proto, DateTimeOffset now)
    {
        var fields = FieldsOf(proto);
        return new State(
            fields.TryGetValue(FavoritesField, out var f) ? DecodeFavorites(f).Emojis : [],
            fields.TryGetValue(FrecencyField, out var e) ? Frequently(DecodeFrecency(e), now) : [],
            fields.TryGetValue(ReactionFrecencyField, out var r) ? Frequently(DecodeFrecency(r), now) : []);
    }

    /// <summary>Favorites (add) or unfavorites one emoji and saves; the new state, or null on failure or at 250.</summary>
    internal static async Task<State?> FavoriteAsync(DiscordRest rest, string key, bool add, CancellationToken ct)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (await rest.FrecencyProtoAsync(ct).ConfigureAwait(false) is not { } proto) return null;
            var fields = FieldsOf(proto);
            var (emojis, unknown) = fields.TryGetValue(FavoritesField, out var f) ? DecodeFavorites(f) : ([], []);
            if (add == emojis.Contains(key)) return StateOf(proto, DateTimeOffset.UtcNow);
            if (add && emojis.Count >= MaxFavorites) return null;
            if (add) emojis.Add(key); else emojis.RemoveAll(e => e == key);
            var field = EncodeFavorites(emojis, unknown);
            var settings = new List<byte>();
            WriteBytes(settings, FavoritesField, field);
            var reply = await rest.SaveFrecencyAsync(Convert.ToBase64String([.. settings]), ct).ConfigureAwait(false);
            if (reply.Error is not null) return null;
            fields[FavoritesField] = field;
            return StateOf(Rebuild(fields), DateTimeOffset.UtcNow);
        }
        catch (InvalidDataException) { return null; }
        finally { Gate.Release(); }
    }

    /// <summary>The account's favorite stickers, by id, or null when Discord did not answer.</summary>
    internal static async Task<List<string>?> FavoriteStickersAsync(DiscordRest rest, CancellationToken ct)
    {
        if (await rest.FrecencyProtoAsync(ct).ConfigureAwait(false) is not { } proto) return null;
        try { return StickersOf(proto); }
        catch (InvalidDataException) { return null; }
    }

    internal static List<string> StickersOf(byte[] proto) =>
        FieldsOf(proto).TryGetValue(StickerFavoritesField, out var f) ? [.. DecodeStickers(f).Ids.Select(id => id.ToString(System.Globalization.CultureInfo.InvariantCulture))] : [];

    /// <summary>Favorites (add) or unfavorites one sticker and saves; the new list, or null on failure or at 250.</summary>
    internal static async Task<List<string>?> FavoriteStickerAsync(DiscordRest rest, ulong id, bool add, CancellationToken ct)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (await rest.FrecencyProtoAsync(ct).ConfigureAwait(false) is not { } proto) return null;
            var (ids, unknown) = FieldsOf(proto).TryGetValue(StickerFavoritesField, out var f) ? DecodeStickers(f) : ([], []);
            if (add == ids.Contains(id)) return StickersOf(proto);
            if (add && ids.Count >= MaxFavorites) return null;
            if (add) ids.Add(id); else ids.RemoveAll(e => e == id);
            var settings = new List<byte>();
            WriteBytes(settings, StickerFavoritesField, EncodeStickers(ids, unknown));
            var reply = await rest.SaveFrecencyAsync(Convert.ToBase64String([.. settings]), ct).ConfigureAwait(false);
            return reply.Error is null ? [.. ids.Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture))] : null;
        }
        catch (InvalidDataException) { return null; }
        finally { Gate.Release(); }
    }

    // sticker_ids is packed when Discord writes it; an unpacked one (wire type 1) is read too, as protobuf requires.
    internal static (List<ulong> Ids, byte[] Unknown) DecodeStickers(byte[] data)
    {
        var ids = new List<ulong>();
        var unknown = new List<byte>();
        foreach (var (number, wire, value) in Fields(data))
        {
            if (number == 1 && wire == 1) ids.Add(BitConverter.ToUInt64(value));
            else if (number == 1 && wire == 2 && value.Length % 8 == 0)
                for (var at = 0; at < value.Length; at += 8) ids.Add(BitConverter.ToUInt64(value, at));
            else Raw(unknown, number, wire, value);
        }
        return (ids, [.. unknown]);
    }

    internal static byte[] EncodeStickers(IReadOnlyCollection<ulong> ids, byte[] unknown)
    {
        var output = new List<byte>();
        if (ids.Count > 0) WriteBytes(output, 1, ids.SelectMany(BitConverter.GetBytes).ToArray());
        output.AddRange(unknown);
        return [.. output];
    }

    // Uses waiting to be saved, as Discord's client batches them: one save a little after the first.
    private static readonly List<(string Key, bool Reaction, ulong At)> Pending = [];
    private static bool _flushing;

    /// <summary>Counts one use of an emoji (in a message or as a reaction) in Discord's frecency, saved shortly after.</summary>
    internal static void Use(DiscordRest rest, string key, bool reaction)
    {
        lock (Pending)
        {
            Pending.Add((key, reaction, (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            if (_flushing) return;
            _flushing = true;
        }
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            List<(string Key, bool Reaction, ulong At)> uses;
            lock (Pending) { uses = [.. Pending]; Pending.Clear(); _flushing = false; }
            try { await SaveUsesAsync(rest, uses, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException) { AppLog.Warn($"Discord emoji frecency: {ex.Message}"); }
        });
    }

    private static async Task SaveUsesAsync(DiscordRest rest, List<(string Key, bool Reaction, ulong At)> uses, CancellationToken ct)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (await rest.FrecencyProtoAsync(ct).ConfigureAwait(false) is not { } proto) return;
            var fields = FieldsOf(proto);
            var settings = new List<byte>();
            foreach (var number in new[] { FrecencyField, ReactionFrecencyField })
            {
                var mine = uses.Where(u => u.Reaction == (number == ReactionFrecencyField)).ToList();
                if (mine.Count == 0) continue;
                var map = fields.TryGetValue(number, out var raw) ? DecodeFrecency(raw) : new Frecency([], []);
                foreach (var use in mine) Track(map, use.Key, use.At);
                WriteBytes(settings, number, EncodeFrecency(map, DateTimeOffset.UtcNow));
            }
            var reply = await rest.SaveFrecencyAsync(Convert.ToBase64String([.. settings]), ct).ConfigureAwait(false);
            if (reply.Error is not null) AppLog.Warn($"Discord emoji frecency: {reply.Error}");
        }
        finally { Gate.Release(); }
    }

    // The last value of each top-level field (protobuf's rule for a repeated message field read as one).
    private static Dictionary<int, byte[]> FieldsOf(byte[] proto)
    {
        var fields = new Dictionary<int, byte[]>();
        foreach (var (number, wire, value) in Fields(proto))
            if (wire == 2 && number is StickerFavoritesField or FavoritesField or FrecencyField or ReactionFrecencyField) fields[number] = value;
        return fields;
    }

    private static byte[] Rebuild(Dictionary<int, byte[]> fields)
    {
        var output = new List<byte>();
        foreach (var (number, value) in fields) WriteBytes(output, number, value);
        return [.. output];
    }

    internal static (List<string> Emojis, byte[] Unknown) DecodeFavorites(byte[] data)
    {
        var emojis = new List<string>();
        var unknown = new List<byte>();
        foreach (var (number, wire, value) in Fields(data))
        {
            if (number == 1 && wire == 2) emojis.Add(System.Text.Encoding.UTF8.GetString(value));
            else Raw(unknown, number, wire, value);
        }
        return (emojis, [.. unknown]);
    }

    internal static byte[] EncodeFavorites(IEnumerable<string> emojis, byte[] unknown)
    {
        var output = new List<byte>();
        foreach (var emoji in emojis) WriteString(output, 1, emoji);
        output.AddRange(unknown);
        return [.. output];
    }

    internal static Frecency DecodeFrecency(byte[] data)
    {
        var entries = new List<(string, Item)>();
        var unknown = new List<byte>();
        foreach (var (number, wire, value) in Fields(data))
        {
            if (number != 1 || wire != 2) { Raw(unknown, number, wire, value); continue; }
            string? key = null;
            var item = new Item();
            foreach (var (n, w, v) in Fields(value))
            {
                if (n == 1 && w == 2) key = System.Text.Encoding.UTF8.GetString(v);
                else if (n == 2 && w == 2) item = DecodeItem(v);
            }
            if (key is not null) entries.Add((key, item));
        }
        return new Frecency(entries, [.. unknown]);
    }

    private static Item DecodeItem(byte[] data)
    {
        var item = new Item();
        var unknown = new List<byte>();
        foreach (var (n, w, v) in Fields(data))
        {
            if (n == 1 && w == 0) item.TotalUses = (uint)Varint(v);
            else if (n == 2 && w == 0) item.RecentUses.Add(Varint(v));
            else if (n == 2 && w == 2) item.RecentUses.AddRange(Packed(v));
            // frecency and score are worked out again from the uses, as Discord's client does on reading.
            else if (n is not (3 or 4)) Raw(unknown, n, w, v);
        }
        item.Unknown = [.. unknown];
        return item;
    }

    private static IEnumerable<ulong> Packed(byte[] data)
    {
        var values = new List<ulong>();
        var at = 0;
        while (at < data.Length)
        {
            var start = at;
            while (at < data.Length && (data[at] & 0x80) != 0) at++;
            if (at >= data.Length) throw new InvalidDataException("Truncated varint.");
            at++;
            values.Add(Varint(data[start..at]));
        }
        return values;
    }

    internal static byte[] EncodeFrecency(Frecency map, DateTimeOffset now)
    {
        var output = new List<byte>();
        foreach (var (key, item) in map.Entries)
        {
            var (frecency, score) = Rank(item, now);
            var value = new List<byte>();
            if (item.TotalUses != 0) { WriteTag(value, 1, 0); WriteVarint(value, item.TotalUses); }
            if (item.RecentUses.Count > 0)
            {
                var packed = new List<byte>();
                foreach (var use in item.RecentUses) WriteVarint(packed, use);
                WriteBytes(value, 2, packed);
            }
            if (frecency != 0) { WriteTag(value, 3, 0); WriteVarint(value, (ulong)frecency); }
            if (score != 0) { WriteTag(value, 4, 0); WriteVarint(value, (ulong)score); }
            value.AddRange(item.Unknown);
            var entry = new List<byte>();
            WriteString(entry, 1, key);
            WriteBytes(entry, 2, value);
            WriteBytes(output, 1, entry);
        }
        output.AddRange(map.Unknown);
        return [.. output];
    }

    /// <summary>One more use: counted, and kept among the last 10.</summary>
    internal static void Track(Frecency map, string key, ulong at)
    {
        var item = map.Entries.FirstOrDefault(e => e.Key == key).Item;
        if (item is null) { item = new Item(); map.Entries.Add((key, item)); }
        item.TotalUses++;
        item.RecentUses.Add(at);
        item.RecentUses.Sort();
        while (item.RecentUses.Count > MaxSamples) item.RecentUses.RemoveAt(0);
    }

    private static int Weight(int days) => days <= 3 ? 100 : days <= 15 ? 70 : days <= 30 ? 50 : days <= 45 ? 30 : days <= 80 ? 10 : 1;

    private static (int Frecency, int Score) Rank(Item item, DateTimeOffset now)
    {
        var score = item.RecentUses.Take(MaxSamples).Sum(use => Weight((int)Math.Floor((now - DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Min(use, long.MaxValue))).TotalDays)));
        return item.RecentUses.Count == 0 || score == 0 ? (0, score) : ((int)Math.Ceiling(item.TotalUses * ((double)score / item.RecentUses.Count)), score);
    }

    /// <summary>Discord's Frequently Used: the most frecent first, those with no recent use left out, at most 42.</summary>
    internal static List<string> Frequently(Frecency map, DateTimeOffset now) =>
        [.. map.Entries.Select(e => (e.Key, Rank(e.Item, now).Frecency)).Where(e => e.Frecency > 0)
            .OrderByDescending(e => e.Frecency).Take(FrequentCount).Select(e => e.Key)];
}
