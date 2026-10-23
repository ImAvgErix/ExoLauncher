namespace ExoLauncher.Helpers;

/// <summary>What a Radeon or Intel profile reads from and writes to the GPU's driver: ADLX or IGCL on the PC, a fake in tests.</summary>
internal interface IGpuDriver
{
    /// <summary>The main GPU's settings, or null when the driver's library did not start.</summary>
    GpuDriverState? Read();

    /// <summary>Writes each value in order and reads it back; null when all held, else a plain line naming the first that didn't.</summary>
    string? Write(IReadOnlyList<KeyValuePair<string, int>> writes);
}

/// <summary>
/// The main GPU's name, its driver's version (AMD Software's from ADLX 1.3's IADLXGPU2 on a Radeon, the graphics
/// driver's on Intel; null when unknown), and every setting Exo touches that this driver has and the GPU supports
/// (switches 0 or 1, a level or mode as the vendor numbers it, frame caps in fps). A key is missing when the
/// driver lacks it. FpsMin and FpsMax are the frame cap's range when the driver gives one.
/// </summary>
internal sealed record GpuDriverState(string? Gpu, string? Software, IReadOnlyDictionary<string, int> Values, int FpsMin = 0, int FpsMax = 0);
