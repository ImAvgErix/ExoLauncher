using System.Text.RegularExpressions;
using ExoLauncher.Helpers;
using Microsoft.Win32;

namespace ExoLauncher.Services;

/// <summary>
/// Reads the installed display adapters from the driver class key. No WMI
/// dependency, no vendor SDK. Used to gate FSR 4: AMD ships RDNA 4 shader
/// binaries in those DLLs, so on any other GPU Exo says unsupported instead
/// of swapping a file the driver will refuse.
/// </summary>
public static class GpuCapability
{
    public const string Fsr4NeedsRdna4 = "Unsupported on this GPU.";

    private const string DisplayClassKey =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    private static readonly string[] AdapterValueNames =
        ["DriverDesc", "HardwareInformation.AdapterString"];

    // RX 9060 / 9070 …, Radeon AI PRO R9700, and the gfx1200/gfx1201 ISA names.
    // The model number may carry a suffix letter, so no trailing word boundary.
    private static readonly Regex Rdna4Model = new(
        @"\b(?:RX\s*|R)9[0-9]{3}(?![0-9])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Rdna4Isa = new(
        @"\bgfx(?:11|12)[0-9]{2}\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Rdna3DiscreteModel = new(
        @"\bRX\s*7[0-9]{3}(?![0-9])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly object Gate = new();
    private static IReadOnlyList<string>? CachedAdapters;

    /// <summary>Display adapter names as the driver reports them.</summary>
    public static IReadOnlyList<string> Adapters()
    {
        lock (Gate)
            return CachedAdapters ??= ReadAdapters();
    }

    public static bool SupportsFsr4() => Adapters().Any(IsRdna4AdapterName);

    /// <summary>Null when the FSR 4 swap is allowed, otherwise the honest reason.</summary>
    public static string? Fsr4BlockReason() => SupportsFsr4() ? null : Fsr4NeedsRdna4;

    internal static bool IsRdna4AdapterName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var text = name.Trim();
        var amd = text.Contains("radeon", StringComparison.OrdinalIgnoreCase)
                  || text.Contains("amd", StringComparison.OrdinalIgnoreCase)
                  || text.Contains("gfx", StringComparison.OrdinalIgnoreCase);
        if (!amd) return false;
        return Rdna4Isa.IsMatch(text) || Rdna4Model.IsMatch(text) || Rdna3DiscreteModel.IsMatch(text);
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
