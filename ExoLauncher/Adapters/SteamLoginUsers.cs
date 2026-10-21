using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ExoLauncher.Adapters;

/// <summary>
/// The Steam accounts this PC remembers (<c>config/loginusers.vdf</c>) and the
/// switch Steam's own account picker makes: AutoLoginUser and RememberPassword
/// in HKCU, MostRecent/AllowAutoLogin in loginusers.vdf. Exo never reads,
/// stores or types a password or token and never touches config.vdf. Account
/// names and ids stay in this process: the UI gets persona names and a one-way key.
/// </summary>
internal static partial class SteamLoginUsers
{
    internal const ulong SteamId64Base = 76561197960265728UL;

    /// <param name="Remembered">Steam keeps this account's sign-in (RememberPassword); otherwise switching shows Steam's sign-in.</param>
    internal sealed record Account(string SteamId64, string AccountName, string PersonaName, bool MostRecent, bool AllowAutoLogin, bool Remembered = true)
    {
        /// <summary>A one-way tag the UI can send back to pick this account.</summary>
        public string Key => KeyFor(SteamId64);

        /// <summary>The 32-bit account id Steam uses for userdata and ActiveUser.</summary>
        public string? AccountId =>
            ulong.TryParse(SteamId64, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > SteamId64Base
                ? (id - SteamId64Base).ToString(CultureInfo.InvariantCulture)
                : null;
    }

    internal static string KeyFor(string steamId64)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes("steam-login\0" + steamId64));
        return Convert.ToHexString(digest.AsSpan(0, 12)).ToLowerInvariant();
    }

    internal static string PathFor(string steamRoot) => Path.Combine(steamRoot, "config", "loginusers.vdf");

    /// <summary>Every account block in loginusers.vdf, in file order.</summary>
    internal static IReadOnlyList<Account> Parse(string? text)
    {
        var result = new List<Account>();
        if (string.IsNullOrWhiteSpace(text)) return result;
        foreach (var block in Blocks(text))
        {
            var body = text[block.Open..(block.Close + 1)];
            var fields = Fields(body);
            fields.TryGetValue("AccountName", out var name);
            fields.TryGetValue("PersonaName", out var persona);
            if (string.IsNullOrWhiteSpace(name)) continue;
            result.Add(new Account(
                block.SteamId64,
                name,
                string.IsNullOrWhiteSpace(persona) ? name : persona,
                fields.GetValueOrDefault("MostRecent") == "1",
                // Older Steam writes AllowAutoLogin; current Steam writes AutoLogin.
                fields.GetValueOrDefault("AllowAutoLogin") == "1" || fields.GetValueOrDefault("AutoLogin") == "1",
                fields.GetValueOrDefault("RememberPassword", "1") == "1"));
        }
        return result;
    }

    /// <summary>
    /// Marks <paramref name="steamId64"/> MostRecent=1 and AllowAutoLogin=1 and
    /// every other account MostRecent=0, and the chosen account's AutoLogin (current
    /// Steam's name for it) to 1 where it has one. Only those values change; a flag
    /// the chosen account lacks is added as one line in the block's own style.
    /// Returns null when the account is not in the file.
    /// </summary>
    internal static string? SetMostRecent(string text, string steamId64)
    {
        var blocks = Blocks(text).ToList();
        if (!blocks.Any(block => block.SteamId64 == steamId64)) return null;
        var output = new StringBuilder(text.Length + 64);
        var at = 0;
        foreach (var block in blocks)
        {
            output.Append(text, at, block.Open - at);
            var body = text[block.Open..(block.Close + 1)];
            var target = block.SteamId64 == steamId64;
            body = SetFlag(body, "MostRecent", target ? "1" : "0", addWhenMissing: target);
            if (target)
            {
                body = SetFlag(body, "AllowAutoLogin", "1", addWhenMissing: true);
                body = SetFlag(body, "AutoLogin", "1", addWhenMissing: false);
            }
            output.Append(body);
            at = block.Close + 1;
        }
        output.Append(text, at, text.Length - at);
        return output.ToString();
    }

    /// <summary>
    /// The file without <paramref name="steamId64"/>'s block (its id line, braces and
    /// line ending); everything else byte for byte. Null when it is not in the file.
    /// </summary>
    internal static string? RemoveAccount(string text, string steamId64)
    {
        var block = Blocks(text).FirstOrDefault(entry => entry.SteamId64 == steamId64);
        if (block.SteamId64 is null) return null;
        // Back from the brace over the quoted id to the start of its line's indent.
        var start = text.LastIndexOf("\"" + steamId64 + "\"", block.Open, StringComparison.Ordinal);
        while (start > 0 && text[start - 1] is ' ' or '\t') start--;
        var end = block.Close + 1;
        if (end < text.Length && text[end] == '\r') end++;
        if (end < text.Length && text[end] == '\n') end++;
        return text[..start] + text[end..];
    }

    private static string SetFlag(string body, string key, string value, bool addWhenMissing)
    {
        var regex = new Regex("(\"" + key + "\"\\s+\")([^\"]*)(\")", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var match = regex.Match(body);
        if (match.Success)
            return body[..match.Groups[2].Index] + value + body[(match.Groups[2].Index + match.Groups[2].Length)..];
        if (!addWhenMissing) return body;

        // Insert after the last key/value line, copying its indent, separator and line ending.
        var last = KeyValueLineRegex().Matches(body).LastOrDefault();
        if (last is null) return body;
        var newline = body.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var line = last.Groups["indent"].Value + "\"" + key + "\"" + last.Groups["sep"].Value + "\"" + value + "\"";
        var insertAt = last.Index + last.Length;
        return body[..insertAt] + newline + line + body[insertAt..];
    }

    private readonly record struct Block(string SteamId64, int Open, int Close);

    /// <summary>The <c>"7656…" { … }</c> blocks inside <c>"users"</c>, with brace positions.</summary>
    private static IEnumerable<Block> Blocks(string text)
    {
        foreach (Match match in UserOpenRegex().Matches(text))
        {
            var open = match.Index + match.Length - 1;
            var depth = 0;
            for (var i = open; i < text.Length; i++)
            {
                if (text[i] == '"')
                {
                    // Skip quoted strings so a brace in a persona name is not structure.
                    i++;
                    while (i < text.Length && text[i] != '"') { if (text[i] == '\\') i++; i++; }
                    continue;
                }
                if (text[i] == '{') depth++;
                else if (text[i] == '}' && --depth == 0)
                {
                    yield return new Block(match.Groups[1].Value, open, i);
                    break;
                }
            }
        }
    }

    private static Dictionary<string, string> Fields(string body)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in FieldRegex().Matches(body))
            fields[match.Groups[1].Value] = Unescape(match.Groups[2].Value);
        return fields;
    }

    private static string Unescape(string value) =>
        value.Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);

    [GeneratedRegex(@"""(7656\d{13})""\s*\{", RegexOptions.CultureInvariant)]
    private static partial Regex UserOpenRegex();

    [GeneratedRegex(@"""(\w+)""\s+""((?:[^""\\]|\\.)*)""", RegexOptions.CultureInvariant)]
    private static partial Regex FieldRegex();

    [GeneratedRegex(@"(?m)^(?<indent>[ \t]*)""\w+""(?<sep>[ \t]+)""(?:[^""\\\r\n]|\\.)*""[ \t]*(?=\r?$)", RegexOptions.CultureInvariant)]
    private static partial Regex KeyValueLineRegex();

    /// <summary>
    /// The account Steam is signed in as: ActiveUser while Steam runs, else the
    /// auto-login account, else the one marked MostRecent.
    /// </summary>
    internal static Account? Current(IReadOnlyList<Account> accounts, uint activeUser, string? autoLoginUser)
    {
        if (activeUser != 0)
        {
            var id = activeUser.ToString(CultureInfo.InvariantCulture);
            if (accounts.FirstOrDefault(account => account.AccountId == id) is { } active) return active;
        }
        if (!string.IsNullOrWhiteSpace(autoLoginUser) &&
            accounts.FirstOrDefault(account => string.Equals(account.AccountName, autoLoginUser, StringComparison.OrdinalIgnoreCase)) is { } auto)
            return auto;
        return accounts.FirstOrDefault(account => account.MostRecent);
    }

    internal static IReadOnlyList<Account> Load(string steamRoot)
    {
        var text = SteamPlaytime.ReadSharedText(PathFor(steamRoot));
        return Parse(text);
    }

    internal static string? ReadAutoLoginUser()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            return key?.GetValue("AutoLoginUser") as string;
        }
        catch { return null; }
    }

    /// <summary>Steam sets RunningAppID while one of its games runs.</summary>
    internal static bool IsGameRunning()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            return key?.GetValue("RunningAppID") switch
            {
                int value => value != 0,
                long value => value != 0,
                string value => value.Trim() is { Length: > 0 } trimmed && trimmed != "0",
                _ => false,
            };
        }
        catch { return false; }
    }

    /// <summary>Clears the auto-login account so the next Steam start shows its own account picker and sign-in.</summary>
    internal static void ClearAutoLogin()
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Valve\Steam");
        key.SetValue("AutoLoginUser", "", RegistryValueKind.String);
    }

    /// <summary>Puts back the auto-login account an Add account cleared, unless Steam has set one since.</summary>
    internal static void RestoreAutoLogin(string accountName)
    {
        if (!string.IsNullOrEmpty(ReadAutoLoginUser())) return;
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Valve\Steam");
        key.SetValue("AutoLoginUser", accountName, RegistryValueKind.String);
    }

    /// <summary>Forgets an account while Steam is closed (Steam rewrites the file as it exits).</summary>
    internal static bool WriteRemove(string steamRoot, Account account)
    {
        var path = PathFor(steamRoot);
        var text = SteamPlaytime.ReadSharedText(path);
        if (text is null || RemoveAccount(text, account.SteamId64) is not { } next) return false;
        WriteAtomically(path, next);
        if (string.Equals(ReadAutoLoginUser(), account.AccountName, StringComparison.OrdinalIgnoreCase)) ClearAutoLogin();
        return true;
    }

    private static void WriteAtomically(string path, string text)
    {
        var tmp = path + ".exo-tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// Writes the switch while Steam is closed. loginusers.vdf is replaced
    /// atomically next to itself; the registry values are the auto-login pair.
    /// </summary>
    internal static bool WriteSwitch(string steamRoot, Account account)
    {
        var path = PathFor(steamRoot);
        var text = SteamPlaytime.ReadSharedText(path);
        if (text is null) return false;
        var next = SetMostRecent(text, account.SteamId64);
        if (next is null) return false;
        if (!string.Equals(next, text, StringComparison.Ordinal)) WriteAtomically(path, next);

        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Valve\Steam");
        key.SetValue("AutoLoginUser", account.AccountName, RegistryValueKind.String);
        key.SetValue("RememberPassword", 1, RegistryValueKind.DWord);
        return true;
    }
}
