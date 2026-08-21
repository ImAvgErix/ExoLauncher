using Xunit;

namespace ExoLauncher.Tests;

public sealed class PremiumUxContractTests
{
    [Fact]
    public void ProfileAndAccount_UseOneIdentitySurfaceWithoutManualCloudButtons()
    {
        var profile = ReadRepoFile("ui", "src", "components", "ProfileRoom.tsx");
        var account = ReadRepoFile("ui", "src", "components", "AccountPanel.tsx");

        Assert.DoesNotContain("Copy public profile", profile, StringComparison.Ordinal);
        Assert.DoesNotContain("Open public profile", profile, StringComparison.Ordinal);
        Assert.DoesNotContain("Reserve a handle", account, StringComparison.Ordinal);
        Assert.DoesNotContain("Save this PC to Exo", account, StringComparison.Ordinal);
        Assert.DoesNotContain("Profile privacy", account, StringComparison.Ordinal);
        Assert.DoesNotContain("<span>Optional</span>", account, StringComparison.Ordinal);
        Assert.Contains("Auto-saved to Exo", account, StringComparison.Ordinal);
    }

    [Fact]
    public void Onboarding_FillsTheWindowAndUsesOneRequiredAccountFlow()
    {
        var panel = ReadRepoFile("ui", "src", "components", "OnboardingPanel.tsx");
        var css = ReadRepoFile("ui", "src", "tokens.css");

        Assert.DoesNotContain("No Exo account required", panel, StringComparison.Ordinal);
        Assert.DoesNotContain("No Exo account is required", panel, StringComparison.Ordinal);
        Assert.DoesNotContain("exo-onboarding-profile-handle", panel, StringComparison.Ordinal);
        Assert.DoesNotContain("Save profile", panel, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(panel, "<AccountPanel"));
        Assert.Contains("Create or sign in to your Exo account", panel, StringComparison.Ordinal);
        Assert.DoesNotContain("Continue offline", panel, StringComparison.Ordinal);
        Assert.DoesNotContain("offlineChosen", panel, StringComparison.Ordinal);
        Assert.Contains("serviceUnavailable || (!!accountState?.signedIn && !!accountState.handle)", panel, StringComparison.Ordinal);
        Assert.Contains("Steam Web API key", panel, StringComparison.Ordinal);
        Assert.Contains("Choose a handle once.", panel, StringComparison.Ordinal);

        var shellStart = css.IndexOf(".exo-onboarding-shell {", StringComparison.Ordinal);
        var shellEnd = css.IndexOf('}', shellStart);
        Assert.True(shellStart >= 0 && shellEnd > shellStart);
        var shell = css[shellStart..shellEnd];
        Assert.Contains("width: 100%", shell, StringComparison.Ordinal);
        Assert.Contains("height: 100%", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("width: min(", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("height: min(", shell, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_HideEnforcedRowsAndKeepPrivacyInOnePlace()
    {
        var settings = ReadRepoFile("ui", "src", "components", "SettingsPanel.tsx");

        Assert.DoesNotContain("function LockedRow", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("<LockedRow", settings, StringComparison.Ordinal);
        Assert.Contains("Profile privacy", settings, StringComparison.Ordinal);
        Assert.Contains("Coming soon", settings, StringComparison.Ordinal);
        Assert.Contains("setStatus(null)", settings, StringComparison.Ordinal);
        Assert.Contains("setOnlineNote(null)", settings, StringComparison.Ordinal);
    }

    [Fact]
    public void TrophySettings_ReplayTheInCardAnimationWithoutAPreviewButton()
    {
        var settings = ReadRepoFile("ui", "src", "components", "TrophyNotificationSettings.tsx");
        var helper = ReadRepoFile("ui", "src", "lib", "trophyBanner.ts");

        Assert.DoesNotContain("previewBusy", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("onPreview", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("'Preview'", settings, StringComparison.Ordinal);
        Assert.Contains("onAnimationComplete={queueReplay}", settings, StringComparison.Ordinal);
        Assert.Contains("EXO NOVA", helper, StringComparison.Ordinal);
    }

    [Fact]
    public void GameDetails_PutEveryUtilityInOneResponsiveRow()
    {
        var page = ReadRepoFile("ui", "src", "components", "GamePage.tsx");

        Assert.Equal(1, CountOccurrences(page, "className=\"exo-game-tools"));
        Assert.Contains("aria-label=\"Game utilities\"", page, StringComparison.Ordinal);
        Assert.Contains("Replace cover", page, StringComparison.Ordinal);
        Assert.Contains("Refetch artwork", page, StringComparison.Ordinal);
        Assert.Contains("Open folder", page, StringComparison.Ordinal);
        Assert.Contains("Verify files", page, StringComparison.Ordinal);
        Assert.Contains("Remove", page, StringComparison.Ordinal);
    }

    [Fact]
    public void LibraryCards_ArePosterFirstWithTextOnThePageSurface()
    {
        var css = ReadRepoFile("ui", "src", "tokens.css");
        var hitStart = css.IndexOf(".exo-tile-hit {", StringComparison.Ordinal);
        var hitEnd = css.IndexOf('}', hitStart);
        var metaStart = css.IndexOf(".exo-card-meta {", StringComparison.Ordinal);
        var metaEnd = css.IndexOf('}', metaStart);
        Assert.True(hitStart >= 0 && hitEnd > hitStart && metaStart >= 0 && metaEnd > metaStart);

        var hit = css[hitStart..hitEnd];
        var meta = css[metaStart..metaEnd];
        Assert.Contains("overflow: visible", hit, StringComparison.Ordinal);
        Assert.Contains("background: transparent", hit, StringComparison.Ordinal);
        Assert.Contains("padding: 8px 2px 4px", meta, StringComparison.Ordinal);
        Assert.DoesNotContain("background:", meta, StringComparison.Ordinal);
        Assert.DoesNotContain("border:", meta, StringComparison.Ordinal);
    }

    [Fact]
    public void PinnedShelf_StaysSingleRowAndNeverFansOutDetailMetadata()
    {
        var css = ReadRepoFile("ui", "src", "tokens.css");
        var launcher = ReadRepoFile("ui", "src", "components", "LauncherApp.tsx");
        var grid = ReadRepoFile("ui", "src", "components", "WindowedGameGrid.tsx");

        var shelf = SliceCss(css, ".exo-pin-track {");
        Assert.Contains("grid-auto-flow: column", shelf, StringComparison.Ordinal);
        Assert.Contains("grid-auto-columns: var(--exo-card-w)", shelf, StringComparison.Ordinal);
        Assert.Contains("overflow-x: auto", shelf, StringComparison.Ordinal);
        Assert.Contains("scroll-snap-type: none", shelf, StringComparison.Ordinal);
        Assert.DoesNotContain("repeat(auto-fill", shelf, StringComparison.Ordinal);
        Assert.DoesNotContain("useGameMetadata", launcher, StringComparison.Ordinal);
        Assert.DoesNotContain("useGameMetadata", grid, StringComparison.Ordinal);
        Assert.DoesNotContain("metadata=", launcher, StringComparison.Ordinal);
        Assert.DoesNotContain("metadata=", grid, StringComparison.Ordinal);
        Assert.Contains("Show earlier pinned games", launcher, StringComparison.Ordinal);
        Assert.Contains("Show later pinned games", launcher, StringComparison.Ordinal);
    }

    [Fact]
    public void Friends_DoNotFlashAnUnavailableStateBeforeTheFirstOnlineResult()
    {
        var friends = ReadRepoFile("ui", "src", "components", "FriendsRoom.tsx");

        Assert.Contains("diagnostics === null", friends, StringComparison.Ordinal);
        Assert.Contains("Checking Exo friends", friends, StringComparison.Ordinal);
        Assert.DoesNotContain("Online friends are unavailable. Local people stay here.", friends[..friends.IndexOf("diagnostics === null", StringComparison.Ordinal)], StringComparison.Ordinal);
    }

    [Fact]
    public void Friends_RefreshesEachOnlineCapabilityWithoutAnAllOrNothingPromise()
    {
        var friends = ReadRepoFile("ui", "src", "components", "FriendsRoom.tsx");

        Assert.Contains("Promise.allSettled", friends, StringComparison.Ordinal);
        Assert.Contains("onlinePresence", friends, StringComparison.Ordinal);
        Assert.Contains("presenceResult.status === 'fulfilled'", friends, StringComparison.Ordinal);
    }

    [Fact]
    public void GameCard_IsCoverAndFullTitleWhileDetailsOwnCatalogMetadata()
    {
        var card = ReadRepoFile("ui", "src", "components", "GameCard.tsx");
        var page = ReadRepoFile("ui", "src", "components", "GamePage.tsx");

        Assert.DoesNotContain("metadataText", card, StringComparison.Ordinal);
        Assert.DoesNotContain("GameMetadata", card, StringComparison.Ordinal);
        Assert.Contains("{game.title}", card, StringComparison.Ordinal);
        Assert.Contains("metadata?.genre, metadata?.year", page, StringComparison.Ordinal);
    }

    [Fact]
    public void CoverFallback_KeepsAValidLastCandidateInsteadOfTheMonogram()
    {
        var cover = ReadRepoFile("ui", "src", "components", "CoverArt.tsx");
        var fit = ReadRepoFile("ui", "src", "lib", "coverFit.ts");
        var page = ReadRepoFile("ui", "src", "components", "GamePage.tsx");

        Assert.Contains("shouldKeepCoverBitmap", cover, StringComparison.Ordinal);
        Assert.Contains("lastCandidate", cover, StringComparison.Ordinal);
        Assert.Contains("el.complete && el.naturalWidth > 0", cover, StringComparison.Ordinal);
        Assert.Contains("if (options.lastCandidate) return width >= 32 && height >= 32", fit, StringComparison.Ordinal);
        Assert.DoesNotContain("<CoverArt", page, StringComparison.Ordinal);
        Assert.Contains("<HeroWash game={artworkView} />", page, StringComparison.Ordinal);
    }

    [Fact]
    public void UtilityRowAndSearch_StayOnThePageWithoutBoxedFootersOrClippedGlyphs()
    {
        var css = ReadRepoFile("ui", "src", "tokens.css");
        var tools = SliceCss(css, ".exo-game-tools {");
        var row = SliceCss(css, ".exo-utility-row {");
        var search = SliceCss(css, ".exo-titlebar-search .exo-search {");
        var pane = SliceCss(css, ".exo-pane {");

        Assert.Contains("flex-wrap: wrap", tools, StringComparison.Ordinal);
        Assert.DoesNotContain("overflow-x: auto", tools, StringComparison.Ordinal);
        Assert.Contains("flex-wrap: wrap", row, StringComparison.Ordinal);
        Assert.Contains("padding: 0 14px", search, StringComparison.Ordinal);
        Assert.DoesNotContain("padding: 0 14px 0 36px", css, StringComparison.Ordinal);
        Assert.DoesNotContain(".exo-cover-meta {", css, StringComparison.Ordinal);
        Assert.Contains("overflow: hidden", pane, StringComparison.Ordinal);
        Assert.DoesNotContain("overflow: hidden auto", pane, StringComparison.Ordinal);
    }

    [Fact]
    public void TrophyPreview_ReplaysFromEnterEndAndAHoldFallback()
    {
        var banner = ReadRepoFile("ui", "src", "components", "TrophyBanner.tsx");
        var settings = ReadRepoFile("ui", "src", "components", "TrophyNotificationSettings.tsx");

        Assert.Contains("if (name !== 'exo-trophy-enter'", banner, StringComparison.Ordinal);
        Assert.Contains("onAnimationComplete={queueReplay}", settings, StringComparison.Ordinal);
        Assert.Contains("Math.max(1800, (motion?.enterMs ?? 220)", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("'Preview'", settings, StringComparison.Ordinal);
    }

    [Fact]
    public void FriendAvatars_RequestFullSourcesAndNewUploadsHaveAQualityFloor()
    {
        var steam = ReadRepoFile("ExoLauncher", "Adapters", "SteamFriends.cs");
        var localMedia = ReadRepoFile("ExoLauncher", "Services", "ProfileImageStore.cs");
        var onlineMedia = ReadRepoFile("services", "exo-id", "src", "media.ts");

        Assert.Contains("_full.jpg", steam, StringComparison.Ordinal);
        Assert.DoesNotContain("_medium.jpg", steam, StringComparison.Ordinal);
        Assert.Contains("AvatarMinSide = 256", localMedia, StringComparison.Ordinal);
        Assert.Contains("width < 256 || height < 256", onlineMedia, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var index = 0; (index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0; index += value.Length)
            count++;
        return count;
    }

    private static string ReadRepoFile(params string[] relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ExoLauncher.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(new[] { dir!.FullName }.Concat(relative).ToArray()));
    }

    private static string SliceCss(string css, string selector)
    {
        var start = css.IndexOf(selector, StringComparison.Ordinal);
        Assert.True(start >= 0, "missing " + selector);
        var end = css.IndexOf('}', start);
        Assert.True(end > start, "missing end of " + selector);
        return css[start..end];
    }
}
