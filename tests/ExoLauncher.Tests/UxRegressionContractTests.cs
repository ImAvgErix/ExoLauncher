using Xunit;

namespace ExoLauncher.Tests;

public sealed class UxRegressionContractTests
{
    [Fact]
    public void LauncherUi_DoesNotExposeExoProfilesOrAccountSync()
    {
        var launcher = ReadRepoFile("ui", "src", "components", "LauncherApp.tsx");
        var settings = ReadRepoFile("ui", "src", "components", "SettingsPanel.tsx");
        var host = ReadRepoFile("ui", "src", "lib", "host.ts");

        Assert.DoesNotContain("ProfileOverlay", launcher, StringComparison.Ordinal);
        Assert.DoesNotContain("ProfileButton", launcher + settings, StringComparison.Ordinal);
        Assert.DoesNotContain("Exo Profile", launcher + settings, StringComparison.Ordinal);
        Assert.DoesNotContain("exo.status", host, StringComparison.Ordinal);
        Assert.DoesNotContain("exo.signIn", host, StringComparison.Ordinal);
        Assert.DoesNotContain("exo.signOut", host, StringComparison.Ordinal);
        Assert.DoesNotContain("exo.updateProfile", host, StringComparison.Ordinal);
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

        // Warm covers immediately so the library is not monogram-heavy after first paint.
        Assert.Contains("deferForFirstPaint: false", library, StringComparison.Ordinal);
        Assert.Contains("g.Installed || g.IsFavorite", library, StringComparison.Ordinal);
        Assert.Contains("Task.Delay(TimeSpan.FromSeconds(20))", library, StringComparison.Ordinal);
        Assert.Contains("FirstPaintCoverWarmDelay = TimeSpan.FromMilliseconds(50)", covers, StringComparison.Ordinal);
        Assert.Contains("private static readonly HttpClient CoverHttp", covers, StringComparison.Ordinal);
        Assert.Contains("BackgroundWarmConcurrency = 8", covers, StringComparison.Ordinal);
        Assert.Contains("requested ? RequestedWarmConcurrency : BackgroundWarmConcurrency", covers, StringComparison.Ordinal);
        Assert.Contains("PERF startup milestone=", window, StringComparison.Ordinal);
        Assert.Contains("webview-core-ready", window, StringComparison.Ordinal);
        Assert.Contains("webview-navigation-complete", window, StringComparison.Ordinal);
    }

    [Fact]
    public void CoverWarm_ResolvesSteamAppIdByTitle_ForEveryNonSteamStore()
    {
        var covers = ReadRepoFile("ExoLauncher", "Services", "CoverArtService.cs");
        var library = ReadRepoFile("ExoLauncher", "Services", "LibraryService.cs");

        // Title→Steam lookup must actually run during warm, not sit unused.
        Assert.Contains("await ResolveSteamAppIdByTitleAsync", covers, StringComparison.Ordinal);
        Assert.Contains("ShouldWarmLibraryCover", library, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "game.Installed || game.IsFavorite",
            library,
            StringComparison.Ordinal);
        Assert.Contains("GogCoverCandidateUrls", covers, StringComparison.Ordinal);
        Assert.DoesNotContain("{gogId}_product_tile_256_2x.jpg", covers, StringComparison.Ordinal);
    }

    [Fact]
    public void GogAuth_UsesTrustedWebCallback_WithoutPersistentSettingsUi()
    {
        var service = ReadRepoFile("ExoLauncher", "Services", "GogAuthService.cs");
        var helper = ReadRepoFile("ExoLauncher", "Adapters", "Cli", "GogdlCli.cs");
        var adapter = ReadRepoFile("ExoLauncher", "Adapters", "GogAdapter.cs");
        var launcher = ReadRepoFile("ui", "src", "components", "LauncherApp.tsx");

        Assert.Contains("CoreWebView2Environment.CreateWithOptionsAsync", service, StringComparison.Ordinal);
        Assert.Contains("NavigationStarting", service, StringComparison.Ordinal);
        Assert.Contains("AuthConfigPath + \".pending-\"", service, StringComparison.Ordinal);
        Assert.Contains("File.Move(pendingPath, AuthConfigPath, overwrite: true)", service, StringComparison.Ordinal);
        Assert.Contains("origin", helper, StringComparison.Ordinal);
        Assert.Contains("embed.gog.com", helper, StringComparison.Ordinal);
        Assert.Contains("GogdlCli.WithAuthConfig", adapter, StringComparison.Ordinal);
        Assert.DoesNotContain("StartAuthConsole(gogdl", adapter, StringComparison.Ordinal);
        Assert.DoesNotContain("runStoreAuth", launcher, StringComparison.Ordinal);
    }

    [Fact]
    public void StoreOpen_IsAsyncAndKeepsRevealingColdClients()
    {
        var text = ReadRepoFile("ExoLauncher", "Services", "WebHostBridge.cs");
        Assert.Contains("\"shell.showStore\" => await ShowStoreAsync", text, StringComparison.Ordinal);
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
        var detail = ReadRepoFile("ui", "src", "components", "DetailPanel.tsx");
        var launcher = ReadRepoFile("ui", "src", "components", "LauncherApp.tsx");
        var bridge = ReadRepoFile("ExoLauncher", "Services", "WebHostBridge.cs");

        Assert.Contains("game.installed || game.canInstall || game.owned", detail, StringComparison.Ordinal);
        Assert.Contains("score: smartSearchScore(game.title, q)", launcher, StringComparison.Ordinal);
        Assert.DoesNotContain("smartSearchScore(game.store, q)", launcher, StringComparison.Ordinal);
        Assert.Contains("if (!proven.Owned) return null", bridge, StringComparison.Ordinal);
        Assert.Contains("title:${titleKey}", launcher, StringComparison.Ordinal);
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
        var detail = ReadRepoFile("ui", "src", "components", "DetailPanel.tsx");
        var steam = ReadRepoFile("ExoLauncher", "Adapters", "SteamAdapter.cs");
        var riot = ReadRepoFile("ExoLauncher", "Adapters", "RiotAdapter.cs");
        var automator = ReadRepoFile("ExoLauncher", "Adapters", "StoreUninstallPromptAutomator.cs");
        Assert.DoesNotContain("window.confirm", detail, StringComparison.Ordinal);
        Assert.Contains("SteamClientIpc.Command", steam, StringComparison.Ordinal);
        Assert.DoesNotContain("SteamUninstallPromptAutomator", steam, StringComparison.Ordinal);
        Assert.DoesNotContain("StoreUninstallPromptAutomator", steam, StringComparison.Ordinal);
        Assert.Contains("StoreUninstallPromptAutomator", riot, StringComparison.Ordinal);
        Assert.DoesNotContain("$pid =", automator, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SetForegroundWindow", automator, StringComparison.Ordinal);
        Assert.DoesNotContain("mouse_event", automator, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("normalizedContext.Contains($normalizedTitle)", automator, StringComparison.Ordinal);
    }

    [Fact]
    public void TrophyNotifications_AreNativeCustomizableAndStayOutOfTheGameProcess()
    {
        var broker = ReadRepoFile("ExoLauncher", "Services", "TrophyNotificationService.cs");
        var presenter = ReadRepoFile("ExoLauncher", "Services", "TrophyNotificationPresenter.cs");
        var bridge = ReadRepoFile("ExoLauncher", "Services", "WebHostBridge.cs");
        var settings = ReadRepoFile("ui", "src", "components", "TrophyNotificationSettings.tsx");
        var styles = ReadRepoFile("ui", "src", "components", "TrophyNotificationSettings.css");

        Assert.Contains("Session-bound notification broker", broker, StringComparison.Ordinal);
        Assert.Contains("WsExNoActivate", presenter, StringComparison.Ordinal);
        Assert.Contains("IsShownInSwitchers = false", presenter, StringComparison.Ordinal);
        Assert.Contains("IsAlwaysOnTop = true", presenter, StringComparison.Ordinal);
        Assert.Contains("NotificationWidth = 432", presenter, StringComparison.Ordinal);
        Assert.Contains("NotificationHeight = 122", presenter, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromMilliseconds(4200)", presenter, StringComparison.Ordinal);
        Assert.Contains("CornerRadius = new CornerRadius(12)", presenter, StringComparison.Ordinal);
        Assert.Contains("Width = 64", presenter, StringComparison.Ordinal);
        Assert.Contains("Height = 64", presenter, StringComparison.Ordinal);
        Assert.Contains("TryGetSafeIconUri", presenter, StringComparison.Ordinal);
        Assert.Contains("new BitmapImage(uri)", presenter, StringComparison.Ordinal);
        Assert.Contains("ImageFailed", presenter, StringComparison.Ordinal);
        Assert.Contains("Uri.UriSchemeHttps", presenter, StringComparison.Ordinal);
        Assert.Contains("Text = \"Unlocked\"", presenter, StringComparison.Ordinal);
        Assert.DoesNotContain("EXO // UNLOCKED", presenter, StringComparison.Ordinal);
        Assert.Contains("AnimateIn(card)", presenter, StringComparison.Ordinal);
        Assert.DoesNotContain("TrophyMotion", presenter, StringComparison.Ordinal);
        Assert.Contains("window.Content = card", presenter, StringComparison.Ordinal);
        Assert.DoesNotContain("window.Content = new Border", presenter, StringComparison.Ordinal);
        Assert.Contains("\"ScaleX\", 0.94, 1, 340", presenter, StringComparison.Ordinal);
        Assert.DoesNotContain("\"TranslateX\", motion.X", presenter, StringComparison.Ordinal);
        Assert.DoesNotContain("\"TranslateY\", motion.Y", presenter, StringComparison.Ordinal);
        Assert.Contains("BeginCloseCurrent", presenter, StringComparison.Ordinal);
        Assert.Contains("OnExitAnimationCompleted", presenter, StringComparison.Ordinal);
        Assert.Contains("CompleteCloseCurrent", presenter, StringComparison.Ordinal);
        Assert.Contains("Window? pendingWindow = null", presenter, StringComparison.Ordinal);
        Assert.Contains("pendingWindow?.Close()", presenter, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildCollectibleBadge", presenter, StringComparison.Ordinal);
        Assert.DoesNotContain("Gradient(", presenter, StringComparison.Ordinal);
        Assert.DoesNotContain("EXO  /", presenter, StringComparison.Ordinal);
        Assert.Contains("\"trophies.preview\"", bridge, StringComparison.Ordinal);
        Assert.Contains("Achievement notifications", settings, StringComparison.Ordinal);
        Assert.Contains("<i>Unlocked</i>", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("EXO // UNLOCKED", settings, StringComparison.Ordinal);
        Assert.Contains("const anchors = [", settings, StringComparison.Ordinal);
        Assert.Contains("role=\"radiogroup\"", settings, StringComparison.Ordinal);
        Assert.Contains("trophyNotificationPosition: anchor.id", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("onPointerMove", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("trophyNotificationSoundCue", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("trophyNotificationDurationSeconds", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("trophyNotificationPreset", settings, StringComparison.Ordinal);
        Assert.Contains("UISettings().AnimationsEnabled", presenter, StringComparison.Ordinal);
        Assert.Contains(".exo-trophy-anchor-grid", styles, StringComparison.Ordinal);
        Assert.Contains(".exo-trophy-preview-stage", styles, StringComparison.Ordinal);
        Assert.Contains(".exo-trophy-preview-card.is-top-left", styles, StringComparison.Ordinal);
        Assert.Contains(".exo-trophy-preview-card.is-bottom-right", styles, StringComparison.Ordinal);
        Assert.Contains("min-height: 248px", styles, StringComparison.Ordinal);
        Assert.Contains("exo-trophy-preview-arrive", styles, StringComparison.Ordinal);
        Assert.Contains("TrophyRarityResolver.Label", presenter, StringComparison.Ordinal);
        Assert.Contains("DwmSetWindowAttribute", presenter, StringComparison.Ordinal);
        Assert.Contains("NotificationWidth = 432", presenter, StringComparison.Ordinal);
        Assert.Contains("NotificationHeight = 122", presenter, StringComparison.Ordinal);
        Assert.Contains("NotificationDuration = TimeSpan.FromMilliseconds(4200)", presenter, StringComparison.Ordinal);
        Assert.Contains("width: min(432px", styles, StringComparison.Ordinal);
        Assert.Contains("position: absolute", styles, StringComparison.Ordinal);
        Assert.Contains("height: 122px", styles, StringComparison.Ordinal);
        Assert.DoesNotContain("grid-area:", styles, StringComparison.Ordinal);
        Assert.Contains("top: 8px; left: 8px", styles, StringComparison.Ordinal);
        Assert.Contains("right: 8px; bottom: 8px", styles, StringComparison.Ordinal);
        Assert.Contains("padding: 14px", styles, StringComparison.Ordinal);
        Assert.Contains("border-radius: 2px", styles, StringComparison.Ordinal);
        Assert.Contains("transform: scale(.94)", styles, StringComparison.Ordinal);
        Assert.DoesNotContain("--trophy-enter-x", styles, StringComparison.Ordinal);
        Assert.DoesNotContain("--trophy-enter-y", styles, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: 4px 64px minmax(0, 1fr) auto", styles, StringComparison.Ordinal);
        Assert.Contains("TrophySurface", presenter, StringComparison.Ordinal);
        Assert.Contains("DecodePixelWidth = 128", presenter, StringComparison.Ordinal);
        Assert.DoesNotContain("SetWindowsHookEx", broker + presenter, StringComparison.Ordinal);
        Assert.DoesNotContain("WriteProcessMemory", broker + presenter, StringComparison.Ordinal);
    }

    [Fact]
    public void PrimaryActions_MorphProgressInPlaceWithoutLayoutAnimation()
    {
        var detail = ReadRepoFile("ui", "src", "components", "DetailPanel.tsx");
        var launcher = ReadRepoFile("ui", "src", "components", "LauncherApp.tsx");
        var settings = ReadRepoFile("ui", "src", "components", "SettingsPanel.tsx");
        var tokens = ReadRepoFile("ui", "src", "tokens.css");
        var actions = detail + launcher + settings;

        Assert.Contains("exo-action-progress", detail, StringComparison.Ordinal);
        Assert.Contains("exo-action-progress", launcher, StringComparison.Ordinal);
        Assert.Contains("exo-action-progress", settings, StringComparison.Ordinal);
        Assert.Contains("exo-action-state", actions, StringComparison.Ordinal);
        Assert.Contains("exo-action-idle", actions, StringComparison.Ordinal);
        Assert.Contains("exo-action-active", actions, StringComparison.Ordinal);
        Assert.Contains("if (selectedProgress.canCancel) onCancel()", detail, StringComparison.Ordinal);
        Assert.Contains("!!selectedProgress && !selectedProgress.canCancel", detail, StringComparison.Ordinal);
        Assert.Contains("role=\"status\"", actions, StringComparison.Ordinal);
        Assert.DoesNotContain("className=\"exo-status", actions, StringComparison.Ordinal);
        Assert.DoesNotContain("exo-progress-track", actions + tokens, StringComparison.Ordinal);
        Assert.DoesNotContain("exo-progress-fill", actions + tokens, StringComparison.Ordinal);
        Assert.DoesNotContain("transition-[width]", actions, StringComparison.Ordinal);
        Assert.Contains("transform: scaleX(var(--progress));", tokens, StringComparison.Ordinal);
        Assert.Contains("transition: transform 360ms linear;", tokens, StringComparison.Ordinal);
        Assert.Contains("transform: translateY(3px);", tokens, StringComparison.Ordinal);
        Assert.Contains("transform: translateY(-3px);", tokens, StringComparison.Ordinal);
        Assert.Contains("@media (prefers-reduced-motion: reduce)", tokens, StringComparison.Ordinal);
    }

    [Fact]
    public void RefreshAndSyncStayAutomaticWithoutVisibleTitlebarAffordances()
    {
        var launcher = ReadRepoFile("ui", "src", "components", "LauncherApp.tsx");
        var settings = ReadRepoFile("ui", "src", "components", "SettingsPanel.tsx");
        var onboarding = ReadRepoFile("ui", "src", "components", "OnboardingPanel.tsx");

        Assert.DoesNotContain("RefreshCw", launcher + settings, StringComparison.Ordinal);
        Assert.DoesNotContain("title=\"Refresh", launcher + settings, StringComparison.Ordinal);
        Assert.DoesNotContain("onRefresh", settings, StringComparison.Ordinal);
        Assert.Contains("e.key === 'F5'", launcher, StringComparison.Ordinal);
        Assert.DoesNotContain("onRefreshStores", launcher + onboarding, StringComparison.Ordinal);
        Assert.DoesNotContain("Rescan", onboarding, StringComparison.Ordinal);
        Assert.Contains("statusMsg && statusGameId === null", launcher, StringComparison.Ordinal);
        Assert.Contains("role=\"alert\"", launcher, StringComparison.Ordinal);
        Assert.Contains("const card = cardForExactId(games, progress.gameId)", launcher, StringComparison.Ordinal);
        Assert.Contains("setSelectedVariantId(card && card.id !== progress.gameId ? progress.gameId : null)", launcher, StringComparison.Ordinal);

        var callbackStart = launcher.IndexOf("onSettings={(next) => {", StringComparison.Ordinal);
        var callbackEnd = launcher.IndexOf("}}", callbackStart, StringComparison.Ordinal);
        Assert.True(callbackStart >= 0 && callbackEnd > callbackStart);
        Assert.DoesNotContain("loadLibrary", launcher[callbackStart..callbackEnd], StringComparison.Ordinal);
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
    public void SettingsPanel_UsesDenseDividedRowsAndKeepsFunctionalActions()
    {
        var settings = ReadRepoFile("ui", "src", "components", "SettingsPanel.tsx");

        Assert.Contains("max-w-[1280px]", settings, StringComparison.Ordinal);
        Assert.Contains("xl:grid-cols-[1.15fr_0.85fr]", settings, StringComparison.Ordinal);
        Assert.Contains("md:grid-cols-[0.9fr_1.1fr]", settings, StringComparison.Ordinal);
        Assert.Contains("divide-y divide-line-soft", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("Launcher settings", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("Preferences", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("Stores, updates, and notifications.", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("Overlapping local sessions", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("Quiet Game Mode keeps", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("Open a client only when", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("rounded-2xl border border-line-soft bg-elevated p-5", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("Exo Profile", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("type=\"password\"", settings, StringComparison.Ordinal);
        Assert.Contains("onCheckUpdate", settings, StringComparison.Ordinal);
        Assert.Contains("onInstallUpdate", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("Connect", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("Reconnect", settings, StringComparison.Ordinal);
        Assert.Contains("host.pickFolder('Choose game folder')", settings, StringComparison.Ordinal);
        Assert.Contains("<TrophyNotificationSettings", settings, StringComparison.Ordinal);
        Assert.Contains("https://github.com/ImAvgErix/ExoLauncher/issues", settings, StringComparison.Ordinal);
        Assert.Contains("https://www.buymeacoffee.com/UhhErix", settings, StringComparison.Ordinal);
        Assert.Contains("PRIVACY", settings, StringComparison.Ordinal);
    }

    [Fact]
    public void StoreLists_OnlyRenderPresentOfficialClients()
    {
        var settings = ReadRepoFile("ui", "src", "components", "SettingsPanel.tsx");
        var onboarding = ReadRepoFile("ui", "src", "components", "OnboardingPanel.tsx");
        var helper = ReadRepoFile("ui", "src", "lib", "storeClients.ts");
        var library = ReadRepoFile("ExoLauncher", "Services", "LibraryService.cs");
        var panels = settings + onboarding;

        Assert.Contains("store.clientPresent === true", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("agentPresent", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("??", helper, StringComparison.Ordinal);
        Assert.Contains("presentStoreClients", settings, StringComparison.Ordinal);
        Assert.Contains("presentStoreClients", onboarding, StringComparison.Ordinal);
        Assert.Contains("storeRows.length > 0", settings, StringComparison.Ordinal);
        Assert.Contains("rows.length > 0", onboarding, StringComparison.Ordinal);
        Assert.DoesNotContain("clientPresent ??", panels, StringComparison.Ordinal);
        Assert.DoesNotContain("displayName: 'Steam'", panels, StringComparison.Ordinal);
        Assert.DoesNotContain("'Not installed'", panels, StringComparison.Ordinal);
        Assert.DoesNotContain("stores.length ? stores", panels, StringComparison.Ordinal);
        Assert.DoesNotContain("While playing", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("Minimize Exo", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("onAuth", panels, StringComparison.Ordinal);
        Assert.DoesNotContain("Reconnect", panels, StringComparison.Ordinal);

        Assert.Contains("if (!present) return \"Not installed\";", library, StringComparison.Ordinal);
        Assert.DoesNotContain("if (!present) return \"Missing\";", library, StringComparison.Ordinal);
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
    public void GameDetail_IsCompactAndRefreshesMissingAchievementsAutomatically()
    {
        var detail = ReadRepoFile("ui", "src", "components", "DetailPanel.tsx");
        var card = ReadRepoFile("ui", "src", "components", "GameCard.tsx");
        var launcher = ReadRepoFile("ui", "src", "components", "LauncherApp.tsx");
        var host = ReadRepoFile("ui", "src", "lib", "host.ts");
        var tokens = ReadRepoFile("ui", "src", "tokens.css");
        var bridge = ReadRepoFile("ExoLauncher", "Services", "WebHostBridge.cs");
        var getStart = bridge.IndexOf("private object AchievementGet", StringComparison.Ordinal);
        var getEnd = bridge.IndexOf("private async Task<object> AchievementRefreshAsync", getStart,
            StringComparison.Ordinal);
        Assert.True(getStart >= 0 && getEnd > getStart);
        var achievementGet = bridge[getStart..getEnd];

        Assert.DoesNotContain("Trophy cabinet", detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("achievementBusy", detail, StringComparison.Ordinal);
        // Immediate paint from last known snapshot, then live account-scoped refresh.
        // requestId freezes the selected source so a fast switch cannot paint the wrong counts.
        Assert.Contains("const requestId = selected.id", detail, StringComparison.Ordinal);
        Assert.Contains("host.getAchievements(requestId)", detail, StringComparison.Ordinal);
        Assert.Contains("host.refreshAchievements(requestId)", detail, StringComparison.Ordinal);
        Assert.Contains("achievementCache.get(requestId)", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("setAchievementData(null)", detail, StringComparison.Ordinal);
        Assert.Contains("[selected.id]", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("selected.launchTarget, selected.store", detail, StringComparison.Ordinal);
        Assert.Contains("achievements.updated", detail, StringComparison.Ordinal);
        Assert.Contains("GetLatestSnapshot", achievementGet, StringComparison.Ordinal);
        Assert.DoesNotContain("achievementRefreshing", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("Checking…", detail, StringComparison.Ordinal);
        Assert.Contains("? 'Updating…'", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("Syncing…", detail, StringComparison.Ordinal);
        Assert.Contains("exo-game-page", detail, StringComparison.Ordinal);
        Assert.Contains("exo-game-close", detail, StringComparison.Ordinal);
        Assert.Contains("const primaryAction = resolvePrimaryAction(game)", card, StringComparison.Ordinal);
        Assert.Contains("game.variants?.some((variant) => variant.updateAvailable)", card, StringComparison.Ordinal);
        Assert.Contains("primaryAction === 'update'", card, StringComparison.Ordinal);
        Assert.Contains("primaryAction === 'install'", card, StringComparison.Ordinal);
        Assert.DoesNotContain("exo-badge is-warn", card, StringComparison.Ordinal);
        Assert.Contains("exo-badge is-update", card, StringComparison.Ordinal);
        Assert.Contains("hasUpdate && !isPlaying && 'is-update'", card, StringComparison.Ordinal);
        Assert.Contains("hasUpdate ? 'Update'", card, StringComparison.Ordinal);
        Assert.DoesNotContain("game.updateAvailable &&", card, StringComparison.Ordinal);
        Assert.DoesNotContain("formatPlaytime", card, StringComparison.Ordinal);
        Assert.Contains("label=\"Time played\"", detail, StringComparison.Ordinal);
        Assert.Contains("label=\"Last launched\"", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("label=\"Played\"", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("label=\"Last\"", detail, StringComparison.Ordinal);
        Assert.Contains("formatPlaytime(bestPlaytimeMinutes(selected), selected.lastPlayedUtc)", detail, StringComparison.Ordinal);
        Assert.Contains("formatRelativeLastPlayed(selected.lastPlayedUtc)", detail, StringComparison.Ordinal);
        Assert.Contains("'None'", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("label=\"Status\"", detail, StringComparison.Ordinal);
        Assert.Contains("selected.canStop", detail, StringComparison.Ordinal);
        Assert.Contains("? 'Stop'", detail, StringComparison.Ordinal);
        Assert.Contains("if (selected?.canStop)", launcher, StringComparison.Ordinal);
        Assert.Contains("await onStopGame(selected)", launcher, StringComparison.Ordinal);
        Assert.Contains("function setExactRunState", launcher, StringComparison.Ordinal);
        Assert.Contains("setExactRunState(items, game.id, false, false)", launcher, StringComparison.Ordinal);
        Assert.Contains("mergeExactGame(items, refreshed.game!)", launcher, StringComparison.Ordinal);
        Assert.Contains("void host.getGame(exactId)", launcher, StringComparison.Ordinal);
        Assert.DoesNotContain("const refreshed = await host.getGame(exactId)", launcher, StringComparison.Ordinal);
        Assert.Contains(".exo-badge.is-warn", tokens, StringComparison.Ordinal);
        Assert.Contains(".exo-badge.is-update", tokens, StringComparison.Ordinal);
        Assert.DoesNotContain("background: #ffb020", tokens, StringComparison.Ordinal);
        Assert.DoesNotContain("font-weight: 800", tokens, StringComparison.Ordinal);
        Assert.Contains("box-shadow:", tokens, StringComparison.Ordinal);
        Assert.Contains("closeDisabled={actionLocked}", launcher, StringComparison.Ordinal);
        Assert.Contains("function isCardActionLocked", launcher, StringComparison.Ordinal);
        Assert.Contains("disabled={isCardActionLocked(game)}", launcher, StringComparison.Ordinal);
        Assert.True(
            host.IndexOf("if (game.installed && game.updateAvailable) return 'update'", StringComparison.Ordinal) <
            host.IndexOf("if (game.primaryAction === 'play'", StringComparison.Ordinal),
            "Update availability must override a stale explicit Play action.");
        Assert.True(
            host.IndexOf("if (game.canInstall || game.owned) return 'install'", StringComparison.Ordinal) <
            host.IndexOf("if (game.primaryAction === 'play'", StringComparison.Ordinal),
            "Owned or installable titles must Download, not inherit a stale None/Buy action.");
    }

    [Fact]
    public void Cards_UseAllStoreVariantsWithHonestLabelsAndHighQualityCoverSources()
    {
        var card = ReadRepoFile("ui", "src", "components", "GameCard.tsx");
        var cover = ReadRepoFile("ui", "src", "components", "CoverArt.tsx");
        var host = ReadRepoFile("ui", "src", "lib", "host.ts");
        var motion = ReadRepoFile("ui", "src", "motion.tsx");
        var tokens = ReadRepoFile("ui", "src", "tokens.css");

        Assert.Contains("stores?: Array<StoreId | string>", host, StringComparison.Ordinal);
        Assert.Contains("game.stores?.length ? game.stores : [game.store]", card, StringComparison.Ordinal);
        Assert.Contains("new Set", card, StringComparison.Ordinal);
        Assert.Contains("stores.map(storeLabel).join(', ')", card, StringComparison.Ordinal);
        Assert.Contains("exo-card-store", card, StringComparison.Ordinal);
        Assert.DoesNotContain("exo-store-dot", card, StringComparison.Ordinal);
        Assert.DoesNotContain("StoreMark", card, StringComparison.Ordinal);
        Assert.Contains("steamPortraitUrlsForApp", cover, StringComparison.Ordinal);
        Assert.Contains("loading={large ? 'eager' : 'lazy'}", cover, StringComparison.Ordinal);
        Assert.Contains("opacity: loaded ? 1 : 0.02", cover, StringComparison.Ordinal);
        Assert.DoesNotContain("opacity-0 pointer-events-none", cover, StringComparison.Ordinal);
        Assert.Contains("raw.replace('library_600x900_2x', 'library_600x900')", cover, StringComparison.Ordinal);
        Assert.DoesNotContain("raw.replace('library_600x900', 'library_600x900_2x')", cover, StringComparison.Ordinal);
        Assert.Contains("/library_600x900.jpg", cover, StringComparison.Ordinal);
        Assert.Contains("library_hero_2x.jpg", cover, StringComparison.Ordinal);
        Assert.True(
            cover.IndexOf("/library_600x900.jpg", StringComparison.Ordinal) <
            cover.IndexOf("/library_600x900_2x.jpg", StringComparison.Ordinal),
            "Grid tiles must request the 600×900 poster before the 2x bitmap.");
        Assert.Contains("fit === 'banner'", cover, StringComparison.Ordinal);
        Assert.Contains("width < 240 || height < 360", cover, StringComparison.Ordinal);
        Assert.Contains("BannerIn", motion, StringComparison.Ordinal);
        Assert.Contains("onExitComplete", motion, StringComparison.Ordinal);
        Assert.DoesNotContain("exo-overlay-in", tokens, StringComparison.Ordinal);
        Assert.DoesNotContain("PosterMorph", motion, StringComparison.Ordinal);
        Assert.DoesNotContain("layoutId=", motion, StringComparison.Ordinal);
        Assert.DoesNotContain("PosterMorph", card, StringComparison.Ordinal);
        Assert.Contains("onMouseDown", card, StringComparison.Ordinal);
        Assert.Contains("preventDefault()", card, StringComparison.Ordinal);
        Assert.DoesNotContain("layoutId", ReadRepoFile("ui", "src", "components", "DetailPanel.tsx"), StringComparison.Ordinal);
        Assert.DoesNotContain("LayoutGroup", ReadRepoFile("ui", "src", "components", "LauncherApp.tsx"), StringComparison.Ordinal);
        Assert.Contains("translateY(-5px)", tokens, StringComparison.Ordinal);
        Assert.DoesNotContain("cardSpring", motion, StringComparison.Ordinal);
        Assert.DoesNotContain("stiffness:", motion, StringComparison.Ordinal);
        Assert.DoesNotContain("willChange:", motion, StringComparison.Ordinal);
        Assert.DoesNotContain("background: #ffb020", tokens, StringComparison.Ordinal);
        Assert.DoesNotContain(".group:hover .exo-cover:not(.is-not-installed)", tokens, StringComparison.Ordinal);
        Assert.Contains(".exo-tile-frame:hover .exo-tile-shine", tokens, StringComparison.Ordinal);
        Assert.DoesNotContain(".exo-tile:hover .exo-tile-shine", tokens, StringComparison.Ordinal);
        Assert.DoesNotContain(".exo-continue", tokens, StringComparison.Ordinal);
    }

    [Fact]
    public void GroupedStoreCards_ExposeAnExactSourcePickerForAllActions()
    {
        var host = ReadRepoFile("ui", "src", "lib", "host.ts");
        var detail = ReadRepoFile("ui", "src", "components", "DetailPanel.tsx");
        var launcher = ReadRepoFile("ui", "src", "components", "LauncherApp.tsx");
        var card = ReadRepoFile("ui", "src", "components", "GameCard.tsx");
        var bridge = ReadRepoFile("ExoLauncher", "Services", "WebHostBridge.cs");

        Assert.Contains("export interface GameVariant", host, StringComparison.Ordinal);
        Assert.Contains("variants?: GameVariant[]", host, StringComparison.Ordinal);
        Assert.Contains("Choose game source", detail, StringComparison.Ordinal);
        Assert.Contains("onSelectSource?: (id: string) => void", detail, StringComparison.Ordinal);
        Assert.Contains("selected.variants.map", detail, StringComparison.Ordinal);
        Assert.Contains("function materializeVariant", launcher, StringComparison.Ordinal);
        Assert.Contains("const [selectedVariantId", launcher, StringComparison.Ordinal);
        Assert.Contains("void host.getGame(exactId)", launcher, StringComparison.Ordinal);
        Assert.Contains("onSelectSource={(id) =>", launcher, StringComparison.Ordinal);
        Assert.Contains("g.variants?.some((variant) => variant.id === id)", launcher, StringComparison.Ordinal);
        Assert.Contains("setExactRunState(items, d.gameId!, false, false)", launcher, StringComparison.Ordinal);
        Assert.Contains("const card = cardForExactId(games, selectedId)", launcher, StringComparison.Ordinal);
        Assert.Contains("setSelectedVariantId(retainedVariant)", launcher, StringComparison.Ordinal);
        Assert.Contains("PeekCachedLibrary()", bridge, StringComparison.Ordinal);
        Assert.Contains("TryLibraryOwnedSource", bridge, StringComparison.Ordinal);
        Assert.DoesNotContain("ContinueBanner", launcher, StringComparison.Ordinal);
        Assert.Contains("catalogHits.find", launcher, StringComparison.Ordinal);
        Assert.Contains("hit.owned || hit.canInstall", launcher, StringComparison.Ordinal);
        Assert.Contains("function mergeHostGames", launcher, StringComparison.Ordinal);
        Assert.Contains("function findLibraryGame", launcher, StringComparison.Ordinal);
        Assert.Contains("function findLibraryGameByTitle", launcher, StringComparison.Ordinal);
        Assert.Contains("isSearchableLibraryGame", launcher, StringComparison.Ordinal);
        Assert.Contains("className=\"exo-boot\"", launcher, StringComparison.Ordinal);
        Assert.DoesNotContain("Scanning libraries", launcher, StringComparison.Ordinal);
        Assert.DoesNotContain("Starting…", launcher, StringComparison.Ordinal);
        Assert.DoesNotContain("['recent', 'Recent']", launcher, StringComparison.Ordinal);
        Assert.DoesNotContain("['played', 'Played']", launcher, StringComparison.Ordinal);
        Assert.DoesNotContain("exo-rail-count", launcher, StringComparison.Ordinal);
        Assert.DoesNotContain("setLibrarySort", launcher, StringComparison.Ordinal);
        Assert.Contains("showGamePage", launcher, StringComparison.Ordinal);
        Assert.Contains("exo-game-overlay", launcher, StringComparison.Ordinal);
        Assert.Contains("exo-game-overlay-scrim", launcher, StringComparison.Ordinal);
        Assert.Contains("inert={overlayLock ? true : undefined}", launcher, StringComparison.Ordinal);
        Assert.Contains("onExitComplete", launcher, StringComparison.Ordinal);
        Assert.Contains("displayedGame", launcher, StringComparison.Ordinal);
        Assert.DoesNotContain("exo-game-page-photo", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("exo-game-page-wash", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("coverBg(selected)", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("style={{ background: coverBg(game) }}", card, StringComparison.Ordinal);
        Assert.Contains("confirmedEmpty", bridge, StringComparison.Ordinal);
        Assert.DoesNotContain("|| snapshot.Coverage == AchievementCoverageStatus.Complete", bridge, StringComparison.Ordinal);
    }

    [Fact]
    public void PinnedRail_WrapsAndLibraryExcludesPinned()
    {
        var launcher = ReadRepoFile("ui", "src", "components", "LauncherApp.tsx");
        var tokens = ReadRepoFile("ui", "src", "tokens.css");
        var card = ReadRepoFile("ui", "src", "components", "GameCard.tsx");

        Assert.Contains("className=\"exo-pin-track\"", launcher, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Pinned games\"", launcher, StringComparison.Ordinal);
        Assert.Contains("libraryGames.filter((g) => !pinnedIds.has(g.id) && g.id !== nowId)", launcher, StringComparison.Ordinal);
        Assert.Contains("libraryGames.filter(", launcher, StringComparison.Ordinal);
        Assert.Contains("g.installed || isInstallingGame(g, installingId)", launcher, StringComparison.Ordinal);
        Assert.Contains("catalogHitIsPresent", launcher, StringComparison.Ordinal);
        Assert.Contains("pickNow", launcher, StringComparison.Ordinal);
        Assert.Contains("NowStage", launcher, StringComparison.Ordinal);
        Assert.DoesNotContain("g.installed || g.owned || g.canInstall", launcher, StringComparison.Ordinal);
        Assert.Contains("game.installed || game.owned || game.canInstall", launcher, StringComparison.Ordinal);
        Assert.DoesNotContain("onWheel={(event)", launcher, StringComparison.Ordinal);
        Assert.DoesNotContain("exo-pinned-edge", launcher, StringComparison.Ordinal);
        Assert.DoesNotContain("exo-card-play", card, StringComparison.Ordinal);
        Assert.Contains(".exo-pin-track", tokens, StringComparison.Ordinal);
        Assert.Contains(".exo-tile-shine", tokens, StringComparison.Ordinal);
        Assert.Contains("padding: 0 16px 20px", tokens, StringComparison.Ordinal);
        Assert.Contains("minmax(116px, 128px)", tokens, StringComparison.Ordinal);
        Assert.Contains("minmax(156px, 1fr)", tokens, StringComparison.Ordinal);
        Assert.Contains("object-position: center", tokens, StringComparison.Ordinal);
        Assert.Contains("left: 50%", tokens, StringComparison.Ordinal);
        Assert.Contains("width: 6ch", tokens, StringComparison.Ordinal);
        Assert.Contains("width: 18ch", tokens, StringComparison.Ordinal);
        Assert.Contains(".exo-titlebar-search.is-open", tokens, StringComparison.Ordinal);
        Assert.Contains("exo-titlebar-search exo-no-drag", launcher, StringComparison.Ordinal);
        Assert.Contains("exo-search-glyph", launcher, StringComparison.Ordinal);
        Assert.Contains(".exo-search:focus::placeholder", tokens, StringComparison.Ordinal);
        Assert.DoesNotContain("exo-search-spin", launcher, StringComparison.Ordinal);
        Assert.DoesNotContain(".exo-continue", tokens, StringComparison.Ordinal);
        Assert.Contains(".exo-boot", tokens, StringComparison.Ordinal);
        Assert.Contains("exo-tile-sweep", tokens, StringComparison.Ordinal);
        Assert.Contains(".exo-game-overlay", tokens, StringComparison.Ordinal);
        Assert.Contains("backdrop-filter: blur(22px)", tokens, StringComparison.Ordinal);
        Assert.DoesNotContain("exo-game-page-wash", tokens, StringComparison.Ordinal);
        Assert.DoesNotContain("exo-game-page-photo", tokens, StringComparison.Ordinal);
        Assert.DoesNotContain("blur(52px)", tokens, StringComparison.Ordinal);
        Assert.DoesNotContain("min(480px", tokens, StringComparison.Ordinal);
        Assert.DoesNotContain("left: 18%", tokens, StringComparison.Ordinal);
        Assert.DoesNotContain("overflow-x: auto;", tokens, StringComparison.Ordinal);
        Assert.DoesNotContain(".exo-pinned-edge", tokens, StringComparison.Ordinal);
        Assert.DoesNotContain("scroll-snap-type: x proximity;", tokens, StringComparison.Ordinal);
        Assert.Contains("IsMaximizable = true", ReadRepoFile("ExoLauncher", "MainWindow.xaml.cs"), StringComparison.Ordinal);
        Assert.Contains("IsResizable = true", ReadRepoFile("ExoLauncher", "MainWindow.xaml.cs"), StringComparison.Ordinal);
        Assert.Contains("shell.maximize", ReadRepoFile("ExoLauncher", "Services", "WebHostBridge.cs"), StringComparison.Ordinal);
        var mainWindow = ReadRepoFile("ExoLauncher", "MainWindow.xaml.cs");
        var mainXaml = ReadRepoFile("ExoLauncher", "MainWindow.xaml");
        Assert.Contains("NonClientRegionKind.Caption", mainWindow, StringComparison.Ordinal);
        Assert.Contains("TitleBarDragDip = 52", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Height=\"12\"", mainXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Height=\"8\"", mainXaml, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWebView_StoresItsProfileOutsideTheReplaceableAppTree()
    {
        var mainWindow = ReadRepoFile("ExoLauncher", "MainWindow.xaml.cs");

        Assert.Contains("Path.Combine(PathHelper.AppDataDir, \"webview\")", mainWindow, StringComparison.Ordinal);
        Assert.Contains("CoreWebView2Environment.CreateWithOptionsAsync", mainWindow, StringComparison.Ordinal);
        Assert.Contains("EnsureCoreWebView2Async(webViewEnvironment)", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("await WebHost.EnsureCoreWebView2Async();", mainWindow, StringComparison.Ordinal);
    }

    [Fact]
    public void ChromeIcons_UsePhosphor_AndHtmlMark()
    {
        var icons = ReadRepoFile("ui", "src", "brand", "icons.tsx");
        var mark = ReadRepoFile("ui", "src", "brand", "ExoMark.tsx");
        var launcher = ReadRepoFile("ui", "src", "components", "LauncherApp.tsx");
        var chrome = ReadRepoFile("ui", "src", "components", "WindowChrome.tsx");
        var tokens = ReadRepoFile("ui", "src", "tokens.css");
        var packageJson = ReadRepoFile("ui", "package.json");

        Assert.Contains("\"@phosphor-icons/react\"", packageJson, StringComparison.Ordinal);
        Assert.Contains("@phosphor-icons/react/dist/csr/", icons, StringComparison.Ordinal);
        Assert.Contains("CircleNotchIcon", icons, StringComparison.Ordinal);
        Assert.Contains("glyph(PlayIcon, 'fill')", icons, StringComparison.Ordinal);
        Assert.Contains("glyph(StopIcon, 'fill')", icons, StringComparison.Ordinal);
        Assert.Contains("glyph(StarIcon, 'fill')", icons, StringComparison.Ordinal);
        Assert.DoesNotContain("amicons", packageJson, StringComparison.Ordinal);
        Assert.DoesNotContain("amicons", icons, StringComparison.Ordinal);
        Assert.DoesNotContain("size={14}", chrome, StringComparison.Ordinal);
        Assert.DoesNotContain("size={14}", launcher, StringComparison.Ordinal);
        Assert.Contains(".exo-search-glyph", tokens, StringComparison.Ordinal);
        Assert.DoesNotContain("@tabler/icons-react", icons, StringComparison.Ordinal);
        Assert.DoesNotContain("@tabler/icons-react", packageJson, StringComparison.Ordinal);
        Assert.DoesNotContain("lucide-react", packageJson, StringComparison.Ordinal);
        Assert.DoesNotContain("lucide-react", launcher, StringComparison.Ordinal);
        Assert.DoesNotContain("lucide-react", chrome, StringComparison.Ordinal);
        Assert.Contains("alive = false", mark, StringComparison.Ordinal);
        Assert.Contains("alive={markBusy}", launcher, StringComparison.Ordinal);
        Assert.Contains("1400", launcher, StringComparison.Ordinal);
        Assert.Contains("exo-mark-bar", mark, StringComparison.Ordinal);
        Assert.DoesNotContain("<polygon", mark, StringComparison.Ordinal);
        Assert.Contains("skewX(-18deg)", tokens, StringComparison.Ordinal);
        Assert.Contains("exo-mark-wave", tokens, StringComparison.Ordinal);
        Assert.Contains("50% { opacity: 0.45; }", tokens, StringComparison.Ordinal);
        Assert.DoesNotContain("scaleX(0.36)", tokens, StringComparison.Ordinal);
        Assert.Contains("width: 36%", tokens, StringComparison.Ordinal);
        Assert.Contains(".exo-mark.is-alive .exo-mark-bar-2 { top: 43.25%; left: 22%; width: 54%; }", tokens, StringComparison.Ordinal);
        Assert.Contains("exo-busy-sweep", ReadRepoFile("ui", "src", "exo-shell.css"), StringComparison.Ordinal);
        Assert.DoesNotContain("exo-mark-idle", tokens, StringComparison.Ordinal);
        Assert.DoesNotContain("translate(2px, -1px)", tokens, StringComparison.Ordinal);
        Assert.Contains("from '../brand/icons'", chrome, StringComparison.Ordinal);
    }

    [Fact]
    public void HomeLibrary_HasNoContinueCarousel()
    {
        var launcher = ReadRepoFile("ui", "src", "components", "LauncherApp.tsx");
        var tokens = ReadRepoFile("ui", "src", "tokens.css");
        var root = RepoRoot();

        Assert.DoesNotContain("ContinueBanner", launcher, StringComparison.Ordinal);
        Assert.DoesNotContain("exo-continue", launcher, StringComparison.Ordinal);
        Assert.DoesNotContain(".exo-continue", tokens, StringComparison.Ordinal);
        Assert.Contains("NowStage", launcher, StringComparison.Ordinal);
        Assert.Contains("pickNow", launcher, StringComparison.Ordinal);
        Assert.Contains(".exo-now", tokens, StringComparison.Ordinal);
        Assert.Contains("naturalWidth / img.naturalHeight < 1.2", ReadRepoFile("ui", "src", "components", "NowStage.tsx"), StringComparison.Ordinal);
        Assert.Contains("Last launched", ReadRepoFile("ui", "src", "lib", "now.ts"), StringComparison.Ordinal);
        var now = ReadRepoFile("ui", "src", "components", "NowStage.tsx");
        var detail = ReadRepoFile("ui", "src", "components", "DetailPanel.tsx");
        Assert.DoesNotContain("exo-now-hit", now, StringComparison.Ordinal);
        Assert.DoesNotContain("PosterMorph", now, StringComparison.Ordinal);
        Assert.DoesNotContain("PosterMorph", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("LayoutGroup", launcher, StringComparison.Ordinal);
        Assert.Contains("exo-library-pane", launcher, StringComparison.Ordinal);
        Assert.Contains("is-overlay-open", launcher, StringComparison.Ordinal);
        Assert.Contains("preventScroll: true", launcher, StringComparison.Ordinal);
        Assert.Contains("onMouseDown", now, StringComparison.Ordinal);
        Assert.Contains("position: fixed", tokens, StringComparison.Ordinal);
        Assert.Contains("visibleInstallPercent", now, StringComparison.Ordinal);
        Assert.Contains("percent: null", launcher, StringComparison.Ordinal);
        Assert.DoesNotContain("percent: 0", launcher, StringComparison.Ordinal);
        Assert.Contains("Close details", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("Continue playing", launcher, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("scroll-snap-type", tokens, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(root, "ui", "src", "components", "ContinueBanner.tsx")));
        Assert.False(File.Exists(Path.Combine(root, "ui", "src", "lib", "spotlight.ts")));
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
}
