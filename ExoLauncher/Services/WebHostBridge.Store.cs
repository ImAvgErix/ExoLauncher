using System.Globalization;
using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>The Store room: store.home, store.calendar, store.search, store.game, store.gameOffers, store.wishlist, store.open; store.changed (what: home or wishlist) and store.sale.</summary>
public sealed partial class WebHostBridge
{
    /// <summary>
    /// A Get button or a free game: the store page in its own launcher when
    /// one is installed (see StoreLinks), else in the browser. Store pages and
    /// CheapShark redirects only; GG.deals and everything else use shell.openUrl.
    /// </summary>
    private async Task<object> StoreOpenAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        if (!Uri.TryCreate(ReadString(p, hasParams, "url"), UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps || !IsStoreHost(url.Host))
            return new { ok = false };
        var owner = App.MainAppWindow is { } window ? WinRT.Interop.WindowNative.GetWindowHandle(window) : 0;
        var page = owner == 0 ? url : await StoreLinks.ResolveAsync(url, owner, ct).ConfigureAwait(true) ?? url;
        if (page.Scheme != Uri.UriSchemeHttps || !IsStoreHost(page.Host)) page = url;
        try
        {
            if (StoreLinks.AppUri(page) is { } app)
            {
                if (app.StartsWith("steam:", StringComparison.Ordinal)) return OpenSteamProtocol(app);
                StoreLinks.StartApp(app);
                return new { ok = true };
            }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(page.AbsoluteUri) { UseShellExecute = true });
            return new { ok = true };
        }
        catch (Exception ex) { return OpenFailed("that page", ex); }
    }

    private static bool IsStoreHost(string host)
    {
        static bool Under(string host, string domain) => host.Equals(domain, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
        return Under(host, "cheapshark.com") || Under(host, "steampowered.com") || Under(host, "epicgames.com") || Under(host, "gog.com") || Under(host, "ubisoft.com") || Under(host, "ubi.com");
    }


    private async Task<object> StoreHomeAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        // Off the UI thread: the first ask reads and parses the saved home page.
        var refresh = ReadBool(p, hasParams, "refresh") == true;
        var home = await Task.Run(() => _services.Store.HomeAsync(refresh, ct), ct).ConfigureAwait(false);
        if (home is null) return new { ok = false, message = "The store could not be reached. Check your connection and try again." };
        var (played, owned) = StoreTasteInputs();
        // The last page made for this person at once, the new one following by store.changed; a Refresh waits for it.
        home = await _services.Store.ForYouHomeAsync(home, played, owned, wait: refresh, ct).ConfigureAwait(false);
        return new
        {
            ok = true,
            at = home.At.ToString("O", CultureInfo.InvariantCulture),
            hero = home.Hero.Select(MapTile),
            shelves = home.Shelves.Select(s => new { id = s.Id, title = s.Title, items = s.Items.Select(MapTile) }),
        };
    }

    /// <summary>
    /// The calendar for this PC: Steam's public lists placed by day, ordered by the wishlist and by the tags of the Steam
    /// games played most. The library supplies hours and ownership here, in native code; the page gets only the result.
    /// </summary>
    private async Task<object> StoreCalendarAsync(CancellationToken ct)
    {
        var (played, owned) = StoreTasteInputs();
        StoreService.Calendar? calendar;
        try { calendar = await Task.Run(() => _services.Store.CalendarAsync(played, owned.AppIds, ct), ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            calendar = null;
        }
        if (calendar is null) return new { ok = false, message = "The store could not be reached. Check your connection and try again." };
        return new
        {
            ok = true,
            at = calendar.At.ToString("O", CultureInfo.InvariantCulture),
            interests = calendar.Interests,
            days = calendar.Days.Select(d => new
            {
                date = d.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                entries = d.Entries.Select(e => new { tile = MapTile(e.Tile), wished = e.Wished, owned = e.Owned, earlyAccess = e.EarlyAccess, why = e.Why }),
            }),
        };
    }

    /// <summary>
    /// What the Store tailors itself by, read here from the library so paths and ids stay native: every game played
    /// (any store; a Steam game by its app id) with its hours, and what the library has, by Steam app id and by title.
    /// </summary>
    private (IReadOnlyList<StoreService.Played> Played, StoreService.OwnedGames Owned) StoreTasteInputs()
    {
        static string? SteamId(Models.GameEntry g) => g.Id.StartsWith("steam:", StringComparison.Ordinal) && g.Id.Length > 6 ? g.Id[6..] : null;
        var library = _services.Library.PeekCachedLibrary();
        var played = library.Where(g => g.PlaytimeMinutes is > 0)
            .Select(g => new StoreService.Played(g.Id, SteamId(g), g.Title, g.PlaytimeMinutes!.Value / 60.0, g.LastPlayedUtc)).ToList();
        var mine = library.Where(g => g.Owned || g.Installed).ToList();
        // A Steam game is known by its app id; only a game from another store goes by its title (owning Resident Evil 4
        // from 2005 on Steam must not hide its remake, another app with the same name).
        return (played, new StoreService.OwnedGames(
            mine.Select(SteamId).OfType<string>().ToHashSet(StringComparer.Ordinal),
            mine.Where(g => SteamId(g) is null).Select(g => StoreService.TitleKey(g.Title)).Where(k => k.Length > 0).ToHashSet(StringComparer.Ordinal)));
    }

    private static object MapTile(StoreService.Tile t) => new
    {
        appId = t.AppId, name = t.Name, final = t.Final, initial = t.Initial, discount = t.Discount, currency = t.Currency,
        priceText = t.PriceText, wasText = t.WasText, review = t.Review, reviewPercent = t.ReviewPercent, free = t.Free,
        art = MapArt(t.Art), soon = t.Soon, when = t.When, wished = t.Wished, similarTo = t.SimilarTo,
    };

    private static object? MapArt(StoreService.Art? a) => a is null ? null : new { poster = a.Poster, hero = a.Hero, capsule = a.Capsule, header = a.Header };

    private static object MapOffer(StoreService.Offer o) => new
    {
        store = o.Store, name = o.Name, final = o.Final, initial = o.Initial, discount = o.Discount, currency = o.Currency,
        priceText = o.PriceText, wasText = o.WasText, url = o.Url,
    };

    /// <summary>A search a newer one replaced answers ok false with no message; the room has already moved on and drops it.</summary>
    private async Task<object> StoreSearchAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        try
        {
            var hits = await _services.Store.SearchAsync(ReadString(p, hasParams, "query") ?? "", ct).ConfigureAwait(false);
            if (hits is null) return new { ok = false, items = Array.Empty<object>() };
            return new { ok = true, items = hits.Select(MapTile) };
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            return new { ok = false, message = "Search could not reach the store. Try again.", items = Array.Empty<object>() };
        }
    }

    private async Task<object> StoreGameAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        if (ReadString(p, hasParams, "appId") is not { Length: > 0 and <= 10 } appId || !appId.All(char.IsAsciiDigit)) return new { ok = false, message = "No game." };
        try
        {
            var g = await _services.Store.GameAsync(appId, ct).ConfigureAwait(false);
            if (g is null)
                return new { ok = false, message = await _services.Store.SoldOnlyElsewhereAsync(appId, ct).ConfigureAwait(false)
                    ? "This game isn't sold in your region on Steam."
                    : "Steam does not list this game anymore." };
            var fit = StoreService.FitSpecs(g.Specs, Helpers.SystemSpecs.Read());
            return new
            {
                ok = true, appId = g.AppId, name = g.Name, description = g.Description, header = g.Header,
                comingSoon = g.ComingSoon, released = g.Released, developers = g.Developers, publishers = g.Publishers, genres = g.Genres,
                metacritic = g.Metacritic, reviewWord = g.ReviewWord, reviewPercent = g.ReviewPercent, reviewCount = g.ReviewCount, free = g.Free,
                tags = g.Tags,
                offers = g.Offers.Select(MapOffer),
                media = g.Media.Select(m => new { thumb = m.Thumb, hls = m.Hls, av1 = m.Av1 }),
                specs = fit.Rows.Select(s => new { label = s.Label, minimum = s.Minimum, recommended = s.Recommended, mine = s.Mine, meets = s.Meets }),
                fit = fit.Verdict,
                screenshots = g.Screenshots,
                art = MapArt(g.Art),
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            return new { ok = false, message = "The store could not be reached." };
        }
    }

    /// <summary>The other official launchers for a game page, Epic first, from CheapShark.</summary>
    private async Task<object> StoreGameOffersAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        if (ReadString(p, hasParams, "appId") is not { Length: > 0 and <= 10 } appId || !appId.All(char.IsAsciiDigit)) return new { ok = false, offers = Array.Empty<object>() };
        try
        {
            // CheapShark did not answer: ok false, so the page does not keep "sold nowhere else".
            if (await _services.Store.GameOffersAsync(appId, ct).ConfigureAwait(false) is not { } offers) return new { ok = false, offers = Array.Empty<object>() };
            return new { ok = true, offers = offers.Select(MapOffer) };
        }
        catch (OperationCanceledException)
        {
            return new { ok = false, offers = Array.Empty<object>() };
        }
    }

    /// <summary>
    /// get, add (appId, name), remove (appId), check (prices now). Off the UI thread: the list and
    /// the card facts are files. A wishlist that cannot be read answers ok false with a message
    /// and no items, so the room keeps what it shows instead of showing it empty.
    /// </summary>
    private Task<object> StoreWishlistAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        var action = ReadString(p, hasParams, "action") ?? "get";
        var appId = ReadString(p, hasParams, "appId") is { Length: > 0 and <= 10 } id && id.All(char.IsAsciiDigit) ? id : null;
        var name = ReadString(p, hasParams, "name") is { } given && given.Length > 200 ? given[..200] : ReadString(p, hasParams, "name");
        return Task.Run(() => StoreWishlistOffThreadAsync(action, appId, name, ct), ct);
    }

    private async Task<object> StoreWishlistOffThreadAsync(string action, string? appId, string? name, CancellationToken ct)
    {
        var list = action switch
        {
            "add" when appId is not null => _services.Store.Wish(appId, name ?? appId, add: true),
            "remove" when appId is not null => _services.Store.Wish(appId, "", add: false),
            _ => _services.Store.Wishlist(),
        };
        var ok = true;
        if (action == "check" && list is not null)
        {
            try { await _services.Store.CheckWishlistAsync(ct, raise: false).ConfigureAwait(false); }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException) { ok = false; }
            list = _services.Store.Wishlist();
        }
        if (list is null) return new { ok = false, message = "Your wishlist could not be read. Try again.", items = Array.Empty<object>() };
        // Card facts from the saved cache at once; old ones refresh behind it.
        IReadOnlyDictionary<string, StoreService.Item> items;
        try { items = await _services.Store.ItemsAsync(list.Select(w => w.AppId), ct, staleOk: true).ConfigureAwait(false); }
        catch (OperationCanceledException) { items = new Dictionary<string, StoreService.Item>(); }
        return new
        {
            ok,
            items = list.Select(w =>
            {
                var item = items.GetValueOrDefault(w.AppId);
                return new
                {
                    appId = w.AppId, name = w.Name, addedAt = w.AddedAt.ToString("O", CultureInfo.InvariantCulture),
                    final = w.Final, initial = w.Initial, discount = w.Discount, currency = w.Currency, priceText = w.PriceText, wasText = w.WasText,
                    review = item?.Review, reviewPercent = item?.ReviewPercent, free = item?.Free ?? false,
                    soon = item?.Soon ?? false, when = item?.When,
                    art = MapArt(item?.Art),
                };
            }),
        };
    }

    private void OnStoreChanged(string what) => PostEvent("store.changed", new { what });

    /// <summary>A wishlisted game on sale, unless the library owns it on Steam by now; price is Steam's own text in the region's currency.</summary>
    private void OnStoreSale(StoreService.WishItem item)
    {
        if (_services.Library.Find("steam:" + item.AppId) is { Owned: true } or { Installed: true }) return;
        PostEvent("store.sale", new { appId = item.AppId, name = item.Name, final = item.Final, discount = item.Discount, currency = item.Currency, price = item.PriceText });
    }
}
