using System.Globalization;
using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// What Steam's item service (IStoreBrowseService/GetItems) says about a game
/// for its card: its real art paths (games added since 2024 keep their art
/// under hashed folders, so the fixed names 404), the review summary, the
/// release, the regional price as Steam writes it and the top tags.
/// Many games per call; kept on disk, so a card's art and score show at once
/// on the next start. Tag names come from Steam's tag list, kept a week.
/// </summary>
internal sealed partial class StoreService
{
    /// <summary>Full URLs; null when Steam has no such art for the game.</summary>
    internal sealed record Art(string? Poster, string? Hero, string? Capsule, string? Header);

    /// <summary>One game's card facts from Steam.</summary>
    internal sealed record Item(Art? Art)
    {
        public DateTimeOffset At { get; init; }
        public string? Review { get; init; }
        public int? ReviewPercent { get; init; }
        public int ReviewCount { get; init; }
        public DateTimeOffset? Released { get; init; }
        /// <summary>Not out yet; <see cref="When"/> says when ("Oct 23", "Q1 2027", "To be announced").</summary>
        public bool Soon { get; init; }
        public string? When { get; init; }
        /// <summary>The release is a real day (not "Q1 2027" or "Feb 2027"), so it can sit on a calendar.</summary>
        public bool Exact { get; init; }
        public SteamPrice? Price { get; init; }
        public bool Free { get; init; }
        /// <summary>Tag ids, the most voted first.</summary>
        public IReadOnlyList<int> Tags { get; init; } = [];
        /// <summary>Steam marks it Adult Only Sexual Content or Frequent Nudity or Sexual Content (content descriptors 3 and 4), which Steam's own store hides unless you opt in; Exo's lists leave it out.</summary>
        public bool Adult { get; init; }
    }

    private sealed record ItemFile(string Country, Dictionary<string, Item> Items);

    private sealed record TagFile(DateTimeOffset At, Dictionary<int, string> Names);

    private const string AssetBase = "https://shared.fastly.steamstatic.com/store_item_assets/";

    /// <summary>Prices and scores older than this are asked for again; art is kept as long as the entry (two weeks).</summary>
    private static readonly TimeSpan ItemFresh = TimeSpan.FromHours(12);
    private static readonly TimeSpan ItemKeep = TimeSpan.FromDays(14);
    private static readonly TimeSpan TagsFresh = TimeSpan.FromDays(7);

    private Dictionary<string, Item>? _items;
    private readonly object _itemsSave = new();
    private TagFile? _tags;

    private static string ItemsFile => Path.Combine(Dir, "items-v3.json");

    private static string TagsFile => Path.Combine(Dir, "tags-v1.json");

    /// <summary>The cache, read from disk the first time (entries for another region or older than two weeks are dropped). Call under <see cref="_gate"/>.</summary>
    private Dictionary<string, Item> CachedItems()
    {
        if (_items is not null) return _items;
        var file = Read<ItemFile>(ItemsFile);
        var cutoff = DateTimeOffset.UtcNow - ItemKeep;
        _items = file?.Country == Country && file.Items is not null
            ? file.Items.Where(kv => kv.Value.At > cutoff).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal)
            : new Dictionary<string, Item>(StringComparer.Ordinal);
        return _items;
    }

    /// <summary>
    /// The card facts of each game: from the cache when fresh, otherwise from
    /// Steam in batches of 50. With <paramref name="staleOk"/> an old entry is
    /// answered at once and refreshed behind it.
    /// </summary>
    internal async Task<IReadOnlyDictionary<string, Item>> ItemsAsync(IEnumerable<string> appIds, CancellationToken ct, bool staleOk = false)
    {
        var wanted = appIds.Where(id => id.Length > 0 && id.All(char.IsAsciiDigit)).Distinct(StringComparer.Ordinal).ToList();
        var now = DateTimeOffset.UtcNow;
        List<string> missing, stale;
        lock (_gate)
        {
            var items = CachedItems();
            missing = wanted.Where(id => !items.ContainsKey(id)).ToList();
            stale = wanted.Where(id => items.TryGetValue(id, out var item) && now - item.At > ItemFresh).ToList();
        }
        var fetch = staleOk ? missing : [.. missing, .. stale];
        if (fetch.Count > 0) await FetchItemsAsync(fetch, ct).ConfigureAwait(false);
        if (staleOk && stale.Count > 0) _ = Task.Run(() => FetchItemsAsync(stale, CancellationToken.None));
        lock (_gate)
        {
            var items = CachedItems();
            return wanted.Where(items.ContainsKey).ToDictionary(id => id, id => items[id], StringComparer.Ordinal);
        }
    }

    private async Task FetchItemsAsync(IReadOnlyList<string> appIds, CancellationToken ct)
    {
        await Task.WhenAll(appIds.Chunk(50).Select(chunk => FetchItemChunkAsync(chunk, ct))).ConfigureAwait(false);
        SaveItems();
    }

    private async Task FetchItemChunkAsync(string[] appIds, CancellationToken ct)
    {
        var input = JsonSerializer.Serialize(new
        {
            // An id that is not a number (a bad entry in a saved list) is left out, not thrown over the whole chunk.
            ids = appIds.Where(id => long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out _)).Select(id => new { appid = long.Parse(id, CultureInfo.InvariantCulture) }),
            context = new { language = "english", country_code = Country },
            data_request = ItemRequest,
        });
        try
        {
            using var doc = await GetJsonAsync("https://api.steampowered.com/IStoreBrowseService/GetItems/v1?input_json=" + Uri.EscapeDataString(input), ct).ConfigureAwait(false);
            if (!doc.RootElement.TryGetProperty("response", out var r) || !r.TryGetProperty("store_items", out var items) || items.ValueKind != JsonValueKind.Array) return;
            var read = new List<(string Id, Item Item)>();
            foreach (var item in items.EnumerateArray())
                if (Str(item, "appid") is { } id) read.Add((id, ReadItem(item)));
            Remember(read);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // The fixed art names still work for most games; the card shows no score.
            AppLog.Debug("Store: item lookup failed: " + ex.Message);
        }
    }

    /// <summary>What every item request asks for: art, the review summary, the release and the top tags. The price comes with it.</summary>
    private static object ItemRequest => new { include_assets = true, include_reviews = true, include_release = true, include_tag_count = 20 };

    private void Remember(IEnumerable<(string Id, Item Item)> read)
    {
        lock (_gate)
        {
            var items = CachedItems();
            foreach (var (id, item) in read) items[id] = item;
        }
    }

    /// <summary>Writes the cache; one writer at a time, from a copy taken under the gate.</summary>
    private void SaveItems()
    {
        lock (_itemsSave)
        {
            ItemFile file;
            lock (_gate) file = new ItemFile(Country, new Dictionary<string, Item>(CachedItems(), StringComparer.Ordinal));
            Write(ItemsFile, file);
        }
    }

    /// <summary>One item from GetItems or the query service.</summary>
    private static Item ReadItem(JsonElement e)
    {
        var item = new Item(e.TryGetProperty("assets", out var a) && a.ValueKind == JsonValueKind.Object ? ReadArt(a) : null) { At = DateTimeOffset.UtcNow };
        if (e.TryGetProperty("reviews", out var rv) && rv.TryGetProperty("summary_filtered", out var sum) && Num(sum, "review_count") is > 0 and var count)
            item = item with { Review = Blank(Str(sum, "review_score_label")), ReviewPercent = (int)Num(sum, "percent_positive"), ReviewCount = (int)count };
        if (e.TryGetProperty("release", out var rel) && rel.ValueKind == JsonValueKind.Object)
        {
            var unix = Num(rel, "steam_release_date");
            DateTimeOffset? released = unix > 0 ? DateTimeOffset.FromUnixTimeSeconds(unix) : null;
            var soon = rel.TryGetProperty("is_coming_soon", out var cs) && cs.ValueKind == JsonValueKind.True;
            // Out: the date is a day. Coming: only when the developer gave a full date (Steam's "date_full"), with no custom message in its place.
            var exact = released is not null && (!soon || (Str(rel, "coming_soon_display") == "date_full" && Blank(Str(rel, "custom_release_date_message")) is null));
            item = item with { Released = released, Soon = soon, Exact = exact, When = soon ? WhenText(released, Str(rel, "coming_soon_display"), Blank(Str(rel, "custom_release_date_message"))) : null };
        }
        if (e.TryGetProperty("best_purchase_option", out var buy) && buy.ValueKind == JsonValueKind.Object && Str(buy, "final_price_in_cents") is not null)
        {
            var final = (int)Num(buy, "final_price_in_cents");
            var initial = Str(buy, "original_price_in_cents") is null ? final : (int)Num(buy, "original_price_in_cents");
            item = item with { Price = new SteamPrice(final, initial, (int)Num(buy, "discount_pct"), null, Blank(Str(buy, "formatted_final_price")), Blank(Str(buy, "formatted_original_price"))) };
        }
        if (e.TryGetProperty("is_free", out var free) && free.ValueKind == JsonValueKind.True) item = item with { Free = true };
        if (e.TryGetProperty("content_descriptorids", out var descriptors) && descriptors.ValueKind == JsonValueKind.Array
            && descriptors.EnumerateArray().Any(d => d.ValueKind == JsonValueKind.Number && d.GetInt32() is 3 or 4))
            item = item with { Adult = true };
        if (e.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
            item = item with { Tags = tags.EnumerateArray().OrderByDescending(t => Num(t, "weight")).Select(t => (int)Num(t, "tagid")).Where(id => id > 0).ToList() };
        return item;
    }

    private static Art ReadArt(JsonElement assets)
    {
        var format = Str(assets, "asset_url_format");
        string? Url(string key) => format is not null && Blank(Str(assets, key)) is { } file
            ? AssetBase + format.Replace("${FILENAME}", file, StringComparison.Ordinal)
            : null;
        return new Art(Url("library_capsule_2x") ?? Url("library_capsule"), Url("library_hero_2x") ?? Url("library_hero"), Url("main_capsule"), Url("header_2x") ?? Url("header"));
    }

    /// <summary>The tile with Steam's facts on it: art, score, release, free to play, and Steam's regional price when the item has one.</summary>
    private static Tile WithItem(Tile t, Item i)
    {
        t = t with { Art = i.Art ?? t.Art, Review = i.Review, ReviewPercent = i.ReviewPercent, Free = i.Free, Soon = i.Soon, When = i.When };
        if (i.Price is { } p)
            t = t with { Final = p.Final, Initial = p.Initial, Discount = p.Discount, Currency = null, PriceText = p.Formatted, WasText = p.WasFormatted };
        else if (i.Free)
            t = t with { Final = 0, Initial = 0, Discount = 0, Currency = null, PriceText = null, WasText = null };
        return t;
    }

    /// <summary>The same tiles with Steam's facts filled in; adult games (<see cref="Item.Adult"/>) left out.</summary>
    private async Task<IReadOnlyList<Tile>> WithItemsAsync(IReadOnlyList<Tile> tiles, CancellationToken ct)
    {
        var items = await ItemsAsync(tiles.Select(t => t.AppId), ct).ConfigureAwait(false);
        return tiles.Where(t => !(items.TryGetValue(t.AppId, out var i) && i.Adult)).Select(t => items.TryGetValue(t.AppId, out var i) ? WithItem(t, i) : t).ToList();
    }

    /// <summary>Steam's tag names by id (English), from disk when under a week old.</summary>
    private async Task<IReadOnlyDictionary<int, string>> TagNamesAsync(CancellationToken ct)
    {
        TagFile? known;
        lock (_gate) known = _tags ??= Read<TagFile>(TagsFile);
        if (known?.Names is { Count: > 0 } names && DateTimeOffset.UtcNow - known.At < TagsFresh) return names;
        try
        {
            using var doc = await GetJsonAsync("https://api.steampowered.com/IStoreService/GetTagList/v1?language=english", ct).ConfigureAwait(false);
            if (!doc.RootElement.TryGetProperty("response", out var r) || !r.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Array)
                return (IReadOnlyDictionary<int, string>?)known?.Names ?? new Dictionary<int, string>();
            var fresh = new Dictionary<int, string>();
            foreach (var t in tags.EnumerateArray())
                if (Num(t, "tagid") is > 0 and var id && Blank(Str(t, "name")) is { } name) fresh[(int)id] = name;
            var file = new TagFile(DateTimeOffset.UtcNow, fresh);
            lock (_gate) _tags = file;
            Write(TagsFile, file);
            return fresh;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            AppLog.Debug("Store: tag list failed: " + ex.Message);
            return (IReadOnlyDictionary<int, string>?)known?.Names ?? new Dictionary<int, string>();
        }
    }
}
