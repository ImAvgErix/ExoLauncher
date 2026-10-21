using System.Text.Json;
using ExoLauncher.Adapters;

namespace ExoLauncher.Services;

public sealed partial class WebHostBridge
{
    private async Task<object> RepairCacheAsync(JsonElement p, bool hasParams)
    {
        var scope = ReadString(p, hasParams, "scope");
        switch (scope)
        {
            case "sizes":
                InstalledSizeCache.Clear();
                await _services.Library.GetLibraryAsync(force: true).ConfigureAwait(false);
                OnLibraryUpdated();
                break;
            case "upscalers": await Task.Run(DlssSwapService.RepairDetectionCache).ConfigureAwait(false); break;
            default: return new { ok = false, message = "Choose sizes or upscalers." };
        }
        return new { ok = true, message = "Cache cleared. Reopen the related page to refresh it." };
    }
}
