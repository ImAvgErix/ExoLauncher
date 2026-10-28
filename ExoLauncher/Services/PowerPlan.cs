using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// Exo's power plan: one Windows plan named "Exo", a copy of Balanced (so AMD's and Intel's tuning of Balanced carries
/// over, and X3D core parking keeps working), with this PC's processor class's settings written into it
/// (<see cref="PowerProfiles"/>) and shown in Control Panel's advanced power settings. On a PC running one of Windows'
/// own plans Exo switches to it by itself once (<see cref="Auto"/>); on a custom or third-party plan System > This PC
/// offers it and Exo never switches by itself. Undo puts back the plan it replaced and Exo doesn't switch again; picking
/// another plan in Windows counts the same. A deleted Exo plan is made again the next time it's used, and Exo's
/// uninstaller deletes it (tools/installer/Remove-ExoPowerPlan.ps1 reads the same file). The active plan's own
/// processor settings are read for This PC's advice (<see cref="Limits"/>).
/// </summary>
internal sealed class PowerPlan(PcHealth.IPowerApi api, string statePath)
{
    internal const string PlanName = "Exo";

    internal static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    internal static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    internal static readonly Guid PowerSaver = new("a1841308-3541-4fab-bc81-f71556f20b4a");
    internal static readonly Guid Ultimate = new("e9a42b02-d5df-448d-aa00-03f14749eb61");
    internal static readonly Guid BestPerformance = new("ded574b5-45a0-4f42-8737-46345c09c238");
    internal static readonly Guid BestEfficiency = new("961cc777-2547-4f9d-8174-7d86181b8a7a");

    // Three of the processor's settings (PROCTHROTTLEMAX, PERFBOOSTMODE, CPMINCORES in powercfg /qh).
    private static readonly Guid MaxStateSetting = new("bc5038f7-23e0-4960-96da-33abaf5935ec");
    private static readonly Guid BoostModeSetting = new("be337238-0d82-4146-a960-4f3749d470c7");
    private static readonly Guid MinCoresSetting = new("0cc5b647-c1df-4637-891a-dec35c318583");

    internal static PowerPlan Shared { get; } = new(new PcHealth.NativePower(), Path.Combine(PathHelper.AppDataDir, "exo-power-plan.json"));

    /// <summary>The active plan's processor settings on AC: maximum processor state (%), boost mode (0 = disabled) and core parking's minimum cores (%; 100 = parking off). Null where the plan doesn't say.</summary>
    internal sealed record Limits(uint? MaxState, uint? BoostMode, uint? MinCores);

    /// <summary>
    /// <paramref name="Exo"/>: Exo's plan is on. <paramref name="CanUndo"/>: Exo switched to it and holds the plan it
    /// replaced. <paramref name="Stock"/>: one of Windows' own plans is on.
    /// </summary>
    internal sealed record State(Guid Active, string? Name, Guid Overlay, bool Battery, bool Exo, bool CanUndo, Limits? Cpu = null)
    {
        internal bool Stock => IsStock(Active);
    }

    /// <summary>What Exo keeps: its plan, the plan it replaced (and its power mode), and whether the user turned it down.</summary>
    internal sealed record Saved(Guid? Plan, Guid? Previous, Guid PreviousOverlay, bool Declined);

    internal static bool IsStock(Guid scheme) => scheme == Balanced || scheme == HighPerformance || scheme == PowerSaver || scheme == Ultimate;

    internal State? Read()
    {
        if (api.Active() is not { } active) return null;
        var saved = Load();
        var exo = saved.Plan is { } plan && active == plan;
        // Another plan was picked in Windows since Exo switched: that's the user's choice now.
        if (!exo && saved.Previous is not null) Save(saved = saved with { Previous = null, Declined = true });
        var cpu = new Limits(api.AcValue(active, PowerProfiles.Processor, MaxStateSetting), api.AcValue(active, PowerProfiles.Processor, BoostModeSetting), api.AcValue(active, PowerProfiles.Processor, MinCoresSetting));
        return new State(active, api.Name(active), api.Overlay(), api.Battery(), exo, exo && saved.Previous is not null, cpu);
    }

    /// <summary>
    /// Writes <paramref name="profile"/> into Exo's plan (made from Balanced if it's missing) and switches to it,
    /// keeping the plan it replaces for Undo (a second Apply keeps the first). Null when done, else the line to show.
    /// </summary>
    internal string? Apply(PowerProfiles.Profile profile)
    {
        if (api.Active() is not { } active) return "Windows didn't say which plan is on.";
        var saved = Load();
        if (Ensure(saved) is not { } plan) return "Windows didn't make Exo's plan.";
        Write(plan, profile);
        if (active != plan) saved = saved with { Previous = active, PreviousOverlay = api.Overlay() };
        // Save before switching, so a crash right after still has the way back.
        if (!Save(saved with { Plan = plan, Declined = false })) return "Your current plan couldn't be saved for Undo, so nothing changed.";
        if (!api.SetActive(plan))
        {
            Save(saved with { Plan = plan, Previous = null });
            return "Windows didn't switch to Exo's plan.";
        }
        AppLog.Info($"Power plan: Exo's plan ({profile.Key}{(profile.Laptop ? ", laptop" : "")}) is on (was {active}).");
        return null;
    }

    /// <summary>The plan Exo replaced, and its power mode if it was Balanced. Exo doesn't switch by itself again.</summary>
    internal string? Undo()
    {
        var saved = Load();
        if (saved.Previous is not { } previous) return "There's nothing to undo.";
        var plan = api.SetActive(previous);
        var mode = previous != Balanced || api.Battery() || api.Overlay() == saved.PreviousOverlay || api.SetOverlay(saved.PreviousOverlay);
        Save(saved with { Previous = null, Declined = true });
        AppLog.Info($"Power plan: put back to {previous}{(plan && mode ? "" : " (Windows refused part of it)")}.");
        return plan ? mode ? null : "Windows didn't put your power mode back." : "Windows didn't put your plan back. It may have been deleted.";
    }

    /// <summary>
    /// At start: on one of Windows' own plans, switch to Exo's once (never after Undo or another pick); with Exo's plan
    /// on, write the profile again (a newer Exo, a driver update), making the plan again if it was deleted.
    /// Null when nothing changed or it switched, else the line Apply returned.
    /// </summary>
    internal string? Auto(PowerProfiles.Profile profile)
    {
        if (api.Active() is not { } active) return null;
        var saved = Load();
        var inUse = saved.Plan is { } plan && (active == plan || (saved.Previous is not null && api.Name(plan) is null));
        if (inUse) return Apply(profile);
        if (saved.Declined || !IsStock(active)) return null;
        return Apply(profile);
    }

    /// <summary>Exo's plan, made from Balanced and named when it doesn't exist (first use, or deleted since).</summary>
    private Guid? Ensure(Saved saved)
    {
        if (saved.Plan is { } plan && api.Name(plan) is not null) return plan;
        if (api.Duplicate(Balanced) is not { } made) return null;
        api.Rename(made, PlanName, "Exo Launcher's plan, tuned for this PC's processor. Undo in Exo's System > This PC puts your previous plan back.");
        AppLog.Info($"Power plan: made Exo's plan {made} from Balanced.");
        return made;
    }

    private void Write(Guid plan, PowerProfiles.Profile profile)
    {
        var refused = new List<string>();
        foreach (var value in profile.Values)
        {
            if (!api.Write(plan, value.Subgroup, value.Setting, value.Ac, dc: false) || (value.Dc && !api.Write(plan, value.Subgroup, value.Setting, value.Ac, dc: true)))
                refused.Add(value.Name);
            // Shown in Control Panel's advanced settings (Windows hides several); needs an administrator, so a refusal only hides it.
            api.Show(value.Subgroup, value.Setting);
        }
        // Windows' other hidden settings worth tuning, shown too (left as they are unless the profile wrote them above).
        foreach (var (subgroup, setting) in WorthSeeing) api.Show(subgroup, setting);
        // A PC without the device (no SATA, no second NVMe) has no such setting: Windows refuses those, and that's fine.
        if (refused.Count > 0) AppLog.Info("Power plan: not on this PC: " + string.Join(", ", refused) + ".");
    }

    private static readonly Guid ProcessorGroup = new("54533251-82be-4824-96c1-47b60b740d00"), UsbGroup = new("2a737441-1930-4402-8d77-b2bebba308a3");

    /// <summary>
    /// Hidden settings worth tuning, shown in Control Panel without being changed where the profile doesn't write them:
    /// boost, a frequency cap, every efficiency class's energy preference, state limits and parking (class 1 is a hybrid's
    /// P-cores), autonomous mode, how a hybrid places long and short threads and which policy is in effect, the latency
    /// hint, and USB 3 link power. Left hidden: what gaming plans turn without a measured effect (idle promote/demote, the
    /// check interval, throttle states) and what Windows stops using once the processor picks its own clocks (increase and
    /// decrease thresholds, times and policies).
    /// </summary>
    internal static readonly (Guid Subgroup, Guid Setting)[] WorthSeeing =
    [
        (ProcessorGroup, new("be337238-0d82-4146-a960-4f3749d470c7")), // boost mode (PERFBOOSTMODE)
        (ProcessorGroup, new("75b0ae3f-bce0-45a7-8c89-c9611c25e100")), // maximum frequency, MHz (PROCFREQMAX)
        (ProcessorGroup, new("36687f9e-e3a5-4dbf-b1dc-15eb381c6863")), // energy preference (PERFEPP)
        (ProcessorGroup, new("36687f9e-e3a5-4dbf-b1dc-15eb381c6864")), // energy preference, efficiency class 1 (PERFEPP1)
        (ProcessorGroup, new("36687f9e-e3a5-4dbf-b1dc-15eb381c6865")), // energy preference, efficiency class 2 (PERFEPP2)
        (ProcessorGroup, new("893dee8e-2bef-41e0-89c6-b55d0929964d")), // minimum state, efficiency class 1 (PROCTHROTTLEMIN1)
        (ProcessorGroup, new("bc5038f7-23e0-4960-96da-33abaf5935ed")), // maximum state, efficiency class 1 (PROCTHROTTLEMAX1)
        (ProcessorGroup, new("0cc5b647-c1df-4637-891a-dec35c318584")), // core parking min cores, efficiency class 1 (CPMINCORES1)
        (ProcessorGroup, new("ea062031-0e34-4ff1-9b6d-eb1059334028")), // core parking max cores (CPMAXCORES)
        (ProcessorGroup, new("8baa4a8a-14c6-4451-8e8b-14bdbd197537")), // autonomous mode (PERFAUTONOMOUS)
        (ProcessorGroup, new("7f2f5cfa-f10c-4823-b5e1-e93ae85f46b5")), // heterogeneous policy in effect (HETEROPOLICY)
        (ProcessorGroup, new("93b8b6dc-0698-4d1c-9ee4-0644e900c85d")), // heterogeneous thread scheduling (SCHEDPOLICY)
        (ProcessorGroup, new("bae08b81-2d5e-4688-ad6a-13243356654b")), // short running threads (SHORTSCHEDPOLICY)
        (ProcessorGroup, new("619b7505-003b-4e82-b7a6-4dd29c300971")), // latency sensitivity hint (LATENCYHINTPERF)
        (UsbGroup, new("d4e98f31-5ffe-4ce1-be31-1b38b384c009")),       // USB 3 link power management
    ];

    private Saved Load()
    {
        try { return (File.Exists(statePath) ? JsonSerializer.Deserialize<Saved>(File.ReadAllText(statePath)) : null) ?? new(null, null, Guid.Empty, false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return new(null, null, Guid.Empty, false); }
    }

    private bool Save(Saved saved)
    {
        try
        {
            var temp = statePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(saved));
            File.Move(temp, statePath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("Power plan: Exo's plan state couldn't be saved: " + ex.Message);
            return false;
        }
    }
}
