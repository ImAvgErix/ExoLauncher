using ExoLauncher.Models;

namespace ExoLauncher.Adapters;

/// <summary>
/// Shared store surface. Prefer shelling out to mature open backends
/// (Legendary and gogdl) over re-implementing store protocols.
/// No adapter edits game binaries or anti-cheat.
/// </summary>
public interface IStoreAdapter
{
    StoreKind Store { get; }
    string Id { get; }
    string DisplayName { get; }

    /// <summary>Whether the preferred backend (Legendary / gogdl / official client) is on disk.</summary>
    bool IsAgentPresent();

    /// <summary>Optional auth. Most backends use their own CLI login; may open a browser.</summary>
    Task<AuthResult> AuthenticateAsync(CancellationToken ct = default);

    /// <summary>Owned titles, installed or not (when the backend can report ownership).</summary>
    Task<IReadOnlyList<GameEntry>> GetLibraryAsync(CancellationToken ct = default);

    Task<InstallResult> InstallAsync(
        GameEntry game,
        string? installPath,
        IProgress<InstallProgress>? progress,
        CancellationToken ct = default);

    Task<InstallResult> UpdateAsync(
        GameEntry game,
        IProgress<InstallProgress>? progress,
        CancellationToken ct = default);

    Task<LaunchResult> LaunchAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default);

    Task<InstallResult> UninstallAsync(GameEntry game, CancellationToken ct = default);

    /// <summary>Snapshot of in-flight download when the adapter tracks one; otherwise idle.</summary>
    InstallProgress GetDownloadProgress(string gameId);

    /// <summary>Optional: hide or soft-close store UI after install/exit. Never kill anti-cheat.</summary>
    Task CleanupAfterExitAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default);
}

/// <summary>
/// Marks an adapter whose installed Steam entries come from local appmanifests.
/// Remote catalog/search providers must not implement this proof boundary.
/// </summary>
public interface IInstalledSteamManifestSource
{
}

/// <summary>
/// Separates a vendor's visible desktop client from Exo's headless backend.
/// A bundled helper can make installs possible without meaning the vendor
/// client itself is installed or can be opened from Settings.
/// </summary>
public interface IStoreClientPresence
{
    bool IsClientPresent();
}

/// <summary>
/// Optional account boundary for adapters whose on-disk catalog, playtime, or
/// entitlement data is tied to the currently signed-in vendor user. The value
/// must be an opaque one-way tag: never a display name, account id, token, or
/// other bridge-visible identity.
/// </summary>
public interface IStoreAccountScope
{
    /// <summary>Opaque active-account tag, or null when account authority cannot be proven.</summary>
    string? GetActiveAccountScope();
}

/// <summary>
/// Optional store capability: a current, authoritative entitlement snapshot.
/// Null means the store could not verify ownership and historical local proof
/// must remain an offline-safe fallback.
/// </summary>
public interface IAuthoritativeOwnershipSource
{
    IReadOnlySet<string>? LastAuthoritativeOwnedAppIds { get; }
}

/// <summary>
/// An official desktop client that Exo can safely reveal on request. This is a
/// presence/open contract only; it does not imply that Exo can read its library
/// or drive installs, updates, achievements, or title launches.
/// </summary>
public interface IOfficialStoreClient : IStoreClientPresence
{
    IReadOnlyList<string> ClientProcessNames { get; }
    StoreClientLaunchCommand? GetClientLaunchCommand();
}

/// <summary>
/// Optional file verify/repair for backends that already own that job
/// (Steam validate, Legendary --repair, gogdl repair). Never used for Riot.
/// </summary>
public interface IStoreRepair
{
    bool CanRepair(GameEntry game);
    Task<InstallResult> RepairAsync(
        GameEntry game,
        IProgress<InstallProgress>? progress,
        CancellationToken ct = default);
}

/// <summary>Legacy alias used during discovery scans.</summary>
public static class StoreAdapterExtensions
{
    public static Task<IReadOnlyList<GameEntry>> DiscoverAsync(this IStoreAdapter adapter, CancellationToken ct = default)
        => adapter.GetLibraryAsync(ct);
}

public sealed class LaunchOptions
{
    public bool CloseStoreUiAfterExit { get; init; } = true;
    public bool MinimizeStoreUi { get; init; } = true;
    public bool AntiCheatSafeMode { get; init; } = true;
    public string? ExtraArgs { get; init; }
    public string? WorkingDirectory { get; init; }
    public bool RunAsAdmin { get; init; }
}

public sealed class AuthResult
{
    public bool Ok { get; init; }
    public string Message { get; init; } = string.Empty;
    public bool RequiresUserAction { get; init; }
}
