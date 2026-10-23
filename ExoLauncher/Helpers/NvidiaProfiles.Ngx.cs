using System.Globalization;

namespace ExoLauncher.Helpers;

internal static unsafe partial class NvidiaProfiles
{
    /// <summary>
    /// What the driver's DLSS override did in one process (NvAPI_NGX_GetNGXOverrideState, R570+,
    /// NV_NGX_DLSS_OVERRIDE_GET_STATE_PARAMS_V2): per feature a feedback mask (NV_NGX_DLSS_OVERRIDE_FLAG_*),
    /// and the preset, mode, ratio and frame generation the override set.
    /// </summary>
    internal readonly record struct NgxState(ulong Sr, ulong Rr, ulong Fg, float Ratio, uint Mode, uint Preset, uint FgCount, uint FgPreset, uint FgMode);

    // NV_NGX_DLSS_OVERRIDE_FLAG_* bits.
    private const ulong NgxEnabled = 1 << 1, NgxDllSelected = 1 << 4, NgxCreated = 1 << 9, NgxEvaluate = 1 << 10;
    private const ulong NgxErrors = 0x1FUL << 16; // failed, denied, DRS, not found, DLL load

    private const int DataNotFound = -121;

    /// <summary>The override's state in process <paramref name="pid"/> (all zero when it runs no DLSS); null when the driver has no such call or refuses.</summary>
    internal static NgxState? DlssState(int pid)
    {
        if (!Available) return null;
        // NV_NGX_DLSS_OVERRIDE_GET_STATE_PARAMS_V2: 96 bytes.
        var p = stackalloc byte[96];
        new Span<byte>(p, 96).Clear();
        *(uint*)p = 96 | (2u << 16);
        *(uint*)(p + 4) = (uint)pid;
        lock (Gate)
        {
            try
            {
                var status = F1(0x3FD96FBA)((nint)p);
                if (status == DataNotFound) return new NgxState(); // no DLSS in that process
                if (status != Ok) return null;
            }
            catch (EntryPointNotFoundException) { return null; }
        }
        return new NgxState(*(ulong*)(p + 8), *(ulong*)(p + 16), *(ulong*)(p + 24), *(float*)(p + 32),
            *(uint*)(p + 36), *(uint*)(p + 40), *(uint*)(p + 44), *(uint*)(p + 48), *(uint*)(p + 52));
    }

    /// <summary>Whether a game runs DLSS in that process at all (a feature created or evaluated).</summary>
    internal static bool RunsDlss(NgxState s) => ((s.Sr | s.Rr | s.Fg) & (NgxCreated | NgxEvaluate)) != 0;

    /// <summary>The state in words, for app.log: per feature whether Exo's override took and with what.</summary>
    internal static string Describe(NgxState s)
    {
        static string Feature(string name, ulong mask) =>
            (mask & (NgxCreated | NgxEvaluate)) == 0 ? ""
            : (mask & NgxErrors) != 0 ? $"{name} the game's own (override refused, 0x{mask:X})"
            : (mask & (NgxEnabled | NgxDllSelected)) != 0 ? $"{name} overridden"
            : $"{name} the game's own";
        var parts = new[] { Feature("Super Resolution", s.Sr), Feature("Ray Reconstruction", s.Rr), Feature("Frame Generation", s.Fg) }
            .Where(part => part.Length > 0).ToList();
        // The preset reads as the DRS preset does (1 = A); mode and frame count are logged as the driver gives them.
        if ((s.Sr | s.Rr) != 0)
            parts.Add(string.Create(CultureInfo.InvariantCulture,
                $"{GraphicsCatalog.ValueOf(GraphicsCatalog.SrPreset, s.Preset)}, mode {s.Mode}, scale {s.Ratio:0.##}"));
        if (s.Fg != 0)
            parts.Add($"frame generation {GraphicsCatalog.ValueOf(GraphicsCatalog.FgPreset, s.FgPreset)}, count {s.FgCount}, mode {s.FgMode}");
        return string.Join("; ", parts);
    }
}
