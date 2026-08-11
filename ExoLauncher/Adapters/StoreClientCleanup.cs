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
/// exit cleanly. Exo never force-kills a store client: it may own an unrelated
/// download/update that must be allowed to pause or finish safely.
/// </summary>
internal static class StoreClientCleanup
{
    internal static readonly TimeSpan GracefulExitTimeout = TimeSpan.FromSeconds(4);

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

    private static readonly StoreCleanupTarget[] Targets =
    [
        new(StoreKind.Steam, SteamExitProcessNames),
        new(StoreKind.Epic, EpicExitProcessNames),
        new(StoreKind.Gog, GogExitProcessNames),
        new(StoreKind.Riot, RiotExitProcessNames),
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
                    var names = target.Store switch
                    {
                        StoreKind.Steam => StoreWindowHider.SteamProcessNames,
                        StoreKind.Epic => StoreWindowHider.EpicProcessNames,
                        StoreKind.Gog => StoreWindowHider.GalaxyProcessNames,
                        StoreKind.Riot => StoreWindowHider.RiotUiProcessNames,
                        _ => [],
                    };
                    if (names.Length > 0)
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
    /// Ask every unused launcher to exit and wait a fixed short interval. Any
    /// client that remains is left hidden and running; a sibling store may own
    /// a download/update Exo did not start and is not safe to kill.
    /// </summary>
    public static Task<StoreCleanupReport> ExitUnusedAsync(
        StoreKind activeProvider,
        CancellationToken cancellationToken = default) =>
        ExitUnusedAsync(
            activeProvider,
            SystemController,
            GracefulExitTimeout,
            cancellationToken);

    internal static async Task<StoreCleanupReport> ExitUnusedAsync(
        StoreKind activeProvider,
        IStoreClientProcessController controller,
        TimeSpan gracefulExitTimeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(controller);
        var candidates = TargetsFor(activeProvider)
            .Where(target => target.ExactProcessNames.Any(controller.IsRunning))
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

        await DelayIfPositiveAsync(gracefulExitTimeout, cancellationToken).ConfigureAwait(false);

        var remainingStores = candidates.Count(target =>
            target.ExactProcessNames.Any(controller.IsRunning));
        return new StoreCleanupReport(gracefulRequests, remainingStores);
    }

    private static Task DelayIfPositiveAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        delay > TimeSpan.Zero
            ? Task.Delay(delay, cancellationToken)
            : Task.CompletedTask;

    private sealed class SystemStoreClientProcessController : IStoreClientProcessController
    {
        public bool IsRunning(string exactProcessName) =>
            ProcessHelper.IsProcessRunning(exactProcessName);

        public void RequestGracefulExit(StoreCleanupTarget target)
        {
            if (target.Store == StoreKind.Steam)
            {
                var executable = SteamAdapter.TryResolveSteamExePublic();
                if (ProcessHelper.IsProcessRunning("steam") && executable is not null)
                {
                    using var shutdown = ProcessHelper.StartHiddenCli(executable, "-shutdown");
                    return;
                }
            }

            ProcessHelper.TryCloseProcesses(target.ExactProcessNames.ToArray());
        }
    }
}
