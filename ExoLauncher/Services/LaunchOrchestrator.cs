using ExoLauncher.Adapters;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>
/// detect ownership → check deps → start required backend minimized → launch game
/// → optional cleanup of store UI after exit.
/// </summary>
public sealed class LaunchOrchestrator
{
    private readonly IReadOnlyList<IStoreAdapter> _adapters;
    private readonly SettingsService _settings;
    private readonly DependencyService _deps;

    public LaunchOrchestrator(
        IReadOnlyList<IStoreAdapter> adapters,
        SettingsService settings,
        DependencyService deps)
    {
        _adapters = adapters;
        _settings = settings;
        _deps = deps;
    }

    public async Task<LaunchResult> LaunchAsync(GameEntry game, CancellationToken ct = default)
    {
        var adapter = _adapters.FirstOrDefault(a => a.Store == game.Store);
        if (adapter is null)
            return new LaunchResult { Ok = false, Message = $"No adapter for store {game.Store}." };

        // Mock / demo titles never hit real processes.
        if (game.Id.StartsWith("mock:", StringComparison.OrdinalIgnoreCase))
        {
            return new LaunchResult
            {
                Ok = false,
                Message = "This is a demo library entry. Install the real title, then refresh.",
            };
        }

        // Anti-cheat safe mode is always on.
        var options = new LaunchOptions
        {
            CloseStoreUiAfterExit = _settings.Current.CloseStoreClientsAfterLaunch,
            MinimizeStoreUi = true,
            AntiCheatSafeMode = true,
        };

        // Dependency awareness (report, never silent-force).
        var missing = _deps.GetMissingRequired(game);
        if (missing.Count > 0 && !_settings.Current.AutoInstallRedistributables)
        {
            // Still allow launch — deps panel is where consent lives.
            // Launch path does not auto-install.
        }

        try
        {
            var result = await adapter.LaunchAsync(game, options, ct).ConfigureAwait(false);
            if (result.Ok)
            {
                // Fire-and-forget cleanup watcher is phase 2; for now soft cleanup is manual/settings-driven.
            }
            return result;
        }
        catch (Exception ex)
        {
            return new LaunchResult { Ok = false, Message = ex.Message };
        }
    }
}
