using ExoLauncher.Models;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class GameIconArtTests
{
    [Fact]
    public void CacheFileName_UsesIconPrefix()
    {
        Assert.Equal("icon_steam_480.png", GameIconArt.CacheFileName("steam:480"));
        Assert.True(GameIconArt.IsCacheFileName("icon_steam_480.png"));
        Assert.True(GameIconArt.IsCacheUrl("https://covers.exo-launcher.local/icon_steam_480.png"));
        Assert.False(GameIconArt.IsCacheFileName("480.jpg"));
    }

    [Fact]
    public void FindExecutable_PrefersLaunchTargetExe()
    {
        var notepad = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
        if (!File.Exists(notepad)) return;

        var game = new GameEntry
        {
            Id = "local:np",
            Title = "Notepad",
            Store = StoreKind.Local,
            Installed = true,
            Path = Path.GetDirectoryName(notepad),
            LaunchTarget = notepad,
        };

        Assert.Equal(
            Path.GetFullPath(notepad),
            GameIconArt.FindExecutable(game),
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryExtractFromExecutable_WritesDarkPlate()
    {
        var notepad = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
        if (!File.Exists(notepad)) return;

        var dest = Path.Combine(Path.GetTempPath(), "exo-icon-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            Assert.True(GameIconArt.TryExtractFromExecutable(notepad, dest));
            var size = CoverArtService.ReadImageSize(dest);
            Assert.NotNull(size);
            Assert.Equal(GameIconArt.PlateWidth, size.Value.Width);
            Assert.Equal(GameIconArt.PlateHeight, size.Value.Height);
            Assert.True(new FileInfo(dest).Length >= CoverArtService.MinCoverBytes);
            Assert.True(CoverArtService.IsPortraitCover(dest));
        }
        finally
        {
            try { File.Delete(dest); } catch { /* */ }
        }
    }
}
