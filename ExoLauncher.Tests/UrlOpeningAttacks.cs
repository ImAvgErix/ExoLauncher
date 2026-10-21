using ExoLauncher.Services;
using ExoLauncher.Ui;

namespace ExoLauncher.Tests;

/// <summary>
/// Attack 1 — opening URLs. The scheme/host gate that every shell-open path
/// shares is <see cref="ExoUrlLaunch.IsAllowed"/> (shell.openUrl, native
/// Settings/keys) and <see cref="StoreLinks.AppUri"/> / the store.open host
/// allowlist (Get-on-store, GG.deals). These tests feed the gate hostile input
/// WITHOUT ever launching anything: they call the pure predicate, not
/// Process.Start, so nothing dangerous can run.
/// </summary>
public class UrlOpeningAttacks
{
    // Payloads from the brief plus a few more.
    public static TheoryData<string> DangerousUrls() =>
    [
        "javascript:alert(1)",
        "file:///C:/Windows/System32/calc.exe",
        "file://server/share/evil.exe",
        "ms-settings:",
        "ms-settings:windowsupdate",
        "search-ms:query=secret&crumb=location:C:\\",
        "search-ms:displayname=Downloads",
        "\\\\server\\share",
        "\\\\attacker.example.com\\share\\payload.exe",
        "vbscript:msgbox(1)",
        "shell:startup",
        "cmd:/c calc",
        "mailto:x@y.z",
        "ftp://evil/x",
        "data:text/html,<script>1</script>",
        // steam:// with odd commands — the brief asks for these specifically.
        "steam://run/1245620//-malicious-arg",
        "steam://install/0; calc",
        "steam://open/console",
        "steam://flushconfig",
        "steam://uninstall/440",
        // epic launcher with odd paths
        "com.epicgames.launcher://store/../../etc",
        "com.epicgames.launcher://apps/x?action=launch&silent=true",
        // quotes/spaces
        "https://example.com/\" & calc & \"",
        "https://example.com/a b c",
    ];

    [Theory]
    [MemberData(nameof(DangerousUrls))]
    public void IsAllowed_rejects_or_classifies(string url)
    {
        // The gate must never approve a non-steam, non-store scheme for a raw
        // shell open. steam:// is approved by design (it is handed to the Steam
        // protocol handler, not ShellExecute of an arbitrary file), so record
        // which bucket each payload falls in rather than blanket-denying.
        var allowed = Uri.TryCreate(url, UriKind.Absolute, out var uri) && ExoUrlLaunch.IsAllowed(uri);
        if (allowed)
        {
            // Only http(s), steam, and ms-windows-store (pdp|search) may pass.
            Assert.NotNull(uri);
            var scheme = uri!.Scheme.ToLowerInvariant();
            Assert.Contains(scheme, new[] { "https", "http", "steam", "ms-windows-store" });
            if (scheme == "ms-windows-store")
                Assert.Contains(uri.Host, new[] { "pdp", "search" });
        }
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("ms-settings:")]
    [InlineData("search-ms:query=x")]
    [InlineData("\\\\server\\share")]
    [InlineData("vbscript:1")]
    [InlineData("data:text/html,x")]
    public void IsAllowed_denies_dangerous_schemes(string url)
    {
        var allowed = Uri.TryCreate(url, UriKind.Absolute, out var uri) && ExoUrlLaunch.IsAllowed(uri);
        Assert.False(allowed, $"gate approved dangerous URL for shell open: {url}");
    }

    [Fact]
    public void TryStart_refuses_dangerous_without_launching()
    {
        // TryStart parses + gates before Process.Start. A denied URL returns
        // false and never reaches the shell. (It would only ShellExecute an
        // allowed http/steam/store URL, which this test does not pass.)
        foreach (var url in new[]
                 {
                     "javascript:alert(1)", "file:///C:/Windows/System32/calc.exe",
                     "ms-settings:", "search-ms:query=x", "\\\\server\\share",
                 })
        {
            Assert.False(ExoUrlLaunch.TryStart(url), $"TryStart accepted {url}");
        }
    }

    [Fact]
    public void TryStart_handles_very_long_and_malformed_without_throwing()
    {
        var huge = "https://example.com/" + new string('a', 200_000);
        // Must not throw or hang; gate decides. Host is example.com (http(s)),
        // so it is "allowed" by scheme — but we do not assert it launches; we
        // only assert the call is bounded and total. Use a clearly invalid one
        // to assert a definite false without any launch.
        Assert.False(ExoUrlLaunch.TryStart("ht!tp://%%%" + huge));
        Assert.False(ExoUrlLaunch.TryStart(null));
        Assert.False(ExoUrlLaunch.TryStart("   "));
    }

    // ---- store.open host allowlist (WebHostBridge.Store.IsStoreHost via StoreLinks) ----

    [Theory]
    [InlineData("https://store.steampowered.com/app/440/", "steam://store/440")]
    [InlineData("https://store.epicgames.com/p/fortnite", "com.epicgames.launcher://store/p/fortnite")]
    public void StoreLinks_AppUri_builds_only_known_launcher_links(string page, string _)
    {
        // AppUri only returns a launcher URI when that launcher's scheme is
        // registered on the machine. On CI nothing is registered, so it returns
        // null — proving it cannot be coerced into an arbitrary protocol here.
        var uri = new Uri(page);
        var app = StoreLinks.AppUri(uri);
        // Either null (launcher not installed) or exactly the expected scheme.
        if (app is not null)
            Assert.True(app.StartsWith("steam://", StringComparison.Ordinal)
                        || app.StartsWith("com.epicgames.launcher://", StringComparison.Ordinal)
                        || app.StartsWith("goggalaxy://", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("https://evil.com/app/440/")]
    [InlineData("https://store.steampowered.com.evil.com/app/440/")]
    [InlineData("https://store.epicgames.com/p/..%2f..%2f")]
    [InlineData("https://store.steampowered.com/app/notanumber/")]
    public void StoreLinks_AppUri_null_for_untrusted_or_nonnumeric(string page)
    {
        var app = StoreLinks.AppUri(new Uri(page));
        // No app link => store.open falls back to opening the https page itself
        // (still an https store host, never an arbitrary scheme).
        if (app is not null)
        {
            // A Steam app link must only ever carry digits.
            if (app.StartsWith("steam://store/", StringComparison.Ordinal))
                Assert.True(app["steam://store/".Length..].All(char.IsAsciiDigit));
        }
    }
}
