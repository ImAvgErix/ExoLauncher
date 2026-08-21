using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// The compact Now dock only spends space on actionable state. A live download
/// or running game outranks an update; recent-only state stays in the library.
/// </summary>
public sealed class NowBannerContractTests
{
    [Fact]
    public void Banner_IsUpdateOrLastLaunched_WithDownloadAndPlayOverrides()
    {
        var now = ReadRepoFile("ui", "src", "lib", "now.ts");

        Assert.Contains("export function pickNow(", now, StringComparison.Ordinal);
        Assert.Contains("kind: 'download'", now, StringComparison.Ordinal);
        Assert.Contains("kind: 'playing'", now, StringComparison.Ordinal);
        Assert.Contains("kind: 'update'", now, StringComparison.Ordinal);
        Assert.Contains("kind: 'recent'", now, StringComparison.Ordinal);
        Assert.Contains("game.installed && hasUpdate(game)", now, StringComparison.Ordinal);
        Assert.Contains("game.installed && game.lastPlayedUtc", now, StringComparison.Ordinal);

        Assert.DoesNotContain("export function nowPicks(", now, StringComparison.Ordinal);
        Assert.DoesNotContain("export function featuredNow(", now, StringComparison.Ordinal);
        Assert.DoesNotContain("ownedNotInstalled", now, StringComparison.Ordinal);
        Assert.DoesNotContain("kind: 'pinned'", now, StringComparison.Ordinal);
        Assert.DoesNotContain("kind: 'installed'", now, StringComparison.Ordinal);
        Assert.DoesNotContain("kind: 'owned'", now, StringComparison.Ordinal);
        Assert.DoesNotContain("Math.random", now, StringComparison.Ordinal);

        // Download and playing still win so a stale last-launched cannot hide work.
        Assert.Contains("if (picked.kind === 'download' || picked.kind === 'playing') return picked", now, StringComparison.Ordinal);
    }

    [Fact]
    public void Banner_HasNoPicksStrip()
    {
        var stage = ReadRepoFile("ui", "src", "components", "NowStage.tsx");
        var app = ReadRepoFile("ui", "src", "components", "LauncherApp.tsx");
        var tokens = ReadRepoFile("ui", "src", "tokens.css");

        Assert.DoesNotContain("exo-now-picks", stage, StringComparison.Ordinal);
        Assert.DoesNotContain("exo-now-pick", stage, StringComparison.Ordinal);
        Assert.DoesNotContain("picks.length", stage, StringComparison.Ordinal);
        Assert.DoesNotContain("onFeature", stage, StringComparison.Ordinal);
        Assert.DoesNotContain("featuredNowId", app, StringComparison.Ordinal);
        Assert.DoesNotContain("nowPicks", app, StringComparison.Ordinal);
        Assert.Contains("retainNow(games, picked, holdNowId.current)", app, StringComparison.Ordinal);
        Assert.Contains("game.isFavorite && game.id !== nowId", app, StringComparison.Ordinal);
        Assert.Contains("!game.isFavorite && game.id !== nowId", app, StringComparison.Ordinal);
        Assert.DoesNotContain("exo-now-picks", tokens, StringComparison.Ordinal);
        Assert.DoesNotContain("exo-now-pick", tokens, StringComparison.Ordinal);
        Assert.DoesNotContain("setInterval", stage, StringComparison.Ordinal);
    }

    [Fact]
    public void NowDock_IsCompactUsefulAndDoesNotCropDecorativeWideArt()
    {
        var stage = ReadRepoFile("ui", "src", "components", "NowStage.tsx");
        var tokens = ReadRepoFile("ui", "src", "tokens.css");
        var banner = Between(tokens, ".exo-now {", "}");

        Assert.Contains("<CoverArt game={game} preload", stage, StringComparison.Ordinal);
        Assert.Contains("exo-now-poster", stage + tokens, StringComparison.Ordinal);
        Assert.DoesNotContain("<HeroWash", stage, StringComparison.Ordinal);
        Assert.DoesNotContain("exo-now-veil", stage, StringComparison.Ordinal);
        Assert.DoesNotContain("is-active", stage, StringComparison.Ordinal);
        Assert.Contains("height: 112px", banner, StringComparison.Ordinal);
        Assert.Contains("min-height: 112px", banner, StringComparison.Ordinal);
        Assert.Contains("background:", banner, StringComparison.Ordinal);
        Assert.Contains("storeLabel(game.store)", stage, StringComparison.Ordinal);
        Assert.Contains("formatPlaytime(game.playtimeMinutes)", stage, StringComparison.Ordinal);
    }

    [Fact]
    public void RecentOnlyState_DoesNotReserveNowDockSpace()
    {
        var app = ReadRepoFile("ui", "src", "components", "LauncherApp.tsx");

        Assert.Contains("const visibleNow = now?.kind === 'recent' ? null : now", app, StringComparison.Ordinal);
        Assert.Contains("const nowId = visibleNow?.game.id ?? null", app, StringComparison.Ordinal);
        Assert.Contains("{visibleNow && !searching && (", app, StringComparison.Ordinal);
        Assert.DoesNotContain("{now && !searching && (", app, StringComparison.Ordinal);
    }

    [Fact]
    public void LibraryNav_DoesNotShowFriendsOnlineCount()
    {
        var app = ReadRepoFile("ui", "src", "components", "LauncherApp.tsx");

        Assert.DoesNotContain("friendCount", app, StringComparison.Ordinal);
        Assert.DoesNotContain("result.activeCount ?? 0", app, StringComparison.Ordinal);
        Assert.DoesNotContain("result.friends?.length", app, StringComparison.Ordinal);
    }

    [Fact]
    public void CoverArt_LeavesPosterCompositionAlone()
    {
        var cover = ReadRepoFile("ui", "src", "components", "CoverArt.tsx");
        var tokens = ReadRepoFile("ui", "src", "tokens.css");

        Assert.DoesNotContain("detectCoverBands", cover, StringComparison.Ordinal);
        Assert.DoesNotContain("sampleLoadedImage", cover, StringComparison.Ordinal);
        Assert.DoesNotContain("setFill", cover, StringComparison.Ordinal);
        Assert.DoesNotContain("is-band", cover, StringComparison.Ordinal);
        Assert.DoesNotContain(".exo-cover.is-band", tokens, StringComparison.Ordinal);
        Assert.DoesNotContain(".exo-cover.is-fill > img.exo-cover-front", tokens, StringComparison.Ordinal);
        Assert.DoesNotContain("last && el.naturalWidth", cover, StringComparison.Ordinal);
        Assert.Contains("shouldKeepCoverBitmap(el.naturalWidth, el.naturalHeight", cover, StringComparison.Ordinal);
        var fit = ReadRepoFile("ui", "src", "lib", "coverFit.ts");
        Assert.Contains("export function isPortraitBitmap", fit, StringComparison.Ordinal);
        Assert.Contains("if (hit) loadedUrlByKey.delete(key)", cover, StringComparison.Ordinal);
        Assert.Contains("candidates.some((entry) => entry.url === remembered)", cover, StringComparison.Ordinal);

        var candidates = Between(cover, "function portraitArtCandidates", "function loadBitmap");
        Assert.True(
            candidates.IndexOf("const raw = game.coverUrl", StringComparison.Ordinal) <
            candidates.IndexOf("const appId = steamAppId(game)", StringComparison.Ordinal),
            "the host-selected portrait must be tried before generic Steam fallbacks");
        Assert.Contains("isIconCover", cover, StringComparison.Ordinal);
        // Icons stay letterboxed on the dark plate.
        Assert.Contains(".exo-cover.is-icon img", tokens, StringComparison.Ordinal);
        Assert.Contains("object-fit: contain", tokens, StringComparison.Ordinal);
        Assert.Contains("#050505", tokens, StringComparison.Ordinal);
    }

    [Fact]
    public void Banner_StaysAPositionedClippingBox()
    {
        var tokens = ReadRepoFile("ui", "src", "tokens.css");

        // The wash is an absolute child. If the winning .exo-now block loses
        // `position`, it resolves against a distant ancestor and the store art
        // paints over the whole library.
        var last = tokens.LastIndexOf(".exo-now {", StringComparison.Ordinal);
        Assert.True(last > 0);
        var end = tokens.IndexOf('}', last);
        Assert.True(end > last);
        var block = tokens[last..end];
        Assert.Contains("position: relative", block, StringComparison.Ordinal);
        Assert.Contains("overflow: hidden", block, StringComparison.Ordinal);
    }

    [Fact]
    public void PinnedShelf_StaysSingleRowAndRemainsSidewaysScrollable()
    {
        var tokens = ReadRepoFile("ui", "src", "tokens.css");

        // The last rule wins in CSS, so assert the authoritative pinned block is a
        // single-row, keyboard/trackpad-scrollable shelf.
        var pinned = tokens.LastIndexOf(".exo-pin-track {", StringComparison.Ordinal);
        Assert.True(pinned > 0);
        var block = tokens[pinned..Math.Min(tokens.Length, pinned + 320)];
        Assert.Contains("display: grid", block, StringComparison.Ordinal);
        Assert.Contains("grid-auto-flow: column", block, StringComparison.Ordinal);
        Assert.Contains("grid-auto-columns: var(--exo-card-w)", block, StringComparison.Ordinal);
        Assert.Contains("overflow-x: auto", block, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ExoLauncher.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Between(string text, string start, string end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"missing '{start}'");
        var to = text.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to > from, $"missing '{end}' after '{start}'");
        return text[from..to];
    }

    private static string ReadRepoFile(params string[] relative) =>
        File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(relative).ToArray()));
}
