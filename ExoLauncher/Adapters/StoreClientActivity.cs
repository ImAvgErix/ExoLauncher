using ExoLauncher.Models;

namespace ExoLauncher.Adapters;

/// <summary>
/// When Exo closes unused store clients it must leave a client running if that
/// client is downloading, hosting a game, or currently shown because the user
/// opened it from Settings. Anti-cheat is never a candidate.
/// </summary>
internal static class StoreClientActivity
{
    /// <summary>
    /// Exact names that must never appear on a kill/exit list and must never
    /// pass <see cref="ProcessHelper.TerminateExactNames"/>.
    /// </summary>
    internal static readonly string[] AntiCheatProcessNames =
    [
        "vgk", "vgc", "vgm", "Vanguard",
        "EasyAntiCheat", "EasyAntiCheat_EOS", "EasyAntiCheat_EOS_Setup", "EAC_Launcher",
        "BEService", "BEService_x64", "BattlEye", "BattlEye_Launcher",
        "start_protected_game", "start_protected_game64",
    ];

    internal static readonly string[] SteamHostedGameProcessNames =
    [
        "GameOverlayUI", "GameOverlayUI64",
    ];

    internal static readonly string[] RiotHostedGameProcessNames =
    [
        "VALORANT-Win64-Shipping", "VALORANT",
        "League of Legends", "LeagueClient", "LeagueClientUx",
        "LoR", "LegendsofRuneterra",
    ];

    internal static readonly string[] EpicHostedGameProcessNames =
    [
        "FortniteClient-Win64-Shipping", "FortniteLauncher",
    ];

    internal static readonly string[] RobloxHostedGameProcessNames =
    [
        "RobloxPlayerBeta",
    ];

    internal readonly record struct KeepSignals(
        bool GameProviderActive,
        bool Suspended,
        bool Transferring,
        bool HostingGame);

    public static bool IsAntiCheatProcess(string? processName) =>
        !string.IsNullOrWhiteSpace(processName) &&
        AntiCheatProcessNames.Any(name =>
            string.Equals(name, processName, StringComparison.OrdinalIgnoreCase));

    public static bool ShouldKeep(KeepSignals signals) =>
        signals.GameProviderActive ||
        signals.Suspended ||
        signals.Transferring ||
        signals.HostingGame;

    public static KeepSignals Evaluate(
        StoreKind store,
        Func<string, bool> isRunning,
        bool gameProviderActive = false,
        bool suspended = false,
        bool transferring = false)
    {
        ArgumentNullException.ThrowIfNull(isRunning);
        return new KeepSignals(
            gameProviderActive,
            suspended,
            transferring || IsKnownTransferProcessRunning(store, isRunning),
            IsHostingGame(store, isRunning));
    }

    public static KeepSignals Probe(StoreKind store, Func<string, bool> isRunning) =>
        Evaluate(
            store,
            isRunning,
            HiddenStoreRuntime.IsGameProviderActive(store),
            HiddenStoreRuntime.IsSuspended(store),
            store == StoreKind.Steam && SteamContentLogProgress.AnyDownloadingFolder());

    public static bool ShouldKeepRunning(StoreKind store, Func<string, bool> isRunning) =>
        ShouldKeep(Probe(store, isRunning));

    internal static bool IsKnownTransferProcessRunning(StoreKind store, Func<string, bool> isRunning) =>
        store switch
        {
            StoreKind.Epic => isRunning("legendary"),
            StoreKind.Gog => isRunning("gogdl"),
            StoreKind.Amazon => isRunning("nile"),
            _ => false,
        };

    internal static bool IsHostingGame(StoreKind store, Func<string, bool> isRunning)
    {
        var names = HostedGameProcessNames(store);
        return names.Length > 0 && names.Any(isRunning);
    }

    internal static string[] HostedGameProcessNames(StoreKind store) => store switch
    {
        StoreKind.Steam => SteamHostedGameProcessNames,
        StoreKind.Riot => RiotHostedGameProcessNames,
        StoreKind.Epic => EpicHostedGameProcessNames,
        StoreKind.Roblox => RobloxHostedGameProcessNames,
        _ => [],
    };
}
