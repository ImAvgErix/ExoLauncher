using System.Text;

namespace ExoLauncher.Services;

/// <summary>
/// The Controller Lab: what a DualSense or DualSense Edge says about itself and how well it reports, measured, never
/// changed. It reads the controller's input reports (what every game reads) and, over USB only, its firmware report
/// (0x20); it sends nothing. Over Bluetooth a feature read switches the pad to its full reports until it reconnects, and
/// an older game that reads the short ones then loses it, so the lab only listens there.
///
/// Layouts follow Linux's hid-playstation.c and SDL's hidapi PS5 driver (data offsets; a USB report's data starts at
/// byte 1, a Bluetooth 0x31 report's at byte 2). The ways this could go wrong, covered by ControllerLabTests:
/// - Bluetooth's short report read with the USB layout (sticks and triggers from the wrong bytes);
/// - repeated reports counted as new ones (the pad's own sensor clock tells them apart);
/// - a polling rate read from a pause (the median interval, not the mean);
/// - a stick's center off by the half step every 8-bit stick has (127.5 is the center, not 128);
/// - circularity measured on a stick that was barely moved (too few directions covered says so instead);
/// - the firmware version read from the wrong bytes, or compared with another model's.
/// </summary>
internal static partial class ControllerLab
{
    internal const int SonyVendor = 0x054C, DualSense = 0x0CE6, DualSenseEdge = 0x0DF2;

    internal static bool Supported(int? vendor, int? product) => vendor == SonyVendor && product is DualSense or DualSenseEdge;

    /// <summary>One input report, as the lab uses it. Sticks and triggers 0..255; Sensor: the pad's clock (0.33 µs), when it sends one.</summary>
    internal readonly record struct State(byte LX, byte LY, byte RX, byte RY, byte L2, byte R2, uint? Sensor, int? Profile, int? StopL, int? StopR, int? Battery, bool Charging);

    /// <summary>A report's fields. <paramref name="usb"/>: the link (a Bluetooth pad sends 0x31 once in full mode, 0x01 short until then).</summary>
    internal static State? Parse(ReadOnlySpan<byte> report, bool usb, bool edge)
    {
        if (report.Length == 0) return null;
        // Bluetooth's short report (0x01): sticks, then buttons, then the triggers at bytes 8 and 9; no clock.
        if (!usb && report[0] == 0x01)
            return report.Length < 10 ? null : new State(report[1], report[2], report[3], report[4], report[8], report[9], null, null, null, null, null, false);
        var start = usb && report[0] == 0x01 ? 1 : !usb && report[0] == 0x31 ? 2 : -1;
        if (start < 0 || report.Length < start + 53) return null;
        var data = report[start..];
        int? profile = null, stopL = null, stopR = null;
        if (edge)
        {
            profile = data[48] >> 4 is var p and >= 1 and <= 4 ? p : null;
            stopL = (data[49] >> 4) & 0x3;
            stopR = (data[49] >> 6) & 0x3;
        }
        var level = data[52] & 0x0F;
        var charge = data[52] >> 4;
        return new State(data[0], data[1], data[2], data[3], data[4], data[5], BitConverter.ToUInt32(data[27..31]), profile, stopL, stopR,
            level <= 10 ? Math.Min(100, level * 10 + 5) : null, charge is 1 or 2);
    }

    /// <summary>How the controller reports, from when each report arrived (Stopwatch ticks) and the pad's clock in it.</summary>
    /// <param name="Rate">Reports a second, from the median interval.</param>
    /// <param name="Fresh">New readings a second: reports whose clock moved (what a game gets).</param>
    /// <param name="P99">The 99th percentile interval, ms.</param>
    /// <param name="Jitter">The intervals' standard deviation, ms.</param>
    /// <param name="Repeats">Share of reports that repeated the one before (the clock didn't move), %; null without a clock.</param>
    internal sealed record Polling(double Rate, double? Fresh, double P99, double Jitter, double? Repeats, int Reports);

    internal static Polling? Measure(IReadOnlyList<(long At, uint? Sensor)> reports, long frequency)
    {
        if (reports.Count < 20 || frequency <= 0) return null;
        var intervals = new double[reports.Count - 1];
        for (var i = 1; i < reports.Count; i++) intervals[i - 1] = (reports[i].At - reports[i - 1].At) * 1000.0 / frequency;
        var sorted = intervals.Order().ToArray();
        var median = sorted[sorted.Length / 2];
        if (median <= 0) return null;
        var mean = intervals.Average();
        var jitter = Math.Sqrt(intervals.Sum(x => (x - mean) * (x - mean)) / intervals.Length);
        var p99 = sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(sorted.Length * 0.99) - 1)];
        double? repeats = null, fresh = null;
        if (reports.All(r => r.Sensor is not null))
        {
            var same = 0;
            for (var i = 1; i < reports.Count; i++) if (reports[i].Sensor == reports[i - 1].Sensor) same++;
            repeats = Math.Round(100.0 * same / (reports.Count - 1), 1);
            var seconds = (reports[^1].At - reports[0].At) / (double)frequency;
            fresh = seconds > 0 ? Math.Round((reports.Count - 1 - same) / seconds) : null;
        }
        return new Polling(Math.Round(1000 / median), fresh, Math.Round(p99, 2), Math.Round(jitter, 2), repeats, reports.Count);
    }

    /// <summary>One stick at rest: how far its center is from the true center (% of half its travel, per axis) and how much it wanders (steps).</summary>
    internal sealed record Rest(double OffsetX, double OffsetY, int Noise);

    internal static Rest? AtRest(IReadOnlyList<(byte X, byte Y)> samples)
    {
        if (samples.Count < 20) return null;
        const double center = 127.5;
        var x = samples.Average(s => s.X);
        var y = samples.Average(s => s.Y);
        var noise = Math.Max(samples.Max(s => s.X) - samples.Min(s => s.X), samples.Max(s => s.Y) - samples.Min(s => s.Y));
        return new Rest(Math.Round((x - center) / center * 100, 1), Math.Round((y - center) / center * 100, 1), noise);
    }

    /// <summary>
    /// One stick rolled around its edge: how far it reaches in the eight directions (% of full travel; the shortest is
    /// what limits a game), how round its edge is (circularity error: the edge's mean distance from its own average, %
    /// of it), and how much of the circle the samples covered (under 0.75 the numbers aren't trusted).
    /// </summary>
    internal sealed record Edge(double Reach, double Circularity, double Covered);

    internal static Edge? Rolled(IReadOnlyList<(byte X, byte Y)> samples)
    {
        const int bins = 36;
        var edge = new double[bins];
        foreach (var (sx, sy) in samples)
        {
            var x = (sx - 127.5) / 127.5;
            var y = (sy - 127.5) / 127.5;
            var r = Math.Sqrt(x * x + y * y);
            if (r < 0.5) continue;
            var bin = (int)((Math.Atan2(y, x) + Math.PI) / (2 * Math.PI) * bins) % bins;
            edge[bin] = Math.Max(edge[bin], Math.Min(r, 1.5));
        }
        var hit = edge.Where(r => r > 0).ToArray();
        var covered = hit.Length / (double)bins;
        if (hit.Length < 8) return null;
        // The eight directions: the bins at every 45° (each bin is 10°, so every 4.5 bins: take the nearest).
        var directions = Enumerable.Range(0, 8).Select(i => edge[(int)Math.Round(i * bins / 8.0) % bins]).Where(r => r > 0).ToArray();
        var reach = directions.Length > 0 ? Math.Min(1, directions.Min()) : Math.Min(1, hit.Min());
        var average = hit.Average();
        var circularity = hit.Average(r => Math.Abs(r - average)) / average * 100;
        return new Edge(Math.Round(reach * 100, 1), Math.Round(circularity, 1), Math.Round(covered, 2));
    }

    /// <summary>The firmware report (0x20): when it was built, its update version (what Sony's updater compares) and its series (which list to compare with).</summary>
    internal sealed record Firmware(string Built, ushort Update, byte Series);

    internal static Firmware? FirmwareOf(ReadOnlySpan<byte> report)
    {
        if (report.Length < 46 || report[0] != 0x20) return null;
        var built = Encoding.ASCII.GetString(report[1..12]).Trim('\0', ' ');
        return new Firmware(built, BitConverter.ToUInt16(report[44..46]), report[22]);
    }

    /// <summary>The key in Sony's list (fwupdater.dl.playstation.net/fwupdater/info.json) for a series: FwUpdate0044LatestVersion for the Edge.</summary>
    internal static string LatestKey(byte series) => $"FwUpdate00{series:X2}LatestVersion";

    /// <summary>A version as Sony's app shows it: 0x0217 is 2.17.</summary>
    internal static string VersionText(ushort update) => $"{update >> 8:X}.{update & 0xFF:X2}";

    /// <summary>Sony's "0x0217".</summary>
    internal static ushort? ParseVersion(string? text) =>
        text is not null && text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && ushort.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber, null, out var v) ? v : null;
}
