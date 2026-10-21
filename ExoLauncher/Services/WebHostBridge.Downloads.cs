using System.Text.Json;

namespace ExoLauncher.Services;

public sealed partial class WebHostBridge
{
    /// <summary>Settings > Downloads: pauseDownloadsWhilePlaying, downloadHoursStart/End (null for any time), autoUpdateGames.</summary>
    private void ApplyDownloadSettings(JsonElement p)
    {
        bool? pause = p.TryGetProperty("pauseDownloadsWhilePlaying", out var pw) && pw.ValueKind is JsonValueKind.True or JsonValueKind.False ? pw.GetBoolean() : null;
        bool? auto = p.TryGetProperty("autoUpdateGames", out var au) && au.ValueKind is JsonValueKind.True or JsonValueKind.False ? au.GetBoolean() : null;
        int? start = null, end = null;
        var anyTime = false;
        var hasStart = p.TryGetProperty("downloadHoursStart", out var hs);
        var hasEnd = p.TryGetProperty("downloadHoursEnd", out var he);
        if (hasStart && hs.ValueKind == JsonValueKind.Null) anyTime = true;
        else if (hasStart && hasEnd && hs.ValueKind == JsonValueKind.Number && he.ValueKind == JsonValueKind.Number &&
                 hs.TryGetInt32(out var s) && he.TryGetInt32(out var e)) (start, end) = (s, e);
        if (pause is null && auto is null && start is null && !anyTime) return;
        _services.Settings.ApplyDownloadPatch(pause, start, end, anyTime, auto);
        _services.Downloads.Apply();
    }

}
