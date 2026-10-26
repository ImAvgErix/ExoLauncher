using ExoLauncher.Helpers;

namespace ExoLauncher.Tests;

/// <summary>
/// What "the PC is idle" means for the work Exo does while you're away (game and chipset updates), fed readings. Ways it
/// could go wrong: a browser, a build, a render, a video or a call with no key pressed reading as idle; a few minutes
/// without input being enough; a locked PC or one with its display off needing the full ten minutes; a processor reading
/// not yet a minute long (the sampler just started) counting as quiet; an older busy stretch hiding a busy last minute;
/// Exo's own processes counting against it.
/// </summary>
public sealed class PcIdleTests
{
    private static readonly PcIdle.Readings Away = new(TimeSpan.FromMinutes(10), Quiet: false, Presenting: false, Sound: false,
        Microphone: false, OthersCpu: 0.05, Game: false, Battery: false);

    [Fact]
    public void Nobody_at_it_and_nothing_going_is_idle() => Assert.True(PcIdle.Idle(Away));

    [Fact]
    public void Input_must_be_ten_minutes_old_or_one_while_locked_or_dark()
    {
        Assert.False(PcIdle.Idle(Away with { Input = TimeSpan.FromMinutes(9.9) }));
        Assert.False(PcIdle.Idle(Away with { Input = TimeSpan.FromMinutes(2) }));
        Assert.True(PcIdle.Idle(Away with { Input = TimeSpan.FromMinutes(1), Quiet = true }));
        Assert.False(PcIdle.Idle(Away with { Input = TimeSpan.FromSeconds(59), Quiet = true }));
    }

    [Fact]
    public void Any_sign_of_use_is_not_idle()
    {
        Assert.False(PcIdle.Idle(Away with { Presenting = true }));
        Assert.False(PcIdle.Idle(Away with { Sound = true }));
        Assert.False(PcIdle.Idle(Away with { Microphone = true }));
        Assert.False(PcIdle.Idle(Away with { OthersCpu = PcIdle.CpuShare }));
        Assert.False(PcIdle.Idle(Away with { OthersCpu = null }));
        Assert.False(PcIdle.Idle(Away with { Game = true }));
        Assert.False(PcIdle.Idle(Away with { Battery = true }));
        Assert.False(PcIdle.Idle(Away with { Input = TimeSpan.FromHours(3), Quiet = true, Sound = true }));
    }

    [Fact]
    public void Processor_use_is_other_programs_over_the_last_minute()
    {
        // 30 intervals of 2 s: 10% busy elsewhere, plus 50% of Exo's own (a compile it started).
        var minute = Enumerable.Repeat(new PcIdle.Interval(2000, Busy: 60, All: 100, Own: 50), 30).ToList();
        Assert.Equal(0.10, PcIdle.OthersShare(minute, PcIdle.CpuWindow)!.Value, 3);
        Assert.Null(PcIdle.OthersShare(minute.Take(10).ToList(), PcIdle.CpuWindow));   // 20 s measured
        Assert.Null(PcIdle.OthersShare([], PcIdle.CpuWindow));
        var busy = Enumerable.Repeat(new PcIdle.Interval(2000, 90, 100, 0), 30).ToList();
        Assert.Equal(0.10, PcIdle.OthersShare([.. busy, .. minute], PcIdle.CpuWindow)!.Value, 3);   // busy before the minute
        Assert.Equal(0.90, PcIdle.OthersShare([.. minute, .. busy], PcIdle.CpuWindow)!.Value, 3);   // busy within it
    }
}
