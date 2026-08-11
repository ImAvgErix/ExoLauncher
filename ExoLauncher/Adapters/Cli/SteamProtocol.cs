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

    public static string RunGameUri(string appId) => $"steam://rungameid/{appId}";

    public static string InstallUri(string appId) => $"steam://install/{appId}";

    public static string ValidateUri(string appId) => $"steam://validate/{appId}";

    /// <summary>Opens the game's store page inside the Steam desktop client (not the browser).</summary>
    public static string StoreUri(string appId) => $"steam://store/{appId}";

    /// <summary>Opens Steam Downloads — helps kick a queued update that never started.</summary>
    public static string DownloadsUri() => "steam://open/downloads";

    public static string? MatchAcfField(string acf, string field)
    {
        var m = AcfFieldRegex(field).Match(acf);
        return m.Success ? m.Groups[1].Value : null;
    }

    public static bool TryParseAppManifest(
        string acfText,
        out string? appId,
        out string? name,
        out string? installDir,
        out long? sizeOnDisk)
    {
        appId = MatchAcfField(acfText, "appid");
        name = MatchAcfField(acfText, "name");
        installDir = MatchAcfField(acfText, "installdir");
        sizeOnDisk = null;
        var sizeRaw = MatchAcfField(acfText, "SizeOnDisk");
        if (long.TryParse(sizeRaw, out var s)) sizeOnDisk = s;
        return IsValidAppId(appId) && !string.IsNullOrWhiteSpace(name);
    }

    private static Regex AcfFieldRegex(string field) =>
        new($"\"{Regex.Escape(field)}\"\\s+\"([^\"]*)\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
