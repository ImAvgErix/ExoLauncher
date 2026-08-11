using ExoLauncher.Models;

namespace ExoLauncher.Adapters;

/// <summary>
/// Shared store surface. Prefer shelling out to mature open backends
/// (Legendary, gogdl, Nile) over re-implementing store protocols.
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
}

public sealed class AuthResult
{
    public bool Ok { get; init; }
    public string Message { get; init; } = string.Empty;
    public bool RequiresUserAction { get; init; }
}
