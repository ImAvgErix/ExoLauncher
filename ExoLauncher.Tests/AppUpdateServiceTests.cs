using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways update metadata parsing can fail:
/// - an asset with another name (a .zip, a .sha256, a look-alike) is taken as the installer;
/// - a download URL on another host, port, user-info or repository, or for another tag, is trusted;
/// - a .sha256 file naming a different asset, or with extra tokens, is accepted;
/// - a digest that is not 64 hex characters is accepted, or a "sha256:" prefix or upper case is rejected;
/// - release JSON without assets, or with a bad digest, throws instead of returning incomplete metadata;
/// - a later verified asset loses to an earlier unverified one.
/// </summary>
public sealed class AppUpdateServiceTests
{
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData("ExoLauncher.exe", true)]
    [InlineData("ExoLauncher-Setup.exe", true)]
    [InlineData("exolauncher-setup.EXE", true)]
    [InlineData("ExoLauncher-Setup.exe.sha256", false)]
    [InlineData("ExoLauncher-Setup.zip", false)]
    [InlineData("ExoLauncher-Setup.exe ", false)]
    [InlineData("", false)]
    [InlineData("  ", false)]
    public void UpdateAssetNames(string name, bool expected) =>
        Assert.Equal(expected, AppUpdateService.IsUpdateAssetName(name));

    [Theory]
    [InlineData("https://github.com/UhhErix/ExoLauncher/releases/download/v3.5.0/ExoLauncher-Setup.exe", "v3.5.0", "ExoLauncher-Setup.exe", true)]
    [InlineData("https://github.com/UhhErix/ExoLauncher/releases/download/v3.5.0/ExoLauncher-Setup.exe", "v3.4.0", "ExoLauncher-Setup.exe", false)]
    [InlineData("https://github.com/Someone/ExoLauncher/releases/download/v3.5.0/ExoLauncher-Setup.exe", "v3.5.0", "ExoLauncher-Setup.exe", false)]
    [InlineData("https://github.com:8443/UhhErix/ExoLauncher/releases/download/v3.5.0/ExoLauncher-Setup.exe", "v3.5.0", "ExoLauncher-Setup.exe", false)]
    [InlineData("https://user@github.com/UhhErix/ExoLauncher/releases/download/v3.5.0/ExoLauncher-Setup.exe", "v3.5.0", "ExoLauncher-Setup.exe", false)]
    [InlineData("http://github.com/UhhErix/ExoLauncher/releases/download/v3.5.0/ExoLauncher-Setup.exe", "v3.5.0", "ExoLauncher-Setup.exe", false)]
    [InlineData("https://github.com.evil.example/UhhErix/ExoLauncher/releases/download/v3.5.0/ExoLauncher-Setup.exe", "v3.5.0", "ExoLauncher-Setup.exe", false)]
    [InlineData("https://github.com/UhhErix/ExoLauncher/releases/download/v3.5.0/Other.exe", "v3.5.0", "Other.exe", false)]
    [InlineData("not a url", "v3.5.0", "ExoLauncher-Setup.exe", false)]
    public void ReleaseAssetUrls(string url, string tag, string name, bool expected) =>
        Assert.Equal(expected, AppUpdateService.IsReleaseAssetUri(url, tag, name));

    [Theory]
    [InlineData("https://github.com/x", true)]
    [InlineData("https://objects.githubusercontent.com/x", true)]
    [InlineData("https://release-assets.githubusercontent.com/x", true)]
    [InlineData("http://github.com/x", false)]
    [InlineData("https://raw.githubusercontent.com/x", false)]
    [InlineData("https://example.com/x", false)]
    public void DownloadRedirectHosts(string url, bool expected) =>
        Assert.Equal(expected, AppUpdateService.IsAllowedDownloadUri(new Uri(url)));

    [Theory]
    [InlineData(Hash, Hash)]
    [InlineData("sha256:" + Hash, Hash)]
    [InlineData("SHA256:0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF", Hash)]
    [InlineData("  " + Hash + "  ", Hash)]
    [InlineData("0123", null)]
    [InlineData("zz23456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void NormalizesDigests(string? digest, string? expected) =>
        Assert.Equal(expected, AppUpdateService.NormalizeSha(digest));

    [Theory]
    [InlineData(Hash, Hash)]
    [InlineData(Hash + "\n", Hash)]
    [InlineData(Hash + "  ExoLauncher-Setup.exe\n", Hash)]
    [InlineData(Hash + " *ExoLauncher-Setup.exe", Hash)]
    [InlineData(Hash + "  ExoLauncher.exe", null)]
    [InlineData(Hash + "  ExoLauncher-Setup.exe extra", null)]
    [InlineData("", null)]
    [InlineData("nothex  ExoLauncher-Setup.exe", null)]
    public void ParsesSha256Files(string text, string? expected) =>
        Assert.Equal(expected, AppUpdateService.ParseSha256File(text, "ExoLauncher-Setup.exe"));

    [Fact]
    public void ParsesReleaseJsonPreferringAVerifiedAsset()
    {
        var json = $$"""
        {
          "tag_name": "v3.5.0",
          "assets": [
            { "name": "notes.txt", "browser_download_url": "https://github.com/UhhErix/ExoLauncher/releases/download/v3.5.0/notes.txt", "size": 5 },
            { "name": "ExoLauncher.exe", "browser_download_url": "https://github.com/UhhErix/ExoLauncher/releases/download/v3.5.0/ExoLauncher.exe", "size": 10 },
            { "name": "ExoLauncher-Setup.exe", "browser_download_url": "https://github.com/UhhErix/ExoLauncher/releases/download/v3.5.0/ExoLauncher-Setup.exe", "size": 2048, "digest": "sha256:{{Hash}}" }
          ]
        }
        """;
        Assert.True(AppUpdateService.TryParseReleaseJson(json, out var release));
        Assert.Equal("v3.5.0", release.Tag);
        Assert.Equal("ExoLauncher-Setup.exe", release.AssetName);
        Assert.Equal(2048, release.Size);
        Assert.Equal(Hash, release.Sha256);
    }

    [Fact]
    public void ReleaseJsonWithoutDigestKeepsTheAssetForTheSha256File()
    {
        const string json = """
        { "tag_name": "v3.5.0", "assets": [
          { "name": "ExoLauncher-Setup.exe", "browser_download_url": "https://github.com/UhhErix/ExoLauncher/releases/download/v3.5.0/ExoLauncher-Setup.exe", "size": 2048, "digest": "md5:abc" } ] }
        """;
        Assert.True(AppUpdateService.TryParseReleaseJson(json, out var release));
        Assert.Equal("ExoLauncher-Setup.exe", release.AssetName);
        Assert.Null(release.Sha256);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{ \"tag_name\": \"\" }")]
    [InlineData("not json")]
    [InlineData("[]")]
    public void RejectsReleaseJsonWithoutATag(string json) =>
        Assert.False(AppUpdateService.TryParseReleaseJson(json, out _));

    [Theory]
    [InlineData("https://github.com/UhhErix/ExoLauncher/releases/tag/v3.5.0", "v3.5.0")]
    [InlineData("https://github.com/uhherix/exolauncher/releases/tag/v3.5.0", "v3.5.0")]
    [InlineData("https://github.com/Other/ExoLauncher/releases/tag/v3.5.0", null)]
    [InlineData("https://github.com/UhhErix/ExoLauncher/releases/latest", null)]
    [InlineData("https://example.com/UhhErix/ExoLauncher/releases/tag/v3.5.0", null)]
    public void TagFromReleasesRedirect(string url, string? expected) =>
        Assert.Equal(expected, AppUpdateService.TagFromReleasesUrl(new Uri(url)));
}
