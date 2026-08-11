using Xunit;
using System.Text.RegularExpressions;

namespace ExoLauncher.Tests;

/// <summary>
/// Ensures WebHostBridge method strings stay in parity with ui/src/lib/host.ts call sites.
/// </summary>
public class BridgeParityTests
{
    private static readonly string[] RequiredMethods =
    [
        "library.get",
        "library.refresh",
        "game.get",
        "game.launch",
        "game.stop",
        "game.install",
        "game.update",
        "game.uninstall",
        "game.openFolder",
        "game.toggleFavorite",
        "game.cancelInstall",
        "game.progress",
        "achievements.get",
        "achievements.refresh",
        "stores.auth",
        "stores.search",
        "deps.list",
        "deps.offerInstall",
        "stores.matrix",
        "settings.get",
        "settings.set",
        "trophies.preview",
        "shell.minimize",
        "shell.close",
        "shell.openUrl",
        "shell.openPath",
        "shell.showStore",
        "shell.pickFolder",
        "app.version",
        "app.checkUpdate",
        "app.installUpdate",
    ];

    [Fact]
    public void TypeScriptWrappers_AndNativeDispatch_HaveExactRpcParity()
    {
        var hostTs = FindRepoFile(Path.Combine("ui", "src", "lib", "host.ts"));
        Assert.True(File.Exists(hostTs), $"host.ts not found at {hostTs}");
        var bridgeCs = FindRepoFile(Path.Combine("ExoLauncher", "Services", "WebHostBridge.cs"));
        Assert.True(File.Exists(bridgeCs), $"WebHostBridge.cs not found at {bridgeCs}");
        var hostText = File.ReadAllText(hostTs);
        var bridgeText = File.ReadAllText(bridgeCs);

        var exportedHostStart = hostText.IndexOf("export const host =", StringComparison.Ordinal);
        Assert.True(exportedHostStart >= 0, "ui/src/lib/host.ts is missing its exported host wrapper.");
        var exportedHost = hostText[exportedHostStart..];

        var wrapperMethods = Regex.Matches(
                exportedHost,
                "['\\\"](?<method>[a-z][a-z0-9]*\\.[A-Za-z][A-Za-z0-9]*)['\\\"]")
            .Select(match => match.Groups["method"].Value)
            .ToHashSet(StringComparer.Ordinal);
        var dispatchMethods = Regex.Matches(
                bridgeText,
                "^\\s*\\\"(?<method>[a-z][a-z0-9]*\\.[A-Za-z][A-Za-z0-9]*)\\\"\\s*=>",
                RegexOptions.Multiline)
            .Select(match => match.Groups["method"].Value)
            .ToHashSet(StringComparer.Ordinal);
        var requiredMethods = RequiredMethods.ToHashSet(StringComparer.Ordinal);

        Assert.Equal(requiredMethods.Order(), wrapperMethods.Order());
        Assert.Equal(requiredMethods.Order(), dispatchMethods.Order());
        Assert.Equal(wrapperMethods.Order(), dispatchMethods.Order());
    }

    [Fact]
    public void LauncherApp_HasPrimaryActionAndProgress()
    {
        var launcher = FindRepoFile(Path.Combine("ui", "src", "components", "LauncherApp.tsx"));
        var detail = FindRepoFile(Path.Combine("ui", "src", "components", "DetailPanel.tsx"));
        Assert.True(File.Exists(launcher));
        Assert.True(File.Exists(detail), "DetailPanel.tsx should hold cover-first CTA UI");
        var text = File.ReadAllText(launcher) + "\n" + File.ReadAllText(detail);
        Assert.Contains("Install", text, StringComparison.Ordinal);
        Assert.Contains("Play", text, StringComparison.Ordinal);
        Assert.Contains("Update", text, StringComparison.Ordinal);
        Assert.Contains("cancelInstall", text, StringComparison.Ordinal);
        Assert.Contains("exo-cta", text, StringComparison.Ordinal);
    }

    [Fact]
    public void LaunchStatus_UsesGameIdForUiAssociation()
    {
        var bridgeCs = FindRepoFile(Path.Combine("ExoLauncher", "Services", "WebHostBridge.cs"));
        var text = File.ReadAllText(bridgeCs);

        Assert.Contains("gameId = game.Id", text, StringComparison.Ordinal);
        Assert.DoesNotContain("id = game.Id", text, StringComparison.Ordinal);
    }

    [Fact]
    public void CoverUi_OnlyAcceptsOfficialPortraitSources()
    {
        var cover = FindRepoFile(Path.Combine("ui", "src", "components", "CoverArt.tsx"));
        var index = FindRepoFile(Path.Combine("ui", "index.html"));
        var coverText = File.ReadAllText(cover);
        var csp = File.ReadAllText(index);

        Assert.Contains("covers.exo-launcher.local", coverText, StringComparison.Ordinal);
        // Official Steam library posters only (library_600x900 / library_capsule).
        Assert.Contains("library_600x900", coverText, StringComparison.Ordinal);
        Assert.Contains("steamstatic", csp, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("valorant-api", coverText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("valorant-api", csp, StringComparison.OrdinalIgnoreCase);
        // Heroes are explicitly rejected in the allowlist helper.
        Assert.Contains("library_hero", coverText, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        var start = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && start is not null; i++)
        {
            var candidate = Path.Combine(start.FullName, relative);
            if (File.Exists(candidate)) return candidate;
            candidate = Path.GetFullPath(Path.Combine(start.FullName, "..", "..", "..", "..", "..", relative));
            if (File.Exists(candidate)) return candidate;
            start = start.Parent;
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", relative));
    }
}
