using System.Globalization;
using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// Steam's store query service: the same ranked lists the Steam store's own
/// front page uses (top sellers, top pre-orders, specials), with each game's
/// release, review summary, regional price and art in the
/// one answer.
/// </summary>
internal sealed partial class StoreService
{
    /// <summary>One game from a query, with what the home page sorts and labels by.</summary>
    private sealed record Ranked(Tile Tile, DateTimeOffset? Released, int Reviews);

    /// <summary>Steam's sort for "top sellers" (units sold, recent weeks weigh most).</summary>
    private const int SortTopSellers = 13;

    private static object Released => new { released_only = true, type_filters = new { include_games = true } };

    private static object ComingSoon => new { coming_soon_only = true, type_filters = new { include_games = true } };

    /// <summary>Released games at any discount: Steam's own specials, by sales.</summary>
    private static object OnSale => new { released_only = true, type_filters = new { include_games = true }, price_filters = new { min_discount_percent = 1 } };

    private async Task<IReadOnlyList<Ranked>> QueryAsync(string what, object filters, int count, CancellationToken ct)
    {
        var input = JsonSerializer.Serialize(new
        {
            query = new { count, sort = SortTopSellers, filters },
            context = new { language = "english", country_code = Country },
            data_request = new { include_release = true, include_reviews = true, include_assets = true, include_tag_count = 20 },
        });
        try
        {
            using var doc = await GetJsonAsync("https://api.steampowered.com/IStoreQueryService/Query/v1?input_json=" + Uri.EscapeDataString(input), ct).ConfigureAwait(false);
            if (!doc.RootElement.TryGetProperty("response", out var r) || !r.TryGetProperty("store_items", out var items) || items.ValueKind != JsonValueKind.Array) return [];
            var list = new List<Ranked>();
            var read = new List<(string, Item)>();
            foreach (var item in items.EnumerateArray())
                if (ReadRanked(item) is { } ranked && list.All(x => x.Tile.AppId != ranked.Ranked.Tile.AppId))
                {
                    list.Add(ranked.Ranked);
                    read.Add((ranked.Ranked.Tile.AppId, ranked.Item));
                }
            Remember(read);
            return list;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            AppLog.Info($"Store: {what} query failed: {ex.Message}");
            return [];
        }
    }

    private static (Ranked Ranked, Item Item)? ReadRanked(JsonElement e)
    {
        if (Str(e, "appid") is not { } id || Blank(Str(e, "name")) is not { } name || NotAGame().IsMatch(name)) return null;
        var item = ReadItem(e);
        return (new Ranked(WithItem(new Tile(id, name), item), item.Released, item.ReviewCount), item);
    }

    /// <summary>
    /// The home page, the way a launcher's store front reads: the banner takes
    /// the biggest games out in the last two months and the most pre-ordered
    /// ones coming, then New and trending (out in the last four months, by
    /// sales), Most anticipated (by pre-orders), the evergreen Top sellers,
    /// sales, and the rest of Steam's most wishlisted.
    /// </summary>
    private static Home BuildHome(IReadOnlyList<Ranked> sellers, IReadOnlyList<Ranked> upcoming, IReadOnlyList<Tile> wished, IReadOnlyList<Ranked> specials, IReadOnlyList<FreeGame> free)
    {
        var now = DateTimeOffset.UtcNow;
        bool OutWithin(Ranked r, int days) => r.Released is { } d && d <= now && now - d <= TimeSpan.FromDays(days);
        var fresh = sellers.Where(r => OutWithin(r, 120)).Select(r => r.Tile).ToList();
        var bigNew = sellers.Where(r => OutWithin(r, 60)).Select(r => r.Tile).Take(5).ToList();
        var bigSoon = upcoming.Select(r => r.Tile).Take(3).ToList();

        // Out now and coming, alternating, so the banner carries both.
        var hero = new List<Tile>();
        for (var i = 0; hero.Count < bigNew.Count + bigSoon.Count; i++)
        {
            if (i < bigNew.Count) hero.Add(bigNew[i]);
            if (i < bigSoon.Count) hero.Add(bigSoon[i]);
        }
        var shown = hero.Select(t => t.AppId).ToHashSet(StringComparer.Ordinal);
        var heroIds = shown.ToHashSet(StringComparer.Ordinal);
        // Each game on one shelf only, except sales, which show every sale.
        List<Tile> Fresh(IEnumerable<Tile> tiles, int take) => tiles.Where(t => shown.Add(t.AppId)).Take(take).ToList();
        var freshIds = fresh.Select(t => t.AppId).ToHashSet(StringComparer.Ordinal);
        return new Home(now, hero,
        [
            new Shelf("new", "New and trending", Fresh(fresh, 30)),
            new Shelf("upcoming", "Most anticipated", Fresh(upcoming.Select(r => r.Tile), 40)),
            new Shelf("top", "Top sellers", Fresh(sellers.Select(r => r.Tile).Where(t => !freshIds.Contains(t.AppId)), 30)),
            new Shelf("specials", "On sale", specials.Select(r => r.Tile).Where(t => t.Discount > 0 && !heroIds.Contains(t.AppId)).Take(30).ToList()),
            new Shelf("wishlisted", "Most wishlisted", Fresh(wished, 40)),
        ], free);
    }

    /// <summary>"Oct 23", "Feb 2027", "Q1 2027", "2027" or "Coming soon", as precise as the developer made the date.</summary>
    private static string WhenText(DateTimeOffset? date, string? display, string? custom)
    {
        if (custom is not null) return custom;
        if (date is not { } d) return "Coming soon";
        var local = d.ToLocalTime();
        return display switch
        {
            "date_full" => local.ToString(local.Year == DateTime.Now.Year ? "MMM d" : "MMM d, yyyy", CultureInfo.InvariantCulture),
            "date_month" => local.ToString("MMM yyyy", CultureInfo.InvariantCulture),
            "date_quarter" => $"Q{(local.Month + 2) / 3} {local.Year}",
            "date_year" => local.Year.ToString(CultureInfo.InvariantCulture),
            _ => "Coming soon",
        };
    }
}
