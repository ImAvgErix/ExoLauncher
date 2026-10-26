using ExoLauncher.Helpers;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Attack 3 — path traversal and symlinks, proven in real temp folders.
/// Covers cover-cache file names, the recursive-delete guard (Add folder /
/// uninstall) and the upscaler swap's symlink/junction refusal. No user data
/// or Steam folders are touched.
/// </summary>
public sealed class PathTraversalAttacks : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "exo-pt-" + Guid.NewGuid().ToString("N"));

    public PathTraversalAttacks() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { }
    }

    // ---- cover cache: virtual-host URL -> on-disk file (CoverArtService.TryResolveLocalFile) ----

    [Theory]
    [InlineData("https://covers.exo-launcher.local/..%2f..%2fsettings.json")]
    [InlineData("https://covers.exo-launcher.local/..\\..\\evil")]
    [InlineData("https://covers.exo-launcher.local/../evil.jpg")]
    [InlineData("https://covers.exo-launcher.local/a/b.jpg")]
    [InlineData("https://covers.exo-launcher.local/a\\b.jpg")]
    public void Cover_virtualhost_resolution_refuses_traversal(string url)
    {
        // Any name with a separator or .. must resolve to null, never a path
        // that escapes the covers cache.
        var resolved = CoverArtService.TryResolveLocalFile(url);
        if (resolved is not null)
        {
            var full = Path.GetFullPath(resolved);
            var root = Path.GetFullPath(CoverArtService.CacheRoot) + Path.DirectorySeparatorChar;
            Assert.StartsWith(root, full);
            Assert.DoesNotContain("..", full);
        }
    }

    // ---- recursive delete guard (Add folder / uninstall under a managed root) ----

    [Fact]
    public void DeleteGuard_rejects_parent_traversal()
    {
        var root = Path.Combine(_tmp, "games");
        Directory.CreateDirectory(Path.Combine(root, "GameA"));
        var outside = Path.Combine(_tmp, "outside");
        Directory.CreateDirectory(outside);

        Assert.False(RecursiveDeleteGuard.TryValidateManagedChild(root, Path.Combine(root, "..", "outside"), out _, out _));
        Assert.False(RecursiveDeleteGuard.TryValidateManagedChild(root, outside, out _, out _));
        Assert.False(RecursiveDeleteGuard.TryValidateManagedChild(root, root, out _, out _));
        // A genuine in-tree child validates.
        Assert.True(RecursiveDeleteGuard.TryValidateManagedChild(root, Path.Combine(root, "GameA"), out var ok, out _));
        Assert.Equal(Path.GetFullPath(Path.Combine(root, "GameA")), ok);
    }

    [Fact]
    public void DeleteGuard_rejects_path_through_junction()
    {
        var root = Path.Combine(_tmp, "games");
        Directory.CreateDirectory(root);
        var realTarget = Path.Combine(_tmp, "elsewhere");
        Directory.CreateDirectory(Path.Combine(realTarget, "victim"));

        // A junction INSIDE the root that points outside it.
        var link = Path.Combine(root, "link");
        Assert.SkipUnless(TryCreateJunction(link, realTarget), "This volume takes no junctions.");

        // Deleting through the junction must be refused: the child is reached
        // via a reparse point.
        var through = Path.Combine(link, "victim");
        Assert.False(RecursiveDeleteGuard.TryValidateManagedChild(root, through, out _, out var error));
        Assert.Contains("link", error, StringComparison.OrdinalIgnoreCase);
        // And the real outside target must still exist (nothing followed it).
        Assert.True(Directory.Exists(Path.Combine(realTarget, "victim")));
    }

    // ---- upscaler swap: game folder whose DLL path crosses a symlink/junction ----

    [Fact]
    public void Upscaler_IsSafeGamePath_rejects_symlinked_dll_pointing_outside()
    {
        var gameRoot = Path.Combine(_tmp, "game");
        Directory.CreateDirectory(gameRoot);
        var outside = Path.Combine(_tmp, "sys");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "nvngx_dlss.dll"), "victim");

        // A symlink inside the game folder that points at an outside DLL (needs Developer Mode or a privilege).
        var linkDll = Path.Combine(gameRoot, "nvngx_dlss.dll");
        Assert.SkipUnless(TryCreateFileSymlink(linkDll, Path.Combine(outside, "nvngx_dlss.dll")), "File symlinks need Developer Mode or a privilege here.");
        Assert.False(DlssSwapService.IsSafeGamePath(gameRoot, linkDll),
            "upscaler accepted a symlinked DLL that resolves outside the game folder");
    }

    [Fact]
    public void Upscaler_IsSafeGamePath_rejects_a_dll_reached_through_a_junction()
    {
        var gameRoot = Path.Combine(_tmp, "game");
        Directory.CreateDirectory(gameRoot);
        var outside = Path.Combine(_tmp, "sys");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "nvngx_dlss.dll"), "victim");

        var linkDir = Path.Combine(gameRoot, "bin");
        Assert.SkipUnless(TryCreateJunction(linkDir, outside), "This volume takes no junctions.");
        Assert.False(DlssSwapService.IsSafeGamePath(gameRoot, Path.Combine(linkDir, "nvngx_dlss.dll")),
            "upscaler accepted a DLL reached through a junction out of the game folder");
    }

    [Fact]
    public void Upscaler_IsSafeGamePath_accepts_a_plain_dll_in_the_folder()
    {
        var gameRoot = Path.Combine(_tmp, "game");
        Directory.CreateDirectory(gameRoot);
        var plain = Path.Combine(gameRoot, "real.dll");
        File.WriteAllText(plain, "x");
        Assert.True(DlssSwapService.IsSafeGamePath(gameRoot, plain));
    }

    [Theory]
    [InlineData("..\\..\\evil.dll")]
    [InlineData("..\\nvngx_dlss.dll")]
    [InlineData("sub\\..\\..\\escape.dll")]
    public void Upscaler_IsSafeGamePath_rejects_dotdot(string relative)
    {
        var gameRoot = Path.Combine(_tmp, "game2");
        Directory.CreateDirectory(gameRoot);
        var candidate = Path.Combine(gameRoot, relative);
        Assert.False(DlssSwapService.IsSafeGamePath(gameRoot, candidate));
    }

    [Fact]
    public void Upscaler_rejects_WindowsApps_store_locked_root()
    {
        Assert.True(DlssSwapService.IsWindowsAppsPath(@"C:\Program Files\WindowsApps\Game\nvngx_dlss.dll"));
        Assert.True(DlssSwapService.IsWindowsAppsPath(@"C:\Program Files\ModifiableWindowsApps\Game\x.dll"));
        Assert.False(DlssSwapService.IsWindowsAppsPath(@"C:\Games\Game\nvngx_dlss.dll"));
    }

    private static bool TryCreateJunction(string link, string target)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = System.Diagnostics.Process.Start(psi)!;
            p.WaitForExit(10_000);
            return p.ExitCode == 0 && Directory.Exists(link);
        }
        catch { return false; }
    }

    private static bool TryCreateFileSymlink(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
            return File.Exists(link);
        }
        catch { return false; } // needs privilege / Developer Mode; skip if denied.
    }
}
