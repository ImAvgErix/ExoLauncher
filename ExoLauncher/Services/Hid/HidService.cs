using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using ExoLauncher.Helpers;
using Microsoft.Win32.SafeHandles;
using static ExoLauncher.Services.Hid.HidInterop;

namespace ExoLauncher.Services.Hid;

/// <summary>
/// HID devices for the Devices room, the way a browser's WebHID sees them, so
/// the mouse drivers (written against WebHID) run unchanged in the page.
///
/// Windows lists one interface per top-level collection; a browser shows one
/// device per USB interface with all its collections. Collections are grouped
/// by their parent device node (the USB interface), which keeps two identical
/// mice apart. Every handle is opened shared: an exclusive open would freeze the
/// mouse's own input. Collections Windows keeps for itself (the pointer and
/// keyboard ones) cannot be opened for reading and writing. A pointer collection
/// is opened with no access instead, for its feature reports only (Razer keeps
/// its settings there, as hidapi and OpenMouse Bridge reach them); a keyboard
/// collection is skipped.
///
/// A device keeps its key while it stays: its product and serial strings are
/// read once per interface (each read is a control transfer, which a receiver
/// busy with its mouse can fail, and a failed serial read renamed the device,
/// so the page saw it leave and a new one arrive), and listings that run side
/// by side are applied in the order they started, so an older one never closes
/// a device a newer one found.
///
/// Byte conventions match Chromium on Windows: an input report arrives without
/// its ID byte; a feature report read keeps its ID byte, except an unnumbered
/// report (ID 0), which loses the leading zero.
/// </summary>
internal sealed class HidService : IDisposable
{
    /// <summary>One top-level collection: its usage, the report IDs it carries per type, and Windows' fixed report lengths (ID byte included).</summary>
    internal sealed record Collection(
        int UsagePage, int Usage,
        IReadOnlyList<int> InputIds, IReadOnlyList<int> OutputIds, IReadOnlyList<int> FeatureIds,
        int InputLength, int OutputLength, int FeatureLength);

    internal sealed record Device(string Key, int VendorId, int ProductId, string ProductName, IReadOnlyList<Collection> Collections);

    private sealed record Found(string Path, string Parent, int VendorId, int ProductId, string ProductName, string Serial, Collection Caps);

    private sealed class Interface(string path, Collection caps, SafeFileHandle control, FileStream? writer, FileStream? reader) : IDisposable
    {
        public string Path { get; } = path;
        public Collection Caps { get; } = caps;
        public SafeFileHandle Control { get; } = control;
        public FileStream? Writer { get; } = writer;
        /// <summary>Opened with the device, so Windows buffers a reply that comes back before anyone listens.</summary>
        public FileStream? Reader { get; } = reader;
        public SemaphoreSlim WriteLock { get; } = new(1, 1);
        public CancellationTokenSource Reading { get; } = new();

        public void Dispose()
        {
            Reading.Cancel();
            Reader?.Dispose();
            Writer?.Dispose();
            Control.Dispose();
            WriteLock.Dispose();
            Reading.Dispose();
        }
    }

    private sealed class Opened(Device device, List<Interface> interfaces)
    {
        public Device Device { get; } = device;
        public List<Interface> Interfaces { get; } = interfaces;
    }

    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(3);

    private readonly object _gate = new();
    private readonly Dictionary<string, List<Found>> _listed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Opened> _open = new(StringComparer.Ordinal);
    /// <summary>Product and serial per interface path while it is present; null where a read has not succeeded yet.</summary>
    private readonly ConcurrentDictionary<string, (string? Product, string? Serial)> _strings = new(StringComparer.OrdinalIgnoreCase);
    private long _listingsStarted;
    private long _listingApplied;
    private CmNotifyCallback? _callback;
    private nint _notify;
    private Timer? _debounce;

    /// <summary>An input report from an open device: key, report ID, data without the ID byte.</summary>
    internal event Action<string, int, byte[]>? Input;

    /// <summary>A HID device arrived or left; the page lists again.</summary>
    internal event Action? Changed;

    internal void Start()
    {
        lock (_gate)
        {
            if (_notify != 0) return;
            HidD_GetHidGuid(out var guid);
            var filter = new CmNotifyFilter { CbSize = 416, FilterType = CmNotifyFilterTypeDeviceInterface, ClassGuid = guid };
            _callback = (_, _, _, _, _) => { _debounce?.Change(250, Timeout.Infinite); return 0; };
            _debounce = new Timer(_ => { try { Changed?.Invoke(); } catch (Exception ex) { AppLog.Debug("Hid: change handler failed: " + ex.Message); } });
            var result = CM_Register_Notification(ref filter, 0, _callback, out _notify);
            if (result != 0) AppLog.Info($"Hid: device notifications unavailable (CR {result}).");
        }
    }

    // ------------------------------------------------------------------ list

    /// <summary>Present devices from these vendors (every vendor when none are given).</summary>
    internal IReadOnlyList<Device> List(IReadOnlyCollection<int>? vendorIds)
    {
        bool Wanted(int vendorId) => vendorIds is not { Count: > 0 } || vendorIds.Contains(vendorId);
        var listing = Interlocked.Increment(ref _listingsStarted);
        var found = Enumerate(Wanted);
        var groups = found.GroupBy(f => KeyOf(f), StringComparer.Ordinal).ToList();
        List<Opened> gone = [];
        lock (_gate)
        {
            if (IsNewest(listing, ref _listingApplied))
            {
                foreach (var g in groups) _listed[g.Key] = g.ToList();
                // A device that left keeps its key when it comes back on the same port, and the page never
                // closes one that left: its dead handles would answer every call after a replug with an error.
                gone = [.. _open.Values.Where(o => Wanted(o.Device.VendorId) && groups.All(g => g.Key != o.Device.Key))];
                foreach (var o in gone) _listed.Remove(o.Device.Key);
                var present = found.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var path in _strings.Keys.Where(p => !present.Contains(p))) _strings.TryRemove(path, out _);
            }
        }
        foreach (var o in gone)
        {
            AppLog.Info($"Hid: {o.Device.VendorId:x4}:{o.Device.ProductId:x4} left; closed.");
            Close(o.Device.Key);
        }
        return groups
            .Select(g => new Device(g.Key, g.First().VendorId, g.First().ProductId,
                g.Select(f => f.ProductName).FirstOrDefault(n => n.Length > 0) ?? "",
                g.Select(f => f.Caps).ToList()))
            .ToList();
    }

    /// <summary>A stable key that names no path: the parent node, the ids and the serial, hashed.</summary>
    private static string KeyOf(Found f)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{f.Parent}|{f.VendorId:x4}|{f.ProductId:x4}|{f.Serial}"));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    /// <summary>Whether a listing started after every listing applied so far; it becomes the newest applied.</summary>
    internal static bool IsNewest(long listing, ref long applied)
    {
        if (listing <= applied) return false;
        applied = listing;
        return true;
    }

    private List<Found> Enumerate(Func<int, bool> wanted)
    {
        var list = new List<Found>();
        HidD_GetHidGuid(out var guid);
        var set = SetupDiGetClassDevs(ref guid, 0, 0, DigcfPresent | DigcfDeviceInterface);
        if (set == -1) return list;
        try
        {
            for (var i = 0; ; i++)
            {
                var data = new SpDeviceInterfaceData { CbSize = System.Runtime.InteropServices.Marshal.SizeOf<SpDeviceInterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(set, 0, ref guid, i, ref data)) break;
                if (Describe(set, ref data, wanted) is { } f) list.Add(f);
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
        return list;
    }

    private Found? Describe(nint set, ref SpDeviceInterfaceData data, Func<int, bool> wanted)
    {
        var info = new SpDevinfoData { CbSize = System.Runtime.InteropServices.Marshal.SizeOf<SpDevinfoData>() };
        SetupDiGetDeviceInterfaceDetail(set, ref data, 0, 0, out var size, ref info);
        if (size <= 0) return null;
        var detail = System.Runtime.InteropServices.Marshal.AllocHGlobal(size);
        string path;
        try
        {
            System.Runtime.InteropServices.Marshal.WriteInt32(detail, 8); // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W on x64
            if (!SetupDiGetDeviceInterfaceDetail(set, ref data, detail, size, out _, ref info)) return null;
            path = System.Runtime.InteropServices.Marshal.PtrToStringUni(detail + 4) ?? "";
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(detail);
        }
        if (path.Length == 0) return null;

        // No access asked: enough to read attributes and caps, even on collections Windows keeps for itself.
        using var handle = CreateFile(path, 0, FileShareRead | FileShareWrite, 0, OpenExisting, 0, 0);
        if (handle.IsInvalid) return null;
        var attributes = new HiddAttributes { Size = System.Runtime.InteropServices.Marshal.SizeOf<HiddAttributes>() };
        // Other vendors' devices stop here, before their strings: each is a control transfer, and a
        // sleeping Bluetooth device can stall one.
        if (!HidD_GetAttributes(handle, ref attributes) || !wanted(attributes.VendorId)) return null;
        if (CapsOf(handle) is not { } caps) return null;
        var (product, serial) = Remember(_strings, path, () => HidString(handle, HidD_GetProductString), () => HidString(handle, HidD_GetSerialNumberString));
        return new Found(path, ParentOf(info.DevInst), attributes.VendorId, attributes.ProductId, product, serial, caps);
    }

    /// <summary>An interface's product and serial: what an earlier listing read, a string not read yet read now.</summary>
    internal static (string Product, string Serial) Remember(ConcurrentDictionary<string, (string? Product, string? Serial)> strings,
        string path, Func<string?> product, Func<string?> serial)
    {
        strings.TryGetValue(path, out var known);
        var now = (Product: known.Product ?? product(), Serial: known.Serial ?? serial());
        strings[path] = now;
        return (now.Product ?? "", now.Serial ?? "");
    }

    private static string ParentOf(uint devInst)
    {
        if (CM_Get_Parent(out var parent, devInst, 0) != 0) return devInst.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var buffer = new char[400];
        return CM_Get_Device_ID(parent, buffer, buffer.Length, 0) == 0 ? new string(buffer).TrimEnd('\0') : parent.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>A device string, or null when the device did not answer.</summary>
    private static string? HidString(SafeFileHandle handle, Func<SafeFileHandle, byte[], int, bool> read)
    {
        var buffer = new byte[256];
        return read(handle, buffer, buffer.Length) ? Encoding.Unicode.GetString(buffer).Split('\0')[0].Trim() : null;
    }

    private static Collection? CapsOf(SafeFileHandle handle)
    {
        if (!HidD_GetPreparsedData(handle, out var preparsed)) return null;
        try
        {
            if (HidP_GetCaps(preparsed, out var caps) != HidpStatusSuccess) return null;
            return new Collection(caps.UsagePage, caps.Usage,
                ReportIds(preparsed, ReportType.Input, caps.NumberInputButtonCaps, caps.NumberInputValueCaps, caps.InputReportByteLength),
                ReportIds(preparsed, ReportType.Output, caps.NumberOutputButtonCaps, caps.NumberOutputValueCaps, caps.OutputReportByteLength),
                ReportIds(preparsed, ReportType.Feature, caps.NumberFeatureButtonCaps, caps.NumberFeatureValueCaps, caps.FeatureReportByteLength),
                caps.InputReportByteLength, caps.OutputReportByteLength, caps.FeatureReportByteLength);
        }
        finally
        {
            HidD_FreePreparsedData(preparsed);
        }
    }

    /// <summary>The report IDs of one type, from its button and value caps; [0] for a collection that numbers no reports.</summary>
    private static IReadOnlyList<int> ReportIds(nint preparsed, ReportType type, int buttons, int values, int length)
    {
        if (length == 0) return [];
        var ids = new SortedSet<int>();
        Collect(buttons, (byte[] b, ref ushort n) => HidP_GetButtonCaps(type, b, ref n, preparsed));
        Collect(values, (byte[] b, ref ushort n) => HidP_GetValueCaps(type, b, ref n, preparsed));
        return ids.Count == 0 ? [0] : ids.ToList();

        void Collect(int count, CapsReader read)
        {
            if (count == 0) return;
            var buffer = new byte[count * CapsSize];
            var n = (ushort)count;
            if (read(buffer, ref n) != HidpStatusSuccess) return;
            for (var i = 0; i < n; i++) ids.Add(buffer[i * CapsSize + CapsReportIdOffset]);
        }
    }

    private delegate int CapsReader(byte[] buffer, ref ushort length);

    // ------------------------------------------------------------------ open and close

    internal void Open(string key)
    {
        List<Found> found;
        lock (_gate)
        {
            if (_open.ContainsKey(key)) return;
            if (!_listed.TryGetValue(key, out var listed)) throw new InvalidOperationException("That device is no longer connected.");
            found = listed;
        }
        var interfaces = new List<Interface>();
        foreach (var f in found)
        {
            var control = CreateFile(f.Path, GenericRead | GenericWrite, FileShareRead | FileShareWrite, 0, OpenExisting, 0, 0);
            if (control.IsInvalid)
            {
                // Windows keeps the pointer collection for itself. Razer's settings live on it as
                // feature reports, which a no-access handle can still send and read (hidapi does
                // the same). Its input and output stay Windows' own; keyboards are never opened.
                control.Dispose();
                if (!OpensForFeaturesOnly(f.Caps)) continue;
                var featureOnly = CreateFile(f.Path, 0, FileShareRead | FileShareWrite, 0, OpenExisting, 0, 0);
                if (featureOnly.IsInvalid) { featureOnly.Dispose(); continue; }
                interfaces.Add(new Interface(f.Path, f.Caps with { InputLength = 0, OutputLength = 0 }, featureOnly, null, null));
                continue;
            }
            FileStream? writer = null;
            if (f.Caps.OutputLength > 0)
            {
                var handle = CreateFile(f.Path, GenericWrite, FileShareRead | FileShareWrite, 0, OpenExisting, FileFlagOverlapped, 0);
                if (!handle.IsInvalid) writer = new FileStream(handle, FileAccess.Write, 0, isAsync: true);
                else handle.Dispose();
            }
            FileStream? reader = null;
            if (f.Caps.InputLength > 0)
            {
                var handle = CreateFile(f.Path, GenericRead, FileShareRead | FileShareWrite, 0, OpenExisting, FileFlagOverlapped, 0);
                if (!handle.IsInvalid) reader = new FileStream(handle, FileAccess.Read, 0, isAsync: true);
                else handle.Dispose();
            }
            interfaces.Add(new Interface(f.Path, f.Caps, control, writer, reader));
        }
        if (interfaces.Count == 0) throw new InvalidOperationException("Windows would not open this device. Close other software that controls it, then try again.");
        var first = found[0];
        var device = new Device(key, first.VendorId, first.ProductId, first.ProductName, found.Select(f => f.Caps).ToList());
        lock (_gate)
        {
            if (_open.ContainsKey(key)) { interfaces.ForEach(i => i.Dispose()); return; }
            _open[key] = new Opened(device, interfaces);
        }
        foreach (var i in interfaces.Where(i => i.Reader is not null))
            _ = Task.Run(() => ReadLoopAsync(key, i));
    }

    /// <summary>A Generic Desktop Mouse or Pointer collection that carries feature reports: where a mouse's vendor settings may share its movement collection.</summary>
    internal static bool OpensForFeaturesOnly(Collection caps) =>
        caps.UsagePage == 0x01 && caps.Usage is (0x01 or 0x02) && caps.FeatureLength > 0;

    internal void Close(string key)
    {
        Opened? opened;
        lock (_gate)
        {
            if (!_open.Remove(key, out opened)) return;
        }
        foreach (var i in opened.Interfaces) i.Dispose();
    }

    /// <summary>Every input report from the moment the device opens: a reply never races a listener.</summary>
    private async Task ReadLoopAsync(string key, Interface i)
    {
        var ct = i.Reading.Token;
        var buffer = new byte[i.Caps.InputLength];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var n = await i.Reader!.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (n <= 0) continue;
                try { Input?.Invoke(key, buffer[0], buffer[1..n]); }
                catch (Exception ex) { AppLog.Debug("Hid: input handler failed: " + ex.Message); }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (IOException ex)
        {
            // Unplugged or the receiver lost the mouse: the arrival/removal notice relists.
            AppLog.Info($"Hid: {i.Caps.UsagePage:x4}:{i.Caps.Usage:x4} reading stopped: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ reports

    internal async Task SendReportAsync(string key, int reportId, byte[] data, CancellationToken ct)
    {
        var i = Route(key, reportId, c => c.OutputIds, c => c.OutputLength, "output");
        var buffer = Frame(reportId, data, i.Caps.OutputLength);
        if (i.Writer is null)
        {
            await Blocking(() => HidD_SetOutputReport(i.Control, buffer, buffer.Length), "send a report to", ct).ConfigureAwait(false);
            return;
        }
        await i.WriteLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(CallTimeout);
            await i.Writer.WriteAsync(buffer, limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("The device did not take the report in time.");
        }
        finally
        {
            i.WriteLock.Release();
        }
    }

    internal Task SendFeatureReportAsync(string key, int reportId, byte[] data, CancellationToken ct)
    {
        var i = Route(key, reportId, c => c.FeatureIds, c => c.FeatureLength, "feature");
        var buffer = Frame(reportId, data, i.Caps.FeatureLength);
        return Blocking(() => HidD_SetFeature(i.Control, buffer, buffer.Length), "send a feature report to", ct);
    }

    internal async Task<byte[]> ReceiveFeatureReportAsync(string key, int reportId, CancellationToken ct)
    {
        var i = Route(key, reportId, c => c.FeatureIds, c => c.FeatureLength, "feature");
        var buffer = new byte[i.Caps.FeatureLength];
        buffer[0] = (byte)reportId;
        await Blocking(() => HidD_GetFeature(i.Control, buffer, buffer.Length), "read a feature report from", ct).ConfigureAwait(false);
        return reportId == 0 ? buffer[1..] : buffer;
    }

    internal async Task<byte[]> ReceiveInputReportAsync(string key, int reportId, CancellationToken ct)
    {
        var i = Route(key, reportId, c => c.InputIds, c => c.InputLength, "input");
        var buffer = new byte[i.Caps.InputLength];
        buffer[0] = (byte)reportId;
        await Blocking(() => HidD_GetInputReport(i.Control, buffer, buffer.Length), "read a report from", ct).ConfigureAwait(false);
        return reportId == 0 ? buffer[1..] : buffer;
    }

    /// <summary>The open collection that carries this report: by its ID, or, for ID 0, the first that numbers none.</summary>
    private Interface Route(string key, int reportId, Func<Collection, IReadOnlyList<int>> ids, Func<Collection, int> length, string kind)
    {
        Opened opened;
        lock (_gate)
            if (!_open.TryGetValue(key, out opened!)) throw new InvalidOperationException("Open the device first.");
        return opened.Interfaces.FirstOrDefault(i => length(i.Caps) > 0 && ids(i.Caps).Contains(reportId))
            ?? throw new InvalidOperationException($"This device has no {kind} report {reportId}.");
    }

    /// <summary>The report as Windows wants it: the ID byte, the data, zeros up to the collection's fixed length.</summary>
    private static byte[] Frame(int reportId, byte[] data, int length)
    {
        var buffer = new byte[length];
        buffer[0] = (byte)reportId;
        Array.Copy(data, 0, buffer, 1, Math.Min(data.Length, length - 1));
        return buffer;
    }

    /// <summary>hid.dll's control transfers block; they run off the caller's thread and give up after three seconds.</summary>
    private static async Task Blocking(Func<bool> call, string verb, CancellationToken ct)
    {
        (bool Ok, int Error) result;
        try
        {
            // The error code is per thread: read it on the thread that made the call.
            result = await Task.Run(() => call() ? (true, 0) : (false, System.Runtime.InteropServices.Marshal.GetLastPInvokeError()), CancellationToken.None)
                .WaitAsync(CallTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"The device did not answer in time when Exo tried to {verb} it.");
        }
        if (!result.Ok) throw new IOException($"Windows could not {verb} the device (error {result.Error}).");
    }

    public void Dispose()
    {
        List<Opened> open;
        lock (_gate)
        {
            open = _open.Values.ToList();
            _open.Clear();
            if (_notify != 0) { CM_Unregister_Notification(_notify); _notify = 0; }
            _debounce?.Dispose();
            _debounce = null;
        }
        foreach (var o in open)
            foreach (var i in o.Interfaces) i.Dispose();
    }
}
