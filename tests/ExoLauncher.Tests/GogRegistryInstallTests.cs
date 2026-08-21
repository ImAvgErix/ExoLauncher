using ExoLauncher.Adapters;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class GogRegistryInstallTests
{
    [Fact]
    public void CollectRegistryInstalled_KeepsBothWow64AndNativeGames()
    {
        var wow = Path.Combine(Path.GetTempPath(), "exo-gog-wow-" + Guid.NewGuid().ToString("N"));
        var native = Path.Combine(Path.GetTempPath(), "exo-gog-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(wow);
        Directory.CreateDirectory(native);
        var wowExe = Path.Combine(wow, "old.exe");
        var nativeExe = Path.Combine(native, "Game.exe");
        File.WriteAllText(wowExe, "MZ");
        File.WriteAllText(nativeExe, "MZ");
        try
        {
            var records = new GogAdapter.GogRegistryRecord[]
            {
                new("1207658691", "The Witcher 2", wow, "old.exe"),
                new("1423049311", "Celeste", native, "Game.exe"),
            };

            var installed = GogAdapter.CollectRegistryInstalled(records, Directory.Exists, File.Exists);

            Assert.Equal(2, installed.Count);
            Assert.Contains(installed, row => row.Game.Id == "1207658691" && row.Game.Title == "The Witcher 2");
            Assert.Contains(installed, row => row.Game.Id == "1423049311" && row.LaunchExe == nativeExe);
        }
        finally
        {
            try { Directory.Delete(wow, recursive: true); } catch { }
            try { Directory.Delete(native, recursive: true); } catch { }
        }
    }

    [Fact]
    public void CollectRegistryInstalled_DedupesByProductId_AndSkipsMissingFolders()
    {
        var real = Path.Combine(Path.GetTempPath(), "exo-gog-real-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(real);
        try
        {
            var records = new GogAdapter.GogRegistryRecord[]
            {
                new("1423049311", "Celeste", real, "missing.exe"),
                new("1423049311", "Celeste Duplicate", real, null),
                new("999", "Gone", @"C:\definitely-not-a-gog-install-" + Guid.NewGuid().ToString("N"), "Game.exe"),
            };

            var installed = GogAdapter.CollectRegistryInstalled(records, Directory.Exists, File.Exists);

            var game = Assert.Single(installed);
            Assert.Equal("1423049311", game.Game.Id);
            Assert.Equal("Celeste", game.Game.Title);
            Assert.Null(game.LaunchExe);
        }
        finally
        {
            try { Directory.Delete(real, recursive: true); } catch { }
        }
    }
}
