using Keys = ExoLauncher.Helpers.IntelIgcl.Keys;

namespace ExoLauncher.Helpers;

/// <summary>
/// Exo's Intel profile: the global IGCL settings "Use Exo's profile" writes on an Intel Arc or Iris Xe GPU.
/// Pure, so every value is tested; docs/architecture/gpu-profiles.md › Intel has the reasons.
/// - Endurance Gaming and the sharpening filter off (Endurance Gaming caps frames on battery to save power, as
///   Radeon Chill does; sharpening every game adds halos to what each game already sharpens).
/// - Low latency mode: On by default (one queued frame, as NVIDIA's Low latency On); "On with boost" (also keeps the
///   GPU's clock up while the CPU limits, which costs power) and Off are the user's other choices.
/// - Adaptive Sync on, and with a main screen that can run it the frame limit at the same cap as NVIDIA's G-SYNC
///   profile (<see cref="FrameCap"/>); without one the limit is off, as No G-SYNC leaves the cap off.
/// - Prebuilt shader downloads on: the driver fetches Intel's compiled shaders for games it has them for, so they
///   don't compile on the PC while you play.
/// - XeSS frame generation's override is never written here: it stays the player's, per game.
/// </summary>
internal static partial class GpuProfile
{
    internal const string LowLatencyOn = "on", LowLatencyBoost = "boost", LowLatencyOff = "off";

    internal static bool IsLowLatencyChoice(string? choice) => choice is LowLatencyOn or LowLatencyBoost or LowLatencyOff;

    /// <summary>
    /// A game's XeSS frame generation as the driver's override (ctl_3d_frame_generation_override_t): the game's own
    /// choice 0, 2x 1, 3x 2, 4x 3. Index = the driver's value.
    /// </summary>
    internal static readonly string[] FrameGenChoices = ["game", "2x", "3x", "4x"];

    /// <summary>The driver's value for a frame generation choice; null for a choice Exo doesn't know.</summary>
    internal static int? FrameGenValue(string? choice) => Array.IndexOf(FrameGenChoices, choice) is >= 0 and var index ? index : null;

    /// <summary>The frame generation choice for the driver's value ("game" for anything else).</summary>
    internal static string FrameGenChoice(int value) => value > 0 && value < FrameGenChoices.Length ? FrameGenChoices[value] : FrameGenChoices[0];

    /// <summary>Low latency mode's value (ctl_3d_low_latency_types_t: off 0, on 1, on with boost 2) for a choice.</summary>
    internal static int LowLatencyValue(string choice) => choice switch { LowLatencyBoost => 2, LowLatencyOff => 0, _ => 1 };

    /// <summary>
    /// Every write of the Intel profile, only for settings this driver has, in order: Endurance Gaming off before the
    /// frame limit (it caps frames on battery), Adaptive Sync before the limit that keeps frames inside its range.
    /// </summary>
    internal static List<KeyValuePair<string, int>> IntelWrites(GpuDriverState state, bool vrr, int refreshHz, string lowLatency)
    {
        var writes = new List<KeyValuePair<string, int>>();
        void Write(string key, int value)
        {
            if (state.Values.ContainsKey(key)) writes.Add(new(key, value));
        }
        Write(Keys.Endurance, 0);
        Write(Keys.Sharpening, 0);
        Write(Keys.LowLatency, LowLatencyValue(lowLatency));
        Write(Keys.AdaptiveSync, 1);
        var cap = vrr ? FrameCap(refreshHz) : 0;
        Write(Keys.FrameLimit, cap > 0 && state.FpsMax > state.FpsMin ? Math.Clamp(cap, state.FpsMin, state.FpsMax) : cap);
        Write(Keys.ShaderDownload, 1);
        return writes;
    }

    /// <summary>What the Intel profile does, in one line under its name.</summary>
    internal static string IntelSummary(IReadOnlyList<KeyValuePair<string, int>> writes, string lowLatency)
    {
        bool Has(string key) => writes.Any(write => write.Key == key);
        var on = new List<string>();
        if (Has(Keys.AdaptiveSync)) on.Add("Adaptive Sync on");
        if (writes.FirstOrDefault(write => write.Key == Keys.FrameLimit) is { Key: not null, Value: > 0 } cap) on.Add($"frames capped at {cap.Value} fps");
        if (Has(Keys.LowLatency))
            on.Add(lowLatency switch { LowLatencyBoost => "Low latency with boost", LowLatencyOff => "Low latency off", _ => "Low latency on" });
        if (Has(Keys.ShaderDownload)) on.Add("Intel's prebuilt shaders");
        var off = new (string Key, string Name)[] { (Keys.Endurance, "Endurance Gaming"), (Keys.Sharpening, "sharpening") }
            .Where(item => Has(item.Key)).Select(item => item.Name).ToList();
        var parts = new List<string>();
        if (on.Count > 0) parts.Add(string.Join(", ", on));
        if (off.Count > 0) parts.Add(string.Join(" and ", off) + " off");
        var line = string.Join("; ", parts);
        return line.Length == 0 ? line : char.ToUpperInvariant(line[0]) + line[1..];
    }
}
