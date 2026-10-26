using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// The test's reads of Windows, none needing administrator rights: the mouse's reports through Raw Input (a
/// message-only window on its own thread, registered with RIDEV_INPUTSINK so it reads while another window has focus;
/// it only reads, and puts back any registration this process had), each CPU's Interrupts/sec and % DPC Time (PDH,
/// English counter names so any Windows language works), the CPUs (GetLogicalProcessorInformationEx) and the main
/// screen's refresh (EnumDisplaySettings). The change goes through Interrupts' one elevated reg.exe run.
/// </summary>
internal sealed partial class MouseCore
{
    internal sealed class ElevatedWriter : IWriter
    {
        public string? Write(string id, int? policy, byte[]? mask) =>
            InterruptMode.RunElevated([new(id, PolicyName, policy), new(id, OverrideName, mask)]);
    }

    internal static class Sampler
    {
        /// <summary>Reads for <paramref name="length"/>; null when Raw Input couldn't be read at all.</summary>
        internal static Sample? Run(TimeSpan length, CancellationToken ct)
        {
            var topology = ReadTopology();
            var refresh = Refresh();
            var reader = RawMouse.Start(length + TimeSpan.FromSeconds(5));
            if (reader is null) return null;
            List<Window> windows;
            try { windows = Counters(length, ct); }
            finally { reader.Stop(); }
            // The mouse that moved: the device that sent the most reports.
            var (device, reports) = reader.Reports.MaxBy(r => r.Value.Count) is { Value.Count: >= 64 } busiest ? (busiest.Key, busiest.Value) : (0, []);
            var mouse = device != 0 && DeviceName(device) is { } path ? InterruptMode.NativeDevices.InstanceOf(path) : null;
            AppLog.Info($"Mouse core: test read {reports.Count} reports, {windows.Count} counter samples, {topology.Cores.Count} cores.");
            return new Sample(reports, mouse, Stopwatch.Frequency, refresh, windows, topology);
        }

        /// <summary>Each CPU's interrupts and DPC time every quarter second for the test's length.</summary>
        private static List<Window> Counters(TimeSpan length, CancellationToken ct)
        {
            var windows = new List<Window>();
            var start = Stopwatch.GetTimestamp();
            var end = start + (long)(length.TotalSeconds * Stopwatch.Frequency);
            var opened = PdhOpenQuery(null, 0, out var query) == 0;
            try
            {
                nint interrupts = 0, dpc = 0;
                var ready = opened
                    && PdhAddEnglishCounter(query, @"\Processor(*)\Interrupts/sec", 0, out interrupts) == 0
                    && PdhAddEnglishCounter(query, @"\Processor(*)\% DPC Time", 0, out dpc) == 0
                    && PdhCollectQueryData(query) == 0;
                if (!ready) AppLog.Info("Mouse core: Windows' processor counters couldn't be read.");
                var from = start;
                for (var next = start + Stopwatch.Frequency / 4; !ct.IsCancellationRequested; next += Stopwatch.Frequency / 4)
                {
                    var wait = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), Math.Min(next, end));
                    if (wait > TimeSpan.Zero && ct.WaitHandle.WaitOne(wait)) break;
                    if (ready && PdhCollectQueryData(query) == 0)
                    {
                        var to = Stopwatch.GetTimestamp();
                        if (Values(interrupts) is { } i && Values(dpc) is { } d) windows.Add(new Window(from, to, i, d));
                        from = to;
                    }
                    if (next >= end) break;
                }
            }
            finally
            {
                if (opened) PdhCloseQuery(query);
            }
            return windows;
        }

        /// <summary>A counter's value per CPU number (Processor(0), Processor(1)…; _Total left out).</summary>
        private static double[]? Values(nint counter)
        {
            const uint format = 0x200 | 0x8000; // PDH_FMT_DOUBLE | PDH_FMT_NOCAP100
            uint size = 0;
            if (PdhGetFormattedCounterArray(counter, format, ref size, out _, 0) != 0x800007D2 /* PDH_MORE_DATA */ || size == 0) return null;
            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (PdhGetFormattedCounterArray(counter, format, ref size, out var count, buffer) != 0) return null;
                var values = new SortedDictionary<int, double>();
                for (var i = 0; i < count; i++)
                {
                    // PDH_FMT_COUNTERVALUE_ITEM_W: the name, then CStatus and the double 8 bytes on.
                    var item = buffer + i * 24;
                    if (!int.TryParse(Marshal.PtrToStringUni(Marshal.ReadIntPtr(item)), NumberStyles.None, CultureInfo.InvariantCulture, out var cpu) || cpu >= 1024) continue;
                    var status = (uint)Marshal.ReadInt32(item, 8);
                    values[cpu] = status <= 1 ? BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, 16)) : 0;
                }
                if (values.Count == 0) return null;
                var all = new double[values.Keys.Max() + 1];
                foreach (var (cpu, value) in values) all[cpu] = value;
                return all;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static Topology ReadTopology()
        {
            var logical = (int)GetActiveProcessorCount(0xFFFF); // ALL_PROCESSOR_GROUPS
            uint length = 0;
            GetLogicalProcessorInformationEx(0xFFFF, null, ref length);
            if (length == 0) return new Topology([], [], logical);
            var buffer = new byte[length];
            return GetLogicalProcessorInformationEx(0xFFFF, buffer, ref length) ? Parse(buffer.AsSpan(0, (int)length), logical) : new Topology([], [], logical);
        }

        /// <summary>The main screen's refresh in Hz (DEVMODEW.dmDisplayFrequency); 0 when Windows doesn't say.</summary>
        private static double Refresh()
        {
            var mode = new byte[220];
            BinaryPrimitives.WriteUInt16LittleEndian(mode.AsSpan(68), 220); // dmSize
            return EnumDisplaySettings(null, -1 /* ENUM_CURRENT_SETTINGS */, mode) ? BitConverter.ToUInt32(mode, 184) : 0;
        }

        private static string? DeviceName(nint device)
        {
            const uint name = 0x20000007; // RIDI_DEVICENAME
            uint size = 0;
            GetRawInputDeviceInfo(device, name, null, ref size);
            if (size is 0 or > 1024) return null;
            var buffer = new char[size];
            if (GetRawInputDeviceInfo(device, name, buffer, ref size) is 0 or uint.MaxValue) return null;
            var path = new string(buffer).TrimEnd('\0');
            return path.StartsWith(@"\??\", StringComparison.Ordinal) ? @"\\?\" + path[4..] : path;
        }
    }

    /// <summary>
    /// Raw Input's mouse reports, each stamped with the Stopwatch the moment its WM_INPUT is taken off the queue, on a
    /// thread of its own at high priority so the reading itself adds as little delay as it can. Relative reports from a
    /// real device only: an absolute one (a tablet, a remote session) or one with no device (another program's) is skipped.
    /// </summary>
    private sealed class RawMouse
    {
        private const uint WmInput = 0x00FF, WmTimer = 0x0113, WmQuit = 0x0012;
        private const uint InputSink = 0x100, Remove = 0x1;
        private readonly ManualResetEventSlim _ready = new();
        private readonly TimeSpan _backstop;
        private Thread? _thread;
        private uint _threadId;
        private bool _reading;

        internal Dictionary<nint, List<Report>> Reports { get; } = [];

        private RawMouse(TimeSpan backstop) => _backstop = backstop;

        internal static RawMouse? Start(TimeSpan backstop)
        {
            var reader = new RawMouse(backstop);
            reader._thread = new Thread(reader.Loop) { IsBackground = true, Name = "Exo mouse test", Priority = ThreadPriority.Highest };
            reader._thread.Start();
            reader._ready.Wait(TimeSpan.FromSeconds(5));
            if (reader._reading) return reader;
            reader.Stop();
            return null;
        }

        internal void Stop()
        {
            if (_threadId != 0) PostThreadMessage(_threadId, WmQuit, 0, 0);
            _thread?.Join(TimeSpan.FromSeconds(3));
        }

        private void Loop()
        {
            _threadId = GetCurrentThreadId();
            var window = CreateWindowEx(0, "STATIC", null, 0, 0, 0, 0, 0, -3 /* HWND_MESSAGE */, 0, 0, 0);
            if (window == 0)
            {
                AppLog.Warn("Mouse core: the reader's window couldn't be made.");
                _ready.Set();
                return;
            }
            var size = (uint)Marshal.SizeOf<RawInputDevice>();
            var previous = Registered(size);
            try
            {
                if (!RegisterRawInputDevices([new RawInputDevice { UsagePage = 1, Usage = 2, Flags = InputSink, Target = window }], 1, size))
                {
                    AppLog.Warn($"Mouse core: Raw Input refused the reader ({Marshal.GetLastWin32Error()}).");
                    _ready.Set();
                    return;
                }
                _reading = true;
                SetTimer(window, 1, (uint)_backstop.TotalMilliseconds, 0);
                _ready.Set();
                var buffer = new byte[64];
                while (GetMessage(out var msg, 0, 0, 0) > 0)
                {
                    var at = Stopwatch.GetTimestamp();
                    if (msg.Message == WmInput) Read(msg.LParam, at, buffer);
                    else if (msg.Message == WmTimer && msg.Window == window) break;
                    // DefWindowProc frees the input (DispatchMessage reaches it through the window's own procedure).
                    DispatchMessage(ref msg);
                }
            }
            catch (Exception ex)
            {
                // The thread's own boundary: nothing it meets may take Exo down with it.
                AppLog.Warn("Mouse core: the reader stopped: " + ex.Message);
                _ready.Set();
            }
            finally
            {
                if (_reading)
                {
                    // Put back what this process had for mice, or take Exo's off.
                    RegisterRawInputDevices([previous ?? new RawInputDevice { UsagePage = 1, Usage = 2, Flags = Remove, Target = 0 }], 1, size);
                    KillTimer(window, 1);
                }
                DestroyWindow(window);
            }
        }

        private void Read(nint input, long at, byte[] buffer)
        {
            var size = (uint)buffer.Length;
            // RAWINPUTHEADER (type, size, device, wParam: 24 bytes on x64), then RAWMOUSE: usFlags, buttons, raw buttons, lLastX, lLastY.
            if (GetRawInputData(input, 0x10000003 /* RID_INPUT */, buffer, ref size, 24) is < 48 or uint.MaxValue) return;
            if (BitConverter.ToUInt32(buffer, 0) != 0 /* RIM_TYPEMOUSE */) return;
            var device = (nint)BitConverter.ToInt64(buffer, 8);
            if (device == 0 || (BitConverter.ToUInt16(buffer, 24) & 1 /* MOUSE_MOVE_ABSOLUTE */) != 0) return;
            if (!Reports.TryGetValue(device, out var list)) Reports[device] = list = new List<Report>(16_384);
            list.Add(new Report(at, BitConverter.ToInt32(buffer, 36), BitConverter.ToInt32(buffer, 40)));
        }

        /// <summary>This process's mouse registration before Exo's, if it had one.</summary>
        private static RawInputDevice? Registered(uint size)
        {
            uint count = 0;
            GetRegisteredRawInputDevices(null, ref count, size);
            if (count == 0) return null;
            var all = new RawInputDevice[count];
            return GetRegisteredRawInputDevices(all, ref count, size) == uint.MaxValue ? null
                : all.Take((int)count).Where(d => d is { UsagePage: 1, Usage: 2 }).Select(d => (RawInputDevice?)d).FirstOrDefault();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public nint Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint Window;
        public uint Message;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices([In] RawInputDevice[] devices, uint count, uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRegisteredRawInputDevices([Out] RawInputDevice[]? devices, ref uint count, uint size);

    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(nint input, uint command, [Out] byte[] data, ref uint size, uint headerSize);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetRawInputDeviceInfoW")]
    private static extern uint GetRawInputDeviceInfo(nint device, uint command, [Out] char[]? data, ref uint size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateWindowExW")]
    private static extern nint CreateWindowEx(uint exStyle, string className, string? windowName, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll", EntryPoint = "GetMessageW")]
    private static extern int GetMessage(out Msg msg, nint window, uint min, uint max);

    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static extern nint DispatchMessage(ref Msg msg);

    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint thread, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern nuint SetTimer(nint window, nuint id, uint elapse, nint callback);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool KillTimer(nint window, nuint id);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplaySettingsW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettings(string? device, int mode, [In, Out] byte[] devMode);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll")]
    private static extern uint GetActiveProcessorCount(ushort group);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLogicalProcessorInformationEx(int relationship, [Out] byte[]? buffer, ref uint length);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhOpenQueryW")]
    private static extern uint PdhOpenQuery(string? source, nint user, out nint query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhAddEnglishCounterW")]
    private static extern uint PdhAddEnglishCounter(nint query, string path, nint user, out nint counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(nint query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhGetFormattedCounterArrayW")]
    private static extern uint PdhGetFormattedCounterArray(nint counter, uint format, ref uint size, out uint count, nint buffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(nint query);
}
