using ExoLauncher.Adapters;

namespace ExoLauncher.Tests;

/// <summary>
/// The family games Steam last listed, kept per account for while Steam is closed (Exo closes it when idle: 107 of 217
/// games left the library). Ways it could fail: another account's family shows; a share Steam stopped listing stays;
/// a damaged file throws or invents ids.
/// </summary>
public sealed class SteamFamilySharesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "exo-family-" + Guid.NewGuid().ToString("N"));
    private readonly string _was = SteamFamilyShares.FilePath;

    public SteamFamilySharesTests() => SteamFamilyShares.FilePath = Path.Combine(_dir, "steam-family.json");

    [Fact]
    public void EachAccountKeepsItsOwnFamilyListUntilSteamListsItAgain()
    {
        SteamFamilyShares.Remember("steam:111", new HashSet<string> { "2767030", "1245620" });
        SteamFamilyShares.Remember("steam:222", new HashSet<string> { "730" });
        Assert.Equal(["1245620", "2767030"], SteamFamilyShares.Recall("steam:111")!.Order());
        Assert.Equal(["730"], SteamFamilyShares.Recall("steam:222")!);
        Assert.Null(SteamFamilyShares.Recall("steam:333"));

        SteamFamilyShares.Remember("steam:111", new HashSet<string> { "2767030" });   // one share stopped
        Assert.Equal(["2767030"], SteamFamilyShares.Recall("steam:111")!);
        SteamFamilyShares.Remember("steam:111", new HashSet<string>());               // left the family
        Assert.Empty(SteamFamilyShares.Recall("steam:111")!);
    }

    [Fact]
    public void ADamagedFileReadsAsNothingKept()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SteamFamilyShares.FilePath, "{ not json");
        Assert.Null(SteamFamilyShares.Recall("steam:111"));
        File.WriteAllText(SteamFamilyShares.FilePath, """{"steam:111":["730","../x","12a"]}""");
        Assert.Equal(["730"], SteamFamilyShares.Recall("steam:111")!);
    }

    public void Dispose()
    {
        SteamFamilyShares.FilePath = _was;
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}
