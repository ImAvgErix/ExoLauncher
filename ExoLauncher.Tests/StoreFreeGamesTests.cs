using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways Epic's free games could read wrong from a saved home page (offline, or
/// minutes after Epic's weekly switch):
/// - a game whose free days ended still says "Free until" a day already past;
/// - the next game, free by now, still says it will be free;
/// - a game with no end date is dropped;
/// - a game free now no longer comes before the ones still to come.
/// </summary>
public sealed class StoreFreeGamesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 16, 0, 0, TimeSpan.Zero);

    private static StoreService.FreeGame Game(string title, bool now, int fromHours, int? untilHours) =>
        new(title, "https://store.epicgames.com/p/" + title, [], Now.AddHours(fromHours), untilHours is { } u ? Now.AddHours(u) : null, now);

    [Fact]
    public void EndedFreeDaysAreLeftOutAndBegunOnesAreFreeNow()
    {
        var saved = new[]
        {
            Game("ended", now: true, fromHours: -170, untilHours: -1),
            Game("later", now: false, fromHours: 167, untilHours: 335),
            Game("begun", now: false, fromHours: -1, untilHours: 167),
            Game("open", now: true, fromHours: -5, untilHours: null),
        };

        var free = StoreService.FreeAt(saved, Now);

        Assert.Equal(new[] { "open", "begun", "later" }, free.Select(f => f.Title));
        Assert.Equal(new[] { true, true, false }, free.Select(f => f.Now));
    }

    [Fact]
    public void AnUntouchedPageStaysAsItWas()
    {
        var saved = new[] { Game("now", now: true, fromHours: -2, untilHours: 100), Game("next", now: false, fromHours: 100, untilHours: 268) };

        Assert.Equal(saved, StoreService.FreeAt(saved, Now));
    }
}
