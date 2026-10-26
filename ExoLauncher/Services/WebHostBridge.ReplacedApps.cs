using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// replaced.scan: the apps Exo does the job of, each installed or not with what it left (clean: first clears what apps
/// that are gone left in this user's folders, said per app); replaced.remove: one of them removed with every leftover and
/// what Windows keeps about it (Revo-style), or a gone one's leftovers cleared. What Exo cleared of each app is kept
/// (cleanup.json), so Clean up keeps saying what went, after a restart too.
/// </summary>
public sealed partial class WebHostBridge
{
    private static readonly SemaphoreSlim ReplacedGate = new(1, 1);

    private static object ReplacedScan(bool clean)
    {
        var apps = ReplacedApps.ForThisPc();
        var cleared = clean ? apps.CleanLeftovers() : [];
        foreach (var (id, summary) in cleared) ClearedLog.Add(id, summary);
        return new { ok = true, cleared = cleared.Values.Sum(summary => summary.Count), apps = apps.Scan(ClearedLog.All()) };
    }

    private async Task<object> ReplacedRemoveAsync(string id, CancellationToken ct)
    {
        if (!await ReplacedGate.WaitAsync(0, ct).ConfigureAwait(false)) return new { ok = false, message = "Another app is being removed." };
        try
        {
            // Uninstalling an app and deleting its data is asked in Exo's own window, not on the page's word.
            var app = ReplacedApps.ForThisPc().Scan(ClearedLog.All()).FirstOrDefault(state => state.Id == id);
            var name = app?.Name ?? "this app";
            if (app is { Installed: false, Leftovers: 0 })
                return new { ok = true, message = (string?)null, removed = 0, left = 0, apps = ReplacedApps.ForThisPc().Scan(ClearedLog.All()) };
            var (title, body, action) = app is { Installed: false }
                ? ($"Clear what {name} left?", $"Exo will delete {Said(app.Found)}. This can't be undone.", "Clear")
                : ($"Remove {name}?", $"Exo will run {name}'s uninstaller, then deep clean what it leaves on this PC: " +
                    $"{(app is null || app.Found.Count == 0 ? "its folders, registry entries and shortcuts" : Said(app.Found))}, and what Windows keeps about it. " +
                    "This can't be undone.", "Remove");
            if (App.MainAppWindow is not { } asker || !await asker.ConfirmNativeAsync(title, body, action, danger: true).ConfigureAwait(true))
                return new { ok = false, declined = true, message = "Nothing was removed.", removed = 0, left = 0, apps = ReplacedApps.ForThisPc().Scan(ClearedLog.All()) };
            var result = await Task.Run(() => ReplacedApps.ForThisPc().RemoveAsync(id, ct), ct).ConfigureAwait(true);
            // Print Screen opened Windows' snip; with it gone, Exo's snip takes the key.
            if (result.Ok && id == "snipping" && App.MainAppWindow is { } window)
                await window.CaptureSetSettingsAsync(System.Text.Json.JsonDocument.Parse("""{"printScreen":true}""").RootElement).ConfigureAwait(true);
            if (result.Cleared is { Count: > 0 } summary) ClearedLog.Add(id, summary);
            return new { ok = result.Ok, message = result.Message, removed = result.Removed, left = result.Left, apps = ReplacedApps.ForThisPc().Scan(ClearedLog.All()) };
        }
        finally { ReplacedGate.Release(); }
    }

    /// <summary>"3 folders (1.2 GB), 9 registry entries and 2 shortcuts", as the Clean up rows say it.</summary>
    internal static string Said(LeftoverSummary found)
    {
        static string Count(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";
        var parts = new List<string>();
        if (found.Folders > 0) parts.Add(Count(found.Folders, "folder", "folders"));
        if (found.Files > 0) parts.Add(Count(found.Files, "file", "files"));
        if (found.Bytes >= 1_000_000 && parts.Count > 0) parts[^1] += string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $" ({(found.Bytes >= 1_000_000_000 ? $"{found.Bytes / 1e9:0.0} GB" : $"{found.Bytes / 1_000_000} MB")})");
        if (found.Registry > 0) parts.Add(Count(found.Registry, "registry entry", "registry entries"));
        if (found.Shortcuts > 0) parts.Add(Count(found.Shortcuts, "shortcut", "shortcuts"));
        return parts.Count switch
        {
            0 => "nothing",
            1 => parts[0],
            _ => string.Join(", ", parts[..^1]) + " and " + parts[^1],
        };
    }

    /// <summary>What Exo has cleared of each app on this PC, added up, in cleanup.json.</summary>
    private static class ClearedLog
    {
        private static readonly Lock Gate = new();
        private static string FilePath => Path.Combine(PathHelper.AppDataDir, "cleanup.json");

        internal static Dictionary<string, LeftoverSummary> All()
        {
            lock (Gate)
            {
                try
                {
                    if (File.Exists(FilePath) && new FileInfo(FilePath).Length < 64 * 1024 &&
                        JsonSerializer.Deserialize<Dictionary<string, LeftoverSummary>>(File.ReadAllText(FilePath)) is { } read)
                        return read;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    AppLog.Debug("Clean up record unreadable: " + ex.GetType().Name);
                }
                return [];
            }
        }

        internal static void Add(string id, LeftoverSummary summary)
        {
            lock (Gate)
            {
                var all = All();
                all[id] = all.TryGetValue(id, out var before)
                    ? new(before.Folders + summary.Folders, before.Files + summary.Files, before.Registry + summary.Registry,
                        before.Shortcuts + summary.Shortcuts, before.Bytes + summary.Bytes)
                    : summary;
                try
                {
                    Directory.CreateDirectory(PathHelper.AppDataDir);
                    var temp = FilePath + ".tmp";
                    File.WriteAllText(temp, JsonSerializer.Serialize(all));
                    File.Move(temp, FilePath, overwrite: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    AppLog.Debug("Clean up record not saved: " + ex.GetType().Name);
                }
            }
        }
    }
}
