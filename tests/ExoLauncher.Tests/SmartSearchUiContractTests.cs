using ExoLauncher.Ui;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class SmartSearchUiContractTests
{
    [Fact]
    public void InstalledLibrarySearch_UsesSharedBoundedSmartSearchScoring()
    {
        var search = ReadRepoFile("ExoLauncher", "Ui", "LibrarySearch.cs");
        var app = ReadRepoFile("ui", "src", "components", "LauncherApp.tsx");
        var utils = ReadRepoFile("ui", "src", "lib", "utils.ts");

        Assert.Contains("StoreSearchService.Normalize", search, StringComparison.Ordinal);
        Assert.Contains("Damerau(", search, StringComparison.Ordinal);
        Assert.Contains("titleToken[0] != queryToken[0]", search, StringComparison.Ordinal);
        Assert.Contains("smartSearchScore(game.title, q)", app, StringComparison.Ordinal);
        Assert.Contains("export function smartSearchScore", utils, StringComparison.Ordinal);
        Assert.DoesNotContain("g.Title.ToLower().Contains", app, StringComparison.Ordinal);
        Assert.True(LibrarySearch.Score("Counter-Strike 2", "counter") > 0);
        Assert.Equal(-1, LibrarySearch.Score("Valorant", "zzzz"));
    }

    [Fact]
    public void InstalledLibrarySearch_AcceptsJoinedWordsWithOrdinaryTypos()
    {
        var utils = ReadRepoFile("ui", "src", "lib", "utils.ts");

        Assert.True(LibrarySearch.Score("Marvel's Spider-Man Remastered", "spidrman remasterd") > 0);
        Assert.Contains("expandAdjacentTokens(searchTokens(normalizedTitle))", utils, StringComparison.Ordinal);
    }

    [Fact]
    public void InstalledLibrarySort_KeepsRegisteredLocalGames()
    {
        var format = ReadRepoFile("ExoLauncher", "Ui", "UiFormat.cs");
        Assert.Contains("game.Id, \"local:add\"", format, StringComparison.Ordinal);
        Assert.DoesNotContain("g.Store != StoreKind.Local", format, StringComparison.Ordinal);
    }

    [Fact]
    public void CatalogSearch_ClearsResultsFromThePreviousQueryImmediately()
    {
        var app = ReadRepoFile("ui", "src", "components", "LauncherApp.tsx");
        var generation = app.IndexOf("const gen = ++searchGen.current", StringComparison.Ordinal);
        var clear = app.IndexOf("setCatalogHits([])", generation, StringComparison.Ordinal);
        var debounce = app.IndexOf("window.setTimeout", generation, StringComparison.Ordinal);

        Assert.True(generation >= 0 && clear > generation && clear < debounce,
            "Old catalog hits must disappear before the replacement query is debounced.");
    }

    [Fact]
    public void HeaderSearch_AnimatesARealCapsuleWithoutQueryingOnFocus()
    {
        var app = ReadRepoFile("ui", "src", "components", "LauncherApp.tsx");
        var tokens = ReadRepoFile("ui", "src", "tokens.css");
        var window = ReadRepoFile("ExoLauncher", "MainWindow.xaml.cs");
        var search = CssBlock(tokens, ".exo-titlebar-search {");
        var input = CssBlock(tokens, ".exo-titlebar-search .exo-search {");

        Assert.Contains("placeholder=\"Search\"", app, StringComparison.Ordinal);
        Assert.Contains("exo-titlebar-search", app, StringComparison.Ordinal);
        Assert.Contains("exo-search-capsule", app, StringComparison.Ordinal);
        Assert.Contains("searchInputRef.current?.focus()", app, StringComparison.Ordinal);
        Assert.Contains("setView('library')", app, StringComparison.Ordinal);
        Assert.DoesNotContain("onFocus={() => setQuery", app, StringComparison.Ordinal);
        Assert.DoesNotContain("onBlur={() => setQuery", app, StringComparison.Ordinal);
        Assert.DoesNotContain("searchFocused", app, StringComparison.Ordinal);
        Assert.DoesNotContain(" is-open", app, StringComparison.Ordinal);
        Assert.DoesNotContain("exo-search-glyph", app + tokens, StringComparison.Ordinal);
        Assert.DoesNotContain("import { Loader2, Search, Settings }", app, StringComparison.Ordinal);
        Assert.DoesNotContain("exo-nav-ink", app, StringComparison.Ordinal);
        Assert.DoesNotContain("@keyframes exo-ink", tokens, StringComparison.Ordinal);
        Assert.Contains("width: 96px", search, StringComparison.Ordinal);
        Assert.Contains("height: 32px", search, StringComparison.Ordinal);
        Assert.Contains("padding: 0", search, StringComparison.Ordinal);
        Assert.Contains("padding: 0 14px", input, StringComparison.Ordinal);
        Assert.Contains("width 200ms var(--ease-in-out)", tokens, StringComparison.Ordinal);
        Assert.Contains(".exo-titlebar-search:focus-within", tokens, StringComparison.Ordinal);
        Assert.Contains(".exo-titlebar-search.has-query", tokens, StringComparison.Ordinal);
        Assert.Contains("width: 184px", tokens, StringComparison.Ordinal);
        Assert.Contains("width: 96px", tokens, StringComparison.Ordinal);
        Assert.Contains("border: 1px solid rgba(255, 255, 255, 0.12)", tokens, StringComparison.Ordinal);
        Assert.DoesNotContain("clip-path: inset(0 58px round 999px)", tokens, StringComparison.Ordinal);
        Assert.Contains("query ? ' has-query' : ''", app, StringComparison.Ordinal);
        Assert.Contains(".exo-app .exo-search-capsule", tokens, StringComparison.Ordinal);
        Assert.Contains("transition-property: border-color, background-color", tokens, StringComparison.Ordinal);
        Assert.Contains("TitleBarSearchPassthroughDip = 184", window, StringComparison.Ordinal);
        Assert.Contains("TitleBarSearchHeightDip = 32", window, StringComparison.Ordinal);
        Assert.Contains("(titleH - searchPillH) / 2", window, StringComparison.Ordinal);
        Assert.Contains(".exo-titlebar-search .exo-search:focus::placeholder", tokens, StringComparison.Ordinal);
        Assert.Contains("caret-color: #f2f2f2", tokens, StringComparison.Ordinal);
        Assert.Contains("caret-color: transparent", input, StringComparison.Ordinal);
        Assert.Contains("transition: caret-color 0s linear 200ms", input, StringComparison.Ordinal);
        Assert.Contains("}, 140)", app, StringComparison.Ordinal);
    }

    private static string ReadRepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine([dir.FullName, .. parts]);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = dir.Parent;
        }
        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, parts));
    }

    private static string CssBlock(string css, string selector)
    {
        var start = css.IndexOf(selector, StringComparison.Ordinal);
        Assert.True(start >= 0, $"missing '{selector}'");
        var end = css.IndexOf('}', start);
        Assert.True(end > start, $"missing end of '{selector}'");
        return css[start..end];
    }
}
