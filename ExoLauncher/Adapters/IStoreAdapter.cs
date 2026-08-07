using ExoLauncher.Models;

namespace ExoLauncher.Adapters;

/// <summary>
/// Store adapter contract. Phase 1 ships working shapes for Local/Steam/Epic/Riot
/// and compile-ready stubs for the rest. No adapter edits game binaries or anti-cheat.
/// </summary>
public interface IStoreAdapter
{
    StoreKind Store { get; }
    string DisplayName { get; }

    /// <summary>Whether the store agent/client is present on disk (not necessarily running).</summary>
    bool IsAgentPresent();

    /// <summary>Discover installed (and optionally owned) titles for this store.</summary>
    Task<IReadOnlyList<GameEntry>> DiscoverAsync(CancellationToken ct = default);

    /// <summary>
    /// Prepare backend if needed (start minimized), then launch the title.
    /// Must not bypass DRM or anti-cheat.
    /// </summary>
    Task<LaunchResult> LaunchAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default);

    /// <summary>Optional: hide or close store UI after the game exits.</summary>
    Task CleanupAfterExitAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default);
}

public sealed class LaunchOptions
{
    public bool CloseStoreUiAfterExit { get; init; } = true;
    public bool MinimizeStoreUi { get; init; } = true;
    public bool AntiCheatSafeMode { get; init; } = true;
}
