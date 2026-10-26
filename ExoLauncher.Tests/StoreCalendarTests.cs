using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// The Store calendar, which nobody would notice failing: a release known only as "Q1 2027" or "Feb 2027" lands on a
/// day; a wishlisted game is the one the daily limit cuts; a tag nearly every game carries ("Singleplayer") makes
/// everything look made for this person; a game out three weeks ago, or dated in a year, fills the page; the day a
/// game sits on is the UTC day instead of the local one; the same game shows twice; a day fills with tiny games far
/// down Steam's list.
/// </summary>
public sealed class StoreCalendarTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly IReadOnlySet<string> None = new HashSet<string>();
    private static readonly IReadOnlyDictionary<int, double> NoTaste = new Dictionary<int, double>();
    private static readonly IReadOnlyDictionary<int, string> Names = new Dictionary<int, string> { [10] = "Roguelike", [20] = "Co-op", [30] = "Singleplayer", [493] = "Early Access" };

    private static StoreService.CalendarCandidate Game(string id, int daysAway, bool exact = true, int[]? tags = null, int rank = 0) =>
        new(new StoreService.Tile(id, "Game " + id), Now.AddDays(daysAway), exact, tags ?? [], rank);

    private static StoreService.Calendar Build(IReadOnlyList<StoreService.CalendarCandidate> games, IReadOnlySet<string>? wished = null, IReadOnlySet<string>? owned = null,
        IReadOnlyDictionary<int, double>? taste = null, TimeZoneInfo? zone = null) =>
        StoreService.BuildCalendar(Now, zone ?? Utc, games, wished ?? None, owned ?? None, taste ?? NoTaste, Names);

    private static string[] Ids(StoreService.Calendar c) => [.. c.Days.SelectMany(d => d.Entries).Select(e => e.Tile.AppId)];

    [Fact]
    public void A_release_known_only_by_month_or_quarter_is_not_put_on_a_day()
    {
        var calendar = Build([Game("a", 3), Game("b", 4, exact: false)]);
        Assert.Equal(["a"], Ids(calendar));
    }

    [Theory]
    [InlineData(-14, true)]
    [InlineData(-15, false)]
    [InlineData(42, true)]
    [InlineData(43, false)]
    public void Only_the_last_two_weeks_and_the_next_six_are_shown(int daysAway, bool shown) =>
        Assert.Equal(shown, Ids(Build([Game("a", daysAway)])).Length == 1);

    [Fact]
    public void Days_come_in_order_and_hold_their_games()
    {
        var calendar = Build([Game("late", 10), Game("soon", 2), Game("sameDay", 2)]);
        Assert.Equal([2, 10], calendar.Days.Select(d => (d.Date.ToDateTime(TimeOnly.MinValue) - new DateTime(2026, 10, 26)).Days));
        Assert.Equal(2, calendar.Days[0].Entries.Count);
    }

    [Fact]
    public void A_game_sits_on_the_local_day_not_the_utc_one()
    {
        // 12:00 UTC on Oct 28 is 02:00 on Oct 29 at UTC+14.
        var east = TimeZoneInfo.CreateCustomTimeZone("east", TimeSpan.FromHours(14), "east", "east");
        var day = Assert.Single(Build([Game("a", 2)], zone: east).Days);
        Assert.Equal(new DateOnly(2026, 10, 29), day.Date);
        Assert.Equal(new DateOnly(2026, 10, 28), Assert.Single(Build([Game("a", 2)]).Days).Date);
    }

    [Fact]
    public void A_wishlisted_game_comes_first_and_the_daily_limit_never_cuts_it()
    {
        var crowd = Enumerable.Range(0, 20).Select(i => Game("g" + i, 5, rank: i + 1)).ToList();
        crowd.Add(Game("wished", 5, rank: 99));
        var day = Assert.Single(Build(crowd, wished: new HashSet<string> { "wished" }).Days);
        Assert.Equal("wished", day.Entries[0].Tile.AppId);
        Assert.True(day.Entries[0].Wished);
        Assert.Contains("On your wishlist", day.Entries[0].Why);
        Assert.Equal(StoreService.CalendarPerDay, day.Entries.Count);
    }

    [Fact]
    public void Several_wishlisted_games_on_one_day_are_all_kept()
    {
        var wished = Enumerable.Range(0, 12).Select(i => "w" + i).ToHashSet();
        var games = wished.Select((id, i) => Game(id, 5, rank: i)).ToList();
        Assert.Equal(12, Assert.Single(Build(games, wished: wished).Days).Entries.Count);
    }

    [Fact]
    public void Steams_own_order_decides_between_games_with_nothing_to_tell_them_apart()
    {
        var day = Assert.Single(Build([Game("second", 5, rank: 2), Game("first", 5, rank: 1)]).Days);
        Assert.Equal(["first", "second"], day.Entries.Select(e => e.Tile.AppId));
    }

    [Fact]
    public void A_tag_nearly_every_game_carries_does_not_make_everything_relevant()
    {
        // The person plays Singleplayer games; so does everything else on the list. A rare taste still counts.
        var games = Enumerable.Range(0, 10).Select(i => Game("g" + i, 3, tags: i == 0 ? [30, 10] : [30], rank: i)).ToList();
        var taste = new Dictionary<int, double> { [30] = 1.0, [10] = 1.0 };
        var entries = Assert.Single(Build(games, taste: taste).Days).Entries;
        var liked = entries.Where(e => e.Why.Any(w => w.StartsWith("Like ", StringComparison.Ordinal))).ToList();
        Assert.Equal("g0", Assert.Single(liked).Tile.AppId);
        Assert.Equal("g0", entries[0].Tile.AppId);
        Assert.DoesNotContain("Singleplayer", string.Join(" ", liked.SelectMany(e => e.Why)));
    }

    [Fact]
    public void The_reason_names_at_most_two_tags_and_only_ones_with_names()
    {
        // Tag 99 has no name and is on two games; 10 and 20 are rare among these eleven.
        List<StoreService.CalendarCandidate> games = [Game("a", 3, tags: [10, 20, 99]), Game("b", 3, tags: [99]), .. Enumerable.Range(0, 9).Select(i => Game("o" + i, 3))];
        var taste = new Dictionary<int, double> { [10] = 1.0, [20] = 0.9, [99] = 1.0 };
        var entry = Assert.Single(Build(games, taste: taste).Days).Entries.First(e => e.Tile.AppId == "a");
        Assert.Equal("Like Roguelike and Co-op", Assert.Single(entry.Why));
    }

    [Fact]
    public void Owned_games_are_marked_not_dropped_and_early_access_is_flagged()
    {
        var entry = Assert.Single(Assert.Single(Build([Game("a", 3, tags: [493])], owned: new HashSet<string> { "a" }).Days).Entries);
        Assert.True(entry.Owned);
        Assert.True(entry.EarlyAccess);
    }

    [Fact]
    public void A_small_game_far_down_steams_list_is_left_out_unless_wished_or_close_to_taste()
    {
        var far = StoreService.CalendarRankCut + 50;
        var games = new[] { Game("big", 3, rank: 0), Game("small", 3, rank: far), Game("wished", 3, rank: far), Game("liked", 3, tags: [10, 20, 30], rank: far) };
        var taste = new Dictionary<int, double> { [10] = 1.0, [20] = 1.0, [30] = 1.0 };
        var ids = Ids(Build(games, wished: new HashSet<string> { "wished" }, taste: taste));
        Assert.Equal(["big", "liked", "wished"], ids.Order());
        Assert.Equal("wished", ids[0]);
    }

    [Fact]
    public void The_same_game_listed_twice_shows_once()
    {
        Assert.Equal(["a"], Ids(Build([Game("a", 3), Game("a", 3)])));
    }

    [Fact]
    public void Nothing_to_show_is_an_empty_calendar_not_a_failure()
    {
        var empty = Build([]);
        Assert.Empty(empty.Days);
        Assert.Empty(empty.Interests);
    }

    [Fact]
    public void Affinity_grows_with_the_square_root_of_hours_and_tops_out_at_one()
    {
        var affinity = StoreService.TagAffinity([(100.0, [10]), (1.0, [20])]);
        Assert.Equal(1.0, affinity[10], 6);
        Assert.Equal(0.1, affinity[20], 6); // sqrt(1) / sqrt(100)
    }

    [Fact]
    public void Affinity_counts_a_games_top_tags_more_than_its_later_ones_and_ignores_unplayed_games()
    {
        var affinity = StoreService.TagAffinity([(4.0, [1, 2]), (0.0, [3])]);
        Assert.True(affinity[1] > affinity[2]);
        Assert.False(affinity.ContainsKey(3));
        Assert.Empty(StoreService.TagAffinity([]));
    }

    [Fact]
    public void Interests_are_the_tags_the_played_games_point_to_most_that_are_not_everywhere()
    {
        var games = Enumerable.Range(0, 6).Select(i => Game("g" + i, 3, tags: [30, 10], rank: i)).ToList();
        var calendar = Build(games, taste: new Dictionary<int, double> { [30] = 1.0, [10] = 0.5 });
        // Both tags are on every game, so neither is a taste that sets this person apart.
        Assert.Empty(calendar.Interests);
    }
}
