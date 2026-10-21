using System.Text.Json;
using Microsoft.Win32;

namespace ExoLauncher.Adapters;

/// <summary>
/// The new Epic Games Launcher (2026, "eglv2", version 0.9x): installed per
/// user in %LOCALAPPDATA%\Epic Games\Launcher, with its data in the folder its
/// HKCU\Software\Epic Games\EpicGamesLauncher\AppDataPath names (by default
/// %LOCALAPPDATA%\com.epicgames.eglv2). It keeps no .item manifests or
/// LauncherInstalled.dat: installations.jsonl is its install list, a
/// transaction log of one JSON object per line (a header line, then each
/// installation as it was last written; the newest line for an id wins), and
/// cache\prod\catalog-cache.json holds each item's title. Its
/// com.epicgames.launcher:// links are the same as the old launcher's.
/// </summary>
internal static class EpicLauncherV2
{
    /// <param name="PendingVersion">The version an update the launcher started is bringing (gone once it lands).</param>
    /// <param name="Id">The launcher's installation id, which every later line for this install carries.</param>
    /// <param name="WrittenAt">When the launcher wrote this line.</param>
    internal sealed record Installation(
        string AppName, string? Namespace, string? ItemId, string Dir, string? LaunchExecutable, bool Complete, string? Version,
        string? PendingVersion = null, string? Id = null, DateTimeOffset? WrittenAt = null)
    {
        /// <summary>Installed whole, with no update the launcher still has to finish.</summary>
        internal bool UpToDate => Complete && string.IsNullOrWhiteSpace(PendingVersion);

        /// <summary>
        /// A version has landed once. A first install the launcher queued, paused or never finished has only a
        /// pendingVersion: its folder exists, but nothing is installed.
        /// </summary>
        internal bool EverInstalled => !string.IsNullOrWhiteSpace(Version);
    }

    /// <summary>The new launcher's data folder, or null when it is not on this PC.</summary>
    internal static string? DataDir()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Epic Games\EpicGamesLauncher");
            if (key?.GetValue("AppDataPath") is string named && Directory.Exists(named)) return named;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException) { }
        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "com.epicgames.eglv2");
        return Directory.Exists(fallback) ? fallback : null;
    }

    /// <summary>The installations the log lists now (removed ones dropped), each the newest line written for its id.</summary>
    internal static IReadOnlyList<Installation> ReadInstallations(string? dataDir = null)
    {
        dataDir ??= DataDir();
        if (dataDir is null) return [];
        var path = Path.Combine(dataDir, "installations.jsonl");
        try
        {
            if (!File.Exists(path)) return [];
            string[] lines;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream))
                lines = reader.ReadToEnd().Split('\n');
            var byId = new Dictionary<string, Installation?>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var e = doc.RootElement;
                    if (e.ValueKind != JsonValueKind.Object || Text(e, "installationId") is not { } id) continue;
                    if (Flag(e, "removed") || Flag(e, "deleted") || Text(e, "op") is "remove" or "delete" || Text(e, "dir") is not { Length: > 0 } dir)
                    {
                        byId[id] = null;
                        continue;
                    }
                    if (Text(e, "artifactId") is not { Length: > 0 } app) continue;
                    byId[id] = new Installation(app, Text(e, "namespace"), Text(e, "itemId"), dir, Text(e, "launchExecutable"),
                        !e.TryGetProperty("complete", out var c) || c.ValueKind != JsonValueKind.False, Text(e, "version"),
                        Text(e, "pendingVersion"), id,
                        e.TryGetProperty("writtenAt", out var w) && w.ValueKind == JsonValueKind.String && w.TryGetDateTimeOffset(out var at) ? at : null);
                }
                catch (JsonException) { /* a torn last line while the launcher writes */ }
            }
            return byId.Values.OfType<Installation>().ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Titles by catalog item id: the launcher's catalog cache, which keeps only items it showed lately, then its
    /// download history, which keeps the title of every game it finished installing (keyed namespace:item:artifact).
    /// </summary>
    internal static IReadOnlyDictionary<string, string> ReadTitles(string? dataDir = null)
    {
        dataDir ??= DataDir();
        var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (dataDir is null) return titles;
        if (ReadJson(Path.Combine(dataDir, "cache", "prod", "catalog-cache.json")) is { } catalog)
            using (catalog)
                if (catalog.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object)
                    foreach (var entry in items.EnumerateObject())
                        if (entry.Value.ValueKind == JsonValueKind.Object && entry.Value.TryGetProperty("item", out var item) &&
                            item.ValueKind == JsonValueKind.Object && Text(item, "title") is { Length: > 0 } title)
                            titles[entry.Name] = title;
        if (ReadJson(Path.Combine(dataDir, "download", "history.json")) is { } history)
            using (history)
                if (history.RootElement.ValueKind == JsonValueKind.Object)
                    foreach (var account in history.RootElement.EnumerateObject())
                        if (account.Value.ValueKind == JsonValueKind.Array)
                            foreach (var done in account.Value.EnumerateArray())
                                if (done.ValueKind == JsonValueKind.Object && Text(done, "key")?.Split(':') is [_, var itemId, _] &&
                                    Text(done, "titleCache") is { Length: > 0 } title)
                                    titles.TryAdd(itemId, title);
        return titles;
    }

    /// <summary>
    /// What the launcher installs that is not a game: Unreal Engine versions (its engines list, namespace "ue") and
    /// tools such as Twinmotion (its tools list), by app name and item id. Installed, they would be cards named by
    /// their app id (UE_5.8, or 32 hex digits for a tool).
    /// </summary>
    internal static IReadOnlySet<string> ReadNonGameIds(string? dataDir = null)
    {
        dataDir ??= DataDir();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (dataDir is null) return ids;
        void Add(JsonElement appId)
        {
            if (appId.ValueKind != JsonValueKind.Object) return;
            if (Text(appId, "appName") is { Length: > 0 } app) ids.Add(app);
            if (Text(appId, "itemId") is { Length: > 0 } item) ids.Add(item);
        }
        if (ReadJson(Path.Combine(dataDir, "cache", "prod", "engines-cache.json")) is { } engines)
            using (engines)
                if (engines.RootElement.ValueKind == JsonValueKind.Array)
                    foreach (var engine in engines.RootElement.EnumerateArray())
                        if (engine.ValueKind == JsonValueKind.Object && engine.TryGetProperty("appId", out var appId)) Add(appId);
        if (ReadJson(Path.Combine(dataDir, "cache", "prod", "tools-cache.json")) is { } tools)
            using (tools)
                if (tools.RootElement.ValueKind == JsonValueKind.Array)
                    foreach (var family in tools.RootElement.EnumerateArray())
                        if (family.ValueKind == JsonValueKind.Object && family.TryGetProperty("versions", out var versions) && versions.ValueKind == JsonValueKind.Array)
                            foreach (var version in versions.EnumerateArray())
                                if (version.ValueKind == JsonValueKind.Object && version.TryGetProperty("appId", out var appId)) Add(appId);
        return ids;
    }

    /// <summary>Not a game: an Unreal Engine version (namespace "ue") or anything the launcher's engines or tools list names.</summary>
    internal static bool IsNonGame(Installation install, IReadOnlySet<string> nonGameIds) =>
        string.Equals(install.Namespace, "ue", StringComparison.OrdinalIgnoreCase) || nonGameIds.Contains(install.AppName) ||
        (install.ItemId is { } item && nonGameIds.Contains(item));

    private static JsonDocument? ReadJson(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonDocument.Parse(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// One card per Epic product. The launcher lists a game's modes as their own
    /// installations under bare hex ids in the game's catalog namespace (LEGO
    /// Fortnite Odyssey and Save the World beside Fortnite); they are the same
    /// game. In each namespace the main installation is the one with a readable
    /// id, then the shortest known title; the rest are its modes, kept only for
    /// their hours. Installations without a namespace stay as they are.
    /// </summary>
    internal static (IReadOnlyList<Installation> Kept, IReadOnlyDictionary<string, IReadOnlyList<string>> ModesOf) FoldModes(
        IReadOnlyList<Installation> installs, Func<Installation, string?> titleOf)
    {
        var kept = new List<Installation>();
        var modes = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in installs.GroupBy(i => string.IsNullOrWhiteSpace(i.Namespace) ? "\0" + i.AppName : i.Namespace!, StringComparer.OrdinalIgnoreCase))
        {
            var all = group.ToList();
            if (all.Count == 1) { kept.Add(all[0]); continue; }
            var main = all
                .OrderBy(i => IsHexId(i.AppName) ? 1 : 0)
                .ThenBy(i => titleOf(i) is { Length: > 0 } title ? title.Length : int.MaxValue)
                .First();
            kept.Add(main);
            modes[main.AppName] = all.Where(i => !ReferenceEquals(i, main)).Select(i => i.AppName).ToList();
        }
        return (kept, modes);
    }

    /// <summary>An id that is only a hash (32 hex digits), never a name to show.</summary>
    internal static bool IsHexId(string? value) =>
        value is { Length: 32 } && value.All(Uri.IsHexDigit);

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Flag(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
