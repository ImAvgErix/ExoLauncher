using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using ExoLauncher.Helpers;
using static ExoLauncher.Helpers.CoreAudioInterop;

namespace ExoLauncher.Services;

/// <summary>
/// A controller's own sound in Windows: a DualSense's or DualShock 4's speaker and headset jack, and an Xbox
/// controller's headset, show up as Windows audio devices, and Windows can make one the default the moment the
/// controller connects. Exo turns those devices off (Sound settings' Don't allow), by default and again whenever a
/// controller connects; the controller's page turns them back on. Matched by the controller's vendor and product on
/// the endpoint's own device, never by name: Sony's Pulse 3D headset shares the controllers' vendor.
/// Covered by ControllerSoundTests.
/// </summary>
internal static partial class Peripherals
{
    // The endpoint's audio device: "{1}.USB\VID_054C&PID_0DF2&MI_00\7&26D31004&0&0000".
    private static readonly PropertyKey EndpointDeviceKey = new(new Guid("b3f8fa53-0004-438e-9003-51a46e139bfc"), 2);

    /// <summary>
    /// Controllers with sound of their own: Sony's DualShock 4 (both models and its USB wireless adapter), DualSense and
    /// DualSense Edge; Microsoft's Xbox One, Elite and Series controllers and its wireless adapters.
    /// </summary>
    private static readonly HashSet<(int Vendor, int Product)> ControllerSoundIds =
    [
        (0x054C, 0x05C4), (0x054C, 0x09CC), (0x054C, 0x0BA0), (0x054C, 0x0CE6), (0x054C, 0x0DF2),
        (0x045E, 0x02D1), (0x045E, 0x02DD), (0x045E, 0x02E3), (0x045E, 0x02EA), (0x045E, 0x0B00), (0x045E, 0x0B12),
        (0x045E, 0x02E6), (0x045E, 0x02FE), (0x045E, 0x0B20),
    ];

    [GeneratedRegex(@"VID_([0-9A-F]{4})&PID_([0-9A-F]{4})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VidPid();

    /// <summary>Whether an audio endpoint's device (its "{1}.USB\VID_…&amp;PID_…" string) is a controller's own sound.</summary>
    internal static bool IsControllerSound(string? device) =>
        device is not null && VidPid().Match(device) is { Success: true } m
        && ControllerSoundIds.Contains((int.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            int.Parse(m.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)));

    private const uint EndpointActive = 1, EndpointDisabled = 2, EndpointUnplugged = 8, EndpointAnyState = 0xF;

    /// <summary>
    /// Every controller's speaker and microphone in Windows turned off (<paramref name="on"/> false) or back on: how
    /// many changed. One that is already as asked, or not plugged in, is left alone. Turned on, they don't take over:
    /// Windows makes a controller's mic (and its speaker for calls) the default the moment it comes on, so the defaults
    /// it had are put back.
    /// </summary>
    internal static int SetControllerSound(bool on)
    {
        var changed = 0;
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            var defaults = on ? Defaults(enumerator) : null;
            if (enumerator.EnumAudioEndpoints(EDataFlow.EAll, EndpointAnyState, out var all) != 0 || all.GetCount(out var count) != 0) return 0;
            IPolicyConfig? policy = null;
            for (var i = 0; i < count; i++)
            {
                if (all.Item(i, out var endpoint) != 0 || endpoint.GetState(out var state) != 0 || endpoint.GetId(out var id) != 0) continue;
                var wanted = on ? state == EndpointDisabled : state is EndpointActive or EndpointUnplugged;
                if (!wanted || StoreOf(endpoint) is not { } store || !IsControllerSound(StringOf(store, EndpointDeviceKey))) continue;
                policy ??= (IPolicyConfig)new PolicyConfigComObject();
                var result = policy.SetEndpointVisibility(id, on ? 1 : 0);
                if (result == 0) changed++;
                else AppLog.Info($"Devices: a controller's sound couldn't be turned {(on ? "on" : "off")} (0x{result:X8}).");
            }
            if (defaults is not null && changed > 0) KeepDefaults(enumerator, defaults, policy ??= (IPolicyConfig)new PolicyConfigComObject());
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            AppLog.Info($"Devices: controllers' sound in Windows couldn't be read (0x{ex.HResult:X8}).");
        }
        if (changed > 0) AppLog.Info($"Devices: {changed} controller sound device{(changed == 1 ? "" : "s")} turned {(on ? "on" : "off")} in Windows.");
        return changed;
    }

    private static readonly (EDataFlow Flow, ERole Role)[] DefaultSlots =
        [.. new[] { EDataFlow.ERender, EDataFlow.ECapture }.SelectMany(flow => new[] { ERole.EConsole, ERole.EMultimedia, ERole.ECommunications }.Select(role => (flow, role)))];

    /// <summary>Windows' default output and microphone for each of its roles, by endpoint id (null where there is none).</summary>
    private static string?[] Defaults(IMMDeviceEnumerator enumerator) =>
        [.. DefaultSlots.Select(slot => enumerator.GetDefaultAudioEndpoint(slot.Flow, slot.Role, out var device) == 0 && device.GetId(out var id) == 0 ? id : null)];

    /// <summary>
    /// For about two seconds after the controller's devices come on (Windows switches a moment later), each default that
    /// moved is set back to the one it was.
    /// </summary>
    private static void KeepDefaults(IMMDeviceEnumerator enumerator, string?[] before, IPolicyConfig policy)
    {
        var kept = 0;
        for (var tick = 0; tick < 10; tick++)
        {
            Thread.Sleep(200);
            var now = Defaults(enumerator);
            for (var i = 0; i < DefaultSlots.Length; i++)
                if (before[i] is { } was && now[i] != was && policy.SetDefaultEndpoint(was, DefaultSlots[i].Role) == 0) kept++;
        }
        if (kept > 0) AppLog.Info("Devices: Windows' default speakers and microphone kept as they were.");
    }

    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    private class PolicyConfigComObject;

    /// <summary>The policy interface Windows' Sound settings use for endpoints (undocumented, the same since Windows 7): only its last method is called.</summary>
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, out nint format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, int defaultFormat, out nint format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, nint endpointFormat, nint mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, int defaultPeriod, out long period, out long minimum);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, ref long period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, nint mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, nint mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, nint key, nint value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, nint key, nint value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, ERole role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, int visible);
    }
}
