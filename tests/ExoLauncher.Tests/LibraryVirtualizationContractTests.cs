using Xunit;

namespace ExoLauncher.Tests;

public sealed class LibraryVirtualizationContractTests
{
    [Fact]
    public void MainLibraryGrid_UsesTheRealScrollRootAndAWindowedWholeRowGrid()
    {
        var app = ReadRepoFile("ui", "src", "components", "LauncherApp.tsx");
        var shelf = ReadRepoFile("ui", "src", "components", "BrowseShelf.tsx");
        var grid = ReadRepoFile("ui", "src", "components", "WindowedGameGrid.tsx");

        Assert.Contains("className={`exo-library-pane", app, StringComparison.Ordinal);
        Assert.Contains("scrollRootRef={libraryMainRef}", app, StringComparison.Ordinal);
        Assert.Contains("<WindowedGameGrid", shelf, StringComparison.Ordinal);
        Assert.Contains("new ResizeObserver", grid, StringComparison.Ordinal);
        Assert.Contains("scrollRoot.addEventListener('scroll'", grid, StringComparison.Ordinal);
        Assert.Contains("layoutKey", grid, StringComparison.Ordinal);
        Assert.Contains("gameOrderKey", grid, StringComparison.Ordinal);
        Assert.Contains("GRID_OVERSCAN_ROWS", grid, StringComparison.Ordinal);
        Assert.Contains("role=\"grid\"", grid, StringComparison.Ordinal);
        Assert.Contains("aria-rowcount", grid, StringComparison.Ordinal);
        Assert.Contains("role=\"row\"", grid, StringComparison.Ordinal);
        Assert.Contains("aria-rowindex", grid, StringComparison.Ordinal);
        Assert.Contains("beforeHeight", grid, StringComparison.Ordinal);
        Assert.Contains("afterHeight", grid, StringComparison.Ordinal);
    }

    [Fact]
    public void LibraryOrdering_HasOneOwner_AndFocusUsesStableGameKeys()
    {
        var app = ReadRepoFile("ui", "src", "components", "LauncherApp.tsx");
        var shelf = ReadRepoFile("ui", "src", "components", "BrowseShelf.tsx");
        var grid = ReadRepoFile("ui", "src", "components", "WindowedGameGrid.tsx");

        Assert.Contains("const gridGames = useMemo", app, StringComparison.Ordinal);
        Assert.DoesNotContain("smartSearchScore", shelf, StringComparison.Ordinal);
        Assert.DoesNotContain("sortGames", shelf, StringComparison.Ordinal);
        Assert.Contains("activeGameId", grid, StringComparison.Ordinal);
        Assert.Contains("resolveActiveGameId", grid, StringComparison.Ordinal);
        Assert.DoesNotContain("onActiveGameChange(resolvedActiveId)", grid, StringComparison.Ordinal);
        Assert.DoesNotContain("document.querySelector<HTMLElement>('.exo-game-grid')", app, StringComparison.Ordinal);
        Assert.DoesNotContain("openGamePage(focused.id", app, StringComparison.Ordinal);

        var openGamePageStart = app.IndexOf("function openGamePage", StringComparison.Ordinal);
        var setActionStatus = app.IndexOf("setActionStatus(null, null)", openGamePageStart, StringComparison.Ordinal);
        Assert.True(openGamePageStart >= 0 && setActionStatus > openGamePageStart);
        Assert.Contains("setQuery('')", app[openGamePageStart..setActionStatus], StringComparison.Ordinal);
    }

    [Fact]
    public void MainGrid_HasRovingFocusWithoutKeyboardLaunchAndCoverCacheSurvivesRemounts()
    {
        var card = ReadRepoFile("ui", "src", "components", "GameCard.tsx");
        var grid = ReadRepoFile("ui", "src", "components", "WindowedGameGrid.tsx");
        var covers = ReadRepoFile("ui", "src", "components", "CoverArt.tsx");

        Assert.Contains("tabIndex={tabIndex}", card, StringComparison.Ordinal);
        Assert.Contains("role={gridPosition ? 'gridcell'", card, StringComparison.Ordinal);
        Assert.Contains("moveGridFocusIndex", grid, StringComparison.Ordinal);
        Assert.Contains("'PageUp'", grid, StringComparison.Ordinal);
        Assert.Contains("'PageDown'", grid, StringComparison.Ordinal);
        Assert.Contains("gridOwnsFocus", grid, StringComparison.Ordinal);
        Assert.Contains("gridRoot.focus({ preventScroll: true })", grid, StringComparison.Ordinal);
        Assert.DoesNotContain("data-game-pin", card + grid, StringComparison.Ordinal);
        Assert.DoesNotContain("aria-keyshortcuts", card, StringComparison.Ordinal);
        Assert.DoesNotContain("onActivate?.()", grid, StringComparison.Ordinal);
        Assert.Contains("const loadedUrlByKey = new Map<string, string>()", covers, StringComparison.Ordinal);
    }

    private static string ReadRepoFile(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray()));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ExoLauncher.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("Repo root not found.");
    }
}
