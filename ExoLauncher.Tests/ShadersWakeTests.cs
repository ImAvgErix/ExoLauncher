using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways Exo Shaders' engine could start when nothing needs it, or not start when something does (it runs only on demand):
/// - it never makes its first check, or starts at every Exo start or every minute although nothing changed since it last ran;
/// - it misses a new graphics driver, a game's update (its exe changed), or a game it has work for exiting;
/// - work it left waiting for the PC to be idle starts while the user is at the PC, or on battery;
/// - its daily check never comes, or comes every minute;
/// - an engine that keeps failing is started again every minute;
/// - a clock set back holds every start off.
/// </summary>
public sealed class ShadersWakeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Away = TimeSpan.FromMinutes(3), Here = TimeSpan.FromSeconds(5);
    private static readonly ShadersAddon.WakeState Last = new("32.0.16.2101", "lib", "exes", Now.AddHours(-1));

    private static string? Reason(ShadersAddon.WakeState last, string driver = "32.0.16.2101", string? exes = "exes", bool due = false,
        TimeSpan? idle = null, bool battery = false, DateTimeOffset? now = null) =>
        ShadersAddon.WakeReason(last, driver, exes, due, idle ?? Away, battery, now ?? Now);

    [Fact]
    public void NothingNewStartsNothing()
    {
        Assert.Null(Reason(Last));
        Assert.Null(Reason(Last, idle: TimeSpan.FromHours(5)));
    }

    [Fact]
    public void ItsFirstCheckComesAtOnce()
    {
        Assert.Equal("its first check", Reason(new ShadersAddon.WakeState(), idle: Here));
        Assert.Null(Reason(new ShadersAddon.WakeState(RanAt: Now.AddMinutes(-1)), idle: Here));   // a first start that failed rests
    }

    [Fact]
    public void ANewDriverAGameUpdateOrExitWorkStartsItWhileTheUserIsHere()
    {
        Assert.NotNull(Reason(Last, driver: "32.0.16.2200", idle: Here, battery: true));
        Assert.NotNull(Reason(Last, exes: "updated", idle: Here));
        Assert.NotNull(Reason(Last, exes: null, idle: Here));   // the list went (Exo Shaders' data folder removed)
        Assert.NotNull(Reason(Last, due: true, idle: Here));
    }

    [Fact]
    public void WorkThatWaitsForIdleStartsOnlyOnceTheUserIsAwayAndOnMainsPower()
    {
        var waiting = Last with { IdleWork = true };
        Assert.Null(Reason(waiting, idle: Here));
        Assert.Null(Reason(waiting, idle: ShadersAddon.IdleAfter - TimeSpan.FromSeconds(1)));
        Assert.Null(Reason(waiting, battery: true));
        Assert.NotNull(Reason(waiting, idle: ShadersAddon.IdleAfter));
    }

    [Fact]
    public void TheDailyCheckComesOnceADayAtAnIdleMoment()
    {
        Assert.Null(Reason(Last with { RanAt = Now.AddHours(-23) }));
        Assert.Null(Reason(Last with { RanAt = Now.AddHours(-25) }, idle: Here));
        Assert.NotNull(Reason(Last with { RanAt = Now.AddHours(-25) }));
        Assert.NotNull(Reason(Last with { RanAt = null }));
    }

    [Fact]
    public void ItRestsAfterARunSoAFailingEngineIsntStartedEveryMinute()
    {
        Assert.Null(Reason(Last with { RanAt = Now.AddMinutes(-5) }, driver: "new", due: true));
        Assert.NotNull(Reason(Last with { RanAt = Now - ShadersAddon.RestAfterRun }, driver: "new"));
    }

    [Fact]
    public void AClockSetBackHoldsNothingOff()
    {
        var future = Last with { RanAt = Now.AddDays(1) };
        Assert.NotNull(Reason(future, driver: "new"));
        Assert.NotNull(Reason(future));   // the daily check: its last run reads as never
    }
}
