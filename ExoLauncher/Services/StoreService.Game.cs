using System.Diagnostics;
using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// Search and a game's page. The page answers as soon as Steam's app details
/// and the game's item facts (art, reviews, tags) land; where else it is
/// sold is a second call (CheapShark, three seconds at most), so
/// a slow outside store never holds the page. A page asked for twice at once
/// (a hover warming it, then the click) is fetched once.
/// </summary>
internal sealed partial class StoreService
{
    private static readonly TimeSpan DetailFresh = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan SearchFresh = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ItemWait = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan OfferWait = TimeSpan.FromSeconds(3);
    /// <summary>CheapShark limits how often it is asked: once it says so (429), it is left alone this long.</summary>
    private static readonly TimeSpan OfferPause = TimeSpan.FromMinutes(10);
    private DateTimeOffset _offersPausedUntil;

    private readonly Dictionary<string, (DateTimeOffset At, GameDetail Detail)> _details = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<GameDetail?>> _detailTasks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (DateTimeOffset At, IReadOnlyList<Offer> Offers)> _offers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (DateTimeOffset At, IReadOnlyList<Tile> Hits)> _searches = new(StringComparer.Ordinal);
    private CancellationTokenSource? _search;

    /// <summary>
    /// Steam's search with each hit's card facts. The same query within five
    /// minutes is answered from memory; a newer query cancels the one still running,
    /// which then answers null. A timeout is an error like any other, not a newer query.
    /// </summary>
    internal async Task<IReadOnlyList<Tile>?> SearchAsync(string query, CancellationToken ct)
    {
        query = query.Trim();
        if (query.Length < 2) return [];
        var key = query.ToLowerInvariant();
        var sw = Stopwatch.StartNew();
        lock (_gate)
            if (_searches.TryGetValue(key, out var known) && DateTimeOffset.UtcNow - known.At < SearchFresh) return known.Hits;
        var mine = CancellationTokenSource.CreateLinkedTokenSource(ct);
        CancellationTokenSource? previous;
        lock (_gate) (previous, _search) = (_search, mine);
        try { previous?.Cancel(); } catch (ObjectDisposedException) { }
        try
        {
            using var doc = await GetJsonAsync($"{Steam}api/storesearch/?term={Uri.EscapeDataString(query)}&cc={Country}&l=english", mine.Token).ConfigureAwait(false);
            if (!doc.RootElement.TryGetProperty("items", out var items)) return [];
            var hits = items.EnumerateArray()
                .Where(i => Str(i, "type") == "app" && Str(i, "name") is { } n && !NotAGame().IsMatch(n))
                .Select(i =>
                {
                    var tile = new Tile(Str(i, "id") ?? "", Str(i, "name")!.Trim());
                    if (i.TryGetProperty("price", out var p) && p.ValueKind == JsonValueKind.Object)
                    {
                        var final = (int)Num(p, "final");
                        var initial = (int)Num(p, "initial");
                        tile = tile with { Final = final, Initial = initial, Discount = initial > 0 ? (int)Math.Round(100.0 * (initial - final) / initial) : 0, Currency = Blank(Str(p, "currency")) };
                    }
                    return tile;
                })
                .Where(t => t.AppId.Length > 0)
                .ToList();
            var result = await WithItemsAsync(hits, mine.Token).ConfigureAwait(false);
            mine.Token.ThrowIfCancellationRequested();
            lock (_gate) Remember(_searches, key, result);
            AppLog.Info($"PERF store search hits={result.Count} totalMs={sw.ElapsedMilliseconds}");
            return result;
        }
        catch (OperationCanceledException) when (mine.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            lock (_gate) if (_search == mine) _search = null;
            mine.Dispose();
        }
    }

    /// <summary>Keeps an answer in a memory cache of at most forty, dropping the ten oldest when full. Call under <c>_gate</c>.</summary>
    private static void Remember<T>(Dictionary<string, (DateTimeOffset At, T Value)> cache, string key, T value)
    {
        if (cache.Count >= 40 && !cache.ContainsKey(key))
            foreach (var old in cache.OrderBy(kv => kv.Value.At).Take(10).Select(kv => kv.Key).ToList()) cache.Remove(old);
        cache[key] = (DateTimeOffset.UtcNow, value);
    }

    /// <summary>One game's page, from memory for twenty minutes; asked for twice at once, fetched once.</summary>
    internal Task<GameDetail?> GameAsync(string appId, CancellationToken ct)
    {
        Task<GameDetail?>? task;
        lock (_gate)
        {
            if (_details.TryGetValue(appId, out var cached) && DateTimeOffset.UtcNow - cached.At < DetailFresh) return Task.FromResult<GameDetail?>(cached.Detail);
            if (!_detailTasks.TryGetValue(appId, out task))
            {
                task = FetchGameAsync(appId);
                _detailTasks[appId] = task;
            }
        }
        return task.WaitAsync(ct);
    }

    /// <summary>
    /// Steam's app details (text, media, requirements, Steam's price) and the
    /// item facts (art, reviews, tags; at most three seconds, else the
    /// page goes without them and is not kept).
    /// </summary>
    private async Task<GameDetail?> FetchGameAsync(string appId)
    {
        await Task.Yield();
        try
        {
            var total = Stopwatch.StartNew();
            // Not disposed here: when the details fail first, the item call still holds it (its timer ends it).
            var itemLimit = new CancellationTokenSource(ItemWait);
            var itemTask = Timed(() => ItemsAsync(new[] { appId }, itemLimit.Token));
            var tagsTask = TagNamesAsync(itemLimit.Token);
            var (doc, detailsMs) = await Timed(() => AppDetailsAsync(appId)).ConfigureAwait(false);
            using (doc)
            {
                if (!doc.RootElement.TryGetProperty(appId, out var wrap) || !wrap.TryGetProperty("data", out var d) || d.ValueKind != JsonValueKind.Object) return null;
                var (items, itemMs) = await itemTask.ConfigureAwait(false);
                var tagNames = await tagsTask.ConfigureAwait(false);
                var item = items.GetValueOrDefault(appId);
                var detail = ReadDetail(appId, d, item, tagNames);
                var complete = item is not null && (item.Tags.Count == 0 || tagNames.Count > 0);
                if (complete) lock (_gate) Remember(_details, appId, detail);
                AppLog.Info($"PERF store game detailsMs={detailsMs} itemMs={itemMs} totalMs={total.ElapsedMilliseconds}{(complete ? "" : " partial")}");
                return detail;
            }
        }
        finally
        {
            lock (_gate) _detailTasks.Remove(appId);
        }
    }

    /// <summary>
    /// Steam's app details. Now and then the first answer stalls for many
    /// seconds while a second ask returns at once, so the first gets four
    /// seconds and the retry eight.
    /// </summary>
    private async Task<JsonDocument> AppDetailsAsync(string appId)
    {
        var url = $"{Steam}api/appdetails?appids={appId}&cc={Country}&l=english";
        using (var first = new CancellationTokenSource(TimeSpan.FromSeconds(4)))
        {
            try { return await GetJsonAsync(url, first.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { AppLog.Info("Store: app details slow or failed, asking again: " + ex.Message); }
        }
        using var retry = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        return await GetJsonAsync(url, retry.Token).ConfigureAwait(false);
    }

    private static GameDetail ReadDetail(string appId, JsonElement d, Item? item, IReadOnlyDictionary<int, string> tagNames)
    {
        var free = d.TryGetProperty("is_free", out var isFree) && isFree.ValueKind == JsonValueKind.True;
        var price = d.TryGetProperty("price_overview", out var po) && po.ValueKind == JsonValueKind.Object ? ReadPriceOverview(po) : null;
        var hasRelease = d.TryGetProperty("release_date", out var rel) && rel.ValueKind == JsonValueKind.Object;
        var comingSoon = hasRelease && rel.TryGetProperty("coming_soon", out var cs) && cs.ValueKind == JsonValueKind.True;
        var steam = new Offer("1", "Steam", free ? 0 : price?.Final, free ? 0 : price?.Initial, price?.Discount ?? 0, $"{Steam}app/{appId}")
        {
            Currency = price?.Currency,
            PriceText = free ? null : price?.Formatted,
            WasText = free ? null : price?.WasFormatted,
        };

        var media = new List<Media>();
        if (d.TryGetProperty("movies", out var movies) && movies.ValueKind == JsonValueKind.Array)
            foreach (var m in movies.EnumerateArray().Take(4))
                if (Str(m, "thumbnail") is { } thumb) media.Add(new Media(thumb, Blank(Str(m, "hls_h264")), true) { Av1 = Blank(Str(m, "dash_av1")) });
        var shots = d.TryGetProperty("screenshots", out var s) && s.ValueKind == JsonValueKind.Array
            ? s.EnumerateArray().Select(x => Str(x, "path_full")).OfType<string>().Take(12).ToList()
            : [];

        return new GameDetail(appId, Str(d, "name") ?? appId, Blank(Decoded(Str(d, "short_description"))), Blank(Str(d, "header_image")),
            comingSoon, hasRelease ? Blank(Str(rel, "date")) : null,
            Strings(d, "developers"), Strings(d, "publishers"),
            d.TryGetProperty("genres", out var g) && g.ValueKind == JsonValueKind.Array ? g.EnumerateArray().Select(x => Str(x, "description")).OfType<string>().ToList() : [],
            d.TryGetProperty("metacritic", out var mc) ? (int?)Num(mc, "score") : null,
            item?.Review, item?.ReviewPercent, item?.ReviewCount ?? 0, free, new[] { steam }, media, shots)
        {
            Art = item?.Art,
            Specs = ReadSpecs(d),
            Tags = item?.Tags.Select(id => tagNames.GetValueOrDefault(id)).OfType<string>().Take(8).ToList() ?? [],
        };
    }

    /// <summary>
    /// The other official launchers selling the game, from CheapShark, Epic
    /// first. CheapShark has three seconds; an answered page is kept twenty
    /// minutes. Null when CheapShark did not answer, so the next ask tries again.
    /// </summary>
    internal async Task<IReadOnlyList<Offer>?> GameOffersAsync(string appId, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_offers.TryGetValue(appId, out var known) && DateTimeOffset.UtcNow - known.At < DetailFresh) return known.Offers;
        }
        var total = Stopwatch.StartNew();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(OfferWait);
        var cheap = await Timed(() => OffersAsync(appId, limit.Token)).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        AppLog.Info($"PERF store offers cheapsharkMs={cheap.Ms} totalMs={total.ElapsedMilliseconds}{(cheap.Value is null ? " failed" : "")}");
        if (cheap.Value is null) return null;
        var list = cheap.Value.Where(o => o.Store != "1").ToList();
        if (list.FindIndex(o => o.Store == "25") is > 0 and var at)
        {
            var row = list[at];
            list.RemoveAt(at);
            list.Insert(0, row);
        }
        lock (_gate) Remember(_offers, appId, list);
        return list;
    }

    /// <summary>
    /// Steam's US store has the page this region's store does not: the game is
    /// sold, just not here, rather than gone from Steam.
    /// </summary>
    internal async Task<bool> SoldOnlyElsewhereAsync(string appId, CancellationToken ct)
    {
        if (Country == "US") return false;
        try
        {
            using var doc = await GetJsonAsync($"{Steam}api/appdetails?appids={appId}&cc=US&filters=basic", ct).ConfigureAwait(false);
            return doc.RootElement.TryGetProperty(appId, out var wrap) && wrap.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// The official launchers selling the Steam app, from CheapShark (Get goes
    /// through its redirect to the store's page); null when CheapShark did not
    /// answer. CheapShark prices are US dollars, so outside the US its rows
    /// carry no price.
    /// </summary>
    private async Task<IReadOnlyList<Offer>?> OffersAsync(string appId, CancellationToken ct)
    {
        var us = Country == "US";
        if (DateTimeOffset.UtcNow < _offersPausedUntil) return null;
        try
        {
            using var doc = await GetJsonAsync($"{CheapShark}deals?steamAppID={appId}", ct).ConfigureAwait(false);
            return doc.RootElement.EnumerateArray()
                .Where(x => Official.ContainsKey(Str(x, "storeID") ?? ""))
                .Select(x => new Offer(Str(x, "storeID")!, Official[Str(x, "storeID")!], Cents(x, "salePrice"), Cents(x, "normalPrice"),
                    (int)Math.Round(Dec(x, "savings")), "https://www.cheapshark.com/redirect?dealID=" + Str(x, "dealID")))
                .GroupBy(o => o.Store).Select(group => group.OrderBy(o => o.Final).First())
                .Select(o => us ? o with { Currency = "USD" } : o with { Final = null, Initial = null, Discount = 0 })
                .ToList();
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            _offersPausedUntil = DateTimeOffset.UtcNow + OfferPause;
            AppLog.Info("Store: CheapShark asked Exo to slow down; other stores' prices wait " + OfferPause.TotalMinutes + " minutes.");
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            AppLog.Debug("Store: CheapShark failed: " + ex.Message);
            return null;
        }
    }

    // Steam sends some descriptions with their entities encoded twice (&amp;amp;): read until plain.
    private static string? Decoded(string? text)
    {
        for (var i = 0; i < 3 && text is not null && text.Contains('&'); i++)
        {
            var next = System.Net.WebUtility.HtmlDecode(text);
            if (next == text) break;
            text = next;
        }
        return text;
    }
}
