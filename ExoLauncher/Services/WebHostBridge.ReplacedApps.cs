namespace ExoLauncher.Services;

/// <summary>
/// replaced.scan: the apps Exo does the job of, each installed or not with what it left (clean: first clears what apps
/// that are gone left in this user's folders); replaced.remove: one of them removed with every leftover (Revo-style).
/// </summary>
public sealed partial class WebHostBridge
{
    private static readonly SemaphoreSlim ReplacedGate = new(1, 1);

    private static object ReplacedScan(bool clean)
    {
        var apps = ReplacedApps.ForThisPc();
        var cleared = clean ? apps.CleanLeftovers() : 0;
        return new { ok = true, cleared, apps = apps.Scan() };
    }

    private async Task<object> ReplacedRemoveAsync(string id, CancellationToken ct)
    {
        if (!await ReplacedGate.WaitAsync(0, ct).ConfigureAwait(false)) return new { ok = false, message = "Another app is being removed." };
        try
        {
            // Uninstalling an app and deleting its data is asked in Exo's own window, not on the page's word.
            var name = ReplacedApps.ForThisPc().Scan().FirstOrDefault(app => app.Id == id)?.Name ?? "this app";
            if (App.MainAppWindow is not { } asker ||
                !await asker.ConfirmNativeAsync("Remove " + name, $"Exo will uninstall {name} and delete everything it left on this PC. This can't be undone.", "Remove").ConfigureAwait(true))
                return new { ok = false, message = "Nothing was removed.", removed = 0, left = 0, apps = ReplacedApps.ForThisPc().Scan() };
            var result = await Task.Run(() => ReplacedApps.ForThisPc().RemoveAsync(id, ct), ct).ConfigureAwait(true);
            // Print Screen opened Windows' snip; with it gone, Exo's snip takes the key.
            if (result.Ok && id == "snipping" && App.MainAppWindow is { } window)
                await window.CaptureSetSettingsAsync(System.Text.Json.JsonDocument.Parse("""{"printScreen":true}""").RootElement).ConfigureAwait(true);
            return new { ok = result.Ok, message = result.Message, removed = result.Removed, left = result.Left, apps = ReplacedApps.ForThisPc().Scan() };
        }
        finally { ReplacedGate.Release(); }
    }
}
