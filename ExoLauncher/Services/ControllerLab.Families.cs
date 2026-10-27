using System.Diagnostics;
using System.Runtime.InteropServices;
using static ExoLauncher.Services.Hid.HidInterop;

namespace ExoLauncher.Services;

/// <summary>
/// Which controllers the lab reads, and how: a DualSense or Edge and a DualShock 4 by their known reports (each with the
/// pad's own clock, so repeats are counted), an Xbox controller through XInput (wired Xbox pads aren't HID), and any
/// other HID gamepad (Switch Pro, 8BitDo, GameSir and the rest) through Windows' description of its axes. Reading is
/// all any of them get: calibration and profiles are Sony's commands, for a DualSense and an Edge only.
/// Covered by ControllerLabTests: a DualShock 4's Bluetooth report read with the USB layout, XInput's Y read upside
/// down (up is positive there, 0 in HID), a signed HID axis read as unsigned.
/// </summary>
internal static partial class ControllerLab
{
    internal enum Family { DualSense, DualShock4, Xbox, Hid }

    /// <summary>How the lab reads this controller; null when it can't.</summary>
    internal static Family? FamilyOf(Peripherals.Peripheral card)
    {
        if (card.Kind != "controller") return null;
        if (Supported(card.VendorId, card.ProductId)) return Family.DualSense;
        if (card.Model == "dualshock4" && Gamepad(card) is not null) return Family.DualShock4;
        if (Gamepad(card) is not null) return Family.Hid;
        return card.Model == "xbox" ? Family.Xbox : null;
    }

    /// <summary>The gamepad or joystick collection a game reads.</summary>
    private static Peripherals.Collection? Gamepad(Peripherals.Peripheral card) =>
        card.Collections.FirstOrDefault(c => c is { Page: 0x01, Usage: 0x05 }) ?? card.Collections.FirstOrDefault(c => c is { Page: 0x01, Usage: 0x04 });

    /// <summary>A DualShock 4's report: USB and Bluetooth's short 0x01 share a layout; Bluetooth's full 0x11 starts two bytes later. Its clock: a u16 in 5.33 µs steps.</summary>
    internal static State? ParseDualShock4(ReadOnlySpan<byte> report, bool usb)
    {
        var start = report.Length > 0 && report[0] == 0x01 ? 1 : !usb && report.Length > 0 && report[0] == 0x11 ? 3 : -1;
        if (start < 0 || report.Length < start + 9) return null;
        var data = report[start..];
        uint? clock = data.Length >= 11 && (usb || start == 3) ? BitConverter.ToUInt16(data[9..11]) : null;
        int? battery = null;
        var charging = false;
        if (data.Length >= 30 && (usb || start == 3))
        {
            var level = data[29] & 0x0F;
            var cable = (data[29] & 0x10) != 0;
            battery = Math.Min(100, cable ? level * 100 / 11 : level * 10 + 5);
            charging = cable && level < 11;
        }
        return new State(data[0], data[1], data[2], data[3], data[7], data[8], clock, null, null, null, battery, charging);
    }

    /// <summary>XInput's state as the lab's: sticks to 0..255 with up at 0, as HID has it.</summary>
    internal static State FromXInput(short lx, short ly, short rx, short ry, byte lt, byte rt, uint packet)
    {
        static byte Axis(short v) => (byte)((v + 32768) / 257);
        return new State(Axis(lx), (byte)(255 - Axis(ly)), Axis(rx), (byte)(255 - Axis(ry)), lt, rt, packet, null, null, null, null, false);
    }

    /// <summary>A HID axis value to 0..255 from its logical range; a signed range's raw bits are sign-extended first.</summary>
    internal static byte Scale(uint raw, int min, int max, int bits)
    {
        long value = raw;
        if (min < 0 && bits is > 0 and < 32 && (raw & (1u << (bits - 1))) != 0) value = raw | ~((1L << bits) - 1);
        if (max <= min) return 128;
        return (byte)Math.Clamp((value - min) * 255 / (max - min), 0, 255);
    }

    /// <summary>
    /// Every state the controller sends for <paramref name="duration"/>, with when it came (Stopwatch ticks): the HID
    /// reports a game reads, parsed for the controller's family, or XInput polled every millisecond.
    /// </summary>
    internal static Task ListenAsync(Peripherals.Peripheral card, TimeSpan duration, Action<long, State> each, CancellationToken ct)
    {
        var family = FamilyOf(card) ?? throw new InvalidOperationException("Exo doesn't read this controller yet.");
        var usb = card.Connection == "usb";
        if (family == Family.Xbox) return Task.Run(() => PollXInput(duration, each, ct), ct);
        if (family == Family.Hid)
        {
            var axes = HidAxes.Open(card) ?? throw new InvalidOperationException("Exo couldn't open the controller.");
            return ReadAsync(card, duration, (at, report, n) => { if (axes.Parse(report, n) is { } s) each(at, s); }, ct)
                .ContinueWith(t => { axes.Dispose(); t.GetAwaiter().GetResult(); }, TaskScheduler.Default);
        }
        var edge = card.ProductId == DualSenseEdge;
        return ReadAsync(card, duration, (at, report, n) =>
        {
            var state = family == Family.DualSense ? Parse(report.AsSpan(0, n), usb, edge) : ParseDualShock4(report.AsSpan(0, n), usb);
            if (state is { } s) each(at, s);
        }, ct);
    }

    // ------------------------------------------------------------------ XInput

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint Packet;
        public ushort Buttons;
        public byte LeftTrigger, RightTrigger;
        public short LX, LY, RX, RY;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern int XInputGetState(int index, out XInputState state);

    /// <summary>The first connected XInput slot's state each millisecond; a state counts when its packet number moves (XInput's own "something changed").</summary>
    private static void PollXInput(TimeSpan duration, Action<long, State> each, CancellationToken ct)
    {
        var slot = Enumerable.Range(0, 4).FirstOrDefault(i => XInputGetState(i, out _) == 0, -1);
        if (slot < 0) throw new InvalidOperationException("Windows doesn't see the Xbox controller.");
        var until = Stopwatch.GetTimestamp() + (long)(duration.TotalSeconds * Stopwatch.Frequency);
        var tick = Stopwatch.Frequency / 1000;
        uint last = uint.MaxValue;
        var next = Stopwatch.GetTimestamp();
        while (!ct.IsCancellationRequested && Stopwatch.GetTimestamp() < until)
        {
            if (XInputGetState(slot, out var s) != 0) throw new IOException("The Xbox controller went away.");
            var now = Stopwatch.GetTimestamp();
            if (s.Packet != last)
            {
                last = s.Packet;
                each(now, FromXInput(s.LX, s.LY, s.RX, s.RY, s.LeftTrigger, s.RightTrigger, s.Packet));
            }
            next += tick;
            // Sleep most of the millisecond, then wait out the rest.
            while (Stopwatch.GetTimestamp() < next)
            {
                if (next - Stopwatch.GetTimestamp() > tick / 2) Thread.Sleep(0);
                else Thread.SpinWait(50);
            }
        }
        ct.ThrowIfCancellationRequested();
    }

    // ------------------------------------------------------------------ any HID gamepad

    /// <summary>A HID gamepad's axes as Windows describes them: X and Y the left stick, Z and Rz (or Rx and Ry) the right, the other pair the triggers.</summary>
    private sealed class HidAxes : IDisposable
    {
        private readonly nint _preparsed;
        private readonly Dictionary<ushort, (ushort Collection, int Min, int Max, int Bits)> _axes;
        private readonly ushort _rightX, _rightY;
        private readonly ushort? _leftTrigger, _rightTrigger;

        private HidAxes(nint preparsed, Dictionary<ushort, (ushort, int, int, int)> axes)
        {
            _preparsed = preparsed;
            _axes = axes;
            // Sony's layout (and most HID pads'): Z and Rz the right stick, Rx and Ry the triggers. Without Rz, Rx and Ry.
            var zrz = axes.ContainsKey(0x32) && axes.ContainsKey(0x35);
            (_rightX, _rightY) = zrz ? ((ushort)0x32, (ushort)0x35) : ((ushort)0x33, (ushort)0x34);
            if (zrz && axes.ContainsKey(0x33) && axes.ContainsKey(0x34)) (_leftTrigger, _rightTrigger) = (0x33, 0x34);
        }

        internal static HidAxes? Open(Peripherals.Peripheral card)
        {
            if (Gamepad(card) is not { } pad || Peripherals.InterfacePath(pad.Id) is not { } path) return null;
            using var handle = CreateFile(path, 0, FileShareRead | FileShareWrite, 0, OpenExisting, 0, 0);
            if (handle.IsInvalid || !HidD_GetPreparsedData(handle, out var preparsed)) return null;
            var axes = new Dictionary<ushort, (ushort, int, int, int)>();
            if (HidP_GetCaps(preparsed, out var caps) == HidpStatusSuccess && caps.NumberInputValueCaps > 0)
            {
                var length = caps.NumberInputValueCaps;
                var buffer = new byte[length * CapsSize];
                if (HidP_GetValueCaps(ReportType.Input, buffer, ref length, preparsed) == HidpStatusSuccess)
                    for (var i = 0; i < length; i++)
                    {
                        var c = buffer.AsSpan(i * CapsSize, CapsSize);
                        var page = BitConverter.ToUInt16(c[0..2]);
                        var usage = BitConverter.ToUInt16(c[56..58]);
                        if (page == 0x01 && usage is >= 0x30 and <= 0x35 && !axes.ContainsKey(usage))
                            axes[usage] = (BitConverter.ToUInt16(c[6..8]), BitConverter.ToInt32(c[40..44]), BitConverter.ToInt32(c[44..48]), BitConverter.ToUInt16(c[18..20]));
                    }
            }
            if (!axes.ContainsKey(0x30) || !axes.ContainsKey(0x31)) { HidD_FreePreparsedData(preparsed); return null; }
            return new HidAxes(preparsed, axes);
        }

        internal State? Parse(byte[] report, int length)
        {
            byte? Read(ushort? usage)
            {
                if (usage is not { } u || !_axes.TryGetValue(u, out var axis)) return null;
                return HidP_GetUsageValue(ReportType.Input, 0x01, axis.Collection, u, out var raw, _preparsed, report, (uint)length) == HidpStatusSuccess
                    ? Scale(raw, axis.Min, axis.Max, axis.Bits) : null;
            }
            if (Read(0x30) is not { } lx || Read(0x31) is not { } ly) return null;
            return new State(lx, ly, Read(_rightX) ?? 128, Read(_rightY) ?? 128, Read(_leftTrigger) ?? 0, Read(_rightTrigger) ?? 0, null, null, null, null, null, false);
        }

        public void Dispose() => HidD_FreePreparsedData(_preparsed);
    }

    [DllImport("hid.dll")]
    private static extern int HidP_GetUsageValue(ReportType type, ushort page, ushort collection, ushort usage, out uint value, nint preparsed, byte[] report, uint length);
}
