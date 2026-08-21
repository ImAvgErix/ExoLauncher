using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// Speed regressions are invisible in a screenshot, so the expensive choices are
/// pinned here: no WebGL runtime in the bundle, no store probe per page open, and
/// no vendor CLI spawn inside a library scan.
/// </summary>
public sealed class StartupCostContractTests
{
    [Fact]
    public void Ui_ShipsNoWebglRuntime()
    {
        var pkg = ReadRepoFile("ui", "package.json");

        // three.js was 491kB of download and parse plus a shader that held the
        // GPU for the whole session, for two radial gradients' worth of effect.
        Assert.DoesNotContain("\"three\"", pkg, StringComparison.Ordinal);
        Assert.DoesNotContain("@react-three/fiber", pkg, StringComparison.Ordinal);

        var ambient = ReadRepoFile("ui", "src", "components", "AppAmbient.tsx");
        Assert.Contains("exo-ambient", ambient, StringComparison.Ordinal);
        Assert.DoesNotContain("lazy(", ambient, StringComparison.Ordinal);
        Assert.DoesNotContain("Canvas", ambient, StringComparison.Ordinal);

        var tokens = ReadRepoFile("ui", "src", "tokens.css");
        var ambientStart = tokens.IndexOf(".exo-ambient {", StringComparison.Ordinal);
        var ambientEnd = tokens.IndexOf(".exo-titlebar-home", ambientStart, StringComparison.Ordinal);
        Assert.True(ambientStart >= 0 && ambientEnd > ambientStart);
        var ambientCss = tokens[ambientStart..ambientEnd];
        Assert.DoesNotContain("61, 214, 140", ambientCss, StringComparison.Ordinal);

        Assert.False(
            Directory.Exists(Path.Combine(RepoRoot(), "ui", "src", "components", "react-bits")) &&
            File.Exists(Path.Combine(RepoRoot(), "ui", "src", "components", "react-bits", "silk-waves.tsx")),
            "the WebGL silk component should be gone");
    }

    [Fact]
    public void StoreMatrix_IsCachedBetweenScans()
    {
        var library = ReadRepoFile("ExoLauncher", "Services", "LibraryService.cs");

        // library.get, profile.get and stores.matrix all call this, and each call
        // hit the registry and Steam's localconfig.
        Assert.Contains("StoreMatrixTtl", library, StringComparison.Ordinal);
        Assert.Contains("private IReadOnlyList<StoreBackendStatus>? _storeMatrix", library, StringComparison.Ordinal);
        Assert.Contains("InvalidateStoreMatrix()", library, StringComparison.Ordinal);
        Assert.Contains("BuildStoreMatrix()", library, StringComparison.Ordinal);
        Assert.Contains("PeekStoreMatrix()", library, StringComparison.Ordinal);
        // Cold boot used to run this probe three times in parallel.
        Assert.Contains("var fresh = BuildStoreMatrix();", library, StringComparison.Ordinal);
    }

    [Fact]
    public void EpicScan_DoesNotSpawnTheCliInline()
    {
        var epic = ReadRepoFile("ExoLauncher", "Adapters", "EpicAdapter.cs");

        var start = epic.IndexOf("public async Task<IReadOnlyList<GameEntry>> GetLibraryAsync", StringComparison.Ordinal);
        Assert.True(start > 0);
        var end = epic.IndexOf(
            "internal static IReadOnlyList<LegendaryCli.GameRow> CachedLegendaryInstalledRows",
            start,
            StringComparison.Ordinal);
        Assert.True(end > start);
        var scan = epic[start..end];

        // The scan reads disk and uses the last CLI answer. Spawning legendary
        // here cost about a second of every refresh.
        Assert.DoesNotContain("await TryListLegendaryInstalledAsync", scan, StringComparison.Ordinal);
        Assert.Contains("CachedLegendaryInstalledRows()", scan, StringComparison.Ordinal);
        Assert.Contains("ScheduleInstalledRowsRefresh(legendary)", scan, StringComparison.Ordinal);
    }

    [Fact]
    public void Overlay_DoesNotFadeTheBlurredLayer()
    {
        var motion = ReadRepoFile("ui", "src", "motion.tsx");
        var tokens = ReadRepoFile("ui", "src", "tokens.css");
        var app = ReadRepoFile("ui", "src", "components", "LauncherApp.tsx");
        var grid = ReadRepoFile("ui", "src", "components", "WindowedGameGrid.tsx");

        var start = motion.IndexOf("export function GameOverlay(", StringComparison.Ordinal);
        Assert.True(start > 0);
        var body = motion[start..];

        // Only the card animates. Animating the ancestor of a backdrop-filter
        // makes the compositor rebuild the blur every frame.
        Assert.Contains("className=\"exo-game-overlay-stage\"", body, StringComparison.Ordinal);
        Assert.Contains("querySelector<HTMLElement>('.exo-game-overlay-stage')", body, StringComparison.Ordinal);
        Assert.Contains("button:not(:disabled):not([tabindex=\"-1\"])", motion, StringComparison.Ordinal);
        Assert.Contains("tabIndex={-1}", app, StringComparison.Ordinal);
        Assert.DoesNotContain("exit={{ opacity: 0, pointerEvents: 'none' }}", body, StringComparison.Ordinal);
        Assert.Contains("duration: 0.16", body, StringComparison.Ordinal);
        Assert.Contains("instant?: boolean", body, StringComparison.Ordinal);
        Assert.Contains("if (reduce || instant)", body, StringComparison.Ordinal);
        Assert.Contains("instant={overlayMotion === 'instant'}", app, StringComparison.Ordinal);
        Assert.DoesNotContain("openGamePage(focused.id", app, StringComparison.Ordinal);
        Assert.Contains("onKeyDown={onGridKeyDown}", grid, StringComparison.Ordinal);

        var scrim = tokens.IndexOf(".exo-game-overlay-scrim", StringComparison.Ordinal);
        Assert.True(scrim > 0);
        Assert.Contains("backdrop-filter", tokens[scrim..(scrim + 400)], StringComparison.Ordinal);

        var washLayer = tokens.IndexOf(".exo-game-page-wash {", StringComparison.Ordinal);
        Assert.True(washLayer > 0);
        var washLayerBlock = tokens[washLayer..(washLayer + 220)];
        Assert.Contains("position: absolute", washLayerBlock, StringComparison.Ordinal);
        Assert.Contains("pointer-events: none", washLayerBlock, StringComparison.Ordinal);

        var wash = tokens.IndexOf(".exo-game-page-wash .exo-now-wash-img {", StringComparison.Ordinal);
        Assert.True(wash > 0);
        var washBlock = tokens[wash..(wash + 420)];
        Assert.Contains("transition: opacity 80ms", washBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("transition: opacity 280ms", washBlock, StringComparison.Ordinal);
        Assert.Contains(".exo-game-page-wash .exo-now-wash-img.is-on", tokens, StringComparison.Ordinal);
    }

    [Fact]
    public void LibraryGet_ReturnsLastGoodWithoutWaitingOnAScan()
    {
        var library = ReadRepoFile("ExoLauncher", "Services", "LibraryService.cs");
        Assert.Contains("ScheduleBackgroundScan()", library, StringComparison.Ordinal);
        Assert.Contains("private void ScheduleBackgroundScan()", library, StringComparison.Ordinal);
        Assert.Contains("if (stale) ScheduleBackgroundScan()", library, StringComparison.Ordinal);
        Assert.Contains("HaveAccountScopesChanged()", library, StringComparison.Ordinal);
        var bridge = ReadRepoFile("ExoLauncher", "Services", "WebHostBridge.cs");
        Assert.Contains("PeekStoreMatrix()", bridge, StringComparison.Ordinal);
    }

    [Fact]
    public void Titlebar_DoesNotRefetchProfileOnEveryCoverWarm()
    {
        var app = ReadRepoFile("ui", "src", "components", "LauncherApp.tsx");
        Assert.Contains("writeCache(CACHE_KEYS.profile, self)", app, StringComparison.Ordinal);
        Assert.Contains("writeCache(CACHE_KEYS.library, res.games)", app, StringComparison.Ordinal);
        Assert.Contains("lastProfileRef.current = self", app, StringComparison.Ordinal);
        Assert.Contains("if (cached) applyIdentity(cached)", app, StringComparison.Ordinal);
        Assert.Contains("hidden={view !== 'library'}", app, StringComparison.Ordinal);
        Assert.Contains("hidden={view !== 'settings'}", app, StringComparison.Ordinal);
        Assert.DoesNotContain("className=\"exo-art-preload\"", app, StringComparison.Ordinal);
        Assert.Contains("<ProfileRoom games={games} active={view === 'profile'} />", app, StringComparison.Ordinal);
        Assert.Contains("preloadInitialCoverArt(ordered, 10)", app, StringComparison.Ordinal);
        Assert.Contains("<FriendsRoom active={view === 'friends'} />", app, StringComparison.Ordinal);
        Assert.DoesNotContain("onHostEvent('library.updated', load)", app, StringComparison.Ordinal);
        var markStart = app.IndexOf("const markBusy =", StringComparison.Ordinal);
        var markEnd = app.IndexOf("const emptyLibrary", markStart, StringComparison.Ordinal);
        Assert.True(markStart >= 0 && markEnd > markStart);
        Assert.DoesNotContain("catalogSearching", app[markStart..markEnd], StringComparison.Ordinal);
        Assert.Contains("const MIN_BOOT_SPLASH_MS = 120", app, StringComparison.Ordinal);
        Assert.Contains("preloadInitialCoverArt", app, StringComparison.Ordinal);
        Assert.Contains("preloadUpscalerStatuses", app, StringComparison.Ordinal);
        var upscalerCache = ReadRepoFile("ui", "src", "lib", "upscalerCache.ts");
        Assert.Contains("!isAntiCheatTitle(game)", upscalerCache, StringComparison.Ordinal);
        Assert.Contains("setBooting(false)", app, StringComparison.Ordinal);
        var libraryLoad = app[app.IndexOf("const loadLibrary", StringComparison.Ordinal)..app.IndexOf("const loadSettings", StringComparison.Ordinal)];
        Assert.DoesNotContain("setBooting(false)", libraryLoad, StringComparison.Ordinal);
        Assert.DoesNotContain("Math.max(0, 320 - (Date.now() - bootAt.current))", app, StringComparison.Ordinal);
        Assert.DoesNotContain("Math.max(0, 1400 - (Date.now() - bootAt.current))", app, StringComparison.Ordinal);
        Assert.Contains("const updateTimer = window.setTimeout", app, StringComparison.Ordinal);
        Assert.Contains("window.clearTimeout(updateTimer)", app, StringComparison.Ordinal);
    }

    [Fact]
    public void HostReads_CoalesceInflightDuplicates()
    {
        var host = ReadRepoFile("ui", "src", "lib", "host.ts");
        Assert.Contains("COALESCE_READS", host, StringComparison.Ordinal);
        Assert.Contains("'profile.get'", host, StringComparison.Ordinal);
        Assert.Contains("'library.get'", host, StringComparison.Ordinal);
        Assert.Contains("inflightReads", host, StringComparison.Ordinal);
    }

    [Fact]
    public void LibraryUpdated_IsThrottledOnTheBridge()
    {
        var bridge = ReadRepoFile("ExoLauncher", "Services", "WebHostBridge.cs");
        Assert.Contains("_libraryPushScheduled", bridge, StringComparison.Ordinal);
        Assert.Contains("one push per 80 ms", bridge, StringComparison.Ordinal);
        var start = bridge.IndexOf("private void OnLibraryUpdated()", StringComparison.Ordinal);
        var end = bridge.IndexOf("private void OnMessage(", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var publication = bridge[start..end];
        Assert.Contains("PeekCachedLibrary()", publication, StringComparison.Ordinal);
        Assert.DoesNotContain("RefreshCovers()", publication, StringComparison.Ordinal);

        var library = ReadRepoFile("ExoLauncher", "Services", "LibraryService.cs");
        var diskStart = library.IndexOf("if (disk is { Count: > 0 })", StringComparison.Ordinal);
        var diskEnd = library.IndexOf("return OverlayUserPrefs(_cache)", diskStart, StringComparison.Ordinal);
        Assert.True(diskStart >= 0 && diskEnd > diskStart);
        Assert.Contains("RefreshCovers()", library[diskStart..diskEnd], StringComparison.Ordinal);
    }

    [Fact]
    public void LibraryScan_QueuesWholeLibraryCoverWarmAtBackgroundPriority()
    {
        var library = ReadRepoFile("ExoLauncher", "Services", "LibraryService.cs");
        var start = library.IndexOf("var warmTargets", StringComparison.Ordinal);
        var end = library.IndexOf("_cacheAt = DateTimeOffset.UtcNow", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var warm = library[start..end];

        Assert.Contains("_ = CoverArtService.WarmCacheAsync", warm, StringComparison.Ordinal);
        Assert.Contains("requested: false", warm, StringComparison.Ordinal);
        Assert.DoesNotContain("requested: true", warm, StringComparison.Ordinal);
        Assert.DoesNotContain("Task.WhenAny", warm, StringComparison.Ordinal);
        Assert.DoesNotContain("TimeSpan.FromSeconds(20)", warm, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaytimeEnrichment_ReusesTheParsedSteamSnapshot()
    {
        var playtime = ReadRepoFile("ExoLauncher", "Services", "PlaytimeService.cs");
        var start = playtime.IndexOf(
            "public static IReadOnlyList<GameEntry> Enrich",
            StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = playtime.IndexOf("private static", start, StringComparison.Ordinal);
        Assert.True(end > start);

        // Session completion already invalidates Steam's VDF snapshot. Invalidating
        // again for every library enrichment discarded the parse cache and made
        // unrelated profile/social refreshes reread localconfig.vdf.
        Assert.DoesNotContain(
            "SteamPlaytime.Invalidate()",
            playtime[start..end],
            StringComparison.Ordinal);

        var orchestrator = ReadRepoFile("ExoLauncher", "Services", "LaunchOrchestrator.cs");
        Assert.Contains("SteamPlaytime.Invalidate()", orchestrator, StringComparison.Ordinal);
    }

    [Fact]
    public void CoverVirtualHost_DoesNotAlsoInterceptEveryMappedPoster()
    {
        var window = ReadRepoFile("ExoLauncher", "MainWindow.xaml.cs");
        var start = window.IndexOf("var coverVirtualHostMapped = false", StringComparison.Ordinal);
        var end = window.IndexOf("_bridge = new WebHostBridge", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var setup = window[start..end];

        Assert.Contains("coverVirtualHostMapped = true", setup, StringComparison.Ordinal);
        Assert.Contains("if (!coverVirtualHostMapped)", setup, StringComparison.Ordinal);
        Assert.Contains("core.WebResourceRequested += CoverResourceRequested", setup, StringComparison.Ordinal);
        Assert.True(
            setup.IndexOf("if (!coverVirtualHostMapped)", StringComparison.Ordinal) <
            setup.IndexOf("core.AddWebResourceRequestedFilter", StringComparison.Ordinal));
    }

    [Fact]
    public void StoreObservers_StartAfterTheMainShellPaints()
    {
        var services = ReadRepoFile("ExoLauncher", "Services", "AppServices.cs");
        var window = ReadRepoFile("ExoLauncher", "MainWindow.xaml.cs");
        var initializeStart = services.IndexOf("public void Initialize()", StringComparison.Ordinal);
        var deferredStart = services.IndexOf("public void StartDeferredServices()", StringComparison.Ordinal);
        Assert.True(initializeStart >= 0 && deferredStart > initializeStart);
        var initialize = services[initializeStart..deferredStart];

        Assert.DoesNotContain("Library.StartWatchers()", initialize, StringComparison.Ordinal);
        Assert.DoesNotContain("HiddenStores.Start()", initialize, StringComparison.Ordinal);
        Assert.Contains("Library.StartWatchers()", services[deferredStart..], StringComparison.Ordinal);
        Assert.Contains("HiddenStores.Start()", services[deferredStart..], StringComparison.Ordinal);
        Assert.Contains("App.Services.StartDeferredServices();", window, StringComparison.Ordinal);
        Assert.True(
            window.IndexOf("LogStartupMilestone(\"webview-visible\")", StringComparison.Ordinal) <
            window.IndexOf("App.Services.StartDeferredServices();", StringComparison.Ordinal));
    }

    [Fact]
    public void SuccessfulStoreAuth_InvalidatesTheCachedStoreMatrix()
    {
        var bridge = ReadRepoFile("ExoLauncher", "Services", "WebHostBridge.cs");
        var start = bridge.IndexOf("private async Task<object> StoresAuthAsync", StringComparison.Ordinal);
        var end = bridge.IndexOf("private async Task<object> FriendsListAsync", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var auth = bridge[start..end];

        var authenticated = auth.IndexOf("AuthenticateAsync", StringComparison.Ordinal);
        var invalidated = auth.IndexOf("InvalidateStoreMatrix()", StringComparison.Ordinal);
        var returned = auth.IndexOf("return new", invalidated, StringComparison.Ordinal);
        Assert.True(authenticated >= 0 && invalidated > authenticated && returned > invalidated);
        Assert.Contains("if (result.Ok)", auth, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsSet_RejectsWhenSteamWebApiKeySaveFails()
    {
        var bridge = ReadRepoFile("ExoLauncher", "Services", "WebHostBridge.cs");
        var start = bridge.IndexOf("private object SetSettings", StringComparison.Ordinal);
        var end = bridge.IndexOf("private object AchievementGet", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var settings = bridge[start..end];

        Assert.Contains("!SteamWebApiKeyStore.Save(", settings, StringComparison.Ordinal);
        Assert.Contains(
            "throw new InvalidOperationException(\"Steam Web API key was not saved.\")",
            settings,
            StringComparison.Ordinal);
        Assert.DoesNotContain("AppLog.", settings, StringComparison.Ordinal);
        var rejected = settings.IndexOf("throw new InvalidOperationException", StringComparison.Ordinal);
        var rejectionEnd = settings.IndexOf(";", rejected, StringComparison.Ordinal);
        Assert.True(rejected >= 0 && rejectionEnd > rejected);
        Assert.DoesNotContain("steamKey", settings[rejected..rejectionEnd], StringComparison.Ordinal);
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
