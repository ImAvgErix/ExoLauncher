using Xunit;

namespace ExoLauncher.Tests;

public sealed class SmartSearchUiContractTests
{
    [Fact]
    public void InstalledLibrarySearch_UsesSharedBoundedSmartSearchScoring()
    {
        var utils = ReadRepoFile("ui", "src", "lib", "utils.ts");
        var launcher = ReadRepoFile("ui", "src", "components", "LauncherApp.tsx");

        Assert.Contains("export function smartSearchScore", utils, StringComparison.Ordinal);
        Assert.Contains("boundedDamerauLevenshtein", utils, StringComparison.Ordinal);
        Assert.Contains("normalizeSearchText", utils, StringComparison.Ordinal);
        Assert.Contains("titleToken.length >= 3", utils, StringComparison.Ordinal);
        Assert.Contains("smartSearchScore(game.title, q)", launcher, StringComparison.Ordinal);
        Assert.Contains("b.score - a.score", launcher, StringComparison.Ordinal);
        Assert.DoesNotContain("g.title.toLowerCase().includes(q)", launcher, StringComparison.Ordinal);
    }

    [Fact]
    public void InstalledLibrarySort_KeepsRegisteredLocalGames()
    {
        var utils = ReadRepoFile("ui", "src", "lib", "utils.ts");

        Assert.Contains("g.id !== 'local:add'", utils, StringComparison.Ordinal);
        Assert.DoesNotContain("g.store !== 'local'", utils, StringComparison.Ordinal);
    }

    [Fact]
    public void CatalogSearch_ClearsResultsFromThePreviousQueryImmediately()
    {
        var launcher = ReadRepoFile("ui", "src", "components", "LauncherApp.tsx");
        var generation = launcher.IndexOf("const gen = ++searchGen.current", StringComparison.Ordinal);
        var clear = launcher.IndexOf("setCatalogHits([])", generation, StringComparison.Ordinal);
        var debounce = launcher.IndexOf("window.setTimeout(() =>", generation, StringComparison.Ordinal);

        Assert.True(generation >= 0 && clear > generation && clear < debounce,
            "Old catalog hits must disappear before the replacement query is debounced.");
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
}
