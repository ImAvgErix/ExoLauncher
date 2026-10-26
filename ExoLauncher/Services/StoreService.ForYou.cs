using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// Discover made for this person. Steam's ranked lists are the same for everyone, so on their own the home page shows
/// Dota 2 and Counter-Strike to someone who has played both for years. Built here, on this PC, from what it already
/// knows (the games played most, from every store, the wishlist, what the library owns) and Steam's public lists:
/// owned games leave Discover, a Picked for you shelf and a Because you play shelf lead, and the banner puts the big
/// games closest to this person's taste first. Nothing leaves the PC and no account is asked for.
/// </summary>
internal sealed partial class StoreService
{
    /// <summary>A game this PC plays: its library id, its Steam app id when it is a Steam game, its title.</summary>
    internal sealed record Played(string Id, string? SteamAppId, string Title, double Hours, DateTimeOffset? LastPlayed);

    /// <summary>What the library has, by Steam app id and by title (for games the library has from another store).</summary>
    internal sealed record OwnedGames(IReadOnlySet<string> AppIds, IReadOnlySet<string> Titles)
    {
        public bool Has(Tile t) => AppIds.Contains(t.AppId) || Titles.Contains(TitleKey(t.Name));
    }

    /// <summary>The taste: tag weights 0 to 1, the played games it came from (with their tags), and tag names.</summary>
    private sealed record Taste(IReadOnlyDictionary<int, double> Affinity, IReadOnlyList<(Played Game, IReadOnlyList<int> Tags)> Games, IReadOnlyDictionary<int, string> Names);

    /// <summary>
    /// Big games that are not on Steam, so Steam has no tags for them: their tags by hand, the ones Steam gives games
    /// like them. Without these, someone whose hours are all in VALORANT or League would get no taste at all.
    /// </summary>
    private static readonly Dictionary<string, int[]> OffSteamTags = new(StringComparer.Ordinal)
    {
        // FPS, Tactical, Shooter, Competitive, Hero Shooter, Multiplayer
        ["valorant"] = [1663, 1708, 1774, 3878, 620519, 3859],
        // MOBA, Strategy, Competitive, Multiplayer, PvP
        ["leagueoflegends"] = [1718, 9, 3878, 3859, 1775],
        // Auto Battler, Strategy, Card Game, Competitive
        ["teamfighttactics"] = [1084988, 9, 1666, 3878],
        // Battle Royale, Shooter, Building, Third-Person Shooter, Multiplayer
        ["fortnite"] = [176981, 1774, 1643, 3814, 3859],
        // Sports, Racing, Soccer, Competitive, Multiplayer
        ["rocketleague"] = [701, 699, 1254546, 3878, 3859],
        // Sandbox, Survival, Open World, Crafting, Building
        ["minecraft"] = [3810, 1662, 1695, 1702, 1643],
        // Card Game, Strategy, Competitive
        ["hearthstone"] = [1666, 9, 3878],
        // FPS, Hero Shooter, Competitive, Shooter
        ["overwatch2"] = [1663, 620519, 3878, 1774],
        // MMORPG, RPG, Fantasy, Open World
        ["worldofwarcraft"] = [1754, 122, 1684, 1695],
        // Action RPG, Hack and Slash, Loot, Dark Fantasy
        ["diabloiv"] = [4231, 1646, 4236, 4604],
        // FPS, Battle Royale, Shooter, Military
        ["callofdutywarzone"] = [1663, 176981, 1774, 4150],
        // Open World, Action, Third-Person Shooter, Crime
        ["grandtheftautov"] = [1695, 19, 3814, 6378],
    };

    private const int PicksShown = 24;
    /// <summary>How close to the taste (times quality and recency) a game must be to be picked.</summary>
    private const double PickFit = 0.03;
    /// <summary>Fewer Steam reviews than this and a game is too small to know whether it is any good; tag lists are full of them.</summary>
    private const int MinReviews = 500;
    private const int BecauseShown = 20;
    private static readonly TimeSpan PoolFresh = TimeSpan.FromMinutes(30);
    private readonly Dictionary<string, (DateTimeOffset At, IReadOnlyList<Ranked> List)> _pools = new(StringComparer.Ordinal);
    private Dictionary<string, string?>? _titleIds;

    /// <summary>"Call of Duty®: Black Ops 7" and "call of duty black ops 7" are the same game.</summary>
    internal static string TitleKey(string title) =>
        new(title.ToLowerInvariant().Where(char.IsAsciiLetterOrDigit).ToArray());

    /// <summary>How long the Store waits for the page made for this person before showing the plain one (still without what is owned).</summary>
    private static readonly TimeSpan ForYouBudget = TimeSpan.FromSeconds(4);
    private (string Key, DateTimeOffset At, Home Page)? _forYou;
    private Task<Home>? _forYouWork;
    /// <summary>A page other than the one being made was answered meanwhile: the made one follows by "home".</summary>
    private bool _forYouLate;
    /// <summary>What the newest ask was for; a page made for an older one asks the room to ask again.</summary>
    private string? _forYouWanted;
    private int _forYouRead;

    /// <summary>The page made last, as it was saved: the first open after Exo starts shows it at once too.</summary>
    private sealed record ForYouSaved(string Key, DateTimeOffset At, Home Page);

    private static string ForYouFile => Path.Combine(Dir, "foryou-v1.json");

    /// <summary>
    /// The home page for this person, stale while it revalidates: the page made for the same home page, library and
    /// wishlist within half an hour is answered as it is; any older page made (in this run or the last, from disk) is
    /// answered at once while the new one is made, which follows by "home" when it differs. Only with none made yet, or
    /// on a Refresh (<paramref name="wait"/>), does the ask wait for it, at most <see cref="ForYouBudget"/>; slower than
    /// that, the last page (or the plain page) answers and the made one follows by "home". Making it took up to a second
    /// on every open past half an hour, with the Store's skeleton up meanwhile. It never fails the Store: anything going
    /// wrong gives the plain page.
    /// </summary>
    internal async Task<Home> ForYouHomeAsync(Home home, IReadOnlyList<Played> played, OwnedGames owned, bool wait, CancellationToken ct)
    {
        var wish = Wishlist() ?? [];
        var key = $"{home.At:O}|{home.Country}|{string.Join(',', played.Select(p => $"{p.Id}:{(int)p.Hours}"))}|{owned.AppIds.Count}+{owned.Titles.Count}|{string.Join(',', wish.Select(w => w.AppId))}";
        if (Interlocked.Exchange(ref _forYouRead, 1) == 0 && Read<ForYouSaved>(ForYouFile) is { Key: not null, Page: not null } saved && saved.Page.Country == Country)
            lock (_gate) _forYou ??= (saved.Key, saved.At, saved.Page);
        Task<Home> work;
        Home? shown;
        lock (_gate)
        {
            _forYouWanted = key;
            if (_forYou is { } kept && kept.Key == key && DateTimeOffset.UtcNow - kept.At < PoolFresh) return kept.Page;
            shown = wait ? null : _forYou?.Page;
            if (shown is not null) _forYouLate = true;
            work = _forYouWork ??= Task.Run(async () =>
            {
                try
                {
                    var page = await ForYouAsync(home, played, owned, CancellationToken.None).ConfigureAwait(false);
                    var made = DateTimeOffset.UtcNow;
                    bool raise;
                    lock (_gate)
                    {
                        // The same page made again keeps the time it was first made, so the room keeps the one it shows
                        // (it tells pages apart by that time); a new one carries the time it was made.
                        var same = _forYou is { } before && SamePage(before.Page, page);
                        page = same ? _forYou!.Value.Page : page with { At = made };
                        _forYou = (key, made, page);
                        raise = (_forYouLate && !same) || _forYouWanted != key;
                        _forYouLate = false;
                    }
                    Write(ForYouFile, new ForYouSaved(key, made, page));
                    if (raise) Raise("home");
                    return page;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    AppLog.Info("Store: for you failed: " + ex.GetType().Name + ": " + ex.Message);
                    return Plain(home, owned);
                }
                finally
                {
                    lock (_gate) _forYouWork = null;
                }
            });
        }
        if (shown is not null) return shown;
        var done = await Task.WhenAny(work, Task.Delay(ForYouBudget, ct)).ConfigureAwait(false);
        if (done == work) return await work.ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _forYouLate = true;
            return _forYou?.Page ?? Plain(home, owned);
        }
    }

    /// <summary>Two made pages that show the same games the same way (when each was made aside).</summary>
    private static bool SamePage(Home a, Home b) =>
        string.Equals(JsonSerializer.Serialize(a with { At = default }, Json), JsonSerializer.Serialize(b with { At = default }, Json), StringComparison.Ordinal);

    /// <summary>The home page without what the library owns, nothing else changed.</summary>
    private static Home Plain(Home home, OwnedGames owned) => home with
    {
        Hero = home.Hero.Where(t => !owned.Has(t)).ToList(),
        Shelves = home.Shelves.Select(s => s with { Items = s.Items.Where(t => !owned.Has(t)).ToList() }).ToList(),
    };

    /// <summary>
    /// The home page for this person. Owned games leave every shelf and the banner; with a taste to go by (anything
    /// played, or a wishlist), Picked for you and Because you play lead, and the banner orders its games by taste.
    /// Any part Steam does not answer is left out, never the whole page.
    /// </summary>
    internal async Task<Home> ForYouAsync(Home home, IReadOnlyList<Played> played, OwnedGames owned, CancellationToken ct)
    {
        var taste = await TasteAsync(played, ct).ConfigureAwait(false);
        var plain = Plain(home, owned);
        var hero = plain.Hero;
        var shelves = plain.Shelves;
        if (taste.Affinity.Count == 0) return plain;

        // How common a tag is, from Steam's own charts (the home page), never from lists fetched for this taste: there a
        // person's main tags would be on everything and count for nothing.
        var homeIds = home.Hero.Concat(home.Shelves.SelectMany(s => s.Items)).Select(t => t.AppId).Distinct(StringComparer.Ordinal).ToList();
        var items = await ItemsAsync(homeIds, ct, staleOk: true).ConfigureAwait(false);
        var scorer = new TasteScorer(taste, items.Values.Select(i => i.Tags).ToList());
        var pools = await PoolsAsync(taste, scorer, ct).ConfigureAwait(false);
        var tagsOf = new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal);
        foreach (var r in pools.Picks.Concat(pools.Because.SelectMany(b => b.List))) tagsOf[r.Tile.AppId] = r.Tags;
        foreach (var (id, item) in items) tagsOf.TryAdd(id, item.Tags);

        var wished = (Wishlist() ?? []).Select(w => w.AppId).ToHashSet(StringComparer.Ordinal);
        var today = DateOnly.FromDateTime(DateTime.Now);

        var heroIds = hero.Select(t => t.AppId).ToHashSet(StringComparer.Ordinal);
        // Why a game is here, as the card shows it: the wishlist's bookmark, or the played game it is most like.
        Tile Marked(Tile t, IReadOnlyList<int> tags) => wished.Contains(t.AppId) ? t with { Wished = true } : t with { SimilarTo = MostLike(taste, tags) };
        // Picked for you: the closest to this person's taste, well reviewed and recent counting for more; a daily nudge
        // turns the order a little each day, so the shelf is not the same twenty games every visit.
        var picks = pools.Picks.Concat(shelves.SelectMany(s => s.Items).Select(t => new Ranked(t, items.GetValueOrDefault(t.AppId)?.Released, items.GetValueOrDefault(t.AppId)?.ReviewCount ?? 0) { Tags = tagsOf.GetValueOrDefault(t.AppId) ?? [] }))
            .Where(r => !owned.Has(r.Tile) && !r.Tile.Soon && !heroIds.Contains(r.Tile.AppId) && r.Reviews >= MinReviews)
            .DistinctBy(r => r.Tile.AppId)
            .Select(r => (r.Tile, Fit: scorer.Match(r.Tags) * Quality(r.Tile) * Recency(r.Released), r.Tags))
            // Close enough to the taste (or wished for) on its own; the daily nudge only reorders what passed.
            .Where(p => p.Fit > PickFit || wished.Contains(p.Tile.AppId))
            .OrderByDescending(p => p.Fit + Nudge(today, p.Tile.AppId) + (wished.Contains(p.Tile.AppId) ? 0.05 : 0))
            .Take(PicksShown)
            .Select(p => Marked(p.Tile, p.Tags))
            .ToList();
        var picked = picks.Select(t => t.AppId).ToHashSet(StringComparer.Ordinal);

        var because = pools.Because
            .Select(b => (b.Game, b.List, Tags: taste.Games.First(g => g.Game == b.Game).Tags))
            .Select(b => new Shelf("because-" + (b.Game.SteamAppId ?? TitleKey(b.Game.Title)), "Because you play " + b.Game.Title,
                b.List.Where(r => r.Reviews >= MinReviews && !owned.Has(r.Tile) && !picked.Contains(r.Tile.AppId) && !TitleKey(r.Tile.Name).Equals(TitleKey(b.Game.Title), StringComparison.Ordinal))
                    .OrderByDescending(r => Alike(b.Tags, r.Tags) * Quality(r.Tile) + Nudge(today, r.Tile.AppId))
                    .Take(BecauseShown).Select(r => r.Tile).ToList()))
            .Where(s => s.Items.Count >= 6)
            .ToList();

        // The banner keeps Steam's big games, closest to this person first, each with why.
        var heroOrdered = hero
            .Select((t, i) => (Tile: t, Score: scorer.Match(tagsOf.GetValueOrDefault(t.AppId) ?? []) + (wished.Contains(t.AppId) ? 1 : 0) - i * 0.01))
            .OrderByDescending(h => h.Score)
            .Select(h => Marked(h.Tile, tagsOf.GetValueOrDefault(h.Tile.AppId) ?? []))
            .ToList();

        var personal = new List<Shelf>();
        if (picks.Count >= 6) personal.Add(new Shelf("foryou", "Picked for you", picks));
        personal.AddRange(because.Take(1));
        var rest = shelves.Select(s => s with { Items = s.Items.Where(t => !picked.Contains(t.AppId)).ToList() }).ToList();
        // The second Because you play goes after New and trending, so the page alternates the charts and this person.
        var at = Math.Min(1, rest.Count);
        rest.InsertRange(at, because.Skip(1));
        return home with { Hero = heroOrdered, Shelves = [.. personal, .. rest] };
    }

    /// <summary>The library ids of up to three played games a pick is like (sharing the most of their top tags, more hours counting for a little more), closest first; null when none is close.</summary>
    private static IReadOnlyList<string>? MostLike(Taste taste, IReadOnlyList<int> tags) =>
        tags.Count == 0 ? null : taste.Games
            .Select(g => (g.Game.Id, Close: Alike(g.Tags, tags), Score: Alike(g.Tags, tags) * (1 + Math.Log10(1 + g.Game.Hours) / 4)))
            .Where(g => g.Close >= 0.4)
            .OrderByDescending(g => g.Score)
            .Select(g => g.Id)
            .Take(3)
            .ToList() is { Count: > 0 } like ? like : null;

    /// <summary>How much of one game's top tags another shares, 0 to 1, the higher tags counting for more.</summary>
    private static double Alike(IReadOnlyList<int> game, IReadOnlyList<int> other)
    {
        var theirs = other.Take(TagsRead).ToHashSet();
        return game.Take(TagsRead).Select((tag, rank) => theirs.Contains(tag) ? RankWeight(rank) : 0).Sum() / Enumerable.Range(0, TagsRead).Sum(RankWeight);
    }

    /// <summary>Under half the players liking it pulls a game down; no reviews yet is middling.</summary>
    private static double Quality(Tile t) => t.ReviewPercent is { } p ? Math.Clamp((p - 40) / 50.0, 0.1, 1.2) : 0.6;

    /// <summary>Out this year counts fully, an older game a little less, so the shelf leans to what is new to discover.</summary>
    private static double Recency(DateTimeOffset? released) =>
        released is { } d ? (DateTimeOffset.UtcNow - d).TotalDays switch { < 365 => 1.15, < 365 * 3 => 1.0, _ => 0.85 } : 1.0;

    /// <summary>A small amount, the same all day for a game, different the next day.</summary>
    private static double Nudge(DateOnly day, string appId)
    {
        var h = 2166136261u;
        foreach (var c in appId) h = (h ^ c) * 16777619u;
        h ^= (uint)day.DayNumber * 2654435761u;
        h = (h ^ (h >> 15)) * 2246822519u;
        return (h ^ (h >> 13)) % 1000 / 1000.0 * 0.06;
    }

    /// <summary>How close a game's tags are to the taste, with a tag nearly every game carries counting for little.</summary>
    private sealed class TasteScorer
    {
        private readonly Taste _taste;
        private readonly Dictionary<int, double> _share = [];

        public TasteScorer(Taste taste, IReadOnlyList<IReadOnlyList<int>> pool)
        {
            _taste = taste;
            foreach (var tags in pool)
                foreach (var tag in tags.Take(TagsRead).Distinct())
                    _share[tag] = _share.GetValueOrDefault(tag) + 1;
            foreach (var tag in _share.Keys.ToList()) _share[tag] /= Math.Max(1, pool.Count);
        }

        public double Of(int tag) => _taste.Affinity.GetValueOrDefault(tag) * Math.Pow(1 - _share.GetValueOrDefault(tag), 2);

        public double Match(IReadOnlyList<int> tags) =>
            tags.Take(TagsRead).Select((tag, rank) => Of(tag) * RankWeight(rank)).Sum() / Enumerable.Range(0, TagsRead).Sum(RankWeight);

        /// <summary>The tags that best tell this taste apart, most telling first.</summary>
        public IReadOnlyList<int> Telling(int count) =>
            _taste.Affinity.Keys.OrderByDescending(Of).Where(t => Of(t) > 0).Take(count).ToList();
    }

    /// <summary>
    /// The taste: every played game's Steam tags (a game from another store found on Steam by its title, or from
    /// <see cref="OffSteamTags"/>), weighted by its hours, with the wishlist counting as a few hours each.
    /// </summary>
    private async Task<Taste> TasteAsync(IReadOnlyList<Played> played, CancellationToken ct)
    {
        var top = played.Where(p => p.Hours > 0).OrderByDescending(p => p.Hours).Take(40).ToList();
        var ids = new System.Collections.Concurrent.ConcurrentDictionary<Played, string?>();
        // Four at a time; each answer is kept on disk, so a library is looked up once, not on every start.
        await Parallel.ForEachAsync(top, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
            async (p, token) => ids[p] = p.SteamAppId ?? await SteamIdForAsync(p.Title, token).ConfigureAwait(false)).ConfigureAwait(false);
        if (top.Any(p => p.SteamAppId is null)) SaveTitleIds();
        var wishIds = (Wishlist() ?? []).Select(w => w.AppId).Take(40).ToList();
        var items = await ItemsAsync(ids.Values.OfType<string>().Concat(wishIds), ct, staleOk: true).ConfigureAwait(false);

        var games = new List<(Played, IReadOnlyList<int>)>();
        foreach (var p in top)
        {
            IReadOnlyList<int>? tags = ids[p] is { } id && items.TryGetValue(id, out var item) && item.Tags.Count > 0 ? item.Tags : OffSteamTags.GetValueOrDefault(TitleKey(p.Title));
            if (tags is not null) games.Add((p, tags));
        }
        var weighted = games.Select(g => (g.Item1.Hours, g.Item2))
            .Concat(wishIds.Where(items.ContainsKey).Select(id => (4.0, items[id].Tags)));
        var affinity = TagAffinity(weighted);
        var names = affinity.Count == 0 ? new Dictionary<int, string>() : await TagNamesAsync(ct).ConfigureAwait(false);
        return new Taste(affinity, games, names);
    }

    private sealed record TitleFile(DateTimeOffset At, Dictionary<string, string?> Ids);

    private static string TitlesFile => Path.Combine(Dir, "titles-v1.json");

    /// <summary>The titles already looked up, from disk the first time (a month at most, so a game that came to Steam since is found).</summary>
    private Dictionary<string, string?> TitleIds()
    {
        if (_titleIds is { } known) return known;
        var file = Read<TitleFile>(TitlesFile);
        return _titleIds = file is { Ids: not null } && DateTimeOffset.UtcNow - file.At < TimeSpan.FromDays(30)
            ? new Dictionary<string, string?>(file.Ids, StringComparer.Ordinal)
            : new Dictionary<string, string?>(StringComparer.Ordinal);
    }

    private void SaveTitleIds()
    {
        TitleFile file;
        lock (_gate) file = new TitleFile(DateTimeOffset.UtcNow, new Dictionary<string, string?>(TitleIds(), StringComparer.Ordinal));
        Write(TitlesFile, file);
    }

    /// <summary>A game from another store on Steam: Steam's search by its title, taken only when the names are the same game. Kept on disk.</summary>
    private async Task<string?> SteamIdForAsync(string title, CancellationToken ct)
    {
        var key = TitleKey(title);
        if (key.Length < 2 || OffSteamTags.ContainsKey(key)) return null;
        lock (_gate) if (TitleIds().TryGetValue(key, out var known)) return known;
        string? id = null;
        try
        {
            using var doc = await GetJsonAsync($"{Steam}api/storesearch/?term={Uri.EscapeDataString(title)}&cc={Country}&l=english", ct).ConfigureAwait(false);
            if (doc.RootElement.TryGetProperty("items", out var hits) && hits.ValueKind == JsonValueKind.Array)
                foreach (var hit in hits.EnumerateArray())
                    if (Str(hit, "type") == "app" && Str(hit, "name") is { } name && TitleKey(name) == key) { id = Str(hit, "id"); break; }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            AppLog.Debug("Store: title lookup failed: " + ex.Message);
            return null;
        }
        lock (_gate) TitleIds()[key] = id;
        return id;
    }

    /// <summary>
    /// Steam's lists for this taste, kept half an hour: the best sellers carrying any of its telling tags (Picked for you),
    /// and, for the two games played most lately, the ones carrying both of that game's most telling tags.
    /// </summary>
    private async Task<(IReadOnlyList<Ranked> Picks, IReadOnlyList<(Played Game, IReadOnlyList<Ranked> List)> Because)> PoolsAsync(Taste taste, TasteScorer scorer, CancellationToken ct)
    {
        var telling = scorer.Telling(4);
        var picks = telling.Count == 0 ? Task.FromResult<IReadOnlyList<Ranked>>([]) : PoolAsync("picks", [telling], 150, ct);

        // Played lately counts as much as played long: a game from last week leads one from two years ago.
        var lead = taste.Games
            .OrderByDescending(g => Math.Sqrt(g.Game.Hours) * (g.Game.LastPlayed is { } at && DateTimeOffset.UtcNow - at < TimeSpan.FromDays(30) ? 2 : 1))
            .Take(2).ToList();
        var because = lead.Select(async g =>
        {
            var own = g.Tags.Take(TagsRead).OrderByDescending(scorer.Of).Take(2).ToList();
            var list = own.Count < 2 ? [] : await PoolAsync("because", own.Select(t => (IReadOnlyList<int>)[t]).ToList(), 150, ct).ConfigureAwait(false);
            return (g.Game, list);
        }).ToList();
        await Task.WhenAll([picks, .. because]).ConfigureAwait(false);
        return (picks.Result, because.Select(b => b.Result).ToList());
    }

    /// <summary>Released games carrying the tags (each group: any of its tags; every group must match), by sales.</summary>
    private async Task<IReadOnlyList<Ranked>> PoolAsync(string what, IReadOnlyList<IReadOnlyList<int>> groups, int count, CancellationToken ct)
    {
        var key = string.Join('|', groups.Select(g => string.Join(',', g))) + "#" + count;
        lock (_gate)
            if (_pools.TryGetValue(key, out var kept) && DateTimeOffset.UtcNow - kept.At < PoolFresh) return kept.List;
        var filters = new { released_only = true, type_filters = new { include_games = true }, tagids_must_match = groups.Select(g => new { tagids = g }) };
        var list = await QueryAsync(what, filters, count, ct).ConfigureAwait(false);
        if (list.Count > 0) lock (_gate) _pools[key] = (DateTimeOffset.UtcNow, list);
        return list;
    }
}
