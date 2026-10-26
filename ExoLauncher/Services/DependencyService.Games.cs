using System.Text.Json;
using System.Text.RegularExpressions;
using ExoLauncher.Adapters;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>
/// What a game's own store says it needs, as catalog ids, so the notice before Play offers exactly that: Steam's shared
/// redistributables (the appmanifest's SharedDepots) and the game's installscript.vdf, Epic's prerequisite (its launcher
/// manifest, Legendary's installed.json), GOG's dependencies (gogdl's manifest), and for every store the installers a game
/// ships in its own _CommonRedist, __redist or Redist folder. Each is a hint read by <see cref="MapHint"/>.
/// </summary>
public sealed partial class DependencyService
{
    // Steamworks Common Redistributables (app 228980): the depots a game names under SharedDepots. Its installscripts run
    // the x86 package everywhere and the x64 one on 64-bit Windows, so each Visual C++ depot is both.
    private static readonly Dictionary<string, string[]> SteamDepots = new(StringComparer.Ordinal)
    {
        ["228981"] = ["vc2005-x86", "vc2005-x64"],
        ["228982"] = ["vc2008-x86", "vc2008-x64"],
        ["228983"] = ["vc2010-x86", "vc2010-x64"],
        ["228984"] = ["vc2012-x86", "vc2012-x64"],
        ["228985"] = ["vc2013-x86", "vc2013-x64"],
        ["228986"] = ["vc14-x86", "vc14-x64"],
        ["228987"] = ["vc14-x86", "vc14-x64"],
        ["228988"] = ["vc14-x86", "vc14-x64"],
        ["228989"] = ["vc14-x86", "vc14-x64"],
        ["228990"] = ["directx"],
    };

    private static readonly string[] RedistFolders =
        ["_CommonRedist", "CommonRedist", "__redist", "_redist", "Redist", "Redistributables", "_Installer", "__Installer", "Prerequisites", "Prereqs", "Support"];

    /// <summary>
    /// The runtimes one hint names. A hint is a path or name from a store's record or a game's redist folder
    /// ("vcredist\2010\vcredist_x64.exe", "MSVC2017_x64", "DirectX\Jun2010\DXSETUP.exe", "UEPrereqSetup_x64.exe").
    /// <paramref name="unsaid"/>: the architectures a Visual C++ hint means when it names none (a Steam folder holds
    /// both; GOG's "MSVC2017" is the 32-bit one).
    /// </summary>
    internal static IReadOnlyList<string> MapHint(string hint, string[]? unsaid = null)
    {
        var text = hint.Replace('\\', '/').ToLowerInvariant();
        var ids = new List<string>();
        var x64 = Regex.IsMatch(text, @"x64|amd64|win64|64-?bit");
        var x86 = Regex.IsMatch(text, @"x86|win32|32-?bit");
        IEnumerable<string> Arches()
        {
            if (!x64 && !x86) return unsaid ?? ["x86", "x64"];
            var arches = new List<string>();
            if (x86) arches.Add("x86");
            if (x64) arches.Add("x64");
            return arches;
        }

        if (Regex.IsMatch(text, @"ue4?prereqsetup")) ids.AddRange(["vc14-x86", "vc14-x64", "directx"]);
        else if (Regex.IsMatch(text, @"vcredist|vc_redist|msvc|visual c\+\+|vcrun|(^|/)vc(/|$)"))
        {
            var year = Regex.Match(text, @"20(05|08|10|12|13|15|17|19|22|26)").Value;
            // No year: vc_redist (with the underscore) is how 2015 and later name it.
            var family = year switch
            {
                "2005" => "vc2005", "2008" => "vc2008", "2010" => "vc2010", "2012" => "vc2012", "2013" => "vc2013",
                "" when !text.Contains("vc_redist", StringComparison.Ordinal) => null,
                _ => "vc14",
            };
            if (family is not null) ids.AddRange(Arches().Select(arch => $"{family}-{arch}"));
        }
        else if (Regex.IsMatch(text, @"dxsetup|directx") && !Regex.IsMatch(text, @"directx ?12|dx12")) ids.Add("directx");
        else if (Regex.IsMatch(text, @"dotnet/3\.5|dotnetfx35|netfx3|dotnet ?3\.?5|net ?framework 3\.5")) ids.Add("netfx35");
        else if (Regex.Match(text, @"windowsdesktop-runtime-(\d+)") is { Success: true } desktop)
            ids.AddRange(Arches().Select(arch => $"dotnet{desktop.Groups[1].Value}-{arch}").Where(id => Find(id) is not null));
        else if (Regex.IsMatch(text, @"dotnet/4\.|ndp4\d|dotnetfx4|dotnet ?4|net ?framework 4")) ids.Add("netfx481");
        else if (Regex.IsMatch(text, @"xna/4\.0|xnafx40|xna ?4")) ids.Add("xna40");
        else if (Regex.IsMatch(text, @"openal|oalinst")) ids.Add("openal");
        else if (text.Contains("physx", StringComparison.Ordinal)) ids.Add(text.Contains("legacy", StringComparison.Ordinal) ? "physx-legacy" : "physx");
        else if (text.Contains("gameinput", StringComparison.Ordinal)) ids.Add("gameinput");
        else if (Regex.IsMatch(text, @"webview2")) ids.Add("webview2");
        return ids;
    }

    /// <summary>The catalog ids a game's store and folder say it needs.</summary>
    internal static IReadOnlyList<string> RuntimesFor(GameEntry game)
    {
        var ids = new List<string>();
        void Add(string hint, string[]? unsaid = null) => ids.AddRange(MapHint(hint, unsaid));
        try
        {
            if (game.Store == StoreKind.Steam) SteamHints(game, ids, Add);
            if (game.Store == StoreKind.Epic) EpicHints(game, Add);
            if (game.Store == StoreKind.Gog) GogHints(game, Add);
            foreach (var hint in FolderHints(game.Path)) Add(hint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            AppLog.Debug("Runtimes: a game's needs couldn't all be read: " + ex.GetType().Name);
        }
        return ids.Distinct(StringComparer.Ordinal).Where(id => Find(id) is not null).ToList();
    }

    private static void SteamHints(GameEntry game, List<string> ids, Action<string, string[]?> add)
    {
        if (string.IsNullOrWhiteSpace(game.Path) || string.IsNullOrWhiteSpace(game.LaunchTarget)) return;
        var common = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(game.Path));
        if (common is null || !Path.GetFileName(common).Equals("common", StringComparison.OrdinalIgnoreCase)) return;
        var manifest = Path.Combine(Path.GetDirectoryName(common)!, $"appmanifest_{game.LaunchTarget}.acf");
        if (File.Exists(manifest)) ids.AddRange(SteamSharedDepots(File.ReadAllText(manifest)));
        var script = Path.Combine(game.Path, "installscript.vdf");
        if (File.Exists(script) && new FileInfo(script).Length < 256 * 1024)
            foreach (var hint in InstallScriptHints(File.ReadAllText(script))) add(hint, null);
    }

    /// <summary>The runtimes behind an appmanifest's SharedDepots.</summary>
    internal static IEnumerable<string> SteamSharedDepots(string acf) =>
        SteamKeyValues.ParseText(acf).Section("AppState/SharedDepots").Keys.SelectMany(depot => SteamDepots.GetValueOrDefault(depot) ?? []);

    /// <summary>What an installscript.vdf runs ("Run Process" › each entry's "process N"), with the entry's own name.</summary>
    internal static IEnumerable<string> InstallScriptHints(string vdf)
    {
        var root = SteamKeyValues.ParseText(vdf);
        var script = root.Section("InstallScript");
        if (script.Keys.Count == 0) script = root.Keys.Count == 1 ? root.Section(root.Keys[0]) : root;
        foreach (var (name, entry) in script.Section("Run Process").Sections)
            foreach (var key in entry.Keys.Where(k => k.StartsWith("process", StringComparison.OrdinalIgnoreCase)))
                if (entry.Value(key) is { } process) yield return name + "/" + process;
    }

    private static void EpicHints(GameEntry game, Action<string, string[]?> add)
    {
        if (string.IsNullOrWhiteSpace(game.LaunchTarget)) return;
        var manifests = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (Directory.Exists(manifests))
            foreach (var file in Directory.EnumerateFiles(manifests, "*.item").Take(500))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var root = doc.RootElement;
                if (!root.TryGetProperty("AppName", out var app) || app.GetString() != game.LaunchTarget) continue;
                foreach (var hint in EpicPrereqHints(root, "PrereqName", "PrereqPath")) add(hint, null);
            }
        foreach (var path in EpicAdapter.LegendaryInstalledJsonCandidates().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path)) continue;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(game.LaunchTarget, out var install) &&
                install.TryGetProperty("prereq_info", out var prereq) && prereq.ValueKind == JsonValueKind.Object)
                foreach (var hint in EpicPrereqHints(prereq, "name", "path")) add(hint, null);
        }
    }

    /// <summary>Epic's prerequisite: its name and its installer's path inside the game ("…/UEPrereqSetup_x64.exe").</summary>
    internal static IEnumerable<string> EpicPrereqHints(JsonElement record, string nameKey, string pathKey)
    {
        foreach (var key in new[] { pathKey, nameKey })
            if (record.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text)
            {
                yield return text;
                yield break; // the path names it best; the name only when there is no path
            }
    }

    private static void GogHints(GameEntry game, Action<string, string[]?> add)
    {
        if (string.IsNullOrWhiteSpace(game.LaunchTarget) || !Regex.IsMatch(game.LaunchTarget, @"^\d+$")) return;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        foreach (var folder in new[] { Path.Combine(home, ".config", "heroic_gogdl"), Path.Combine(roaming, "heroic", "gogdlConfig", "heroic_gogdl") })
        {
            var manifest = Path.Combine(folder, "manifests", game.LaunchTarget);
            if (!File.Exists(manifest) || new FileInfo(manifest).Length > 32 * 1024 * 1024) continue;
            foreach (var dependency in GogDependencies(File.ReadAllText(manifest))) add(dependency, ["x86"]);
        }
    }

    /// <summary>A gogdl manifest's dependencies ("MSVC2017", "MSVC2017_x64", "DirectX", "DotNet35").</summary>
    internal static IEnumerable<string> GogDependencies(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("dependencies", out var dependencies) || dependencies.ValueKind != JsonValueKind.Array) return [];
        return dependencies.EnumerateArray().Where(d => d.ValueKind == JsonValueKind.String).Select(d => d.GetString()!).ToArray();
    }

    /// <summary>
    /// The installers a game ships in its own redist folders, as paths under them ("vcredist/2010/vcredist_x64.exe"); a
    /// folder holding none is itself the hint. Bounded: a few levels and a few hundred entries.
    /// </summary>
    internal static IReadOnlyList<string> FolderHints(string? gamePath)
    {
        var hints = new List<string>();
        if (string.IsNullOrWhiteSpace(gamePath) || !Directory.Exists(gamePath)) return hints;
        var seen = 0;
        foreach (var name in RedistFolders)
        {
            var root = Path.Combine(gamePath, name);
            if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) continue;
            Walk(root, "", 0);
        }
        return hints;

        void Walk(string folder, string relative, int depth)
        {
            var installers = 0;
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                if (++seen > 400) return;
                if (Path.GetExtension(file).ToLowerInvariant() is not (".exe" or ".msi" or ".cmd" or ".bat")) continue;
                hints.Add(relative + Path.GetFileName(file));
                installers++;
            }
            if (depth >= 3) return;
            var children = 0;
            foreach (var child in Directory.EnumerateDirectories(folder))
            {
                if (++seen > 400) return;
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
                children++;
                Walk(child, relative + Path.GetFileName(child) + "/", depth + 1);
            }
            if (installers == 0 && children == 0 && relative.Length > 0) hints.Add(relative.TrimEnd('/'));
        }
    }
}
