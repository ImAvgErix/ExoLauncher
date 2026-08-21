using ExoLauncher.Adapters.Cli;
using ExoLauncher.Models;

namespace ExoLauncher.Adapters;

internal sealed record StoreCleanupTarget(
    StoreKind Store,
    IReadOnlyList<string> ExactProcessNames);

internal sealed record StoreCleanupReport(
    int GracefulStoreRequests,
    int RemainingStoreClients);

internal interface IStoreClientProcessController
{
    bool IsRunning(string exactProcessName);
    void RequestGracefulExit(StoreCleanupTarget target);
}

/// <summary>
    /// Hides launcher shells that the active game does not need and asks them to
    /// exit cleanly. A client that ignores the request is left running; Exo
    /// never force-terminates a vendor client it did not start.
    /// </summary>
internal static class StoreClientCleanup
{
    internal static readonly TimeSpan GracefulExitTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan ExitPollInterval = TimeSpan.FromMilliseconds(150);

    internal static readonly string[] SteamExitProcessNames =
    [
        "steam", "steamwebhelper",
    ];

    internal static readonly string[] EpicExitProcessNames =
    [
        "EpicGamesLauncher", "EpicWebHelper",
    ];

    internal static readonly string[] GogExitProcessNames =
    [
        "GalaxyClient", "GOG Galaxy Notifications",
    ];

    /// <summary>
    /// Riot launcher client processes only. RiotClientServices.exe is the
    /// per-user launcher core, not the Vanguard Windows services (vgc/vgk).
    /// League, VALORANT, Vanguard, and every anti-cheat process are excluded.
    /// </summary>
    internal static readonly string[] RiotExitProcessNames =
    [
        "Riot Client", "RiotClientServices", "RiotClientUx",
        "RiotClientUxRender", "RiotClientCrashHandler",
    ];

    internal static readonly string[] XboxExitProcessNames =
    [
        "XboxPcApp", "GamingApp",
    ];

    internal static readonly string[] EaExitProcessNames =
    [
        "EADesktop",
    ];

    internal static readonly string[] UbisoftExitProcessNames =
    [
        "UbisoftConnect", "upc", "UplayWebCore",
    ];

    internal static readonly string[] BattleNetExitProcessNames =
    [
        "Battle.net",
    ];

    internal static readonly string[] AmazonExitProcessNames =
    [
        "Amazon Games", "AmazonGames", "AmazonGamesUI",
    ];

    /// <summary>
    /// Rockstar's main executable is named Launcher.exe. Exit requests must be
    /// path-qualified to that install; never close an unrelated Launcher.
    /// </summary>
    internal static readonly string[] RockstarExitProcessNames =
    [
        "Launcher", "LauncherPatcher",
    ];

    internal static readonly string[] ItchExitProcessNames = ["itch"];
    internal static readonly string[] MinecraftExitProcessNames = ["MinecraftLauncher"];
    internal static readonly string[] RobloxExitProcessNames = ["RobloxPlayerLauncher"];
    internal static readonly string[] ParadoxExitProcessNames = ["Paradox Launcher", "ParadoxLauncher"];
    internal static readonly string[] WargamingExitProcessNames = ["wgc"];

    private static readonly StoreCleanupTarget[] Targets =
    [
        new(StoreKind.Steam, SteamExitProcessNames),
        new(StoreKind.Epic, EpicExitProcessNames),
        new(StoreKind.Gog, GogExitProcessNames),
        new(StoreKind.Riot, RiotExitProcessNames),
        new(StoreKind.Xbox, XboxExitProcessNames),
        new(StoreKind.Ea, EaExitProcessNames),
        new(StoreKind.Ubisoft, UbisoftExitProcessNames),
        new(StoreKind.BattleNet, BattleNetExitProcessNames),
        new(StoreKind.Amazon, AmazonExitProcessNames),
        new(StoreKind.Rockstar, RockstarExitProcessNames),
        new(StoreKind.Itch, ItchExitProcessNames),
        new(StoreKind.Minecraft, MinecraftExitProcessNames),
        new(StoreKind.Roblox, RobloxExitProcessNames),
        new(StoreKind.Paradox, ParadoxExitProcessNames),
        new(StoreKind.Wargaming, WargamingExitProcessNames),
    ];

    private static readonly IStoreClientProcessController SystemController =
        new SystemStoreClientProcessController();

    internal static IReadOnlyList<StoreCleanupTarget> TargetsFor(StoreKind activeProvider) =>
        Targets.Where(target => target.Store != activeProvider).ToArray();

    /// <summary>Hide sibling launcher chrome immediately so graceful exit cannot flash it.</summary>
    public static void HideUnused(StoreKind activeProvider)
    {
        foreach (var target in TargetsFor(activeProvider))
        {
            try
            {
                _ = HiddenStoreRuntime.TryWhileGameProviderInactive(target.Store, () =>
                {
                    var names = ProcessNamesToHide(target.Store);
                    if (names.Length == 0) return;
                    if (target.Store == StoreKind.Rockstar)
                        StoreWindowHider.CollapseOrphanSurfaces(names, "Rockstar Games");
                    else
                        StoreWindowHider.CollapseOrphanSurfaces(names);
                });
            }
            catch
            {
                /* best-effort */
            }
        }
    }

    /// <summary>
    /// Ask every unused launcher to exit, waiting only as long as it takes them
    /// to go. A shell that ignores the graceful requests is left running.
    /// </summary>
    public static async Task<StoreCleanupReport> ExitUnusedAsync(
        StoreKind activeProvider,
        CancellationToken cancellationToken = default)
    {
        bool Keep(StoreKind store) =>
            StoreClientActivity.ShouldKeepRunning(store, ProcessHelper.IsProcessRunning);
        var report = await ExitUnusedAsync(
            activeProvider,
            SystemController,
            GracefulExitTimeout,
            cancellationToken,
            Keep).ConfigureAwait(false);
        QuietKeptBackend(activeProvider);
        return report;
    }

    internal static async Task<StoreCleanupReport> ExitUnusedAsync(
        StoreKind activeProvider,
        IStoreClientProcessController controller,
        TimeSpan gracefulExitTimeout,
        CancellationToken cancellationToken = default,
        Func<StoreKind, bool>? shouldKeep = null)
    {
        ArgumentNullException.ThrowIfNull(controller);
        var candidates = TargetsFor(activeProvider)
            .Where(target => target.ExactProcessNames.Any(controller.IsRunning))
            .Where(target => shouldKeep?.Invoke(target.Store) != true)
            .ToArray();
        var gracefulRequests = 0;

        foreach (var target in candidates)
        {
            try
            {
                var requested = false;
                _ = HiddenStoreRuntime.TryWhileGameProviderInactive(target.Store, () =>
                {
                    if (!target.ExactProcessNames.Any(controller.IsRunning)) return;
                    controller.RequestGracefulExit(target);
                    requested = true;
                });
                if (requested) gracefulRequests++;
            }
            catch
            {
                /* one vendor failure must not block the others */
            }
        }

        if (gracefulRequests == 0)
            return new StoreCleanupReport(0, 0);

        await WaitForExitAsync(candidates, controller, gracefulExitTimeout, cancellationToken)
            .ConfigureAwait(false);

        var remaining = candidates
            .Where(target => target.ExactProcessNames.Any(controller.IsRunning))
            .ToArray();
        if (remaining.Length == 0)
            return new StoreCleanupReport(gracefulRequests, 0);

        foreach (var target in remaining)
        {
            try
            {
                _ = HiddenStoreRuntime.TryWhileGameProviderInactive(target.Store, () =>
                {
                    if (!target.ExactProcessNames.Any(controller.IsRunning)) return;
                    controller.RequestGracefulExit(target);
                });
            }
            catch
            {
                /* one vendor failure must not block the others */
            }
        }

        await WaitForExitAsync(remaining, controller, gracefulExitTimeout, cancellationToken)
            .ConfigureAwait(false);

        var remainingStores = candidates.Count(target =>
            target.ExactProcessNames.Any(controller.IsRunning));
        return new StoreCleanupReport(gracefulRequests, remainingStores);
    }

    /// <summary>
    /// A launcher that honours WM_CLOSE is normally gone well inside a second.
    /// Sleeping each grace window whole made "close the launchers this game does
    /// not need" cost a flat eight seconds on every launch and every install.
    /// </summary>
    private static async Task WaitForExitAsync(
        IReadOnlyList<StoreCleanupTarget> targets,
        IStoreClientProcessController controller,
        TimeSpan gracefulExitTimeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + gracefulExitTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (!targets.Any(target => target.ExactProcessNames.Any(controller.IsRunning)))
                return;

            var left = deadline - DateTimeOffset.UtcNow;
            if (left <= TimeSpan.Zero) return;
            await Task.Delay(left < ExitPollInterval ? left : ExitPollInterval, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Steam as a hidden backend must not keep Friends/chat toasts. Settings →
    /// Open Steam suspends this store so the full client can come back.
    /// </summary>
    internal static void QuietKeptBackend(StoreKind keep)
    {
        if (keep != StoreKind.Steam) return;
        if (HiddenStoreRuntime.IsSuspended(StoreKind.Steam)) return;
        if (!ProcessHelper.IsProcessRunning("steam")) return;
        var executable = ResolveSteamExe();
        if (executable is null) return;
        try
        {
            ProcessHelper.StartHidden(executable, SteamUpdateCommandPlan.HiddenClientStartArguments());
        }
        catch
        {
            /* Steam may already be shutting down. */
        }
    }

    private static string[] ProcessNamesToHide(StoreKind store) => store switch
    {
        StoreKind.Steam => StoreWindowHider.SteamProcessNames,
        StoreKind.Epic => StoreWindowHider.EpicProcessNames,
        StoreKind.Gog => StoreWindowHider.GalaxyProcessNames,
        StoreKind.Riot => StoreWindowHider.RiotUiProcessNames,
        StoreKind.Xbox => StoreWindowHider.XboxClientProcessNames,
        StoreKind.Ea => StoreWindowHider.EaClientProcessNames,
        StoreKind.Ubisoft => StoreWindowHider.UbisoftClientProcessNames,
        StoreKind.BattleNet => StoreWindowHider.BattleNetClientProcessNames,
        StoreKind.Amazon => StoreWindowHider.AmazonClientProcessNames,
        StoreKind.Rockstar => StoreWindowHider.RockstarClientProcessNames,
        StoreKind.Itch => StoreWindowHider.ItchClientProcessNames,
        StoreKind.Minecraft => StoreWindowHider.MinecraftClientProcessNames,
        StoreKind.Roblox => StoreWindowHider.RobloxClientProcessNames,
        StoreKind.Paradox => StoreWindowHider.ParadoxClientProcessNames,
        StoreKind.Wargaming => StoreWindowHider.WargamingClientProcessNames,
        _ => [],
    };

    private static string? ResolveSteamExe()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            var path = key?.GetValue("SteamPath") as string;
            if (!string.IsNullOrWhiteSpace(path))
            {
                var exe = Path.Combine(path.Replace('/', Path.DirectorySeparatorChar), "steam.exe");
                if (File.Exists(exe)) return exe;
            }
        }
        catch { /* fall through */ }

        foreach (var root in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"),
        })
        {
            var exe = Path.Combine(root, "steam.exe");
            if (File.Exists(exe)) return exe;
        }

        return null;
    }

    private sealed class SystemStoreClientProcessController : IStoreClientProcessController
    {
        public bool IsRunning(string exactProcessName) =>
            ProcessHelper.IsProcessRunning(exactProcessName);

        public void RequestGracefulExit(StoreCleanupTarget target)
        {
            if (target.Store == StoreKind.Steam)
            {
                var executable = ResolveSteamExe();
                if (ProcessHelper.IsProcessRunning("steam") && executable is not null)
                    ProcessHelper.StartHiddenCli(executable, "-shutdown");
            }
            else if (target.Store == StoreKind.Riot)
            {
                Riot.RiotClientApi.TryRequestShutdown();
            }

            if (target.Store == StoreKind.Rockstar)
            {
                ProcessHelper.TryCloseProcesses(target.ExactProcessNames.ToArray(), "Rockstar Games");
                return;
            }

            ProcessHelper.TryCloseProcesses(target.ExactProcessNames.ToArray());
        }
    }
}
