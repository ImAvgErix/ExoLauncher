using static ExoLauncher.Services.DiscordClient.Voice.AudioDevices;

namespace ExoLauncher.Services.DiscordClient.Voice;

internal enum MicKind { UsbMic, XlrInterface, UsbMixer, Headset, Bluetooth, Webcam, BuiltIn, Virtual }

/// <summary>A job a microphone's own hardware or app can do on the voice; Exo Voice does all but Gain, ClipProtection and Eq itself.</summary>
internal enum MicStage { Gain, ClipProtection, LowCut, Gate, Compressor, Eq, DeEsser, NoiseSuppression, Limiter, AutoGain }

/// <summary>
/// How the device does a job against Exo: Better (do it there: before the converter, where nothing after it can
/// redo it), Equal (as well as Exo's), Worse (turn it off and let Exo's do it).
/// </summary>
internal enum MicVersus { Better, Equal, Worse }

/// <summary>One of the device's own settings, and the steps to set it the way Exo recommends.</summary>
internal sealed record MicFeature(MicStage Stage, string Name, MicVersus Versus, string Steps);

internal sealed record MicProfile(string Title, MicKind Kind, string? App, IReadOnlyList<MicFeature> Features, IReadOnlyList<string> SetupTips);

/// <summary>
/// Voice Tune's knowledge of microphones: popular gaming and streaming mics, XLR interfaces, mixers, headsets,
/// webcams and virtual mics, each with its control app, what its own processing does next to Exo Voice, and setup
/// tips. Matched by USB ids (vendor and product, as public USB id lists name them) first, then by name (the longest
/// name that fits wins), then by the brand's USB vendor; anything else gets the tips for its kind. Exo's noise
/// suppression and click-proof gate beat what mics and their apps build in, so on the device only work before the
/// converter (gain, clip protection) counts as better.
/// </summary>
internal static class MicCatalog
{
    /// <summary>A model or a brand. Ids are vendor &lt;&lt; 16 | product; Vendor, when set, makes it the brand's fallback.</summary>
    private sealed record Entry(string Title, MicKind Kind, string? App, uint[] Ids, string[] Names, MicFeature[] Features, string[] Tips, ushort Vendor = 0);

    private const string Target = "your loudest words reach about −12 dB";
    private const string CloseTip = "Speak from about a hand's width away, a little off to the side so breaths and pops miss it.";
    private const string HeadsetTip = "Keep the mic about two fingers from the corner of your mouth, not straight in front of it.";
    private const string CardioidTip = "Set the pickup pattern to cardioid (the heart) for one voice.";
    private const string WindowsGainTip = $"This mic's gain is Windows' input level: in Settings > System > Sound, open the mic and set Input volume so {Target}.";
    private const string NoKnobTip = $"It has no gain knob: in Settings > System > Sound, open the mic and set Input volume so {Target}.";
    private const string EndTip = "Point the end of the mic at your mouth from about a hand's width away.";
    private const string VoiceHighPass = "; Exo's high-pass is set for your voice.";
    private const string Leveler = " so it doesn't stack with Exo's leveler.";

    private static MicFeature Knob(string where) => new(MicStage.Gain, "Gain", MicVersus.Better, $"Turn {where} so {Target}.");

    private static readonly MicFeature[] WaveLink =
    [
        new(MicStage.ClipProtection, "Clipguard", MicVersus.Better, "In Wave Link, open your Wave mic's settings and turn on Clipguard."),
        new(MicStage.LowCut, "Low Cut", MicVersus.Worse, "In Wave Link, open your Wave mic's settings and turn off the low cut" + VoiceHighPass),
        new(MicStage.NoiseSuppression, "Voice Focus (Wave Link)", MicVersus.Worse, "In Wave Link, open your mic's effects and turn off Voice Focus."),
    ];

    private static readonly MicFeature[] BlueVoice =
    [
        new(MicStage.NoiseSuppression, "Noise Reduction", MicVersus.Worse, "In G HUB, open the mic's Microphone tab and turn off Blue VO!CE's Noise Reduction."),
        new(MicStage.Gate, "Gate", MicVersus.Worse, "In G HUB, open the mic's Microphone tab and turn off Blue VO!CE's Gate."),
        new(MicStage.Compressor, "Compressor", MicVersus.Equal, "In G HUB, open the mic's Microphone tab and turn off Blue VO!CE's Compressor" + Leveler),
    ];

    private static readonly MicFeature[] ScarlettFourth =
    [
        new(MicStage.Gain, "Auto Gain", MicVersus.Better, "Press Select until your mic's input lights, press Auto and talk as you would for 10 seconds."),
        new(MicStage.ClipProtection, "Clip Safe", MicVersus.Better, "Press Select until your mic's input lights, then press Safe (it lights green)."),
    ];

    // SSL 2 and 2+ MKII: an analog 75 Hz high-pass ahead of the converter, which keeps rumble from eating its headroom.
    private static readonly MicFeature SslHpf = new(MicStage.LowCut, "HPF", MicVersus.Equal, "Press HPF on the SSL to cut rumble before it reaches the converter.");

    private static MicFeature[] Motiv(string model, bool compressor, bool denoiser, bool highPass)
    {
        var features = new List<MicFeature>
        {
            new(MicStage.AutoGain, "Auto Level", MicVersus.Equal, $"In MOTIV Mix, select the {model}, click the gear next to its name and switch from Auto Level to manual gain, so your level holds still."),
            new(MicStage.Gain, "Gain", MicVersus.Better, $"In MOTIV Mix, with manual gain, set the {model}'s gain so {Target}."),
        };
        if (compressor) features.Add(new(MicStage.Compressor, "Compressor", MicVersus.Equal, $"In MOTIV Mix, turn off the {model}'s Compressor" + Leveler));
        if (denoiser) features.Add(new(MicStage.NoiseSuppression, "Real-time Denoiser", MicVersus.Worse, $"In MOTIV Mix, turn off the {model}'s Real-time Denoiser."));
        if (highPass) features.Add(new(MicStage.LowCut, "High Pass Filter", MicVersus.Worse, $"In MOTIV Mix, turn off the {model}'s High Pass Filter" + VoiceHighPass));
        return [.. features];
    }

    private static MicFeature[] GoXlr(string model) =>
    [
        new(MicStage.Gain, "Gain", MicVersus.Better, "In the GoXLR App's mic setup, pick your mic type and set the gain so the meter reads Good, reaching Loud only on your loudest words."),
        new(MicStage.Gate, "Gate", MicVersus.Worse, $"In the GoXLR App's Mic tab, set the {model}'s Gate threshold all the way down so it never closes."),
        new(MicStage.Compressor, "Compressor", MicVersus.Equal, $"In the GoXLR App's Mic tab, set the {model}'s Compressor so it barely acts; Exo's leveler does this job."),
    ];

    private static MicFeature[] RodeCentral(string model) =>
    [
        new(MicStage.Gate, "Noise Gate", MicVersus.Worse, $"In RØDE Central, select the {model} and turn off Noise Gate."),
        new(MicStage.Compressor, "Compressor", MicVersus.Equal, $"In RØDE Central, select the {model} and turn off Compressor" + Leveler),
        new(MicStage.LowCut, "High-Pass Filter", MicVersus.Worse, $"In RØDE Central, select the {model} and turn off High-Pass Filter" + VoiceHighPass),
    ];

    private static MicFeature[] RodeCaster(string model) =>
    [
        new(MicStage.Gain, "Gain", MicVersus.Better, $"On the {model}, tap the button above your mic's fader, pick your mic in the mic locker, then fine-tune the level so {Target}."),
        new(MicStage.Gate, "Noise Gate", MicVersus.Worse, $"On the {model}, open your mic channel's Processing, tap Advanced and turn off Noise Gate."),
        new(MicStage.Compressor, "Compressor", MicVersus.Equal, $"On the {model}, open your mic channel's Processing, tap Advanced and turn off Compressor" + Leveler),
        new(MicStage.LowCut, "High-pass Filter", MicVersus.Worse, $"On the {model}, open your mic channel's Processing, tap Advanced and turn off High-pass Filter" + VoiceHighPass),
    ];

    private static MicFeature[] Smartgain(string model) =>
        [new(MicStage.Gain, "Smartgain", MicVersus.Better, $"On the {model}, press Smartgain, tap your mic's input, press Smartgain again and talk at your loudest until it flashes green.")];

    private static MicFeature[] MaonoLink(string model) =>
    [
        new(MicStage.Gain, "Microphone Gain", MicVersus.Better, $"Turn the {model}'s knob (or Microphone Gain in Maono Link) so {Target}."),
        new(MicStage.NoiseSuppression, "Noise Cancellation", MicVersus.Worse, $"In Maono Link, turn off the {model}'s Noise Cancellation."),
        new(MicStage.Compressor, "Compressor", MicVersus.Equal, $"In Maono Link, turn off the {model}'s Compressor" + Leveler),
    ];

    private static readonly Entry[] Entries =
    [
        // Elgato
        new("Elgato Wave:3", MicKind.UsbMic, "Wave Link", [0x0FD9_0070], ["wave:3", "wave 3"], [Knob("the Wave:3's dial"), .. WaveLink],
            ["Speak into the front of the Wave:3, the side with the dial, from 10 to 20 cm."]),
        new("Elgato Wave:1", MicKind.UsbMic, "Wave Link", [0x0FD9_006E], ["wave:1", "wave 1"], [Knob("the Wave:1's dial"), .. WaveLink], [CloseTip]),
        new("Elgato Wave Neo", MicKind.UsbMic, "Wave Link", [0x0FD9_0098], ["wave neo"], [Knob("the Wave Neo's gain"), WaveLink[2]],
            ["Speak into the front of the Wave Neo from 8 to 12 cm."]),
        new("Elgato Wave XLR", MicKind.XlrInterface, "Wave Link", [0x0FD9_007D, 0x0FD9_00C7], ["wave xlr"], WaveLink, []),
        new("Wave Link mix", MicKind.Virtual, "Wave Link", [], ["wave link"], [WaveLink[2]],
            ["This is a Wave Link mix. Pick your Wave mic itself here so Exo hears it before any effects."]),

        // Shure
        new("Shure MV7+", MicKind.UsbMic, "MOTIV Mix", [0x14ED_1019], ["mv7+"], Motiv("MV7+", compressor: true, denoiser: true, highPass: true),
            ["Point the MV7+ straight at your mouth, 3 to 15 cm away."]),
        new("Shure MV7", MicKind.UsbMic, "MOTIV Mix", [0x14ED_1012], ["mv7"], Motiv("MV7", compressor: true, denoiser: false, highPass: false),
            ["Point the MV7 straight at your mouth, 3 to 15 cm away."]),
        new("Shure MV6", MicKind.UsbMic, "MOTIV Mix", [0x14ED_1026], ["mv6"], Motiv("MV6", compressor: false, denoiser: true, highPass: false), [CloseTip]),
        new("Shure MVX2U", MicKind.XlrInterface, "MOTIV Mix", [0x14ED_1013], ["mvx2u"], Motiv("MVX2U", compressor: true, denoiser: false, highPass: true),
            [$"Leave Windows' input level at 100% and set the gain in MOTIV Mix so {Target}.",
             "The MVX2U comes with phantom power on. Turn it off in MOTIV Mix unless your mic is a condenser."]),

        // TC-Helicon, BEACN
        new("GoXLR", MicKind.UsbMixer, "GoXLR App", [0x1220_8FE0], ["goxlr"], GoXlr("GoXLR"), ["Pick the GoXLR's Chat Mic here."]),
        new("GoXLR Mini", MicKind.UsbMixer, "GoXLR App", [0x1220_8FE4], ["goxlr mini", "goxlrmini"], GoXlr("GoXLR Mini"), ["Pick the GoXLR Mini's Chat Mic here."]),
        new("BEACN Mic", MicKind.UsbMic, "BEACN App", [], ["beacn mic"],
            [
                new(MicStage.Gain, "Gain", MicVersus.Better, $"In the BEACN App's Mic Setup, set the gain so {Target}."),
                new(MicStage.NoiseSuppression, "Noise Suppression", MicVersus.Worse, "In the BEACN App, click the Mic Chain circle, open Noise Suppression and turn it off."),
                new(MicStage.Gate, "Expander", MicVersus.Worse, "In the BEACN App's Mic Chain, turn off the Expander."),
                new(MicStage.Compressor, "Compressor", MicVersus.Equal, "In the BEACN App's Mic Chain, turn off the Compressor" + Leveler),
            ],
            ["Speak into the end of the BEACN Mic from 3 to 8 cm, at a slight angle."], Vendor: 0x33AE),

        // Logitech and Blue
        new("Yeti", MicKind.UsbMic, "G HUB", [0xB58E_9E84], ["yeti"], [Knob("the Yeti's gain knob"), .. BlueVoice],
            ["Speak into the front of the Yeti, the side with the logo, not the top.", CardioidTip]),
        new("Yeti X", MicKind.UsbMic, "G HUB", [0x046D_0AAF, 0x046D_0AD1, 0x046D_0AC6], ["yeti x"], [Knob("the Yeti X's knob"), .. BlueVoice],
            ["Speak into the front of the Yeti X, the side with the logo, not the top.", CardioidTip]),
        new("Yeti Nano", MicKind.UsbMic, "G HUB", [0xB58E_0005, 0x046D_0AB1, 0x046D_0ACF], ["yeti nano"],
            [new(MicStage.Gain, "Gain", MicVersus.Better, $"In G HUB, set the Yeti Nano's gain so {Target}."), .. BlueVoice],
            ["Speak into the front of the Yeti Nano, the side with the logo, not the top.", CardioidTip]),
        new("Yeti GX", MicKind.UsbMic, "G HUB", [0x046D_0AFC], ["yeti gx"],
            [
                Knob("the Yeti GX's wheel"),
                new(MicStage.ClipProtection, "Smart Audio Lock", MicVersus.Better, "After setting the gain, click the wheel in (it lights cyan), or turn on Smart Audio Lock in G HUB's Microphone tab."),
                .. BlueVoice,
            ],
            ["Face the flat side of the windscreen toward you and speak from about a hand's width away."]),
        new("Yeti Orb", MicKind.UsbMic, "G HUB", [0x046D_0AD5], ["yeti orb"], BlueVoice, [CloseTip]),
        new("Blue mic", MicKind.UsbMic, null, [], ["blue snowball"], [], [], Vendor: 0xB58E),
        new("Logitech G PRO X", MicKind.Headset, "G HUB", [0x046D_0AAA, 0x046D_0ABA, 0x046D_0AF7, 0x046D_0AF8], ["g pro x", "pro x wireless", "pro x 2"], BlueVoice, [HeadsetTip]),
        new("Logitech C920", MicKind.Webcam, null, [0x046D_082D, 0x046D_0892, 0x046D_08E5], ["c920"], [], []),
        new("Logitech C922", MicKind.Webcam, null, [0x046D_085C], ["c922"], [], []),
        new("Logitech Brio", MicKind.Webcam, "Logi Tune", [0x046D_085E, 0x046D_086B], ["brio"], [], []),

        // HyperX (Kingston's vendor id, then HP's)
        new("HyperX QuadCast", MicKind.UsbMic, "HyperX NGENUITY", [0x0951_16DF, 0x03F0_0491], ["quadcast"],
            [Knob("the gain knob on the QuadCast's bottom")], [CardioidTip, CloseTip]),
        new("HyperX QuadCast S", MicKind.UsbMic, "HyperX NGENUITY",
            [0x0951_171D, 0x0951_171F, 0x03F0_028C, 0x03F0_048C, 0x03F0_068C, 0x03F0_0F8B, 0x03F0_0294, 0x03F0_0D8B], ["quadcast s"],
            [Knob("the gain knob on the QuadCast S's bottom")], [CardioidTip, CloseTip]),
        new("HyperX QuadCast 2", MicKind.UsbMic, "HyperX NGENUITY", [0x03F0_07AF, 0x03F0_07B4], ["quadcast 2"],
            [Knob("the QuadCast 2's knob (or its gain in NGENUITY)")], [CardioidTip, CloseTip]),
        new("HyperX QuadCast 2 S", MicKind.UsbMic, "HyperX NGENUITY", [0x03F0_0D84], ["quadcast 2 s", "quadcast 2s"],
            [Knob("the QuadCast 2 S's knob (or its gain in NGENUITY)")], [CardioidTip, CloseTip]),
        new("HyperX SoloCast", MicKind.UsbMic, "HyperX NGENUITY", [0x0951_170F, 0x03F0_0592, 0x03F0_078B, 0x03F0_098B, 0x03F0_0B8B, 0x03F0_0FBF], ["solocast"], [], []),
        new("HyperX SoloCast 2 Pro", MicKind.UsbMic, "HyperX NGENUITY", [], ["solocast 2 pro"],
            [new(MicStage.NoiseSuppression, "AI Noise Reduction", MicVersus.Worse, "In HyperX NGENUITY, turn off the SoloCast 2 Pro's AI Noise Reduction.")], []),
        new("HyperX Cloud", MicKind.Headset, "HyperX NGENUITY", [0x03F0_089D, 0x03F0_05B7, 0x03F0_06B7, 0x03F0_0C9D, 0x03F0_098D, 0x03F0_0B8D],
            ["hyperx cloud", "cloud iii", "cloud alpha"], [], []),

        // Razer
        new("Razer Seiren V2 Pro", MicKind.UsbMic, "Razer Synapse", [0x1532_0542], ["seiren v2 pro"],
            [
                new(MicStage.ClipProtection, "Analog Gain Limiter", MicVersus.Better, "In Razer Synapse, open the Seiren V2 Pro and turn on Analog Gain Limiter."),
                new(MicStage.LowCut, "High Pass Filter", MicVersus.Worse, "In Razer Synapse, open the Seiren V2 Pro and turn off High Pass Filter" + VoiceHighPass),
                Knob("the Seiren V2 Pro's gain (on the mic or in Synapse)"),
            ], [CloseTip]),
        new("Razer Seiren V2 X", MicKind.UsbMic, "Razer Synapse", [0x1532_0543], ["seiren v2 x"],
            [
                new(MicStage.ClipProtection, "Analog Gain Limiter", MicVersus.Better, "In Razer Synapse, open the Seiren V2 X and turn on Analog Gain Limiter."),
                Knob("the Seiren V2 X's gain"),
            ], [CloseTip]),
        new("Razer Seiren V3 Chroma", MicKind.UsbMic, "Razer Synapse", [0x1532_056F], ["seiren v3 chroma"], [], []),
        new("Razer Seiren V3 Mini", MicKind.UsbMic, "Razer Synapse", [0x1532_056A], ["seiren v3 mini"], [], []),
        new("Razer Seiren", MicKind.UsbMic, "Razer Synapse", [0x1532_0503, 0x1532_0508, 0x1532_0511, 0x1532_0516, 0x1532_0524, 0x1532_0531], ["seiren"], [], []),
        new("Razer BlackShark V2", MicKind.Headset, "Razer Synapse", [0x1532_0528, 0x1532_052E, 0x1532_0555, 0x1532_0557], ["blackshark v2"],
            [
                new(MicStage.NoiseSuppression, "Ambient Noise Reduction", MicVersus.Worse, "In Razer Synapse's mic settings for the headset, turn off Ambient Noise Reduction."),
                new(MicStage.Gate, "Voice Gate", MicVersus.Worse, "In Razer Synapse's mic settings for the headset, turn off Voice Gate."),
                new(MicStage.AutoGain, "Volume Normalization", MicVersus.Equal, "In Razer Synapse's mic settings for the headset, turn off Volume Normalization so your level holds still."),
            ], [HeadsetTip]),

        // SteelSeries; Sonar's processing is on its virtual mic, not the headset's own.
        new("SteelSeries Arctis Nova Pro", MicKind.Headset, "SteelSeries GG", [0x1038_12CB, 0x1038_12E0, 0x1038_12E5, 0x1038_12E6], ["arctis nova pro"], [],
            ["Pull the mic all the way out: it only works fully extended.", HeadsetTip]),
        new("SteelSeries Arctis Nova", MicKind.Headset, "SteelSeries GG",
            [0x1038_2200, 0x1038_22A1, 0x1038_227E, 0x1038_2208, 0x1038_2204, 0x1038_2232, 0x1038_2253, 0x1038_12F6, 0x1038_2244], ["arctis nova"], [], []),
        new("SteelSeries Arctis", MicKind.Headset, "SteelSeries GG", [], ["arctis"], [], []),
        new("SteelSeries Sonar mic", MicKind.Virtual, "SteelSeries GG", [], ["steelseries sonar", "sonar microphone"],
            [
                new(MicStage.NoiseSuppression, "ClearCast AI Noise Cancellation", MicVersus.Worse, "In SteelSeries GG, open Sonar's Microphone tab and turn off ClearCast AI Noise Cancellation."),
                new(MicStage.NoiseSuppression, "Noise Reduction", MicVersus.Worse, "In SteelSeries GG, open Sonar's Microphone tab and turn off Noise Reduction."),
                new(MicStage.Gate, "Noise Gate", MicVersus.Worse, "In SteelSeries GG, open Sonar's Microphone tab and turn off Noise Gate."),
                new(MicStage.Compressor, "Compressor", MicVersus.Equal, "In SteelSeries GG, open Sonar's Microphone tab and turn off Compressor" + Leveler),
            ],
            ["This is Sonar's virtual mic. Pick your headset's own mic here, or turn off Sonar's mic effects so they don't stack with Exo Voice."]),

        // NVIDIA Broadcast's noise removal runs on the GPU and is as good as Exo's, so Exo's stands down for it.
        new("NVIDIA Broadcast mic", MicKind.Virtual, "NVIDIA Broadcast", [], ["nvidia broadcast"],
            [new(MicStage.NoiseSuppression, "Noise Removal", MicVersus.Equal, "Keep Noise Removal on in NVIDIA Broadcast's Microphone settings; Exo's noise suppression stands down for it.")],
            ["This is NVIDIA Broadcast's virtual mic. Make sure NVIDIA Broadcast listens to your real mic."]),
        // AMD Noise Suppression (Adrenalin, driver only: no SDK) cleans the mic on its virtual "AMD Streaming Audio Device".
        new("AMD Noise Suppression mic", MicKind.Virtual, "AMD Software", [], ["amd streaming audio device", "amd noise suppression"],
            [new(MicStage.NoiseSuppression, "AMD Noise Suppression", MicVersus.Equal, "Keep Noise Suppression on for your input in AMD Software's Audio & Video settings; Exo's noise suppression stands down for it.")],
            ["This is AMD Noise Suppression's virtual mic. Make sure AMD Software listens to your real mic."]),
        // Krisp's app cleans the mic on its virtual "Krisp Microphone (Krisp Audio)": cleaning it again would only cost the voice.
        new("Krisp mic", MicKind.Virtual, "Krisp", [], ["krisp microphone", "krisp audio"],
            [new(MicStage.NoiseSuppression, "Noise Cancellation", MicVersus.Equal, "Keep Noise Cancellation on for your microphone in Krisp; Exo's noise suppression stands down for it.")],
            ["This is Krisp's virtual mic. Make sure Krisp listens to your real mic."]),

        // Corsair, Turtle Beach
        new("Corsair HS80", MicKind.Headset, "iCUE", [0x1B1C_0A69, 0x1B1C_0A6A, 0x1B1C_0A6B, 0x1B1C_0A88, 0x1B1C_0A97], ["hs80"], [], []),
        new("Corsair Virtuoso", MicKind.Headset, "iCUE", [0x1B1C_0A47, 0x1B1C_0A49, 0x1B1C_0A4A, 0x1B1C_0A4B, 0x1B1C_0A63, 0x1B1C_2A00], ["virtuoso"], [], []),
        new("Corsair headset", MicKind.Headset, "iCUE", [], ["corsair void", "void pro", "void elite"], [], [], Vendor: 0x1B1C),
        new("Turtle Beach Stream Mic", MicKind.UsbMic, "Turtle Beach Audio Hub", [], ["turtle beach stream"], [], []),

        // RØDE
        new("RØDE NT-USB+", MicKind.UsbMic, "RØDE Central", [0x19F7_0035], ["nt usb+"], RodeCentral("NT-USB+"), [WindowsGainTip, CloseTip]),
        new("RØDE NT-USB Mini", MicKind.UsbMic, "RØDE Connect", [0x19F7_0015], ["nt usb mini"], [], [WindowsGainTip, CloseTip]),
        new("RØDE NT-USB", MicKind.UsbMic, null, [0x19F7_0003], ["nt usb"], [], [WindowsGainTip, CloseTip]),
        new("RØDE PodMic USB", MicKind.UsbMic, "RØDE Central", [0x19F7_004A], ["podmic usb"],
            [
                new(MicStage.Gain, "Gain", MicVersus.Better, $"In RØDE Central, click the PodMic USB, open Audio Setup and set the gain so {Target}."),
                .. RodeCentral("PodMic USB"),
            ], [CloseTip]),
        new("RØDE AI-Micro", MicKind.XlrInterface, "RØDE Central", [], ["rode ai micro"],
            [new(MicStage.LowCut, "High-Pass Filter", MicVersus.Worse, "In RØDE Central, select the AI-Micro and turn off High-Pass Filter" + VoiceHighPass)],
            [$"Set the AI-Micro's gain in RØDE Central so {Target}: Central keeps it, other apps don't."]),
        new("RØDECaster Pro II", MicKind.UsbMixer, "RØDECaster", [0x19F7_0026], ["rodecaster pro ii", "rodecaster pro 2"], RodeCaster("RØDECaster Pro II"), []),
        new("RØDECaster Duo", MicKind.UsbMixer, "RØDECaster", [0x19F7_004E, 0x19F7_0050], ["rodecaster duo"], RodeCaster("RØDECaster Duo"), []),

        // Interfaces
        new("Focusrite Scarlett 2i2 (4th gen)", MicKind.XlrInterface, "Focusrite Control 2", [0x1235_8219], ["scarlett 2i2 4th"], ScarlettFourth, []),
        new("Focusrite Scarlett 4i4 (4th gen)", MicKind.XlrInterface, "Focusrite Control 2", [0x1235_821A], ["scarlett 4i4 4th"], ScarlettFourth, []),
        new("Focusrite Scarlett Solo (4th gen)", MicKind.XlrInterface, "Focusrite Control 2", [0x1235_8218], ["scarlett solo 4th"], [], []),
        new("Focusrite Scarlett Solo", MicKind.XlrInterface, "Focusrite Control", [0x1235_8211], ["scarlett solo"], [], []),
        new("Focusrite Scarlett 2i2", MicKind.XlrInterface, "Focusrite Control", [0x1235_8210], ["scarlett 2i2"], [], []),
        new("Focusrite Scarlett 4i4", MicKind.XlrInterface, "Focusrite Control", [0x1235_8212], ["scarlett 4i4"], [], []),
        new("Focusrite interface", MicKind.XlrInterface, null, [], ["scarlett"], [], [], Vendor: 0x1235),
        new("Audient EVO 4", MicKind.XlrInterface, "EVO", [0x2708_0006], ["evo4", "evo 4", "audient evo4", "audient evo 4"], Smartgain("EVO 4"), []),
        new("Audient EVO 8", MicKind.XlrInterface, "EVO", [0x2708_0007], ["evo8", "evo 8", "audient evo8", "audient evo 8"], Smartgain("EVO 8"), []),
        new("Audient iD4", MicKind.XlrInterface, "iD", [0x2708_0003, 0x2708_0009], ["audient id4"], [], []),
        new("Audient iD14", MicKind.XlrInterface, "iD", [0x2708_0002, 0x2708_0008], ["audient id14"], [], []),
        new("Audient interface", MicKind.XlrInterface, null, [], [], [], [], Vendor: 0x2708),
        new("SSL 2 MKII", MicKind.XlrInterface, null, [0x31E9_0008], ["ssl 2 mk"], [SslHpf], []),
        new("SSL 2+ MKII", MicKind.XlrInterface, null, [0x31E9_0009], ["ssl 2+ mk"], [SslHpf], []),
        new("SSL 2", MicKind.XlrInterface, null, [0x31E9_0001], ["ssl 2"], [], []),
        new("SSL 2+", MicKind.XlrInterface, null, [0x31E9_0002], ["ssl 2+"], [], []),
        new("Solid State Logic interface", MicKind.XlrInterface, null, [], [], [], [], Vendor: 0x31E9),
        new("Universal Audio Volt 76", MicKind.XlrInterface, "UA Connect", [0x2B5A_0022, 0x2B5A_0023, 0x2B5A_0024, 0x2B5A_0029],
            ["volt 176", "volt 276", "volt 476", "volt 876"],
            [new(MicStage.Compressor, "76 Compressor", MicVersus.Equal, "Press 76 Compressor until its light is off" + Leveler + " Then set the gain again.")], []),
        new("Universal Audio Volt", MicKind.XlrInterface, "UA Connect", [0x2B5A_0020, 0x2B5A_0021, 0x2B5A_0028], ["volt 1", "volt 2", "volt 4"], [], []),
        new("Universal Audio interface", MicKind.XlrInterface, null, [], [], [], [], Vendor: 0x2B5A),
        new("MOTU M Series", MicKind.XlrInterface, null, [0x07FD_0008, 0x07FD_000B], ["motu m2", "motu m4", "motu m series"], [], []),
        new("MOTU interface", MicKind.XlrInterface, null, [], [], [], [], Vendor: 0x07FD),
        new("Behringer UMC", MicKind.XlrInterface, null, [0x1397_0503, 0x1397_0504, 0x1397_0507, 0x1397_0508, 0x1397_0509],
            ["umc22", "umc202", "umc204", "umc404", "umc1820", "u phoria"], [], [], Vendor: 0x1397),
        new("PreSonus Revelator io24", MicKind.XlrInterface, "Universal Control", [], ["revelator io24", "io24"],
            [new(MicStage.Gate, "Fat Channel", MicVersus.Worse, "Hold your mic channel's Preset button to bypass the Fat Channel; Exo's gate and leveler do its job.")], []),
        new("PreSonus AudioBox", MicKind.XlrInterface, "Universal Control", [0x194F_0101, 0x194F_0301, 0x194F_0303], ["audiobox"], [], []),
        new("PreSonus Studio", MicKind.XlrInterface, "Universal Control", [0x194F_010A, 0x194F_010B, 0x194F_010C, 0x194F_010D],
            ["studio 24c", "studio 26c", "studio 68c", "studio 1810c", "studio 1824c"], [], []),
        new("PreSonus interface", MicKind.XlrInterface, null, [], [], [], [], Vendor: 0x194F),

        // Fifine, Maono, Samson
        new("Fifine K669", MicKind.UsbMic, null, [0x3142_0020, 0x3142_0100], ["k669", "fifine k669"], [Knob("the knob on the K669")],
            ["Speak to the front of the K669 from 15 to 30 cm."]),
        new("Fifine AmpliGame AM8", MicKind.UsbMic, null, [0x3142_A008, 0x3142_A010], ["ampligame am8", "fifine am8"], [Knob("the top knob on the AM8")],
            ["Speak into the top of the AM8 from 5 to 15 cm."]),
        new("Fifine mic", MicKind.UsbMic, null, [], ["fifine"],
            [
                Knob("the gain knob on the mic"),
                new(MicStage.NoiseSuppression, "AI Noise Cancellation", MicVersus.Worse, "If you use fifine SoundLink, turn off AI Noise Cancellation there."),
                new(MicStage.AutoGain, "Smart Gain", MicVersus.Equal, "If you use fifine SoundLink, turn off Smart Gain there so your level holds still."),
            ], [CloseTip], Vendor: 0x3142),
        new("Maono PD400X", MicKind.UsbMic, "Maono Link", [0x352F_0100], ["pd400x"], MaonoLink("PD400X"), [CloseTip]),
        new("Maono PD200X", MicKind.UsbMic, "Maono Link", [0x352F_0104], ["pd200x"], MaonoLink("PD200X"), [CloseTip]),
        new("Maono PD100X", MicKind.UsbMic, "Maono Link", [0x352F_010C], ["pd100x"], MaonoLink("PD100X"), [CloseTip]),
        new("Maono DM30", MicKind.UsbMic, "Maono Link", [0x352F_0101], ["maono dm30", "dm30 usb"], MaonoLink("DM30"), [CloseTip]),
        new("Maono mic", MicKind.UsbMic, "Maono Link", [], ["maono"], [], [], Vendor: 0x352F),
        new("Samson Q2U", MicKind.UsbMic, null, [0x17A0_0301, 0x17A0_0304, 0x17A0_0307], ["q2u"], [], [NoKnobTip, EndTip]),
        new("Samson Q9U", MicKind.UsbMic, null, [0x17A0_0243], ["q9u"],
            [new(MicStage.LowCut, "Low Cut", MicVersus.Worse, "Leave the Low Cut switch on the Q9U off" + VoiceHighPass)], [NoKnobTip, EndTip]),
        new("Samson mic", MicKind.UsbMic, null, [], [], [], [], Vendor: 0x17A0),
    ];

    internal static MicProfile Describe(MicIdentity id)
    {
        var names = new[] { id.DeviceName, id.InterfaceName, id.EndpointName }.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => Normal(n!)).ToArray();
        var usb = id.UsbVendor is { } vendor && id.UsbProduct is { } product ? (uint)vendor << 16 | product : 0;
        var entry = Entries.FirstOrDefault(e => usb != 0 && e.Ids.Contains(usb))
            ?? Entries.SelectMany(e => e.Names.Where(n => names.Any(name => name.Contains(n, StringComparison.Ordinal))).Select(n => (e, n.Length)))
                .OrderByDescending(m => m.Length).Select(m => m.e).FirstOrDefault()
            ?? Entries.FirstOrDefault(e => e.Vendor != 0 && e.Vendor == id.UsbVendor);
        var bluetooth = id.Bus?.StartsWith("BTH", StringComparison.OrdinalIgnoreCase) == true;
        var kind = bluetooth ? MicKind.Bluetooth : entry?.Kind ?? Infer(id, names);
        var tips = new List<string>(entry is { Tips.Length: > 0 } ? entry.Tips : KindTips(kind));
        if (bluetooth)
            tips.Add(id.NativeSampleRate >= 32000
                ? "A Bluetooth mic carries less of your voice than a wired one. A USB or wired mic will sound clearer."
                : "Over Bluetooth a headset mic carries only phone-call sound, nothing above 8 kHz, and using it drops what you hear to call quality too. A USB or wired mic will sound far clearer.");
        else if (kind != MicKind.Virtual && id.NativeSampleRate is > 0 and < 32000)
            tips.Add($"Windows runs this mic at {id.NativeSampleRate / 1000.0:0.#} kHz, which cuts the top of your voice. In Settings > System > Sound, open the mic and set Format to 48000 Hz if it's offered.");
        var title = entry?.Title ?? (kind is MicKind.UsbMic or MicKind.Headset && id.Bus == "USB" ? id.DeviceName : null) ?? Generic(kind);
        return new MicProfile(title, kind, entry?.App, entry?.Features ?? [], tips);
    }

    /// <summary>
    /// Whether Exo's own stage can stand down for this device: only when the device does it better, or as well for
    /// noise suppression (the one stage of Exo Voice that costs real CPU).
    /// </summary>
    internal static bool StandsDown(MicProfile p, MicStage stage) =>
        p.Features.Any(f => f.Stage == stage && (f.Versus == MicVersus.Better || f.Versus == MicVersus.Equal && stage == MicStage.NoiseSuppression));

    private static readonly string[] VirtualHints = ["virtual", "voicemeeter", "vb audio", "cable output", "voicemod", "krisp"];
    private static readonly string[] WebcamHints = ["webcam", "web cam", "camera", "uvc"];
    private static readonly string[] BuiltInHints = ["microphone array", "realtek", "smart sound", "high definition audio"];

    private static MicKind Infer(MicIdentity id, string[] names)
    {
        bool Any(string[] hints) => names.Any(name => hints.Any(h => name.Contains(h, StringComparison.Ordinal)));
        if (id.Bus is "ROOT" or "SWD" || Any(VirtualHints)) return MicKind.Virtual;
        if (Any(WebcamHints)) return MicKind.Webcam;
        if (names.Any(name => name.Contains("headset", StringComparison.Ordinal))) return MicKind.Headset;
        if (id.Bus == "USB") return MicKind.UsbMic;
        return id.Bus is not null || Any(BuiltInHints) ? MicKind.BuiltIn : MicKind.UsbMic;
    }

    private static string[] KindTips(MicKind kind) => kind switch
    {
        MicKind.XlrInterface => [$"Leave Windows' input level at 100% and set the gain knob on the interface so {Target}.", "Turn on 48V phantom power only for a condenser mic."],
        MicKind.UsbMixer => [$"Set the mic's gain in the mixer so {Target}."],
        MicKind.UsbMic => [$"Set the mic's gain so {Target}.", CloseTip],
        MicKind.Headset => [HeadsetTip],
        MicKind.Webcam => ["A webcam mic hears the whole room. Sit within arm's length and face it, or use a headset or USB mic."],
        MicKind.BuiltIn => ["Built-in audio hears fans, typing and the room. Keep the mic close and face it, or use a headset or USB mic."],
        MicKind.Virtual => ["This is a virtual mic: another app processes your real mic before Exo. Pick your real mic here, or turn off that app's noise suppression so the two don't stack."],
        _ => [],
    };

    private static string Generic(MicKind kind) => kind switch
    {
        MicKind.XlrInterface => "Audio interface",
        MicKind.UsbMixer => "Mixer",
        MicKind.Headset => "Headset",
        MicKind.Bluetooth => "Bluetooth headset",
        MicKind.Webcam => "Webcam mic",
        MicKind.BuiltIn => "Built-in audio",
        MicKind.Virtual => "Virtual mic",
        _ => "USB mic",
    };

    /// <summary>Lowercase, Ø as o, dashes and underscores as spaces, single spaces: "RØDE NT-USB+" is "rode nt usb+".</summary>
    private static string Normal(string name) =>
        string.Join(' ', name.ToLowerInvariant().Replace('ø', 'o').Replace('-', ' ').Replace('_', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries));
}
