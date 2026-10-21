using ExoLauncher.Helpers;

namespace ExoLauncher.Tests;

/// <summary>
/// Finding Spotify however it was installed. Ways it could fail: only
/// %APPDATA%\Spotify is looked at, so a moved AppData or a Scoop copy reads as
/// "not installed" and onboarding offers to install Spotify again; the Store
/// edition's WindowsApps exe is taken as the classic app, so Exo tries to patch
/// a read-only folder and hand a pipe to a packaged process; an icon value's
/// ",0" or a protocol command's arguments end up in the path; the same exe is
/// tried twice.
/// </summary>
public sealed class SpotifyInstallsTests
{
    private const string Roaming = @"C:\Users\a\AppData\Roaming";
    private const string Profile = @"C:\Users\a";

    [Fact]
    public void The_installer_location_comes_first_and_each_path_once()
    {
        var candidates = SpotifyInstalls.ClassicCandidates(Roaming, Roaming + @"\Spotify", "\"" + Roaming + "\\Spotify\\Spotify.exe\",0",
            "\"" + Roaming + "\\Spotify\\Spotify.exe\" --protocol-uri=\"%1\"", Profile).ToList();
        Assert.Equal([Roaming + @"\Spotify\Spotify.exe", Profile + @"\scoop\apps\spotify\current\Spotify.exe"], candidates);
    }

    [Fact]
    public void A_moved_install_is_found_from_its_uninstall_entry_and_link()
    {
        var candidates = SpotifyInstalls.ClassicCandidates(Roaming, "\"D:\\Apps\\Spotify\"", @"E:\Spotify\Spotify.exe,0",
            @"F:\Tools\Spotify\Spotify.exe --protocol-uri=%1", Profile).ToList();
        Assert.Equal(@"D:\Apps\Spotify\Spotify.exe", candidates[1]);
        Assert.Equal(@"E:\Spotify\Spotify.exe", candidates[2]);
        Assert.Equal(@"F:\Tools\Spotify\Spotify.exe", candidates[3]);
    }

    [Fact]
    public void The_Store_edition_is_never_the_classic_app()
    {
        const string store = @"C:\Program Files\WindowsApps\SpotifyAB.SpotifyMusic_1.250.0.0_x64__zpdnekdrzrea0\Spotify.exe";
        var candidates = SpotifyInstalls.ClassicCandidates(Roaming, null, null, "\"" + store + "\" --protocol-uri=\"%1\"", Profile);
        Assert.DoesNotContain(candidates, path => path.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase));
        Assert.True(SpotifyInstalls.IsPackaged(store));
    }

    [Theory]
    [InlineData(@"C:\Spotify\uninstall.exe")]
    [InlineData("relative\\Spotify.exe")]
    [InlineData("")]
    public void Other_files_are_not_Spotify(string value) =>
        Assert.Equal(2, SpotifyInstalls.ClassicCandidates(Roaming, null, value, value, Profile).Count());
}
