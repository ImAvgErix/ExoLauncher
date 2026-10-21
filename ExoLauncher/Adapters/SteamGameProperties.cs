using System.Globalization;
using System.Text.Json;

namespace ExoLauncher.Adapters;

/// <summary>
/// One of the game's DLC. <see cref="Owned"/> is null when Steam is not there to ask;
/// <see cref="Toggle"/> is a DLC with its own files that this account holds, the only
/// kind Steam lets a player switch off.
/// </summary>
internal sealed record SteamDlcContent(uint Id, string Name, long? SizeBytes, bool Enabled, bool Installed, bool? Owned, bool Toggle);

/// <summary>A branch the game's Betas list offers; <see cref="Name"/> "" is the default branch.</summary>
internal sealed record SteamBranch(string Name, string Description);

/// <summary>A language the game offers, Steam's ELanguage code, and the bytes of its own depots.</summary>
internal sealed record SteamLanguage(string Name, int Code, long DepotBytes);

/// <summary>What <c>appmanifest_&lt;id&gt;.acf</c> holds for a game's Properties (Steam writes it; Exo only reads).</summary>
internal sealed record SteamManifestConfig(
    long? SizeOnDisk,
    int AutoUpdate,
    int Background,
    string Language,
    string Beta,
    IReadOnlySet<uint> DisabledDlc,
    IReadOnlySet<uint> InstalledDlc,
    IReadOnlySet<uint> InstalledDepots);

/// <summary>The helper's <c>props</c> answer: what the running Steam client holds now.</summary>
internal sealed record SteamLiveConfig(
    int? AutoUpdate,
    int? Background,
    int? Language,
    string? Beta,
    bool? Cloud,
    bool? CloudAccount,
    int? Folder,
    IReadOnlyList<string> Folders,
    long? MoveDone,
    long? MoveTotal,
    IReadOnlyDictionary<uint, bool> DlcEnabled,
    IReadOnlyDictionary<uint, bool>? DlcInstalled = null);

/// <summary>
/// Steam Properties, read the way Steam's own page reads them: the manifest for
/// what is installed, appinfo for the DLC, branches and languages the game
/// offers, the live client for what it holds right now.
/// </summary>
internal static class SteamGameProperties
{
    /// <summary>Steam's ELanguage order (steamclient64's language table; the code is the index).</summary>
    internal static readonly string[] LanguageCodes =
    [
        "english", "german", "french", "italian", "koreana", "spanish", "schinese", "tchinese", "russian", "thai",
        "japanese", "portuguese", "polish", "danish", "dutch", "finnish", "norwegian", "swedish", "hungarian", "czech",
        "romanian", "turkish", "brazilian", "bulgarian", "greek", "arabic", "ukrainian", "latam", "vietnamese",
        "sc_schinese", "indonesian",
    ];

    private static readonly Dictionary<string, string> LanguageNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["english"] = "English", ["german"] = "German", ["french"] = "French", ["italian"] = "Italian",
        ["koreana"] = "Korean", ["spanish"] = "Spanish (Spain)", ["schinese"] = "Simplified Chinese",
        ["tchinese"] = "Traditional Chinese", ["russian"] = "Russian", ["thai"] = "Thai", ["japanese"] = "Japanese",
        ["portuguese"] = "Portuguese (Portugal)", ["polish"] = "Polish", ["danish"] = "Danish", ["dutch"] = "Dutch",
        ["finnish"] = "Finnish", ["norwegian"] = "Norwegian", ["swedish"] = "Swedish", ["hungarian"] = "Hungarian",
        ["czech"] = "Czech", ["romanian"] = "Romanian", ["turkish"] = "Turkish", ["brazilian"] = "Portuguese (Brazil)",
        ["bulgarian"] = "Bulgarian", ["greek"] = "Greek", ["arabic"] = "Arabic", ["ukrainian"] = "Ukrainian",
        ["latam"] = "Spanish (Latin America)", ["vietnamese"] = "Vietnamese", ["indonesian"] = "Indonesian",
    };

    internal static string LanguageName(string code) => LanguageNames.GetValueOrDefault(code) ?? code;

    internal static int LanguageCode(string name) =>
        Array.FindIndex(LanguageCodes, code => code.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The game's <c>appmanifest_&lt;id&gt;.acf</c> in Steam's own folder or one of the
    /// library folders <c>libraryfolders.vdf</c> lists; null when none has it.
    /// </summary>
    internal static string? FindManifest(string steamRoot, string appId, string? libraryFolders, Func<string, bool> fileExists)
    {
        var folders = new List<string> { steamRoot };
        foreach (var (_, folder) in SteamKeyValues.ParseText(libraryFolders ?? "").Section("libraryfolders").Sections)
            if (folder.Value("path") is { Length: > 0 } path && !folders.Contains(path, StringComparer.OrdinalIgnoreCase))
                folders.Add(path);
        return folders
            .Select(folder => Path.Combine(folder, "steamapps", $"appmanifest_{appId}.acf"))
            .FirstOrDefault(fileExists);
    }

    internal static SteamManifestConfig ReadManifest(string acf)
    {
        var state = SteamKeyValues.ParseText(acf).Section("AppState");
        var user = state.Section("UserConfig");
        var mounted = state.Section("MountedConfig");
        var disabled = Ids(user.Value("DisabledDLC"));
        // Steam keeps the choice in UserConfig and what is mounted in MountedConfig;
        // until it applies a change the two differ, and the choice is what counts.
        if (user.Value("DisabledDLC") is null) disabled = Ids(mounted.Value("DisabledDLC"));
        var installedDlc = new HashSet<uint>();
        var installedDepots = new HashSet<uint>();
        foreach (var (key, depot) in state.Section("InstalledDepots").Sections)
        {
            if (uint.TryParse(key, out var depotId)) installedDepots.Add(depotId);
            if (uint.TryParse(depot.Value("dlcappid"), out var dlc) && dlc != 0) installedDlc.Add(dlc);
        }
        return new SteamManifestConfig(
            state.Number("SizeOnDisk"),
            (int)(state.Number("AutoUpdateBehavior") ?? 0),
            (int)(state.Number("AllowOtherDownloadsWhileRunning") ?? 0),
            user.Value("language") ?? mounted.Value("language") ?? "",
            user.Value("BetaKey") ?? mounted.Value("BetaKey") ?? "",
            disabled,
            installedDlc,
            installedDepots);
    }

    /// <summary>
    /// Every DLC the game lists (<c>extended/listofdlc</c>) that Steam has a record
    /// of or the account holds, the account's own first. For a DLC without files
    /// Steam's <c>BIsAppDlcInstalled</c> answers whether the account (or its Steam
    /// Family) holds it; for one with files, whether they are on disk, which the
    /// manifest also says without the client. Only a DLC with depots of its own (in
    /// the game's depots, or in the DLC's record when the game says
    /// <c>hasdepotsindlc</c>) that the account holds gets a switch, as in Steam.
    /// A DLC with neither a record nor a license is unreleased or hidden: left out.
    /// </summary>
    internal static IReadOnlyList<SteamDlcContent> DlcContent(
        SteamKeyValues app,
        IReadOnlyDictionary<uint, SteamKeyValues> dlcRecords,
        SteamManifestConfig manifest,
        SteamLiveConfig? live)
    {
        var result = new List<SteamDlcContent>();
        var depots = app.Section("depots");
        var inDlc = depots.Value("hasdepotsindlc") == "1";
        foreach (var dlc in DlcIds(app))
        {
            var record = dlcRecords.GetValueOrDefault(dlc);
            var sizes = ContentDepots(depots, dlc, manifest.Language, ownRecord: false).ToList();
            if (record is not null && (inDlc || sizes.Count == 0))
                sizes.AddRange(ContentDepots(record.Section("depots"), dlc, manifest.Language, ownRecord: true));
            var installed = manifest.InstalledDlc.Contains(dlc);
            var disabled = manifest.DisabledDlc.Contains(dlc);
            bool? held = live?.DlcInstalled is { } answers && answers.TryGetValue(dlc, out var answer) ? answer : null;
            // A DLC with files that is not on disk may be owned and waiting to download: unknown, not "not owned".
            bool? owned = installed || disabled || held == true ? true : held == false && sizes.Count == 0 ? false : null;
            if (record is null && owned != true) continue;
            var enabled = live?.DlcEnabled.TryGetValue(dlc, out var on) == true ? on : !disabled;
            var name = record?.Section("common").Value("name") is { Length: > 0 } title ? title.Trim() : $"DLC {dlc}";
            // Depots of equal size are the same content for different buyers or
            // regions; Steam installs one of them.
            var bytes = sizes.Distinct().Sum();
            result.Add(new SteamDlcContent(dlc, name, bytes > 0 ? bytes : null, enabled, installed, owned,
                Toggle: sizes.Count > 0 && owned == true));
        }
        return result
            .OrderByDescending(dlc => dlc.Toggle)
            .ThenByDescending(dlc => dlc.Owned == true)
            .ThenBy(dlc => dlc.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // The game's DLC list, and every DLC one of its depots belongs to: content packs such as Call of Duty's Multiplayer
    // and Zombies (over 200 GB together) are only in the depots, and Steam's Installed Content lists them from there.
    internal static IEnumerable<uint> DlcIds(SteamKeyValues app) =>
        Ids(app.Section("extended").Value("listofdlc"))
            .Concat(app.Section("depots").Sections.Select(depot => depot.Section.Number("dlcappid")).OfType<long>().Where(id => id is > 0 and <= uint.MaxValue).Select(id => (uint)id))
            .Distinct()
            .Order();

    /// <summary>
    /// Public sizes of the depots that belong to <paramref name="dlc"/> and would
    /// install on this PC. In the game's record those carry <c>dlcappid</c>; in the
    /// DLC's own record (<paramref name="ownRecord"/>) every depot is the DLC's.
    /// </summary>
    private static IEnumerable<long> ContentDepots(SteamKeyValues depots, uint dlc, string language, bool ownRecord)
    {
        var id = dlc.ToString(CultureInfo.InvariantCulture);
        foreach (var (key, depot) in depots.Sections)
        {
            if (!uint.TryParse(key, out _)) continue;
            var owner = depot.Value("dlcappid");
            if (owner is null ? !ownRecord : owner != id) continue;
            if (!InstallsHere(depot.Section("config"), language)) continue;
            if (long.TryParse(depot.Section("manifests/public").Value("size"), out var size) && size > 0) yield return size;
        }
    }

    private static bool InstallsHere(SteamKeyValues config, string language)
    {
        if (config.Value("oslist") is { Length: > 0 } os && !os.Contains("windows", StringComparison.OrdinalIgnoreCase)) return false;
        if (config.Value("osarch") is "32") return false;
        return config.Value("language") is not { Length: > 0 } depotLanguage ||
               depotLanguage.Equals(language is { Length: > 0 } ? language : "english", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The branches Steam's Betas list offers: the default, every open branch, and a coded one only once it is current.</summary>
    internal static IReadOnlyList<SteamBranch> Branches(SteamKeyValues app, string current)
    {
        var result = new List<SteamBranch> { new("", "") };
        foreach (var (name, branch) in app.Section("depots/branches").Sections)
        {
            if (name.Equals("public", StringComparison.OrdinalIgnoreCase)) continue;
            var coded = branch.Value("pwdrequired") == "1";
            if (coded && !name.Equals(current, StringComparison.OrdinalIgnoreCase)) continue;
            result.Add(new SteamBranch(name, branch.Value("description")?.Trim() ?? ""));
        }
        if (current.Length > 0 && result.All(branch => !branch.Name.Equals(current, StringComparison.OrdinalIgnoreCase)))
            result.Add(new SteamBranch(current, ""));
        return result;
    }

    /// <summary>
    /// The languages the game supports (<c>common/supported_languages</c>, else the
    /// depots' <c>baselanguages</c>) that Steam has a code for, and each one's own depot bytes.
    /// </summary>
    internal static IReadOnlyList<SteamLanguage> Languages(SteamKeyValues app)
    {
        var names = app.Section("common/supported_languages").Sections
            .Where(entry => entry.Section.Value("supported") is not ("false" or "0"))
            .Select(entry => entry.Key)
            .ToList();
        if (names.Count == 0)
            names = (app.Section("depots").Value("baselanguages") ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var depots = app.Section("depots");
        return names
            .Select(name => (name, code: LanguageCode(name)))
            .Where(entry => entry.code >= 0)
            .DistinctBy(entry => entry.code)
            .Select(entry => new SteamLanguage(LanguageCodes[entry.code], entry.code, depots.Sections
                .Where(depot => uint.TryParse(depot.Key, out _) && depot.Section.Value("dlcappid") is null &&
                                (depot.Section.Section("config").Value("language") ?? "").Equals(entry.name, StringComparison.OrdinalIgnoreCase))
                .Sum(depot => long.TryParse(depot.Section.Section("manifests/public").Value("size"), out var size) ? size : 0)))
            .ToList();
    }

    /// <summary>Parses the helper's <c>props</c> line; null when it is not an answer.</summary>
    internal static SteamLiveConfig? ParseLive(string? output)
    {
        var line = output?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(text => text.StartsWith('{'));
        if (line is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True) return null;
            int? Int(string name) => root.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : null;
            long? Long(string name) => root.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : null;
            bool? Bool(string name) => root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? value.GetBoolean() : null;
            var folders = root.TryGetProperty("folders", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString() ?? "").Take(64).ToList()
                : [];
            var dlc = new Dictionary<uint, bool>();
            var installed = new Dictionary<uint, bool>();
            if (root.TryGetProperty("dlc", out var items) && items.ValueKind == JsonValueKind.Array)
                foreach (var item in items.EnumerateArray())
                {
                    if (!item.TryGetProperty("id", out var id) || !id.TryGetUInt32(out var dlcId)) continue;
                    if (item.TryGetProperty("enabled", out var enabled) && enabled.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        dlc[dlcId] = enabled.GetBoolean();
                    if (item.TryGetProperty("installed", out var held) && held.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        installed[dlcId] = held.GetBoolean();
                }
            return new SteamLiveConfig(
                Int("autoUpdate"), Int("background"), Int("language"),
                root.TryGetProperty("beta", out var beta) && beta.ValueKind == JsonValueKind.String ? beta.GetString() : null,
                Bool("cloud"), Bool("cloudAccount"), Int("folder"), folders, Long("moveDone"), Long("moveTotal"), dlc, installed);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static HashSet<uint> Ids(string? list)
    {
        var ids = new HashSet<uint>();
        foreach (var token in (list ?? "").Split([',', ' '], StringSplitOptions.RemoveEmptyEntries))
            if (uint.TryParse(token, out var id) && id != 0) ids.Add(id);
        return ids;
    }
}
