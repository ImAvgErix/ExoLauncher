using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>
/// The library keeps playable titles. Marketplace junk, engines, and
/// extras stay out unless the user asks for tools.
/// </summary>
internal static class CatalogHygiene
{
    private static readonly string[] MarketplaceNeedles =
    [
        "actor locker", "auto footstep", "async loading", "ai for npc",
        "metahuman", "loading screen", "unreal engine", "wait for players",
        "plugin", "sample", "starter game", "template", "toolkit", " tool",
        "system c++", "c++", "blueprint", "framework", "editor",
        "sequence manager", "montage sequence", "electric dreams", "code flow",
        "optimisation", "optimization", "shader",
        "steamworks", "redistributable",
        "quixel", "megascan", "twinmotion", "lyra starter",
        "city sample", "content example", "starter content",
        "vehicle variety", "fab plugin", "unreal marketplace",
    ];

    private static readonly string[] ToolNeedles =
    [
        "soundtrack", "dedicated server", " server dedicated",
        " - tools", " playtest", "content creator",
        "software development kit", " sdk ", " server ", " dedserver ",
    ];

    private static readonly string[] EpicDropCategories =
    [
        "engine", "engines", "plugin", "plugins", "editor", "editors", "software",
        "assets", "asset", "addons", "digitalextras", "digital extras",
    ];

    /// <summary>
    /// Title words that mark companion content rather than a playable game on an
    /// owned-not-installed shelf. Matched on word boundaries, case-insensitive.
    /// </summary>
    private static readonly string[] OwnedExtraPhrases =
    [
        "bonus content", "artbook", "art book", "soundtrack", "original soundtrack",
        "sdk", "dev kit", "devkit", "development kit", "level editor", "mod tools",
        "modding tools", "mod kit", "modkit", "mod uploader", "benchmark", "test server",
        "public test", "playtest", "dedicated server", "dedserver", "closed beta", "open beta",
        "beta test", "remote console", "redistributable", "redistributables", "demo",
        "digital extras", "making of", "strategy guide",
    ];

    /// <summary>Extras words that are only a marker at the end of a title ("Trailer Park" is a game).</summary>
    private static readonly string[] OwnedExtraSuffixWords =
    [
        "ost", "editor", "server", "trailer", "trailers", "wallpaper", "wallpapers", "tools",
    ];

    /// <summary>
    /// Steam app ids that are Valve/Steamworks plumbing, SDKs, or test apps even
    /// though some carry <c>type=game</c> (480 Spacewar is the Steamworks sample).
    /// </summary>
    private static readonly HashSet<string> SteamNonGameAppIds = new(StringComparer.Ordinal)
    {
        "7", "753", "760", "480", "481", "482", "205", "211", "215", "218", "310", "1007",
        "228980", "243730", "243750", "250820", "1070560", "1391110", "1493710", "1628350",
        "1826330", "1887720", "2180100", "2805730", "4690330",
    };

    public static string NormalizeTitleKey(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        var form = title.Normalize(System.Text.NormalizationForm.FormD);
        var builder = new System.Text.StringBuilder(form.Length);
        foreach (var character in form)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(character) ==
                System.Globalization.UnicodeCategory.NonSpacingMark)
                continue;
            if (char.IsLetterOrDigit(character)) builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }

    public static bool IsMarketplaceOrEngineTitle(string? title)
    {
        var t = (title ?? "").ToLowerInvariant();
        if (t.Length == 0) return false;
        var packed = NormalizeTitleKey(title);
        if (packed is "discord" or "epiconlineservices" or "eosoverlay")
            return true;
        foreach (var needle in MarketplaceNeedles)
        {
            if (ContainsNeedleSpaceInsensitive(t, needle)) return true;
        }

        return false;
    }

    public static bool IsToolOrExtraTitle(string? title)
    {
        if (IsDemoTitle(title)) return true;
        var t = (" " + (title ?? "") + " ").ToLowerInvariant();
        foreach (var needle in ToolNeedles)
        {
            if (t.Contains(needle, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    public static bool IsDemoTitle(string? title)
    {
        if (string.IsNullOrEmpty(title)) return false;
        var remaining = title.AsSpan();
        var offset = 0;
        while (remaining.IndexOf("demo", StringComparison.OrdinalIgnoreCase) is var relative && relative >= 0)
        {
            var start = offset + relative;
            var end = start + 4;
            if ((start == 0 || !char.IsLetterOrDigit(title[start - 1])) &&
                (end == title.Length || !char.IsLetterOrDigit(title[end]))) return true;
            offset = end;
            remaining = title.AsSpan(offset);
        }
        return false;
    }

    public static bool IsDedicatedServerTitle(string? title)
    {
        var t = (" " + (title ?? "") + " ").ToLowerInvariant();
        return t.Contains("dedicated server", StringComparison.Ordinal)
               || t.Contains(" server dedicated", StringComparison.Ordinal);
    }

    /// <summary>
    /// Steam type filter. Demos, DLC, advertising, and dedicated servers never
    /// stay. Tools and soundtracks need Show tools; applications remain in the library. Empty type
    /// may stay only on an installed ACF row; uninstalled owned rows need a
    /// real <c>game</c> type.
    /// </summary>
    public static bool KeepSteamType(string? type, bool showTools, bool installed = true)
    {
        var t = (type ?? "").Trim().ToLowerInvariant();
        if (t.Length == 0) return installed;
        if (t is "game") return true;
        if (t is "demo") return false;
        if (t is "dlc" or "advertising") return false;
        if (t is "dedicated server" or "dedicatedserver" or "dedicated_server") return false;
        if (t is "application") return true;
        if (t is "tool" or "music") return showTools;
        return false;
    }

    /// <summary>
    /// Companion content (bonus content, soundtrack, SDK, editor, test server,
    /// wallpaper, trailer, " - Tools" …) on an owned-not-installed shelf.
    /// </summary>
    public static bool IsOwnedExtraTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return true;
        var words = WordsOf(title);
        if (words.Count == 0) return true;
        var last = words[^1];
        if (OwnedExtraSuffixWords.Any(word => string.Equals(word, last, StringComparison.Ordinal)))
            return true;
        var packed = " " + string.Join(' ', words) + " ";
        foreach (var phrase in OwnedExtraPhrases)
        {
            if (packed.Contains(" " + phrase + " ", StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>Lower-case letter/digit words; punctuation (including ™ ® : - –) separates words.</summary>
    private static List<string> WordsOf(string title)
    {
        var words = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var character in title.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                current.Append(character);
                continue;
            }
            if (current.Length > 0) { words.Add(current.ToString()); current.Clear(); }
        }
        if (current.Length > 0) words.Add(current.ToString());
        return words;
    }

    public static bool IsSteamNonGameAppId(string? appId) =>
        !string.IsNullOrWhiteSpace(appId) && SteamNonGameAppIds.Contains(appId.Trim());

    /// <summary>
    /// The only Steam rows allowed on the owned-not-installed shelf: appinfo
    /// <c>common/type</c> is exactly <c>game</c>, the app has no
    /// <c>common/parent</c> (companion content), it is not Steamworks plumbing,
    /// and its title is not an extra. Installed rows never use this rule.
    /// </summary>
    public static bool IsOwnedUninstalledSteamGame(string? appId, string? title, string? type, string? parentAppId)
    {
        if (!string.Equals((type ?? "").Trim(), "game", StringComparison.OrdinalIgnoreCase)) return false;
        var parent = (parentAppId ?? "").Trim();
        if (parent.Length > 0 && parent != "0" && !string.Equals(parent, appId?.Trim(), StringComparison.Ordinal))
            return false;
        if (IsSteamNonGameAppId(appId)) return false;
        return !IsOwnedExtraTitle(title);
    }

    public static bool KeepEpicCategories(IReadOnlyList<string> categories)
    {
        if (categories.Count == 0) return false;
        foreach (var category in categories)
        {
            var root = category.Split('/', 2)[0].Trim();
            if (EpicDropCategories.Any(drop => string.Equals(drop, category, StringComparison.OrdinalIgnoreCase) ||
                                               string.Equals(drop, root, StringComparison.OrdinalIgnoreCase)))
                return false;
        }

        return categories.Any(category =>
            string.Equals(category, "game", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(category, "games", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Empty Epic categories are not proof of a game. Installed rows may stay
    /// when the title does not look like an asset pack.
    /// </summary>
    public static bool KeepEpicRow(string? title, IReadOnlyList<string> categories, bool installed = false)
    {
        if (IsMarketplaceOrEngineTitle(title)) return false;
        if (NormalizeTitleKey(title) is "emergence") return false;
        if (IsDemoTitle(title) || IsDedicatedServerTitle(title) || IsToolOrExtraTitle(title))
            return false;
        if (!installed && IsOwnedExtraTitle(title)) return false;
        if (categories.Count == 0)
            return installed;
        return KeepEpicCategories(categories);
    }

    public static bool KeepLibraryEntry(GameEntry game, bool showTools)
    {
        var confirmedSteamProduct = game.Store == StoreKind.Steam &&
            (string.Equals(game.SteamAppType, "game", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(game.SteamAppType, "application", StringComparison.OrdinalIgnoreCase));
        if (!confirmedSteamProduct && IsMarketplaceOrEngineTitle(game.Title)) return false;
        if (IsDemoTitle(game.Title)) return false;
        if (!showTools && IsDedicatedServerTitle(game.Title)) return false;
        if (game.Store == StoreKind.Steam &&
            !KeepSteamType(game.SteamAppType, showTools, game.Installed))
            return false;
        if (IsOwnedNotInstalledShelfRow(game))
        {
            // Owned-not-installed is games only, whatever Show tools says.
            // Steam rows without a manifest path came from the account's
            // license list or the ownership catalog, never from local files.
            if (game.Store == StoreKind.Steam &&
                !IsOwnedUninstalledSteamGame(game.LaunchTarget, game.Title, game.SteamAppType, game.ParentAppId))
                return false;
            if (IsOwnedExtraTitle(game.Title)) return false;
        }
        if (!showTools && IsToolOrExtraTitle(game.Title)) return false;
        return true;
    }

    /// <summary>
    /// A row offered only because the account owns it: not installed and not a
    /// partially installed/manifest-backed row (those carry a path).
    /// </summary>
    private static bool IsOwnedNotInstalledShelfRow(GameEntry game) =>
        !game.Installed &&
        game.Store != StoreKind.Local &&
        string.IsNullOrWhiteSpace(game.Path) &&
        game.EntitlementState != EntitlementState.NotOwned;

    /// <summary>
    /// One row per store id (steam:&lt;appid&gt;, epic:&lt;AppName&gt;). Two apps that
    /// share a title (Prey 2006 and Prey 2017) are two games; rows with no
    /// title are dropped.
    /// </summary>
    public static IReadOnlyList<GameEntry> DeduplicateById(IReadOnlyList<GameEntry> games)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<GameEntry>(games.Count);
        foreach (var game in games.OrderByDescending(row => row.Installed)
                     .ThenByDescending(row => row.Owned))
        {
            if (NormalizeTitleKey(game.Title).Length == 0 || !seen.Add(game.Id)) continue;
            kept.Add(game);
        }

        return kept;
    }

    /// <summary>
    /// Marketplace needles match with or without spaces so ActorLocker equals
    /// Actor Locker. Leading-space word needles such as " tool" stay exact so
    /// they do not collapse onto Toolkit.
    /// </summary>
    private static bool ContainsNeedleSpaceInsensitive(string haystack, string needle)
    {
        if (haystack.Contains(needle, StringComparison.Ordinal)) return true;
        var compactNeedle = StripSpaces(needle);
        if (compactNeedle.Length == 0 ||
            compactNeedle.Equals(needle.Trim(), StringComparison.Ordinal))
            return false;
        return StripSpaces(haystack).Contains(compactNeedle, StringComparison.Ordinal);
    }

    private static string StripSpaces(string value)
    {
        var chars = value.Where(static c => !char.IsWhiteSpace(c)).ToArray();
        return new string(chars);
    }
}
