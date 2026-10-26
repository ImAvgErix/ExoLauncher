using System.Text.Json;
using System.Text.RegularExpressions;
using ExoLauncher.Helpers;

namespace ExoLauncher.Adapters;

/// <summary>
/// Steam's own offline mode for the account it signs in as: <c>WantsOfflineMode</c> and <c>SkipOfflineModeWarning</c>
/// in that account's block of loginusers.vdf, the two values Steam's Go Offline writes (current Steam keeps both, as "0"
/// when online; steamui reads them at sign-in: "StartLogin: SetWantsOfflineMode"). It needs a saved sign-in
/// (RememberPassword). Exo writes them only while Steam is closed (Steam rewrites the file as it exits), keeps a copy of
/// the file and the values they had (<c>steam-offline.json</c>), and puts exactly those values back, removing a line it
/// added; every other byte stays Steam's. Never a password, token or any other value.
/// </summary>
internal static partial class SteamLoginUsers
{
    internal const string WantsOfflineKey = "WantsOfflineMode";
    internal const string SkipWarningKey = "SkipOfflineModeWarning";

    /// <summary>One account's two offline values as the file has them; null where its line is missing.</summary>
    internal sealed record OfflineFlags(string? WantsOfflineMode, string? SkipOfflineModeWarning);

    /// <summary>What Exo switched: the account's one-way key and the values it had before.</summary>
    internal sealed record OfflineSwitch(string Account, string? WantsOfflineMode, string? SkipOfflineModeWarning)
    {
        internal OfflineFlags Before => new(WantsOfflineMode, SkipOfflineModeWarning);
    }

    private static string OfflineStatePath => Path.Combine(PathHelper.AppDataDir, "steam-offline.json");
    private static string OfflineBackupPath => Path.Combine(PathHelper.AppDataDir, "steam-offline-loginusers.vdf");

    /// <summary>The account's offline values; null when the account is not in the file.</summary>
    internal static OfflineFlags? ReadOffline(string text, string steamId64)
    {
        var block = Blocks(text).FirstOrDefault(entry => entry.SteamId64 == steamId64);
        if (block.SteamId64 is null) return null;
        var body = text[block.Open..(block.Close + 1)];
        return new OfflineFlags(FlagValue(body, WantsOfflineKey), FlagValue(body, SkipWarningKey));
    }

    /// <summary>Both values "1" for the account (a missing line added in the block's style), with what they were; null when the account is not in the file.</summary>
    internal static (string Text, OfflineFlags Before)? SetOffline(string text, string steamId64)
    {
        if (ReadOffline(text, steamId64) is not { } before) return null;
        var next = EditBlock(text, steamId64, body =>
            SetFlag(SetFlag(body, WantsOfflineKey, "1", addWhenMissing: true), SkipWarningKey, "1", addWhenMissing: true));
        return next is null ? null : (next, before);
    }

    /// <summary>The account's two values as <paramref name="before"/> had them (a line that was missing removed); null when the account is gone.</summary>
    internal static string? RestoreOffline(string text, string steamId64, OfflineFlags before) =>
        EditBlock(text, steamId64, body => Put(Put(body, WantsOfflineKey, before.WantsOfflineMode), SkipWarningKey, before.SkipOfflineModeWarning));

    /// <summary>The account Steam signs in as now, and whether Steam starts it in offline mode (Exo's switch or Steam's own).</summary>
    internal static (Account? Account, bool Offline) CurrentOffline(string steamRoot)
    {
        var text = SteamPlaytime.ReadSharedText(PathFor(steamRoot));
        if (text is null) return (null, false);
        var account = Current(Parse(text), SteamClientReady.ReadActiveUser(), ReadAutoLoginUser());
        return (account, account is not null && ReadOffline(text, account.SteamId64)?.WantsOfflineMode == "1");
    }

    /// <summary>Exo's switch while it stands; null when Exo has not put Steam offline.</summary>
    internal static OfflineSwitch? LoadOfflineSwitch()
    {
        try { return File.Exists(OfflineStatePath) ? JsonSerializer.Deserialize<OfflineSwitch>(File.ReadAllText(OfflineStatePath)) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>
    /// While Steam is closed: offline mode on for <paramref name="steamId64"/> (else the account Steam signs in as), the
    /// file copied and the values it had kept first. A switch already standing keeps its first values. Null when
    /// written, else why not.
    /// </summary>
    internal static string? WriteOffline(string steamRoot, string? steamId64 = null)
    {
        var path = PathFor(steamRoot);
        var accounts = SteamPlaytime.ReadSharedText(path) is { } first ? Parse(first) : [];
        var account = steamId64 is null ? Current(accounts, 0, ReadAutoLoginUser()) : accounts.FirstOrDefault(entry => entry.SteamId64 == steamId64);
        if (account is null || !account.Remembered) return "Steam plays offline only with a saved sign-in.";
        // A switch Exo made for another account goes back first.
        if (LoadOfflineSwitch() is { } other && other.Account != account.Key) WriteOnline(steamRoot);

        var text = SteamPlaytime.ReadSharedText(path);
        if (text is null || SetOffline(text, account.SteamId64) is not { } set) return "Steam has no saved sign-in on this PC.";
        if (string.Equals(set.Text, text, StringComparison.Ordinal)) return null;
        if (LoadOfflineSwitch() is null)
        {
            Directory.CreateDirectory(PathHelper.AppDataDir);
            File.Copy(path, OfflineBackupPath, overwrite: true);
            var staged = OfflineStatePath + ".exo-tmp";
            File.WriteAllText(staged, JsonSerializer.Serialize(new OfflineSwitch(account.Key, set.Before.WantsOfflineMode, set.Before.SkipOfflineModeWarning)));
            File.Move(staged, OfflineStatePath, overwrite: true);
        }
        WriteAtomically(path, set.Text);
        AppLog.Info("Steam: offline mode on for its sign-in account (loginusers.vdf backed up).");
        return null;
    }

    /// <summary>
    /// While Steam is closed: Exo's switch undone, the two values exactly as they were, the copy and the record removed.
    /// True when nothing of Exo's is left in the file.
    /// </summary>
    internal static bool WriteOnline(string steamRoot)
    {
        if (LoadOfflineSwitch() is not { } standing) return true;
        var path = PathFor(steamRoot);
        var text = SteamPlaytime.ReadSharedText(path);
        if (text is null) return false;
        var account = Parse(text).FirstOrDefault(entry => entry.Key == standing.Account);
        if (account is not null && RestoreOffline(text, account.SteamId64, standing.Before) is { } next &&
            !string.Equals(next, text, StringComparison.Ordinal))
            WriteAtomically(path, next);
        try
        {
            File.Delete(OfflineStatePath);
            File.Delete(OfflineBackupPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Warn("Steam: offline record not removed: " + ex.Message); }
        AppLog.Info(account is null ? "Steam: offline switch dropped; its account is no longer on this PC." : "Steam: offline mode values put back.");
        return true;
    }

    private static string? EditBlock(string text, string steamId64, Func<string, string> edit)
    {
        var block = Blocks(text).FirstOrDefault(entry => entry.SteamId64 == steamId64);
        if (block.SteamId64 is null) return null;
        var body = text[block.Open..(block.Close + 1)];
        return text[..block.Open] + edit(body) + text[(block.Close + 1)..];
    }

    private static string? FlagValue(string body, string key) =>
        FlagRegex(key).Match(body) is { Success: true } match ? match.Groups[2].Value : null;

    /// <summary>The key set to <paramref name="value"/>, or its line removed (with the line break before it) when null.</summary>
    private static string Put(string body, string key, string? value)
    {
        if (value is not null) return SetFlag(body, key, value, addWhenMissing: true);
        var line = new Regex("\\r?\\n[ \\t]*\"" + key + "\"[ \\t]+\"[^\"\\r\\n]*\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Match(body);
        return line.Success ? body.Remove(line.Index, line.Length) : body;
    }

    private static Regex FlagRegex(string key) =>
        new("(\"" + key + "\"\\s+\")([^\"]*)(\")", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
