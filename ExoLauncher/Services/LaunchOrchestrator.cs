using ExoLauncher.Adapters;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>
/// detect ownership → resolve deps → install/update with progress →
/// launch backend minimized → launch game → optional store-UI cleanup.
/// </summary>
public sealed class LaunchOrchestrator
{
    private readonly IReadOnlyList<IStoreAdapter> _adapters;
    private readonly SettingsService _settings;
    private readonly DependencyService _deps;
    private readonly object _gate = new();
    private CancellationTokenSource? _jobCts;
    private string? _activeGameId;
    private InstallProgress _lastProgress = new();

    public event Action<InstallProgress>? ProgressChanged;

    public LaunchOrchestrator(
        IReadOnlyList<IStoreAdapter> adapters,
        SettingsService settings,
        DependencyService deps)
    {
        _adapters = adapters;
        _settings = settings;
        _deps = deps;
    }

    public InstallProgress CurrentProgress
    {
        get
        {
            lock (_gate) return _lastProgress;
        }
    }

    public bool IsBusy
    {
        get
        {
            lock (_gate) return _jobCts is not null && !_jobCts.IsCancellationRequested && _lastProgress.IsActive;
        }
    }

    public async Task<LaunchResult> LaunchAsync(GameEntry game, CancellationToken ct = default)
    {
        var adapter = Find(game.Store);
        if (adapter is null)
            return new LaunchResult { Ok = false, Message = $"No adapter for store {game.Store}." };

        if (game.Id.StartsWith("mock:", StringComparison.OrdinalIgnoreCase))
        {
            return new LaunchResult
            {
                Ok = false,
                Message = "This is a demo library entry. Install the real title, then refresh.",
            };
        }

        var options = BuildOptions();
        try
        {
            var result = await adapter.LaunchAsync(game, options, ct).ConfigureAwait(false);
            return result;
        }
        catch (Exception ex)
        {
            return new LaunchResult { Ok = false, Message = ex.Message };
        }
    }

    public async Task<InstallResult> InstallAsync(GameEntry game, string? path = null, CancellationToken outer = default)
    {
        return await RunJobAsync(game, async (adapter, progress, ct) =>
        {
            // Consent is the UI click. Auto-install redistributables is ask-first:
            // we only surface missing deps; we do not silent-force.
            _ = _deps.GetMissingRequired(game);
            return await adapter.InstallAsync(game, path, progress, ct).ConfigureAwait(false);
        }, outer).ConfigureAwait(false);
    }

    public async Task<InstallResult> UpdateAsync(GameEntry game, CancellationToken outer = default)
    {
        return await RunJobAsync(game, async (adapter, progress, ct) =>
            await adapter.UpdateAsync(game, progress, ct).ConfigureAwait(false), outer).ConfigureAwait(false);
    }

    public object Cancel()
    {
        lock (_gate)
        {
            if (_jobCts is null)
                return new { ok = false, message = "Nothing is running." };
            try { _jobCts.Cancel(); } catch { }
            _lastProgress = new InstallProgress
            {
                GameId = _activeGameId ?? string.Empty,
                Phase = InstallPhase.Cancelled,
                Status = "Cancelling…",
                CanCancel = false,
            };
        }
        ProgressChanged?.Invoke(_lastProgress);
        return new { ok = true, message = "Cancel requested." };
    }

    public InstallProgress GetProgress(string? gameId = null)
    {
        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(gameId) &&
                !string.Equals(_activeGameId, gameId, StringComparison.OrdinalIgnoreCase) &&
                FindByGameId(gameId) is { } adapter)
            {
                return adapter.GetDownloadProgress(gameId);
            }
            return _lastProgress;
        }
    }

    public async Task CleanupAfterExitAsync(GameEntry game)
    {
        var adapter = Find(game.Store);
        if (adapter is null) return;
        await adapter.CleanupAfterExitAsync(game, BuildOptions()).ConfigureAwait(false);
    }

    private async Task<InstallResult> RunJobAsync(
        GameEntry game,
        Func<IStoreAdapter, IProgress<InstallProgress>, CancellationToken, Task<InstallResult>> work,
        CancellationToken outer)
    {
        var adapter = Find(game.Store);
        if (adapter is null)
            return new InstallResult { Ok = false, Message = $"No adapter for store {game.Store}." };

        if (game.Id.StartsWith("mock:", StringComparison.OrdinalIgnoreCase))
        {
            return new InstallResult
            {
                Ok = false,
                Message = "Demo entry — install uses the real store backend when the title is discovered.",
            };
        }

        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_jobCts is not null && !_jobCts.IsCancellationRequested && _lastProgress.IsActive)
                return new InstallResult { Ok = false, Message = "Another install/update is already running." };

            _jobCts?.Dispose();
            _jobCts = CancellationTokenSource.CreateLinkedTokenSource(outer);
            cts = _jobCts;
            _activeGameId = game.Id;
            _lastProgress = new InstallProgress
            {
                GameId = game.Id,
                Phase = InstallPhase.Preparing,
                Percent = 0,
                Status = "Starting…",
                CanCancel = true,
            };
        }
        ProgressChanged?.Invoke(_lastProgress);

        var progress = new Progress<InstallProgress>(p =>
        {
            lock (_gate) _lastProgress = p;
            ProgressChanged?.Invoke(p);
        });

        try
        {
            var result = await work(adapter, progress, cts.Token).ConfigureAwait(false);
            lock (_gate)
            {
                if (!_lastProgress.IsActive && _lastProgress.Phase is not InstallPhase.Failed and not InstallPhase.Cancelled)
                {
                    _lastProgress = new InstallProgress
                    {
                        GameId = game.Id,
                        Phase = result.Ok ? InstallPhase.Completed : InstallPhase.Failed,
                        Percent = result.Ok ? 100 : _lastProgress.Percent,
                        Status = result.Message,
                        CanCancel = false,
                    };
                }
            }
            ProgressChanged?.Invoke(_lastProgress);
            return result;
        }
        catch (OperationCanceledException)
        {
            var p = new InstallProgress
            {
                GameId = game.Id,
                Phase = InstallPhase.Cancelled,
                Status = "Cancelled.",
                CanCancel = false,
            };
            lock (_gate) _lastProgress = p;
            ProgressChanged?.Invoke(p);
            return new InstallResult { Ok = false, Message = "Cancelled." };
        }
        finally
        {
            lock (_gate)
            {
                _jobCts?.Dispose();
                _jobCts = null;
            }
        }
    }

    private LaunchOptions BuildOptions() => new()
    {
        CloseStoreUiAfterExit = _settings.Current.CloseStoreClientsAfterLaunch,
        MinimizeStoreUi = true,
        AntiCheatSafeMode = true,
    };

    private IStoreAdapter? Find(StoreKind store) =>
        _adapters.FirstOrDefault(a => a.Store == store);

    private IStoreAdapter? FindByGameId(string gameId)
    {
        var prefix = gameId.Split(':')[0];
        return _adapters.FirstOrDefault(a =>
            string.Equals(a.Id, prefix, StringComparison.OrdinalIgnoreCase));
    }
}
