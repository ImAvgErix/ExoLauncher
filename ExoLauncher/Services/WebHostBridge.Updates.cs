
namespace ExoLauncher.Services;

public sealed partial class WebHostBridge
{
    private string? _updateProblem;
    private bool _updateProblemRead;

    /// <summary>How the update before this start ended, when it didn't take: said beside the same version being offered again.</summary>
    private string? LastUpdateProblem()
    {
        if (_updateProblemRead) return _updateProblem;
        _updateProblemRead = true;
        var root = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
        return _updateProblem = root is null ? null : UpdateOutcome.Take(root);
    }

    /// <param name="manual">Settings' "Check for update": bypasses the release cache.</param>
    private async Task<object> CheckUpdateAsync(bool manual, CancellationToken ct)
    {
        // Update checks are always on.
        var check = await _services.Updater.CheckAsync(_services.AppVersion, ct, fresh: manual).ConfigureAwait(false);
        return new ExoUpdateResponse
        {
            Ok = check.UpdateAvailable || check.AlreadyLatest,
            AlreadyLatest = check.AlreadyLatest,
            UpdateAvailable = check.UpdateAvailable,
            Latest = check.RemoteVersion,
            Current = check.LocalVersion,
            Message = check.UpdateAvailable && LastUpdateProblem() is { } problem ? problem + " " + check.Message : check.Message,
            Upcoming = check.Upcoming,
            // In-app only — no browser URL for install.
            InApp = true,
        };
    }

    private async Task<object> InstallUpdateAsync(CancellationToken ct)
    {
        void Push(string status, double? percent) =>
            PostEvent("app.updateProgress", new { status, percent });

        var progress = new Progress<(string status, double? percent)>(p => Push(p.status, p.percent));
        var result = await _services.Updater
            .InstallAsync(_services.AppVersion, progress, ct)
            .ConfigureAwait(true);

        if (result.ShouldExit)
        {
            Push("Restarting…", null);
            // Let the normal Window.Closed path flush active playtime,
            // settings, sync state, tray resources, and bridge services before
            // the installer swaps the application directory.
            _ = Task.Run(async () =>
            {
                try { await Task.Delay(350).ConfigureAwait(false); } catch { /* */ }
                _queue.TryEnqueue(() =>
                {
                    try { App.MainAppWindow?.CloseWindow(); } catch { /* installer will time out safely */ }
                    try { Microsoft.UI.Xaml.Application.Current?.Exit(); } catch { /* installer will time out safely */ }
                });
            });
        }

        return new ExoUpdateResponse
        {
            Ok = result.Installed || result.AlreadyLatest,
            UpdateAvailable = result.UpdateAvailable,
            AlreadyLatest = result.AlreadyLatest,
            Installed = result.Installed,
            ShouldExit = result.ShouldExit,
            Latest = result.RemoteVersion,
            Current = result.LocalVersion,
            Message = result.Message,
        };
    }

}
