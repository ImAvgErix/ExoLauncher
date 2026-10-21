using ExoLauncher.Services;

namespace ExoLauncher.Helpers;

/// <summary>
/// System > Display > NVIDIA color, as NVIDIA Control Panel's "Adjust desktop color settings":
/// brightness, contrast and gamma become the screen's gamma ramp (<see cref="DisplayOutput.Ramp"/>),
/// digital vibrance and hue go to the driver (<see cref="NvidiaDisplay"/>). Kept per screen in
/// <see cref="Models.AppSettings.NvidiaColor"/> only while "Use NVIDIA color settings" is on,
/// with the ramp the screen had before so "Other apps control color" puts it back. Exo puts the
/// values back on start, because Windows and games reset gamma ramps.
/// </summary>
internal static class NvidiaColor
{
    internal sealed record Values(int Brightness, int Contrast, int Gamma, int Vibrance, int Hue);

    /// <summary>NVIDIA's defaults: 50 %, 50 %, gamma 1.00, the driver's vibrance, hue 0°.</summary>
    internal static Values Defaults(int vibrance, int hue = 0) => new(50, 50, 100, vibrance, hue);

    /// <summary>Stored as "brightness,contrast,gamma,vibrance,hue;ramp before, base64"; null when unreadable.</summary>
    internal static (Values Values, ushort[]? Before)? Parse(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return null;
        var parts = stored.Split(';', 2);
        var numbers = parts[0].Split(',');
        if (numbers.Length != 5 || !numbers.All(n => int.TryParse(n, out _))) return null;
        var v = numbers.Select(int.Parse).ToArray();
        ushort[]? before = null;
        try
        {
            if (parts.Length == 2 && Convert.FromBase64String(parts[1]) is { Length: 3 * 256 * 2 } bytes)
            {
                before = new ushort[3 * 256];
                Buffer.BlockCopy(bytes, 0, before, 0, bytes.Length);
            }
        }
        catch (FormatException) { }
        return (new Values(v[0], v[1], v[2], v[3], v[4]), before);
    }

    internal static string Format(Values values, ushort[]? before)
    {
        var text = $"{values.Brightness},{values.Contrast},{values.Gamma},{values.Vibrance},{values.Hue}";
        if (before is not { Length: 3 * 256 }) return text;
        var bytes = new byte[before.Length * 2];
        Buffer.BlockCopy(before, 0, bytes, 0, bytes.Length);
        return text + ";" + Convert.ToBase64String(bytes);
    }

    /// <summary>
    /// Applies all five to the screen: the ramp only while HDR is off (Windows draws HDR without
    /// it), then vibrance and hue. Null when everything went through, else the first plain line.
    /// </summary>
    internal static string? Apply(WindowsHdr.Screen screen, Values values)
    {
        string? message = null;
        if (!screen.Enabled && !WindowsHdr.WriteRamp(screen.GdiName, DisplayOutput.Ramp(values.Brightness, values.Contrast, values.Gamma)))
            message = "Windows refused a change that large to brightness, contrast or gamma.";
        message = NvidiaDisplay.SetVibrance(screen.GdiName, values.Vibrance) ?? message;
        message = NvidiaDisplay.SetHue(screen.GdiName, values.Hue) ?? message;
        return message;
    }

    /// <summary>On Exo's start: every NVIDIA screen set to "Use NVIDIA color settings" gets its values back.</summary>
    internal static void ApplySaved(SettingsService settings)
    {
        var saved = settings.Current.NvidiaColor;
        if (saved.Count == 0) return;
        try
        {
            foreach (var screen in WindowsHdr.All())
                if (saved.TryGetValue(screen.DevicePath, out var stored) && Parse(stored) is { } entry && NvidiaDisplay.DisplayId(screen.GdiName) is not null)
                {
                    var message = Apply(screen, entry.Values);
                    AppLog.Info($"Display: NVIDIA color put back on {screen.GdiName}{(message is null ? "" : ": " + message)}");
                }
        }
        catch (Exception ex) { AppLog.Warn("Display: NVIDIA color on start failed: " + ex.Message); }
    }
}
