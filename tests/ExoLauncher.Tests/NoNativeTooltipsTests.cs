using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// The user does not want Windows' own tooltip appearing over Exo's chrome.
/// Hover text belongs in `aria-label`, which assistive tech reads and Windows
/// does not draw.
/// </summary>
public sealed class NoNativeTooltipsTests
{
    [Fact]
    public void NoComponentSetsTheHtmlTitleAttribute()
    {
        var root = Path.Combine(RepoRoot(), "ui", "src");
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(root, "*.tsx", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                // `title="..."` or `title={...}` as a JSX prop. ExoMark takes a
                // `title` prop that it renders as aria-label, so allow passing it
                // to a component while banning it on intrinsic elements.
                if (!line.Contains(" title=", StringComparison.Ordinal)) continue;
                if (line.Contains("<ExoMark", StringComparison.Ordinal)) continue;
                offenders.Add($"{Path.GetFileName(file)}:{i + 1}: {line.Trim()}");
            }
        }

        Assert.True(offenders.Count == 0, "native tooltips found:\n" + string.Join("\n", offenders));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ExoLauncher.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
