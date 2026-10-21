using System.Diagnostics;
using ExoLauncher.Helpers;
using Microsoft.Win32;

namespace ExoLauncher.Services;

/// <summary>
/// NVIDIA's DLSS overlay (NGXCore). DLSS presets are System > Graphics > Driver and profile's
/// (<see cref="GpuProfileService"/>).
/// </summary>
public sealed partial class DlssSwapService
{
    private const string NgxCoreKey = @"SOFTWARE\NVIDIA Corporation\Global\NGXCore";
    // DLSS Swapper's "for all DLSS DLLs" value; 1 shows it for debug DLLs only.
    private const int IndicatorAllDlls = 1024;

    public sealed record IndicatorResult(bool Available, bool On, bool Ok, string? Message);

    /// <summary>NVIDIA's own DLSS overlay (version and preset in the corner of a DLSS game), from NGXCore.</summary>
    public static IndicatorResult GetIndicator()
    {
        if (!NvidiaProfiles.Available) return new IndicatorResult(false, false, true, null);
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(NgxCoreKey);
            return new IndicatorResult(true, key?.GetValue("ShowDlssIndicator") is int value && value != 0, true, null);
        }
        catch (Exception ex) { return new IndicatorResult(true, false, false, "Could not read the setting: " + ex.Message); }
    }

    /// <summary>
    /// The value is machine-wide under HKLM, so Windows asks for administrator
    /// rights each time it changes; Exo itself stays unelevated.
    /// </summary>
    public static async Task<IndicatorResult> SetIndicatorAsync(bool on)
    {
        if (!NvidiaProfiles.Available) return new IndicatorResult(false, false, false, "This PC has no NVIDIA driver.");
        var reg = Path.Combine(Environment.SystemDirectory, "reg.exe");
        var start = new ProcessStartInfo(reg, $"add \"HKLM\\{NgxCoreKey}\" /v ShowDlssIndicator /t REG_DWORD /d {(on ? IndicatorAllDlls : 0)} /f /reg:64")
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        try
        {
            using var process = Process.Start(start);
            if (process is null) return GetIndicator() with { Ok = false, Message = "Windows did not start the change." };
            await process.WaitForExitAsync().ConfigureAwait(false);
            AppLog.Info($"DLSS indicator: on={on}; exit={process.ExitCode}.");
            var now = GetIndicator();
            return now.On == on ? now : now with { Ok = false, Message = "The setting did not change." };
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return GetIndicator() with { Ok = false, Message = "Not changed: Windows asked for permission and it was declined." };
        }
        catch (Exception ex)
        {
            AppLog.Warn("DLSS indicator not changed: " + ex.Message);
            return GetIndicator() with { Ok = false, Message = "Could not change the setting." };
        }
    }
}
