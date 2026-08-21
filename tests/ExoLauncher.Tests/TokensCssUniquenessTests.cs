using System.Text.RegularExpressions;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// Parallel edits kept re-appending the same <c>.exo-*</c> blocks. A later
/// copy silently wins, so a fix can land three times and still look broken.
/// Nested <c>@media</c> inside a rule is one definition; a second top-level
/// copy of the same selector is not.
/// </summary>
public sealed class TokensCssUniquenessTests
{
    [Fact]
    public void SharedShell_LoadsAfterProductTokens()
    {
        var main = ReadRepoFile("ui", "src", "main.tsx");
        var tokens = ReadRepoFile("ui", "src", "tokens.css");
        var shellCss = ReadRepoFile("ui", "src", "exo-shell.css");
        var product = main.IndexOf("import './tokens.css'", StringComparison.Ordinal);
        var shell = main.IndexOf("import './exo-shell.css'", StringComparison.Ordinal);

        Assert.True(product >= 0 && shell > product,
            "The shared Exo shell must own the final chrome/button cascade.");
        Assert.DoesNotContain("\n.exo-titlebar {", tokens, StringComparison.Ordinal);
        Assert.DoesNotContain("\n.exo-titlebar-button {", tokens, StringComparison.Ordinal);
        Assert.Contains("\n.exo-titlebar {", shellCss, StringComparison.Ordinal);
        Assert.Contains("\n.exo-titlebar-button {", shellCss, StringComparison.Ordinal);
    }

    [Fact]
    public void ExoSelectors_AreDefinedOnce()
    {
        var css = ReadRepoFile("ui", "src", "tokens.css");
        var counts = CountExoSelectors(css);
        var dups = counts
            .Where(pair => pair.Value > 1)
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key} x{pair.Value}")
            .ToArray();

        Assert.True(dups.Length == 0, "Duplicate .exo-* selectors:\n" + string.Join("\n", dups));
        Assert.True(counts.Count > 100, "Parser found too few .exo-* rules; uniqueness would be a false pass.");
    }

    internal static Dictionary<string, int> CountExoSelectors(string css)
    {
        var stripped = Comment.Replace(css, string.Empty);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var selector in Selectors(stripped))
        {
            if (!selector.Contains(".exo-", StringComparison.Ordinal)) continue;
            if (selector.Contains(".exo-trophy", StringComparison.Ordinal)) continue;
            var key = Ws.Replace(selector, " ").Trim();
            if (key.Length == 0) continue;
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }
        return counts;
    }

    private static IEnumerable<string> Selectors(string css)
    {
        var i = 0;
        var n = css.Length;
        while (i < n)
        {
            while (i < n && char.IsWhiteSpace(css[i])) i++;
            if (i >= n) yield break;
            if (css[i] == '}')
            {
                i++;
                continue;
            }

            if (css[i] == '@')
            {
                var j = i;
                while (j < n && css[j] is not '{' and not ';') j++;
                if (j >= n) yield break;
                var at = Ws.Replace(css[i..j], " ").Trim();
                if (css[j] == ';')
                {
                    i = j + 1;
                    continue;
                }

                var (inner, next) = SliceBlock(css, j);
                i = next;
                if (at.StartsWith("@keyframes", StringComparison.Ordinal)
                    || at.StartsWith("@theme", StringComparison.Ordinal)
                    || at.StartsWith("@import", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (var sel in Selectors(inner)) yield return sel;
                continue;
            }

            var start = i;
            while (i < n && css[i] != '{')
            {
                if (css[i] == '}') break;
                i++;
            }
            if (i >= n || css[i] != '{')
            {
                i++;
                continue;
            }

            var selector = css[start..i];
            var (body, after) = SliceBlock(css, i);
            i = after;
            yield return selector;
            _ = body;
        }
    }

    private static (string Inner, int Next) SliceBlock(string css, int openBrace)
    {
        var depth = 1;
        var k = openBrace + 1;
        while (k < css.Length && depth > 0)
        {
            var c = css[k];
            if (c == '{') depth++;
            else if (c == '}') depth--;
            k++;
        }
        return (css[(openBrace + 1)..(k - 1)], k);
    }

    private static readonly Regex Comment = new(@"/\*.*?\*/", RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex Ws = new(@"\s+", RegexOptions.CultureInvariant);

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
