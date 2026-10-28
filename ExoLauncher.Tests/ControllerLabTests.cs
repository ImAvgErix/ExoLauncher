using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the Controller Lab could read a DualSense wrong:
/// - Bluetooth's short report read with the USB layout (sticks and triggers from the wrong bytes);
/// - an Edge's active profile or trigger stops read from the wrong bits;
/// - repeated reports counted as fresh ones, or a polling rate pulled down by one pause;
/// - a stick's center called off by the half step every 8-bit stick has;
/// - circularity claimed from a stick barely moved;
/// - the firmware version read from the wrong bytes, or looked up under another model's list.
/// </summary>
public sealed class ControllerLabTests
{
    private static byte[] UsbReport(byte lx = 128, byte ly = 128, uint sensor = 1000, byte edgeProfile = 0, byte stops = 0, byte battery = 0x08)
    {
        var r = new byte[64];
        r[0] = 0x01;
        r[1] = lx; r[2] = ly; r[3] = 127; r[4] = 129; r[5] = 10; r[6] = 250;
        BitConverter.GetBytes(sensor).CopyTo(r, 1 + 27);
        r[1 + 48] = edgeProfile;
        r[1 + 49] = stops;
        r[1 + 52] = battery;
        return r;
    }

    [Fact]
    public void AUsbReportGivesSticksTriggersAndTheClock()
    {
        var s = ControllerLab.Parse(UsbReport(lx: 0, ly: 255, sensor: 0xDEADBEEF), usb: true, edge: false)!.Value;
        Assert.Equal((0, 255, 127, 129, 10, 250), ((int)s.LX, (int)s.LY, (int)s.RX, (int)s.RY, (int)s.L2, (int)s.R2));
        Assert.Equal(0xDEADBEEFu, s.Sensor);
        Assert.Null(s.Profile);
        Assert.Equal(85, s.Battery);
    }

    [Fact]
    public void AnEdgeSaysItsProfileAndTriggerStops()
    {
        // Profile 3 (Cross) in the high nibble; L2 stop 2 (bits 4-5), R2 stop 1 (bits 6-7).
        var s = ControllerLab.Parse(UsbReport(edgeProfile: 0x30, stops: 0b0110_0000), usb: true, edge: true)!.Value;
        Assert.Equal(3, s.Profile);
        Assert.Equal(2, s.StopL);
        Assert.Equal(1, s.StopR);
    }

    [Fact]
    public void BluetoothsShortReportHasTriggersAtEightAndNine()
    {
        var r = new byte[10] { 0x01, 1, 2, 3, 4, 0x08, 0, 0, 77, 88 };
        var s = ControllerLab.Parse(r, usb: false, edge: false)!.Value;
        Assert.Equal((1, 2, 3, 4, 77, 88), ((int)s.LX, (int)s.LY, (int)s.RX, (int)s.RY, (int)s.L2, (int)s.R2));
        Assert.Null(s.Sensor);
    }

    [Fact]
    public void BluetoothsFullReportStartsOneByteLater()
    {
        var r = new byte[78];
        r[0] = 0x31; r[1] = 0x10; r[2] = 42;
        Assert.Equal(42, ControllerLab.Parse(r, usb: false, edge: false)!.Value.LX);
    }

    [Fact]
    public void RepeatsAreToldByThePadsOwnClock()
    {
        // 1000 reports a second; every fourth repeats the one before (the pad had nothing new).
        const long f = 10_000_000;
        var reports = Enumerable.Range(0, 400).Select(i => ((long)i * 10_000, (uint?)(uint)(i - i / 4))).ToList();
        var p = ControllerLab.Measure(reports, f)!;
        Assert.Equal(1000, p.Rate);
        Assert.Equal(25, p.Repeats!.Value, 0);
        Assert.InRange(p.Fresh!.Value, 745, 755);
    }

    [Fact]
    public void OnePauseDoesntPullTheRateDown()
    {
        const long f = 10_000_000;
        var reports = Enumerable.Range(0, 300).Select(i => ((long)i * 40_000 + (i >= 150 ? 2_000_000 : 0), (uint?)null)).ToList();
        var p = ControllerLab.Measure(reports, f)!;
        Assert.Equal(250, p.Rate);
        Assert.Null(p.Repeats);
        Assert.True(p.P99 >= 4);
    }

    [Fact]
    public void AStickRestingOnTheHalfStepIsCentered()
    {
        var samples = Enumerable.Range(0, 100).Select(i => ((byte)(i % 2 == 0 ? 127 : 128), (byte)128)).ToList();
        var rest = ControllerLab.AtRest(samples)!;
        Assert.Equal(0, rest.OffsetX);
        Assert.Equal(0.4, rest.OffsetY);
        Assert.Equal(1, rest.Noise);
    }

    [Fact]
    public void ARoundGateIsRoundAndReachesTheEdge()
    {
        var samples = Enumerable.Range(0, 720).Select(i =>
        {
            var a = i * Math.PI / 360;
            return ((byte)Math.Round(127.5 + 127.5 * Math.Cos(a)), (byte)Math.Round(127.5 + 127.5 * Math.Sin(a)));
        }).ToList();
        var edge = ControllerLab.Rolled(samples)!;
        Assert.True(edge.Reach >= 99, $"reach {edge.Reach}");
        Assert.True(edge.Circularity < 1, $"circularity {edge.Circularity}");
        Assert.Equal(1, edge.Covered);
    }

    [Fact]
    public void ABarelyMovedStickGivesNoEdge() =>
        Assert.Null(ControllerLab.Rolled([.. Enumerable.Repeat(((byte)250, (byte)128), 200)]));

    [Fact]
    public void TheFirmwareFieldsAreReadFromTheEdgesOwnReport()
    {
        // The user's DualSense Edge, 2026-10-26: built Sep 19 2025, series 0x44, update version 0x0217.
        var hex = "20 53 65 70 20 31 39 20 32 30 32 35 31 30 3A 31 34 3A 33 30 03 00 44 00 16 02 00 01 8B 00 00 01 51 C8 00 00 00 00 00 00 00 00 00 00 17 02 00 00";
        var report = new byte[64];
        hex.Split(' ').Select(b => Convert.ToByte(b, 16)).ToArray().CopyTo(report, 0);
        var fw = ControllerLab.FirmwareOf(report)!;
        Assert.Equal("Sep 19 2025", fw.Built);
        Assert.Equal(0x0217, fw.Update);
        Assert.Equal(0x44, fw.Series);
        Assert.Equal("FwUpdate0044LatestVersion", ControllerLab.LatestKey(fw.Series));
        Assert.Equal("2.17", ControllerLab.VersionText(fw.Update));
        Assert.Equal((ushort)0x0217, ControllerLab.ParseVersion("0x0217"));
    }

    [Theory]
    [InlineData(0x054C, 0x0CE6, true)]
    [InlineData(0x054C, 0x0DF2, true)]
    [InlineData(0x054C, 0x09CC, false)]   // DualShock 4: not yet
    [InlineData(0x045E, 0x0B13, false)]   // Xbox
    public void OnlyDualSensesAreSetUp(int vendor, int product, bool read) =>
        Assert.Equal(read, ControllerLab.Supported(vendor, product));

    [Fact]
    public void ADualShock4ReadsTheSameOverUsbAndBluetooth()
    {
        var usb = new byte[64];
        usb[0] = 0x01; usb[1] = 10; usb[2] = 20; usb[3] = 30; usb[4] = 40; usb[8] = 200; usb[9] = 100;
        BitConverter.GetBytes((ushort)0xBEEF).CopyTo(usb, 10);
        usb[30] = 0x10 | 5; // on the cable, charging, half full
        var bt = new byte[78];
        bt[0] = 0x11; bt[1] = 0xC0;
        usb.AsSpan(1).CopyTo(bt.AsSpan(3));
        var a = ControllerLab.ParseDualShock4(usb, usb: true)!.Value;
        var b = ControllerLab.ParseDualShock4(bt, usb: false)!.Value;
        Assert.Equal((10, 20, 30, 40, 200, 100), ((int)a.LX, (int)a.LY, (int)a.RX, (int)a.RY, (int)a.L2, (int)a.R2));
        Assert.Equal(a, b);
        Assert.Equal(0xBEEFu, a.Sensor);
        Assert.True(a.Charging);
    }

    [Fact]
    public void XInputsUpIsHidsZero()
    {
        var s = ControllerLab.FromXInput(short.MinValue, short.MaxValue, 0, short.MinValue, 255, 0, 7);
        Assert.Equal((0, 0), ((int)s.LX, (int)s.LY));       // full left, full up
        Assert.Equal((127, 255), ((int)s.RX, (int)s.RY));   // centered, full down
        Assert.Equal(7u, s.Sensor);
    }

    [Theory]
    [InlineData(0x80u, -127, 127, 8, 0)]      // a signed byte's -128, clamped
    [InlineData(0x7Fu, -127, 127, 8, 255)]
    [InlineData(0x00u, -127, 127, 8, 127)]
    [InlineData(32768u, 0, 65535, 16, 127)]   // an unsigned 16-bit center
    [InlineData(1023u, 0, 1023, 10, 255)]
    public void HidAxesScaleFromTheirOwnRange(uint raw, int min, int max, int bits, int expected) =>
        Assert.Equal(expected, ControllerLab.Scale(raw, min, max, bits));

    [Theory]
    [InlineData(0x08, "")]                 // power off
    [InlineData(0x0A, "")]                 // pairing key
    [InlineData(0xA0, "")]                 // firmware
    [InlineData(0xF0, "")]
    [InlineData(0xF4, "")]
    [InlineData(0x80, "01 01")]            // the test channel's reset
    [InlineData(0x80, "0C 01 00")]         // a fine-tune write without its 24 bytes
    [InlineData(0x82, "02 01 03")]         // a calibration step that isn't one
    [InlineData(0x68, "02")]               // clearing an Edge's profile slot: Exo leaves its on-board profiles alone
    [InlineData(0x61, "")]                 // writing one
    [InlineData(0x80, "03 02 65 32 40 0D")] // an unlock with the wrong key
    public void WhatCouldHarmAControllerIsNeverSent(int id, string payload) =>
        Assert.False(ControllerLab.Allowed((byte)id, Hex(payload), set: true, edge: true));

    [Fact]
    public void WhatTheLabNeedsIsAllowed()
    {
        Assert.True(ControllerLab.Allowed(0x80, ControllerLab.FinetuneRead(edge: true), set: true, edge: true));
        Assert.True(ControllerLab.Allowed(0x80, ControllerLab.FinetunePayload(new ushort[12]), set: true, edge: false));
        Assert.True(ControllerLab.Allowed(0x82, ControllerLab.RangeEnd, set: true, edge: false));
        // An Edge's on-board profiles are neither written nor read: game profiles stand in for them.
        Assert.False(ControllerLab.Allowed(0x61, new byte[63], set: true, edge: true));
        Assert.False(ControllerLab.Allowed(0x70, [], set: false, edge: true));
    }

    [Theory]
    [InlineData(0x83010102u, 1, false, false, true)]   // a DualSense's centers took
    [InlineData(0x83010202u, 2, false, false, true)]   // its range took
    [InlineData(0x83010101u, 1, false, false, false)]  // still sampling: not done
    [InlineData(0x83010101u, 1, true, false, true)]    // an Edge's first answer to "end"
    [InlineData(0x83010103u, 1, true, true, true)]     // its second
    [InlineData(0x83010312u, 1, true, true, true)]     // its modules keep their own: it lasts until unplugged
    [InlineData(0x83010203u, 2, true, true, true)]
    [InlineData(0x83010312u, 2, true, true, false)]    // that answer is the centers', not the range's
    public void EndsAreReadPerModel(uint answer, int target, bool edge, bool second, bool ended) =>
        Assert.Equal(ended, ControllerLab.Ended(answer, target, edge, second));

    [Fact]
    public void ARangeCalibrationLeavesAMarginSoANormalPushReachesFullOutput()
    {
        // The user's Edge (October 26, 2026): its factory values, then what its own range routine recorded.
        ushort[] factory = [494, 697, 592, 652, 3325, 3414, 3471, 3414, 1972, 2086, 2001, 2066];
        ushort[] ranged = [442, 670, 530, 646, 3413, 3517, 3495, 3503, 1959, 2080, 1992, 2082];
        var trimmed = ControllerLab.WithMargin(ranged)!;
        Assert.Equal((518, 3340), ((int)trimmed[0], (int)trimmed[4]));   // left X: 5% of each half in from the hardest push
        Assert.Equal(ranged[8..], trimmed[8..]);                        // centers stay
        for (var axis = 0; axis < 4; axis++)
        {
            // A push 96% of the way to where the routine saw the edge now gives full output, on both sides.
            Assert.True(0.96 * (ranged[axis + 8] - ranged[axis]) >= trimmed[axis + 8] - trimmed[axis]);
            Assert.True(0.96 * (ranged[axis + 4] - ranged[axis + 8]) >= trimmed[axis + 4] - trimmed[axis + 8]);
        }
        Assert.True(ControllerLab.RangeMoved(ranged, factory));
        Assert.False(ControllerLab.RangeMoved([.. factory[..8], 1960, 2081, 1990, 2080], factory));   // centers alone
        Assert.Null(ControllerLab.WithMargin(new ushort[12]));                                        // nothing odd is written
    }

    [Fact]
    public void AControllerInAnotherPortIsKnownByItsOwnValues()
    {
        // The user's Edge moved ports (October 26, 2026): Windows named it anew, and its calibration looked gone.
        ushort[] factory = [494, 697, 592, 652, 3325, 3414, 3471, 3414, 1972, 2086, 2001, 2066];
        ushort[] calibrated = [494, 697, 592, 652, 3325, 3414, 3471, 3414, 1942, 2090, 1987, 2080];
        var saved = ControllerLab.Saved.Empty with { Original = factory, Calibrated = calibrated };
        Assert.True(ControllerLab.Holds(factory, saved));       // replugged: an Edge's own values are back
        Assert.True(ControllerLab.Holds(calibrated, saved));    // Exo's already put back
        ushort[] another = [480, 702, 600, 640, 3300, 3420, 3460, 3400, 1980, 2070, 2010, 2060];
        Assert.False(ControllerLab.Holds(another, saved));      // another Edge never takes this one's calibration
    }

    [Fact]
    public void FinetuneComesBackAsItWent()
    {
        ushort[] values = [100, 200, 300, 400, 500, 600, 700, 800, 2048, 2047, 2049, 4095];
        var payload = ControllerLab.FinetunePayload(values);
        var reply = new byte[64];
        reply[0] = 0x81; reply[1] = 0x0C; reply[2] = 0x04; reply[3] = 0x02;
        payload.AsSpan(2).CopyTo(reply.AsSpan(4));
        Assert.Equal(values, ControllerLab.Finetune(reply));
        reply[3] = 0x01; // not done yet
        Assert.Null(ControllerLab.Finetune(reply));
    }

    [Theory]
    [InlineData("81 03 03 02 01", "locked")]
    [InlineData("81 03 03 02 00", "unlocked")]
    [InlineData("81 15 01 01 00", "reconnect")]
    public void NvStatusIsRead(string reply, string state) => Assert.Equal(state, ControllerLab.NvState(Hex(reply)));

    private static byte[] Hex(string text) => text.Length == 0 ? [] : [.. text.Split(' ').Select(b => Convert.ToByte(b, 16))];

    [Theory]
    [InlineData("0", "off")]
    [InlineData("2", "on")]
    [InlineData("1", "unsupported")]
    [InlineData(null, "unsupported")]
    public void SteamsPlayStationSettingIsRead(string? value, string expected) =>
        Assert.Equal(expected, ControllerLab.PlayStationSupport(value));
}
