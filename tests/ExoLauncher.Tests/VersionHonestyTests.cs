using Xunit;

namespace ExoLauncher.Tests;

public sealed class VersionHonestyTests
{
    [Fact]
    public void VersionFile_IsTheProductVersion()
    {
        var version = File.ReadAllText(Path.Combine(RepoRoot(), "VERSION")).Trim();
        Assert.Equal("2.0.0", version);
        var props = File.ReadAllText(Path.Combine(RepoRoot(), "Directory.Build.props"));
        Assert.Contains("$(VersionFile)", props, StringComparison.Ordinal);
        Assert.Contains("<Version>$(ExoLauncherVersion)</Version>", props, StringComparison.Ordinal);
        Assert.DoesNotContain("ui/package.json", props, StringComparison.Ordinal);

        var changelog = File.ReadAllText(Path.Combine(RepoRoot(), "CHANGELOG.md"));
        Assert.StartsWith("# Changelog\n\n## 2.0.0 - 2026-08-21", changelog.Replace("\r\n", "\n"), StringComparison.Ordinal);
    }

    [Fact]
    public void RootDocs_DescribeLivePasswordAccountsAndCurrentLimitations()
    {
        var readme = File.ReadAllText(Path.Combine(RepoRoot(), "README.md"));
        var privacy = File.ReadAllText(Path.Combine(RepoRoot(), "PRIVACY.md"));
        var security = File.ReadAllText(Path.Combine(RepoRoot(), "SECURITY.md"));
        var adr = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "adr", "0005-online-profiles-presence.md"));
        var changelog = File.ReadAllText(Path.Combine(RepoRoot(), "CHANGELOG.md"));

        Assert.Contains("12–128-character password", readme, StringComparison.Ordinal);
        Assert.Contains("deployed and production-smoke-tested", readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Scrypt", privacy, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("marked unverified", privacy, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Password recovery is unavailable", privacy, StringComparison.Ordinal);
        Assert.Contains("POST /api/auth/sign-up/email", security, StringComparison.Ordinal);
        Assert.Contains("DPAPI", security, StringComparison.Ordinal);
        Assert.Contains("salted Scrypt hashes", adr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Deployed the Cloudflare exo-id Worker", changelog, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password grant", privacy, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password grant", adr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SecurityAndPrivacy_DescribeOptionalUpscalerSwaps()
    {
        var security = File.ReadAllText(Path.Combine(RepoRoot(), "SECURITY.md"));
        var privacy = File.ReadAllText(Path.Combine(RepoRoot(), "PRIVACY.md"));
        var readme = File.ReadAllText(Path.Combine(RepoRoot(), "README.md"));

        Assert.DoesNotContain("No game binary edits", security, StringComparison.Ordinal);
        Assert.Contains("upscaler", security, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".exo-bak", security, StringComparison.Ordinal);
        Assert.Contains("beeradmoore", privacy, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FidelityFX-SDK", privacy, StringComparison.Ordinal);
        Assert.Contains("Upscalers", readme, StringComparison.Ordinal);
        Assert.Contains(".exo-bak", readme, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ExoLauncher.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
