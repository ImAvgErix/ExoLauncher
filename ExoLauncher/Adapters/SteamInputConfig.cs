namespace ExoLauncher.Adapters;

/// <summary>
/// Steam Input, per game, where Steam keeps it: the account's localconfig.vdf › UserLocalConfigStore › apps › &lt;app&gt; ›
/// <c>UseSteamControllerConfig</c> ("0" off, "1" or absent Steam's default, "2" on). Off wins over Steam's global setting
/// and over a developer's opt-in; app 413080 is Steam's desktop layout (the controller as mouse and keyboard outside
/// Steam games). Steam rewrites the file as it exits, so it is written with the overlay switch, while Steam is closed.
///
/// With Games read it themselves on, every installed Steam game whose store page lists PlayStation controller support
/// (category 57) reads the controller itself, and the desktop layout is off. A game that reads only Xbox controllers
/// keeps Steam Input: without it a DualSense does nothing there, and Exo adds no layer of its own. A game picked in Exo
/// gets what was picked. Exo records each value it replaced (none: the line wasn't there) and puts it back exactly when
/// it no longer owns the key, the line taken out again where there was none; a value
/// changed in Steam after Exo set it is Steam's from then on, so Exo never fights the player. Covered by
/// SteamInputConfigTests: a prior value lost on the way back, a player's change overwritten, an Xbox-only game turned off.
/// </summary>
internal static class SteamInputConfig
{
    internal const string Key = "UseSteamControllerConfig";
    internal const string DesktopLayout = "413080";
    /// <summary>Steam's store category for "PlayStation controller support" over USB (58 is over Bluetooth).</summary>
    internal const string PlayStationCategory = "category_57";

    internal static string? Read(SteamKeyValues apps, string appId) => apps.Section(appId).Value(Key);

    /// <summary>Whether a game's appinfo lists PlayStation controller support.</summary>
    internal static bool ReadsPlayStation(SteamKeyValues app) => app.Section("common").Section("category").Value(PlayStationCategory) is not null;

    /// <summary>What a game's Steam Input is now, in Exo's words: native (off), steam (on) or default (Steam decides).</summary>
    internal static string Mode(string? value) => value switch { "0" => "native", "2" => "steam", _ => "default" };

    /// <summary>
    /// The values to write and Exo's record once they land. <paramref name="native"/>: installed games that read a
    /// PlayStation controller themselves. <paramref name="fresh"/>: choices just made in Exo (they win over a change
    /// made in Steam). <paramref name="choices"/> comes back without the games whose value the player changed in Steam.
    /// </summary>
    internal static (Dictionary<string, string> Writes, Dictionary<string, string> Record, Dictionary<string, string> Choices) Plan(
        SteamKeyValues apps,
        IReadOnlyCollection<string> native,
        bool on,
        IReadOnlyDictionary<string, string> choices,
        IReadOnlyDictionary<string, string> record,
        IReadOnlySet<string>? fresh = null)
    {
        var writes = new Dictionary<string, string>(StringComparer.Ordinal);
        var nextRecord = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var nextChoices = new Dictionary<string, string>(choices, StringComparer.OrdinalIgnoreCase);
        var keys = record.Keys.Concat(choices.Keys).Concat(on ? native.Append(DesktopLayout) : []).Distinct(StringComparer.Ordinal);
        foreach (var appId in keys)
        {
            if (appId.Length is 0 or > 10 || !appId.All(char.IsAsciiDigit)) continue;
            // An empty prior is a line that wasn't there; Steam reads that as 1.
            var current = Read(apps, appId);
            var owned = record.TryGetValue(appId, out var entry) && entry.Split('>') is [var p, var s] ? (Prior: p, Set: s) : ((string Prior, string Set)?)null;
            // Changed in Steam since Exo set it: Steam's now, and Exo's pick for it is dropped.
            if (owned is { } o && (current ?? "1") != o.Set && fresh?.Contains(appId) != true)
            {
                nextChoices.Remove(appId);
                continue;
            }
            var desired = nextChoices.GetValueOrDefault(appId) switch
            {
                "native" => "0",
                "steam" => "2",
                _ => on && (appId == DesktopLayout || native.Contains(appId)) ? "0" : null,
            };
            if (desired is null)
            {
                // Exo no longer owns it: its value goes back to what it was.
                if (owned is { } given && current != (given.Prior.Length == 0 ? null : given.Prior)) writes[appId] = given.Prior;
                continue;
            }
            var prior = owned?.Prior ?? current ?? "";
            if ((current ?? "1") != desired) writes[appId] = desired;
            if (desired != (prior.Length == 0 ? "1" : prior)) nextRecord[appId] = $"{prior}>{desired}";
        }
        return (writes, nextRecord, nextChoices);
    }

    /// <summary>The file with each game's Steam Input value set (an empty value takes the line out); nothing else changes.</summary>
    internal static string Write(string text, IReadOnlyDictionary<string, string> values)
    {
        foreach (var (appId, value) in values)
        {
            if (appId.Length is 0 or > 10 || !appId.All(char.IsAsciiDigit)) throw new ArgumentException("Not a Steam app id.", nameof(values));
            if (value is not ("" or "0" or "1" or "2")) throw new ArgumentException("Steam Input is 0, 1 or 2.", nameof(values));
            string[] path = ["UserLocalConfigStore", "apps", appId];
            text = value.Length == 0 ? SteamOverlayConfig.RemoveValue(text, path, Key) : SteamOverlayConfig.SetValue(text, path, Key, value);
        }
        return text;
    }
}
