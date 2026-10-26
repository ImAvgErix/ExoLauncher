using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// The Store's calendar: what came out in the last two weeks and what is dated in the next six, a day at a time, with
/// the games that matter to this person first. Steam's own Personal Calendar leans on the account (its play history
/// and what similar players wish for); Exo has no account and asks for none, so this one is built here from Steam's
/// public lists and three things the PC already knows: the wishlist, what the library owns, and the tags of the games
/// played most. Nothing leaves the PC and nothing is learned about anyone else.
/// </summary>
internal sealed partial class StoreService
{
    /// <summary>One game on one day; <see cref="Why"/> says in plain words why it is on the calendar for this person.</summary>
    internal sealed record CalendarEntry(Tile Tile, DateOnly Date, bool Wished, bool Owned, bool EarlyAccess, IReadOnlyList<string> Why);

    internal sealed record CalendarDay(DateOnly Date, IReadOnlyList<CalendarEntry> Entries);

    /// <summary><see cref="Interests"/>: the tags the played games point to, for the line under the calendar's title.</summary>
    internal sealed record Calendar(DateTimeOffset At, IReadOnlyList<CalendarDay> Days, IReadOnlyList<string> Interests);

    /// <summary>A game from Steam's lists with the facts the calendar sorts and places it by; <see cref="Rank"/> is its place in Steam's own order.</summary>
    internal sealed record CalendarCandidate(Tile Tile, DateTimeOffset Released, bool Exact, IReadOnlyList<int> Tags, int Rank);

    internal const int CalendarPastDays = 14;
    internal const int CalendarAheadDays = 42;
    internal const int CalendarPerDay = 6;
    /// <summary>
    /// Past this place in Steam's order of coming games (by pre-orders and sales) a game is too small to fill the calendar
    /// with, unless it is wished for or close to this person's taste: below about forty that order is mostly noise (a
    /// poker trainer next to Dawn of War IV). Games already out made Steam's top sellers to be listed at all, so they keep
    /// their place.
    /// </summary>
    internal const int CalendarRankCut = 40;
    /// <summary>How close to the taste a game past <see cref="CalendarRankCut"/> must be to stay.</summary>
    private const double CalendarCloseMatch = 0.15;
    /// <summary>Steam's "Early Access" tag.</summary>
    private const int EarlyAccessTag = 493;
    /// <summary>How much of a game's top tags the person's taste has to cover before the calendar says "Like ...".</summary>
    private const double TasteMatch = 0.3;
    private const int TagsRead = 8;

    private static readonly TimeSpan CalendarFresh = TimeSpan.FromMinutes(30);
    private (DateTimeOffset At, IReadOnlyList<CalendarCandidate> List)? _calendarCandidates;

    /// <summary>
    /// The calendar for this person: <paramref name="played"/> is every game played, from any store, with its hours
    /// (see <see cref="TasteAsync"/>), <paramref name="owned"/> the Steam app ids the library has. Null when Steam's
    /// lists could not be read at all.
    /// </summary>
    internal async Task<Calendar?> CalendarAsync(IReadOnlyList<Played> played, IReadOnlySet<string> owned, CancellationToken ct)
    {
        var candidates = await CalendarCandidatesAsync(ct).ConfigureAwait(false);
        if (candidates.Count == 0) return null;
        var wished = (Wishlist() ?? []).Select(w => w.AppId).ToHashSet(StringComparer.Ordinal);
        var taste = await TasteAsync(played, ct).ConfigureAwait(false);
        var names = taste.Names.Count > 0 ? taste.Names : await TagNamesAsync(ct).ConfigureAwait(false);
        return BuildCalendar(DateTimeOffset.UtcNow, TimeZoneInfo.Local, candidates, wished, owned, taste.Affinity, names);
    }

    /// <summary>Steam's coming and just-out lists, kept half an hour; the same two queries as the home page, asked once for both.</summary>
    private async Task<IReadOnlyList<CalendarCandidate>> CalendarCandidatesAsync(CancellationToken ct)
    {
        lock (_gate)
            if (_calendarCandidates is { } kept && DateTimeOffset.UtcNow - kept.At < CalendarFresh) return kept.List;
        var coming = QueryAsync("calendar coming", ComingSoon, 200, ct);
        var out_ = QueryAsync("calendar out", Released, 200, ct);
        await Task.WhenAll(coming, out_).ConfigureAwait(false);
        // Each list keeps its own order: the tenth best seller coming and the tenth just out are as big as each other.
        static IEnumerable<CalendarCandidate> Ranked(IReadOnlyList<Ranked> list) =>
            list.Select((r, i) => (r, i)).Where(x => x.r.Released is not null).Select(x => new CalendarCandidate(x.r.Tile, x.r.Released!.Value, x.r.Exact, x.r.Tags, x.i));
        var list = Ranked(coming.Result).Concat(Ranked(out_.Result)).ToList();
        if (list.Count > 0) lock (_gate) _calendarCandidates = (DateTimeOffset.UtcNow, list);
        return list;
    }

    /// <summary>
    /// How much each tag matters to this person, 0 to 1: every played game adds its top tags, weighted by the square
    /// root of its hours (a hundred hours counts ten times one hour, not a hundred) and by how high the tag stands on
    /// the game. Nothing played gives nothing, so the calendar then goes by the wishlist and Steam's own order.
    /// </summary>
    internal static Dictionary<int, double> TagAffinity(IEnumerable<(double Hours, IReadOnlyList<int> Tags)> played)
    {
        var sum = new Dictionary<int, double>();
        foreach (var (hours, tags) in played)
        {
            if (hours <= 0) continue;
            var weight = Math.Sqrt(hours);
            for (var rank = 0; rank < Math.Min(tags.Count, TagsRead); rank++)
                sum[tags[rank]] = sum.GetValueOrDefault(tags[rank]) + weight * RankWeight(rank);
        }
        var max = sum.Count == 0 ? 0 : sum.Values.Max();
        return max <= 0 ? [] : sum.ToDictionary(kv => kv.Key, kv => kv.Value / max);
    }

    private static double RankWeight(int rank) => 1 - rank * 0.1;

    /// <summary>
    /// Games with a real release day inside the window, grouped by day (local time), the wishlist first and then the
    /// ones closest to this person's taste, then Steam's own order. A tag nearly every game carries ("Singleplayer")
    /// counts for little, so a taste for it does not make everything look relevant. At most <see cref="CalendarPerDay"/>
    /// a day, and a wishlisted game is never the one left out.
    /// </summary>
    internal static Calendar BuildCalendar(
        DateTimeOffset now, TimeZoneInfo zone, IReadOnlyList<CalendarCandidate> candidates,
        IReadOnlySet<string> wished, IReadOnlySet<string> owned,
        IReadOnlyDictionary<int, double> affinity, IReadOnlyDictionary<int, string> tagNames)
    {
        DateOnly Day(DateTimeOffset t) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(t, zone).DateTime);
        var today = Day(now);
        var first = today.AddDays(-CalendarPastDays);
        var last = today.AddDays(CalendarAheadDays);

        // Tags nearly everything carries are discounted by how common they are among these games.
        var share = new Dictionary<int, double>();
        foreach (var c in candidates)
            foreach (var tag in c.Tags.Take(TagsRead).Distinct())
                share[tag] = share.GetValueOrDefault(tag) + 1;
        foreach (var tag in share.Keys.ToList()) share[tag] /= candidates.Count;
        double Taste(int tag) => affinity.GetValueOrDefault(tag) * Math.Pow(1 - share.GetValueOrDefault(tag), 2);

        var days = new SortedDictionary<DateOnly, List<(CalendarEntry Entry, double Score, int Rank)>>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in candidates)
        {
            if (!c.Exact || !seen.Add(c.Tile.AppId)) continue;
            var date = Day(c.Released);
            if (date < first || date > last) continue;

            var isWished = wished.Contains(c.Tile.AppId);
            var tags = c.Tags.Take(TagsRead).ToList();
            var match = tags.Select((tag, rank) => Taste(tag) * RankWeight(rank)).Sum() / Enumerable.Range(0, TagsRead).Sum(RankWeight);
            if (date > today && c.Rank > CalendarRankCut && !isWished && match < CalendarCloseMatch) continue;
            var why = new List<string>();
            if (isWished) why.Add("On your wishlist");
            var alike = tags.Where(tag => Taste(tag) >= TasteMatch && tagNames.ContainsKey(tag)).OrderByDescending(Taste).Take(2).Select(tag => tagNames[tag]).ToList();
            if (alike.Count > 0) why.Add("Like " + string.Join(" and ", alike));

            var entry = new CalendarEntry(c.Tile, date, isWished, owned.Contains(c.Tile.AppId), c.Tags.Contains(EarlyAccessTag), why);
            if (!days.TryGetValue(date, out var day)) days[date] = day = [];
            // The wishlist first; then taste and how big the game is weigh together, so a day leads with the big releases
            // and a small game close to this person's taste still beats a big one far from it.
            day.Add((entry, (isWished ? 100 : 0) + match * 10 + 8 / (1 + c.Rank / 10.0), c.Rank));
        }

        var result = days.Select(d =>
        {
            var ordered = d.Value.OrderByDescending(e => e.Score).ThenBy(e => e.Rank).ToList();
            var shown = ordered.Where(e => e.Entry.Wished).Concat(ordered.Where(e => !e.Entry.Wished)).Take(Math.Max(CalendarPerDay, ordered.Count(e => e.Entry.Wished)));
            return new CalendarDay(d.Key, shown.Select(e => e.Entry).ToList());
        }).Where(d => d.Entries.Count > 0).ToList();

        var interests = affinity.OrderByDescending(kv => Taste(kv.Key)).Where(kv => tagNames.ContainsKey(kv.Key) && Taste(kv.Key) > 0)
            .Take(5).Select(kv => tagNames[kv.Key]).ToList();
        return new Calendar(now, result, interests);
    }
}
