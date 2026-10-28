using System.Text.Json;

namespace ExoLauncher.Services;

/// <summary>
/// lucid: Lucid as Exo knows it and its settings ("state"), Start with Exo ("set", on), Lucid's own window ("open"),
/// Equalizer APO's device picker ("devices"), a change to its settings ("change", change), and its download page when
/// this PC has no Lucid ("get"); lucid.headphones lists its headphones; lucid.update names a newer Lucid. Lucid Voice is only read, never changed.
/// The state also says which output Lucid's EQ is on and whether Windows plays there (<see cref="LucidOutput"/>), and
/// whether a change waits for a game to end (held), its settings shown as they will be.
/// </summary>
public sealed partial class WebHostBridge
{
    /// <summary>Lucid's changes wait while a game plays and go in as the last one ends.</summary>
    private void HoldLucidForGames()
    {
        LucidCompanion.Playing = () => _services.Launcher.AnyGameRunning || MainWindow.RunningLibraryGames().Count > 0;
        _services.Launcher.GameRunStateChanged += ReleaseLucidAfterGames;
    }

    private void ReleaseLucidAfterGames(string gameId, GameRunState state)
    {
        if (state.IsRunning || LucidCompanion.Held is null) return;
        _ = Task.Run(() => { if (!LucidCompanion.Playing()) LucidCompanion.ReleaseHeld(_services.Settings.Current.LucidPath); });
    }

    private async Task<object> LucidAsync(JsonElement p, bool hasParams)
    {
        var settings = _services.Settings;
        var state = LucidCompanion.Read(settings.Current.LucidPath);
        string? message = null;
        switch (ReadString(p, hasParams, "action"))
        {
            case "set" when state.Path is { } path:
                var on = ReadBool(p, hasParams, "on") == true;
                settings.UpdateProfile(s => { s.StartLucidWithExo = on; s.LucidPath = path; });
                LucidCompanion.HideTray(on);
                if (on && !state.Running && !LucidCompanion.Start(path, window: false)) message = "Lucid didn't start. Open it once yourself, then try again.";
                break;
            case "open" when state.Path is { } path:
                if (!LucidCompanion.Start(path, window: true)) message = "Lucid didn't open.";
                break;
            case "devices" when state.Path is not null:
                message = LucidCompanion.OpenDeviceSelector();
                break;
            case "change" when state.Path is { } path && hasParams && p.TryGetProperty("change", out var change) && change.ValueKind == JsonValueKind.Object:
                message = await LucidCompanion.ChangeAsync(path, ReadLucidChange(change)).ConfigureAwait(false);
                break;
            case "get" when state.Path is null:
                if (!Ui.ExoUrlLaunch.TryStart(LucidCompanion.DownloadPage)) message = "The browser didn't open.";
                break;
        }
        state = LucidCompanion.Read(settings.Current.LucidPath);
        var root = state.Path is null ? null : LucidSettings.Load(LucidSettings.FilePath);
        var held = LucidCompanion.Held;
        // A change waiting for the game shows as it will be.
        if (root is not null && held is not null && state.Path is { } lucidPath)
            LucidSettings.Apply(root, held, id => LucidCompanion.HeadphoneName(lucidPath, id), LucidSettings.WindowsNightLight);
        var lucid = root is null ? null : LucidSettings.Read(root);
        var coverage = state.Path is not null ? LucidOutput.Read() : null;
        // A USB or wireless headset names itself: offer its tuning when Lucid has one and isn't on it.
        var guess = lucid is not null && state.Path is { } lucidExe && LucidCatalog.Read(lucidExe) is { } catalog
            && LucidCatalog.Guess(coverage?.Playing, catalog) is { } found && !string.Equals(found.Id, lucid.HeadphoneId, StringComparison.OrdinalIgnoreCase)
            ? found : null;
        return new
        {
            ok = message is null,
            message,
            found = state.Path is not null,
            version = state.Path is { } exe && LucidCompanion.FileVersion(exe) is { } installed ? LucidCompanion.Shown(installed) : null,
            running = state.Running,
            voiceOn = state.VoiceOn,
            startWithExo = settings.Current.StartLucidWithExo,
            held = held is not null,
            devices = state.Path is not null && LucidCompanion.DeviceSelector() is not null,
            eq = coverage is { } eq ? new { on = eq.On, playing = eq.Playing, covered = eq.Covered } : null,
            guess = guess is null ? null : new { id = guess.Id, name = guess.Name },
            settings = lucid is null ? null : new
            {
                headphone = new { id = lucid.HeadphoneId, name = lucid.HeadphoneName, chosen = lucid.HeadphoneChosen },
                favorites = lucid.Favorites,
                gameMode = lucid.GameMode,
                keepOnAltTab = lucid.KeepOnAltTab,
                onlyWhileGaming = lucid.OnlyWhileGaming,
                nightShift = lucid.NightShift,
                nightStart = lucid.NightStart,
                nightEnd = lucid.NightEnd,
                nightSync = lucid.NightSync,
                notifications = lucid.Notifications,
                discord = lucid.Discord,
                toggleSound = lucid.ToggleSound,
                detectSound = lucid.DetectSound,
                hotkey = new { modifiers = lucid.HotkeyModifiers, key = lucid.HotkeyKey },
                eqOn = lucid.EqOn,
            },
        };
    }

    /// <summary>lucid.update: the newer Lucid its own feed offers, for Lucid's window to install; null without one.</summary>
    private async Task<object> LucidUpdateAsync() =>
        new
        {
            version = LucidCompanion.Locate(_services.Settings.Current.LucidPath) is { } path && await LucidCompanion.AvailableUpdateAsync(path).ConfigureAwait(false) is { } latest
                ? LucidCompanion.Shown(latest)
                : null,
        };

    /// <summary>lucid.headphones: the headphones this PC's Lucid has tunings for, Lucid IEMs first.</summary>
    private object LucidHeadphones() =>
        LucidCompanion.Locate(_services.Settings.Current.LucidPath) is { } path && LucidCatalog.Read(path) is { } list
            ? new { ok = true, headphones = list.Select(h => new { id = h.Id, model = h.Model, brand = h.Brand, category = h.Category, grade = h.Grade, keywords = h.Keywords }).ToArray() }
            : new { ok = false, headphones = Array.Empty<object>() };

    private static LucidSettings.Change ReadLucidChange(JsonElement c)
    {
        bool? Flag(string name) => ReadBool(c, true, name);
        var favorites = c.TryGetProperty("favorites", out var fav) && fav.ValueKind == JsonValueKind.Object
            ? fav.EnumerateObject().Take(64).Where(e => e.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                .ToDictionary(e => e.Name, e => e.Value.GetBoolean(), StringComparer.OrdinalIgnoreCase)
            : null;
        return new LucidSettings.Change
        {
            Headphone = ReadString(c, true, "headphone"),
            Favorites = favorites,
            GameMode = Flag("gameMode"),
            KeepOnAltTab = Flag("keepOnAltTab"),
            OnlyWhileGaming = Flag("onlyWhileGaming"),
            NightShift = Flag("nightShift"),
            NightStart = ReadString(c, true, "nightStart"),
            NightEnd = ReadString(c, true, "nightEnd"),
            NightSync = Flag("nightSync"),
            Notifications = Flag("notifications"),
            Discord = Flag("discord"),
            ToggleSound = ReadDouble(c, true, "toggleSound") is { } toggle ? (int)Math.Round(toggle) : null,
            DetectSound = ReadDouble(c, true, "detectSound") is { } detect ? (int)Math.Round(detect) : null,
            Hotkey = c.TryGetProperty("hotkey", out var hotkey) && hotkey.ValueKind == JsonValueKind.Object
                && ReadDouble(hotkey, true, "key") is { } key && ReadDouble(hotkey, true, "modifiers") is { } modifiers
                ? ((int)modifiers, (int)key) : null,
        };
    }
}
