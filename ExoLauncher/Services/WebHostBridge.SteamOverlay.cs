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
/// (<see cref="StoreQuiet"/>) and Steam Input per game (<see cref="SteamInputConfig"/>) go through the same handoff.
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
    private async Task<string?> ApplySteamOverlayAsync(IReadOnlyDictionary<string, bool>? choices, bool userAsked, CancellationToken ct,
        IReadOnlySet<string>? freshInput = null)
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
            var info = InstalledAppInfo(root, installed);
            var eulas = SteamOverlayConfig.PendingEulas(configText, EulasOf(info));
            var native = NativeControllerGames(info);
            var input = SteamInputConfig.Plan(SteamOverlayConfig.Apps(configText), native, settings.NativeControllers, settings.SteamInputChoices, settings.SteamInput, freshInput);
            var quiet = settings.QuietStoreApps ? StoreQuiet.SteamEdits(root).Count : 0;
            if (writes.Count == 0 && eulas.Count == 0 && quiet == 0 && input.Writes.Count == 0)
            {
                if (choices is not null) AppLog.Info("Steam overlay: already as asked for " + string.Join(", ", choices.Keys) + "; nothing to write.");
                SaveOverlayRecord(record);
                SaveSteamInput(input.Record, input.Choices);
                return null;
            }
            var running = ProcessHelper.IsProcessRunning("steam");
            // Quiet alone closes Steam only when asked: Steam Cloud can hand back a sharedconfig.vdf that isn't quiet,
            // and closing Steam on its own over and over to fight that would be worse than a toast.
            string? wait = !running ? null
                : writes.Count == 0 && eulas.Count == 0 && input.Writes.Count == 0 && !userAsked ? "Steam takes it when it next closes."
                : SteamLoginUsers.IsGameRunning() ? "Steam takes it when the game closes."
                : SteamDownloading() ? "Steam takes it when its download finishes."
                : !userAsked && (HiddenStoreRuntime.IsSuspended(StoreKind.Steam) || DateTimeOffset.UtcNow - _overlayRestartedAt < OverlayRestartSpacing) ? "Steam takes it when it next closes."
                : null;
            if (wait is not null)
            {
                // Said in the log too: a switch that waits looks, from the page, like one that did nothing.
                AppLog.Info($"Steam overlay: {writes.Count} change(s) and Steam Input: {input.Writes.Count} wait. {wait}");
                SaveOverlayRecord(record);
                return wait;
            }
            var exe = Path.Combine(root, "steam.exe");
            if (running)
            {
                AppLog.Info($"Steam overlay: closing Steam to set {writes.Count} game(s), Steam Input for {input.Writes.Count}, accept {eulas.Count} game(s)' EULAs and quiet {quiet} file(s).");
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
                // Steam Input planned again on that copy: a value changed in Steam just before it closed stays Steam's.
                var inputNow = SteamInputConfig.Plan(SteamOverlayConfig.Apps(text), native, settings.NativeControllers, settings.SteamInputChoices, settings.SteamInput, freshInput);
                var next = SteamInputConfig.Write(
                    SteamOverlayConfig.AcceptEulas(SteamOverlayConfig.Write(text, writes), SteamOverlayConfig.PendingEulas(text, EulasOf(info))), inputNow.Writes);
                if (next != text) SteamOverlayConfig.WriteFile(config, next);
                SaveSteamInput(inputNow.Record, inputNow.Choices);
                if (inputNow.Writes.Count > 0) AppLog.Info($"Steam Input: set for {inputNow.Writes.Count} game(s) ({inputNow.Writes.Count(w => w.Value == "0")} reading the controller themselves).");
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
        SteamOverlayConfig.PendingEulas(SteamPlaytime.ReadSharedText(config), EulasOf(InstalledAppInfo(root, [appId]))).Count > 0;

    /// <summary>These games' appinfo (Steam's local copy of each store entry), by app id.</summary>
    private static Dictionary<string, SteamKeyValues> InstalledAppInfo(string root, IReadOnlyCollection<string> installed)
    {
        var ids = installed.Select(id => uint.TryParse(id, out var n) ? n : 0).Where(n => n != 0).ToHashSet();
        if (ids.Count == 0 || ReadSharedBytes(Path.Combine(root, "appcache", "appinfo.vdf")) is not { } appInfo) return [];
        return SteamAppInfoNames.ReadApps(appInfo, ids).ToDictionary(a => a.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), a => a.Value, StringComparer.Ordinal);
    }

    /// <summary>The EULAs Steam lists for these games (appinfo's common/eulas: id and version).</summary>
    private static Dictionary<string, IReadOnlyList<(string Id, int Version)>> EulasOf(IReadOnlyDictionary<string, SteamKeyValues> info)
    {
        var found = new Dictionary<string, IReadOnlyList<(string, int)>>(StringComparer.Ordinal);
        foreach (var (appId, app) in info)
        {
            var list = app.Section("common").Section("eulas").Sections
                .Select(e => (Id: e.Section.Value("id") ?? "", Version: (int)(e.Section.Number("version") ?? 0)))
                .Where(e => e.Id.Length > 0)
                .ToList();
            if (list.Count > 0) found[appId] = list;
        }
        return found;
    }

    /// <summary>The games among these that read a PlayStation controller themselves (their store page says so).</summary>
    private static List<string> NativeControllerGames(IReadOnlyDictionary<string, SteamKeyValues> info) =>
        [.. info.Where(a => SteamInputConfig.ReadsPlayStation(a.Value)).Select(a => a.Key)];

    private void SaveSteamInput(Dictionary<string, string> record, Dictionary<string, string> choices)
    {
        var s = _services.Settings.Current;
        static bool Same(Dictionary<string, string> a, Dictionary<string, string> b) => a.Count == b.Count && a.All(e => b.GetValueOrDefault(e.Key) == e.Value);
        if (Same(record, s.SteamInput) && Same(choices, s.SteamInputChoices)) return;
        _services.Settings.UpdateProfile(x =>
        {
            x.SteamInput = new Dictionary<string, string>(record, StringComparer.OrdinalIgnoreCase);
            x.SteamInputChoices = new Dictionary<string, string>(choices, StringComparer.OrdinalIgnoreCase);
        });
    }

    /// <summary>A game's Steam Input for its Properties: the pick (or what Steam has), and whether the game reads a PlayStation controller itself.</summary>
    private (string Mode, bool ReadsPlayStation)? SteamInputFor(string appId)
    {
        if (SteamInstallation.ResolveRoot() is not { } root || SteamPlaytime.TryGetLocalConfigPath(root) is not { } config) return null;
        var current = SteamInputConfig.Read(SteamOverlayConfig.Apps(SteamPlaytime.ReadSharedText(config)), appId);
        var picked = _services.Settings.Current.SteamInputChoices.GetValueOrDefault(appId);
        return (picked ?? SteamInputConfig.Mode(current), InstalledAppInfo(root, [appId]).Values.Any(SteamInputConfig.ReadsPlayStation));
    }

    /// <summary>Devices > controller > Games read it themselves: on or off for every Steam game, applied now (Steam closes and starts again hidden).</summary>
    private async Task<object> SteamInputSetAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        if (ReadBool(p, hasParams, "on") is not { } on) return new { ok = false, message = "On or off?" };
        if (on != _services.Settings.Current.NativeControllers) _services.Settings.UpdateProfile(s => s.NativeControllers = on);
        var wait = await ApplySteamOverlayAsync(null, userAsked: true, ct).ConfigureAwait(false);
        return new { ok = true, message = wait };
    }

    /// <summary>The switch, and how many installed Steam games read the controller themselves and how many need Steam Input.</summary>
    private object SteamInputState()
    {
        if (SteamInstallation.ResolveRoot() is not { } root) return new { ok = true, steam = false, on = false, native = 0, translated = 0 };
        var installed = _services.Library.PeekCachedLibrary()
            .Where(game => game.Store == StoreKind.Steam && game.Installed && SteamProtocol.IsValidAppId(game.LaunchTarget))
            .Select(game => game.LaunchTarget!).ToList();
        var native = NativeControllerGames(InstalledAppInfo(root, installed));
        return new { ok = true, steam = true, on = _services.Settings.Current.NativeControllers, native = native.Count, translated = installed.Count - native.Count };
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
