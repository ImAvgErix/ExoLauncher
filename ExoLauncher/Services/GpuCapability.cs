using ExoLauncher.Helpers;
using Microsoft.Win32;

namespace ExoLauncher.Services;

/// <summary>
/// Reads the installed display adapters from the driver class key. No WMI
/// dependency, no vendor SDK. Used to say where DLSS presets are chosen.
/// </summary>
public static class GpuCapability
{
    private const string DisplayClassKey =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    private static readonly string[] AdapterValueNames =
        ["DriverDesc", "HardwareInformation.AdapterString"];

    private static readonly object Gate = new();
    private static IReadOnlyList<string>? CachedAdapters;

    /// <summary>Display adapter names as the driver reports them.</summary>
    public static IReadOnlyList<string> Adapters()
    {
        lock (Gate)
            return CachedAdapters ??= ReadAdapters();
    }

    /// <summary>
    /// Every display driver's version, read fresh (one registry read): it
    /// changes when a graphics driver is updated, without SCSKiller running.
    /// </summary>
    internal static string DriverFingerprint()
    {
        if (!OperatingSystem.IsWindows()) return "";
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(DisplayClassKey);
            if (root is null) return "";
            return string.Join('|', root.GetSubKeyNames()
                .Where(child => child.Length == 4 && child.All(char.IsAsciiDigit))
                .Select(child =>
                {
                    using var adapter = root.OpenSubKey(child);
                    return adapter?.GetValue("DriverVersion") as string ?? "";
                }));
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    /// <summary>The most video memory a display adapter reports (HardwareInformation.qwMemorySize in its driver key), in GB.</summary>
    public static double? VideoMemoryGb()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(DisplayClassKey);
            if (root is null) return null;
            double? best = null;
            foreach (var child in root.GetSubKeyNames())
            {
                if (child.Length != 4 || !child.All(char.IsAsciiDigit)) continue;
                try
                {
                    using var adapter = root.OpenSubKey(child);
                    var bytes = adapter?.GetValue("HardwareInformation.qwMemorySize") switch { long l => l, byte[] b when b.Length >= 8 => BitConverter.ToInt64(b, 0), _ => 0L };
                    if (bytes > 0) best = Math.Max(best ?? 0, bytes / 1073741824.0);
                }
                catch { /* skip one adapter */ }
            }
            return best;
        }
        catch { return null; }
    }

    private static IReadOnlyList<string> ReadAdapters()
    {
        var names = new List<string>();
        if (!OperatingSystem.IsWindows()) return names;
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(DisplayClassKey);
            if (root is null) return names;
            foreach (var child in root.GetSubKeyNames())
            {
                if (child.Length != 4 || !child.All(char.IsAsciiDigit)) continue;
                try
                {
                    using var adapter = root.OpenSubKey(child);
                    if (adapter is null) continue;
                    foreach (var value in AdapterValueNames)
                    {
                        if (adapter.GetValue(value) is not string text) continue;
                        var trimmed = text.Trim();
                        if (trimmed.Length == 0) continue;
                        if (!names.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                            names.Add(trimmed);
                    }
                }
                catch
                {
                    /* skip one adapter */
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Debug("GPU probe failed: " + ex.Message);
        }

        return names;
    }
}
