using ExoLauncher.Helpers;
using Microsoft.Win32;

namespace ExoLauncher.Tests;

/// <summary>
/// Turning a store client's or app's start with Windows off and on.
/// Ways it could fail: an unquoted path with spaces ("C:\Riot Games\Riot
/// Client\RiotClientServices.exe --launch-background-mode") or Squirrel's
/// "Update.exe --processStart Discord.exe" is not recognized, so the row
/// never shows; Riot Vanguard's tray or another unrelated entry is matched and
/// turned off; the vendor's own Run value is changed or deleted instead of its
/// StartupApproved entry, so turning it back on can't restore it; the off
/// marker is not the one Windows reads (an odd first byte), so it keeps
/// starting; an app with two entries reads as off while one still starts it;
/// the automatic hand-off turns an app off again after the user turned it back
/// on, or misses the Microsoft Store edition's startup task.
/// </summary>
public sealed class StartupAppsTests : IDisposable
{
    private readonly string _root = @"Software\ExoLauncherTests\" + Guid.NewGuid().ToString("N");
    private readonly RegistryKey _run;
    private readonly RegistryKey _approved;

    public StartupAppsTests()
    {
        _run = Registry.CurrentUser.CreateSubKey(_root + @"\Run");
        _approved = Registry.CurrentUser.CreateSubKey(_root + @"\Approved");
    }

    public void Dispose()
    {
        _run.Dispose();
        _approved.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_root, throwOnMissingSubKey: false);
    }

    [Theory]
    [InlineData("\"C:\\Program Files (x86)\\Steam\\steam.exe\" -silent", "steam")]
    [InlineData("C:\\Program Files (x86)\\Steam\\steam.exe -silent", "steam")]
    [InlineData("C:\\Riot Games\\Riot Client\\RiotClientServices.exe --launch-background-mode", "riot")]
    [InlineData("\"C:\\Users\\a\\AppData\\Local\\Discord\\Update.exe\" --processStart Discord.exe --process-start-args \"--start-inactive\"", "discord")]
    [InlineData("\"C:\\Users\\a\\AppData\\Roaming\\Spotify\\Spotify.exe\" --autostart --minimized", "spotify")]
    [InlineData("\"C:\\Users\\a\\AppData\\Local\\Epic Games\\Launcher\\EpicGamesLauncher.exe\" --autostart", "epic")]
    [InlineData("\"C:\\Program Files (x86)\\Battle.net\\Battle.net Launcher.exe\" --autostarted", "battlenet")]
    public void Recognizes_vendor_entries(string command, string id) =>
        Assert.Equal(id, StartupApps.Match(command)?.Id);

    [Theory]
    [InlineData("\"C:\\Program Files\\Riot Vanguard\\vgtray.exe\"")]
    [InlineData("\"C:\\Users\\a\\Documents\\Lucid.exe\" /startup")]
    [InlineData("\"C:\\Program Files\\Roblox\\Versions\\v\\RobloxPlayerBeta.exe\" --launch-to-tray")]
    [InlineData("765aa5ec-50a2-4916-aba3-8ef112d65372")]
    [InlineData("")]
    public void Leaves_other_entries_alone(string command) => Assert.Null(StartupApps.Match(command));

    [Fact]
    public void Turning_off_marks_StartupApproved_and_keeps_the_vendor_value()
    {
        const string steam = "\"C:\\Program Files (x86)\\Steam\\steam.exe\" -silent";
        _run.SetValue("Steam", steam);
        _run.SetValue("Riot Vanguard", "\"C:\\Program Files\\Riot Vanguard\\vgtray.exe\"");

        Assert.True(Assert.Single(StartupApps.Read(_run, _approved)).Enabled);
        Assert.True(StartupApps.Set(_run, _approved, "steam", enabled: false, DateTime.UtcNow));

        Assert.Equal(steam, _run.GetValue("Steam"));
        var marker = (byte[])_approved.GetValue("Steam")!;
        Assert.Equal(12, marker.Length);
        Assert.Equal(1, marker[0] & 1);
        Assert.Null(_approved.GetValue("Riot Vanguard"));
        Assert.False(Assert.Single(StartupApps.Read(_run, _approved)).Enabled);

        Assert.True(StartupApps.Set(_run, _approved, "steam", enabled: true, DateTime.UtcNow));
        Assert.True(Assert.Single(StartupApps.Read(_run, _approved)).Enabled);
    }

    [Fact]
    public void An_app_with_one_entry_still_on_reads_as_on()
    {
        _run.SetValue("Discord", "\"C:\\a\\Discord\\Update.exe\" --processStart Discord.exe");
        _run.SetValue("Discord PTB", "\"C:\\a\\DiscordPTB\\Update.exe\" --processStart Discord.exe");
        _approved.SetValue("Discord", StartupApps.Approved(false, DateTime.UtcNow), RegistryValueKind.Binary);
        Assert.True(Assert.Single(StartupApps.Read(_run, _approved)).Enabled);
    }

    [Fact]
    public void Hand_off_turns_each_app_off_once_and_respects_one_turned_back_on()
    {
        _run.SetValue("Steam", "\"C:\\Program Files (x86)\\Steam\\steam.exe\" -silent");
        _run.SetValue("RiotClient", "C:\\Riot Games\\Riot Client\\RiotClientServices.exe --launch-background-mode");
        _run.SetValue("Lucid", "\"C:\\Users\\a\\Documents\\Lucid.exe\" /startup");
        using var packages = Registry.CurrentUser.CreateSubKey(_root + @"\Packages");
        using (var task = packages.CreateSubKey(SpotifyInstalls.StoreFamily + @"\SpotifyStartupTask")) task.SetValue("State", 2, RegistryValueKind.DWord);
        using (var task = packages.CreateSubKey(SpotifyInstalls.StoreFamily + @"\Other")) task.SetValue("State", 0, RegistryValueKind.DWord);

        // Riot was handed before and the user turned it back on: it stays on.
        var off = StartupApps.HandOff(_run, _approved, packages, new HashSet<string> { "riot" }, DateTime.UtcNow);

        Assert.Equal(["steam", "spotify"], off);
        Assert.False(StartupApps.Read(_run, _approved).Single(app => app.Id == "steam").Enabled);
        Assert.True(StartupApps.Read(_run, _approved).Single(app => app.Id == "riot").Enabled);
        Assert.Null(_approved.GetValue("Lucid"));
        using (var task = packages.OpenSubKey(SpotifyInstalls.StoreFamily + @"\SpotifyStartupTask")) Assert.Equal(1, task!.GetValue("State"));
        using (var task = packages.OpenSubKey(SpotifyInstalls.StoreFamily + @"\Other")) Assert.Equal(0, task!.GetValue("State"));
        Assert.Empty(StartupApps.HandOff(_run, _approved, packages, new HashSet<string>(off) { "riot" }, DateTime.UtcNow));
    }

    [Fact]
    public void An_app_without_an_entry_is_not_changed() =>
        Assert.False(StartupApps.Set(_run, _approved, "epic", enabled: false, DateTime.UtcNow));

    [Theory]
    [InlineData(null, true)]
    [InlineData(new byte[] { 2, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 6, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 3, 0, 0, 0 }, false)]
    [InlineData(new byte[] { 1, 0, 0, 0 }, false)]
    public void Reads_the_marker_as_Windows_does(byte[]? marker, bool enabled) =>
        Assert.Equal(enabled, StartupApps.IsEnabled(marker));
}
