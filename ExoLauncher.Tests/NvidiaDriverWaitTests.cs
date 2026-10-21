using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the driver install's wait for a game can fail (a clean install blanks the screen, so it must never
/// start under a game started during the download):
/// - it goes on while a game still runs;
/// - it says "Waiting for your game to close" when no game runs, or says it again on every poll;
/// - it never returns once the game has closed.
/// </summary>
public sealed class NvidiaDriverWaitTests
{
    [Fact]
    public async Task WaitsWhileAGameRunsAndSaysSoOnce()
    {
        var checks = 0;
        var said = 0;
        await NvidiaDriverService.WaitWhileAsync(() => ++checks <= 3, () => said++, TimeSpan.FromMilliseconds(1));
        Assert.Equal(4, checks);
        Assert.Equal(1, said);
    }

    [Fact]
    public async Task GoesStraightOnWithoutAGame()
    {
        var said = 0;
        await NvidiaDriverService.WaitWhileAsync(() => false, () => said++, TimeSpan.FromMilliseconds(1));
        Assert.Equal(0, said);
    }
}
