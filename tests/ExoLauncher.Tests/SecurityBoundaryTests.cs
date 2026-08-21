using System.Diagnostics;
using ExoLauncher.Helpers;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class SecurityBoundaryTests : IDisposable
{
    private readonly string _fixture = Path.Combine(
        Path.GetTempPath(),
        "exo-security-test-" + Guid.NewGuid().ToString("N"));

    public SecurityBoundaryTests() => Directory.CreateDirectory(_fixture);

    public void Dispose()
    {
        try { Directory.Delete(_fixture, recursive: true); } catch { }
    }

    [Fact]
    public void MainUiCsp_DoesNotPermitArbitraryHttpsImages()
    {
        var html = File.ReadAllText(FindRepoFile(Path.Combine("ui", "index.html")));
        var imageSources = html.Split("img-src ", StringSplitOptions.None)[1]
            .Split(';', 2, StringSplitOptions.None)[0];

        Assert.DoesNotContain(" https: ", " " + imageSources + " ", StringComparison.Ordinal);
        Assert.Contains("https://covers.exo-launcher.local", imageSources, StringComparison.Ordinal);
        Assert.Contains("https://profile-media.exo-launcher.local", imageSources, StringComparison.Ordinal);
        Assert.Contains("https://avatars.steamstatic.com", imageSources, StringComparison.Ordinal);
    }

    [Fact]
    public void RecursiveDeleteGuard_AcceptsStrictNormalDescendant()
    {
        var root = Directory.CreateDirectory(Path.Combine(_fixture, "Games")).FullName;
        var child = Directory.CreateDirectory(Path.Combine(root, "Title")).FullName;

        var ok = RecursiveDeleteGuard.TryValidateManagedChild(root, child, out var validated, out var error);

        Assert.True(ok, error);
        Assert.Equal(Path.GetFullPath(child), validated, ignoreCase: true);
    }

    [Fact]
    public void RecursiveDeleteGuard_RejectsRootSiblingAndTraversal()
    {
        var root = Directory.CreateDirectory(Path.Combine(_fixture, "Games")).FullName;
        var sibling = Directory.CreateDirectory(Path.Combine(_fixture, "Games-Backup")).FullName;
        var traversed = Path.Combine(root, "..", Path.GetFileName(sibling));

        Assert.False(RecursiveDeleteGuard.TryValidateManagedChild(root, root, out _, out _));
        Assert.False(RecursiveDeleteGuard.TryValidateManagedChild(root, sibling, out _, out _));
        Assert.False(RecursiveDeleteGuard.TryValidateManagedChild(root, traversed, out _, out _));
    }

    [Fact]
    public void RecursiveDeleteGuard_RejectsReparsePointEscape()
    {
        var root = Directory.CreateDirectory(Path.Combine(_fixture, "Games")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(_fixture, "Outside")).FullName;
        Directory.CreateDirectory(Path.Combine(outside, "Title"));
        var link = Path.Combine(root, "Linked");

        CreateDirectoryJunction(link, outside);

        Assert.False(RecursiveDeleteGuard.TryValidateManagedChild(
            root,
            Path.Combine(link, "Title"),
            out _,
            out var error));
        Assert.Contains("reparse", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PrivilegedShell_HostsTrustedUiWebView_AndKeepsDeleteGuards()
    {
        var bridge = File.ReadAllText(FindRepoFile(Path.Combine("ExoLauncher", "Services", "WebHostBridge.cs")));
        var shell = File.ReadAllText(FindRepoFile(Path.Combine("ExoLauncher", "MainWindow.xaml.cs")));
        var gogAuth = File.ReadAllText(FindRepoFile(Path.Combine("ExoLauncher", "Services", "GogAuthService.cs")));
        var gog = File.ReadAllText(FindRepoFile(Path.Combine("ExoLauncher", "Adapters", "GogAdapter.cs")));
        var local = File.ReadAllText(FindRepoFile(Path.Combine("ExoLauncher", "Adapters", "LocalAdapter.cs")));
        var installedCatalog = File.ReadAllText(FindRepoFile(
            Path.Combine("ExoLauncher", "Services", "InstalledGameCatalog.cs")));

        Assert.Contains("WebViewTrustPolicy", bridge, StringComparison.Ordinal);
        Assert.Contains("CoreWebView2", shell, StringComparison.Ordinal);
        Assert.Contains("EnsureCoreWebView2Async", shell, StringComparison.Ordinal);
        Assert.Contains("IsPasswordAutosaveEnabled = false", shell, StringComparison.Ordinal);
        Assert.Contains("WebViewTrustPolicy.IsTrustedAppUri", shell, StringComparison.Ordinal);
        Assert.Contains("CoreWebView2Environment.CreateWithOptionsAsync", gogAuth, StringComparison.Ordinal);
        Assert.Contains("gog-webview", gogAuth, StringComparison.Ordinal);
        Assert.Contains("RecursiveDeleteGuard.TryValidateManagedChild", gog, StringComparison.Ordinal);
        Assert.Contains("_installedCatalog.UninstallRegistered(game)", local, StringComparison.Ordinal);
        Assert.Contains("RecursiveDeleteGuard.TryValidateManagedChild", installedCatalog, StringComparison.Ordinal);
    }

    private static void CreateDirectoryJunction(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Junction creation does not require symbolic-link privilege on NTFS.
        }

        var shell = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = shell,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        process.StartInfo.ArgumentList.Add("/d");
        process.StartInfo.ArgumentList.Add("/c");
        process.StartInfo.ArgumentList.Add("mklink");
        process.StartInfo.ArgumentList.Add("/J");
        process.StartInfo.ArgumentList.Add(link);
        process.StartInfo.ArgumentList.Add(target);

        Assert.True(process.Start(), "Failed to start junction helper.");
        process.WaitForExit();
        Assert.True(
            process.ExitCode == 0 && Directory.Exists(link),
            "Could not create a test junction: " + process.StandardError.ReadToEnd());
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

        throw new FileNotFoundException("Repository file not found.", relative);
    }
}
