using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the bridge's request table could mislead the page:
/// - a request reusing the id of one still running settles that one's promise with an error (the duplicate is told apart now);
/// - a finished request keeps its id taken, so the next one with it is refused;
/// - a method with no deadline is tracked and can then be cancelled by the page's say-so.
/// </summary>
public sealed class BridgeRequestRegistryTests
{
    [Fact]
    public void AReusedIdWhileTheFirstRunsIsADuplicate()
    {
        var registry = new BridgeRequestRegistry();
        using var first = registry.Begin("r1", "library.get", CancellationToken.None);
        Assert.NotNull(first);
        Assert.Throws<BridgeRequestRegistry.DuplicateRequestException>(() => registry.Begin("r1", "library.get", CancellationToken.None));
        // The live one is untouched: its token is not cancelled by the refusal.
        Assert.False(first!.Token.IsCancellationRequested);
    }

    [Fact]
    public void AFinishedRequestFreesItsId()
    {
        var registry = new BridgeRequestRegistry();
        registry.Begin("r1", "library.get", CancellationToken.None)!.Dispose();
        using var again = registry.Begin("r1", "library.get", CancellationToken.None);
        Assert.NotNull(again);
    }

    [Fact]
    public void AMethodWithoutADeadlineIsNotTracked() =>
        Assert.Null(new BridgeRequestRegistry().Begin("r1", "app.version", CancellationToken.None));

    [Fact]
    public void CancelReachesOnlyTheNamedRequest()
    {
        var registry = new BridgeRequestRegistry();
        using var one = registry.Begin("a", "library.get", CancellationToken.None);
        using var two = registry.Begin("b", "library.get", CancellationToken.None);
        Assert.True(registry.Cancel("a"));
        Assert.True(one!.Token.IsCancellationRequested);
        Assert.False(two!.Token.IsCancellationRequested);
        Assert.False(registry.Cancel("missing"));
    }
}
