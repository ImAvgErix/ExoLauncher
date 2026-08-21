using ExoLauncher.Adapters;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class SteamScanReliabilityTests
{
    [Fact]
    public void LeftoverAge_UsesDirectoryMtime_NotARecursiveFileWalk()
    {
        var root = Path.Combine(Path.GetTempPath(), "exo-steam-leftover-" + Guid.NewGuid().ToString("N"));
        var nested = Path.Combine(root, "a", "b", "c");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "blob.bin"), new string('x', 1024));
        try
        {
            Directory.SetLastWriteTimeUtc(root, DateTime.UtcNow.AddHours(-72));
            Assert.True(SteamLeftoverCleanup.IsOlderThan(root, TimeSpan.FromHours(48)));

            Directory.SetLastWriteTimeUtc(root, DateTime.UtcNow);
            Assert.False(SteamLeftoverCleanup.IsOlderThan(root, TimeSpan.FromHours(48)));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void AppInfoLoad_ReusesCacheWithinTtlWhenWriteTimeMoves()
    {
        SteamAppInfoNames.ResetCacheForTests();
        var path = Path.Combine(Path.GetTempPath(), "exo-appinfo-" + Guid.NewGuid().ToString("N") + ".vdf");
        var bytes = SteamAppInfoNamesTests.BuildV41((730, "Counter-Strike 2", "game"));
        File.WriteAllBytes(path, bytes);
        try
        {
            var first = SteamAppInfoNames.Load(path);
            Assert.Equal("Counter-Strike 2", first["730"].Name);

            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
            var second = SteamAppInfoNames.Load(path);
            Assert.Same(first, second);
        }
        finally
        {
            SteamAppInfoNames.ResetCacheForTests();
            try { File.Delete(path); } catch { }
        }
    }
}
