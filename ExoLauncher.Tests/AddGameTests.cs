using ExoLauncher.Adapters;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// Library > Add a game registers the .exe the player picked. It fails if the picked .exe isn't the one launched, the
/// game's folder stops at an engine's build folder (Unreal's Binaries\Win64), the title is an engine's or a build
/// folder's name, or the folder it is in stops a game whose .exe a folder scan wouldn't find.
/// </summary>
public sealed class AddGameTests
{
    [Fact]
    public void GameFolderSkipsBuildFolders()
    {
        Assert.Equal(@"D:\Games\Hollow", LocalAdapter.GameFolderFor(@"D:\Games\Hollow\hollow.exe"));
        Assert.Equal(@"D:\Games\Palworld\Pal", LocalAdapter.GameFolderFor(@"D:\Games\Palworld\Pal\Binaries\Win64\Palworld-Win64-Shipping.exe"));
        Assert.Equal(@"D:\Games\Tool", LocalAdapter.GameFolderFor(@"D:\Games\Tool\bin\x64\tool.exe"));
        Assert.Equal(@"D:\bin", LocalAdapter.GameFolderFor(@"D:\bin\game.exe"));
    }

    [Fact]
    public void TitleFallsBackToTheGameFolder() =>
        Assert.Equal("Hollow", LocalAdapter.TitleFor(@"D:\Games\Hollow\missing.exe", @"D:\Games\Hollow"));

    [Fact]
    public async Task PickedExeIsTheOneLaunched()
    {
        var root = Path.Combine(Path.GetTempPath(), "exo-add-" + Guid.NewGuid().ToString("N"));
        var exe = Path.Combine(root, "Deep Game", "Engine", "Binaries", "Win64", "DeepGame-Win64-Shipping.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllBytes(exe, [0x4D, 0x5A]);
        try
        {
            var catalog = new InstalledGameCatalog(Path.Combine(root, "installed-games.json"));
            var adapter = new LocalAdapter(null, catalog, copyPortableIntoLibrary: false);
            var result = await adapter.InstallAsync(LocalAdapter.CreateAddPortableEntry(), exe, null);
            Assert.True(result.Ok, result.Message);
            var game = Assert.Single(catalog.GetInstalledGames(ExoLauncher.Models.StoreKind.Local));
            Assert.Equal(exe, game.LaunchTarget);
            Assert.Equal(Path.Combine(root, "Deep Game", "Engine"), game.Path);
            Assert.Equal("Engine", game.Title);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
