using ExoLauncher.Services;
using static ExoLauncher.Services.ControllerLab;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the controller's own settings could go wrong:
/// - a settings report that also changes a game's trigger effects, the audio route or an Edge's preview mode;
/// - the light's color in the wrong bytes, or "off" lighting it up;
/// - muting the speaker muting the microphone (they share a byte);
/// - a Sony headset (Pulse 3D) taken for a controller and its sound turned off in Windows.
/// </summary>
public sealed class ControllerSettingsTests
{
    [Fact]
    public void ADefaultReportTouchesNothingButItsOwnFields()
    {
        var r = SettingsReport(new PadSettings(), 48);
        Assert.Equal(0x02, r[0]);
        Assert.Equal(0x00, r[1]);                      // valid_flag0: no audio volumes or route, no rumble, no trigger effects
        Assert.Equal(0x02 | 0x10 | 0x40, r[2]);        // mute bits, player light, motor power: all at the controller's own
        Assert.Equal(0, r[10]);
        Assert.Equal(0, r[37]);
        Assert.Equal(0, r[39]);                        // the light bar left to games
        Assert.True(OnlySettings(r));
    }

    [Fact]
    public void EachSettingLandsInItsOwnBits()
    {
        var r = SettingsReport(new PadSettings { Light = "#1E90FF", PlayerLight = true, SpeakerOff = true, Vibration = "off", TriggerEffects = "low" }, 64);
        Assert.Equal(64, r.Length);
        Assert.Equal((0x1E, 0x90, 0xFF), (r[45], r[46], r[47]));
        Assert.Equal(0x04, r[2] & 0x04);
        Assert.Equal((0x02, 0x02), (r[39], r[42]));   // lightbar setup: out, so the color shows (on would show the controller's blue)
        Assert.Equal(0x20 | 0x80, r[10]);              // speaker and haptics muted, the microphone not
        Assert.Equal(7 | 5 << 4, r[37]);               // rumble held back fully, triggers to "low"
        Assert.Equal(0x04, r[44]);                     // player one's light
        Assert.True(OnlySettings(r));

        var dark = SettingsReport(new PadSettings { Light = "off", MicOff = true }, 48);
        Assert.Equal((0x02, 0x02), (dark[39], dark[42])); // lightbar setup: out
        Assert.Equal(0x04, dark[2] & 0x04);                // black sent: out alone leaves the last color lit
        Assert.Equal((0, 0, 0), (dark[45], dark[46], dark[47]));
        Assert.Equal(0x10, dark[10]);
        Assert.True(OnlySettings(dark));

        var games = SettingsReport(new PadSettings { Light = "on" }, 48);
        Assert.Equal((0x02, 0x01), (games[39], games[42])); // back to the controller's own light
        Assert.Equal(0, games[2] & 0x04);                    // no color sent
    }

    [Fact]
    public void AnythingElseIsRefused()
    {
        var trigger = SettingsReport(new PadSettings(), 48);
        trigger[11] = 0x21;                            // a trigger effect's mode
        Assert.False(OnlySettings(trigger));
        var route = SettingsReport(new PadSettings(), 48);
        route[1] = 0x80;                               // valid_flag0: audio control
        Assert.False(OnlySettings(route));
        var preview = SettingsReport(new PadSettings(), 64);
        preview[39] = 0x80;                            // an Edge's preview mode
        Assert.False(OnlySettings(preview));
        Assert.False(OnlySettings(new byte[47]));
        Assert.False(Valid(new PadSettings { Light = "blue" }));
        Assert.False(Valid(new PadSettings { Vibration = "max" }));
        Assert.Throws<ArgumentException>(() => SettingsReport(new PadSettings { Light = "#12345" }, 48));
    }

    [Fact]
    public void AGameProfileChangesOnlyWhatItSets()
    {
        var usual = new PadSettings { Light = "#1E90FF", MicOff = true, Vibration = "low" };
        var merged = Merge(usual, new PadOverrides { Vibration = "off", TriggerEffects = "medium" });
        Assert.Equal(usual with { Vibration = "off", TriggerEffects = "medium" }, merged);   // the light and the muted mic stay
        Assert.Equal(usual, Merge(usual, new PadOverrides()));                            // an empty profile changes nothing
        Assert.Null(Merge(usual, new PadOverrides { Light = "games" }).Light);            // "games": left to the game
        Assert.False(Merge(usual, new PadOverrides { MicOff = false }).MicOff);           // a profile can turn the mic back on
        Assert.Equal("off", Merge(null, new PadOverrides { Light = "off" }).Light);       // no usual settings: the controller's own
    }

    [Fact]
    public void AGameStartPicksItsProfileByAnyOfItsIds()
    {
        GameProfile Profile(string id, params string[] games) => new() { Id = id, Name = id, Games = games };
        GameProfile[] profiles = [Profile("a", "steam:10"), Profile("b", "epic:x", "steam:20"), Profile("c", "steam:20")];
        Assert.Equal("a", ProfileFor(profiles, ["steam:10"])!.Id);
        Assert.Equal("b", ProfileFor(profiles, ["gog:1", "epic:x"])!.Id);   // a card's other store
        Assert.Equal("c", ProfileFor(profiles, ["steam:20"])!.Id);           // linked twice: the last listed wins
        Assert.Null(ProfileFor(profiles, ["steam:30"]));
        Assert.False(Valid(Profile("", "steam:10")));
        Assert.False(Valid(new GameProfile { Id = "x", Name = "  " }));
        Assert.False(Valid(new GameProfile { Id = "x", Name = "X", Settings = new() { Vibration = "max" } }));
        Assert.False(Valid(new GameProfile { Id = "x", Name = "X", Settings = new() { Light = "on" } }));   // Exo's own word, not a choice
    }

    [Theory]
    [InlineData(@"{1}.USB\VID_054C&PID_0DF2&MI_00\7&26D31004&0&0000", true)]   // DualSense Edge
    [InlineData(@"{1}.USB\VID_054C&PID_0CE6&MI_00\7&1&0&0000", true)]          // DualSense
    [InlineData(@"{1}.USB\VID_054C&PID_09CC&MI_00\7&1&0&0000", true)]          // DualShock 4
    [InlineData(@"{1}.USB\VID_054C&PID_0CDA&MI_00\7&1&0&0000", false)]         // Pulse 3D headset: Sony, not a controller
    [InlineData(@"{1}.USB\VID_1532&PID_0527&MI_00\7&1&0&0000", false)]         // a USB headset
    [InlineData(@"{1}.HDAUDIO\FUNC_01&VEN_10DE&DEV_0097", false)]
    [InlineData(null, false)]
    public void OnlyControllersSoundIsTurnedOff(string? device, bool controller) => Assert.Equal(controller, Peripherals.IsControllerSound(device));
}
