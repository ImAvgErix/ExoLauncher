using Xunit;

namespace ExoLauncher.Tests;

public sealed class AccessibilityContractTests
{
    [Fact]
    public void NativeWindow_KeepsItsLogicalMinimumAcrossDpiAndAnnouncesStartupFailures()
    {
        var xaml = ReadRepoFile("ExoLauncher", "MainWindow.xaml");
        var window = ReadRepoFile("ExoLauncher", "MainWindow.xaml.cs");

        Assert.Contains("ApplyWindowMinimumSize", window, StringComparison.Ordinal);
        Assert.Contains("MinWindowWidth * scale", window, StringComparison.Ordinal);
        Assert.Contains("MinWindowHeight * scale", window, StringComparison.Ordinal);
        Assert.Contains("args.DidPositionChange", window, StringComparison.Ordinal);
        Assert.Contains("RootGrid_SizeChanged", window, StringComparison.Ordinal);

        Assert.Contains("AutomationProperties.Name=\"Exo Launcher startup status\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.LiveSetting=\"Polite\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Exo Launcher UI runtime error\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.LiveSetting=\"Assertive\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Restart Exo Launcher\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Get Microsoft WebView2 Runtime\"", xaml, StringComparison.Ordinal);
        Assert.Contains("WebViewRestartButton.Focus(FocusState.Programmatic)", window, StringComparison.Ordinal);
    }

    [Fact]
    public void TrophyBanner_AnnouncesOnlyTheRealOverlay()
    {
        var banner = ReadRepoFile("ui", "src", "components", "TrophyBanner.tsx");
        var overlay = ReadRepoFile("ui", "src", "trophy-overlay.tsx");
        var settings = ReadRepoFile("ui", "src", "components", "TrophyNotificationSettings.tsx");

        Assert.Contains("announce?: boolean", banner, StringComparison.Ordinal);
        Assert.Contains("role={announce ? 'status' : undefined}", banner, StringComparison.Ordinal);
        Assert.Contains("aria-live={announce ? 'polite' : undefined}", banner, StringComparison.Ordinal);
        Assert.Contains("aria-atomic={announce ? true : undefined}", banner, StringComparison.Ordinal);
        Assert.Contains("aria-hidden={announce ? undefined : true}", banner, StringComparison.Ordinal);
        Assert.Contains("<TrophyBanner", overlay, StringComparison.Ordinal);
        Assert.Contains("announce", overlay, StringComparison.Ordinal);
        Assert.DoesNotContain("announce", settings, StringComparison.Ordinal);
    }

    [Fact]
    public void TrophyRadioGroups_UseRovingFocusAndCompleteKeyboardNavigation()
    {
        var settings = ReadRepoFile("ui", "src", "components", "TrophyNotificationSettings.tsx");

        Assert.Contains("radioTargetIndex", settings, StringComparison.Ordinal);
        Assert.Contains("'ArrowLeft'", settings, StringComparison.Ordinal);
        Assert.Contains("'ArrowRight'", settings, StringComparison.Ordinal);
        Assert.Contains("'ArrowUp'", settings, StringComparison.Ordinal);
        Assert.Contains("'ArrowDown'", settings, StringComparison.Ordinal);
        Assert.Contains("'Home'", settings, StringComparison.Ordinal);
        Assert.Contains("'End'", settings, StringComparison.Ordinal);
        Assert.True(CountOccurrences(settings, "tabIndex={") >= 2);
        Assert.True(CountOccurrences(settings, "onKeyDown={") >= 2);
    }

    [Fact]
    public void WebUi_PreservesVisibleFocusAndWindowsHighContrast()
    {
        var tokens = ReadRepoFile("ui", "src", "tokens.css");

        Assert.Contains("--exo-focus-ring: #f2f2f2", tokens, StringComparison.Ordinal);
        Assert.Contains("outline: 2px solid var(--exo-focus-ring)", tokens, StringComparison.Ordinal);
        Assert.Contains("@media (forced-colors: active)", tokens, StringComparison.Ordinal);
        Assert.Contains("--exo-focus-ring: Highlight", tokens, StringComparison.Ordinal);
        Assert.Contains("color: HighlightText", tokens, StringComparison.Ordinal);
        Assert.Contains("background: Highlight", tokens, StringComparison.Ordinal);
        Assert.Contains("border-color: ButtonText", tokens, StringComparison.Ordinal);
        Assert.DoesNotContain("color: #4a4a4a", tokens, StringComparison.OrdinalIgnoreCase);

        var searchStart = tokens.IndexOf(".exo-search {", StringComparison.Ordinal);
        var searchEnd = tokens.IndexOf('}', searchStart);
        Assert.True(searchStart >= 0 && searchEnd > searchStart);
        Assert.DoesNotContain("outline: none", tokens[searchStart..searchEnd], StringComparison.Ordinal);

        var fieldStart = tokens.IndexOf(".exo-field:focus-visible {", StringComparison.Ordinal);
        var fieldEnd = tokens.IndexOf('}', fieldStart);
        Assert.True(fieldStart >= 0 && fieldEnd > fieldStart);
        Assert.DoesNotContain("outline: none", tokens[fieldStart..fieldEnd], StringComparison.Ordinal);
    }

    [Fact]
    public void SocialAndProfilePages_ExposeMainLandmarksWithoutPageEntryMotion()
    {
        var friends = ReadRepoFile("ui", "src", "components", "FriendsRoom.tsx");
        var profile = ReadRepoFile("ui", "src", "components", "ProfileRoom.tsx");
        var settings = ReadRepoFile("ui", "src", "components", "SettingsPanel.tsx");

        Assert.Contains("<main className=\"exo-friends\">", friends, StringComparison.Ordinal);
        Assert.DoesNotContain("<FadeIn className=\"exo-friends\"", friends, StringComparison.Ordinal);
        Assert.DoesNotContain("import { FadeIn }", friends, StringComparison.Ordinal);
        Assert.Contains("<main", profile, StringComparison.Ordinal);
        Assert.Contains("'exo-profile min-h-0 flex-1'", profile, StringComparison.Ordinal);
        Assert.Contains("</main>", profile, StringComparison.Ordinal);
        Assert.Contains(
            "<main className=\"exo-set\" data-controller-scope=\"settings\">",
            settings,
            StringComparison.Ordinal);
    }

    [Fact]
    public void GamepadNavigation_ActivatesOnlyExplicitSafeTargets()
    {
        var navigation = ReadRepoFile("ui", "src", "lib", "gamepadNavigation.ts");
        var app = ReadRepoFile("ui", "src", "components", "LauncherApp.tsx");
        var card = ReadRepoFile("ui", "src", "components", "GameCard.tsx");
        var page = ReadRepoFile("ui", "src", "components", "GamePage.tsx");
        var chrome = ReadRepoFile("ui", "src", "components", "WindowChrome.tsx");

        Assert.Contains("navigator.getGamepads", navigation, StringComparison.Ordinal);
        Assert.Contains("gamepadconnected", navigation, StringComparison.Ordinal);
        Assert.Contains("gamepaddisconnected", navigation, StringComparison.Ordinal);
        Assert.Contains("isTypingContext", navigation, StringComparison.Ordinal);
        Assert.Contains("canControllerActivate", navigation, StringComparison.Ordinal);
        Assert.Contains("hasAttribute('data-controller-safe')", navigation, StringComparison.Ordinal);
        Assert.DoesNotContain("key: 'Enter'", navigation, StringComparison.Ordinal);

        Assert.Contains("installGamepadNavigation", app, StringComparison.Ordinal);
        Assert.Contains("data-controller-safe", card, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(page, "data-controller-safe"));
        Assert.DoesNotContain("data-controller-safe", chrome, StringComparison.Ordinal);

        var primaryStart = page.IndexOf("className={`exo-play exo-primary-action", StringComparison.Ordinal);
        var primaryEnd = page.IndexOf("</button>", primaryStart, StringComparison.Ordinal);
        Assert.True(primaryStart >= 0 && primaryEnd > primaryStart);
        Assert.DoesNotContain("data-controller-safe", page[primaryStart..primaryEnd], StringComparison.Ordinal);
    }

    [Fact]
    public void SemanticProgressbars_AreNotNestedInsideActionButtons()
    {
        var app = ReadRepoFile("ui", "src", "components", "LauncherApp.tsx");
        var page = ReadRepoFile("ui", "src", "components", "GamePage.tsx");

        var updateButton = app.IndexOf("className={`exo-cta exo-update-action", StringComparison.Ordinal);
        var updateButtonEnd = app.IndexOf("</button>", updateButton, StringComparison.Ordinal);
        var updateProgress = app.IndexOf("aria-label=\"App update progress\"", updateButton, StringComparison.Ordinal);
        Assert.True(updateButton >= 0 && updateButtonEnd > updateButton && updateProgress > updateButtonEnd);
        Assert.DoesNotContain("role=\"progressbar\"", app[updateButton..updateButtonEnd], StringComparison.Ordinal);
        Assert.Contains("aria-valuetext", app[updateProgress..], StringComparison.Ordinal);

        var primaryButton = page.IndexOf("className={`exo-play exo-primary-action", StringComparison.Ordinal);
        var primaryButtonEnd = page.IndexOf("</button>", primaryButton, StringComparison.Ordinal);
        var primaryProgress = page.IndexOf("role=\"progressbar\"", primaryButton, StringComparison.Ordinal);
        Assert.True(primaryButton >= 0 && primaryButtonEnd > primaryButton && primaryProgress > primaryButtonEnd);
        Assert.DoesNotContain("role=\"progressbar\"", page[primaryButton..primaryButtonEnd], StringComparison.Ordinal);
        Assert.Contains("aria-valuetext", page[primaryProgress..], StringComparison.Ordinal);
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

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = 0; (i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0; i += needle.Length)
            count++;
        return count;
    }
}
