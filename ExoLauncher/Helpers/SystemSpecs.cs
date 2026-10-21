using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using ExoLauncher.Adapters;
using ExoLauncher.Services;
using Microsoft.Win32;

namespace ExoLauncher.Helpers;

/// <summary>
/// This PC's processor, installed memory, graphics card and Windows, read once (none of it changes
/// while Exo runs), and the free space where a Store game would go, read each time: the Steam library
/// with the most room, else the Windows drive. The Store judges a game's requirements against it.
/// </summary>
internal static class SystemSpecs
{
    private static readonly Lazy<StoreService.ThisPc> Once = new(ReadNow, LazyThreadSafetyMode.ExecutionAndPublication);

    internal static StoreService.ThisPc Read() => Once.Value with { FreeGb = FreeGb(), Online = NetworkInterface.GetIsNetworkAvailable() };

    private static StoreService.ThisPc ReadNow()
    {
        string? cpu = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            // "AMD Ryzen 5 5600X 6-Core Processor" reads as the game pages write it: "AMD Ryzen 5 5600X".
            cpu = (key?.GetValue("ProcessorNameString") as string) is { } name
                ? Regex.Replace(name, @"\s+(\d+-Core|Six-Core|Eight-Core|Quad-Core)?\s*Processor\s*$|\(R\)|\(TM\)|\s+CPU\s+@.*$", "", RegexOptions.IgnoreCase).Trim()
                : null;
        }
        catch { /* left out */ }
        // The memory installed (what the box says), not what Windows can use after hardware reserves.
        var ramGb = GetPhysicallyInstalledSystemMemory(out var kilobytes) ? (int)Math.Round(kilobytes / 1048576.0) : 0;
        // The adapter names from the driver key alone: never the GPU profile, whose read can write the frame cap.
        var adapters = GpuCapability.Adapters();
        var gpu = GraphicsService.Vendors(adapters)
            .Select(vendor => adapters.Where(name => GraphicsService.Vendors([name]).Contains(vendor))
                .OrderByDescending(name => name.Any(char.IsAsciiDigit)).First()) // a model number, not "Radeon Graphics"
            .FirstOrDefault()?.Replace("(R)", "", StringComparison.OrdinalIgnoreCase).Replace("(TM)", "", StringComparison.OrdinalIgnoreCase).Trim();
        var windows = Environment.OSVersion.Version.Build >= 22000 ? 11 : 10;
        return new StoreService.ThisPc(cpu, gpu, ramGb, windows, null, GpuCapability.VideoMemoryGb(), DirectXVersion());
    }

    /// <summary>The most free space on a Steam library's drive (where Get on Steam installs), else on the Windows drive, in GB.</summary>
    private static double? FreeGb()
    {
        var roots = new List<string>();
        if (SteamInstallation.ResolveRoot() is { } steam)
        {
            roots.Add(steam);
            try
            {
                var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
                if (File.Exists(vdf))
                    roots.AddRange(Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\"").Select(m => m.Groups[1].Value.Replace(@"\\", @"\", StringComparison.Ordinal)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the Steam folder alone */ }
        }
        else roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        double? best = null;
        foreach (var drive in roots.Select(Path.GetPathRoot).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try { best = Math.Max(best ?? 0, new DriveInfo(drive).AvailableFreeSpace / 1073741824.0); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { /* not ready */ }
        }
        return best;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicallyInstalledSystemMemory(out ulong totalMemoryInKilobytes);

    /// <summary>
    /// The DirectX a game can count on: 12 when the main graphics card runs Direct3D 12 at feature level 12_0 (asked
    /// of the driver without making a device), else 11, which every card Windows 10 and 11 run on has.
    /// </summary>
    private static int DirectXVersion()
    {
        try
        {
            var device = new Guid("189819f1-1db6-4b57-be54-1821339b85f7"); // ID3D12Device
            return D3D12CreateDevice(IntPtr.Zero, 0xc000, ref device, IntPtr.Zero) >= 0 ? 12 : 11;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return 11; }
    }

    // A null device asks only whether one could be made (S_FALSE when it could).
    [DllImport("d3d12.dll")]
    private static extern int D3D12CreateDevice(IntPtr adapter, int minimumFeatureLevel, ref Guid riid, IntPtr device);
}
