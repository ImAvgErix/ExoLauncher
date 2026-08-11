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

        Assert.Contains("deferForFirstPaint: true", library, StringComparison.Ordinal);
        Assert.Contains("FirstPaintCoverWarmDelay = TimeSpan.FromMilliseconds(750)", covers, StringComparison.Ordinal);
        Assert.Contains("private static readonly HttpClient CoverHttp", covers, StringComparison.Ordinal);
        Assert.Contains("BackgroundWarmConcurrency = 4", covers, StringComparison.Ordinal);
        Assert.Contains("requested ? RequestedWarmConcurrency : BackgroundWarmConcurrency", covers, StringComparison.Ordinal);
        Assert.Contains("PERF startup milestone=", window, StringComparison.Ordinal);
        Assert.Contains("webview-core-ready", window, StringComparison.Ordinal);
        Assert.Contains("webview-navigation-complete", window, StringComparison.Ordinal);
    }

    [Fact]
    public void GogAuth_UsesTrustedWebCallback_AndPersistentGogdlCredentials()
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
        Assert.Contains("if (r.ok && !r.requiresUserAction) await loadLibrary(true)", launcher, StringComparison.Ordinal);
    }

    [Fact]
    public void StoreOpen_IsAsyncAndKeepsRevealingColdClients()
    {
        var text = ReadRepoFile("ExoLauncher", "Services", "WebHostBridge.cs");
        Assert.Contains("\"shell.showStore\" => await ShowStoreAsync", text, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromSeconds(15)", text, StringComparison.Ordinal);
        Assert.Contains("using var started = Process.Start", text, StringComparison.Ordinal);
        Assert.DoesNotContain("processNames.Any(ProcessHelper.IsProcessRunning)", text, StringComparison.Ordinal);
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
        Assert.Contains("StoreUninstallPromptAutomator", steam, StringComparison.Ordinal);
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
        Assert.Contains("TimeSpan.FromMilliseconds(3500)", presenter, StringComparison.Ordinal);
        Assert.Contains("CornerRadius = new CornerRadius(12)", presenter, StringComparison.Ordinal);
        Assert.Contains("Width = 64", presenter, StringComparison.Ordinal);
        Assert.Contains("Height = 64", presenter, StringComparison.Ordinal);
        Assert.Contains("TryGetSafeIconUri", presenter, StringComparison.Ordinal);
        Assert.Contains("new BitmapImage(uri)", presenter, StringComparison.Ordinal);
        Assert.Contains("ImageFailed", presenter, StringComparison.Ordinal);
        Assert.Contains("Uri.UriSchemeHttps", presenter, StringComparison.Ordinal);
        Assert.Contains("Text = \"EXO // UNLOCKED\"", presenter, StringComparison.Ordinal);
        Assert.Contains("TrophyMotion.For(options)", presenter, StringComparison.Ordinal);
        Assert.Contains("\"TranslateY\", motion.Y, 0, 240", presenter, StringComparison.Ordinal);
        Assert.Contains("\"ScaleX\", 0.985, 1, 260", presenter, StringComparison.Ordinal);
        Assert.Contains("BeginCloseCurrent", presenter, StringComparison.Ordinal);
        Assert.Contains("OnExitAnimationCompleted", presenter, StringComparison.Ordinal);
        Assert.Contains("CompleteCloseCurrent", presenter, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildCollectibleBadge", presenter, StringComparison.Ordinal);
        Assert.DoesNotContain("Gradient(", presenter, StringComparison.Ordinal);
        Assert.DoesNotContain("EXO  /", presenter, StringComparison.Ordinal);
        Assert.Contains("\"trophies.preview\"", bridge, StringComparison.Ordinal);
        Assert.Contains("Achievement notifications", settings, StringComparison.Ordinal);
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
        Assert.Contains("min-height: 180px", styles, StringComparison.Ordinal);
        Assert.Contains("exo-trophy-preview-arrive", styles, StringComparison.Ordinal);
        Assert.Contains("TrophyRarityResolver.Label", presenter, StringComparison.Ordinal);
        Assert.Contains("DwmSetWindowAttribute", presenter, StringComparison.Ordinal);
        Assert.Contains("background: #090909", styles, StringComparison.Ordinal);
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
        Assert.Contains("transition: transform 200ms var(--ease-in-out);", tokens, StringComparison.Ordinal);
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
        Assert.Contains("setSelectedId(progress.gameId)", launcher, StringComparison.Ordinal);

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

        Assert.Contains("max-w-[1280px] px-6 py-7", settings, StringComparison.Ordinal);
        Assert.Contains("gap-x-8 gap-y-0 lg:grid-cols-2", settings, StringComparison.Ordinal);
        Assert.Contains("divide-y divide-line-soft", settings, StringComparison.Ordinal);
        Assert.Contains("<h2", settings, StringComparison.Ordinal);
        Assert.Contains("Launcher settings", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("Stores, updates, and notifications.", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("Overlapping local sessions", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("Quiet Game Mode keeps", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("Open a client only when", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("rounded-2xl border border-line-soft bg-elevated p-5", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("Exo Profile", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("type=\"password\"", settings, StringComparison.Ordinal);
        Assert.Contains("onCheckUpdate", settings, StringComparison.Ordinal);
        Assert.Contains("onInstallUpdate", settings, StringComparison.Ordinal);
        Assert.Contains("onAuth(store.store)", settings, StringComparison.Ordinal);
        Assert.Contains("host.pickFolder('Choose game folder')", settings, StringComparison.Ordinal);
        Assert.Contains("<TrophyNotificationSettings", settings, StringComparison.Ordinal);
    }

    [Fact]
    public void StoreBackends_DescribeAbsentBackendsAsNotInstalled_AndHideReadyActions()
    {
        var settings = ReadRepoFile("ui", "src", "components", "SettingsPanel.tsx");
        var onboarding = ReadRepoFile("ui", "src", "components", "OnboardingPanel.tsx");
        var library = ReadRepoFile("ExoLauncher", "Services", "LibraryService.cs");

        Assert.Contains("const clientInstalled = store.clientPresent ?? store.agentPresent", settings, StringComparison.Ordinal);
        Assert.Contains("const backendAvailable = !!store.agentPresent", settings, StringComparison.Ordinal);
        Assert.Contains("const connected = clientInstalled && accountConnected", settings, StringComparison.Ordinal);
        Assert.Contains("const canOpen = clientInstalled", settings, StringComparison.Ordinal);
        Assert.Contains("const canAuthenticate = backendAvailable", settings, StringComparison.Ordinal);
        Assert.Contains("'Not installed'", settings, StringComparison.Ordinal);

        Assert.Contains("const clientInstalled = s.clientPresent ?? s.agentPresent", onboarding, StringComparison.Ordinal);
        Assert.Contains("const backendAvailable = !!s.agentPresent", onboarding, StringComparison.Ordinal);
        Assert.Contains("const connected = clientInstalled && accountConnected", onboarding, StringComparison.Ordinal);
        Assert.Contains("needsAuth && backendAvailable", onboarding, StringComparison.Ordinal);
        Assert.Contains("'Not installed'", onboarding, StringComparison.Ordinal);

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
        Assert.Contains("Display only a fresh, account-scoped provider result", detail, StringComparison.Ordinal);
        Assert.Contains("host.refreshAchievements(selected.id)", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("host.getAchievements(selected.id)", detail, StringComparison.Ordinal);
        Assert.Contains("selected.launchTarget, selected.store", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("GetLatestSnapshot", achievementGet, StringComparison.Ordinal);
        Assert.Contains("achievementRefreshing", detail, StringComparison.Ordinal);
        Assert.Contains("? 'Updating…'", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("Syncing…", detail, StringComparison.Ordinal);
        Assert.Contains("md:overflow-hidden", detail, StringComparison.Ordinal);
        Assert.Contains("const primaryAction = resolvePrimaryAction(game)", card, StringComparison.Ordinal);
        Assert.Contains("primaryAction === 'update'", card, StringComparison.Ordinal);
        Assert.Contains("primaryAction === 'install'", card, StringComparison.Ordinal);
        Assert.DoesNotContain("game.updateAvailable &&", card, StringComparison.Ordinal);
        Assert.DoesNotContain("formatPlaytime", card, StringComparison.Ordinal);
        Assert.Contains("label=\"Playtime\"", detail, StringComparison.Ordinal);
        Assert.Contains("formatPlaytime(selected.playtimeMinutes, selected.lastPlayedUtc)", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("label=\"Status\"", detail, StringComparison.Ordinal);
        Assert.Contains("selected.canStop", detail, StringComparison.Ordinal);
        Assert.Contains("? 'Stop'", detail, StringComparison.Ordinal);
        Assert.Contains("if (selected?.canStop)", launcher, StringComparison.Ordinal);
        Assert.Contains("await onStopGame()", launcher, StringComparison.Ordinal);
        Assert.Contains("{ ...item, isRunning: false, canStop: false }", launcher, StringComparison.Ordinal);
        Assert.Contains("void host.getGame(selected.id)", launcher, StringComparison.Ordinal);
        Assert.DoesNotContain("const refreshed = await host.getGame(selected.id)", launcher, StringComparison.Ordinal);
        Assert.Contains(".exo-badge.is-warn", tokens, StringComparison.Ordinal);
        Assert.Contains("background: #ffb020", tokens, StringComparison.Ordinal);
        Assert.Contains("font-weight: 800", tokens, StringComparison.Ordinal);
        Assert.Contains("box-shadow:", tokens, StringComparison.Ordinal);
        Assert.Contains("closeDisabled={actionLocked}", launcher, StringComparison.Ordinal);
        Assert.Contains("disabled={actionLocked && lockedGameId !== game.id}", launcher, StringComparison.Ordinal);
        Assert.True(
            host.IndexOf("if (game.installed && game.updateAvailable) return 'update'", StringComparison.Ordinal) <
            host.IndexOf("if (game.primaryAction === 'play'", StringComparison.Ordinal),
            "Update availability must override a stale explicit Play action.");
    }

    private static string ReadRepoFile(params string[] relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ExoLauncher.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(new[] { dir!.FullName }.Concat(relative).ToArray()));
    }
}
