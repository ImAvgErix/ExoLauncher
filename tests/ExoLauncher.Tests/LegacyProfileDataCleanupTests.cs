using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class LegacyProfileDataCleanupTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "exo-legacy-profile-cleanup", Guid.NewGuid().ToString("N"));

    [Fact]
    public void CleanupRoot_RemovesOnlyExactRetiredProfileArtifacts()
    {
        Directory.CreateDirectory(_root);
        var profile = Path.Combine(_root, "exo-profile-state.json");
        var tracker = Path.Combine(_root, "tracker-gg-webview");
        var settings = Path.Combine(_root, "settings.json");
        var similarlyNamed = Path.Combine(_root, "exo-profile-state.json.keep");
        File.WriteAllText(profile, "retired");
        Directory.CreateDirectory(tracker);
        File.WriteAllText(Path.Combine(tracker, "Cookies"), "retired");
        File.WriteAllText(settings, "keep");
        File.WriteAllText(similarlyNamed, "keep");

        LegacyProfileDataCleanup.CleanupRoot(_root);

        Assert.False(File.Exists(profile));
        Assert.False(Directory.Exists(tracker));
        Assert.True(File.Exists(settings));
        Assert.True(File.Exists(similarlyNamed));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
