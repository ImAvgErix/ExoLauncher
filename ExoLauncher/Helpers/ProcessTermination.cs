using System.Diagnostics;
using ExoLauncher.Adapters;
using ExoLauncher.Helpers;

namespace ExoLauncher;

/// <summary>
/// The one place Exo force-ends a process. Every other <c>Process.Kill</c> is a
/// build error (RS0030, see BannedSymbols.txt). Close normally first
/// (<c>ProcessHelper.TryCloseProcesses</c>, WM_CLOSE); come here only for a
/// process Exo started or a verified game whose close was refused. Anti-cheat
/// processes are refused outright, at the root and anywhere in a tree:
/// AGENTS.md's hard stop, made mechanical.
/// </summary>
internal static class ProcessTermination
{
    private static readonly HashSet<string> AntiCheatNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // Riot Vanguard
        "vgc", "vgk", "vgtray",
        // Easy Anti-Cheat, and EA's own
        "easyanticheat", "easyanticheat_eos", "easyanticheat_setup", "easyanticheat_eos_setup",
        "easyanticheat_launcher", "eac_launcher", "start_protected_game", "start_protected_game64",
        // BattlEye
        "beservice", "beservice_x64", "battleye", "battleye_launcher",
        // FACEIT
        "faceit", "faceitclient", "faceitservice",
        // PunkBuster
        "pnkbstra", "pnkbstrb",
        // XIGNCODE3
        "xigncode", "xigncode3", "xhunter1",
        // nProtect GameGuard
        "gamemon", "gamemon64", "gamemon.des", "gamemon64.des", "gameguard.des",
        // Tencent ACE
        "sguard64", "sguardsvc64",
    };

    // A game's own anti-cheat launcher: FortniteClient-Win64-Shipping_BE, ..._EAC, ..._EAC_EOS.
    private static readonly string[] AntiCheatLauncherSuffixes = ["_be", "_eac", "_eac_eos"];

    internal static bool IsAntiCheat(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return false;
        var name = processName.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return AntiCheatNames.Contains(name) ||
            name.StartsWith("eaanticheat.", StringComparison.OrdinalIgnoreCase) || // EA Javelin: GameService, GameServiceLauncher, Installer
            AntiCheatLauncherSuffixes.Any(suffix => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// What a tree kill ends, child first and the root last, from the tree's names.
    /// An anti-cheat member is skipped and listed in <c>Refused</c>; an anti-cheat
    /// root refuses the whole tree.
    /// </summary>
    internal static (IReadOnlyList<int> Kill, IReadOnlyList<int> Refused) PlanTree(
        (int Pid, string? Name) root, IReadOnlyList<(int Pid, string? Name)> descendantsChildFirst)
    {
        if (IsAntiCheat(root.Name))
            return ([], [root.Pid, .. descendantsChildFirst.Select(d => d.Pid)]);
        var kill = new List<int>();
        var refused = new List<int>();
        foreach (var (pid, name) in descendantsChildFirst)
            (IsAntiCheat(name) ? refused : kill).Add(pid);
        kill.Add(root.Pid);
        return (kill, refused);
    }

    /// <summary>
    /// Like <see cref="Process.Kill(bool)"/>, except no anti-cheat process is ever touched.
    /// A tree is ended one checked process at a time, never through <c>Kill(true)</c>, which
    /// would reach anti-cheat children unseen. Returns false when the close was refused
    /// (the process itself is anti-cheat), so a caller never counts it as ended.
    /// </summary>
    internal static bool Kill(Process process, bool entireProcessTree = false)
    {
        ArgumentNullException.ThrowIfNull(process);
        var name = NameOf(process);
        if (IsAntiCheat(name))
        {
            AppLog.Warn($"Refused to end {name}: Exo never stops an anti-cheat process");
            return false;
        }
        if (!entireProcessTree)
        {
            End(process, rethrow: true);
            return true;
        }

        // Each child stays open from plan to kill: an open handle keeps its pid from
        // being reused, so the process ended is the one whose name was checked.
        var children = new Dictionary<int, Process>();
        try
        {
            var descendants = new List<(int, string?)>();
            foreach (var pid in ProcessHelper.GetDescendantProcessIds(process.Id))
            {
                try { var child = Process.GetProcessById(pid); children[pid] = child; descendants.Add((pid, NameOf(child))); }
                catch (ArgumentException) { /* already gone */ }
            }
            var (kill, refused) = PlanTree((process.Id, name), descendants);
            if (refused.Count > 0)
                AppLog.Warn($"Ending {name} left {refused.Count} anti-cheat process(es) in its tree untouched");
            foreach (var pid in kill)
                End(pid == process.Id ? process : children[pid], rethrow: pid == process.Id);
        }
        finally
        {
            foreach (var child in children.Values) child.Dispose();
        }
        return true;
    }

    private static string? NameOf(Process process)
    {
        try { return process.ProcessName; }
        catch (InvalidOperationException) { return null; } // Exited: Kill behaves as it always did.
    }

    private static void End(Process process, bool rethrow)
    {
        try
        {
#pragma warning disable RS0030 // The sanctioned termination site: this method is what the ban points to.
            process.Kill(entireProcessTree: false);
#pragma warning restore RS0030
        }
        catch (Exception) when (!rethrow) { /* a child exiting or denied: the root still goes */ }
    }
}
