using System.Globalization;
using System.Runtime.InteropServices;

namespace ExoLauncher.Helpers;

/// <summary>
/// NVIDIA's management library (nvml.dll), which every NVIDIA driver installs in System32: asked in-process,
/// where nvidia-smi.exe would start a process each time. Nothing here is NVIDIA's to ship: the driver's own copy
/// is loaded by full path, and a PC without one (no NVIDIA driver) answers null.
/// </summary>
internal static unsafe class Nvml
{
    private const int Success = 0;

    /// <summary>The first NVIDIA GPU's CUDA compute capability and the driver's version, as "8.6, 621.01"; null without one.</summary>
    internal static string? FirstGpu()
    {
        if (!NativeLibrary.TryLoad(Path.Combine(Environment.SystemDirectory, "nvml.dll"), out var library)) return null;
        try
        {
            if (!NativeLibrary.TryGetExport(library, "nvmlInit_v2", out var init)
                || !NativeLibrary.TryGetExport(library, "nvmlShutdown", out var shutdown)
                || !NativeLibrary.TryGetExport(library, "nvmlDeviceGetHandleByIndex_v2", out var byIndex)
                || !NativeLibrary.TryGetExport(library, "nvmlDeviceGetCudaComputeCapability", out var capability)
                || !NativeLibrary.TryGetExport(library, "nvmlSystemGetDriverVersion", out var driverVersion))
                return null;
            if (((delegate* unmanaged[Cdecl]<int>)init)() != Success) return null;
            try
            {
                nint device;
                int major, minor;
                var text = stackalloc byte[80];
                if (((delegate* unmanaged[Cdecl]<uint, nint*, int>)byIndex)(0, &device) != Success
                    || ((delegate* unmanaged[Cdecl]<nint, int*, int*, int>)capability)(device, &major, &minor) != Success
                    || ((delegate* unmanaged[Cdecl]<byte*, uint, int>)driverVersion)(text, 80) != Success)
                    return null;
                return string.Create(CultureInfo.InvariantCulture, $"{major}.{minor}, {Marshal.PtrToStringAnsi((nint)text)}");
            }
            finally
            {
                ((delegate* unmanaged[Cdecl]<int>)shutdown)();
            }
        }
        finally
        {
            NativeLibrary.Free(library);
        }
    }
}
