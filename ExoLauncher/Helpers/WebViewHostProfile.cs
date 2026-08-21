namespace ExoLauncher.Helpers;

/// <summary>
/// Single WebView2 user-data folder and browser-argument set for the app
/// window and the trophy overlay. One environment means one browser process
/// and one remote-debugging endpoint that lists both pages.
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
        if (!ContainsSwitch(extra, "remote-debugging-port") && CdpRequested(exoCdp))
        {
            var port = string.IsNullOrWhiteSpace(exoCdpPort) ? "9229" : exoCdpPort.Trim();
            extra = Append(extra, "--remote-debugging-port=" + port);
        }

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
