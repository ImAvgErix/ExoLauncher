using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the shared version lookup could go wrong for every check at once (each silent: a row shows nothing, or a maker is
/// asked over and over):
/// - two asking at once send two requests;
/// - a kept answer is asked for again before its period;
/// - a failure forgets the last good answer (the row goes blank), or is asked again on every showing;
/// - a source's "slow down" (429) is ignored;
/// - a stale answer holds the caller on the network instead of showing at once.
/// </summary>
public sealed class LatestCacheTests
{
    private sealed record Answer(string Version);

    [Fact]
    public async Task EveryoneAskingAtOnceSharesOneLookup()
    {
        var cache = new LatestCache<Answer>("test", TimeSpan.FromHours(1));
        var asked = 0;
        var gate = new TaskCompletionSource();
        async Task<Answer?> Lookup(CancellationToken ct) { Interlocked.Increment(ref asked); await gate.Task; return new Answer("1.0"); }
        var first = cache.GetAsync("k", Lookup, CancellationToken.None);
        var second = cache.GetAsync("k", Lookup, CancellationToken.None);
        gate.SetResult();
        Assert.Equal("1.0", (await first)?.Version);
        Assert.Equal("1.0", (await second)?.Version);
        Assert.Equal(1, asked);
        // Kept for its period: no second lookup.
        Assert.Equal("1.0", (await cache.GetAsync("k", Lookup, CancellationToken.None))?.Version);
        Assert.Equal(1, asked);
        Assert.Equal("1.0", cache.Kept("k")?.Version);
    }

    [Fact]
    public async Task AFailureKeepsTheLastAnswerAndWaitsBeforeAskingAgain()
    {
        var cache = new LatestCache<Answer>("test", TimeSpan.FromMilliseconds(1));
        Assert.Equal("1.0", (await cache.GetAsync("k", _ => Task.FromResult<Answer?>(new Answer("1.0")), CancellationToken.None))?.Version);
        await Task.Delay(20);
        var asked = 0;
        Task<Answer?> Failing(CancellationToken ct) { asked++; throw new HttpRequestException("offline"); }
        // Stale: shown at once while it is asked again in the background; that lookup fails and keeps 1.0.
        Assert.Equal("1.0", (await cache.GetAsync("k", Failing, CancellationToken.None, fresh: true))?.Version);
        Assert.Equal(1, asked);
        // Within the retry wait: not asked again, still 1.0.
        Assert.Equal("1.0", (await cache.GetAsync("k", Failing, CancellationToken.None, fresh: true))?.Version);
        Assert.Equal(1, asked);
    }

    [Fact]
    public async Task AStaleAnswerShowsAtOnceWhileItIsAskedAgain()
    {
        var cache = new LatestCache<Answer>("test", TimeSpan.FromMilliseconds(1));
        await cache.GetAsync("k", _ => Task.FromResult<Answer?>(new Answer("1.0")), CancellationToken.None);
        await Task.Delay(20);
        var gate = new TaskCompletionSource<Answer?>();
        var shown = cache.GetAsync("k", _ => gate.Task, CancellationToken.None);
        Assert.True(shown.IsCompletedSuccessfully);
        Assert.Equal("1.0", (await shown)?.Version);
        gate.SetResult(new Answer("2.0"));
        await Task.Delay(50);
        Assert.Equal("2.0", cache.Kept("k")?.Version);
    }

    [Fact]
    public async Task ASourceThatSaysSlowDownIsLeftAlone()
    {
        var cache = new LatestCache<Answer>("test", TimeSpan.FromHours(1));
        using var response = new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests);
        Assert.True(cache.SlowDown(response));
        var asked = 0;
        Assert.Null(await cache.GetAsync("k", _ => { asked++; return Task.FromResult<Answer?>(new Answer("1.0")); }, CancellationToken.None));
        Assert.Equal(0, asked);
    }

    [Fact]
    public async Task ClearAsksAgain()
    {
        var cache = new LatestCache<Answer>("test", TimeSpan.FromHours(1));
        var asked = 0;
        Task<Answer?> Lookup(CancellationToken ct) { asked++; return Task.FromResult<Answer?>(new Answer("1." + asked)); }
        await cache.GetAsync("k", Lookup, CancellationToken.None);
        cache.Clear();
        Assert.Equal("1.2", (await cache.GetAsync("k", Lookup, CancellationToken.None))?.Version);
    }
}
