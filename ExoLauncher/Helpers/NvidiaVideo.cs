using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ExoLauncher.Helpers;

/// <summary>
/// RTX Video: NVIDIA Control Panel's Video > Adjust video image settings > RTX video enhancement
/// (Super resolution with its quality, and High Dynamic Range). Private NvAPI, decoded from the panel
/// (docs/architecture/nvidia-control-panel.md, section 5):
/// - NvAPI_GetVideoStateEx 0x0B6EF8B9 (NvDisplayHandle, GET struct 0x80 bytes, version 0x10080):
///   componentId@4, flags@0xC (bit0 supported), lastValueSet@0x58.
/// - NvAPI_SetVideoStateEx 0x9321CA5B (NvDisplayHandle, SET struct 0x40 bytes, version 0x10040):
///   componentId@4, bIsTopPriority@0xC, enable@0x10, setToValue@0x14.
/// - Component 29 Super resolution: 0 off, 1-4 quality, 5 Auto. Component 30 RTX Video HDR: 0 off,
///   5 on. Both are global (the device index is ignored).
/// Values decoded on an RTX 3070 (617.42) by setting each choice in NVIDIA Control Panel and reading it
/// back, and the panel's own code read in nvxdapix.dll (621.01), which Exo now follows:
/// - On or off is the GET struct's enable@0x10, not the value alone: off is enable 0, whatever lastValueSet says.
/// - A write sets enable 1 and bIsTopPriority 1 (the panel's "NVIDIA overrides the application") for a value,
///   enable 0 for off, setToAlgo@0x18 0.
/// - Before it writes, the panel makes sure the active video device is set: NvAPI 0x6579F1C8 reads a 0x20-byte
///   struct (version 0x10020); if its word@4 isn't 1, 0xF8181529 writes it back with @4 = 1 and @8 = -1.
/// - While the driver answers -108 (busy) the panel tries again (for up to 50 s; Exo for 5).
/// The NVIDIA app (11.0.9) goes through the same calls, after the same active video device step: Super resolution 0-5, HDR
/// 1 for on (Exo writes the panel's 5; any value but 0 reads as on), and only when the user changes it there: it never puts
/// its own back. The driver keeps the choice under the display adapter's class key (_User_Global_VAL/XEN_SuperResolution).
/// Exo reads a write back for a second, and a value that took and then went back is said as that.
/// </summary>
internal static unsafe class NvidiaVideo
{
    internal const int SuperResolution = 29, VideoHdr = 30;
    private static readonly HashSet<int> Unknown = [];

    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint QueryInterface(uint id);

    private static Lock Gate => NvidiaProfiles.Gate;

    /// <summary>A component's setting (0 when it is off), or null when the card or driver has no such component.</summary>
    internal static int? Read(string gdiName, int component)
    {
        if (!NvidiaProfiles.Available) return null;
        lock (Gate)
        {
            try
            {
                if (Handle(gdiName) is not { } handle) return null;
                var data = stackalloc byte[0x80];
                new Span<byte>(data, 0x80).Clear();
                *(uint*)data = 0x10080;
                *(uint*)(data + 4) = (uint)component;
                var get = (delegate* unmanaged[Cdecl]<nint, byte*, int>)Resolve(0x0B6EF8B9);
                if (get(handle, data) != 0 || (*(uint*)(data + 0xC) & 1) == 0) return null;
                // Off is enable@0x10 at 0, as the panel reads it: the last value set stays behind when it is turned off.
                if (*(int*)(data + 0x10) == 0) return 0;
                var value = *(int*)(data + 0x58);
                // A value Exo has no name for (seen on another PC: the page then says Set elsewhere and no change sticks)
                // goes to the log once, with the whole struct and what the driver keeps, so it can be decoded.
                if (component == SuperResolution && value is < 0 or > 5 && Unknown.Add(value))
                    AppLog.Info($"Display: RTX Video Super Resolution reads {value}, Exo knows 0-5. GET words: {Words(data, 0x80)}; kept: {Kept()}.");
                return value;
            }
            catch (Exception ex) { AppLog.Debug("NvAPI video state read: " + ex.Message); return null; }
        }
    }

    /// <summary>
    /// Sets a component the way NVIDIA Control Panel does (the active video device first, busy answers tried again) and
    /// reads it back for a second; null when it took and stayed, else a plain line.
    /// </summary>
    internal static string? Write(string gdiName, int component, int value)
    {
        lock (Gate)
        {
            try
            {
                if (Handle(gdiName) is not { } handle) return "The NVIDIA driver has no RTX Video for this screen.";
                EnsureActiveVideoDevice(handle);
                var data = stackalloc byte[0x40];
                var set = (delegate* unmanaged[Cdecl]<nint, byte*, int>)Resolve(0x9321CA5B);
                int status, result;
                var until = Environment.TickCount64 + 5000;
                while (true)
                {
                    new Span<byte>(data, 0x40).Clear();
                    *(uint*)data = 0x10040;
                    *(uint*)(data + 4) = (uint)component;
                    *(uint*)(data + 0xC) = value != 0 ? 1u : 0u;   // bIsTopPriority: NVIDIA's setting over the player's
                    *(uint*)(data + 0x10) = value != 0 ? 1u : 0u;  // enable: off is enable 0
                    *(int*)(data + 0x14) = value;
                    status = set(handle, data);
                    result = *(int*)(data + 0x20);
                    if (status != Busy || Environment.TickCount64 > until) break;
                    Thread.Sleep(100);
                }
                AppLog.Info($"Display: RTX Video component {component} = {value}: status {status}, result {result}.");
                if (status != 0 || result != 0) return "The NVIDIA driver did not take that.";
            }
            catch (Exception ex)
            {
                AppLog.Warn("NvAPI video state write failed: " + ex.Message);
                return "The NVIDIA driver did not answer.";
            }
        }
        // The driver can take a moment to apply, and another program can put its own value back: read for a second.
        var took = false;
        int? now = null;
        for (var i = 0; i < 10; i++)
        {
            now = Read(gdiName, component);
            took |= now == value;
            if (took && now != value) break;
            Thread.Sleep(100);
        }
        if (now == value) return null;
        AppLog.Info($"Display: RTX Video component {component} set to {value} reads back {now?.ToString() ?? "nothing"}{(took ? " after it took" : "")}; kept: {Kept()}.");
        return took
            ? "Another program changed RTX Video right after Exo set it."
            : "The NVIDIA driver didn't keep that. If NVIDIA Control Panel can't set it either, update the driver.";
    }

    private static string Words(byte* data, int bytes) => string.Join(" ", Enumerable.Range(0, bytes / 4).Select(i => ((int*)data)[i]));

    // What the driver keeps for Super resolution under each display adapter's class key, for the log.
    private static string Kept()
    {
        try
        {
            using var display = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            var kept = new List<string>();
            foreach (var name in display?.GetSubKeyNames() ?? [])
            {
                if (!name.All(char.IsAsciiDigit)) continue;
                using var adapter = display!.OpenSubKey(name);
                if (adapter?.GetValue("_User_Global_VAL_SuperResolution") is not { } value) continue;
                kept.Add($"{name} VAL {Shown(value)} XEN {Shown(adapter.GetValue("_User_Global_XEN_SuperResolution"))}");
            }
            return kept.Count == 0 ? "nothing" : string.Join(", ", kept);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return "unreadable"; }

        static string Shown(object? value) => value switch { byte[] bytes => Convert.ToHexString(bytes), null => "none", _ => value.ToString() ?? "" };
    }

    /// <summary>NvAPI's answer while the driver is busy: the panel tries again.</summary>
    private const int Busy = -108;

    /// <summary>
    /// The panel's step before every RTX Video write: the active video device read (0x6579F1C8, 0x20 bytes, version
    /// 0x10020) and, when its word@4 isn't 1, written back with @4 = 1 and @8 = -1 (0xF8181529). Any failure is only
    /// logged: the write is tried as before.
    /// </summary>
    private static void EnsureActiveVideoDevice(nint handle)
    {
        try
        {
            var data = stackalloc byte[0x20];
            new Span<byte>(data, 0x20).Clear();
            *(uint*)data = 0x10020;
            var get = (delegate* unmanaged[Cdecl]<nint, byte*, int>)Resolve(0x6579F1C8);
            var status = get(handle, data);
            if (status != 0 || *(int*)(data + 4) == 1) return;
            *(int*)(data + 4) = 1;
            *(int*)(data + 8) = -1;
            var set = (delegate* unmanaged[Cdecl]<nint, byte*, int>)Resolve(0xF8181529);
            AppLog.Info($"Display: RTX Video active video device set: status {set(handle, data)}.");
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException) { AppLog.Debug("NvAPI active video device: " + ex.Message); }
    }

    private static nint? Handle(string gdiName)
    {
        var byName = (delegate* unmanaged[Cdecl]<byte*, nint*, int>)Resolve(0x35C29134);
        var name = System.Text.Encoding.ASCII.GetBytes(gdiName + "\0");
        nint handle = 0;
        fixed (byte* text = name)
            return byName(text, &handle) == 0 && handle != 0 ? handle : null;
    }

    private static nint Resolve(uint id)
    {
        var fn = QueryInterface(id);
        return fn != 0 ? fn : throw new EntryPointNotFoundException($"NvAPI 0x{id:X8} is not in this driver.");
    }
}
