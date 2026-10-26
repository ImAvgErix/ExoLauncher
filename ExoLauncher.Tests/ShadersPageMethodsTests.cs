using System.Text.RegularExpressions;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// The Shaders page and the methods Exo lets it ask its engine, which nobody notices drifting apart until a button
/// fails with "Unknown request.": a page method left off the list (Refresh, Pause, Resume and Stop were, in 3.15.1's
/// first build); a method the page never calls left on it, widening what the page can reach for nothing; a method that
/// does no work (reading the board, saying it is on screen, moving or pausing what isn't running) starting the engine,
/// which then runs with nothing pressed.
/// </summary>
public sealed partial class ShadersPageMethodsTests
{
    [Fact]
    public void Only_Compile_and_Refresh_start_the_engine()
    {
        Assert.Equal(["game.compile", "queue.add", "queue.resume", "queue.start", "scan"], ShadersAddon.StartMethods.Order(StringComparer.Ordinal));
        Assert.True(ShadersAddon.StartMethods.IsSubsetOf(ShadersAddon.PageMethods));
    }

    private static HashSet<string> PageCalls()
    {
        var source = string.Join('\n', Directory.GetFiles(Repo.PathOf("ui", "src", "components", "shaders"), "*.tsx").Select(File.ReadAllText));
        return [.. EngineMethod().Matches(source).Select(m => m.Groups[1].Value)];
    }

    [Fact]
    public void Every_method_the_page_calls_is_allowed()
    {
        var missing = PageCalls().Except(ShadersAddon.PageMethods).ToList();
        Assert.True(missing.Count == 0, "Not allowed for the Shaders page: " + string.Join(", ", missing));
    }

    [Fact]
    public void Every_allowed_method_is_one_the_page_calls()
    {
        var unused = ShadersAddon.PageMethods.Except(PageCalls()).ToList();
        Assert.True(unused.Count == 0, "Allowed but never called by the Shaders page: " + string.Join(", ", unused));
    }

    /// <summary>An engine method in the page's source: call('snapshot'), or a step of run(): ['queue.add', ...] / [cond ? 'queue.resume' : 'queue.pause'].</summary>
    [GeneratedRegex(@"(?:call\(|\[|\? |: )'((?:snapshot|scan|ui\.[a-z]+|queue\.[a-z]+|game\.[a-z]+))'")]
    private static partial Regex EngineMethod();
}
