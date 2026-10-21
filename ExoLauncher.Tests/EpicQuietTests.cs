using ExoLauncher.Adapters;
using System.Text.Json.Nodes;

namespace ExoLauncher.Tests;

/// <summary>
/// Starting the Epic Games Launcher in its tray and quieting its own toasts.
/// Ways it could fail: the new launcher gets the old -silent, which it does
/// not know, so its window opens on every Exo start; the old one gets
/// --autostart; quieting drops or reorders Epic's other settings; a value the
/// user already had off is recorded as on.
/// </summary>
public sealed class EpicQuietTests
{
    // From this PC's settings\app-settings.json (new launcher 0.97.0), trimmed.
    private const string Settings = """
        {
          "defaultInstallDir": "C:\\Users\\a\\Epic Games",
          "minimizeToTray": true,
          "startOnBoot": false,
          "enableNotifications": true,
          "showWhatsNewOnStartup": false,
          "topBar": { "hideLabels": true }
        }
        """;

    [Theory]
    [InlineData(@"C:\Users\a\AppData\Local\Epic Games\Launcher\EpicGamesLauncher.exe", "--autostart")]
    [InlineData(@"C:\Program Files (x86)\Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe", "-silent")]
    public void Each_launcher_gets_its_own_tray_start_flag(string exe, string flag) =>
        Assert.Equal(flag, EpicAdapter.QuietStartFlag(exe));

    [Fact]
    public void Quiet_turns_off_only_what_was_on_and_keeps_the_rest()
    {
        var (json, before) = EpicQuietSettings.Quiet(Settings)!.Value;
        var settings = JsonNode.Parse(json)!.AsObject();
        Assert.False(settings["enableNotifications"]!.GetValue<bool>());
        Assert.False(settings["showWhatsNewOnStartup"]!.GetValue<bool>());
        Assert.True(settings["minimizeToTray"]!.GetValue<bool>());
        Assert.True(settings["topBar"]!["hideLabels"]!.GetValue<bool>());
        Assert.Equal(["enableNotifications"], before.Keys);
    }

    [Fact]
    public void Already_quiet_changes_nothing() =>
        Assert.Null(EpicQuietSettings.Quiet("""{ "enableNotifications": false, "showWhatsNewOnStartup": false }"""));
}
