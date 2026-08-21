using Xunit;

namespace ExoLauncher.Tests;

public sealed class UxRegressionContractTests
{
    [Fact]
    public void LegacyProfileOverlayAndExoRpcNames_StayRemoved()
    {
        var settings = Path.Combine(RepoRoot(), "ui", "src", "components", "SettingsPanel.tsx");
        var settingsText = File.ReadAllText(settings);
        var window = ReadRepoFile("ExoLauncher", "MainWindow.xaml.cs");
        var shell = ReadRepoFile("ExoLauncher", "Services", "WebHostBridge.cs");
        var all = window + settingsText + shell;

        Assert.DoesNotContain("ProfileOverlay", all, StringComparison.Ordinal);
        Assert.DoesNotContain("ProfileButton", all, StringComparison.Ordinal);
        Assert.DoesNotContain("Exo Profile", all, StringComparison.Ordinal);
        Assert.DoesNotContain("exo.signIn", all, StringComparison.Ordinal);
        Assert.DoesNotContain("exo.signOut", all, StringComparison.Ordinal);
        Assert.DoesNotContain("exo.updateProfile", all, StringComparison.Ordinal);
    }

    [Fact]
    public void LibraryScan_RecordsReleaseSafePerAdapterTimings()
    {
        var library = ReadRepoFile("ExoLauncher", "Services", "LibraryService.cs");

        Assert.Contains("PERF library-scan totalMs=", library, StringComparison.Ordinal);
        Assert.Contains("ElapsedMilliseconds", library, StringComparison.Ordinal);
        Assert.Contains("result.Id", library, StringComparison.Ordinal);
        Assert.Contains("result.Items.Count", library, StringComparison.Ordinal);
        Assert.Contains("AppLog.Info", library, StringComparison.Ordinal);
    }

    [Fact]
    public void StartupDefersNonessentialCoverNetworkWork_AndRecordsMilestones()
    {
        var library = ReadRepoFile("ExoLauncher", "Services", "LibraryService.cs");
        var covers = ReadRepoFile("ExoLauncher", "Services", "CoverArtService.cs");
        var window = ReadRepoFile("ExoLauncher", "MainWindow.xaml.cs");

        Assert.Contains("requested: false, deferForFirstPaint: true", library, StringComparison.Ordinal);
        Assert.Contains(".Where(CoverArtService.ShouldWarmLibraryCover)", library, StringComparison.Ordinal);
        Assert.DoesNotContain("Task.WhenAny", library, StringComparison.Ordinal);
        Assert.DoesNotContain("Task.Delay(TimeSpan.FromSeconds(20))", library, StringComparison.Ordinal);
        Assert.Contains("FirstPaintCoverWarmDelay = TimeSpan.FromMilliseconds(50)", covers, StringComparison.Ordinal);
        Assert.Contains("private static readonly HttpClient CoverHttp", covers, StringComparison.Ordinal);
        Assert.Contains("BackgroundWarmConcurrency = 4", covers, StringComparison.Ordinal);
        Assert.Contains("SearchWarmConcurrency = 4", covers, StringComparison.Ordinal);
        Assert.Contains("ArtworkWarmIntent.SearchPortrait => SearchWarmConcurrency", covers, StringComparison.Ordinal);
        Assert.Contains("PERF startup milestone=", window, StringComparison.Ordinal);
        Assert.Contains("window-constructed", window, StringComparison.Ordinal);
        Assert.Contains("webview-core-ready", window, StringComparison.Ordinal);
        Assert.Contains("EnsureCoreWebView2Async", window, StringComparison.Ordinal);
        Assert.Contains("LogStartupMilestone(\"bridge-attached\")", window, StringComparison.Ordinal);
    }

    [Fact]
    public void CoverWarm_ResolvesSteamAppIdByTitle_ForEveryNonSteamStore()
    {
        var covers = ReadRepoFile("ExoLauncher", "Services", "CoverArtService.cs");
        var library = ReadRepoFile("ExoLauncher", "Services", "LibraryService.cs");

        Assert.Contains("await ResolveSteamAppIdByTitleAsync", covers, StringComparison.Ordinal);
        Assert.Contains("ShouldWarmLibraryCover", library, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "game.Installed || game.IsFavorite",
            library,
            StringComparison.Ordinal);
        Assert.Contains("ProvisionalStorePosterUrl", covers, StringComparison.Ordinal);
        Assert.Contains("TryImportSteamLibraryCachePoster", covers, StringComparison.Ordinal);
        Assert.Contains("TryImportHashedSteamPortrait", covers, StringComparison.Ordinal);
        Assert.Contains("Variants = g.Variants", covers, StringComparison.Ordinal);
        Assert.Contains("RefreshCovers();", library, StringComparison.Ordinal);
        Assert.Contains("GogCoverCandidateUrls", covers, StringComparison.Ordinal);
        Assert.DoesNotContain("{gogId}_product_tile_256_2x.jpg", covers, StringComparison.Ordinal);
        Assert.Contains("public static Uri? TryImageUri", covers, StringComparison.Ordinal);
        Assert.Contains("public static IReadOnlyList<string> SteamHeroUrls", covers, StringComparison.Ordinal);
    }

    [Fact]
    public void GogAuth_UsesTrustedWebCallback_WithoutPersistentSettingsUi()
    {
        var service = ReadRepoFile("ExoLauncher", "Services", "GogAuthService.cs");
        var helper = ReadRepoFile("ExoLauncher", "Adapters", "Cli", "GogdlCli.cs");
        var adapter = ReadRepoFile("ExoLauncher", "Adapters", "GogAdapter.cs");
        var settings = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "src", "components", "SettingsPanel.tsx"));

        Assert.Contains("CoreWebView2Environment.CreateWithOptionsAsync", service, StringComparison.Ordinal);
        Assert.Contains("NavigationStarting", service, StringComparison.Ordinal);
        Assert.Contains("Path.Combine(PathHelper.AppDataDir, \"gog-webview\")", service, StringComparison.Ordinal);
        Assert.Contains("AuthConfigPath + \".pending-\"", service, StringComparison.Ordinal);
        Assert.Contains("File.Move(pendingPath, AuthConfigPath, overwrite: true)", service, StringComparison.Ordinal);
        Assert.Contains("origin", helper, StringComparison.Ordinal);
        Assert.Contains("embed.gog.com", helper, StringComparison.Ordinal);
        Assert.Contains("GogdlCli.WithAuthConfig", adapter, StringComparison.Ordinal);
        Assert.DoesNotContain("StartAuthConsole(gogdl", adapter, StringComparison.Ordinal);
        Assert.DoesNotContain("StoresAuthAsync", settings, StringComparison.Ordinal);
    }

    [Fact]
    public void StoreOpen_IsAsyncAndKeepsRevealingColdClients()
    {
        var text = ReadRepoFile("ExoLauncher", "Services", "WebHostBridge.cs");
        Assert.Contains("private async Task<object> ShowStoreAsync", text, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromSeconds(15)", text, StringComparison.Ordinal);
        Assert.Contains("using var started = Process.Start", text, StringComparison.Ordinal);
        Assert.Contains("SteamProtocol.OpenMainUri()", text, StringComparison.Ordinal);
        Assert.Contains("StoreClientCleanup.HideUnused(kind)", text, StringComparison.Ordinal);
        Assert.Contains("StoreClientCleanup.ExitUnusedAsync(kind)", text, StringComparison.Ordinal);
        Assert.Contains("StoreClientCleanup.ExitUnusedAsync(StoreKind.Steam)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("processNames.Any(ProcessHelper.IsProcessRunning)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("-nofriendsui", text, StringComparison.Ordinal);
    }

    [Fact]
    public void SearchCoverWarmKeysReleaseOnlyAfterTheWholeWarmTaskSettles()
    {
        var bridge = ReadRepoFile("ExoLauncher", "Services", "WebHostBridge.cs");

        Assert.Contains("ReleaseSearchCoverWarmKeysAsync(warm, warmKeys)", bridge, StringComparison.Ordinal);
        Assert.Contains("await warm.ConfigureAwait(false)", bridge, StringComparison.Ordinal);
        Assert.Contains("finally", bridge, StringComparison.Ordinal);
        Assert.DoesNotContain("onBatchDone: () =>\n        {\n            lock (_searchCoverWarmGate)", bridge, StringComparison.Ordinal);
    }

    [Fact]
    public void OwnedTitles_NeverRenderAPurchaseAction()
    {
        var format = ReadRepoFile("ExoLauncher", "Ui", "UiFormat.cs");
        var storefront = ReadRepoFile("ExoLauncher", "Adapters", "Storefront.cs");
        var app = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "src", "components", "LauncherApp.tsx"));
        var host = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "src", "lib", "host.ts"));
        var entitlement = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "src", "lib", "entitlementActions.ts"));
        var bridge = ReadRepoFile("ExoLauncher", "Services", "WebHostBridge.cs");

        Assert.Contains("Storefront.BuyUrl(game)", format, StringComparison.Ordinal);
        Assert.Contains("if (game.EntitlementState == EntitlementState.NotOwned) return Destination(game);", storefront, StringComparison.Ordinal);
        Assert.Contains("if (game.EntitlementState == EntitlementState.Unverified) return null;", storefront, StringComparison.Ordinal);
        Assert.Contains("if (game.Installed || game.Owned) return null;", storefront, StringComparison.Ordinal);
        Assert.Contains("smartSearchScore(game.title, q)", app, StringComparison.Ordinal);
        Assert.DoesNotContain("smartSearchScore(game.store", app, StringComparison.Ordinal);
        Assert.Contains("return resolveEntitlementPrimaryAction(game)", host, StringComparison.Ordinal);
        Assert.Contains("if (game.canInstall && game.owned === true) return 'install'", entitlement, StringComparison.Ordinal);
        Assert.Contains("if (game.entitlementState === 'notOwned') return 'none'", entitlement, StringComparison.Ordinal);
        Assert.Contains("if (game.entitlementState === 'unverified' && !game.installed) return 'none'", entitlement, StringComparison.Ordinal);
        Assert.DoesNotContain("if (game.canInstall || game.owned) return 'install'", entitlement, StringComparison.Ordinal);
        Assert.Contains("if (!proven.Owned) return null", bridge, StringComparison.Ordinal);
        Assert.Contains("if (!game.Owned)", bridge, StringComparison.Ordinal);
        Assert.DoesNotContain("game.Owned || game.CanInstall || game.Installed", bridge, StringComparison.Ordinal);
        Assert.Contains("TitleIdentity", ReadRepoFile("ExoLauncher", "Services", "StoreSearchService.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void GameplayUsesNotificationArea_NotTaskbarMinimize()
    {
        var bridge = ReadRepoFile("ExoLauncher", "Services", "WebHostBridge.cs");
        var window = ReadRepoFile("ExoLauncher", "MainWindow.xaml.cs");
        Assert.Contains("HideForGameplay", bridge, StringComparison.Ordinal);
        Assert.Contains("Task.WhenAny(launchTask, Task.Delay(450))", bridge, StringComparison.Ordinal);
        Assert.Contains("RestoreAndActivate", bridge, StringComparison.Ordinal);
        Assert.Contains("NotificationAreaIcon", window, StringComparison.Ordinal);
        Assert.Contains("IsShownInSwitchers = false", window, StringComparison.Ordinal);
    }

    [Fact]
    public void ShellMinimize_UsesNotificationArea_NotTaskbarMinimize()
    {
        var bridge = ReadRepoFile("ExoLauncher", "Services", "WebHostBridge.cs");
        var icon = ReadRepoFile("ExoLauncher", "Services", "NotificationAreaIcon.cs");
        Assert.Contains("\"shell.minimize\" => HideToNotificationArea()", bridge, StringComparison.Ordinal);
        Assert.Contains("App.MainAppWindow?.HideForGameplay()", bridge, StringComparison.Ordinal);
        Assert.DoesNotContain("SwMinimize", bridge, StringComparison.Ordinal);
        Assert.Contains("WmSysCommand", icon, StringComparison.Ordinal);
        Assert.Contains("ScMinimize", icon, StringComparison.Ordinal);
    }

    [Fact]
    public void Uninstall_IsDirectAndArmsStorePromptAutomation()
    {
        var plate = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "src", "components", "GamePage.tsx"));
        var steam = ReadRepoFile("ExoLauncher", "Adapters", "SteamAdapter.cs");
        var riot = ReadRepoFile("ExoLauncher", "Adapters", "RiotAdapter.cs");
        var automator = ReadRepoFile("ExoLauncher", "Adapters", "StoreUninstallPromptAutomator.cs");
        Assert.DoesNotContain("window.confirm", plate, StringComparison.Ordinal);
        Assert.Contains("Confirm remove", plate, StringComparison.Ordinal);
        Assert.Contains("removeArmed", plate, StringComparison.Ordinal);
        Assert.Contains("SteamClientIpc.Command", steam, StringComparison.Ordinal);
        Assert.DoesNotContain("SteamUninstallPromptAutomator", steam, StringComparison.Ordinal);
        Assert.Contains("StoreUninstallPromptAutomator", steam, StringComparison.Ordinal);
        Assert.Contains("StoreUninstallPromptAutomator", riot, StringComparison.Ordinal);
        Assert.DoesNotContain("$pid =", automator, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SetForegroundWindow", automator, StringComparison.Ordinal);
        Assert.DoesNotContain("mouse_event", automator, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("normalizedContext.Contains($normalizedTitle)", automator, StringComparison.Ordinal);
    }

    [Fact]
    public void TrophyNotifications_AreAnOverlayOfTheSameBannerAndStayOutOfTheGameProcess()
    {
        var broker = ReadRepoFile("ExoLauncher", "Services", "TrophyNotificationService.cs");
        var presenter = ReadRepoFile("ExoLauncher", "Services", "TrophyNotificationPresenter.cs");
        var design = ReadRepoFile("ExoLauncher", "Services", "TrophyBannerDesign.cs");
        var bridge = ReadRepoFile("ExoLauncher", "Services", "WebHostBridge.cs");
        var settings = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "src", "components", "TrophyNotificationSettings.tsx"));
        var banner = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "src", "components", "TrophyBanner.tsx"));
        var overlay = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "src", "trophy-overlay.tsx"));

        Assert.Contains("Session-bound notification broker", broker, StringComparison.Ordinal);
        Assert.Contains("WsExNoActivate", presenter, StringComparison.Ordinal);
        Assert.Contains("WsExToolWindow", presenter, StringComparison.Ordinal);
        Assert.Contains("HwndTopmost", presenter, StringComparison.Ordinal);
        Assert.Contains("TrophyBannerDesign.Current", presenter, StringComparison.Ordinal);
        Assert.Contains("CreateCoreWebView2ControllerAsync", presenter, StringComparison.Ordinal);
        Assert.Contains("DefaultBackgroundColor", presenter, StringComparison.Ordinal);
        Assert.Contains("DwmExtendFrameIntoClientArea", presenter, StringComparison.Ordinal);
        Assert.Contains("DwmEnableBlurBehindWindow", presenter, StringComparison.Ordinal);
        Assert.Contains("ui/src/lib/trophyBannerDesign.json", design, StringComparison.Ordinal);
        Assert.Contains("from './TrophyBanner'", settings, StringComparison.Ordinal);
        Assert.Contains("from './components/TrophyBanner'", overlay, StringComparison.Ordinal);
        Assert.Contains("from '../lib/trophyBanner'", banner, StringComparison.Ordinal);
        Assert.Contains("TryGetSafeIconUri", presenter, StringComparison.Ordinal);
        Assert.Contains("Uri.UriSchemeHttps", presenter, StringComparison.Ordinal);
        Assert.DoesNotContain("Achievement unlocked", presenter, StringComparison.Ordinal);
        Assert.DoesNotContain("EXO // UNLOCKED", presenter, StringComparison.Ordinal);
        Assert.Contains("TrophySoundPlayer.Play(rarity)", presenter, StringComparison.Ordinal);
        Assert.DoesNotContain("TrophyMotion", presenter, StringComparison.Ordinal);
        Assert.DoesNotContain("window.Content = built.Card", presenter, StringComparison.Ordinal);
        Assert.DoesNotContain("new BitmapImage", presenter, StringComparison.Ordinal);
        Assert.Contains("BeginCloseCurrent", presenter, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildCollectibleBadge", presenter, StringComparison.Ordinal);
        Assert.Contains("PreviewTrophyNotification", bridge, StringComparison.Ordinal);
        Assert.Contains("Show unlocks", settings, StringComparison.Ordinal);
        Assert.Contains("Exclusive fullscreen cannot be covered", settings, StringComparison.Ordinal);
        Assert.Contains("id: 'top-left'", settings, StringComparison.Ordinal);
        Assert.Contains("id: 'top-center'", settings, StringComparison.Ordinal);
        Assert.Contains("id: 'top-right'", settings, StringComparison.Ordinal);
        Assert.Contains("UISettings().AnimationsEnabled", presenter, StringComparison.Ordinal);
        Assert.Contains("DwmSetWindowAttribute", presenter, StringComparison.Ordinal);
        Assert.DoesNotContain("SetWindowsHookEx", broker + presenter, StringComparison.Ordinal);
        Assert.DoesNotContain("WriteProcessMemory", broker + presenter, StringComparison.Ordinal);
    }

    [Fact]
    public void StoreSearch_PartialsAreScopedToTheRequestQuery()
    {
        var bridge = ReadRepoFile("ExoLauncher", "Services", "WebHostBridge.cs");
        var search = ReadRepoFile("ExoLauncher", "Services", "StoreSearchService.cs");

        Assert.DoesNotContain("OnPartialResults", bridge + search, StringComparison.Ordinal);
        Assert.Contains(".SearchAsync(query, lib, ct, PublishPartial)", bridge, StringComparison.Ordinal);
        Assert.Contains("Action<IReadOnlyList<StoreSearchHit>>? onPartialResults", search, StringComparison.Ordinal);
        Assert.Contains("PublishOwnedWhenWarmAsync", search, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsPanel_KeepsFunctionalActionsWithoutStoreAuthChrome()
    {
        var settings = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "src", "components", "SettingsPanel.tsx"));
        var trophies = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "src", "components", "TrophyNotificationSettings.tsx"));
        var onboarding = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "src", "components", "OnboardingPanel.tsx"));
        var portable = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "src", "lib", "portable.ts"));
        var library = ReadRepoFile("ExoLauncher", "Services", "LibraryService.cs");

        Assert.Contains("Stores on this PC", settings, StringComparison.Ordinal);
        Assert.Contains("Show unlocks", trophies, StringComparison.Ordinal);
        Assert.Contains("Check for update", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("Exo Profile", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("Reconnect", settings, StringComparison.Ordinal);
        // Store login chrome stays out. The one password input is the opt-in
        // Steam Web API key — it must stay masked, labelled, and free of a
        // native title tooltip that would leak the value on hover.
        Assert.Equal(1, CountOccurrences(settings, "type=\"password\""));
        var keyInput = SliceBetween(settings, "<input", "/>");
        Assert.Contains("type=\"password\"", keyInput, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Steam Web API key\"", keyInput, StringComparison.Ordinal);
        Assert.Contains("autoComplete=\"off\"", keyInput, StringComparison.Ordinal);
        Assert.DoesNotContain(" title=", keyInput, StringComparison.Ordinal);
        Assert.Contains("pickFolder('Choose game folder')", portable, StringComparison.Ordinal);
        Assert.Contains("host.showStore", settings, StringComparison.Ordinal);
        Assert.Contains("No store apps were found in the last local check.", settings, StringComparison.Ordinal);
        Assert.Contains("Stores on this PC", onboarding, StringComparison.Ordinal);
        Assert.Contains("Steam Web API key", onboarding, StringComparison.Ordinal);
        Assert.Contains("Create or sign in to your Exo account", onboarding, StringComparison.Ordinal);
        Assert.DoesNotContain("Continue offline", onboarding, StringComparison.Ordinal);
        Assert.DoesNotContain("offlineChosen", onboarding, StringComparison.Ordinal);
        Assert.Contains("serviceUnavailable || (!!accountState?.signedIn && !!accountState.handle)", onboarding, StringComparison.Ordinal);
        Assert.Contains("Finish setup", onboarding, StringComparison.Ordinal);
        Assert.DoesNotContain("onSkip", onboarding, StringComparison.Ordinal);
        Assert.Contains("if (!clientPresent && !backendPresent && !signedIn)", library, StringComparison.Ordinal);
        Assert.DoesNotContain("return \"Missing\";", library, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsPanel_DoesNotSendCompletedUsersBackThroughOnboarding()
    {
        var settings = ReadRepoFile("ui", "src", "components", "SettingsPanel.tsx");
        var account = ReadRepoFile("ui", "src", "components", "AccountPanel.tsx");

        Assert.DoesNotContain("Run setup again", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("restartOnboarding", settings, StringComparison.Ordinal);
        Assert.Contains("'Log out'", account, StringComparison.Ordinal);
    }

    [Fact]
    public void Onboarding_IsAThreeStepAccountFlowThatFillsTheWindow()
    {
        var onboarding = ReadRepoFile("ui", "src", "components", "OnboardingPanel.tsx");
        var app = ReadRepoFile("ui", "src", "components", "LauncherApp.tsx");
        var tokens = ReadRepoFile("ui", "src", "tokens.css");

        Assert.Contains("{ id: 'stores', label: 'Stores' }", onboarding, StringComparison.Ordinal);
        Assert.Contains("{ id: 'account', label: 'Account' }", onboarding, StringComparison.Ordinal);
        Assert.Contains("{ id: 'profile', label: 'Make it yours' }", onboarding, StringComparison.Ordinal);
        Assert.Contains("<AccountPanel", onboarding, StringComparison.Ordinal);
        Assert.Contains("host.storesAuth(store.store)", onboarding, StringComparison.Ordinal);
        Assert.Contains("host.profilePickImage(kind)", onboarding, StringComparison.Ordinal);
        Assert.DoesNotContain("Continue offline", onboarding, StringComparison.Ordinal);
        Assert.DoesNotContain("offlineChosen", onboarding, StringComparison.Ordinal);
        Assert.Contains("serviceUnavailable || (!!accountState?.signedIn && !!accountState.handle)", onboarding, StringComparison.Ordinal);
        Assert.DoesNotContain("StaggeredText", onboarding, StringComparison.Ordinal);
        Assert.DoesNotContain("BlurHighlight", onboarding, StringComparison.Ordinal);

        Assert.Contains("stores={stores}", app, StringComparison.Ordinal);
        Assert.Contains("onComplete={finishOnboarding}", app, StringComparison.Ordinal);
        Assert.Contains("if (refreshLibrary) await loadLibrary(true)", app, StringComparison.Ordinal);

        var shell = SliceBetween(tokens, ".exo-onboarding-shell {", ".exo-onboarding-rail {");
        var main = SliceBetween(tokens, ".exo-onboarding-main {", ".exo-onboarding-content {");
        var storeBody = SliceBetween(tokens, ".exo-onboarding-store-body {", ".exo-onboarding-store-body::-webkit-scrollbar");
        Assert.Contains("width: 100%", shell, StringComparison.Ordinal);
        Assert.Contains("height: 100%", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("width: min(", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("height: min(", shell, StringComparison.Ordinal);
        Assert.Contains("overflow: hidden", shell, StringComparison.Ordinal);
        Assert.Contains("grid-template-rows: minmax(0, 1fr) auto", main, StringComparison.Ordinal);
        Assert.Contains("overflow-y: auto", storeBody, StringComparison.Ordinal);
        Assert.Contains("scrollbar-width: none", storeBody, StringComparison.Ordinal);
        Assert.Contains("exo-onboarding-step-in 190ms", tokens, StringComparison.Ordinal);
        Assert.Contains("@media (prefers-reduced-motion: reduce)", tokens, StringComparison.Ordinal);
    }

    [Fact]
    public void FirstRun_WaitsForStoreDetectionAndDefersTheProfileIdentityRead()
    {
        var app = ReadRepoFile("ui", "src", "components", "LauncherApp.tsx");
        Assert.Contains("if (settings?.onboardingComplete !== true) return", app, StringComparison.Ordinal);
        Assert.Contains("host.profileGet()", app, StringComparison.Ordinal);
        Assert.Contains("}, [applyIdentity, settings?.onboardingComplete])", app, StringComparison.Ordinal);
        Assert.Contains("const [storeMatrixReady, setStoreMatrixReady] = useState(false)", app, StringComparison.Ordinal);
        Assert.Contains(".finally(() => setStoreMatrixReady(true))", app, StringComparison.Ordinal);
        Assert.Contains("!settings.onboardingComplete && !storeMatrixReady", app, StringComparison.Ordinal);
    }

    [Fact]
    public void StoreSettings_RefreshTheLibraryAndKeepBusyStatePerRow()
    {
        var app = ReadRepoFile("ui", "src", "components", "LauncherApp.tsx");
        var settings = ReadRepoFile("ui", "src", "components", "SettingsPanel.tsx");
        var settingsPanel = SliceBetween(app, "const settingsPanel = (", "return (");
        var storePanel = SliceBetween(settings, "{section === 'stores'", "<SteamWebApiKeyRow");

        Assert.Contains("onStores={async (next) =>", settingsPanel, StringComparison.Ordinal);
        Assert.Contains("await loadLibrary(true)", settingsPanel, StringComparison.Ordinal);
        Assert.Contains("const storeBusyRef = useRef", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("openingStore", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("connectingStore", settings, StringComparison.Ordinal);
        Assert.Contains("const next = await host.storesMatrix()", settings, StringComparison.Ordinal);
        Assert.Contains("setCheckedStores(next)", settings, StringComparison.Ordinal);
        Assert.Contains("await onStores?.(next)", settings, StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(storePanel, "disabled={rowBusy}"));
    }

    [Fact]
    public void SteamKeyDraft_IsClearedBeforeTheSaveSettles()
    {
        var settings = ReadRepoFile("ui", "src", "components", "SettingsPanel.tsx");
        var persist = SliceBetween(settings, "async function persist(value: string)", "return (");
        var started = persist.IndexOf("const pending = host.setSettings", StringComparison.Ordinal);
        var cleared = persist.IndexOf("setDraft('')", StringComparison.Ordinal);
        var settled = persist.IndexOf("await pending", StringComparison.Ordinal);

        Assert.True(started >= 0, "Steam key save must start through the host.");
        Assert.True(started < cleared, "The input must clear only after the host save starts.");
        Assert.True(cleared < settled, "The input must clear before the host save settles.");
        Assert.Contains("catch (cause)", persist, StringComparison.Ordinal);
    }

    [Fact]
    public void PrimaryCta_OffersBuyAgainOnlyForExplicitRevocation()
    {
        var format = ReadRepoFile("ExoLauncher", "Ui", "UiFormat.cs");
        var storefront = ReadRepoFile("ExoLauncher", "Adapters", "Storefront.cs");
        var host = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "src", "lib", "host.ts"));
        var entitlement = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "src", "lib", "entitlementActions.ts"));
        Assert.Contains("return \"none\";", format, StringComparison.Ordinal);
        Assert.DoesNotContain("return \"Buy\"", format, StringComparison.Ordinal);
        Assert.Contains("return 'Not installed'", host, StringComparison.Ordinal);
        Assert.Contains("if (game.entitlementState === 'notOwned') return 'Buy again'", entitlement, StringComparison.Ordinal);
        Assert.Contains("if (game.entitlementState === 'unverified') return 'Unavailable'", entitlement, StringComparison.Ordinal);
        Assert.Contains("if (game.entitlementState === 'unverified') return false", entitlement, StringComparison.Ordinal);
        Assert.Contains("if (game.entitlementState === 'notOwned') return true", entitlement, StringComparison.Ordinal);
        Assert.Contains("if (game.Installed || game.Owned) return null;", storefront, StringComparison.Ordinal);
        Assert.Contains("\"Play\"", format, StringComparison.Ordinal);
        Assert.Contains("\"Install\"", format, StringComparison.Ordinal);
        Assert.Contains("\"Update\"", format, StringComparison.Ordinal);
    }

    [Fact]
    public void Overlay_UsesHostBuyUrl_ForEveryPurchasableStore()
    {
        var overlay = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "src", "components", "GamePage.tsx"));
        var host = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "src", "lib", "host.ts"));
        var entitlement = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "src", "lib", "entitlementActions.ts"));
        var bridge = ReadRepoFile("ExoLauncher", "Services", "WebHostBridge.cs");

        Assert.Contains("hostedBuyUrl(selected)", overlay, StringComparison.Ordinal);
        Assert.Contains("const dealsUrl = buyUrl ? ggDealsUrl(selected) : null", overlay, StringComparison.Ordinal);
        Assert.Contains("export function hostedBuyUrl", host, StringComparison.Ordinal);
        Assert.Contains("buyUrl = UiFormat.BuyUrl(g)", bridge, StringComparison.Ordinal);
        Assert.Contains("buyUrl = UiFormat.BuyUrl(SearchHitEntry(h))", bridge, StringComparison.Ordinal);
        Assert.DoesNotContain("function storeBuyUrl", overlay, StringComparison.Ordinal);
        Assert.DoesNotContain("https://www.gog.com/en/game/${encodeURIComponent(target)}", overlay, StringComparison.Ordinal);
        Assert.DoesNotContain("steam://store/${target}", overlay, StringComparison.Ordinal);
        Assert.DoesNotContain("epic:catalog:", overlay, StringComparison.Ordinal);
        Assert.Contains("if (!canExposeBuyUrl(game)) return null", host, StringComparison.Ordinal);
        Assert.Contains("return !game.installed && !game.owned", entitlement, StringComparison.Ordinal);
        Assert.Contains("if (game.entitlementState === 'notOwned') return true", entitlement, StringComparison.Ordinal);
    }

    [Fact]
    public void InAppUpdate_UsesTheNormalGracefulShutdownPath()
    {
        var bridge = ReadRepoFile("ExoLauncher", "Services", "WebHostBridge.cs");
        var updateStart = bridge.IndexOf("private async Task<object> InstallUpdateAsync()", StringComparison.Ordinal);
        Assert.True(updateStart >= 0);
        var updateEnd = bridge.IndexOf("private object MapGame", updateStart, StringComparison.Ordinal);
        Assert.True(updateEnd > updateStart);
        var update = bridge[updateStart..updateEnd];

        Assert.Contains("App.MainAppWindow?.Close()", update, StringComparison.Ordinal);
        Assert.Contains("Application.Current?.Exit()", update, StringComparison.Ordinal);
        Assert.DoesNotContain("Environment.Exit", update, StringComparison.Ordinal);
    }

    [Fact]
    public void ReactShell_HoldsPlaytimeAndLeavesNowAlone()
    {
        var overlay = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "src", "components", "GamePage.tsx"));
        var app = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "src", "components", "LauncherApp.tsx"));
        var nowTs = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "src", "lib", "now.ts"));
        var now = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "src", "components", "NowStage.tsx"));
        var tile = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "src", "components", "GameCard.tsx"));
        var cover = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "src", "components", "CoverArt.tsx"));
        var windowCs = ReadRepoFile("ExoLauncher", "MainWindow.xaml.cs");
        var tokens = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "src", "tokens.css"));

        Assert.Contains("Confirm remove", overlay, StringComparison.Ordinal);
        Assert.Contains("formatPlayed", overlay, StringComparison.Ordinal);
        Assert.Contains("variant.id === selected.id", overlay, StringComparison.Ordinal);
        Assert.DoesNotContain("hours >= 10", overlay, StringComparison.Ordinal);
        Assert.Contains("UpscalerFiles", overlay, StringComparison.Ordinal);
        Assert.Contains("<Wrench size={14} />", overlay, StringComparison.Ordinal);
        Assert.Contains("retainNow(games, picked, holdNowId.current)", app, StringComparison.Ordinal);
        Assert.Contains("Tile click / library churn must not steal the banner", nowTs, StringComparison.Ordinal);
        Assert.Contains("{cta}", now, StringComparison.Ordinal);
        Assert.Contains("exo-tile", tile, StringComparison.Ordinal);
        Assert.DoesNotContain("StarFilled size={14}", tile, StringComparison.Ordinal);
        Assert.DoesNotContain("GameMetadata", tile, StringComparison.Ordinal);
        Assert.DoesNotContain("metadata?.genre", tile, StringComparison.Ordinal);
        Assert.DoesNotContain("metadata?.year", tile, StringComparison.Ordinal);
        Assert.DoesNotContain("exo-tile-shine", tile + tokens, StringComparison.Ordinal);
        var tileHit = tile.IndexOf("className=\"exo-tile-hit\"", StringComparison.Ordinal);
        var tileHitEnd = tile.IndexOf("</button>", tileHit, StringComparison.Ordinal);
        Assert.True(tileHit >= 0 && tileHitEnd > tileHit, "The card must remain one complete button.");
        Assert.Contains(".exo-tile-hit:focus-visible {", tokens, StringComparison.Ordinal);
        Assert.DoesNotContain("translateY(-5px)", tokens, StringComparison.Ordinal);
        Assert.Contains("exo-tile {", tokens, StringComparison.Ordinal);
        Assert.DoesNotContain("exo-tile-pin", tile + tokens, StringComparison.Ordinal);
        Assert.DoesNotContain("title=", tile, StringComparison.Ordinal);
        Assert.DoesNotContain("ResizeObserver", tile, StringComparison.Ordinal);
        Assert.DoesNotContain("data-card-name", tile, StringComparison.Ordinal);
        Assert.Contains("safeUrl.startsWith(`${COVER_CACHE_ORIGIN}/`)", cover, StringComparison.Ordinal);
        Assert.Contains("900", cover, StringComparison.Ordinal);
        Assert.Contains("pinned", app, StringComparison.Ordinal);
        Assert.Contains("DefaultWindowWidth = 1400", windowCs, StringComparison.Ordinal);
        Assert.Contains("DefaultWindowHeight = 900", windowCs, StringComparison.Ordinal);
        Assert.Contains("MinWindowWidth = 1100", windowCs, StringComparison.Ordinal);
        Assert.Contains("IsMaximizable = true", windowCs, StringComparison.Ordinal);
        Assert.Contains("IsResizable = true", windowCs, StringComparison.Ordinal);
        Assert.Contains("NonClientRegionKind.Caption", windowCs, StringComparison.Ordinal);
        Assert.Contains("TitleBarSearchPassthroughDip = 184", windowCs, StringComparison.Ordinal);
        Assert.Contains("TitleBarSearchHeightDip = 32", windowCs, StringComparison.Ordinal);
        Assert.Contains("NonClientRegionKind.Passthrough", windowCs, StringComparison.Ordinal);
        Assert.Contains(".exo-titlebar-search:focus-within", tokens, StringComparison.Ordinal);
        Assert.Contains(".exo-titlebar-search.has-query", tokens, StringComparison.Ordinal);
        Assert.Contains("width: 96px", SliceBetween(tokens, ".exo-titlebar-search {", ".exo-search-capsule {"), StringComparison.Ordinal);
        Assert.Contains("width: 184px", tokens, StringComparison.Ordinal);
        Assert.Contains("exo-search-capsule", app, StringComparison.Ordinal);
        Assert.Contains("width: 100%", SliceBetween(tokens, ".exo-search-capsule {", "}"), StringComparison.Ordinal);
        Assert.Contains("transition:", SliceBetween(tokens, ".exo-search-capsule {", "}"), StringComparison.Ordinal);
        Assert.Contains("width 200ms var(--ease-in-out)", SliceBetween(tokens, ".exo-titlebar-search {", ".exo-search-capsule {"), StringComparison.Ordinal);
        Assert.DoesNotContain("scaleX(", SliceBetween(tokens, ".exo-search-capsule {", "}"), StringComparison.Ordinal);
        Assert.Contains("width: 100%", SliceBetween(tokens, ".exo-titlebar-search:focus-within .exo-search-capsule,", "@media (prefers-reduced-motion: reduce)"), StringComparison.Ordinal);
        Assert.DoesNotContain("box-shadow", SliceBetween(tokens, ".exo-search-capsule {", "@media (prefers-reduced-motion: reduce)"), StringComparison.Ordinal);
        Assert.Contains("padding: 0 14px", tokens, StringComparison.Ordinal);
        Assert.Contains("white-space: normal", SliceBetween(tokens, ".exo-card-title {", "}"), StringComparison.Ordinal);
        Assert.Contains("overflow-wrap: anywhere", SliceBetween(tokens, ".exo-card-title {", "}"), StringComparison.Ordinal);
        Assert.Contains("private object ToggleMaximize()", ReadRepoFile("ExoLauncher", "Services", "WebHostBridge.cs"), StringComparison.Ordinal);
        Assert.DoesNotContain("ContinueBanner", app, StringComparison.Ordinal);
        // GamePage is the details overlay. The old DetailPanel re-export shim is gone.
        Assert.False(File.Exists(Path.Combine(RepoRoot(), "ui", "src", "components", "DetailPanel.tsx")));
        Assert.True(File.Exists(Path.Combine(RepoRoot(), "ui", "src", "components", "GamePage.tsx")));
        Assert.True(Directory.Exists(Path.Combine(RepoRoot(), "ui")));
    }

    [Fact]
    public void LibrarySurface_KeepsCardsPointerSafeQuietAndScrollableWithoutVisibleBars()
    {
        var tokens = ReadRepoFile("ui", "src", "tokens.css");
        var tile = ReadRepoFile("ui", "src", "components", "GameCard.tsx");
        var gridComponent = ReadRepoFile("ui", "src", "components", "WindowedGameGrid.tsx");
        var hit = SliceBetween(tokens, ".exo-tile-hit {", "}");
        var tileShell = SliceBetween(tokens, ".exo-tile {", "}");
        var title = SliceBetween(tokens, ".exo-card-title {", "}");
        var grid = SliceBetween(tokens, ".exo-game-grid {", "}");
        var gridRow = SliceBetween(tokens, ".exo-game-grid-row {", "}");
        var windowedGrid = SliceBetween(tokens, ".exo-windowed-game-grid {", "}");
        var pinnedGrid = SliceBetween(tokens, ".exo-pin-track {", "}");
        var library = SliceBetween(tokens, ".exo-library-pane {", "}");
        var details = SliceBetween(tokens, ".exo-game-page-inner {", "}");
        var webkit = SliceBetween(tokens, ".exo-library-pane::-webkit-scrollbar,", "}");
        var ambientStart = tokens.LastIndexOf(".exo-ambient::after {", StringComparison.Ordinal);
        Assert.True(ambientStart >= 0, "missing the second ambient radial");
        var ambientEnd = tokens.IndexOf('}', ambientStart);
        Assert.True(ambientEnd > ambientStart, "missing the second ambient radial block end");
        var ambient = tokens[ambientStart..ambientEnd];

        Assert.Contains("--exo-card-w: 160px", tokens, StringComparison.Ordinal);
        Assert.Contains("--exo-card-w: clamp(148px", tokens, StringComparison.Ordinal);
        Assert.Contains("resolveGridCardLayout", gridComponent, StringComparison.Ordinal);
        Assert.Contains("gap: var(--exo-grid-row-gap)", grid, StringComparison.Ordinal);
        Assert.Contains("repeat(var(--exo-grid-columns), var(--exo-card-w))", gridRow, StringComparison.Ordinal);
        Assert.Contains("grid-auto-flow: column", pinnedGrid, StringComparison.Ordinal);
        Assert.Contains("grid-auto-columns: var(--exo-card-w)", pinnedGrid, StringComparison.Ordinal);
        Assert.Contains("overflow-x: auto", pinnedGrid, StringComparison.Ordinal);
        Assert.DoesNotContain("grid-auto-flow: column", grid + gridRow, StringComparison.Ordinal);
        Assert.Contains("height: calc((var(--exo-card-w) * 1.5) + 54px)", hit, StringComparison.Ordinal);
        Assert.Contains("overflow-anchor: none", windowedGrid, StringComparison.Ordinal);
        Assert.DoesNotContain("content-visibility", tileShell, StringComparison.Ordinal);
        Assert.DoesNotContain("contain-intrinsic-size", tileShell, StringComparison.Ordinal);
        Assert.Contains("transition: transform 170ms var(--ease-out)", tileShell, StringComparison.Ordinal);
        Assert.Contains("transform: translateY(-2px)", tokens, StringComparison.Ordinal);
        Assert.Contains("scale(1.02)", tokens, StringComparison.Ordinal);
        Assert.Contains("0 12px 28px rgba(0, 0, 0, 0.28)", tokens, StringComparison.Ordinal);
        Assert.Contains("scale(0.985)", tokens, StringComparison.Ordinal);
        Assert.Contains("transition-duration: 120ms", tokens, StringComparison.Ordinal);
        Assert.DoesNotContain("transition: all", tokens, StringComparison.Ordinal);
        Assert.DoesNotContain("min-height", title, StringComparison.Ordinal);
        Assert.DoesNotContain("exo-tile-shine", tile + tokens, StringComparison.Ordinal);
        Assert.DoesNotContain("exo-tile-sweep", tokens, StringComparison.Ordinal);
        Assert.DoesNotContain("exo-playing-pulse", tokens, StringComparison.Ordinal);
        Assert.DoesNotContain("exo-update-breathe", tokens, StringComparison.Ordinal);

        Assert.DoesNotContain("exo-tile-pin", tile + tokens + gridComponent, StringComparison.Ordinal);

        Assert.DoesNotContain("\n.exo-ghost-btn:hover {", tokens, StringComparison.Ordinal);
        Assert.Contains("@media (hover: hover) and (pointer: fine) {\n  .exo-ghost-btn:hover", tokens, StringComparison.Ordinal);

        Assert.Contains("overflow-y: auto", library, StringComparison.Ordinal);
        Assert.Contains("scrollbar-width: none", library, StringComparison.Ordinal);
        Assert.Contains("overflow-y: auto", details, StringComparison.Ordinal);
        Assert.Contains("scrollbar-width: none", details, StringComparison.Ordinal);
        Assert.Contains("display: none", webkit, StringComparison.Ordinal);
        Assert.Contains("radial-gradient", ambient, StringComparison.Ordinal);
        Assert.DoesNotContain("animation", ambient, StringComparison.Ordinal);
        Assert.DoesNotContain("text-rendering: optimizeSpeed", tokens, StringComparison.Ordinal);
        Assert.Contains("@media (max-height: 760px)", tokens, StringComparison.Ordinal);
        Assert.Contains("height: 168px", tokens, StringComparison.Ordinal);
        Assert.DoesNotContain("exo-tile-pin", SliceBetween(tokens, "@media (prefers-reduced-motion: reduce) {", "/* ── Exo profile ── */"), StringComparison.Ordinal);
    }

    [Fact]
    public void FavoriteControlLivesOnlyInGameDetails_NotOnLibraryCards()
    {
        var page = ReadRepoFile("ui", "src", "components", "GamePage.tsx");
        var card = ReadRepoFile("ui", "src", "components", "GameCard.tsx");
        var tokens = ReadRepoFile("ui", "src", "tokens.css");

        Assert.DoesNotContain("exo-tile-pin", card + tokens, StringComparison.Ordinal);
        Assert.DoesNotContain("StarFilled", card, StringComparison.Ordinal);
        Assert.DoesNotContain("onToggleFavorite", card, StringComparison.Ordinal);
        Assert.DoesNotContain("exo-detail-cover", page, StringComparison.Ordinal);
        Assert.Contains("exo-game-favorite", page, StringComparison.Ordinal);
    }

    [Fact]
    public void TileClick_OpensOverlayAndLeavesNowAlone()
    {
        var app = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "src", "components", "LauncherApp.tsx"));
        var nowTs = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "src", "lib", "now.ts"));
        var overlay = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "src", "components", "GamePage.tsx"));

        Assert.Contains("holdNowId.current = nowIdRef.current ?? holdNowId.current", app, StringComparison.Ordinal);
        Assert.Contains("export function retainNow(", nowTs, StringComparison.Ordinal);
        Assert.Contains("if (picked.kind === 'download' || picked.kind === 'playing') return picked", nowTs, StringComparison.Ordinal);
        Assert.Contains("Confirm remove", overlay, StringComparison.Ordinal);
        Assert.DoesNotContain("const box = nowBox ?? viewBox()", app, StringComparison.Ordinal);
    }

    [Fact]
    public void PlayLaunchesWithoutWaitingOnUpscalerPack()
    {
        var bridge = ReadRepoFile("ExoLauncher", "Services", "WebHostBridge.cs");
        var launchStart = bridge.IndexOf("private async Task<object> GameLaunchAsync", StringComparison.Ordinal);
        var launchEnd = bridge.IndexOf("private async Task<object> GameStopAsync", launchStart, StringComparison.Ordinal);
        Assert.True(launchStart >= 0 && launchEnd > launchStart);
        var launch = bridge[launchStart..launchEnd];
        Assert.DoesNotContain("EnsureLatest", launch, StringComparison.Ordinal);
        Assert.DoesNotContain("Dlss", launch, StringComparison.Ordinal);
        Assert.DoesNotContain("UpdateGame", launch, StringComparison.Ordinal);

        var orchestrator = ReadRepoFile("ExoLauncher", "Services", "LaunchOrchestrator.cs");
        Assert.DoesNotContain("EnsureLatestPackAsync", orchestrator, StringComparison.Ordinal);
        Assert.DoesNotContain("DlssSwapService", orchestrator, StringComparison.Ordinal);

        var app = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "src", "components", "LauncherApp.tsx"));
        Assert.Contains("host.launch(", app, StringComparison.Ordinal);
        Assert.DoesNotContain("host.dlssApply", app, StringComparison.Ordinal);
        // Applying lives in the details overlay's upscaler panel, never on the
        // shelf, and one press covers every destination the game ships.
        var upscalers = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "src", "components", "UpscalerFiles.tsx"));
        Assert.Contains("host.dlssApply(gameId)", upscalers, StringComparison.Ordinal);
        Assert.Contains("host.dlssRestore(gameId)", upscalers, StringComparison.Ordinal);
        Assert.DoesNotContain("dlssApplyFile", upscalers, StringComparison.Ordinal);

        var swap = ReadRepoFile("ExoLauncher", "Services", "DlssSwapService.cs");
        Assert.DoesNotContain("pack.Files.TryGetValue(Fsr4LoaderName", swap, StringComparison.Ordinal);
        Assert.DoesNotContain("haveCore", swap, StringComparison.Ordinal);
        Assert.DoesNotContain("Download the rest.", swap, StringComparison.Ordinal);
        Assert.Contains("NeededPackFiles(targets.SelectMany(ScanGame))", swap, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var companion in Fsr4CompanionNames)", swap, StringComparison.Ordinal);
        Assert.Contains("EvaluateSdkStatus(", swap, StringComparison.Ordinal);
        Assert.DoesNotContain("string.IsNullOrWhiteSpace(remoteFsr) ? \"FSR 3.1\" : \"FSR \" + remoteFsr", swap, StringComparison.Ordinal);
        Assert.DoesNotContain("\"XeSS\",\n            });", swap, StringComparison.Ordinal);
    }

    [Fact]
    public void Details_KnowEveryUpscalerDestination_ButRenderOnlyDetectedFiles()
    {
        var dests = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "src", "lib", "upscalers.ts"));
        var overlay = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "src", "components", "GamePage.tsx"));
        var files = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "src", "components", "UpscalerFiles.tsx"));
        var swap = ReadRepoFile("ExoLauncher", "Services", "DlssSwapService.cs");

        foreach (var name in new[]
        {
            "nvngx_dlss.dll",
            "nvngx_dlssg.dll",
            "nvngx_dlssd.dll",
            "amd_fidelityfx_dx12.dll",
            "amd_fidelityfx_vk.dll",
            "amd_fidelityfx_loader_dx12.dll",
            "amd_fidelityfx_upscaler_dx12.dll",
            "amd_fidelityfx_framegeneration_dx12.dll",
            "amd_fidelityfx_denoiser_dx12.dll",
            "amd_fidelityfx_radiancecache_dx12.dll",
            "libxess.dll",
            "libxess_dx11.dll",
            "libxess_fg.dll",
            "libxell.dll",
        })
        {
            Assert.Contains(name, dests, StringComparison.Ordinal);
        }

        Assert.Contains("UPSCALER_DESTS", dests, StringComparison.Ordinal);
        Assert.Contains("present === true", dests, StringComparison.Ordinal);
        Assert.Contains("'—'", dests, StringComparison.Ordinal);
        Assert.Contains("UpscalerFiles", overlay, StringComparison.Ordinal);
        Assert.Contains("const visibleRows = rows.filter((row) => row.present)", files, StringComparison.Ordinal);
        Assert.Contains("upscalerGroup(visibleRows, gate)", files, StringComparison.Ordinal);
        Assert.Contains("const visibleGate = antiCheat ? null : gateReason(gate)", files, StringComparison.Ordinal);
        Assert.Contains("{visibleGate && <p className=\"exo-upscaler-gate\">{visibleGate}</p>}", files, StringComparison.Ordinal);
        Assert.DoesNotContain("{blockedByGame &&", files, StringComparison.Ordinal);
        Assert.Contains("const state = upscalerVisualState(row)", files, StringComparison.Ordinal);
        Assert.Contains("exo-upscaler-signal", files, StringComparison.Ordinal);
        Assert.DoesNotContain("visibleNoteText", files, StringComparison.Ordinal);
        Assert.Contains("hideBlockedReason={antiCheat === true}", files, StringComparison.Ordinal);
        Assert.DoesNotContain("/anti[- ]?cheat/i.test(note)", files, StringComparison.Ordinal);
        Assert.Contains("if (visibleRows.length === 0 && run.kind === 'idle') return null", files, StringComparison.Ordinal);
        Assert.Contains("{visibleRows.length} detected", files, StringComparison.Ordinal);
        Assert.Contains("{visibleRows.map", files, StringComparison.Ordinal);
        Assert.DoesNotContain("{rows.map", files, StringComparison.Ordinal);
        Assert.DoesNotContain("exo-upscaler-toggle", files, StringComparison.Ordinal);
        Assert.DoesNotContain("aria-expanded", files, StringComparison.Ordinal);
        Assert.DoesNotContain("setOpen", files, StringComparison.Ordinal);
        Assert.Contains("exo-game-tool exo-upscaler-btn", files, StringComparison.Ordinal);
        Assert.Contains("host.dlssApply(gameId)", files, StringComparison.Ordinal);
        Assert.Contains("host.dlssRestore(gameId)", files, StringComparison.Ordinal);
        Assert.Contains("Already newest", dests, StringComparison.Ordinal);
        Assert.Contains("Nothing to update", dests, StringComparison.Ordinal);
        Assert.Contains("isNewest ? null", dests, StringComparison.Ordinal);
        Assert.DoesNotContain("isNewest ? 'Newest'", dests, StringComparison.Ordinal);
        Assert.Contains("WithFullDestCatalog", swap, StringComparison.Ordinal);
        Assert.Contains("Present: false", swap, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadingIndicators_RespectReducedMotion()
    {
        var app = ReadRepoFile("ui", "src", "components", "LauncherApp.tsx");
        var page = ReadRepoFile("ui", "src", "components", "GamePage.tsx");
        var now = ReadRepoFile("ui", "src", "components", "NowStage.tsx");

        Assert.DoesNotContain("className=\"animate-spin\"", app + page + now, StringComparison.Ordinal);
        Assert.DoesNotContain("className=\"shrink-0 animate-spin\"", page, StringComparison.Ordinal);
        Assert.Equal(5, CountOccurrences(app + page + now, "motion-reduce:animate-none"));
    }

    [Fact]
    public void GamePage_ResetsPerSourceStateAndRejectsStaleDlssRefreshes()
    {
        var page = ReadRepoFile("ui", "src", "components", "GamePage.tsx");
        var reset = SliceBetween(page, "// Every local result belongs to one exact store entry.", "// Catalog text is fetched");
        var refresh = SliceBetween(page, "const refreshDlss = useCallback", "useEffect(() => {");
        var sources = SliceBetween(page, "{sources && onSelectSource && (", "<div className=\"exo-game-stats\">");

        Assert.Contains("JSON.stringify([selected.id, selected.store])", page, StringComparison.Ordinal);
        foreach (var state in new[]
                 {
                     "setUninstalling(false)",
                     "setRemoveArmed(false)",
                     "setRepairing(false)",
                     "setRepair(null)",
                     "setAchievementData(null)",
                     "setDlss(selected.installed ? peekUpscalerStatus(selected.id) : null)",
                     "setMetadata(null)",
                 })
        {
            Assert.Contains(state, reset, StringComparison.Ordinal);
        }
        Assert.Contains("}, [selectionKey])", reset, StringComparison.Ordinal);

        Assert.Contains("const requestKey = selectionKey", refresh, StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(refresh, "selectionKeyRef.current === requestKey"));
        Assert.DoesNotContain(".then(setDlss)", refresh, StringComparison.Ordinal);
        Assert.Contains("const sourceSwitchLocked =", page, StringComparison.Ordinal);
        Assert.Contains("repairing", SliceBetween(page, "const sourceSwitchLocked =", "const action ="), StringComparison.Ordinal);
        Assert.Contains("uninstalling", SliceBetween(page, "const sourceSwitchLocked =", "const action ="), StringComparison.Ordinal);
        Assert.Contains("progress?.isActive", SliceBetween(page, "const sourceSwitchLocked =", "const action ="), StringComparison.Ordinal);
        Assert.Contains("disabled={sourceSwitchLocked}", sources, StringComparison.Ordinal);
        Assert.Contains("key={selectionKey}", page, StringComparison.Ordinal);
        Assert.Contains("peekUpscalerStatus(selected.id)", page, StringComparison.Ordinal);
        Assert.Contains("loadUpscalerStatus(selected.id", page, StringComparison.Ordinal);
    }

    [Fact]
    public void ReactShell_KeepsDetailsScrollableAndModalActionsLocked()
    {
        var app = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "src", "components", "LauncherApp.tsx"));
        var page = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "src", "components", "GamePage.tsx"));
        var tokens = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "src", "tokens.css"));

        Assert.Contains("className=\"exo-game-page-inner\"", page, StringComparison.Ordinal);
        Assert.Contains(".exo-game-page-inner", tokens, StringComparison.Ordinal);
        Assert.Contains("overflow-y: auto", tokens, StringComparison.Ordinal);
        Assert.Contains("inert={libraryPane === 'game' ? true : undefined}", app, StringComparison.Ordinal);
        Assert.Contains("closeDisabled={actionLocked}", app, StringComparison.Ordinal);
        Assert.Contains("disabled={actionLocked}", app, StringComparison.Ordinal);
    }

    [Fact]
    public void Shell_NoticesOverlayContentInsteadOfChangingPageHeight()
    {
        var app = ReadRepoFile("ui", "src", "components", "LauncherApp.tsx");
        var tokens = ReadRepoFile("ui", "src", "tokens.css");
        var stack = SliceBetween(tokens, ".exo-toast-stack {", ".exo-toast {");

        Assert.Contains("<div className=\"exo-toast-stack\">", app, StringComparison.Ordinal);
        Assert.Contains("position: fixed", stack, StringComparison.Ordinal);
        Assert.Contains("pointer-events: none", stack, StringComparison.Ordinal);
        Assert.Contains("role=\"alert\"", app, StringComparison.Ordinal);
    }

    [Fact]
    public void FriendsRoom_ContainsScrollingInsideItsTwoColumns()
    {
        var tokens = ReadRepoFile("ui", "src", "tokens.css");
        var room = SliceBetween(tokens, ".exo-friends {", ".exo-friend-list {");
        var list = SliceBetween(tokens, ".exo-friend-list {", ".exo-friend-list-head {");
        var body = SliceBetween(tokens, ".exo-friend-list-body {", ".exo-friend-group {");
        var detail = SliceBetween(tokens, ".exo-friends-detail {", ".exo-friend-page {");

        Assert.Contains("height: 100%", room, StringComparison.Ordinal);
        Assert.Contains("overflow: hidden", room, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: 300px minmax(0, 1fr)", room, StringComparison.Ordinal);
        Assert.Contains("grid-template-rows: minmax(0, 1fr)", room, StringComparison.Ordinal);
        Assert.Contains("overflow: hidden", list, StringComparison.Ordinal);
        Assert.Contains("overflow-y: auto", body, StringComparison.Ordinal);
        Assert.Contains("scrollbar-width: none", body, StringComparison.Ordinal);
        Assert.Contains("overflow-y: auto", detail, StringComparison.Ordinal);
        Assert.Contains("scrollbar-width: none", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void FriendsDetail_UsesCompactScopedLayoutAtBothSupportedSizes()
    {
        var friends = ReadRepoFile("ui", "src", "components", "FriendsRoom.tsx");
        var tokens = ReadRepoFile("ui", "src", "tokens.css");
        var friendsCss = SliceBetween(tokens, ".exo-friends {", ".exo-set {");

        Assert.Contains("className=\"exo-friends-detail\"", friends, StringComparison.Ordinal);
        Assert.DoesNotContain("exo-friends-detail min-h-0 overflow-y-auto", friends, StringComparison.Ordinal);
        Assert.Contains("exo-friend-page is-person", friends, StringComparison.Ordinal);
        Assert.Contains("exo-friend-page is-store", friends, StringComparison.Ordinal);
        Assert.Contains("exo-friend-person-grid", friends, StringComparison.Ordinal);
        Assert.Contains("exo-friend-store-grid", friends, StringComparison.Ordinal);
        Assert.Contains("playing && 'has-playing'", friends, StringComparison.Ordinal);
        Assert.Contains("exo-friend-playing-actions", friends, StringComparison.Ordinal);
        Assert.Contains("exo-friend-context", friends, StringComparison.Ordinal);

        // Profile editing owns a full-height nested scroller. Friend details use
        // their own compact form so that rule cannot create an empty canvas.
        Assert.DoesNotContain("className=\"exo-profile-form\"", friends, StringComparison.Ordinal);
        Assert.DoesNotContain("className=\"exo-profile-head\"", friends, StringComparison.Ordinal);
        Assert.DoesNotContain(".exo-friend-page .exo-profile-form", friendsCss, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: minmax(0, 1.55fr) minmax(230px, 0.72fr)", friendsCss, StringComparison.Ordinal);
        Assert.Contains(".exo-friend-page.is-store:not(.has-playing)", friendsCss, StringComparison.Ordinal);
        Assert.Contains("width: min(100%, 46rem)", friendsCss, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: minmax(0, 42rem)", friendsCss, StringComparison.Ordinal);
        Assert.Contains("@media (max-width: 1156px)", friendsCss, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: 276px minmax(0, 1fr)", friendsCss, StringComparison.Ordinal);
        Assert.Contains("@media (max-height: 760px)", friendsCss, StringComparison.Ordinal);
        Assert.Contains("height: 122px", friendsCss, StringComparison.Ordinal);

        // Layout work must not weaken the presence truth or remove shop parity.
        Assert.Contains("presence === 'unknown' || !live", friends, StringComparison.Ordinal);
        Assert.Contains("note ?? CACHE_PRESENCE_NOTE", friends, StringComparison.Ordinal);
        Assert.Contains("Buy cheapest key", friends, StringComparison.Ordinal);
        Assert.Contains("void openDeals()", friends, StringComparison.Ordinal);
    }

    [Fact]
    public void FriendsPlayingActivity_UsesPortraitCoverArtAndKeepsActions()
    {
        var friends = ReadRepoFile("ui", "src", "components", "FriendsRoom.tsx");
        var tokens = ReadRepoFile("ui", "src", "tokens.css");
        var playingCard = SliceBetween(friends, "function PlayingCard(", "function LinkPicker(");
        var playingArt = SliceBetween(tokens, ".exo-friend-playing-art {", ".exo-friend-playing-copy {");

        Assert.Contains("<CoverArt game={artGame} className=\"h-full w-full\" />", playingCard, StringComparison.Ordinal);
        Assert.Contains("coverUrl: game.coverUrl ?? steamPlayingCoverUrl", playingCard, StringComparison.Ordinal);
        Assert.Contains("<HeroWash game={artGame} />", playingCard, StringComparison.Ordinal);
        Assert.Contains("exo-friend-playing-banner", playingCard, StringComparison.Ordinal);
        Assert.Contains("aspect-ratio: 2 / 3", playingArt, StringComparison.Ordinal);
        Assert.Contains("onClick={() => void run()}", playingCard, StringComparison.Ordinal);
        Assert.Contains("Buy cheapest key", playingCard, StringComparison.Ordinal);
        Assert.Contains("onClick={() => void openDeals()}", playingCard, StringComparison.Ordinal);
    }

    [Fact]
    public void Details_KeepLongTitlesAndUpscalerMetadataReadable()
    {
        var page = ReadRepoFile("ui", "src", "components", "GamePage.tsx");
        var tokens = ReadRepoFile("ui", "src", "tokens.css");

        Assert.Contains("exo-ghost-btn exo-buy-key", page, StringComparison.Ordinal);
        Assert.Contains("overflow-wrap: anywhere", SliceBetween(tokens, ".exo-game-title {", "}"), StringComparison.Ordinal);
        Assert.Contains("display: flex", SliceBetween(tokens, ".exo-game-status {", "}"), StringComparison.Ordinal);
        Assert.Contains("gap:", SliceBetween(tokens, ".exo-upscaler-id {", "}"), StringComparison.Ordinal);
        Assert.Contains("gap:", SliceBetween(tokens, ".exo-upscaler-meta {", "}"), StringComparison.Ordinal);
        Assert.Contains("min-width: 0", SliceBetween(tokens, ".exo-upscaler-row {", "}"), StringComparison.Ordinal);
        Assert.Contains("min-width: 0", SliceBetween(tokens, ".exo-card-meta {", "}"), StringComparison.Ordinal);
        Assert.Contains("white-space: normal", SliceBetween(tokens, ".exo-card-title {", "}"), StringComparison.Ordinal);
        Assert.DoesNotContain("text-overflow: ellipsis", SliceBetween(tokens, ".exo-card-title {", "}"), StringComparison.Ordinal);
    }

    [Fact]
    public void LibraryKeyboardAndSearch_DoNotLaunchFromChromeOrFlashFalseEmptyState()
    {
        var app = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "src", "components", "LauncherApp.tsx"));
        var browse = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "src", "components", "BrowseShelf.tsx"));
        var grid = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "src", "components", "WindowedGameGrid.tsx"));

        Assert.Contains("interactiveTarget", app, StringComparison.Ordinal);
        Assert.DoesNotContain("openGamePage(focused.id", app, StringComparison.Ordinal);
        Assert.Contains("moveGridFocusIndex", grid, StringComparison.Ordinal);
        Assert.Contains("key === 'ArrowDown'", grid, StringComparison.Ordinal);
        Assert.Contains("key === 'PageDown'", grid, StringComparison.Ordinal);
        Assert.DoesNotContain("onActivate?.()", grid, StringComparison.Ordinal);
        Assert.Contains("setView('library')", app, StringComparison.Ordinal);
        Assert.Contains("loading={catalogSearching}", app, StringComparison.Ordinal);
        Assert.Contains("loading ? 'Searching stores…'", browse, StringComparison.Ordinal);
    }

    [Fact]
    public void RetainedRooms_StopHiddenPollingAndAcceptEmptyLibraryUpdates()
    {
        var app = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "src", "components", "LauncherApp.tsx"));
        var friends = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "src", "components", "FriendsRoom.tsx"));
        var profile = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "src", "components", "ProfileRoom.tsx"));

        Assert.Contains("<FriendsRoom active={view === 'friends'} />", app, StringComparison.Ordinal);
        Assert.Contains("<ProfileRoom games={games} active={view === 'profile'} />", app, StringComparison.Ordinal);
        Assert.Contains("if (!active || showcaseGames.length === 0) return", profile, StringComparison.Ordinal);
        Assert.DoesNotContain("<AccountPanel", profile, StringComparison.Ordinal);
        Assert.Contains("if (!active) return", friends, StringComparison.Ordinal);
        Assert.DoesNotContain("payload?.games?.length", friends, StringComparison.Ordinal);
        Assert.DoesNotContain("d?.games?.length", app, StringComparison.Ordinal);
        Assert.DoesNotContain("payload?.games?.length", profile, StringComparison.Ordinal);
        Assert.DoesNotContain("host.getLibrary()", profile, StringComparison.Ordinal);
        Assert.DoesNotContain("onHostEvent('library.updated'", profile, StringComparison.Ordinal);
    }

    [Fact]
    public void CoalescedHostReads_DoNotCreateAnUnhandledFinallyPromise()
    {
        var host = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "src", "lib", "host.ts"));

        Assert.Contains("void work.then(clearInflight, clearInflight)", host, StringComparison.Ordinal);
        Assert.DoesNotContain("void work.finally", host, StringComparison.Ordinal);
    }

    [Fact]
    public void Chrome_UsesReactTitlebarNotHeroUi()
    {
        var chrome = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "src", "components", "WindowChrome.tsx"));
        var mark = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "src", "brand", "ExoMark.tsx"));
        var app = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "src", "components", "LauncherApp.tsx"));
        Assert.Contains("host.minimize()", chrome, StringComparison.Ordinal);
        Assert.Contains("host.maximize()", chrome, StringComparison.Ordinal);
        Assert.Contains("host.close()", chrome, StringComparison.Ordinal);
        Assert.Contains("exo-titlebar-button", chrome, StringComparison.Ordinal);
        Assert.Contains("export function ExoMark", mark, StringComparison.Ordinal);
        Assert.DoesNotContain("exo-nav-ink", app, StringComparison.Ordinal);
        Assert.Contains("placeholder=\"Search\"", app, StringComparison.Ordinal);
        Assert.DoesNotContain("searchFocused", app, StringComparison.Ordinal);
        Assert.DoesNotContain("@heroui/react", app, StringComparison.Ordinal);
        Assert.DoesNotContain("lucide-react", chrome, StringComparison.Ordinal);
    }

    [Fact]
    public void TitlebarSearch_StaysCompactAndDelaysItsCaretUntilExpansionFinishes()
    {
        var app = ReadRepoFile("ui", "src", "components", "LauncherApp.tsx");
        var tokens = ReadRepoFile("ui", "src", "tokens.css");
        var shell = SliceBetween(tokens, ".exo-titlebar-search {", ".exo-search-capsule {");
        var expanded = SliceBetween(tokens, ".exo-titlebar-search:focus-within .exo-search-capsule,", "@media (prefers-reduced-motion: reduce) {");
        var input = SliceBetween(tokens, ".exo-titlebar-search .exo-search {", ".exo-titlebar-search:focus-within .exo-search,");

        Assert.Contains("placeholder=\"Search\"", app, StringComparison.Ordinal);
        Assert.DoesNotContain("exo-search-glyph", app + tokens, StringComparison.Ordinal);
        Assert.Contains("width: 96px", shell, StringComparison.Ordinal);
        Assert.Contains("padding: 0", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("padding: 0 12px", shell, StringComparison.Ordinal);
        Assert.Contains("padding: 0 14px", input, StringComparison.Ordinal);
        Assert.Contains("width: 100%", expanded, StringComparison.Ordinal);
        Assert.Contains("width: 184px", expanded, StringComparison.Ordinal);
        Assert.Contains("width: 96px", tokens, StringComparison.Ordinal);
        Assert.Contains("width 200ms var(--ease-in-out)", tokens, StringComparison.Ordinal);
        Assert.Contains("caret-color: transparent", input, StringComparison.Ordinal);
        Assert.Contains("transition: caret-color 0s linear 200ms", input, StringComparison.Ordinal);
        Assert.DoesNotContain("box-shadow", expanded, StringComparison.Ordinal);
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

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = 0; (i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0; i += needle.Length)
            count++;
        return count;
    }

    private static string SliceBetween(string haystack, string start, string end)
    {
        var from = haystack.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, "missing " + start);
        var to = haystack.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to > from, "missing " + end);
        return haystack[from..(to + end.Length)];
    }
}
