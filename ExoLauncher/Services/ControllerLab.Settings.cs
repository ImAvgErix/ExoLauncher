using System.Globalization;

namespace ExoLauncher.Services;

/// <summary>
/// A DualSense's or Edge's own settings that last until it's unplugged: its light bar, player light, microphone,
/// speaker, vibration and trigger effects. Sent as one output report (0x02) with only these fields marked valid, so
/// nothing else (rumble, a game's trigger effects, the audio route, an Edge's preview mode) changes. The controller
/// forgets them when it disconnects and a game may set its own light, so Exo sends them again each time it connects
/// and after each game. Field offsets as Linux's hid-playstation and dualsensectl name them. Covered by
/// ControllerSettingsTests.
/// </summary>
internal static partial class ControllerLab
{
    /// <summary>
    /// What Exo sets on a controller. Light: "#RRGGBB", "off", or null to leave it to games. Vibration and
    /// TriggerEffects: "full", "medium", "low" or "off". Each property's default is the controller's own behavior.
    /// </summary>
    internal sealed record PadSettings
    {
        public string? Light { get; init; }
        public bool PlayerLight { get; init; }
        public bool MicOff { get; init; }
        public bool SpeakerOff { get; init; }
        public string Vibration { get; init; } = "full";
        public string TriggerEffects { get; init; } = "full";

        /// <summary>Nothing changed from the controller's own: Exo sends it nothing.</summary>
        internal bool IsDefault => this == new PadSettings();
    }

    internal static readonly string[] Strengths = ["full", "medium", "low", "off"];

    // Common block offsets, +1 for the USB report ID.
    private const int ValidFlag1 = 2, PowerSave = 10, ReduceMotorPower = 37, ValidFlag2 = 39, LightbarSetup = 42, PlayerLeds = 44, Red = 45;
    private const byte PowerSaveControl = 0x02, LightbarControl = 0x04, PlayerIndicatorControl = 0x10, VibrationAttenuation = 0x40;
    private const byte LightbarSetupControl = 0x02, LightOn = 0x01, LightOut = 0x02;
    private const byte MicMute = 0x10, SpeakerMute = 0x20, HapticsMute = 0x80;
    private const byte PlayerOne = 0x04; // the middle one of the five lights

    /// <summary>How much the controller holds a motor back: 0 none to 7 the most (dualsensectl's range).</summary>
    private static int Attenuation(string strength) => strength switch { "medium" => 3, "low" => 5, "off" => 7, _ => 0 };

    /// <summary>"#1E90FF" as its three bytes; null when it isn't a color.</summary>
    internal static (byte R, byte G, byte B)? Color(string? text) =>
        text is { Length: 7 } && text[0] == '#' && uint.TryParse(text.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)
            ? ((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb) : null;

    /// <summary>Whether Exo can send these: a known light ("on": the bar lit again, its color left to games), strengths it knows.</summary>
    internal static bool Valid(PadSettings s) =>
        (s.Light is null or "off" or "on" || Color(s.Light) is not null) && Strengths.Contains(s.Vibration) && Strengths.Contains(s.TriggerEffects);

    /// <summary>The USB output report that sets <paramref name="s"/>, <paramref name="length"/> bytes (48 on a DualSense, 64 on an Edge).</summary>
    internal static byte[] SettingsReport(PadSettings s, int length)
    {
        if (length is < 48 or > 64 || !Valid(s)) throw new ArgumentException("Not settings Exo sends.", nameof(s));
        var report = new byte[length];
        report[0] = 0x02;
        report[ValidFlag1] = PowerSaveControl | PlayerIndicatorControl | VibrationAttenuation;
        // Muting the haptics silences rumble too: both play through the same actuators.
        report[PowerSave] = (byte)((s.MicOff ? MicMute : 0) | (s.SpeakerOff ? SpeakerMute : 0) | (s.Vibration == "off" ? HapticsMute : 0));
        report[ReduceMotorPower] = (byte)(Attenuation(s.Vibration) | Attenuation(s.TriggerEffects) << 4);
        report[PlayerLeds] = s.PlayerLight ? PlayerOne : (byte)0;
        if (s.Light is not null)
        {
            // "Light out" hands the bar to the color sent with it, black for off. "Light on" gives it back to the
            // controller's own blue, which hides any color sent with it (seen on an Edge: a color sent that way never showed).
            report[ValidFlag2] = LightbarSetupControl;
            report[LightbarSetup] = s.Light == "on" ? LightOn : LightOut;
            if (s.Light != "on")
            {
                report[ValidFlag1] |= LightbarControl;
                (report[Red], report[Red + 1], report[Red + 2]) = Color(s.Light) ?? ((byte)0, (byte)0, (byte)0);
            }
        }
        return report;
    }

    /// <summary>
    /// Whether a report only sets what <see cref="SettingsReport"/> sets: the one kind of output report Exo sends a
    /// controller. Any other field or valid flag (a game's trigger effects, the audio route, an Edge's preview mode) refuses it.
    /// </summary>
    internal static bool OnlySettings(ReadOnlySpan<byte> report)
    {
        if (report.Length is < 48 or > 64 || report[0] != 0x02) return false;
        if ((report[ValidFlag1] & ~(PowerSaveControl | LightbarControl | PlayerIndicatorControl | VibrationAttenuation)) != 0) return false;
        if ((report[ValidFlag2] & ~LightbarSetupControl) != 0 || report[LightbarSetup] > LightOut) return false;
        if ((report[PowerSave] & ~(MicMute | SpeakerMute | HapticsMute)) != 0 || (report[ReduceMotorPower] & 0x88) != 0) return false;
        for (var i = 1; i < report.Length; i++)
            if (report[i] != 0 && i is not (ValidFlag1 or PowerSave or ReduceMotorPower or ValidFlag2 or LightbarSetup or PlayerLeds or >= Red and <= Red + 2))
                return false;
        return true;
    }
}
