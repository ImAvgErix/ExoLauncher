using System.Text.Json;
using ExoLauncher.Adapters;
using ExoLauncher.Adapters.Cli;
using ExoLauncher.Helpers;
using ExoLauncher.Models;
using Microsoft.Win32;

namespace ExoLauncher.Services;

/// <summary>
/// The Steam overlay, off by default for every Steam game Exo manages (it costs
/// frames, and Exo has its own overlay), with a switch per game in Properties and
/// the default in Settings > Game stores. Steam keeps the switch in the account's
/// localconfig.vdf (<see cref="SteamOverlayConfig"/>), which it rewrites as it
/// exits, so Exo writes it while Steam is closed: at once when Steam is not
/// running, else through the same close-and-start-hidden handoff as an account
/// switch, and never while a Steam game runs or Steam downloads. Steam's notifications and sounds
/// (<see cref="StoreQuiet"/>) go through the same handoff.
/// </summary>
public sealed partial class WebHostBridge
{
    private readonly SemaphoreSlim _overlayGate = new(1, 1);
    private DateTimeOffset _overlayRestartedAt = DateTimeOffset.MinValue;

    /// <summary>A Steam restart Exo starts on its own waits this long after the last one, so a write Steam refuses cannot loop.</summary>
    private static readonly TimeSpan OverlayRestartSpacing = TimeSpan.FromMinutes(30);

    /// <summary>The game's overlay switch for Properties: what Steam has, or what Exo is about to write.</summary>
    private bool? SteamOverlayFor(string appId)
    {
        if (SteamInstallation.ResolveRoot() is not { } root || SteamPlaytime.TryGetLocalConfigPath(root) is not { } config) return null;
        var kept = _services.Settings.Current.SteamOverlay.GetValueOrDefault(appId);
        if (kept is "on?" or "off?") return kept.StartsWith("on", StringComparison.Ordinal);
        return SteamOverlayConfig.Read(SteamOverlayConfig.Apps(SteamPlaytime.ReadSharedText(config)), appId) ?? true;
    }

    /// <summary>After each library read: brings new Steam games to the default, in the background.</summary>
    private void SteamOverlaySoon() => _ = Task.Run(async () =>
    {
        try { await ApplySteamOverlayAsync(null, userAsked: false, _bridgeLifetime.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppLog.Warn($"Steam overlay: {ex.GetType().Name}: {ex.Message}"); }
    });

    /// <summary>Settings > Game stores > Steam overlay: the new default for every Steam game, applied now.</summary>
    private void ApplySteamOverlaySettings(JsonElement p)
    {
        if (!p.TryGetProperty("steamOverlayDefaultOn", out var value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return;
        var on = value.GetBoolean();
        if (on == _services.Settings.Current.SteamOverlayDefaultOn) return;
        // A new default is a fresh start: every game takes it; Properties can change one after.
        _services.Settings.UpdateProfile(s => { s.SteamOverlayDefaultOn = on; s.SteamOverlay.Clear(); });
        _ = Task.Run(async () =>
        {
            try { await ApplySteamOverlayAsync(null, userAsked: true, _bridgeLifetime.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { AppLog.Warn($"Steam overlay default: {ex.GetType().Name}: {ex.Message}"); }
        });
    }

    /// <summary>
    /// Brings Steam's switches to Exo's record. <paramref name="choices"/> are new
    /// choices from Properties. Returns why a change waits, or null when Steam has it.
    /// A restart Exo starts on its own happens only while Steam's window is not the
    /// user's, and at most once per <see cref="OverlayRestartSpacing"/>.
    /// </summary>
    private async Task<string?> ApplySteamOverlayAsync(IReadOnlyDictionary<string, bool>? choices, bool userAsked, CancellationToken ct)
    {
        if (SteamInstallation.ResolveRoot() is not { } root || SteamPlaytime.TryGetLocalConfigPath(root) is not { } config) return null;
        var installed = _services.Library.PeekCachedLibrary()
            .Where(game => game.Store == StoreKind.Steam && game.Installed && SteamProtocol.IsValidAppId(game.LaunchTarget))
            .Select(game => game.LaunchTarget!)
            .ToList();
        await _overlayGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var settings = _services.Settings.Current;
            var configText = SteamPlaytime.ReadSharedText(config);
            var (writes, record) = SteamOverlayConfig.Plan(
                SteamOverlayConfig.Apps(configText), installed, settings.SteamOverlay, settings.SteamOverlayDefaultOn, choices);
            // Each installed game's EULA accepted the way Steam's Accept does, so no game stops at a EULA at launch.
            var eulas = SteamOverlayConfig.PendingEulas(configText, InstalledEulas(root, installed));
            var quiet = settings.QuietStoreApps ? StoreQuiet.SteamEdits(root).Count : 0;
            if (writes.Count == 0 && eulas.Count == 0 && quiet == 0)
            {
                SaveOverlayRecord(record);
                return null;
            }
            var running = ProcessHelper.IsProcessRunning("steam");
            // Quiet alone closes Steam only when asked: Steam Cloud can hand back a sharedconfig.vdf that isn't quiet,
            // and closing Steam on its own over and over to fight that would be worse than a toast.
            string? wait = !running ? null
                : writes.Count == 0 && eulas.Count == 0 && !userAsked ? "Steam takes it when it next closes."
                : SteamLoginUsers.IsGameRunning() ? "Steam takes it when the game closes."
                : SteamDownloading() ? "Steam takes it when its download finishes."
                : !userAsked && (HiddenStoreRuntime.IsSuspended(StoreKind.Steam) || DateTimeOffset.UtcNow - _overlayRestartedAt < OverlayRestartSpacing) ? "Steam takes it when it next closes."
                : null;
            if (wait is not null)
            {
                SaveOverlayRecord(record);
                return wait;
            }
            var exe = Path.Combine(root, "steam.exe");
            if (running)
            {
                AppLog.Info($"Steam overlay: closing Steam to set {writes.Count} game(s), accept {eulas.Count} game(s)' EULAs and quiet {quiet} file(s).");
                _overlayRestartedAt = DateTimeOffset.UtcNow;
                if (!await CloseSteamAsync(exe, ct).ConfigureAwait(false))
                {
                    SaveOverlayRecord(record);
                    return "Steam did not close. Steam takes it when it next closes.";
                }
            }
            try
            {
                // Steam rewrote the file as it closed; the switches go into that copy.
                var text = SteamPlaytime.ReadSharedText(config) ?? throw new IOException("localconfig.vdf is gone.");
                var next = SteamOverlayConfig.AcceptEulas(SteamOverlayConfig.Write(text, writes), SteamOverlayConfig.PendingEulas(text, InstalledEulas(root, installed)));
                if (next != text) SteamOverlayConfig.WriteFile(config, next);
                // Every account's notifications and sounds, from the files as Steam rewrote them closing.
                if (settings.QuietStoreApps)
                {
                    var quieted = StoreQuiet.SteamEdits(root);
                    foreach (var (path, quietText) in quieted) SteamOverlayConfig.WriteFile(path, quietText);
                    if (quieted.Count > 0) AppLog.Info($"Steam: notifications and sounds off ({quieted.Count} file(s)).");
                }
                if (eulas.Count > 0) AppLog.Info("Steam: EULAs accepted for " + string.Join(", ", eulas.Keys) + ".");
                foreach (var appId in writes.Keys) record[appId] = record[appId].TrimEnd('?');
                SaveOverlayRecord(record);
                if (writes.Count > 0) AppLog.Info($"Steam overlay: set for {writes.Count} game(s) ({writes.Count(w => !w.Value)} off).");
                return null;
            }
            finally
            {
                if (running) StartSteamHidden(exe);
            }
        }
        finally
        {
            _overlayGate.Release();
        }
    }

    /// <summary>
    /// Settings > Game stores > Notifications and sounds > Turn off: Steam's and Epic's own pop-ups and sounds off now
    /// and kept off. Steam closes and starts again hidden for it, unless a game runs or it downloads; Epic takes it
    /// now when it's closed, else before Exo next starts it.
    /// </summary>
    private async Task<object> QuietStoresAsync(CancellationToken ct)
    {
        if (!_services.Settings.Current.QuietStoreApps) _services.Settings.UpdateProfile(s => s.QuietStoreApps = true);
        EpicQuietSettings.HandToExo();
        var wait = await ApplySteamOverlayAsync(null, userAsked: true, ct).ConfigureAwait(false);
        return new { ok = true, message = wait };
    }

    /// <summary>Whether this Steam game has a EULA Steam would ask for at launch.</summary>
    private static bool SteamEulaPending(string appId) =>
        SteamInstallation.ResolveRoot() is { } root && SteamPlaytime.TryGetLocalConfigPath(root) is { } config &&
        SteamOverlayConfig.PendingEulas(SteamPlaytime.ReadSharedText(config), InstalledEulas(root, [appId])).Count > 0;

    /// <summary>The EULAs Steam lists for these games (appinfo's common/eulas: id and version).</summary>
    private static Dictionary<string, IReadOnlyList<(string Id, int Version)>> InstalledEulas(string root, IReadOnlyCollection<string> installed)
    {
        var found = new Dictionary<string, IReadOnlyList<(string, int)>>(StringComparer.Ordinal);
        var ids = installed.Select(id => uint.TryParse(id, out var n) ? n : 0).Where(n => n != 0).ToHashSet();
        if (ids.Count == 0 || ReadSharedBytes(Path.Combine(root, "appcache", "appinfo.vdf")) is not { } appInfo) return found;
        foreach (var (appId, app) in SteamAppInfoNames.ReadApps(appInfo, ids))
        {
            var list = app.Section("common").Section("eulas").Sections
                .Select(e => (Id: e.Section.Value("id") ?? "", Version: (int)(e.Section.Number("version") ?? 0)))
                .Where(e => e.Id.Length > 0)
                .ToList();
            if (list.Count > 0) found[appId.ToString(System.Globalization.CultureInfo.InvariantCulture)] = list;
        }
        return found;
    }

    private void SaveOverlayRecord(Dictionary<string, string> record)
    {
        var current = _services.Settings.Current.SteamOverlay;
        if (current.Count == record.Count && record.All(entry => current.GetValueOrDefault(entry.Key) == entry.Value)) return;
        _services.Settings.UpdateProfile(s => s.SteamOverlay = new Dictionary<string, string>(record, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Steam marks a game it is downloading under HKCU\Software\Valve\Steam\Apps\&lt;id&gt; › Updating.</summary>
    private static bool SteamDownloading()
    {
        try
        {
            using var apps = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\Apps");
            return apps?.GetSubKeyNames().Any(name =>
            {
                using var app = apps.OpenSubKey(name);
                return app?.GetValue("Updating") is int updating && updating != 0;
            }) == true;
        }
        catch { return false; }
    }
}
