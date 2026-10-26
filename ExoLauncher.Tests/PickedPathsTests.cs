using ExoLauncher.Helpers;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways Add a game could register a program the user never chose:
/// - a path the page made up is accepted because nothing was picked;
/// - a pick works twice, or after it is long stale;
/// - a different spelling of the same path (case, trailing slash) is refused though the user picked it;
/// - a pick for one path opens another, or a blank path matches a blank pick.
/// </summary>
public sealed class PickedPathsTests
{
    private static readonly DateTime T = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void AnUnpickedPathIsRefused()
    {
        Assert.False(PickedPaths.Consume(@"C:\Games\neverpicked-1\game.exe", T));
        Assert.False(PickedPaths.Consume(null, T));
        Assert.False(PickedPaths.Consume("  ", T));
    }

    [Fact]
    public void APickWorksOnceAndInEitherSpelling()
    {
        PickedPaths.Remember(@"C:\Games\Alpha\game.exe", T);
        Assert.True(PickedPaths.Consume(@"c:\games\alpha\GAME.EXE", T.AddMinutes(1)));
        Assert.False(PickedPaths.Consume(@"C:\Games\Alpha\game.exe", T.AddMinutes(1)));
    }

    [Fact]
    public void AFolderPickIgnoresATrailingSlash()
    {
        PickedPaths.Remember(@"D:\Games\Beta\", T);
        Assert.True(PickedPaths.Consume(@"D:\Games\Beta", T));
    }

    [Fact]
    public void AStalePickIsRefused()
    {
        PickedPaths.Remember(@"C:\Games\Gamma\game.exe", T);
        Assert.False(PickedPaths.Consume(@"C:\Games\Gamma\game.exe", T + PickedPaths.Lifetime + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void APickOpensOnlyItsOwnPath()
    {
        PickedPaths.Remember(@"C:\Games\Delta\game.exe", T);
        Assert.False(PickedPaths.Consume(@"C:\Games\Delta\other.exe", T));
        Assert.True(PickedPaths.Consume(@"C:\Games\Delta\game.exe", T));
    }

    [Fact]
    public void OnlyTheLatestPicksAreKept()
    {
        for (var i = 0; i < 40; i++) PickedPaths.Remember($@"C:\Games\Many\g{i}.exe", T);
        Assert.False(PickedPaths.Consume(@"C:\Games\Many\g0.exe", T));
        Assert.True(PickedPaths.Consume(@"C:\Games\Many\g39.exe", T));
    }
}
