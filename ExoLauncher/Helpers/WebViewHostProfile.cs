namespace ExoLauncher.Helpers;

/// <summary>
/// Single WebView2 user-data folder and browser-argument set for the chrome
/// WebView. Nothing native may use a sidecar browser profile.
/// </summary>
internal static class WebViewHostProfile
{
    internal static string UserDataFolderName
    {
        get
        {
#if DEBUG
            return "webview-debug";
#else
            return "webview";
#endif
        }
    }

    internal static string UserDataFolder => Path.Combine(PathHelper.AppDataDir, UserDataFolderName);

    // Every music provider shares one named profile inside the
    // shell's user data folder so WebView2 boots a single environment.
    // WebView2 names the on-disk folder WV2Profile_<ProfileName>.
    internal const string EmbeddedProfileName = "embedded";
    internal static string EmbeddedProfileFolder =>
        Path.Combine("EBWebView", "WV2Profile_" + EmbeddedProfileName);

    internal static string AdditionalBrowserArguments() => AdditionalBrowserArguments(
        Environment.GetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS"),
        Environment.GetEnvironmentVariable("EXO_CDP")
            ?? Environment.GetEnvironmentVariable("EXOOS_CDP")
            ?? Environment.GetEnvironmentVariable("AETHER_CDP"),
        Environment.GetEnvironmentVariable("EXO_CDP_PORT"));

    internal static string AdditionalBrowserArguments(
        string? webView2AdditionalBrowserArguments,
        string? exoCdp,
        string? exoCdpPort)
    {
        var extra = (webView2AdditionalBrowserArguments ?? "").Trim();
#if DEBUG
        // Debug builds only: a shipped Exo never opens a debugging port from EXO_CDP.
        // The E2E run sets WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS itself, which WebView2 reads directly.
        if (!ContainsSwitch(extra, "remote-debugging-port") && CdpRequested(exoCdp))
        {
            var port = int.TryParse(exoCdpPort, out var n) && n is >= 1024 and <= 65535 ? n : 9229;
            extra = Append(extra, "--remote-debugging-port=" + port);
        }
#endif

        // Same-site pages share a renderer; a hung provider page still
        // cannot take the shell's renderer down. Drop Edge mini-menu / PDF chrome.
        // No spare renderer: the shell is one site, and a warm spare costs ~50 MB for nothing.
        // Wheel scrolling stays Chromium's own, smoothed on the compositor thread.
        if (!ContainsSwitch(extra, "disable-features"))
            extra = Append(extra, "--disable-features=msWebOOUI,msPdfOOUI,SpareRendererForSitePerProcess");
        if (!ContainsSwitch(extra, "process-per-site"))
            extra = Append(extra, "--process-per-site");

        return extra;
    }

    private static bool CdpRequested(string? value) =>
        string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsSwitch(string arguments, string name) =>
        arguments.Contains(name, StringComparison.OrdinalIgnoreCase);

    private static string Append(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left)) return right;
        if (string.IsNullOrWhiteSpace(right)) return left;
        return left + " " + right;
    }
}
