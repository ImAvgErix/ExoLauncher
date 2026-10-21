using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// The Store room, built like a launcher's storefront: the big games, top
/// sellers, popular new releases, the most wishlisted and popular upcoming
/// games, Steam's specials and Epic's free games, with search, a wishlist and
/// a page per game. The catalog is Steam's public storefront (ranked lists,
/// item facts, app details); where else a game is sold comes from CheapShark,
/// limited to the official launchers (Steam, Epic, GOG, Ubisoft); Epic's free
/// games come from Epic's own list. Exo sells nothing: Get
/// opens the launcher's own page. Everything is cached on disk, so the room
/// opens at once with the last answer and refreshes behind it.
/// </summary>
internal sealed partial class StoreService : IDisposable
{
    internal sealed record Tile(string AppId, string Name)
    {
        public int? Final { get; init; }
        public int? Initial { get; init; }
        public int Discount { get; init; }
        /// <summary>The currency of the prices when Steam named it ("USD", "EUR"); null when <see cref="PriceText"/> carries Steam's own text.</summary>
        public string? Currency { get; init; }
        /// <summary>Steam's text for <see cref="Final"/> and <see cref="Initial"/> in the region's currency ("$14.99", "19,99€").</summary>
        public string? PriceText { get; init; }
        public string? WasText { get; init; }
        public string? Review { get; init; }
        public int? ReviewPercent { get; init; }
        /// <summary>Free to play: Steam sells it for nothing.</summary>
        public bool Free { get; init; }
        public Art? Art { get; init; }
        /// <summary>Not out yet; <see cref="When"/> says when ("Oct 23", "Feb 2027").</summary>
        public bool Soon { get; init; }
        public string? When { get; init; }
    }

    internal sealed record Shelf(string Id, string Title, IReadOnlyList<Tile> Items);

    /// <summary>Epic's free game; <see cref="Images"/> in the order to try them (wide, thumbnail, tall).</summary>
    internal sealed record FreeGame(string Title, string Url, IReadOnlyList<string> Images, DateTimeOffset? From, DateTimeOffset? Until, bool Now);

    internal sealed record Home(DateTimeOffset At, IReadOnlyList<Tile> Hero, IReadOnlyList<Shelf> Shelves, IReadOnlyList<FreeGame> Free)
    {
        /// <summary>The region the prices are for; a home saved for another region is fetched again.</summary>
        public string? Country { get; init; }
    }

    internal sealed record Offer(string Store, string Name, int? Final, int? Initial, int Discount, string Url)
    {
        public string? Currency { get; init; }
        public string? PriceText { get; init; }
        public string? WasText { get; init; }
    }

    /// <summary>One Steam price: cents (in <see cref="Currency"/> when Steam named it), and Steam's own text for the final and original price ("19,99€").</summary>
    internal sealed record SteamPrice(int? Final, int? Initial, int Discount, string? Currency, string? Formatted, string? WasFormatted = null);

    internal sealed record Media(string Thumb, string? Hls, bool Trailer)
    {
        /// <summary>Steam's AV1 stream (DASH): the same bitrate as the H.264 one, visibly sharper.</summary>
        public string? Av1 { get; init; }
    }

    /// <summary>One line of the PC requirements: OS, Processor, Memory, Graphics, ...</summary>
    internal sealed record Spec(string Label, string? Minimum, string? Recommended);

    internal sealed record GameDetail(
        string AppId, string Name, string? Description, string? Header, bool ComingSoon, string? Released,
        IReadOnlyList<string> Developers, IReadOnlyList<string> Publishers, IReadOnlyList<string> Genres,
        int? Metacritic, string? ReviewWord, int? ReviewPercent, int ReviewCount, bool Free,
        IReadOnlyList<Offer> Offers, IReadOnlyList<Media> Media, IReadOnlyList<string> Screenshots)
    {
        public Art? Art { get; init; }
        public IReadOnlyList<Spec> Specs { get; init; } = [];
        /// <summary>Steam's most voted tags for the game, by name.</summary>
        public IReadOnlyList<string> Tags { get; init; } = [];
    }

    internal sealed record WishItem(string AppId, string Name, DateTimeOffset AddedAt)
    {
        public int? Final { get; init; }
        public int? Initial { get; init; }
        public int Discount { get; init; }
        public string? Currency { get; init; }
        /// <summary>Steam's text for <see cref="Final"/> and <see cref="Initial"/> in the region's currency ("$14.99", "19,99€").</summary>
        public string? PriceText { get; init; }
        public string? WasText { get; init; }
        public DateTimeOffset? CheckedAt { get; init; }
        /// <summary>The price the user was last told about, so a sale is announced once.</summary>
        public int? NotifiedAt { get; init; }
    }

    private static readonly TimeSpan HomeFresh = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan WishFresh = TimeSpan.FromHours(6);
    private const string Steam = "https://store.steampowered.com/";
    private const string CheapShark = "https://www.cheapshark.com/api/1.0/";

    /// <summary>The official launchers in CheapShark's store list.</summary>
    private static readonly Dictionary<string, string> Official = new()
    {
        ["1"] = "Steam", ["25"] = "Epic Games Store", ["7"] = "GOG", ["13"] = "Ubisoft Store",
    };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _homeRefresh = new(1, 1);
    private readonly SemaphoreSlim _wishCheck = new(1, 1);
    private Home? _home;
    private List<WishItem>? _wishlist;
    private Timer? _timer;

    /// <summary>"home": the home page changed. "wishlist": the wishlist or its prices did. The room reads only that one again.</summary>
    internal event Action<string>? Changed;

    /// <summary>A wishlisted game went on sale or got cheaper.</summary>
    internal event Action<WishItem>? SaleFound;

    internal StoreService(string version)
    {
        _http = ExoHttp.Create(TimeSpan.FromSeconds(15), new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectTimeout = TimeSpan.FromSeconds(6),
        });
        // CheapShark asks every client to name itself; Steam and Epic are fine with it too.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"ExoLauncher/{version} (+https://github.com/ImAvgErix/ExoLauncher)");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    private static string Dir => Path.Combine(PathHelper.AppDataDir, "store");

    private static string HomeFile => Path.Combine(Dir, "home-v5.json");

    private static string WishFile => Path.Combine(Dir, "wishlist-v2.json");

    /// <summary>The Windows region as Steam's and Epic's country ("US", "DE"); US when Windows names none.</summary>
    private static string Country
    {
        get
        {
            try
            {
                var cc = RegionInfo.CurrentRegion.TwoLetterISORegionName;
                return cc.Length == 2 && cc.All(char.IsAsciiLetter) ? cc.ToUpperInvariant() : "US";
            }
            catch (ArgumentException)
            {
                return "US";
            }
        }
    }

    /// <summary>
    /// A minute after Exo starts, then every six hours: the wishlist's prices
    /// are checked when it has games, and the home page warms only once the
    /// Store has been opened (its saved home page exists).
    /// </summary>
    internal void Start()
    {
        lock (_gate)
            _timer ??= new Timer(_ => _ = Task.Run(async () =>
            {
                // The home page before cards carried scores and Steam's price text; card facts before they carried release dates.
                foreach (var old in new[] { "home-v4.json", "items-v1.json" })
                    try { File.Delete(Path.Combine(Dir, old)); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                if (File.Exists(HomeFile))
                    try { await HomeAsync(false, CancellationToken.None).ConfigureAwait(false); } catch (Exception ex) { AppLog.Debug("Store: home warm failed: " + ex.Message); }
                if (Wishlist() is { Count: > 0 })
                    try { await CheckWishlistAsync(CancellationToken.None).ConfigureAwait(false); } catch (Exception ex) { AppLog.Debug("Store: wishlist check failed: " + ex.Message); }
            }), null, TimeSpan.FromMinutes(1), WishFresh);
    }

    // ------------------------------------------------------------------ home

    /// <summary>
    /// The saved home page at once (refreshed behind it when older than half an hour); a fresh one when
    /// there is none or on Refresh. A Refresh that fails answers null, so the room can say so.
    /// </summary>
    internal async Task<Home?> HomeAsync(bool refresh, CancellationToken ct)
    {
        Home? home;
        lock (_gate) home = _home;
        home ??= Read<Home>(HomeFile);
        // Saved for another region: fetched again, shown only if that fails.
        if (home is not null && !refresh && home.Country == Country)
        {
            lock (_gate) _home = home;
            if (DateTimeOffset.UtcNow - home.At > HomeFresh) _ = Task.Run(() => RefreshHomeAsync(CancellationToken.None));
            return home;
        }
        return await RefreshHomeAsync(ct).ConfigureAwait(false) ?? (refresh ? null : home);
    }

    /// <summary>A fresh home page, or null when it failed; one already on its way is waited for, not fetched twice.</summary>
    private async Task<Home?> RefreshHomeAsync(CancellationToken ct)
    {
        Home? before;
        lock (_gate) before = _home;
        if (!await _homeRefresh.WaitAsync(0, ct).ConfigureAwait(false))
        {
            await _homeRefresh.WaitAsync(ct).ConfigureAwait(false);
            _homeRefresh.Release();
            // The one waited for failed when it left the page as it was: a Refresh then says so.
            lock (_gate) return ReferenceEquals(_home, before) ? null : _home;
        }
        try
        {
            var total = Stopwatch.StartNew();
            var sellers = Timed(() => QueryAsync("top sellers", Released, 200, ct));
            var upcoming = Timed(() => QueryAsync("upcoming", ComingSoon, 80, ct));
            var specials = Timed(() => QueryAsync("on sale", OnSale, 60, ct));
            var wished = Timed(async () => await WithItemsAsync(await ListAsync("filter=popularwishlist", 80, ct).ConfigureAwait(false), ct).ConfigureAwait(false));
            var free = Timed(() => FreeGamesAsync(ct));
            await Task.WhenAll(sellers, upcoming, specials, wished, free).ConfigureAwait(false);
            var home = BuildHome(sellers.Result.Value, upcoming.Result.Value, wished.Result.Value, specials.Result.Value, free.Result.Value) with { Country = Country };
            AppLog.Info($"PERF store home sellersMs={sellers.Result.Ms} upcomingMs={upcoming.Result.Ms} saleMs={specials.Result.Ms} wishedMs={wished.Result.Ms} freeMs={free.Result.Ms} totalMs={total.ElapsedMilliseconds}");
            if (home.Hero.Count == 0 && home.Shelves.All(s => s.Items.Count == 0)) return null;
            Write(HomeFile, home);
            lock (_gate) _home = home;
            Raise("home");
            return home;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            AppLog.Info("Store: home refresh failed: " + ex.Message);
            return null;
        }
        finally
        {
            _homeRefresh.Release();
        }
    }

    /// <summary>One of Steam's store lists (most wishlisted): games only, no demos, soundtracks, hardware or bundles.</summary>
    private async Task<IReadOnlyList<Tile>> ListAsync(string filter, int count, CancellationToken ct)
    {
        try
        {
            using var doc = await GetJsonAsync($"{Steam}search/results/?{filter}&json=1&count={count + 20}&cc={Country}&l=english&category1=998", ct).ConfigureAwait(false);
            var tiles = new List<Tile>();
            foreach (var item in doc.RootElement.GetProperty("items").EnumerateArray())
            {
                if (Str(item, "name") is not { } name || NotAGame().IsMatch(name)) continue;
                if (Str(item, "logo") is not { } logo || AppInLogo().Match(logo) is not { Success: true } m) continue;
                if (tiles.All(t => t.AppId != m.Groups[1].Value)) tiles.Add(new Tile(m.Groups[1].Value, name.Trim()));
                if (tiles.Count == count) break;
            }
            return tiles;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            AppLog.Info($"Store: list {filter} failed: {ex.Message}");
            return [];
        }
    }

    /// <summary>Steam prices in the region's currency for many apps at once (appdetails with filters=price_overview).</summary>
    private async Task<Dictionary<string, SteamPrice>> PricesAsync(IEnumerable<string> appIds, CancellationToken ct)
    {
        var country = Country;
        var result = new Dictionary<string, SteamPrice>(StringComparer.Ordinal);
        foreach (var chunk in appIds.Chunk(100))
        {
            using var doc = await GetJsonAsync($"{Steam}api/appdetails?appids={string.Join(',', chunk)}&filters=price_overview&cc={country}", ct).ConfigureAwait(false);
            foreach (var id in chunk)
            {
                if (!doc.RootElement.TryGetProperty(id, out var wrap) || !wrap.TryGetProperty("data", out var data)) continue;
                result[id] = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("price_overview", out var p)
                    ? ReadPriceOverview(p)
                    : new SteamPrice(0, 0, 0, null, null);
            }
        }
        return result;
    }

    /// <summary>appdetails' price_overview: cents, discount, currency and Steam's text for both prices.</summary>
    private static SteamPrice ReadPriceOverview(JsonElement p) =>
        new((int)Num(p, "final"), (int)Num(p, "initial"), (int)Num(p, "discount_percent"), Blank(Str(p, "currency")), Blank(Str(p, "final_formatted")), Blank(Str(p, "initial_formatted")));

    /// <summary>Epic's free games: those free now, and those free next.</summary>
    private async Task<IReadOnlyList<FreeGame>> FreeGamesAsync(CancellationToken ct)
    {
        try
        {
            using var doc = await GetJsonAsync($"https://store-site-backend-static-ipv4.ak.epicgames.com/freeGamesPromotions?locale=en-US&country={Country}&allowCountries={Country}", ct).ConfigureAwait(false);
            var list = new List<FreeGame>();
            var elements = doc.RootElement.GetProperty("data").GetProperty("Catalog").GetProperty("searchStore").GetProperty("elements");
            foreach (var e in elements.EnumerateArray())
            {
                if (Str(e, "title") is not { } title || !e.TryGetProperty("promotions", out var promos) || promos.ValueKind != JsonValueKind.Object) continue;
                var now = FreeOffer(promos, "promotionalOffers");
                var next = now is null ? FreeOffer(promos, "upcomingPromotionalOffers") : null;
                if ((now ?? next) is not { } offer) continue;
                var slug = Blank(Str(e, "productSlug"))?.Replace("/home", "", StringComparison.Ordinal) ?? FirstSlug(e, "offerMappings") ?? FirstSlug(e, "catalogNs");
                if (slug is null) continue;
                list.Add(new FreeGame(title, "https://store.epicgames.com/p/" + slug, EpicImages(e), offer.From, offer.Until, now is not null));
            }
            return list.OrderByDescending(f => f.Now).ThenBy(f => f.From).ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            AppLog.Info("Store: Epic free games failed: " + ex.Message);
            return [];
        }
    }

    /// <summary>
    /// The free games as they stand at <paramref name="now"/>: the saved home page can be older than
    /// a week's turn (offline, or minutes after Epic's switch), so one whose free days ended is left
    /// out and one whose free days began is free now.
    /// </summary>
    internal static IReadOnlyList<FreeGame> FreeAt(IReadOnlyList<FreeGame> free, DateTimeOffset now) =>
        free.Where(f => f.Until is not { } until || until > now)
            .Select(f => !f.Now && f.From is { } from && from <= now ? f with { Now = true } : f)
            .OrderByDescending(f => f.Now).ThenBy(f => f.From)
            .ToList();

    private static (DateTimeOffset? From, DateTimeOffset? Until)? FreeOffer(JsonElement promos, string name)
    {
        if (!promos.TryGetProperty(name, out var groups) || groups.ValueKind != JsonValueKind.Array) return null;
        foreach (var group in groups.EnumerateArray())
            if (group.TryGetProperty("promotionalOffers", out var offers) && offers.ValueKind == JsonValueKind.Array)
                foreach (var offer in offers.EnumerateArray())
                    if (offer.TryGetProperty("discountSetting", out var discount) && Num(discount, "discountPercentage") == 0)
                        return (Date(Str(offer, "startDate")), Date(Str(offer, "endDate")));
        return null;
    }

    private static string? FirstSlug(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        if (name == "catalogNs" && !v.TryGetProperty("mappings", out v)) return null;
        if (v.ValueKind != JsonValueKind.Array) return null;
        foreach (var m in v.EnumerateArray()) if (Blank(Str(m, "pageSlug")) is { } slug) return slug;
        return null;
    }

    /// <summary>The free game's art in the order a wide card wants it; the card tries the next when one fails.</summary>
    private static List<string> EpicImages(JsonElement e)
    {
        var urls = new List<string>();
        if (!e.TryGetProperty("keyImages", out var images) || images.ValueKind != JsonValueKind.Array) return urls;
        foreach (var type in new[] { "OfferImageWide", "DieselStoreFrontWide", "Thumbnail", "OfferImageTall" })
            foreach (var image in images.EnumerateArray())
                if (Str(image, "type") == type && Blank(Str(image, "url")) is { } url && !urls.Contains(url)) urls.Add(url);
        return urls;
    }

    // ------------------------------------------------------------------ wishlist

    /// <summary>The wishlist, or null while wishlist.json cannot be read (busy), so the room never shows it as empty.</summary>
    internal IReadOnlyList<WishItem>? Wishlist()
    {
        lock (_gate) return LoadWishlistLocked()?.ToList();
    }

    /// <summary>
    /// The wishlist, or null while wishlist.json cannot be read. A failed read
    /// is never cached or saved: caching it as empty and saving that on the next
    /// change erased the whole list. Content that can never parse is moved aside.
    /// </summary>
    private List<WishItem>? LoadWishlistLocked()
    {
        if (_wishlist is not null) return _wishlist;
        try
        {
            if (!File.Exists(WishFile)) return _wishlist = [];
            return _wishlist = JsonSerializer.Deserialize<List<WishItem>>(File.ReadAllText(WishFile), Json) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            AppLog.Warn("Store: wishlist.json could not be read: " + ex.GetType().Name);
            return PathHelper.MoveAsideUnreadable(WishFile) ? _wishlist = [] : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("Store: wishlist.json could not be read: " + ex.GetType().Name);
            return null;
        }
    }

    /// <summary>
    /// Adds or removes a game and answers the new list; the caller shows it, so
    /// nothing is raised here. A new game's price is checked behind it, which
    /// raises "wishlist" once when it lands. Null, with nothing changed, while
    /// wishlist.json cannot be read.
    /// </summary>
    internal IReadOnlyList<WishItem>? Wish(string appId, string name, bool add)
    {
        lock (_gate)
        {
            if (LoadWishlistLocked() is not { } list) return null;
            list.RemoveAll(w => w.AppId == appId);
            if (add) list.Insert(0, new WishItem(appId, name, DateTimeOffset.UtcNow));
            Write(WishFile, list);
        }
        if (add) _ = Task.Run(async () =>
        {
            try { await CheckWishlistAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { AppLog.Debug("Store: wishlist check failed: " + ex.Message); }
        });
        return Wishlist();
    }

    /// <summary>Steam prices for the wishlist; a game on sale below the price last announced raises <see cref="SaleFound"/> once. Without <paramref name="raise"/> the caller shows the list itself.</summary>
    internal async Task CheckWishlistAsync(CancellationToken ct, bool raise = true)
    {
        // One check at a time, so two overlapping checks never announce the same sale twice.
        await _wishCheck.WaitAsync(ct).ConfigureAwait(false);
        try { await CheckWishlistOnceAsync(ct, raise).ConfigureAwait(false); }
        finally { _wishCheck.Release(); }
    }

    private async Task CheckWishlistOnceAsync(CancellationToken ct, bool raise)
    {
        if (Wishlist() is not { Count: > 0 } list) return;
        var prices = await PricesAsync(list.Select(w => w.AppId), ct).ConfigureAwait(false);
        var updated = new Dictionary<string, WishItem>(StringComparer.Ordinal);
        foreach (var w in list)
        {
            if (!prices.TryGetValue(w.AppId, out var p)) continue;
            var next = w with { Final = p.Final, Initial = p.Initial, Discount = p.Discount, Currency = p.Currency, PriceText = p.Formatted, WasText = p.WasFormatted, CheckedAt = DateTimeOffset.UtcNow };
            // A price last announced in another currency (the region changed) says nothing about this one.
            var told = w.Currency is null || w.Currency == p.Currency ? w.NotifiedAt : null;
            if (p.Discount > 0 && p.Final is { } final && (told is null || final < told))
            {
                next = next with { NotifiedAt = final };
                if (w.CheckedAt is not null) SaleFound?.Invoke(next);
            }
            else if (p.Discount == 0) next = next with { NotifiedAt = null };
            updated[w.AppId] = next;
        }
        lock (_gate)
        {
            if (_wishlist is not { } current) return;
            for (var i = 0; i < current.Count; i++)
                if (updated.TryGetValue(current[i].AppId, out var fresh)) current[i] = fresh;
            Write(WishFile, current);
        }
        if (raise) Raise("wishlist");
    }

    // ------------------------------------------------------------------ helpers

    private const int MaxJsonBytes = 16 * 1024 * 1024;

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        // HttpClient.Timeout stops at the headers; the deadline also bounds a stalled body.
        using var deadline = ExoHttpTransport.Deadline(_http, ct);
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await ExoHttpTransport.ReadJsonAsync(response.Content, MaxJsonBytes, deadline.Token).ConfigureAwait(false)
            ?? throw new JsonException("Store: empty, invalid or oversized JSON from " + new Uri(url).Host + ".");
    }

    /// <summary>A call and how long it took, for the PERF lines.</summary>
    private static async Task<(T Value, long Ms)> Timed<T>(Func<Task<T>> start)
    {
        var sw = Stopwatch.StartNew();
        var value = await start().ConfigureAwait(false);
        return (value, sw.ElapsedMilliseconds);
    }

    private static T? Read<T>(string path) where T : class
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return null; }
    }

    private static void Write<T>(string path, T value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, Json);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Debug("Store: could not save " + Path.GetFileName(path) + ": " + ex.Message);
        }
    }

    private void Raise(string what)
    {
        try { Changed?.Invoke(what); } catch { }
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number => v.GetRawText(), _ => null }
            : null;

    private static IReadOnlyList<string> Strings(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
            : [];

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static decimal Dec(JsonElement e, string name) =>
        decimal.TryParse(Str(e, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0m;

    private static int? Cents(JsonElement e, string name) =>
        decimal.TryParse(Str(e, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? (int)Math.Round(d * 100) : null;

    private static long Num(JsonElement e, string name) =>
        long.TryParse(Str(e, name)?.Split('.')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;

    private static DateTimeOffset? Date(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null;

    public void Dispose()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            _search?.Dispose();
            _search = null;
        }
        _http.Dispose();
        _homeRefresh.Dispose();
    }

    /// <summary>Store lists are for games: demos, soundtracks, artbooks, season passes, DLC packs, hardware and bundles are left out.</summary>
    [GeneratedRegex(@"\b(demo|playtest|soundtrack|ost|artbook|art book|season pass|dlc|expansion pass|upgrade|bundle|costume|skin pack|steam deck|valve index|steam controller|steam frame|steam machine)\b", RegexOptions.IgnoreCase)]
    private static partial Regex NotAGame();

    /// <summary>The app id in a Steam search result's capsule URL (…/steam/apps/1422450/…).</summary>
    [GeneratedRegex(@"/steam/apps/(\d+)/")]
    private static partial Regex AppInLogo();
}
