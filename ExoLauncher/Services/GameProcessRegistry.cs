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
        "gameoverlayui64",
        "wallpaper32", "wallpaper64", "wallpaperservice32", "wallpaperservice64", "webwallpaper32", "webwallpaper64",
        "epicgameslauncher", "epicwebhelper", "epiconlineservices", "eosoverlayrenderer-win64-shipping",
        "galaxyclient", "galaxyclientservice", "goggalaxynotifications",
        "riotclientservices", "riotclientux", "riotclientuxrender", "riotclientcrashhandler", "riot client",
        "leagueclient", "leagueclientux", "leagueclientuxrender",
        "xboxpcapp", "gamingapp", "eadesktop", "ubisoftconnect", "ubisoftgamelauncher", "upc", "uplaywebcore",
        "battle.net", "amazon games", "amazongames", "amazongamesui",
        "rockstarservice", "socialclubhelper", "launcherpatcher",
        "vgc", "vgk", "easyanticheat", "easyanticheat_eos", "easyanticheat_eos_setup",
        "beservice", "beservice_x64", "battleye", "battleye_launcher", "eac_launcher",
        "start_protected_game", "start_protected_game64", "eossdk-win64-shipping",
        "crashreportclient", "crashhandler", "crashpad_handler", "unitycrashhandler32",
        "unitycrashhandler64", "unins000", "setup", "updater", "patcher", "launcher",
    };

    private readonly ConcurrentDictionary<string, ProcessIdentity> _launched =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Func<Process, string?> _readExecutablePath;
    private readonly SteamGameProcessLog _steamProcesses;
    private readonly Func<IReadOnlyList<ProcessObservation>> _snapshot;
    internal readonly record struct ProcessObservation(int ProcessId, string Name, string? ExecutablePath, long StartedUtcTicks)
    {
        /// <summary>The image path as <see cref="ProcessHelper.IsPathUnderRoot"/> normalizes it, once per process.</summary>
        internal string? FullPath { get; } = FullPathOf(ExecutablePath);

        private static string? FullPathOf(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try { return Path.GetFullPath(path); }
            catch { return null; }
        }
    }

    /// <summary>Raised when an exact game id gains or loses its verified process.</summary>
    internal event Action<string, GameRunState>? StateChanged;

    internal GameProcessRegistry(Func<Process, string?>? readExecutablePath = null, Func<string>? readSteamProcessLog = null,
        Func<IReadOnlyList<ProcessObservation>>? readSnapshot = null)
    {
        _readExecutablePath = readExecutablePath ?? ProcessHelper.TryGetExecutablePath;
        _steamProcesses = new SteamGameProcessLog(readSteamProcessLog);
        _snapshot = readSnapshot ?? CaptureSnapshot;
    }

    internal static bool IsReservedProcessName(string? processName) =>
        string.IsNullOrWhiteSpace(processName) ||
        DeniedProcessNames.Contains(processName) || ProcessTermination.IsAntiCheat(processName) ||
        ProcessHelper.IsNonGameProcessName(processName);

    /// <summary>
    /// Exo currently has exact install/path and launch contracts only for these
    /// backends. A detected official client is intentionally not enough to
    /// infer that one of its games is safe to inspect or stop.
    /// </summary>
    internal static bool SupportsGameProcessControl(StoreKind store) => store is
        StoreKind.Steam or StoreKind.Epic or StoreKind.Gog or StoreKind.Riot or StoreKind.Local
        or StoreKind.Ea or StoreKind.Ubisoft or StoreKind.Xbox
        or StoreKind.BattleNet or StoreKind.Amazon or StoreKind.Rockstar
        or StoreKind.Itch or StoreKind.Minecraft or StoreKind.Roblox
        or StoreKind.Paradox or StoreKind.Wargaming;

    internal static bool IsEligibleExecutableForStop(
        GameEntry game,
        string? processName,
        string? executablePath)
    {
        if (!SupportsGameProcessControl(game.Store) ||
            !CanInspect(game) || IsReservedProcessName(processName) ||
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
        var wasBound = _launched.ContainsKey(game.Id);
        if (!discoverExternal && !wasBound)
            return default;

        // The UI asks for run state on every projection. When the identity Exo
        // launched still matches, that is already a running verified process —
        // enumerating every process on the machine cannot change the answer.
        if (MatchesLaunchedIdentity(game))
            return new GameRunState(true, true);

        var candidates = FindCandidates(game);
        // Keep the replacement identity after a bootstrap exits. Otherwise the
        // next ordinary projection sees no binding and hides Stop again.
        if (candidates.Count > 0) _launched[game.Id] = candidates[0];
        return Report(game.Id, wasBound, new GameRunState(candidates.Count > 0, candidates.Count > 0));
    }

    /// <summary>
    /// One short-lived process snapshot for a library observation batch. With
    /// <paramref name="underRootOnly"/> a Steam game also needs its process inside
    /// its install folder (no Steam process-log lookups), which keeps a frequent
    /// poll down to path comparisons.
    /// </summary>
    internal IReadOnlyDictionary<string, GameRunState> GetStates(IReadOnlyList<GameEntry> games, bool underRootOnly = false)
    {
        var states = new Dictionary<string, GameRunState>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<ProcessObservation>? snapshot = null;
        foreach (var game in games)
        {
            states[game.Id] = default;
            if (!CanInspect(game)) continue;
            var wasBound = _launched.ContainsKey(game.Id);
            if (MatchesLaunchedIdentity(game)) { states[game.Id] = new(true, true); continue; }
            snapshot ??= _snapshot();
            // Only Steam can vouch for an image outside the install root;
            // every other store needs the prefix, so skip the full check
            // (and its per-call Directory.Exists) for unrelated processes.
            // The root is normalized once per game, each image once per
            // process: the same test as IsPathUnderRoot, which normalized
            // both for every (game, process) pair on every poll.
            var needsRoot = underRootOnly || game.Store != StoreKind.Steam;
            var root = needsRoot ? ProcessHelper.RootPrefix(game.Path) : null;
            foreach (var observation in snapshot)
            {
                if (needsRoot && (root is null || observation.FullPath is not { } image ||
                        !image.StartsWith(root, StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (!TryReadEligibleIdentity(game, observation, out var identity)) continue;
                _launched[game.Id] = identity;
                states[game.Id] = new(true, true);
                break;
            }
            Report(game.Id, wasBound, states[game.Id]);
        }
        return states;
    }

    // Only a change of binding is news; a rebind to a replacement process is not.
    private GameRunState Report(string gameId, bool wasBound, GameRunState state)
    {
        if (wasBound == state.IsRunning) return state;
        try { StateChanged?.Invoke(gameId, state); }
        catch { /* observers must not break process inspection */ }
        return state;
    }

    /// <summary>
    /// What each process was when first seen, by id: a process's image and start
    /// time never change, so a frequent snapshot opens only processes that are new
    /// since the last one. A reused id with another name is read again, and
    /// identities are verified again before Exo acts on them.
    /// </summary>
    private readonly Dictionary<int, ProcessObservation> _seen = new();
    private readonly Lock _seenGate = new();

    private IReadOnlyList<ProcessObservation> CaptureSnapshot()
    {
        var found = new List<ProcessObservation>();
        var live = new HashSet<int>();
        using (_seenGate.EnterScope())
        {
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        var name = process.ProcessName;
                        live.Add(process.Id);
                        if (IsReservedProcessName(name)) continue;
                        if (!_seen.TryGetValue(process.Id, out var known) || !string.Equals(known.Name, name, StringComparison.Ordinal))
                        {
                            if (process.HasExited) continue;
                            known = new(process.Id, name, _readExecutablePath(process), TryReadStartUtcTicks(process));
                            _seen[process.Id] = known;
                        }
                        found.Add(known);
                    }
                    catch { /* Closing and protected processes may not be readable. */ }
                }
            }
            foreach (var gone in _seen.Keys.Where(id => !live.Contains(id)).ToList()) _seen.Remove(gone);
        }
        return found;
    }

    private bool MatchesLaunchedIdentity(GameEntry game)
    {
        if (!_launched.TryGetValue(game.Id, out var launched)) return false;
        try
        {
            using var process = Process.GetProcessById(launched.ProcessId);
            if (MatchesIdentity(game, process, launched)) return true;
        }
        catch { /* pid reuse, exit, or denied inspection falls through to a scan */ }

        _launched.TryRemove(game.Id, out _);
        return false;
    }

    public void ObserveLaunch(GameEntry game, int? processId)
    {
        if (processId is not int pid || pid <= 0) return;
        if (!TryReadEligibleIdentity(game, pid, out var identity)) return;
        var wasBound = _launched.ContainsKey(game.Id);
        _launched[game.Id] = identity;
        Report(game.Id, wasBound, new GameRunState(true, true));
    }

    public async Task<GameStopResult> StopAsync(GameEntry game, CancellationToken cancellationToken = default)
    {
        var candidates = FindCandidates(game);
        if (candidates.Count == 0)
            return new GameStopResult(false, "Exo could not find this game running.", NothingRunning: true);

        // The close request is graceful first. This reaches windows which do not
        // expose a MainWindowHandle without touching children or store clients.
        ProcessHelper.RequestCloseProcesses(candidates.Select(candidate => candidate.ProcessId));
        var graceDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTimeOffset.UtcNow < graceDeadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FindStillValid(game, candidates).Count == 0 && FindCandidates(game).Count == 0)
            {
                _launched.TryRemove(game.Id, out _);
                Report(game.Id, wasBound: true, default);
                return new GameStopResult(true, "Game closed.");
            }
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        }

        // Force-stop verified identities and their non-reserved helpers.
        // Never expand Kill to an unfiltered process tree — that bypasses the
        // deny-list for Easy Anti-Cheat / BattlEye / Vanguard children.
        var stubborn = FindStillValid(game, candidates);
        foreach (var candidate in stubborn)
        {
            try
            {
                using var process = Process.GetProcessById(candidate.ProcessId);
                if (!MatchesIdentity(game, process, candidate)) continue;
                if (IsReservedProcessName(process.ProcessName)) continue;
                if (candidate.SteamTracked) ProcessTermination.Kill(process, entireProcessTree: false);
                else ProcessHelper.KillVerifiedGameTree(process, IsReservedProcessName, game.Path);
            }
            catch { /* process may have exited or rejected a non-elevated close */ }
        }

        // Rescan once more — a child may have re-parented; still only under install root.
        var remaining = FindCandidates(game);
        foreach (var candidate in remaining)
        {
            try
            {
                using var process = Process.GetProcessById(candidate.ProcessId);
                if (!MatchesIdentity(game, process, candidate)) continue;
                if (IsReservedProcessName(process.ProcessName)) continue;
                if (candidate.SteamTracked) ProcessTermination.Kill(process, entireProcessTree: false);
                else ProcessHelper.KillVerifiedGameTree(process, IsReservedProcessName, game.Path);
            }
            catch { /* */ }
        }

        var forceDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(4);
        while (DateTimeOffset.UtcNow < forceDeadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FindCandidates(game).Count == 0)
            {
                _launched.TryRemove(game.Id, out _);
                Report(game.Id, wasBound: true, default);
                return new GameStopResult(true, "Game closed.");
            }
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        }
        return new GameStopResult(false, "Could not close the game.");
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

    private IReadOnlyList<ProcessIdentity> FindStillValid(
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
        SupportsGameProcessControl(game.Store) &&
        game.Installed &&
        !string.Equals(game.Id, "local:add", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(game.Path) &&
        Directory.Exists(game.Path) &&
        (game.Store != StoreKind.Local || LocalAdapter.IsSafePortableRegistrationRoot(game.Path)) &&
        // League's persistent client is not an in-game session. The explicit
        // process-name allow-list below makes Stop appear only after a match has
        // actually launched.
        !(game.Store == StoreKind.Riot && string.IsNullOrWhiteSpace(game.LaunchTarget));

    private bool TryReadEligibleIdentity(GameEntry game, int processId, out ProcessIdentity identity)
    {
        identity = default;
        try
        {
            using var process = Process.GetProcessById(processId);
            return TryReadEligibleIdentity(game, process, out identity, byId: true);
        }
        catch { return false; }
    }

    /// <param name="byId">
    /// The process came from <see cref="Process.GetProcessById(int)"/>, whose
    /// ProcessName walks the whole process table; its name is read from the
    /// image instead. One from <see cref="Process.GetProcesses()"/> already
    /// carries its name.
    /// </param>
    private bool TryReadEligibleIdentity(GameEntry game, Process process, out ProcessIdentity identity, bool byId = false)
    {
        identity = default;
        try
        {
            if (!CanInspect(game) || process.HasExited)
                return false;
        }
        catch { return false; }

        var name = byId ? ProcessHelper.TryReadProcessName(process.Id) : process.ProcessName;
        if (name is null) return false;
        if (IsReservedProcessName(name)) return false;
        return TryReadEligibleIdentity(game, new ProcessObservation(process.Id, name,
            _readExecutablePath(process), TryReadStartUtcTicks(process)), out identity);
    }

    private bool TryReadEligibleIdentity(GameEntry game, ProcessObservation observation, out ProcessIdentity identity)
    {
        identity = default;
        var name = observation.Name;
        if (IsReservedProcessName(name)) return false;
        var executable = observation.ExecutablePath;
        var startedTicks = observation.StartedUtcTicks;
        // Some Steam games unpack a protected runtime outside their original
        // install folder (for example Ubisoft's per-user Rainbow Six runtime).
        // Steam's exact app/PID/start-time handoff proves this association;
        // preserve the real image path for subsequent identity checks.
        var steamTracked = !IsEligibleExecutableForStop(game, name, executable);
        if (steamTracked)
        {
            var installedImage = _steamProcesses.Resolve(game, observation.ProcessId, name, startedTicks);
            if (installedImage is null) return false;
            executable = string.IsNullOrWhiteSpace(executable) ? installedImage : executable;
        }

        // IsEligibleExecutableForStop rejects null/empty paths above; keep the
        // invariant explicit for nullable flow analysis and future callers.
        if (string.IsNullOrWhiteSpace(executable))
            return false;

        // StartTime is often access-denied for elevated/protected game processes.
        // Never drop a valid image path just because start-time is unreadable —
        // that used to make Stop/canStop miss live sessions entirely.
        identity = new ProcessIdentity(observation.ProcessId, startedTicks, Path.GetFullPath(executable), steamTracked);
        return true;
    }

    private static long TryReadStartUtcTicks(Process process)
    {
        try { return process.StartTime.ToUniversalTime().Ticks; }
        catch { return 0; }
    }

    private bool MatchesIdentity(GameEntry game, Process process, ProcessIdentity expected)
    {
        try
        {
            if (process.HasExited || process.Id != expected.ProcessId) return false;
        }
        catch { return false; }
        // Every caller opened the process by id.
        if (!TryReadEligibleIdentity(game, process, out var actual, byId: true)) return false;
        if (!string.Equals(actual.ExecutablePath, expected.ExecutablePath, StringComparison.OrdinalIgnoreCase))
            return false;
        // When either side could not read start-time, path + PID is enough —
        // refusing on 0 vs real ticks left Stop unable to force-close live games.
        if (expected.StartedUtcTicks == 0 || actual.StartedUtcTicks == 0)
            return true;
        return actual.StartedUtcTicks == expected.StartedUtcTicks;
    }

    private readonly record struct ProcessIdentity(int ProcessId, long StartedUtcTicks, string ExecutablePath, bool SteamTracked);
}

internal readonly record struct GameRunState(bool IsRunning, bool CanStop);
/// <param name="NothingRunning">No process of the game was found, so nothing was left to close.</param>
internal readonly record struct GameStopResult(bool Ok, string Message, bool NothingRunning = false);
