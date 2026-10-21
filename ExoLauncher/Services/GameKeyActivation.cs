using System.Text;
using System.Text.RegularExpressions;

namespace ExoLauncher.Services;

/// <summary>
/// Product-key shapes and official redeem URLs. Exo never redeems the code
/// itself — Steam / Epic / GOG own that step, and each asks the user to
/// confirm it (Steam's subscriber agreement, GOG's Continue and Redeem).
/// Steam and GOG pages take the key in the URL; Epic's page does not.
/// </summary>
internal static class GameKeyActivation
{
    private static readonly Regex SteamKey = new(
        @"^[A-Z0-9]{5}(?:-[A-Z0-9]{5}){2,4}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Steam's own key page, opened in the signed-in Steam client.</summary>
    public const string SteamRegisterKey = "steam://openurl/https://store.steampowered.com/account/registerkey?key=";
    public const string EpicWeb = "https://store.epicgames.com/redeem";
    public const string GogRedeem = "https://www.gog.com/redeem/";

    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var chars = new StringBuilder(raw.Length);
        foreach (var ch in raw.Trim().ToUpperInvariant())
        {
            if (char.IsAsciiLetterOrDigit(ch))
                chars.Append(ch);
            else if ((ch is '-' or ' ' or '\t') && chars.Length > 0 && chars[^1] != '-')
                chars.Append('-');
        }

        return chars.ToString().Trim('-');
    }

    public static string DetectStore(string? raw)
    {
        var key = Normalize(raw);
        if (key.Length == 0) return "unknown";
        if (key.StartsWith("GOG", StringComparison.Ordinal) && key.Length >= 10)
            return "gog";
        if (key.StartsWith("EPIC", StringComparison.Ordinal))
            return "epic";
        if (SteamKey.IsMatch(key))
            return "steam";
        return "unknown";
    }

    public static bool LooksLikeKey(string? raw)
    {
        var store = DetectStore(raw);
        return store is "steam" or "epic" or "gog";
    }

    public static string ResolveStore(string? raw, string? overrideStore)
    {
        var chosen = (overrideStore ?? "").Trim().ToLowerInvariant();
        if (chosen is "steam" or "epic" or "gog") return chosen;
        var detected = DetectStore(raw);
        return detected == "unknown" ? "steam" : detected;
    }

    /// <summary>The store's redeem page; <paramref name="key"/> must already be <see cref="Normalize"/>d.</summary>
    public static string RedeemUrl(string store, string key) =>
        store.Trim().ToLowerInvariant() switch
        {
            "epic" => EpicWeb,
            "gog" => GogRedeem + Uri.EscapeDataString(key),
            _ => SteamRegisterKey + Uri.EscapeDataString(key),
        };
}
