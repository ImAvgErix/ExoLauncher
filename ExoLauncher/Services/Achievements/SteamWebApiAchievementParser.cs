using System.Globalization;
using System.Text.Json;
using ExoLauncher.Models;
using ExoLauncher.Services;

namespace ExoLauncher.Services.Achievements;

/// <summary>
/// Parses Steam's official ISteamUserStats Web API payloads.
/// Unlock state comes only from the <c>achieved</c> flag, never from
/// <c>unlocktime</c> alone.
/// </summary>
internal static class SteamWebApiAchievementParser
{
    private const int MaxPayloadBytes = 8 * 1024 * 1024;
    private const int MaxAchievements = 10_000;

    internal static AchievementSnapshot ParsePlayerAchievements(
        string? playerJson,
        string? schemaJson,
        string sourceGameId,
        string coverageKey,
        DateTimeOffset observedAtUtc,
        string? expectedSteamId64 = null,
        IReadOnlyList<AchievementEntry>? localProgress = null)
    {
        if (string.IsNullOrWhiteSpace(playerJson) || playerJson.Length > MaxPayloadBytes)
            return Unavailable(sourceGameId, coverageKey, observedAtUtc,
                "Steam Web API returned no usable achievement data.");

        try
        {
            using var document = JsonDocument.Parse(playerJson, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("playerstats", out var stats) ||
                stats.ValueKind != JsonValueKind.Object)
                return Unavailable(sourceGameId, coverageKey, observedAtUtc,
                    "Steam Web API returned no usable achievement data.");

            var hasSuccess = stats.TryGetProperty("success", out var success);
            if (hasSuccess && success.ValueKind == JsonValueKind.False)
            {
                var error = ReadText(stats, "error", 128);
                var message = string.Equals(error, "Profile is not public", StringComparison.OrdinalIgnoreCase)
                    ? "Steam Web API cannot read a private profile with this key."
                    : string.Equals(error, "Requested app has no stats", StringComparison.OrdinalIgnoreCase)
                        ? "Steam Web API reports no stats for this game."
                        : "Steam Web API did not return this account's achievements.";
                return Unavailable(sourceGameId, coverageKey, observedAtUtc, message);
            }

            if (!hasSuccess || success.ValueKind != JsonValueKind.True ||
                string.IsNullOrWhiteSpace(expectedSteamId64) ||
                !string.Equals(ReadText(stats, "steamID", 32), expectedSteamId64,
                    StringComparison.Ordinal))
                return Unavailable(sourceGameId, coverageKey, observedAtUtc,
                    "Steam Web API did not verify the active Steam account.");

            if (!stats.TryGetProperty("achievements", out var rows) ||
                rows.ValueKind != JsonValueKind.Array)
                return Unavailable(sourceGameId, coverageKey, observedAtUtc,
                    "Steam Web API returned no usable achievement data.");

            if (!TryParseCompleteSchema(schemaJson, sourceGameId, out var schema))
                return Unavailable(sourceGameId, coverageKey, observedAtUtc,
                    "Steam Web API returned no complete achievement schema.");
            var progress = IndexProgress(localProgress);
            var entries = new Dictionary<string, AchievementEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows.EnumerateArray())
            {
                if (entries.Count >= MaxAchievements)
                    return Unavailable(sourceGameId, coverageKey, observedAtUtc,
                        "Steam Web API returned an unexpectedly large achievement catalog.");
                if (row.ValueKind != JsonValueKind.Object) continue;
                var externalId = ReadText(row, "apiname", 512);
                if (string.IsNullOrWhiteSpace(externalId)) continue;

                var achieved = ReadBool(row, "achieved");
                if (achieved is null)
                    return Unavailable(sourceGameId, coverageKey, observedAtUtc,
                        "Steam Web API returned an achievement without an unlock flag.");

                if (!schema.TryGetValue(externalId, out var def))
                    return Unavailable(sourceGameId, coverageKey, observedAtUtc,
                        "Steam Web API achievement rows did not match the game schema.");
                var hidden = def.Hidden;
                var unlocked = achieved.Value;
                DateTimeOffset? unlockedAtUtc = null;
                var unlockUnix = ReadInt64(row, "unlocktime");
                if (unlocked && unlockUnix is > 0)
                {
                    try { unlockedAtUtc = DateTimeOffset.FromUnixTimeSeconds(unlockUnix.Value); }
                    catch { /* invalid provider timestamp; flag still stands */ }
                }

                progress.TryGetValue(externalId, out var local);
                var name = def.Name;
                var description = def.Description;
                if (hidden && !unlocked)
                {
                    name = "Hidden achievement";
                    description = string.Empty;
                }

                var incoming = new AchievementEntry
                {
                    Definition = new AchievementDefinition
                    {
                        ProviderId = "steam",
                        SourceGameId = sourceGameId,
                        ExternalId = externalId,
                        Name = string.IsNullOrWhiteSpace(name) ? (hidden ? "Hidden achievement" : externalId) : name,
                        Description = description,
                        Hidden = hidden,
                        IconUnlockedUrl = hidden && !unlocked ? null : def.IconUnlockedUrl,
                        IconLockedUrl = hidden && !unlocked ? null : def.IconLockedUrl,
                    },
                    State = new AchievementState
                    {
                        ExternalId = externalId,
                        Unlocked = unlocked,
                        UnlockedAtUtc = unlockedAtUtc,
                        ProgressCurrent = local?.State.ProgressCurrent,
                        ProgressTarget = local?.State.ProgressTarget,
                        ObservedAtUtc = observedAtUtc,
                    },
                };

                if (!entries.TryAdd(externalId, incoming))
                    return Unavailable(sourceGameId, coverageKey, observedAtUtc,
                        "Steam Web API returned duplicate achievement identities.");
            }

            if (entries.Count != schema.Count ||
                schema.Keys.Any(externalId => !entries.ContainsKey(externalId)))
                return Unavailable(sourceGameId, coverageKey, observedAtUtc,
                    "Steam Web API achievement rows did not match the game schema.");

            var unlockedCount = entries.Values.Count(row => row.State.Unlocked);
            return new AchievementSnapshot
            {
                ProviderId = "steam",
                SourceGameId = sourceGameId,
                CoverageKey = coverageKey,
                Coverage = AchievementCoverageStatus.Complete,
                Capabilities = AchievementProviderCapabilities.Snapshot |
                               AchievementProviderCapabilities.Progress |
                               AchievementProviderCapabilities.CompleteCatalog,
                ReportedTotal = entries.Count,
                ReportedUnlocked = unlockedCount,
                ObservedAtUtc = observedAtUtc,
                Entries = entries.Values.OrderBy(row => row.Definition.ExternalId, StringComparer.Ordinal).ToArray(),
                Message = "Steam Web API achievement progress.",
            };
        }
        catch (JsonException)
        {
            return Unavailable(sourceGameId, coverageKey, observedAtUtc,
                "Steam Web API returned no usable achievement data.");
        }
    }

    internal static bool TryParseCompleteSchema(
        string? json,
        string sourceGameId,
        out Dictionary<string, AchievementDefinition> map)
    {
        map = new Dictionary<string, AchievementDefinition>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxPayloadBytes) return false;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("game", out var game) ||
                game.ValueKind != JsonValueKind.Object)
                return false;
            if (!game.TryGetProperty("availableGameStats", out var stats) ||
                stats.ValueKind != JsonValueKind.Object ||
                !stats.TryGetProperty("achievements", out var rows) ||
                rows.ValueKind != JsonValueKind.Array)
                return false;

            foreach (var row in rows.EnumerateArray())
            {
                if (map.Count >= MaxAchievements || row.ValueKind != JsonValueKind.Object)
                    return false;
                var externalId = ReadText(row, "name", 512);
                var hidden = ReadBool(row, "hidden");
                if (string.IsNullOrWhiteSpace(externalId) || hidden is null)
                    return false;
                var definition = new AchievementDefinition
                {
                    ProviderId = "steam",
                    SourceGameId = sourceGameId,
                    ExternalId = externalId,
                    Name = ReadText(row, "displayName", 512) ?? externalId,
                    Description = ReadText(row, "description", 4_096) ?? string.Empty,
                    Hidden = hidden.Value,
                    IconUnlockedUrl = ReadHttpsUrl(row, "icon"),
                    IconLockedUrl = ReadHttpsUrl(row, "icongray"),
                };
                if (!map.TryAdd(externalId, definition)) return false;
            }
        }
        catch (JsonException)
        {
            map.Clear();
            return false;
        }

        return true;
    }

    internal static bool TrySteamId64(string? accountId, out string steamId64)
    {
        steamId64 = "";
        if (string.IsNullOrWhiteSpace(accountId) ||
            !ulong.TryParse(accountId, NumberStyles.None, CultureInfo.InvariantCulture, out var account) ||
            account == 0)
            return false;
        steamId64 = (76561197960265728UL + account).ToString(CultureInfo.InvariantCulture);
        return true;
    }

    internal static string PlayerAchievementsUri(string key, string steamId64, string appId) =>
        "https://api.steampowered.com/ISteamUserStats/GetPlayerAchievements/v1/?key=" +
        Uri.EscapeDataString(key) +
        "&steamid=" + Uri.EscapeDataString(steamId64) +
        "&appid=" + Uri.EscapeDataString(appId);

    internal static string SchemaUri(string key, string appId) =>
        "https://api.steampowered.com/ISteamUserStats/GetSchemaForGame/v2/?key=" +
        Uri.EscapeDataString(key) +
        "&appid=" + Uri.EscapeDataString(appId) +
        "&l=english";

    private static Dictionary<string, AchievementEntry> IndexProgress(
        IReadOnlyList<AchievementEntry>? localProgress)
    {
        var map = new Dictionary<string, AchievementEntry>(StringComparer.OrdinalIgnoreCase);
        if (localProgress is null) return map;
        foreach (var row in localProgress)
        {
            if (!string.IsNullOrWhiteSpace(row.Definition.ExternalId))
                map[row.Definition.ExternalId] = row;
        }
        return map;
    }

    private static AchievementSnapshot Unavailable(
        string sourceGameId,
        string coverageKey,
        DateTimeOffset observedAtUtc,
        string message) => new()
    {
        ProviderId = "steam",
        SourceGameId = sourceGameId,
        CoverageKey = coverageKey,
        Coverage = AchievementCoverageStatus.Unavailable,
        Capabilities = AchievementProviderCapabilities.Snapshot |
                       AchievementProviderCapabilities.Progress |
                       AchievementProviderCapabilities.CompleteCatalog,
        ObservedAtUtc = observedAtUtc,
        Message = message,
    };

    private static string? ReadText(JsonElement element, string property, int maxLength)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
            return null;
        var text = value.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(text) || text.Length > maxLength ? null : text;
    }

    private static long? ReadInt64(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
        return value.ValueKind == JsonValueKind.String &&
               long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
            ? number
            : null;
    }

    private static bool? ReadBool(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return null;
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) return value.GetBoolean();
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number != 0;
        if (value.ValueKind == JsonValueKind.String)
        {
            if (bool.TryParse(value.GetString(), out var parsed)) return parsed;
            if (int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
                return number != 0;
        }
        return null;
    }

    private static string? ReadHttpsUrl(JsonElement element, string property)
    {
        var text = ReadText(element, property, 2_048);
        return AchievementIconCache.SanitizeProviderImageUrl(text);
    }
}
