using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// How the last update ended, as the installer's scripts leave it beside the install (update-result.json, update-error.log).
/// Without this the app that comes back after a failed or rolled-back update says nothing, and offers the same version
/// again.
/// </summary>
internal static class UpdateOutcome
{
    internal const string ResultFile = "update-result.json";
    internal const string ErrorFile = "update-error.log";

    /// <summary>What to tell the user about an update that did not take; null when it did, or there was none.</summary>
    internal static string? Say(string? resultJson, string? errorText)
    {
        string? status = null, version = null;
        if (!string.IsNullOrWhiteSpace(resultJson))
        {
            try
            {
                using var document = JsonDocument.Parse(resultJson);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    status = root.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
                    version = root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                }
            }
            catch (JsonException) { /* an unreadable result says nothing; the log file may */ }
        }
        if (status == "succeeded") return null;
        var named = version is { Length: > 0 and <= 32 } && version.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '+') ? " (v" + version.TrimStart('v') + ")" : "";
        return status switch
        {
            "rolled-back" => $"The last update{named} didn't start, so Exo went back to the version before.",
            "failed-preserved" => $"The last update{named} didn't finish; the version before is kept.",
            _ when !string.IsNullOrWhiteSpace(errorText) => $"The last update{named} didn't install.",
            _ => null,
        };
    }

    /// <summary>
    /// Reads and removes the installer's notes in <paramref name="root"/> (the install's folder). The scripts' own words go to
    /// the log, never to the page: they name paths.
    /// </summary>
    internal static string? Take(string root)
    {
        string? result = null, error = null;
        foreach (var (name, set) in new (string, Action<string?>)[] { (ResultFile, text => result = text), (ErrorFile, text => error = text) })
        {
            var path = Path.Combine(root, name);
            try
            {
                if (!File.Exists(path) || new FileInfo(path).Length > 64 * 1024) continue;
                set(File.ReadAllText(path));
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* read again next start */ }
        }
        var said = Say(result, error);
        if (said is not null) AppLog.Warn($"Update: {said} Installer said: {Trim(error) ?? Trim(result)}");
        return said;
    }

    private static string? Trim(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Length > 600 ? text[..600] : text;
}
