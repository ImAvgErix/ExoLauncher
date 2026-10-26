using ExoLauncher.Helpers;
using Microsoft.Web.WebView2.Core;

namespace ExoLauncher;

public sealed partial class MainWindow
{
    private async Task VerifyUpdateStartupAsync(CoreWebView2 core)
    {
        var deadline = DateTime.UtcNow.AddSeconds(45);
        try
        {
            while (!_closingWindow && UpdateStartupHealth.Requested && DateTime.UtcNow < deadline)
            {
                if (!ReferenceEquals(WebHost.CoreWebView2, core)) return;
                // Confirm React progressed beyond its boot/error surface. The Library pane (with games,
                // empty, or showing a load error) and first-run setup are all usable starts.
                if (await core.ExecuteScriptAsync("Boolean(document.querySelector('main.exo-library-pane, .exo-ob-stage'))") == "true")
                {
                    // A receipt that could not be written this second (a scanner on the folder) is tried again: one miss would
                    // have the updater take a working build for a dead one and roll it back.
                    for (var attempt = 0; attempt < 10 && !_closingWindow; attempt++)
                    {
                        if (await Task.Run(() => UpdateStartupHealth.MarkHealthy(App.Services.AppVersion))) return;
                        await Task.Delay(1000);
                    }
                    return;
                }
                await Task.Delay(200);
            }
        }
        catch (Exception ex) { AppLog.Warn("Update startup health is unavailable: " + ex.GetType().Name); }
    }
}
