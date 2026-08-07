using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// Ensures WebHostBridge method strings stay in parity with ui/src/lib/host.ts call sites.
/// Reads shipped source files from the repo (not re-implementations).
/// </summary>
public class BridgeParityTests
{
    private static readonly string[] RequiredMethods =
    [
        "library.get",
        "library.refresh",
        "game.get",
        "game.launch",
        "game.install",
        "game.update",
        "game.cancelInstall",
        "game.progress",
        "deps.list",
        "deps.offerInstall",
        "stores.matrix",
        "settings.get",
        "settings.set",
        "shell.minimize",
        "shell.close",
        "shell.openUrl",
        "app.version",
    ];

    [Fact]
    public void HostTs_CallsRequiredBridgeMethods()
    {
        var hostTs = FindRepoFile(Path.Combine("ui", "src", "lib", "host.ts"));
        Assert.True(File.Exists(hostTs), $"host.ts not found at {hostTs}");
        var text = File.ReadAllText(hostTs);

        foreach (var method in RequiredMethods)
        {
            Assert.True(
                text.Contains(method, StringComparison.Ordinal),
                $"ui/src/lib/host.ts missing bridge method reference: {method}");
        }
    }

    [Fact]
    public void WebHostBridge_Source_DispatchesRequiredMethods()
    {
        var bridgeCs = FindRepoFile(Path.Combine("ExoLauncher", "Services", "WebHostBridge.cs"));
        Assert.True(File.Exists(bridgeCs), $"WebHostBridge.cs not found at {bridgeCs}");
        var text = File.ReadAllText(bridgeCs);

        foreach (var method in RequiredMethods)
        {
            Assert.True(
                text.Contains($"\"{method}\"", StringComparison.Ordinal),
                $"WebHostBridge.cs missing dispatch for: {method}");
        }
    }

    [Fact]
    public void LauncherApp_HasPrimaryActionAndProgress()
    {
        var ui = FindRepoFile(Path.Combine("ui", "src", "components", "LauncherApp.tsx"));
        Assert.True(File.Exists(ui));
        var text = File.ReadAllText(ui);
        Assert.Contains("Install", text, StringComparison.Ordinal);
        Assert.Contains("Play", text, StringComparison.Ordinal);
        Assert.Contains("Update", text, StringComparison.Ordinal);
        Assert.Contains("InstallProgressPanel", text, StringComparison.Ordinal);
        Assert.Contains("cancelInstall", text, StringComparison.Ordinal);
    }

    private static string FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
            // From tests/ExoLauncher.Tests/bin/.../net10.0-windows → repo root is 5 levels up
            candidate = Path.GetFullPath(Path.Combine(dir.FullName, relative));
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        // Explicit walk from base directory
        var start = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && start is not null; i++)
        {
            var candidate = Path.Combine(start.FullName, relative);
            if (File.Exists(candidate)) return candidate;
            // Also try when start is tests/ExoLauncher.Tests
            candidate = Path.GetFullPath(Path.Combine(start.FullName, "..", "..", relative));
            if (File.Exists(candidate)) return candidate;
            candidate = Path.GetFullPath(Path.Combine(start.FullName, "..", "..", "..", relative));
            if (File.Exists(candidate)) return candidate;
            candidate = Path.GetFullPath(Path.Combine(start.FullName, "..", "..", "..", "..", relative));
            if (File.Exists(candidate)) return candidate;
            candidate = Path.GetFullPath(Path.Combine(start.FullName, "..", "..", "..", "..", "..", relative));
            if (File.Exists(candidate)) return candidate;
            start = start.Parent;
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", relative));
    }
}
