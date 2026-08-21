using Xunit;

namespace ExoLauncher.Tests;

public sealed class GameArtworkUiContractTests
{
    [Fact]
    public void Replace_UsesOnlyTheNativePickerAndTheUiSendsOnlyAnId()
    {
        var bridge = Read("ExoLauncher", "Services", "WebHostBridge.cs");
        var host = Read("ui", "src", "lib", "host.ts");
        var page = Read("ui", "src", "components", "GamePage.tsx");
        var replace = Slice(bridge, "private async Task<object> ArtworkReplaceAsync", "private async Task<object> ArtworkResetAsync");

        Assert.Contains("PickImageFileAsync()", replace, StringComparison.Ordinal);
        Assert.Contains("ReplaceAsync(gameId, picked.Path)", replace, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadString(p, hasParams, \"path\")", replace, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadString(p, hasParams, \"sourcePath\")", replace, StringComparison.Ordinal);
        Assert.Contains("artReplace: (id: string) => rawCall<ArtworkMutationResponse>('art.replace', { id })", host, StringComparison.Ordinal);
        Assert.DoesNotContain("input type=\"file\"", page + host, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FileReader", page + host, StringComparison.Ordinal);
        Assert.DoesNotContain("file://", page + host, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ArtworkMutations_ReturnAnAuthoritativeGameAndRevisionIncludingNullReset()
    {
        var bridge = Read("ExoLauncher", "Services", "WebHostBridge.cs");
        var host = Read("ui", "src", "lib", "host.ts");
        var page = Read("ui", "src", "components", "GamePage.tsx");
        var art = Read("ExoLauncher", "Services", "GameArtworkService.cs");

        Assert.Contains("game = result.Game is null ? null : MapGame(result.Game)", bridge, StringComparison.Ordinal);
        Assert.Contains("result.ArtRevision", bridge, StringComparison.Ordinal);
        Assert.Contains("artRevision = g.ArtRevision", bridge, StringComparison.Ordinal);
        Assert.Contains("coverUrl: artworkGame.coverUrl ?? null", page, StringComparison.Ordinal);
        Assert.DoesNotContain("artworkGame.coverUrl ||", page, StringComparison.Ordinal);
        Assert.Contains("PublishArtworkChangeAsync", art, StringComparison.Ordinal);
        Assert.Contains("case 'art.reset':", host, StringComparison.Ordinal);
        Assert.Contains("emitHostEvent('library.updated'", host, StringComparison.Ordinal);
    }

    [Fact]
    public void GamePage_OffersRestrainedCardLevelArtworkControlsForOwnedUninstalledGames()
    {
        var page = Read("ui", "src", "components", "GamePage.tsx");

        Assert.Contains("const artworkEnabled = !selected.isAddPortable && (!!selected.owned || selected.installed)", page, StringComparison.Ordinal);
        Assert.Contains("exo-game-tools exo-utility-row", page, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Game utilities\"", page, StringComparison.Ordinal);
        Assert.Contains("Replace cover", page, StringComparison.Ordinal);
        Assert.Contains("Reset cover", page, StringComparison.Ordinal);
        Assert.Contains("Refetch artwork", page, StringComparison.Ordinal);
        Assert.Contains("Report wrong art", page, StringComparison.Ordinal);
        Assert.Contains("artAction !== null", page, StringComparison.Ordinal);
        Assert.Contains("sourceSwitchLocked = busy || repairing || uninstalling || artAction !== null", page, StringComparison.Ordinal);
    }

    [Fact]
    public void CoverRendering_BustsReactAndWebViewCachesWithTheHostRevision()
    {
        var cover = Read("ui", "src", "components", "CoverArt.tsx");

        Assert.Contains("artRevision", cover, StringComparison.Ordinal);
        Assert.Contains("withArtRevision", cover, StringComparison.Ordinal);
        Assert.Contains("rev=${artRevision}", cover, StringComparison.Ordinal);
        Assert.Contains("cacheKey(game.id, 'poster', game.artRevision)", cover, StringComparison.Ordinal);
        Assert.Contains("cacheKey(game.id, 'wash', game.artRevision)", cover, StringComparison.Ordinal);
    }

    [Fact]
    public void ArtworkReport_IsBoundedLocalAndCanOpenOnlyTheFixedEmptyIssuePage()
    {
        var bridge = Read("ExoLauncher", "Services", "WebHostBridge.cs");
        var art = Read("ExoLauncher", "Services", "GameArtworkService.cs");

        Assert.Contains("public const int MaxReportBytes = 4 * 1024", art, StringComparison.Ordinal);
        Assert.Contains("No file paths or image bytes are included.", art, StringComparison.Ordinal);
        Assert.Contains("package.SetText(report.Diagnostics)", bridge, StringComparison.Ordinal);
        Assert.Contains("new Uri(GameArtworkService.IssueUrl)", bridge, StringComparison.Ordinal);
        Assert.DoesNotContain("report.Diagnostics)", Slice(bridge, "LaunchUriAsync(", "catch (Exception ex)"), StringComparison.Ordinal);
        Assert.Contains("https://github.com/ImAvgErix/ExoLauncher/issues/new", art, StringComparison.Ordinal);
    }

    [Fact]
    public void CustomCoverSettings_AreNeverPartOfPortableAccountSync()
    {
        var synced = Read("ExoLauncher", "Services", "ExoIdentity", "ExoSyncedSettings.cs");
        Assert.DoesNotContain("CustomCoverImages", synced, StringComparison.Ordinal);
        Assert.DoesNotContain("customCoverImages", synced, StringComparison.Ordinal);
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray()));

    private static string Slice(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, "Missing " + start);
        var to = source.IndexOf(end, from + start.Length, StringComparison.Ordinal);
        Assert.True(to > from, "Missing " + end);
        return source[from..to];
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
