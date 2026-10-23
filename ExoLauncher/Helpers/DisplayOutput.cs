using System.Runtime.InteropServices;

namespace ExoLauncher.Helpers;

/// <summary>
/// What a screen's cable carries: color format ("rgb", "444", "422", "420" for YCbCr),
/// bits per color and Full (PC, 0-255) or Limited (TV, 16-235) range. YCbCr is always
/// Limited, as the drivers send it.
/// </summary>
internal sealed record OutputColor(string Format, int Bpc, bool Full);

/// <summary>One color slider in System > Display (NVIDIA or AMD): its value, range and the driver's normal value.</summary>
internal sealed record ColorControl(string Key, string Label, int Value, int Min, int Max, int Step, int Normal, string Unit);

/// <summary>
/// The pure choices behind System > Display, and the refresh rate, which Windows sets the
/// same way on every GPU (EnumDisplaySettingsEx / ChangeDisplaySettingsEx, user32).
/// </summary>
internal static class DisplayOutput
{
    internal static readonly string[] Formats = ["rgb", "444", "422", "420"];

    internal static string Label(OutputColor color) =>
        $"{FormatLabel(color.Format)} {color.Bpc}-bit{(color.Format == "rgb" ? color.Full ? " full" : " limited" : "")}";

    internal static string FormatLabel(string format) => format switch
    {
        "rgb" => "RGB",
        "444" => "YCbCr 4:4:4",
        "422" => "YCbCr 4:2:2",
        "420" => "YCbCr 4:2:0",
        _ => format,
    };

    /// <summary>
    /// The best refresh rate for a screen: the main screen's highest; any other screen 60 Hz (the rate it has closest to
    /// it). A second screen at a high rate keeps the GPU's memory clock from idling down on many cards and gives the
    /// desktop compositor more frames to draw beside the game; Windows 11 runs each screen at its own rate, so a
    /// fraction of the main screen's (82 of 165) gains nothing over 60.
    /// </summary>
    internal static int BestRefresh(bool main, IReadOnlyCollection<int> refreshes, int current)
    {
        if (refreshes.Count == 0) return current;
        return main ? refreshes.Max() : refreshes.OrderBy(hz => Math.Abs(hz - 60)).ThenByDescending(hz => hz).First();
    }

    /// <summary>
    /// The best color the link takes at the current mode: RGB Full at its highest depth
    /// (12-bit at most), else RGB Limited, else the fullest YCbCr. Why is one plain line when
    /// that is not RGB Full, or when a deeper color only fits as YCbCr; null otherwise. A panel
    /// whose EDID states its depth gets no deeper than that (8 at least): more bits than the
    /// panel shows only cost link bandwidth (an HDMI screen can take 12-bit on an 8-bit panel).
    /// </summary>
    internal static (OutputColor? Color, string? Why) BestColor(IReadOnlyCollection<OutputColor> supported, int refresh, int? panelBpc = null)
    {
        var cap = panelBpc is { } panel ? Math.Clamp(panel, 8, 12) : 12;
        var usable = supported.Where(color => color.Bpc >= 6 && color.Bpc <= cap).ToList();
        OutputColor? Deepest(string format, bool full) =>
            usable.Where(color => color.Format == format && color.Full == full).MaxBy(color => color.Bpc);

        var rate = refresh > 0 ? $"At {refresh} Hz" : "At this refresh rate";
        if (Deepest("rgb", true) is { } rgb)
        {
            var deeper = usable.Where(color => color.Format != "rgb" && color.Bpc > rgb.Bpc).MaxBy(color => color.Bpc);
            return (rgb, deeper is null ? null
                : $"{rate} {deeper.Bpc}-bit only fits as {FormatLabel(deeper.Format)}; {rgb.Bpc}-bit RGB keeps text and color sharper.");
        }
        if (Deepest("rgb", false) is { } limited)
            return (limited, "This screen only takes RGB in limited range, so blacks are lifted a little. That is the screen, not the cable.");
        foreach (var format in Formats.Skip(1))
            if (Deepest(format, false) is { } ycc)
                return (ycc, $"{rate} the cable or port can't carry RGB, so {FormatLabel(format)} {ycc.Bpc}-bit is the best it takes. " +
                    "A DisplayPort 1.4 or HDMI 2.1 link gives full RGB.");
        return (null, null);
    }

    /// <summary>
    /// The colors System > Display offers: those the driver says the link takes at the current mode
    /// (<paramref name="supported"/>) that the screen's EDID takes too, 6 to 12-bit. With no EDID
    /// the driver's answer stands.
    /// </summary>
    internal static List<OutputColor> Offered(IEnumerable<OutputColor> supported, MonitorCaps? caps, long pixelRate) =>
        supported.Where(color => color.Bpc is >= 6 and <= 12 && caps?.Takes(color, pixelRate) != false).Distinct().ToList();

    /// <summary>The refresh rates offered: Windows' list for the current resolution, less what the EDID rules out; the current one always.</summary>
    internal static int[] OfferedRates(IEnumerable<int> rates, int current, MonitorCaps? caps) =>
        rates.Where(hz => hz == current || caps?.TakesRate(hz) != false).ToArray();

    /// <summary>The screen's EDID name, or "Screen n" when it has none or names itself by a bare part number (an onn monitor reports "100002487").</summary>
    internal static string ScreenName(string? edidName, int number) =>
        edidName is { } name && name.Any(char.IsLetter) ? name.Trim() : $"Screen {number}";

    /// <summary>
    /// The gamma ramp (3 x 256) for desktop brightness and contrast (0-100, 50 leaves it) and
    /// gamma (x100: 30-280, 100 leaves it), the curve NVIDIA's panels draw (the same steps as
    /// NVIDIA's open nvidia-settings, NvCtrlAttributes.c, written here in 0-1 terms): contrast
    /// stretches or squeezes around mid-grey, brightness shifts by up to a third, then gamma.
    /// </summary>
    internal static ushort[] Ramp(int brightness, int contrast, int gamma)
    {
        var stretch = (contrast - 50) / 75.0; // ±1 contrast is ±2/3 of the range
        var shift = (brightness - 50) / 150.0; // ±1 brightness is ±1/3 of the range
        var ramp = new ushort[3 * 256];
        for (var i = 0; i < 256; i++)
        {
            var x = i / 255.0 - 0.5;
            x = (stretch > 0 ? x / (1 - stretch) : x * (1 + stretch)) + 0.5 + shift;
            var value = (ushort)Math.Round(Math.Pow(Math.Clamp(x, 0, 1), 100.0 / Math.Clamp(gamma, 30, 280)) * 65535);
            ramp[i] = ramp[256 + i] = ramp[512 + i] = value;
        }
        return ramp;
    }

    // ── Refresh rate (Windows, every vendor) ─────────────────────────────

    /// <summary>The refresh rates Windows offers at the screen's current resolution, lowest first, and the current one.</summary>
    internal static (int Current, int[] Rates) RefreshRates(string gdiName)
    {
        var current = NewMode();
        if (!EnumDisplaySettingsEx(gdiName, CurrentSettings, ref current, 0)) return (0, []);
        var rates = new SortedSet<int>();
        var mode = NewMode();
        for (var index = 0; EnumDisplaySettingsEx(gdiName, index, ref mode, 0); index++)
        {
            if (mode.dmPelsWidth == current.dmPelsWidth && mode.dmPelsHeight == current.dmPelsHeight
                && mode.dmBitsPerPel == current.dmBitsPerPel && (mode.dmDisplayFlags & Interlaced) == 0 && mode.dmDisplayFrequency > 1)
                rates.Add((int)mode.dmDisplayFrequency);
            mode = NewMode();
        }
        return ((int)current.dmDisplayFrequency, rates.ToArray());
    }

    /// <summary>
    /// Sets the refresh rate at the current resolution for now only: Windows' saved mode stays the old one
    /// until <see cref="SaveRefresh"/>, so a trial Exo never gets to put back (a crash, Exo ended in Task
    /// Manager) is gone after a restart. Null on success, else a plain line.
    /// </summary>
    internal static string? SetRefresh(string gdiName, int hz)
    {
        var mode = NewMode();
        if (!EnumDisplaySettingsEx(gdiName, CurrentSettings, ref mode, 0)) return "Windows did not answer for this screen.";
        if (mode.dmDisplayFrequency == hz) return null;
        mode.dmDisplayFrequency = (uint)hz;
        mode.dmFields = PelsWidth | PelsHeight | DisplayFrequency;
        var status = ChangeDisplaySettingsEx(gdiName, ref mode, IntPtr.Zero, 0, IntPtr.Zero);
        AppLog.Info($"Display: refresh {hz} Hz on {gdiName}: status {status}.");
        return status switch
        {
            0 => null,
            1 => "Windows needs a restart for that refresh rate.",
            _ => $"Windows did not take {hz} Hz on this screen.",
        };
    }

    /// <summary>Saves the mode the screen runs now as Windows' own (a kept change), without setting it again.</summary>
    internal static string? SaveRefresh(string gdiName)
    {
        var mode = NewMode();
        if (!EnumDisplaySettingsEx(gdiName, CurrentSettings, ref mode, 0)) return "Windows did not answer for this screen.";
        mode.dmFields = PelsWidth | PelsHeight | DisplayFrequency;
        var status = ChangeDisplaySettingsEx(gdiName, ref mode, IntPtr.Zero, UpdateRegistry | NoReset, IntPtr.Zero);
        AppLog.Info($"Display: saved {mode.dmDisplayFrequency} Hz on {gdiName}: status {status}.");
        return status == 0 ? null : "Windows did not save that refresh rate; it lasts until you restart.";
    }

    private const int CurrentSettings = -1;
    private const uint Interlaced = 0x2;
    private const uint PelsWidth = 0x80000, PelsHeight = 0x100000, DisplayFrequency = 0x400000;
    private const uint UpdateRegistry = 0x1, NoReset = 0x10000000;

    private static DevMode NewMode() => new() { dmDeviceName = "", dmFormName = "", dmSize = (ushort)Marshal.SizeOf<DevMode>() };

    // DEVMODEW, display variant of the union (wingdi.h).
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsEx(string deviceName, int modeNum, ref DevMode mode, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string deviceName, ref DevMode mode, IntPtr hwnd, uint flags, IntPtr param);
}
