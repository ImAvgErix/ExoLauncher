namespace ExoLauncher.SteamIpc;

internal static class SteamUninstallReadiness
{
    // The app manager can connect before Steam has read its app manifests.
    // Do not interpret its initial Uninstalled/Unknown result as readiness
    // when the requested game's manifest is still on disk.
    internal static bool Wait(Func<int> readState, Func<bool> manifestExists, Action? wait = null)
    {
        wait ??= () => Thread.Sleep(250);
        for (var attempt = 0; attempt <= 48; attempt++)
        {
            if (readState() is not (0 or 1) || !manifestExists()) return true;
            if (attempt == 48) return false;
            wait();
        }
        return false;
    }
}
