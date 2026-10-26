using System.Text.RegularExpressions;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the network registry and PRIVACY.md can drift apart:
/// - a registry entry names a PRIVACY.md row that was renamed or removed;
/// - a PRIVACY.md row has no entry, so nobody knows which code it describes;
/// - new code contacts a host that is in neither list, so users are never told.
/// The mapping is by exact row text, never by fuzzy matching.
/// </summary>
public sealed partial class NetworkDestinationsTests
{
    /// <summary>
    /// https:// hosts written in the host's source that its own code does not
    /// request: pages opened in the user's browser, pages hosted in WebView2
    /// (out of the registry's scope, see NetworkDestinations), Exo's virtual
    /// hosts, and project links in comments.
    /// </summary>
    private static readonly Dictionary<string, string> NotNativeRequests = new(StringComparer.OrdinalIgnoreCase)
    {
        // Store pages and searches opened in the user's browser (Storefront, StoreService.Game, GameKeyActivation).
        ["www.gog.com"] = "browser",
        ["gaming.amazon.com"] = "browser",
        ["store.rockstargames.com"] = "browser",
        ["www.minecraft.net"] = "browser",
        ["www.roblox.com"] = "browser",
        ["www.paradoxinteractive.com"] = "browser",
        ["wargaming.com"] = "browser",
        ["itch.io"] = "browser",
        ["playruneterra.com"] = "browser",
        ["tracker.gg"] = "browser (profile link; also the Origin header of api.tracker.gg requests)",
        // Store sign-in pages hosted in WebView2.
        ["auth.gog.com"] = "WebView sign-in",
        ["accounts.google.com"] = "WebView sign-in",
        // Steam's website in the hidden gifts page and its sign-in window (StoreGifts).
        ["login.steampowered.com"] = "WebView page",
        ["help.steampowered.com"] = "WebView page",
        // Web music players hosted in WebView2, and scripts that run inside Spotify's own page.
        ["music.youtube.com"] = "WebView page",
        ["music.apple.com"] = "WebView page",
        ["spclient.wg.spotify.com"] = "Spotify's page",
        // Exo's own WebView virtual hosts.
        ["app.exo-launcher.local"] = "virtual host",
        ["covers.exo-launcher.local"] = "virtual host",
        ["thumbs.exo-launcher.local"] = "virtual host",
        ["devices.exo-launcher.local"] = "virtual host",
    };

    private static IReadOnlyList<string> PrivacyRows()
    {
        var lines = File.ReadAllLines(Repo.PathOf("PRIVACY.md"));
        var start = Array.FindIndex(lines, line => line.Trim() == "## Other network requests");
        Assert.True(start >= 0, "PRIVACY.md has no \"## Other network requests\" section");
        var rows = new List<string>();
        foreach (var line in lines.Skip(start + 1).SkipWhile(line => !line.StartsWith('|')).TakeWhile(line => line.StartsWith('|')))
        {
            var cell = line.Split('|', StringSplitOptions.TrimEntries)[1];
            if (cell is "Destination" || cell.StartsWith("---", StringComparison.Ordinal)) continue;
            rows.Add(cell);
        }
        Assert.NotEmpty(rows);
        return rows;
    }

    [Fact]
    public void EveryRegistryRowIsInPrivacy()
    {
        var rows = PrivacyRows().ToHashSet(StringComparer.Ordinal);
        var named = NetworkDestinations.Hosts.Select(host => host.PrivacyRow).Concat(NetworkDestinations.RowsWithoutNativeHosts);
        var missing = named.Distinct().Where(row => !rows.Contains(row)).ToArray();
        Assert.True(missing.Length == 0, "Rows not in PRIVACY.md's table: " + string.Join(" | ", missing));
    }

    [Fact]
    public void EveryPrivacyRowIsInTheRegistry()
    {
        var named = NetworkDestinations.Hosts.Select(host => host.PrivacyRow)
            .Concat(NetworkDestinations.RowsWithoutNativeHosts).ToHashSet(StringComparer.Ordinal);
        var missing = PrivacyRows().Where(row => !named.Contains(row)).ToArray();
        Assert.True(missing.Length == 0, "PRIVACY.md rows with no NetworkDestinations entry: " + string.Join(" | ", missing));
    }

    [Fact]
    public void RowsWithoutNativeHostsReallyHaveNone() =>
        Assert.DoesNotContain(NetworkDestinations.Hosts, host => NetworkDestinations.RowsWithoutNativeHosts.Contains(host.PrivacyRow));

    [Fact]
    public void EveryHostInTheSourceIsAccountedFor()
    {
        var unlisted = Repo.HostSources()
            .SelectMany(path => UrlHost().Matches(File.ReadAllText(path)).Select(match => (path, host: match.Groups[1].Value)))
            .Where(entry => entry.host is not ("127.0.0.1" or "localhost") &&
                            !NetworkDestinations.IsListed(entry.host) &&
                            !NotNativeRequests.ContainsKey(entry.host))
            .Select(entry => $"{entry.host} ({Path.GetRelativePath(Repo.Root, entry.path)})")
            .Distinct()
            .ToArray();
        Assert.True(unlisted.Length == 0,
            "Hosts in the source that are neither in NetworkDestinations nor classed as not requested by native code: " +
            string.Join(", ", unlisted));
    }

    [Theory]
    [InlineData("*.steamstatic.com", "cdn.cloudflare.steamstatic.com", true)]
    [InlineData("*.steamstatic.com", "steamstatic.com", true)]
    [InlineData("*.steamstatic.com", "evilsteamstatic.com", false)]
    [InlineData("api.gog.com", "API.GOG.COM", true)]
    [InlineData("api.gog.com", "x.api.gog.com", false)]
    public void WildcardsCoverSubdomainsOnly(string pattern, string host, bool expected) =>
        Assert.Equal(expected, NetworkDestinations.Covers(pattern, host));

    [Theory]
    [InlineData("https://aka.ms/vs/17/release/vc_redist.x64.exe", true)]
    [InlineData("https://download.visualstudio.microsoft.com/x/vc_redist.x64.exe", true)]
    [InlineData("http://download.visualstudio.microsoft.com/x.exe", false)]
    [InlineData("https://download.visualstudio.microsoft.com:8443/x.exe", false)]
    [InlineData("https://cdn.cloudflare.steamstatic.com/x.exe", false)] // listed, but not on the installers row
    [InlineData("https://evil.example/x.exe", false)]
    public void InstallerRedirectsStayOnTheInstallersRow(string url, bool expected) =>
        Assert.Equal(expected, ExoLauncher.Services.DependencyService.IsInstallerHost(new Uri(url)));

    [Theory]
    [InlineData("https://bacon.secure.dyn.riotcdn.net/channels/public/rccontent/theme/x.png", true)]
    [InlineData("http://bacon.secure.dyn.riotcdn.net/x.png", false)]
    [InlineData("https://riotcdn.net.evil.example/x.png", false)]
    public void RiotThemeArtComesOnlyFromItsCdn(string url, bool expected) =>
        Assert.Equal(expected, ExoLauncher.Services.CoverArtService.IsApprovedArtworkDownloadUrl(url));

    [GeneratedRegex(@"https?://([A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)+)")]
    private static partial Regex UrlHost();
}
