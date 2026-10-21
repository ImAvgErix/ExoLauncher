using ExoLauncher.Helpers;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the bridge's sender check can fail (red-team attack 2b, security/attacks/bridge):
/// - Exo Mouse, on its own host (or its files under the app host's /mouse/), posts straight to the host and reaches every RPC;
/// - the /mouse/ check is dodged with case, percent-encoding, a backslash or a dot segment;
/// - a look-alike host, http, a port or userinfo passes as the app;
/// - the shell document itself is refused.
/// </summary>
public sealed class WebViewTrustPolicyTests
{
    [Theory]
    [InlineData("https://app.exo-launcher.local/index.html")]
    [InlineData("https://app.exo-launcher.local/")]
    [InlineData("https://app.exo-launcher.local/index.html#/library")]
    public void ShellDocumentReachesTheBridge(string source) =>
        Assert.True(WebViewTrustPolicy.IsTrustedBridgeSource(source));

    [Theory]
    [InlineData("https://app.exo-launcher.local/mouse/index.html")]
    [InlineData("https://app.exo-launcher.local/MOUSE/index.html")]
    [InlineData("https://app.exo-launcher.local/%6Douse/index.html")]
    [InlineData("https://app.exo-launcher.local/x/../mouse/index.html")]
    [InlineData(@"https://app.exo-launcher.local\mouse\index.html")]
    [InlineData("https://app.exo-launcher.local.evil.test/index.html")]
    [InlineData("http://app.exo-launcher.local/index.html")]
    [InlineData("https://app.exo-launcher.local:8443/index.html")]
    [InlineData("https://user@app.exo-launcher.local/index.html")]
    [InlineData("https://shaders.exo-launcher.local/index.html")]
    [InlineData("https://mouse.exo-launcher.local/index.html")]
    [InlineData("")]
    [InlineData(null)]
    public void EverythingElseIsRefused(string? source) =>
        Assert.False(WebViewTrustPolicy.IsTrustedBridgeSource(source));

    [Fact]
    public void TheOriginCheckAloneLetsTheMousePageThrough() =>
        // Why the bridge needs IsTrustedBridgeSource: the host-only check accepts the mouse page.
        Assert.True(WebViewTrustPolicy.IsTrustedAppUri("https://app.exo-launcher.local/mouse/index.html"));

    [Fact]
    public void ExoMouseHasItsOwnOrigin() =>
        // The frame must be another origin, or its script can reach the shell's chrome.webview.
        Assert.False(WebViewTrustPolicy.IsTrustedAppUri("https://" + WebViewTrustPolicy.MouseHost + "/index.html"));
}
