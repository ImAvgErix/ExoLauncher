using System.Text.RegularExpressions;

namespace ExoLauncher.Adapters.Cli;

/// <summary>
/// Steam defers some installed patches with <c>ScheduledAutoUpdate</c>. Clearing that
/// field on an identity-verified appmanifest is the local equivalent of "update now"
/// without clicking another game's Downloads row.
/// </summary>
public static class SteamAppManifestSchedule
{
    private static readonly Regex ScheduledField = new(
        "\"ScheduledAutoUpdate\"\\s+\"[^\"]*\"",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    public static bool TryClearScheduledAutoUpdate(
        string acfText,
        string appId,
        string exactTitle,
        out string updatedText)
    {
        updatedText = acfText ?? string.Empty;
        if (string.IsNullOrWhiteSpace(acfText) ||
            !SteamProtocol.IsValidAppId(appId) ||
            string.IsNullOrWhiteSpace(exactTitle))
            return false;

        if (!SteamProtocol.TryParseAppManifest(acfText, out var parsedId, out var name, out _, out _))
            return false;

        if (!string.Equals(parsedId, appId, StringComparison.Ordinal) ||
            !string.Equals(name?.Trim(), exactTitle.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;

        var current = SteamProtocol.MatchAcfField(acfText, "ScheduledAutoUpdate");
        if (string.IsNullOrWhiteSpace(current) || current.Trim() == "0")
            return false;

        var replaced = ScheduledField.Replace(acfText, "\"ScheduledAutoUpdate\"\t\t\"0\"", 1);
        if (string.Equals(replaced, acfText, StringComparison.Ordinal))
            return false;
        if (!string.Equals(SteamProtocol.MatchAcfField(replaced, "ScheduledAutoUpdate"), "0", StringComparison.Ordinal))
            return false;

        updatedText = replaced;
        return true;
    }
}
