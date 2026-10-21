using ExoLauncher.Services.DiscordClient.Voice;
using Xunit;

namespace ExoLauncher.Tests;

// Ways this goes wrong: Lucid installed but turned off is still named (its block stays in config.txt and only its
// plugin's own settings say it is off); an Include is taken as processing without reading what it includes; Lucid's
// block on another device, or Lucid EQ's plugin on the speakers, is blamed on the mic; a missing or looping include
// throws or never ends; Lucid's settings file missing or garbled hides a Lucid that is on.
public sealed class EqualizerApoTests
{
    private const string Mic = "{0deb347c-90b6-43d6-b549-63e63b1b23a9}";

    // The shape Lucid writes: its EQ on the speakers, its voice chain on the microphone.
    private const string Lucid = """
        # Equalizer APO Configuration

        # Lucid EQ begin
        Device: {9709e4ab-6969-4817-ad38-1704eab1c079}
        Include: lucid_temp.txt
        Device: all
        # Lucid EQ end

        # Lucid Voice begin
        Device: {0deb347c-90b6-43d6-b549-63e63b1b23a9}
        Include: lucid_voice.txt
        Device: all
        # Lucid Voice end
        """;

    // lucid_voice.txt as Lucid 6 writes it, on or off: a 0 dB preamp and its plugin.
    private const string LucidVoiceFile = """
        # Lucid Voice
        # Lucid Voice Core: expander, tone shaper, gentle density, de-ess, limiter.

        Preamp: 0.0 dB
        VSTPlugin: Library "C:\Program Files\EqualizerAPO\config\lucid_dsp_0a884e1013db4dff132248ab.dll"
        """;

    private const string LucidEqFile = """
        Preamp: -2.0 dB
        Filter: ON PK Fc 60 Hz Gain -0.2 dB Q 0.6
        VSTPlugin: Library "C:\Program Files\EqualizerAPO\config\lucid_dsp_0a884e1013db4dff132248ab.dll" ChunkData "eyJ9"
        """;

    private static string? Files(string name) => name switch
    {
        "lucid_voice.txt" => LucidVoiceFile,
        "lucid_temp.txt" => LucidEqFile,
        _ => null,
    };

    private static string? Use(string config, string mic, bool lucidOn = true) => VoiceTune.EqualizerApoUse(config, mic, Files, lucidOn);

    [Fact]
    public void Lucid_voice_on_this_mic_is_named()
    {
        Assert.Equal("lucid", Use(Lucid, Mic));
        Assert.Equal("lucid", Use(Lucid.Replace("\n", "\r\n"), Mic.ToUpperInvariant()));
    }

    [Fact]
    public void Lucid_voice_turned_off_is_not_named() => Assert.Null(Use(Lucid, Mic, lucidOn: false));

    [Fact]
    public void Lucid_on_another_device_leaves_this_mic_alone() =>
        Assert.Null(Use(Lucid, "{11111111-2222-3333-4444-555555555555}"));

    [Fact]
    public void Lucid_eq_on_every_device_is_processing_even_with_lucid_voice_off() =>
        Assert.Equal("apo", Use("Include: lucid_temp.txt", Mic, lucidOn: false));

    [Fact]
    public void An_include_counts_only_for_what_it_does()
    {
        Assert.Null(Use("Include: missing.txt", Mic));
        Assert.Equal("apo", Use("Preamp: -6 dB\nInclude: missing.txt", Mic));
    }

    [Fact]
    public void An_include_that_includes_itself_ends() =>
        Assert.Null(VoiceTune.EqualizerApoUse("Include: loop.txt", Mic, _ => "Include: loop.txt", lucidOn: true));

    [Fact]
    public void A_zero_preamp_and_comments_change_nothing() =>
        Assert.Null(Use("# nothing\nPreamp: 0.0 dB\nDevice: all\n", Mic));

    [Fact]
    public void Lucids_own_switch_decides_and_is_on_when_unreadable()
    {
        Assert.False(VoiceTune.LucidVoiceOn("""{ "enabled": false, "engine": "lucidCore" }"""));
        Assert.True(VoiceTune.LucidVoiceOn("""{ "enabled": true }"""));
        Assert.True(VoiceTune.LucidVoiceOn(null));
        Assert.True(VoiceTune.LucidVoiceOn("{ not json"));
        Assert.True(VoiceTune.LucidVoiceOn("[]"));
    }
}
