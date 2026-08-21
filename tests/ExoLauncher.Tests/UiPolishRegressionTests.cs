using Xunit;

namespace ExoLauncher.Tests;

public sealed class UiPolishRegressionTests
{
    [Fact]
    public void PinnedShelf_ResetsRetainedScrollBeforeItCanClipTheFirstCard()
    {
        var app = ReadRepoFile("ui", "src", "components", "LauncherApp.tsx");

        Assert.Contains("const pinnedShelfKey =", app, StringComparison.Ordinal);
        Assert.Contains("useLayoutEffect(() =>", app, StringComparison.Ordinal);
        Assert.Contains("track.scrollTo({ left: 0, behavior: 'auto' })", app, StringComparison.Ordinal);
        Assert.Contains("[libraryPane, pinnedShelfKey, view]", app, StringComparison.Ordinal);
        Assert.Contains("scrollPinned(-1)", app, StringComparison.Ordinal);
        Assert.Contains("scrollPinned(1)", app, StringComparison.Ordinal);
    }

    [Fact]
    public void FriendsArtPreload_NeverParticipatesInPageLayout()
    {
        var friends = ReadRepoFile("ui", "src", "components", "FriendsRoom.tsx");
        var tokens = ReadRepoFile("ui", "src", "tokens.css");
        var preload = SliceBetween(tokens, ".exo-art-preload {", ".exo-friends {");

        Assert.Contains("exo-art-preload exo-friend-art-preload", friends, StringComparison.Ordinal);
        Assert.Contains("position: fixed", preload, StringComparison.Ordinal);
        Assert.Contains("width: 1px", preload, StringComparison.Ordinal);
        Assert.Contains("height: 1px", preload, StringComparison.Ordinal);
        Assert.Contains("opacity: 0", preload, StringComparison.Ordinal);
        Assert.Contains("pointer-events: none", preload, StringComparison.Ordinal);
        Assert.Contains("contain: strict", preload, StringComparison.Ordinal);
    }

    [Fact]
    public void Search_AnimatesARealCapsuleContourWithoutDrawingAWhiteFocusBox()
    {
        var app = ReadRepoFile("ui", "src", "components", "LauncherApp.tsx");
        var tokens = ReadRepoFile("ui", "src", "tokens.css");
        var shell = SliceBetween(tokens, ".exo-titlebar-search {", "/* ── Optional Exo account ── */");
        var capsule = SliceBetween(tokens, ".exo-search-capsule {", ".exo-titlebar-search:focus-within .exo-search-capsule,");
        var input = SliceBetween(tokens, ".exo-titlebar-search .exo-search {", ".exo-titlebar-search:focus-within .exo-search,");

        Assert.Contains("exo-search-capsule", app, StringComparison.Ordinal);
        Assert.Contains("border-radius: 999px", shell, StringComparison.Ordinal);
        Assert.Contains(".exo-search-capsule {", shell, StringComparison.Ordinal);
        Assert.Contains("width: 96px", shell, StringComparison.Ordinal);
        Assert.Contains("width: 100%", capsule, StringComparison.Ordinal);
        Assert.Contains("border: 1px solid rgba(255, 255, 255, 0.12)", capsule, StringComparison.Ordinal);
        Assert.Contains("border-radius: 999px", capsule, StringComparison.Ordinal);
        Assert.Contains("width 200ms var(--ease-in-out)", shell, StringComparison.Ordinal);
        Assert.Contains("width: 184px", shell, StringComparison.Ordinal);
        Assert.Contains("caret-color: transparent", input, StringComparison.Ordinal);
        Assert.Contains("transition: caret-color 0s linear 200ms", input, StringComparison.Ordinal);
        Assert.DoesNotContain("clip-path:", capsule, StringComparison.Ordinal);
        Assert.DoesNotContain("scaleX(", capsule, StringComparison.Ordinal);
        Assert.DoesNotContain("box-shadow", shell, StringComparison.Ordinal);
    }

    [Fact]
    public void BrightGameBanners_UseDirectionalContrastWithoutBoxingEverySection()
    {
        var tokens = ReadRepoFile("ui", "src", "tokens.css");
        var tools = SliceBetween(tokens, ".exo-game-tools {", ".exo-utility-row {");
        var tool = SliceBetween(tokens, ".exo-game-tool {", "@media (hover: hover) and (pointer: fine) {");
        var stats = SliceBetween(tokens, ".exo-game-stats {", ".exo-game-status {");

        Assert.DoesNotContain("background:", tools, StringComparison.Ordinal);
        Assert.DoesNotContain("border:", tools, StringComparison.Ordinal);
        Assert.DoesNotContain("box-shadow:", tools, StringComparison.Ordinal);
        Assert.Contains("background: rgba(7, 7, 7, 0.7)", tool, StringComparison.Ordinal);
        Assert.Contains("color: rgba(255, 255, 255, 0.86)", tool, StringComparison.Ordinal);
        Assert.DoesNotContain("background:", stats, StringComparison.Ordinal);
        Assert.DoesNotContain("border-radius:", stats, StringComparison.Ordinal);
        Assert.Contains("border-top: 1px solid rgba(255, 255, 255, 0.12)", stats, StringComparison.Ordinal);
    }

    [Fact]
    public void GameTitle_OwnsFavoriteAction_AndCloseStaysReadableOnAnyBanner()
    {
        var page = ReadRepoFile("ui", "src", "components", "GamePage.tsx");
        var tokens = ReadRepoFile("ui", "src", "tokens.css");
        var titleRow = SliceBetween(page, "<div className=\"exo-game-title-row\">", "{sources && onSelectSource");
        var favorite = SliceBetween(tokens, ".exo-game-favorite {", ".exo-game-favorite.is-on {");
        var close = SliceBetween(tokens, ".exo-game-close {", ".exo-game-close:focus-visible {");

        Assert.Contains("<h1 className=\"exo-game-title\">", titleRow, StringComparison.Ordinal);
        Assert.Contains("className={`exo-game-favorite", titleRow, StringComparison.Ordinal);
        Assert.DoesNotContain("exo-detail-pin", page + tokens, StringComparison.Ordinal);
        Assert.Contains("position: static", favorite, StringComparison.Ordinal);
        Assert.Contains("flex: 0 0 auto", favorite, StringComparison.Ordinal);
        Assert.Contains("width: 38px", close, StringComparison.Ordinal);
        Assert.Contains("height: 38px", close, StringComparison.Ordinal);
        Assert.Contains("border: 1px solid rgba(255, 255, 255, 0.24)", close, StringComparison.Ordinal);
        Assert.Contains("background: rgba(4, 4, 4, 0.86)", close, StringComparison.Ordinal);
        Assert.Contains("color: #fff", close, StringComparison.Ordinal);
        Assert.Contains("box-shadow: 0 6px 18px rgba(0, 0, 0, 0.42)", close, StringComparison.Ordinal);
        Assert.Contains("<Close size={18} />", page, StringComparison.Ordinal);
    }

    [Fact]
    public void CoverSurfaces_ClipOnceAndOverscanTheBitmapUnderRoundedCorners()
    {
        var tokens = ReadRepoFile("ui", "src", "tokens.css");
        var media = SliceBetween(tokens, ".exo-tile-media {", ".exo-tile-media .exo-cover {");
        var front = SliceBetween(tokens, ".exo-cover-front {", "/* Icons stay letterboxed");

        Assert.Contains("clip-path: inset(0 round calc(var(--exo-tile-radius) + 1px))", media, StringComparison.Ordinal);
        Assert.Contains("inset: -1px", front, StringComparison.Ordinal);
        Assert.Contains("calc(100% + 2px)", front, StringComparison.Ordinal);
        Assert.Contains("clip-path: inset(0 round 11px)", tokens, StringComparison.Ordinal);
        Assert.Contains("clip-path: inset(0 round 12px)", tokens, StringComparison.Ordinal);
    }

    [Fact]
    public void SteamCoverAllowlist_ParsesHttpsHostAndRejectsNonDefaultPorts()
    {
        var cover = ReadRepoFile("ui", "src", "components", "CoverArt.tsx");
        var portrait = SliceBetween(cover, "function isOfficialSteamPortraitCdn", "function isOfficialSteamHeroCdn");
        var hero = SliceBetween(cover, "function isOfficialSteamHeroCdn", "export function isSafeCoverUrl");
        var hostCheck = SliceBetween(cover, "function httpsHostIs", "function isOfficialEpicPortraitCdn");

        Assert.DoesNotContain("url.includes('steamstatic.com/')", portrait + hero, StringComparison.Ordinal);
        Assert.DoesNotContain("url.includes('steamcdn-a.akamaihd.net/')", portrait + hero, StringComparison.Ordinal);
        Assert.Contains("httpsHostIs(url, 'steamstatic.com')", portrait + hero, StringComparison.Ordinal);
        Assert.Contains("httpsHostIs(url, 'steamcdn-a.akamaihd.net')", portrait + hero, StringComparison.Ordinal);
        Assert.Contains("const parsed = new URL(url)", hostCheck, StringComparison.Ordinal);
        Assert.Contains("parsed.protocol === 'https:'", hostCheck, StringComparison.Ordinal);
        Assert.Contains("parsed.port === '' || parsed.port === '443'", hostCheck, StringComparison.Ordinal);
        Assert.Contains("parsed.hostname.endsWith(`.${host}`)", hostCheck, StringComparison.Ordinal);
    }

    [Fact]
    public void ProfileBadges_AreServerProvidedAndUpscalerStatusUsesCompactColorSignals()
    {
        var profile = ReadRepoFile("ui", "src", "components", "ProfileRoom.tsx");
        var upscalers = ReadRepoFile("ui", "src", "components", "UpscalerFiles.tsx");

        Assert.Contains("profileBadges(profile)", profile, StringComparison.Ordinal);
        Assert.Contains("exo-profile-badges", profile, StringComparison.Ordinal);
        Assert.DoesNotContain("Erix", profile, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("upscalerVisualState(row)", upscalers, StringComparison.Ordinal);
        Assert.Contains("exo-upscaler-signal", upscalers, StringComparison.Ordinal);
        Assert.DoesNotContain("visibleNoteText", upscalers, StringComparison.Ordinal);
    }

    [Fact]
    public void InstalledScreenshotShapes_KeepSevenPinnedCardsCleanAndUpscalerRetryUsable()
    {
        var card = ReadRepoFile("ui", "src", "components", "GameCard.tsx");
        var upscalers = ReadRepoFile("ui", "src", "components", "UpscalerFiles.tsx");
        var tokens = ReadRepoFile("ui", "src", "tokens.css");
        var pinned = SliceBetween(tokens, ".exo-pin-row {", ".exo-pin-track .exo-tile,");
        var track = SliceBetween(tokens, ".exo-pin-track {", ".exo-pinned-section");
        var media = SliceBetween(tokens, ".exo-tile-media {", ".exo-tile-media .exo-cover {");
        var meta = SliceBetween(tokens, ".exo-card-meta {", ".exo-card-title {");
        var title = SliceBetween(tokens, ".exo-card-title {", ".exo-card-title.is-long {");
        var run = SliceBetween(tokens, ".exo-upscaler-count,", ".exo-upscaler-actions {");

        Assert.Contains("/ 7", pinned, StringComparison.Ordinal);
        Assert.Contains("- 72px", pinned, StringComparison.Ordinal);
        Assert.Contains("column-gap: 12px", track, StringComparison.Ordinal);
        Assert.Contains("inset: -1px", media, StringComparison.Ordinal);
        Assert.Contains("calc(var(--exo-tile-radius) + 1px)", media, StringComparison.Ordinal);
        Assert.DoesNotContain("exo-tile-pin", card + tokens, StringComparison.Ordinal);
        Assert.DoesNotContain("StarFilled size={14}", card, StringComparison.Ordinal);
        Assert.Contains("padding: 8px 2px 4px", meta, StringComparison.Ordinal);
        Assert.Contains("line-height: 1.25", title, StringComparison.Ordinal);
        Assert.Contains("action: GroupAction; text: string", upscalers, StringComparison.Ordinal);
        Assert.Contains("retry={run.kind === 'failed'", upscalers, StringComparison.Ordinal);
        Assert.Contains("if (visibleRows.length === 0 && run.kind === 'idle') return null", upscalers, StringComparison.Ordinal);
        Assert.Contains("overflow-wrap: anywhere", run, StringComparison.Ordinal);
    }

    [Fact]
    public void ProfileStudioHeader_IsOpenAndUnruledWhileKeepingAutosaveGuidance()
    {
        var profile = ReadRepoFile("ui", "src", "components", "ProfileRoom.tsx");
        var tokens = ReadRepoFile("ui", "src", "tokens.css");
        var form = SliceBetween(tokens, ".exo-profile-form {", ".exo-profile-form::-webkit-scrollbar");
        var head = SliceBetween(tokens, ".exo-profile-form-head {", ".exo-profile-form-head h3 {");

        Assert.Contains("Auto-saved. Drag sections and showcase cards; eye icons hide them.", profile, StringComparison.Ordinal);
        Assert.Contains("gap: 18px 30px", form, StringComparison.Ordinal);
        Assert.Contains("padding: 8px 0 6px", head, StringComparison.Ordinal);
        Assert.DoesNotContain("border-bottom", head, StringComparison.Ordinal);
        Assert.DoesNotContain(".exo-profile-form-head::before", tokens, StringComparison.Ordinal);
        Assert.DoesNotContain(".exo-profile-form-head::after", tokens, StringComparison.Ordinal);
    }

    [Fact]
    public void ProfileAchievementCache_IsScopedToAccountAndLinkedStoreIdentity()
    {
        var profile = ReadRepoFile("ui", "src", "components", "ProfileRoom.tsx");

        Assert.Contains("accountAchievementScope(account)", profile, StringComparison.Ordinal);
        Assert.Contains("linkedStoreAchievementScope(next)", profile, StringComparison.Ordinal);
        Assert.Contains("onHostEvent('account.updated'", profile, StringComparison.Ordinal);
        Assert.Contains("onHostEvent('profile.updated', apply)", profile, StringComparison.Ordinal);
        Assert.Contains("setAchievementByGame(new Map())", profile, StringComparison.Ordinal);
        Assert.Contains("achievementScopeRevision", profile, StringComparison.Ordinal);
    }

    [Fact]
    public void PresenceFailureAfterSuccess_DowngradesRosterWithoutInventingOfflineOrActivity()
    {
        var friends = ReadRepoFile("ui", "src", "components", "FriendsRoom.tsx");
        var presence = ReadRepoFile("ui", "src", "lib", "presence.ts");
        var bridge = ReadRepoFile("ExoLauncher", "Services", "WebHostBridge.cs");

        Assert.Contains("roster.unavailable && rows.length === 0", friends, StringComparison.Ordinal);
        Assert.Contains("setPresence(projectPresenceRoster(rows))", friends, StringComparison.Ordinal);
        Assert.Contains("setPresence((current) => downgradePresenceRoster(current))", friends, StringComparison.Ordinal);
        Assert.Contains("setPresence((current) => applyPresenceEvent(current, event))", friends, StringComparison.Ordinal);
        Assert.Contains("event.scope === 'roster' || event.kind === 'transportError'", presence, StringComparison.Ordinal);
        Assert.Contains("status: 'unknown'", presence, StringComparison.Ordinal);
        Assert.Contains("gameId: null", presence, StringComparison.Ordinal);
        Assert.Contains("gameTitle: null", presence, StringComparison.Ordinal);
        Assert.Contains("available: false", presence, StringComparison.Ordinal);
        Assert.DoesNotContain("status: 'offline'", presence, StringComparison.Ordinal);
        Assert.Contains("message.Kind == ExoPresenceMessageKind.TransportError", bridge, StringComparison.Ordinal);
        Assert.Contains("? \"roster\"", bridge, StringComparison.Ordinal);
        Assert.DoesNotContain("exo-presence-dot", friends, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ExoLauncher.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string ReadRepoFile(params string[] relative)
    {
        return File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(relative).ToArray()));
    }

    private static string SliceBetween(string haystack, string start, string end)
    {
        var from = haystack.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, "missing " + start);
        var to = haystack.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to > from, "missing " + end);
        return haystack[from..to];
    }
}
