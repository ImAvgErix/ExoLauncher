using System.Text;
using ExoLauncher.Adapters;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways Xbox app games in a folder the user picked go missing (Exo used to look only in each drive's XboxGames):
/// - the .GamingRoot header is not "RGBX", or a short or odd-length file throws;
/// - the folder keeps its NUL or is joined to the drive with a doubled separator;
/// - a full path in the file is joined to the drive again.
/// </summary>
public sealed class XboxGamingRootTests
{
    private static byte[] Marker(params string[] folders) =>
        [.. "RGBX"u8, .. BitConverter.GetBytes(folders.Length), .. Encoding.Unicode.GetBytes(string.Concat(folders.Select(f => f + "\0")))];

    [Fact]
    public void TheDefaultMarkerIs28BytesAndNamesXboxGames()
    {
        var marker = Marker("XboxGames");
        Assert.Equal(28, marker.Length);
        Assert.Equal([@"D:\XboxGames"], OfficialInstalledLibraries.GamingRootFolders(@"D:\", marker));
    }

    [Fact]
    public void APickedFolderIsFound()
    {
        Assert.Equal([@"D:\Games\Xbox"], OfficialInstalledLibraries.GamingRootFolders(@"D:\", Marker(@"\Games\Xbox")));
        Assert.Equal([@"E:\Xbox"], OfficialInstalledLibraries.GamingRootFolders(@"D:\", Marker(@"E:\Xbox")));
    }

    [Fact]
    public void AnythingElseNamesNothing()
    {
        Assert.Empty(OfficialInstalledLibraries.GamingRootFolders(@"D:\", "RGB"u8.ToArray()));
        Assert.Empty(OfficialInstalledLibraries.GamingRootFolders(@"D:\", [.. "XXXX"u8, 1, 0, 0, 0, 65, 0, 0, 0]));
        Assert.Empty(OfficialInstalledLibraries.GamingRootFolders(@"D:\", [.. "RGBX"u8, 0, 0, 0, 0, 0, 0, 0]));
    }
}
