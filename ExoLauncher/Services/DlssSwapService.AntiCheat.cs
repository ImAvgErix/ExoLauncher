using System.Text.RegularExpressions;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>
/// Exo does not change a file in a game that runs anti-cheat unless the user
/// allowed it for that game (settings <c>upscalerAntiCheatConsent</c>), after
/// being told which anti-cheat it is and that a swapped DLL can get the
/// account banned. The consent names the anti-cheat it was given for and
/// lapses when a different one is detected. Riot games and games with Riot
/// Vanguard (kernel anti-cheat) are never changed, consent or not. Detection
/// fails closed: a store, a title, a Riot install folder or a single marker
/// file anywhere in the install is enough, and an install folder Exo cannot
/// read counts as protected with no way to allow it.
/// </summary>
public sealed partial class DlssSwapService
{
    /// <summary>Folder names, matched whole and anywhere in the install.</summary>
    private static readonly (string Name, string AntiCheat)[] AntiCheatFolders =
    [
        ("EasyAntiCheat", "Easy Anti-Cheat"),
        ("EasyAntiCheat_EOS", "Easy Anti-Cheat"),
        ("BattlEye", "BattlEye"),
        ("AntiCheatExpert", "Anti-Cheat Expert"),
        ("GameGuard", "nProtect GameGuard"),
        ("nProtect", "nProtect GameGuard"),
        ("XIGNCODE", "XIGNCODE3"),
        ("XignCode", "XIGNCODE3"),
        ("EAAntiCheat", "EA Javelin"),
        ("EQU8", "EQU8"),
        ("PunkBuster", "PunkBuster"),
        ("Denuvo Anti-Cheat", "Denuvo Anti-Cheat"),
    ];

    /// <summary>File-name prefixes, matched anywhere in the install.</summary>
    private static readonly (string Prefix, string AntiCheat)[] AntiCheatFiles =
    [
        ("start_protected_game", "Easy Anti-Cheat"),
        ("easyanticheat", "Easy Anti-Cheat"),
        ("eac_launcher", "Easy Anti-Cheat"),
        ("beservice", "BattlEye"),
        ("beclient", "BattlEye"),
        ("bedaisy", "BattlEye"),
        ("eaanticheat", "EA Javelin"),
        ("vgk.sys", RiotVanguard),
        ("vgc.exe", RiotVanguard),
        ("pnkbstr", "PunkBuster"),
        ("gameguard.des", "nProtect GameGuard"),
        ("x3.xem", "XIGNCODE3"),
        ("mhyprot", "HoYoverse anti-cheat"),
        ("mhypbase", "HoYoverse anti-cheat"),
        ("sguard", "Anti-Cheat Expert"),
        ("ace-base", "Anti-Cheat Expert"),
        ("neacclient", "NetEase anti-cheat"),
        ("neacsafe", "NetEase anti-cheat"),
        ("randgrid", "Ricochet"),
        ("equ8", "EQU8"),
        ("denuvo-anticheat", "Denuvo Anti-Cheat"),
        ("faceit", "FACEIT"),
    ];

    /// <summary>
    /// Titles whose anti-cheat lives outside the install folder (Vanguard,
    /// Ricochet, Javelin, VAC) or is loaded by a launcher. Whole words only, so
    /// "Rust" does not catch "Rusty Lake".
    /// </summary>
    private static readonly (Regex Title, string AntiCheat)[] AntiCheatTitles =
    [
        (Word("valorant|league of legends|teamfight tactics|legends of runeterra|2xko"), RiotVanguard),
        (Word("call of duty|warzone"), "Ricochet"),
        (Word("battlefield"), "EA Javelin"),
        (Word("fortnite|apex legends|the finals|dead by daylight|elden ring|hunt: showdown|rust|multiversus|predecessor|lost ark|new world|fall guys|halo infinite"), "Easy Anti-Cheat"),
        (Word("pubg|playerunknown's battlegrounds|rainbow six|escape from tarkov|destiny 2|dayz|arma 3|arma reforger|the division|for honor|splitgate"), "BattlEye"),
        (Word("counter-strike|dota 2|deadlock"), "Valve Anti-Cheat"),
        (Word("overwatch"), "Blizzard anti-cheat"),
        (Word("marvel rivals|naraka"), "NetEase anti-cheat"),
        (Word("delta force|arena breakout"), "Anti-Cheat Expert"),
        (Word("genshin impact|honkai: star rail|zenless zone zero"), "HoYoverse anti-cheat"),
        (Word("wuthering waves|throne and liberty|black desert"), "Kernel anti-cheat"),
    ];

    private static Regex Word(string alternatives) => new(
        @"(?<![\p{L}\p{N}])(?:" + alternatives + @")(?![\p{L}\p{N}])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static string? AntiCheatForFolder(string folderName)
    {
        foreach (var (name, antiCheat) in AntiCheatFolders)
            if (folderName.Equals(name, StringComparison.OrdinalIgnoreCase)) return antiCheat;
        return null;
    }

    internal static string? AntiCheatForFile(string fileName)
    {
        foreach (var (prefix, antiCheat) in AntiCheatFiles)
            if (fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return antiCheat;
        return null;
    }

    internal const string RiotVanguard = "Riot Vanguard";

    /// <summary>The store, title and Riot folder half; the install half comes from the scan.</summary>
    internal static string? AntiCheatForGame(GameEntry game)
    {
        if (game.Store == StoreKind.Riot) return RiotVanguard;
        if ((game.Path ?? string.Empty).Replace('/', '\\').Contains("\\Riot Games\\", StringComparison.OrdinalIgnoreCase))
            return RiotVanguard;
        var title = game.Title ?? string.Empty;
        foreach (var (pattern, antiCheat) in AntiCheatTitles)
            if (pattern.IsMatch(title)) return antiCheat;
        return null;
    }

    /// <summary>The install could not be read, so it cannot be cleared of anti-cheat.</summary>
    internal const string UnreadableInstall = "unreadable";

    /// <summary>
    /// Riot games, Riot Vanguard (kernel anti-cheat) and installs Exo cannot
    /// read: no consent lifts these.
    /// </summary>
    internal static bool NeverAllowed(GameEntry game, string antiCheat) =>
        game.Store == StoreKind.Riot || antiCheat is RiotVanguard or UnreadableInstall;

    /// <summary>The user's consent counts only for the anti-cheat it was given for.</summary>
    internal static bool Accepted(GameEntry game, string antiCheat, string? consented) =>
        !NeverAllowed(game, antiCheat) && consented is not null &&
        string.Equals(antiCheat, consented, StringComparison.OrdinalIgnoreCase);

    /// <summary><c>never</c>, <c>available</c> (the user may allow it) or <c>given</c>; null without anti-cheat.</summary>
    internal static string? ConsentState(GameEntry game, string? antiCheat, string? consented) =>
        antiCheat is null ? null
        : NeverAllowed(game, antiCheat) ? "never"
        : Accepted(game, antiCheat, consented) ? "given"
        : "available";

    internal static string AntiCheatMessage(GameEntry game, string antiCheat, string? consented)
    {
        if (antiCheat == UnreadableInstall) return "Exo could not check this game's folder for anti-cheat, so it changes nothing.";
        if (NeverAllowed(game, antiCheat)) return $"This game uses {RiotVanguard}. Exo never changes Riot games or games with Vanguard.";
        return Accepted(game, antiCheat, consented)
            ? $"This game uses {antiCheat}. You allowed Exo to change its upscaler files. Any ban is your risk."
            : $"This game uses {antiCheat}. Exo changes nothing here unless you allow it for this game.";
    }
}
