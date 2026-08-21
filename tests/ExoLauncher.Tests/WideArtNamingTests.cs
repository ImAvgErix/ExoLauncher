using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// Banner art is passed from host to UI by filename convention, not a bridge
/// field. If the two sanitizers ever disagree the UI asks for a file that was
/// never written and every banner silently falls back, so pin the contract.
/// </summary>
public sealed class WideArtNamingTests
{
    [Theory]
    [InlineData("steam:1091500", "hero_steam_1091500.jpg")]
    [InlineData("epic:fortnite", "hero_epic_fortnite.jpg")]
    [InlineData("riot:valorant", "hero_riot_valorant.jpg")]
    [InlineData("local:C:\\Games\\Doom (2016)", "hero_local_C__Games_Doom__2016_.jpg")]
    [InlineData("steam:Hell is Us\u2122", "hero_steam_Hell_is_Us_.jpg")]
    public void WideArtFileName_ReplacesEverythingButLettersAndDigits(string gameId, string expected)
    {
        Assert.Equal(expected, ExoLauncher.Services.CoverArtService.WideArtFileName(gameId));
    }

    [Fact]
    public void WideArtFileName_TreatsEachSurrogateHalfSeparately()
    {
        // char.IsLetterOrDigit runs per UTF-16 unit, so an astral character
        // becomes TWO underscores. The UI loop must do the same.
        const string astral = "local:\U0001F600";
        Assert.Equal("hero_local___.jpg", ExoLauncher.Services.CoverArtService.WideArtFileName(astral));
    }

    [Fact]
    public void WideArtFileName_KeepsNonDecimalNumeralsOut()
    {
        // '\u00BD' (one half) is Unicode category No, not Nd, so .NET drops it.
        // A UI regex using \p{N} would keep it and request the wrong file.
        Assert.Equal("hero_local_Game__.jpg", ExoLauncher.Services.CoverArtService.WideArtFileName("local:Game \u00BD"));
    }

    [Fact]
    public void Ui_MirrorsTheHostSanitizerPerCodeUnit()
    {
        var art = ReadRepoFile("ui", "src", "components", "CoverArt.tsx");

        Assert.Contains("function sanitizeCacheId(", art, StringComparison.Ordinal);
        // Per code unit, letters plus DECIMAL digits only.
        Assert.Contains("/[\\p{L}\\p{Nd}]/u.test(unit)", art, StringComparison.Ordinal);
        Assert.Contains("i += 1", art, StringComparison.Ordinal);
        // The old loose form must not come back.
        Assert.DoesNotContain("replace(/[^\\p{L}\\p{N}]/gu", art, StringComparison.Ordinal);
        Assert.Contains("hero_${sanitizeCacheId(gameId)}.jpg", art, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ExoLauncher.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string ReadRepoFile(params string[] relative) =>
        File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(relative).ToArray()));
}
