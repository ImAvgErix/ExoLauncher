using System.Reflection;
using System.Text.RegularExpressions;
using ExoLauncher.Services.DiscordClient.Voice;
using static ExoLauncher.Services.DiscordClient.Voice.AudioDevices;

namespace ExoLauncher.Tests;

/// <summary>
/// Voice Tune's microphone catalog. Ways it could fail silently: a mic whose name says nothing ("USB Audio Device")
/// not found by its USB ids; a model taken for its sibling (a Yeti X for a Yeti, an NT-USB+ for an NT-USB, a 4th gen
/// Scarlett for a 3rd); the same USB id under two models; a Bluetooth headset not told it is band-limited, an
/// unknown USB mic called built-in, a laptop array called a USB mic, a virtual mic taken for hardware; a feature with
/// no steps to follow, or UK spelling in them; a mic's own denoiser or gate making Exo's stand down, or clip
/// protection taken for a reason to drop Exo's limiter.
/// </summary>
public sealed partial class MicCatalogTests
{
    private static MicIdentity Usb(string name, ushort? vendor = null, ushort? product = null, int rate = 48000) =>
        new($"Microphone ({name})", name, name, vendor, product, "USB", rate);

    [Fact]
    public void AMicWithAGenericNameIsFoundByItsUsbIds()
    {
        var wave = MicCatalog.Describe(Usb("USB Audio Device", 0x0FD9, 0x0070));
        Assert.Equal("Elgato Wave:3", wave.Title);
        Assert.Equal("Wave Link", wave.App);
        Assert.Equal("HyperX QuadCast S", MicCatalog.Describe(Usb("USB Audio Device", 0x03F0, 0x0F8B)).Title); // HP's vendor id, not Kingston's
        Assert.Equal(MicKind.XlrInterface, MicCatalog.Describe(Usb("USB Audio Device", 0x1235, 0x8219)).Kind);
    }

    [Theory]
    [InlineData("Yeti Stereo Microphone", "Yeti")]
    [InlineData("Yeti X", "Yeti X")]
    [InlineData("Yeti GX", "Yeti GX")]
    [InlineData("RØDE NT-USB+", "RØDE NT-USB+")]
    [InlineData("RØDE NT-USB Mini", "RØDE NT-USB Mini")]
    [InlineData("RODE NT-USB", "RØDE NT-USB")]
    [InlineData("Shure MV7+", "Shure MV7+")]
    [InlineData("Shure MV7", "Shure MV7")]
    [InlineData("TC-Helicon GoXLR Mini", "GoXLR Mini")]
    [InlineData("TC-Helicon GoXLR", "GoXLR")]
    [InlineData("Scarlett 2i2 4th Gen", "Focusrite Scarlett 2i2 (4th gen)")]
    [InlineData("Scarlett 2i2 USB", "Focusrite Scarlett 2i2")]
    [InlineData("Audient EVO4", "Audient EVO 4")]
    [InlineData("Volt 276", "Universal Audio Volt 76")]
    [InlineData("SSL 2+", "SSL 2+")]
    [InlineData("FIFINE K669 Microphone", "Fifine K669")]
    [InlineData("HD Pro Webcam C920", "Logitech C920")]
    public void TheLongestMatchingNameWins(string name, string title) =>
        Assert.Equal(title, MicCatalog.Describe(Usb(name)).Title);

    [Fact]
    public void ThisPcsFifineIsKnownByItsVendor()
    {
        // What AudioDevices.Identify returned for this PC's mic: 3142:0A80, a model no public list names.
        var fifine = MicCatalog.Describe(new MicIdentity("Microphone (fifine Microphone)", "fifine Microphone", "fifine Microphone", 0x3142, 0x0A80, "USB", 48000));
        Assert.Equal("Fifine mic", fifine.Title);
        Assert.Equal(MicKind.UsbMic, fifine.Kind);
        Assert.Contains(fifine.Features, f => f.Stage == MicStage.Gain && f.Versus == MicVersus.Better);
    }

    [Fact]
    public void ABluetoothHeadsetIsToldItIsBandLimited()
    {
        var headset = MicCatalog.Describe(new MicIdentity("Headset (WH-1000XM5 Hands-Free)", "WH-1000XM5 Hands-Free", null, null, null, "BTHHFENUM", 16000));
        Assert.Equal(MicKind.Bluetooth, headset.Kind);
        Assert.Contains(headset.SetupTips, t => t.Contains("8 kHz", StringComparison.Ordinal));
        Assert.DoesNotContain(headset.SetupTips, t => t.Contains("Format", StringComparison.Ordinal)); // its rate is Bluetooth's, not a setting

        // A known headset over Bluetooth is still Bluetooth.
        Assert.Equal(MicKind.Bluetooth, MicCatalog.Describe(new MicIdentity("Headset (Arctis Nova 7)", "Arctis Nova 7", null, null, null, "BTHENUM", 16000)).Kind);
    }

    [Fact]
    public void AnUnknownUsbMicKeepsItsNameAndGetsTheLevelTip()
    {
        var mic = MicCatalog.Describe(Usb("USB PnP Audio Device", 0x1234, 0x5678));
        Assert.Equal(MicKind.UsbMic, mic.Kind);
        Assert.Equal("USB PnP Audio Device", mic.Title);
        Assert.Empty(mic.Features);
        Assert.Contains(mic.SetupTips, t => t.Contains("−12 dB", StringComparison.Ordinal));
        Assert.Contains(MicCatalog.Describe(Usb("USB PnP Audio Device", 0x1234, 0x5678, rate: 16000)).SetupTips, t => t.Contains("16 kHz", StringComparison.Ordinal));
    }

    [Fact]
    public void BuiltInWebcamAndVirtualMicsAreToldApart()
    {
        var array = MicCatalog.Describe(new MicIdentity("Microphone Array (Realtek(R) Audio)", "Realtek(R) Audio", "Realtek(R) Audio", null, null, "HDAUDIO", 48000));
        Assert.Equal(MicKind.BuiltIn, array.Kind);
        Assert.Equal("Built-in audio", array.Title);
        Assert.Contains(array.SetupTips, t => t.Contains("Keep the mic close", StringComparison.Ordinal));
        Assert.Equal(MicKind.Webcam, MicCatalog.Describe(Usb("USB2.0 HD UVC WebCam")).Kind);
        Assert.Equal(MicKind.Virtual, MicCatalog.Describe(new MicIdentity("CABLE Output (VB-Audio Virtual Cable)", "VB-Audio Virtual Cable", null, null, null, "ROOT", 48000)).Kind);
        Assert.Equal(MicKind.Virtual, MicCatalog.Describe(new MicIdentity("SteelSeries Sonar - Microphone (SteelSeries Sonar Virtual Audio Device)", null, null, null, null, "ROOT", 48000)).Kind);
    }

    [Fact]
    public void AnInterfaceKeepsWindowsAt100AndSetsTheKnob()
    {
        var umc = MicCatalog.Describe(Usb("UMC204HD 192k", 0x1397, 0x0508));
        Assert.Equal(MicKind.XlrInterface, umc.Kind);
        Assert.Contains(umc.SetupTips, t => t.Contains("100%", StringComparison.Ordinal) && t.Contains("gain knob", StringComparison.Ordinal));
        Assert.Equal("Focusrite interface", MicCatalog.Describe(Usb("Focusrite USB Audio", 0x1235, 0x8215)).Title); // an id the catalog doesn't list
    }

    [Fact]
    public void EveryFeatureAndTipIsWrittenOutAndNoIdIsListedTwice()
    {
        var ids = new HashSet<uint>();
        foreach (var entry in Entries())
        {
            var title = (string)Property(entry, "Title");
            foreach (var id in (uint[])Property(entry, "Ids")) Assert.True(ids.Add(id), $"{id:X8} is listed twice ({title})");
            foreach (var feature in (MicFeature[])Property(entry, "Features"))
            {
                Assert.False(string.IsNullOrWhiteSpace(feature.Name), title);
                Assert.True(feature.Steps.Length > 20 && feature.Steps.EndsWith('.'), $"{title}: {feature.Name}");
                Assert.False(UkSpelling().IsMatch(feature.Steps), $"{title}: {feature.Steps}");
            }
            foreach (var tip in (string[])Property(entry, "Tips"))
                Assert.False(UkSpelling().IsMatch(tip), $"{title}: {tip}");
        }
        Assert.True(ids.Count > 100); // the scan saw the catalog
    }

    [Fact]
    public void ExoKeepsItsOwnSuppressionGateAndLimiterForHardware()
    {
        foreach (var entry in Entries())
        {
            var features = (MicFeature[])Property(entry, "Features");
            if ((MicKind)Property(entry, "Kind") != MicKind.Virtual)
                Assert.DoesNotContain(features, f => f.Stage == MicStage.NoiseSuppression && f.Versus != MicVersus.Worse);
            Assert.DoesNotContain(features, f => f.Stage is MicStage.Gate or MicStage.Limiter && f.Versus != MicVersus.Worse);
            Assert.DoesNotContain(features, f => f.Stage == MicStage.Compressor && f.Versus == MicVersus.Better);
        }

        var mv7 = MicCatalog.Describe(Usb("Shure MV7+", 0x14ED, 0x1019));
        Assert.Contains(mv7.Features, f => f.Stage == MicStage.NoiseSuppression);
        Assert.False(MicCatalog.StandsDown(mv7, MicStage.NoiseSuppression));
        Assert.False(MicCatalog.StandsDown(mv7, MicStage.AutoGain)); // Auto Level as good as Exo's rider is no reason to drop it
        Assert.True(MicCatalog.StandsDown(mv7, MicStage.Gain)); // the mic's own gain, before the converter

        // Clipguard is recommended, and Exo's limiter still runs.
        var wave = MicCatalog.Describe(Usb("Elgato Wave:3", 0x0FD9, 0x0070));
        Assert.Contains(wave.Features, f => f.Stage == MicStage.ClipProtection && f.Versus == MicVersus.Better && f.Steps.Contains("Clipguard", StringComparison.Ordinal));
        Assert.False(MicCatalog.StandsDown(wave, MicStage.Limiter));
        Assert.False(MicCatalog.StandsDown(wave, MicStage.LowCut));
        Assert.False(MicCatalog.StandsDown(wave, MicStage.NoiseSuppression));

        // The one suppressor as good as Exo's runs on the GPU, so Exo's stands down for it.
        var broadcast = MicCatalog.Describe(new MicIdentity("Microphone (NVIDIA Broadcast)", null, null, null, null, "ROOT", 48000));
        Assert.True(MicCatalog.StandsDown(broadcast, MicStage.NoiseSuppression));

        // AMD Noise Suppression's virtual mic, by the name AMD Software gives it.
        var amd = MicCatalog.Describe(new MicIdentity("Microphone (AMD Streaming Audio Device)", null, null, null, null, "SWD", 48000));
        Assert.Equal("AMD Noise Suppression mic", amd.Title);
        Assert.True(MicCatalog.StandsDown(amd, MicStage.NoiseSuppression));
    }

    private static IEnumerable<object> Entries() =>
        ((Array)typeof(MicCatalog).GetField("Entries", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!).Cast<object>();

    private static object Property(object entry, string name) => entry.GetType().GetProperty(name)!.GetValue(entry)!;

    [GeneratedRegex(@"\b(colour|favourite|grey|analyse|optimise|centre|metre|licence)\b", RegexOptions.IgnoreCase)]
    private static partial Regex UkSpelling();
}
