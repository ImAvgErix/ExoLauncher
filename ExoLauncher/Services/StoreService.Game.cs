using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// Search and a game's page. The page answers as soon as Steam's app details
/// and the game's item facts (art, reviews, tags) land; where else it is
/// sold (CheapShark) and its Recent reviews are calls of their own, made only
/// once the page opens, so a slow outside store never holds the page. A page
/// asked for twice at once (a hover warming it, then the click) is fetched once.
/// </summary>
internal sealed partial class StoreService
{
    private static readonly TimeSpan DetailFresh = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan SearchFresh = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ItemWait = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan OfferWait = TimeSpan.FromSeconds(6);
    /// <summary>Other stores' prices change seldom: an answer is asked for again after this long.</summary>
    private static readonly TimeSpan OfferFresh = TimeSpan.FromHours(6);
    /// <summary>CheapShark limits how often it is asked: once it says so (429), it is left alone this long.</summary>
    private static readonly TimeSpan OfferPause = TimeSpan.FromMinutes(10);
    private DateTimeOffset _offersPausedUntil;

    private readonly Dictionary<string, (DateTimeOffset At, GameDetail Detail)> _details = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<GameDetail?>> _detailTasks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (DateTimeOffset At, OtherStores Stores)> _offers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (DateTimeOffset At, RecentReviews Recent)> _recent = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (DateTimeOffset At, IReadOnlyList<Tile> Hits)> _searches = new(StringComparer.Ordinal);
    private CancellationTokenSource? _search;

    /// <summary>
    /// Steam's search with each hit's card facts. The same query within five
    /// minutes is answered from memory. The Store room's search as you type
    /// (<paramref name="typed"/>) cancels its own one still running, which then
    /// answers null; a lookup (Friends are playing asking whether the Store sells
    /// a title) neither cancels one nor is cancelled, so it never ends the search
    /// you typed. A timeout is an error like any other, not a newer query.
    /// </summary>
    internal async Task<IReadOnlyList<Tile>?> SearchAsync(string query, bool typed, CancellationToken ct)
    {
        query = query.Trim();
        if (query.Length < 2) return [];
        var key = query.ToLowerInvariant();
        var sw = Stopwatch.StartNew();
        lock (_gate)
            if (_searches.TryGetValue(key, out var known) && DateTimeOffset.UtcNow - known.At < SearchFresh) return known.Hits;
        var mine = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (typed)
        {
            CancellationTokenSource? previous;
            lock (_gate) (previous, _search) = (_search, mine);
            try { previous?.Cancel(); } catch (ObjectDisposedException) { }
        }
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

    internal static GameDetail ReadDetail(string appId, JsonElement d, Item? item, IReadOnlyDictionary<int, string> tagNames)
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
        // Steam's categories: 28 full controller support, 18 partial; 55 to 58 DualShock and DualSense (USB and Bluetooth).
        var categories = d.TryGetProperty("categories", out var cat) && cat.ValueKind == JsonValueKind.Array
            ? cat.EnumerateArray().Select(x => (int)Num(x, "id")).ToHashSet()
            : [];
        var controller = categories.Contains(28) ? "full" : categories.Contains(18) ? "partial" : Str(d, "controller_support") is ("full" or "partial") and var said ? said : null;

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
            Drm = Notice(Str(d, "drm_notice")),
            Account = Notice(Str(d, "ext_user_account_notice")) is { } account ? Blank(LinksToSteam().Replace(account, "")) : null,
            Controller = controller,
            PlayStation = categories.Overlaps([55, 56, 57, 58]),
            Languages = ReadLanguages(Str(d, "supported_languages")),
        };
    }

    /// <summary>A notice Steam writes in HTML ("Denuvo Anti-tamper&lt;br&gt;5 a day machine activation limit") as one line of words.</summary>
    private static string? Notice(string? html)
    {
        if (Blank(html) is not { } text) return null;
        text = Decoded(Tag().Replace(LineBreak().Replace(text, ", "), "")) ?? "";
        return Blank(Spaces().Replace(text, " ").Trim(' ', ','));
    }

    /// <summary>
    /// Steam's language list ("English&lt;strong&gt;*&lt;/strong&gt;, French, …&lt;br&gt;&lt;strong&gt;*&lt;/strong&gt;languages with
    /// full audio support") as names, without the full-audio marks or the footnote after the break.
    /// </summary>
    private static IReadOnlyList<string> ReadLanguages(string? html)
    {
        if (Blank(html) is not { } text) return [];
        var list = Tag().Replace(LineBreak().Split(text)[0], "").Replace("*", "", StringComparison.Ordinal);
        return list.Split(',').Select(name => Blank(Decoded(name))).OfType<string>()
            .Where(name => !name.Contains("audio support", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// The other official launchers selling the game, from CheapShark, Epic
    /// first. CheapShark has six seconds and a second try (a busy CheapShark
    /// timed out at three often enough that pages said "Other stores
    /// unavailable"); an answered page is kept six hours, and an older answer
    /// stands in when CheapShark doesn't answer. Null only when there is none.
    /// </summary>
    internal async Task<OtherStores?> GameOffersAsync(string appId, CancellationToken ct)
    {
        OtherStores? older = null;
        lock (_gate)
        {
            if (_offers.TryGetValue(appId, out var known))
            {
                if (DateTimeOffset.UtcNow - known.At < OfferFresh) return known.Stores;
                older = known.Stores;
            }
        }
        var total = Stopwatch.StartNew();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(OfferWait);
        var cheap = await Timed(() => OffersAsync(appId, limit.Token)).ConfigureAwait(false);
        if (cheap.Value is null && !limit.IsCancellationRequested && DateTimeOffset.UtcNow >= _offersPausedUntil)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            cheap = await Timed(() => OffersAsync(appId, limit.Token)).ConfigureAwait(false);
        }
        ct.ThrowIfCancellationRequested();
        AppLog.Info($"PERF store offers cheapsharkMs={cheap.Ms} totalMs={total.ElapsedMilliseconds}{(cheap.Value is null ? " failed" : "")}");
        if (cheap.Value is null) return older;
        var list = cheap.Value.Offers.Where(o => o.Store != "1").ToList();
        if (list.FindIndex(o => o.Store == "25") is > 0 and var at)
        {
            var row = list[at];
            list.RemoveAt(at);
            list.Insert(0, row);
        }
        var stores = cheap.Value with { Offers = list };
        lock (_gate) Remember(_offers, appId, stores);
        return stores;
    }

    /// <summary>
    /// Steam's Recent reviews for a game page: the last 30 days in every language, Steam purchases only and review
    /// bombs left out, as Steam's own page counts them (IUserReviewsService/GetAppReviews: documented, no key). Asked
    /// only when a page opens, never to warm one, since keyless calls share a lower limit. Kept twenty minutes; null
    /// when Steam did not answer within three seconds or said no (429), so the page shows a dash rather than waiting.
    /// </summary>
    internal async Task<RecentReviews?> RecentReviewsAsync(string appId, CancellationToken ct)
    {
        lock (_gate)
            if (_recent.TryGetValue(appId, out var known) && DateTimeOffset.UtcNow - known.At < DetailFresh) return known.Recent;
        // The window ends on the next whole hour, so the same question all hour can be answered from Steam's cache.
        var end = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 3600 + 1) * 3600;
        var input = JsonSerializer.Serialize(new
        {
            appid = long.Parse(appId, CultureInfo.InvariantCulture),
            filter = 1, languages = new[] { "all" }, review_type = 0, num_per_page = 1,
            date_range_start = end - (long)TimeSpan.FromDays(30).TotalSeconds, date_range_end = end,
        });
        var sw = Stopwatch.StartNew();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(ItemWait);
        try
        {
            using var doc = await GetJsonAsync("https://api.steampowered.com/IUserReviewsService/GetAppReviews/v1/?input_json=" + Uri.EscapeDataString(input), limit.Token).ConfigureAwait(false);
            if (ReadRecentReviews(doc.RootElement) is not { } recent) return null;
            lock (_gate) Remember(_recent, appId, recent);
            AppLog.Info($"PERF store reviews totalMs={sw.ElapsedMilliseconds}");
            return recent;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            ct.ThrowIfCancellationRequested();
            AppLog.Debug("Store: recent reviews failed: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// GetAppReviews' query_summary as Recent reviews; null when the answer carries none. Under ten reviews Steam gives
    /// no score (review_score 0, "3 user reviews") and its page shows no Recent line, so neither does Exo: no word.
    /// The share positive is rounded down, as Steam's own percentages are.
    /// </summary>
    internal static RecentReviews? ReadRecentReviews(JsonElement root)
    {
        if (!root.TryGetProperty("response", out var r) || !r.TryGetProperty("query_summary", out var s) || s.ValueKind != JsonValueKind.Object
            || Str(s, "total_reviews") is null) return null;
        var total = (int)Num(s, "total_reviews");
        var positive = (int)Num(s, "total_positive");
        var scored = total > 0 && Num(s, "review_score") > 0;
        return new RecentReviews(scored ? Blank(Str(s, "review_score_desc")) : null, total > 0 ? (int)(100L * positive / total) : 0, total);
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
    /// through its redirect to the store's page), and the game's lowest price
    /// ever; null when CheapShark did not answer. CheapShark prices are US
    /// dollars, so outside the US its rows carry no price and the lowest price
    /// is not asked for.
    /// </summary>
    private async Task<OtherStores?> OffersAsync(string appId, CancellationToken ct)
    {
        var us = Country == "US";
        if (DateTimeOffset.UtcNow < _offersPausedUntil) return null;
        try
        {
            List<Offer> offers;
            string? gameId;
            using (var doc = await GetJsonAsync($"{CheapShark}deals?steamAppID={appId}", ct).ConfigureAwait(false))
            {
                gameId = doc.RootElement.EnumerateArray().Select(x => Str(x, "gameID")).FirstOrDefault(id => id is not null);
                offers = doc.RootElement.EnumerateArray()
                    .Where(x => Official.ContainsKey(Str(x, "storeID") ?? ""))
                    .Select(x => new Offer(Str(x, "storeID")!, Official[Str(x, "storeID")!], Cents(x, "salePrice"), Cents(x, "normalPrice"),
                        (int)Math.Round(Dec(x, "savings")), "https://www.cheapshark.com/redirect?dealID=" + Str(x, "dealID")))
                    .GroupBy(o => o.Store).Select(group => group.OrderBy(o => o.Final).First())
                    .Select(o => us ? o with { Currency = "USD" } : o with { Final = null, Initial = null, Discount = 0 })
                    .ToList();
            }
            return new OtherStores(offers, us && gameId is not null ? await LowestAsync(gameId, ct).ConfigureAwait(false) : null);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            PauseOffers();
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            AppLog.Debug("Store: CheapShark failed: " + ex.Message);
            return null;
        }
    }

    /// <summary>The lowest price CheapShark has seen for its game (games?id=); null when it has none or did not answer, which leaves the other stores' rows as they are.</summary>
    private async Task<Lowest?> LowestAsync(string gameId, CancellationToken ct)
    {
        try
        {
            using var doc = await GetJsonAsync($"{CheapShark}games?id={Uri.EscapeDataString(gameId)}", ct).ConfigureAwait(false);
            return ReadLowest(doc.RootElement);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            PauseOffers();
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            AppLog.Debug("Store: CheapShark's lowest price failed: " + ex.Message);
            return null;
        }
    }

    /// <summary>CheapShark's cheapestPriceEver ({ "price": "20.99", "date": 1782413542 }); null without a price and a day.</summary>
    internal static Lowest? ReadLowest(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("cheapestPriceEver", out var low)
        && Cents(low, "price") is int final and > 0 && Num(low, "date") is > 0 and var at
            ? new Lowest(final, DateTimeOffset.FromUnixTimeSeconds(at))
            : null;

    private void PauseOffers()
    {
        _offersPausedUntil = DateTimeOffset.UtcNow + OfferPause;
        AppLog.Info("Store: CheapShark asked Exo to slow down; other stores' prices wait " + OfferPause.TotalMinutes + " minutes.");
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

    [GeneratedRegex(@"<\s*br\s*/?\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreak();

    /// <summary>Steam's aside on an account notice ("Ubisoft Account (Supports Linking to Steam Account)"): the account is what matters.</summary>
    [GeneratedRegex(@"\s*\(\s*supports linking to steam account\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex LinksToSteam();
}
