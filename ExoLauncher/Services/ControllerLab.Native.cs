using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using ExoLauncher.Helpers;
using Microsoft.Win32;
using static ExoLauncher.Services.Hid.HidInterop;

namespace ExoLauncher.Services;

/// <summary>
/// The lab's reads: a controller's input reports through its gamepad interface (opened shared and read only, as any
/// game opens it), its firmware report over USB, Sony's list of the newest firmware (once a day), and what else sits
/// between the controller and games (Steam Input's setting for PlayStation pads, remappers and their drivers, a polling
/// driver, Memory Integrity). Nothing is written to the controller, Steam or Windows.
/// </summary>
internal static partial class ControllerLab
{
    /// <summary>Reads the controller's input reports for <paramref name="duration"/> (or until cancelled), handing each to <paramref name="each"/> with when it came.</summary>
    private static async Task ReadAsync(Peripherals.Peripheral card, TimeSpan duration, Action<long, byte[], int> each, CancellationToken ct)
    {
        if (Gamepad(card) is not { } pad || Peripherals.InterfacePath(pad.Id) is not { } path)
            throw new InvalidOperationException("Exo couldn't open the controller.");
        var handle = CreateFile(path, GenericRead, FileShareRead | FileShareWrite, 0, OpenExisting, FileFlagOverlapped, 0);
        if (handle.IsInvalid) { handle.Dispose(); throw new InvalidOperationException("Exo couldn't open the controller."); }
        int length;
        using (var control = CreateFile(path, 0, FileShareRead | FileShareWrite, 0, OpenExisting, 0, 0))
            length = Math.Max(Caps(control) ?? 0, 64);
        await using var reader = new FileStream(handle, FileAccess.Read, 0, isAsync: true);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(duration);
        var buffer = new byte[length];
        try
        {
            while (!limit.IsCancellationRequested)
            {
                var n = await reader.ReadAsync(buffer, limit.Token).ConfigureAwait(false);
                if (n > 0) each(Stopwatch.GetTimestamp(), buffer, n);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
    }

    private static int? Caps(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        if (handle.IsInvalid || !HidD_GetPreparsedData(handle, out var preparsed)) return null;
        try { return HidP_GetCaps(preparsed, out var caps) == HidpStatusSuccess ? caps.InputReportByteLength : null; }
        finally { HidD_FreePreparsedData(preparsed); }
    }

    /// <summary>The firmware report (0x20), over USB only: over Bluetooth it would switch the pad to its full reports.</summary>
    internal static Firmware? ReadFirmware(Peripherals.Peripheral card)
    {
        using var pad = Pad.Open(card);
        return pad?.Get(0x20) is { } report ? FirmwareOf(report) : null;
    }

    // ------------------------------------------------------------------ Sony's newest firmware

    private const string FirmwareList = "https://fwupdater.dl.playstation.net/fwupdater/info.json";
    private static readonly HttpClient Http = CreateHttp();
    /// <summary>Sony's firmware list, read at most once a day and retried as every version check is (<see cref="LatestCache{T}"/>).</summary>
    private static readonly LatestCache<string> FirmwareFeed = new("Sony's firmware list", TimeSpan.FromDays(1));

    private static HttpClient CreateHttp()
    {
        var http = ExoHttp.Create(TimeSpan.FromSeconds(20));
        http.MaxResponseContentBufferSize = 16 * 1024;
        return http;
    }

    /// <summary>The newest firmware Sony offers for a series, read at most once a day; null offline or when the list doesn't name it.</summary>
    internal static async Task<ushort?> LatestAsync(byte series)
    {
        var text = await FirmwareFeed.GetAsync("list", async ct =>
        {
            var json = await Http.GetStringAsync(FirmwareList, ct).ConfigureAwait(false);
            using var check = JsonDocument.Parse(json);
            return json;
        }, CancellationToken.None).ConfigureAwait(false);
        if (text is null) return null;
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty(LatestKey(series), out var value) && value.ValueKind == JsonValueKind.String
            ? ParseVersion(value.GetString()) : null;
    }

    // ------------------------------------------------------------------ what sits between the controller and games

    /// <param name="SteamPlayStation">Steam's PlayStation Controller Support for the signed-in account: off, unsupported (only
    /// in games without their own support, Steam's default) or on; null without Steam or its settings.</param>
    /// <param name="Tools">Remappers and their drivers found on this PC, each with whether it's running now.</param>
    /// <param name="PollingDriver">hidusbf (Battle Beaver's overclock) is installed.</param>
    /// <param name="MemoryIntegrity">Windows' Memory Integrity is on (an overclock driver needs it off); null when Windows doesn't say.</param>
    internal sealed record Layers(string? SteamPlayStation, IReadOnlyList<Tool> Tools, bool PollingDriver, bool? MemoryIntegrity);

    internal sealed record Tool(string Name, bool Running);

    /// <summary>The programs and drivers that grab or translate a controller: name, process (if it is one) and service (if it has one).</summary>
    private static readonly (string Name, string? Process, string? Service)[] Known =
    [
        ("DS4Windows", "DS4Windows", null),
        ("DSX", "DSX", null),
        ("reWASD", "reWASD", "reWASDService"),
        ("x360ce", "x360ce", null),
        ("HidHide", null, "HidHide"),
        ("ViGEmBus", null, "ViGEmBus"),
    ];

    internal static Layers ReadLayers()
    {
        var tools = new List<Tool>();
        foreach (var (name, process, service) in Known)
        {
            var running = process is not null && Running(process);
            if (running || service is not null && ServiceExists(service)) tools.Add(new Tool(name, running));
        }
        return new Layers(SteamPlayStationSupport(), tools, ServiceExists("hidusbf"), MemoryIntegrityOn());
    }

    private static bool Running(string process)
    {
        var found = Process.GetProcessesByName(process);
        try { return found.Length > 0; }
        finally { foreach (var p in found) p.Dispose(); }
    }

    private static bool ServiceExists(string name)
    {
        try { using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}"); return key is not null; }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return false; }
    }

    internal static bool? MemoryIntegrityOn()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity");
            return key?.GetValue("Enabled") is int enabled ? enabled == 1 : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    /// <summary>The account's <c>SteamController_PSSupport</c> (top of its localconfig.vdf): 0 off, 1 or absent games without their own support, 2 on.</summary>
    private static string? SteamPlayStationSupport()
    {
        if (Adapters.SteamInstallation.ResolveRoot() is not { } root || Adapters.SteamPlaytime.TryGetLocalConfigPath(root) is not { } path
            || Adapters.SteamPlaytime.ReadSharedText(path) is not { } text) return null;
        return PlayStationSupport(Adapters.SteamKeyValues.ParseText(text).Section("UserLocalConfigStore").Value("SteamController_PSSupport"));
    }

    internal static string PlayStationSupport(string? value) => value switch { "0" => "off", "2" => "on", _ => "unsupported" };
}
