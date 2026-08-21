using ExoLauncher.Helpers;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class WebViewEnvironmentContractTests
{
    [Fact]
    public void AdditionalBrowserArguments_AlwaysDisableBackgrounding_AndKeepASingleDebugPort()
    {
        var fromEnv = WebViewHostProfile.AdditionalBrowserArguments(
            "--remote-debugging-port=9333",
            "1",
            "9229");
        Assert.DoesNotContain("disable-backgrounding-occluded-windows", fromEnv, StringComparison.Ordinal);
        Assert.Contains("--remote-debugging-port=9333", fromEnv, StringComparison.Ordinal);
        Assert.DoesNotContain("9229", fromEnv, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(fromEnv, "--remote-debugging-port="));

        var fromExoCdp = WebViewHostProfile.AdditionalBrowserArguments(null, "true", null);
        Assert.DoesNotContain("disable-backgrounding-occluded-windows", fromExoCdp, StringComparison.Ordinal);
        Assert.Contains("--remote-debugging-port=9229", fromExoCdp, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(fromExoCdp, "--remote-debugging-port="));

        var quiet = WebViewHostProfile.AdditionalBrowserArguments("  ", null, null);
        Assert.Equal(string.Empty, quiet);
        Assert.DoesNotContain("remote-debugging-port", quiet, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UserDataFolder_IsTheSharedAppProfile_NotATrophySidecar()
    {
        var folder = WebViewHostProfile.UserDataFolder;
        Assert.Equal(WebViewHostProfile.UserDataFolderName, Path.GetFileName(folder));
        Assert.True(
            folder.EndsWith("webview-debug", StringComparison.OrdinalIgnoreCase)
            || folder.EndsWith(Path.DirectorySeparatorChar + "webview", StringComparison.OrdinalIgnoreCase),
            folder);
        Assert.DoesNotContain("webview-trophy", folder, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AppAndTrophyOverlay_ShareOneWebViewEnvironment_SoOneDebugPortListsBothPages()
    {
        var profile = ReadRepoFile("ExoLauncher", "Helpers", "WebViewHostProfile.cs");
        var factory = ReadRepoFile("ExoLauncher", "Helpers", "WebViewEnvironmentFactory.cs");
        var window = ReadRepoFile("ExoLauncher", "MainWindow.xaml.cs");
        var presenter = ReadRepoFile("ExoLauncher", "Services", "TrophyNotificationPresenter.cs");
        var gog = ReadRepoFile("ExoLauncher", "Services", "GogAuthService.cs");

        Assert.Contains("WebViewEnvironmentFactory.GetAsync", window, StringComparison.Ordinal);
        Assert.Contains("WebViewEnvironmentFactory.GetAsync", presenter, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateWithOptionsAsync", window, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateWithOptionsAsync", presenter, StringComparison.Ordinal);
        Assert.Contains("CreateWithOptionsAsync", factory, StringComparison.Ordinal);
        Assert.Contains("WebViewHostProfile.UserDataFolder", factory, StringComparison.Ordinal);
        Assert.Contains("WebViewHostProfile.AdditionalBrowserArguments", factory, StringComparison.Ordinal);
        Assert.DoesNotContain("disable-backgrounding-occluded-windows", profile, StringComparison.Ordinal);
        Assert.Contains("MemoryUsageTargetLevel.Normal", window, StringComparison.Ordinal);
        Assert.Contains("_controller.IsVisible = false", presenter, StringComparison.Ordinal);
        Assert.DoesNotContain("CoreWebView2HostResourceAccessKind.Allow", window + presenter, StringComparison.Ordinal);
        Assert.Contains("CoreWebView2HostResourceAccessKind.DenyCors", window, StringComparison.Ordinal);
        Assert.DoesNotContain("Access-Control-Allow-Origin: *", window, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "_dispatcher = dispatcher;\n        EnqueueWarm();",
            presenter.Replace("\r\n", "\n", StringComparison.Ordinal),
            StringComparison.Ordinal);
        Assert.Contains("_trophyPresenter?.Warm();", window, StringComparison.Ordinal);
        Assert.Contains("\"webview-debug\"", profile, StringComparison.Ordinal);
        Assert.Contains("return \"webview\";", profile, StringComparison.Ordinal);

        Assert.DoesNotContain("webview-trophy", profile + factory + window + presenter, StringComparison.Ordinal);
        Assert.DoesNotContain("OverlayCdpPort", presenter, StringComparison.Ordinal);
        Assert.DoesNotContain("remote-debugging-port", presenter, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("remote-debugging-port", window, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("remote-debugging-port", factory, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, CountOccurrences(profile, "--remote-debugging-port="));

        Assert.Contains("DefaultBackgroundColor", presenter, StringComparison.Ordinal);
        Assert.Contains("CreateCoreWebView2ControllerOptions", presenter, StringComparison.Ordinal);

        Assert.Contains("gog-webview", gog, StringComparison.Ordinal);
        Assert.DoesNotContain("remote-debugging-port", gog, StringComparison.OrdinalIgnoreCase);
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
