using ExoLauncher.Helpers;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace ExoLauncher;

public sealed partial class MainWindow
{
    private WindowPlacement? _normalPlacement;
    private WindowPlacement? _persistedPlacement;
    private DispatcherQueueTimer? _placementSaveTimer;

    private void RestoreWindowPlacement()
    {
        var saved = WindowPlacementStore.Load();
        if (saved is null) { TryCenterOnScreen(); RememberWindowPlacement(); return; }
        try
        {
            var area = DisplayArea.GetFromRect(new RectInt32(saved.X, saved.Y, saved.Width, saved.Height), DisplayAreaFallback.Nearest);
            if (area is null) { TryCenterOnScreen(); RememberWindowPlacement(); return; }
            // Move to the saved monitor before reading its DPI.
            AppWindow.Move(new PointInt32(area.WorkArea.X, area.WorkArea.Y));
            var work = area.WorkArea;
            var restored = WindowPlacementStore.Fit(saved, work.X, work.Y, work.Width, work.Height, GetWindowScale());
            AppWindow.MoveAndResize(new RectInt32(restored.X, restored.Y, restored.Width, restored.Height));
            _normalPlacement = restored;
            _persistedPlacement = saved;
            if (restored.Maximized && AppWindow.Presenter is OverlappedPresenter presenter) presenter.Maximize();
        }
        catch { TryCenterOnScreen(); RememberWindowPlacement(); }
    }

    private void RememberWindowPlacement()
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter || presenter.State == OverlappedPresenterState.Minimized) return;
        if (presenter.State == OverlappedPresenterState.Restored)
            _normalPlacement = new(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height, false, GetWindowScale());
        else if (_normalPlacement is not null)
            _normalPlacement = _normalPlacement with { Maximized = true };
        if (_normalPlacement is null || _normalPlacement == _persistedPlacement) return;
        _placementSaveTimer ??= CreatePlacementSaveTimer();
        _placementSaveTimer.Stop();
        _placementSaveTimer.Start();
    }

    private DispatcherQueueTimer CreatePlacementSaveTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(500);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => SaveWindowPlacement();
        return timer;
    }

    private void SaveWindowPlacement()
    {
        _placementSaveTimer?.Stop();
        if (_normalPlacement is not { } placement || placement == _persistedPlacement) return;
        WindowPlacementStore.Save(placement);
        _persistedPlacement = placement;
    }
}
