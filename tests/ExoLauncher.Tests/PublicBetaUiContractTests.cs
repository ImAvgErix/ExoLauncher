using Xunit;

namespace ExoLauncher.Tests;

public sealed class PublicBetaUiContractTests
{
    [Fact]
    public void Titlebar_IsMarkSearchSettingsAndWindowButtons_WithoutPlayApplyOrWordmark()
    {
        var launcher = ReadRepoFile("ui", "src", "components", "LauncherApp.tsx");
        var settings = ReadRepoFile("ui", "src", "components", "SettingsPanel.tsx");
        var chrome = ReadRepoFile("ui", "src", "components", "WindowChrome.tsx");
        var now = ReadRepoFile("ui", "src", "components", "NowStage.tsx");
        var detail = ReadRepoFile("ui", "src", "components", "DetailPanel.tsx");
        var shell = ReadRepoFile("ui", "src", "exo-shell.css");

        var homeHeader = Slice(launcher, "<header className={`exo-titlebar exo-titlebar-home", "</header>");
        Assert.Contains("ExoMark", homeHeader, StringComparison.Ordinal);
        Assert.Contains("exo-titlebar-search", homeHeader, StringComparison.Ordinal);
        Assert.Contains("className=\"exo-search\"", homeHeader, StringComparison.Ordinal);
        Assert.Contains("setView('settings')", homeHeader, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Settings\"", homeHeader, StringComparison.Ordinal);
        Assert.Contains("<Settings", homeHeader, StringComparison.Ordinal);
        Assert.Contains("<WindowChrome", homeHeader, StringComparison.Ordinal);
        Assert.DoesNotContain("exo-titlebar-gun-cta", homeHeader, StringComparison.Ordinal);
        Assert.DoesNotContain("Play", homeHeader, StringComparison.Ordinal);
        Assert.DoesNotContain("Apply", homeHeader, StringComparison.Ordinal);
        Assert.DoesNotContain("Launcher", homeHeader, StringComparison.Ordinal);
        Assert.DoesNotContain("exo-brand-name", homeHeader, StringComparison.Ordinal);
        Assert.DoesNotContain("exo-brand-text", homeHeader, StringComparison.Ordinal);
        Assert.DoesNotContain("title=\"Exo Launcher\"", homeHeader, StringComparison.Ordinal);

        var settingsHeader = Slice(settings, "<header className={`exo-titlebar", "</header>");
        Assert.Contains("ExoMark", settingsHeader, StringComparison.Ordinal);
        Assert.Contains("<WindowChrome", settingsHeader, StringComparison.Ordinal);
        Assert.DoesNotContain("Play", settingsHeader, StringComparison.Ordinal);
        Assert.DoesNotContain("Apply", settingsHeader, StringComparison.Ordinal);
        Assert.DoesNotContain("Launcher", settingsHeader, StringComparison.Ordinal);
        Assert.DoesNotContain(">Settings<", settingsHeader, StringComparison.Ordinal);
        Assert.DoesNotContain("title=\"Exo Launcher\"", settingsHeader, StringComparison.Ordinal);

        Assert.DoesNotContain("Play", chrome, StringComparison.Ordinal);
        Assert.DoesNotContain("Apply", chrome, StringComparison.Ordinal);
        Assert.DoesNotContain("exo-brand-name", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("exo-brand-role", shell, StringComparison.Ordinal);

        Assert.Contains("onPrimary", now, StringComparison.Ordinal);
        Assert.Contains("onOpen", now, StringComparison.Ordinal);
        Assert.Contains("exo-now-cta", now, StringComparison.Ordinal);
        Assert.Contains("<Play", now, StringComparison.Ordinal);
        Assert.Contains("exo-primary-action", detail, StringComparison.Ordinal);
        Assert.Contains("<Play", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsAndFirstRun_ListOnlyPresentStores_AndStayGeneric()
    {
        var settings = ReadRepoFile("ui", "src", "components", "SettingsPanel.tsx");
        var onboarding = ReadRepoFile("ui", "src", "components", "OnboardingPanel.tsx");
        var launcher = ReadRepoFile("ui", "src", "components", "LauncherApp.tsx");
        var stores = ReadRepoFile("ui", "src", "lib", "stores.ts");
        var host = ReadRepoFile("ui", "src", "lib", "host.ts");
        var library = ReadRepoFile("ExoLauncher", "Services", "LibraryService.cs");

        Assert.Contains("export function isPresentStore", stores, StringComparison.Ordinal);
        Assert.Contains("export function presentStoreRows", stores, StringComparison.Ordinal);
        Assert.Contains("if (store.signedIn) return true", stores, StringComparison.Ordinal);
        Assert.Contains("store.clientPresent === true", stores, StringComparison.Ordinal);
        Assert.Contains("store.store === 'local'", stores, StringComparison.Ordinal);
        Assert.DoesNotContain("agentPresent: false", stores, StringComparison.Ordinal);

        Assert.Contains("presentStoreRows(stores)", settings, StringComparison.Ordinal);
        Assert.Contains("canOpenStoreClient", settings, StringComparison.Ordinal);
        Assert.Contains("storePresenceLabel", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("displayName: 'Steam', agentPresent: false", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("displayName: 'Epic', agentPresent: false", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("displayName: 'GOG', agentPresent: false", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("displayName: 'Riot', agentPresent: false", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("'Not installed'", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("Xbox app", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("Battle.net", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("onAuth", settings, StringComparison.Ordinal);

        Assert.Contains("Open library", onboarding, StringComparison.Ordinal);
        Assert.Contains("Add a folder", onboarding, StringComparison.Ordinal);
        Assert.DoesNotContain("displayName: 'Steam', agentPresent: false", onboarding, StringComparison.Ordinal);
        Assert.DoesNotContain("'Not installed'", onboarding, StringComparison.Ordinal);
        Assert.DoesNotContain("clientInstalled", onboarding, StringComparison.Ordinal);
        Assert.DoesNotContain("stores.map", onboarding, StringComparison.Ordinal);
        Assert.DoesNotContain(">Exo<", onboarding, StringComparison.Ordinal);
        Assert.DoesNotContain("VALORANT", onboarding, StringComparison.Ordinal);
        Assert.DoesNotContain("Stellar Blade", onboarding, StringComparison.Ordinal);

        Assert.Contains("Nothing here yet", launcher, StringComparison.Ordinal);
        Assert.Contains("Add a folder", launcher, StringComparison.Ordinal);
        Assert.DoesNotContain("Nothing installed", launcher, StringComparison.Ordinal);
        Assert.DoesNotContain("Search to download a game you already own", launcher, StringComparison.Ordinal);
        Assert.DoesNotContain(">Search library<", launcher, StringComparison.Ordinal);

        Assert.DoesNotContain("{ store: 'steam', displayName: 'Steam', agentPresent: false }", host, StringComparison.Ordinal);
        Assert.DoesNotContain("{ store: 'epic', displayName: 'Epic', agentPresent: false }", host, StringComparison.Ordinal);

        Assert.Contains("if (signedIn) return \"Signed in\";", library, StringComparison.Ordinal);
        Assert.Contains("if (!present) return \"Not installed\";", library, StringComparison.Ordinal);
        Assert.True(
            library.IndexOf("if (signedIn) return \"Signed in\";", StringComparison.Ordinal) <
            library.IndexOf("if (!present) return \"Not installed\";", StringComparison.Ordinal),
            "A signed-in headless backend must not be labeled Not installed.");
        Assert.DoesNotContain("if (!present) return \"Missing\";", library, StringComparison.Ordinal);
    }

    [Fact]
    public void ChromeMotion_UsesEaseOut_WithoutBackdropFilter()
    {
        var tokens = ReadRepoFile("ui", "src", "tokens.css");
        var shell = ReadRepoFile("ui", "src", "exo-shell.css");

        Assert.DoesNotContain("backdrop-filter", tokens, StringComparison.Ordinal);
        Assert.DoesNotContain("-webkit-backdrop-filter", tokens, StringComparison.Ordinal);
        Assert.DoesNotContain("backdrop-filter", shell, StringComparison.Ordinal);
        Assert.Contains("--ease-out", tokens, StringComparison.Ordinal);
        Assert.Contains("background: #000", tokens, StringComparison.Ordinal);
    }

    private static string Slice(string source, string start, string end)
    {
        var startAt = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(startAt >= 0, $"missing {start}");
        var endAt = source.IndexOf(end, startAt, StringComparison.Ordinal);
        Assert.True(endAt > startAt, $"missing {end} after {start}");
        return source[startAt..endAt];
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
