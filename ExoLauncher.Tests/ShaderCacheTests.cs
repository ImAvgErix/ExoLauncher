using ExoLauncher.Models;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways Rebuild shaders can go wrong:
/// - it deletes a folder that isn't the game's shader cache (a title's own files, another game's folder, the install);
/// - it misses a cache, so the glitch stays (the campaign's, or Battle.net's _retail_ layout);
/// - it offers itself for a game that isn't Call of Duty, or follows a cache that links to somewhere else.
/// </summary>
public sealed class ShaderCacheTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("exo-shadercache-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Make(params string[] parts)
    {
        var path = Path.Combine([_dir, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Fill(string folder, string name, int bytes) => File.WriteAllBytes(Path.Combine(folder, name), new byte[bytes]);

    private GameEntry Game(string title = "Call of Duty®", string folder = "Call of Duty HQ") =>
        new() { Id = "steam:1938090", Title = title, Store = StoreKind.Steam, Installed = true, Path = Path.Combine(_dir, folder) };

    [Fact]
    public void SteamHqListsEachTitlesCacheAndNothingElse()
    {
        Fill(Make("Call of Duty HQ", "cod26", "shadercache"), "techsets_global.psolib", 300);
        Fill(Make("Call of Duty HQ", "sp26", "shadercache"), "ppsod.dat", 200);
        Fill(Make("Call of Duty HQ", "cod26"), "settings.dat", 50);
        Make("Call of Duty HQ", "cod25");
        Make("Call of Duty HQ", "xpak_cache", "shadercache");
        Make("Call of Duty HQ", "D3D12", "shadercache");

        var caches = GameSettings.ShaderCaches(Game())!;

        Assert.Equal(new[] { Path.Combine(_dir, "Call of Duty HQ", "cod26", "shadercache"), Path.Combine(_dir, "Call of Duty HQ", "sp26", "shadercache") }, caches.Order());
        Assert.Equal(500, GameSettings.ShaderCacheBytes(caches));
    }

    [Fact]
    public void BattleNetsRetailFolderToo()
    {
        Fill(Make("Call of Duty", "_retail_", "cod26", "shadercache"), "code_post_gfx.psolib", 10);

        Assert.Equal(new[] { Path.Combine(_dir, "Call of Duty", "_retail_", "cod26", "shadercache") }, GameSettings.ShaderCaches(Game("Call of Duty", "Call of Duty"))!);
    }

    [Fact]
    public void ClearingLeavesTheTitlesOwnFiles()
    {
        Fill(Make("Call of Duty HQ", "cod26", "shadercache", "nested"), "a.psolib", 10);
        Fill(Make("Call of Duty HQ", "cod26"), "settings.dat", 50);

        GameSettings.ClearShaderCaches(GameSettings.ShaderCaches(Game())!);

        Assert.False(Directory.Exists(Path.Combine(_dir, "Call of Duty HQ", "cod26", "shadercache")));
        Assert.True(File.Exists(Path.Combine(_dir, "Call of Duty HQ", "cod26", "settings.dat")));
        // Cleared and not yet built again: still offered, with nothing in it.
        Assert.Empty(GameSettings.ShaderCaches(Game())!);
    }

    [Fact]
    public void OnlyForCallOfDuty()
    {
        Make("Other Game", "cod26", "shadercache");
        Make("Call of Duty HQ", "content", "shadercache");

        Assert.Null(GameSettings.ShaderCaches(Game("Other Game", "Other Game")));
        Assert.Null(GameSettings.ShaderCaches(Game()));
        Assert.Null(GameSettings.ShaderCaches(Game(folder: "missing")));
    }
}
