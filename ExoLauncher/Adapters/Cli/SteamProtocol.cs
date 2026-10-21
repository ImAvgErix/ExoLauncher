using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace ExoLauncher.Adapters.Cli;

/// <summary>Steam URI helpers and appmanifest field parsing (pure / testable).</summary>
public static partial class SteamProtocol
{
    /// <summary>Steam protocol IDs are positive decimal AppIDs, never arbitrary URI fragments.</summary>
    public static bool IsValidAppId([NotNullWhen(true)] string? appId) =>
        appId is { Length: >= 1 and <= 10 } &&
        appId.All(char.IsAsciiDigit) &&
        ulong.TryParse(appId, out var parsed) &&
        parsed > 0;

    public static string[] LaunchArguments(string appId, string? extraArgs = null)
    {
        if (!IsValidAppId(appId)) throw new ArgumentException("Invalid Steam app id.", nameof(appId));
        return ["-applaunch", appId, .. WindowsCommandLine.Parse(extraArgs)];
    }

    public static string InstallUri(string appId) => $"steam://install/{appId}";

    public static string UninstallUri(string appId) => $"steam://uninstall/{appId}";

    public static string ValidateUri(string appId) => $"steam://validate/{appId}";

    /// <summary>Asks a running (including -silent) Steam instance to show its main window.</summary>
    public static string OpenMainUri() => "steam://open/main";

    /// <summary>
    /// Every <c>"key" "value"</c> pair in an ACF/VDF file, nested sections
    /// included, keyed case-insensitively. The first occurrence in file order
    /// wins, matching the old per-field regex (top-level AppState fields come
    /// before InstalledDepots/UserConfig).
    /// </summary>
    public static Dictionary<string, string> ParseAcfFields(string acf)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in AcfPairRegex().Matches(acf))
            fields.TryAdd(Unescape(match.Groups[1].Value), Unescape(match.Groups[2].Value));
        return fields;
    }

    public static bool TryParseAppManifest(
        IReadOnlyDictionary<string, string> fields,
        out string? appId,
        out string? name,
        out string? installDir,
        out long? sizeOnDisk)
    {
        appId = fields.GetValueOrDefault("appid");
        name = fields.GetValueOrDefault("name");
        installDir = fields.GetValueOrDefault("installdir");
        sizeOnDisk = null;
        var sizeRaw = fields.GetValueOrDefault("SizeOnDisk");
        if (long.TryParse(sizeRaw, out var s)) sizeOnDisk = s;
        return IsValidAppId(appId) && !string.IsNullOrWhiteSpace(name);
    }

    /// <summary>VDF escapes a quote or backslash inside a value with a backslash;
    /// one left-to-right pass, so <c>\\"</c> is a backslash then the closing quote.</summary>
    private static string Unescape(string value) =>
        value.Contains('\\') ? AcfEscapeRegex().Replace(value, "$1") : value;

    [GeneratedRegex(@"""((?:[^""\\]|\\.)*)""\s+""((?:[^""\\]|\\.)*)""", RegexOptions.CultureInvariant)]
    private static partial Regex AcfPairRegex();

    [GeneratedRegex(@"\\([""\\])", RegexOptions.CultureInvariant)]
    private static partial Regex AcfEscapeRegex();
}
