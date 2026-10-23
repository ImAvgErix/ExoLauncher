using ExoLauncher.Helpers;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the bundled provider extensions (uBlock Origin Lite, SponsorBlock, Better Lyrics) could fail:
/// - a file in an extension's folder changes (an update, or a note added beside it) without its pinned hash, so Exo
///   refuses to install it on every PC that installs fresh, silently;
/// - an extension listed in WebExtensions has no folder, or no manifest, in Assets\Extensions.
/// </summary>
public sealed class WebExtensionsTests
{
    private static string Bundled(string folder)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "ExoLauncher", "Assets", "Extensions", folder);
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException("Assets\\Extensions\\" + folder + " is not in the repository.");
    }

    [Fact]
    public void EveryBundledExtensionMatchesItsPinnedHash()
    {
        foreach (var entry in WebExtensions.Entries)
        {
            var folder = Bundled(entry.Folder);
            Assert.True(File.Exists(Path.Combine(folder, "manifest.json")), entry.Title + " has no manifest.json");
            Assert.Equal(entry.ManifestSha256, WebExtensionManifest.ComputeManifestHash(folder));
        }
    }
}
