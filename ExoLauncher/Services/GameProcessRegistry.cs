using System.Collections.Concurrent;
using System.Diagnostics;
using ExoLauncher.Adapters;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>
/// Finds only executable processes which can be proven to belong to an installed
/// game. Store clients, overlays, anti-cheat, patchers, and services are a hard
/// deny-list even when they happen to be underneath a vendor install directory.
/// </summary>
internal sealed class GameProcessRegistry
{
    private static readonly HashSet<string> DeniedProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "steam", "steamwebhelper", "steamservice", "gameoverlayui", "steamerrorreporter",
        "epicgameslauncher", "epicwebhelper", "epiconlineservices", "eosoverlayrenderer-win64-shipping",
        "galaxyclient", "galaxyclientservice", "goggalaxynotifications",
        "riotclientservices", "riotclientux", "riotclientuxrender", "riotclientcrashhandler", "riot client",
        "leagueclient", "leagueclientux", "leagueclientuxrender",
        "vgc", "vgk", "easyanticheat", "easyanticheat_eos", "easyanticheat_eos_setup",
        "beservice", "beservice_x64", "battleye", "battleye_launcher", "eac_launcher",
        "start_protected_game", "start_protected_game64", "eossdk-win64-shipping",
        "crashreportclient", "crashhandler", "crashpad_handler", "unitycrashhandler32",
        "unitycrashhandler64", "unins000", "setup", "updater", "patcher", "launcher",
    };

    private readonly ConcurrentDictionary<string, ProcessIdentity> _launched =
        new(StringComparer.OrdinalIgnoreCase);

    internal static bool IsReservedProcessName(string? processName) =>
        string.IsNullOrWhiteSpace(processName) || DeniedProcessNames.Contains(processName);

    internal static bool IsEligibleExecutableForStop(
        GameEntry game,
        string? processName,
        string? executablePath)
    {
        if (!CanInspect(game) || IsReservedProcessName(processName) ||
            string.IsNullOrWhiteSpace(executablePath))
            return false;

        if (game.Store == StoreKind.Local)
        {
            // A portable registration names one executable. Never infer that
            // every .exe under its folder belongs to the game: a broad or
            // accidentally chosen root could otherwise expose unrelated apps.
            if (string.IsNullOrWhiteSpace(game.LaunchTarget))
                return false;
            try
            {
                if (!string.Equals(
                        Path.GetFullPath(executablePath),
                        Path.GetFullPath(game.LaunchTarget),
                        StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            catch { return false; }
        }

        if (game.Store == StoreKind.Riot)
        {
            var allowed = RiotAdapter.GameProcessNames(game.LaunchTarget!);
            if (allowed.Length == 0 || !allowed.Contains(processName!, StringComparer.OrdinalIgnoreCase))
                return false;
        }

        return ProcessHelper.IsPathUnderRoot(executablePath, game.Path!);
    }

    public GameRunState GetState(GameEntry game, bool discoverExternal = false)
    {
        if (!discoverExternal && !_launched.ContainsKey(game.Id))
            return default;
        var candidates = FindCandidates(game);
        return new GameRunState(candidates.Count > 0, candidates.Count > 0);
    }

    public void ObserveLaunch(GameEntry game, int? processId)
    {
        if (processId is not int pid || pid <= 0) return;
        if (TryReadEligibleIdentity(game, pid, out var identity))
            _launched[game.Id] = identity;
    }

    public async Task<GameStopResult> StopAsync(GameEntry game, CancellationToken cancellationToken = default)
    {
        var candidates = FindCandidates(game);
        if (candidates.Count == 0)
            return new GameStopResult(false, "No verified game process is running.");

        // The close request is graceful first. This reaches windows which do not
        // expose a MainWindowHandle without touching children or store clients.
        ProcessHelper.RequestCloseProcesses(candidates.Select(candidate => candidate.ProcessId));
        var graceDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTimeOffset.UtcNow < graceDeadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FindStillValid(game, candidates).Count == 0)
            {
                _launched.TryRemove(game.Id, out _);
                return new GameStopResult(true, "Game closed.");
            }
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        }

        // A force stop is deliberately limited to the exact identities that
        // ignored WM_CLOSE. Revalidate PID, start time, executable and install
        // root immediately before Kill(), and never kill a process tree.
        var stubborn = FindStillValid(game, candidates);
        foreach (var candidate in stubborn)
        {
            try
            {
                using var process = Process.GetProcessById(candidate.ProcessId);
                if (!MatchesIdentity(game, process, candidate)) continue;
                process.Kill(entireProcessTree: false);
            }
            catch { /* process may have exited or rejected a non-elevated close */ }
        }

        var forceDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTimeOffset.UtcNow < forceDeadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FindStillValid(game, candidates).Count == 0)
            {
                _launched.TryRemove(game.Id, out _);
                return new GameStopResult(true, "Game closed.");
            }
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        }
        return new GameStopResult(false, "Could not close the verified game process.");
    }

    private IReadOnlyList<ProcessIdentity> FindCandidates(GameEntry game)
    {
        if (!CanInspect(game)) return Array.Empty<ProcessIdentity>();

        var matches = new List<ProcessIdentity>();
        // Keep the identity observed during Exo's own launch, but treat it as
        // a hint only: PID reuse, a moved executable, or a changed install root
        // immediately invalidates it.
        if (_launched.TryGetValue(game.Id, out var launched))
        {
            try
            {
                using var process = Process.GetProcessById(launched.ProcessId);
                if (MatchesIdentity(game, process, launched))
                    matches.Add(launched);
                else
                    _launched.TryRemove(game.Id, out _);
            }
            catch { _launched.TryRemove(game.Id, out _); }
        }
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (TryReadEligibleIdentity(game, process, out var identity) &&
                    !matches.Any(match => match.ProcessId == identity.ProcessId))
                    matches.Add(identity);
            }
            catch { /* process exited or module inspection was denied */ }
            finally { process.Dispose(); }
        }
        return matches;
    }

    private static IReadOnlyList<ProcessIdentity> FindStillValid(
        GameEntry game,
        IReadOnlyList<ProcessIdentity> identities) =>
        identities.Where(identity =>
        {
            try
            {
                using var process = Process.GetProcessById(identity.ProcessId);
                return MatchesIdentity(game, process, identity);
            }
            catch { return false; }
        }).ToArray();

    private static bool CanInspect(GameEntry game) =>
        game.Installed &&
        !string.Equals(game.Id, "local:add", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(game.Path) &&
        Directory.Exists(game.Path) &&
        (game.Store != StoreKind.Local || LocalAdapter.IsSafePortableRegistrationRoot(game.Path)) &&
        // League's persistent client is not an in-game session. The explicit
        // process-name allow-list below makes Stop appear only after a match has
        // actually launched.
        !(game.Store == StoreKind.Riot && string.IsNullOrWhiteSpace(game.LaunchTarget));

    private static bool TryReadEligibleIdentity(GameEntry game, int processId, out ProcessIdentity identity)
    {
        identity = default;
        try
        {
            using var process = Process.GetProcessById(processId);
            return TryReadEligibleIdentity(game, process, out identity);
        }
        catch { return false; }
    }

    private static bool TryReadEligibleIdentity(GameEntry game, Process process, out ProcessIdentity identity)
    {
        identity = default;
        if (!CanInspect(game) || process.HasExited)
            return false;

        string? executable;
        DateTime started;
        try
        {
            executable = process.MainModule?.FileName;
            started = process.StartTime.ToUniversalTime();
        }
        catch { return false; }

        if (!IsEligibleExecutableForStop(game, process.ProcessName, executable))
            return false;

        // IsEligibleExecutableForStop rejects null/empty paths above; keep the
        // invariant explicit for nullable flow analysis and future callers.
        if (string.IsNullOrWhiteSpace(executable))
            return false;
        identity = new ProcessIdentity(process.Id, started.Ticks, Path.GetFullPath(executable));
        return true;
    }

    private static bool MatchesIdentity(GameEntry game, Process process, ProcessIdentity expected)
    {
        if (process.HasExited || process.Id != expected.ProcessId) return false;
        if (!TryReadEligibleIdentity(game, process, out var actual)) return false;
        return actual.StartedUtcTicks == expected.StartedUtcTicks &&
               string.Equals(actual.ExecutablePath, expected.ExecutablePath, StringComparison.OrdinalIgnoreCase);
    }

    private readonly record struct ProcessIdentity(int ProcessId, long StartedUtcTicks, string ExecutablePath);
}

internal readonly record struct GameRunState(bool IsRunning, bool CanStop);
internal readonly record struct GameStopResult(bool Ok, string Message);
