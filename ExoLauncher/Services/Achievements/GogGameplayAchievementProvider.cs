using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ExoLauncher.Adapters.Cli;
using ExoLauncher.Helpers;
using ExoLauncher.Models;
using ExoLauncher.Services;

namespace ExoLauncher.Services.Achievements;

/// <summary>
/// Reads GOG achievements from gameplay.gog.com with the signed-in gogdl token.
/// Galaxy's local sqlite exposes playtime (<c>GameTimes</c>), not unlocks.
/// </summary>
public sealed class GogGameplayAchievementProvider : IAchievementProvider
{
    private const int MaxPayloadBytes = 2 * 1024 * 1024;
    private const int MaxAchievements = 10_000;
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(8);
    private static readonly HttpClient Http = CreateHttp();

    private readonly Func<(string UserId, string AccessToken)?> _resolveSession;
    private readonly Func<Uri, string, CancellationToken, Task<string?>> _fetch;

    public GogGameplayAchievementProvider()
        : this(GogAchievementSession.TryRead, FetchAsync)
    {
    }

    internal GogGameplayAchievementProvider(
        Func<(string UserId, string AccessToken)?> resolveSession,
        Func<Uri, string, CancellationToken, Task<string?>>? fetch = null)
    {
        _resolveSession = resolveSession;
        _fetch = fetch ?? FetchAsync;
    }

    public string Id => "gog";
    public StoreKind Store => StoreKind.Gog;
    public AchievementProviderCapabilities Capabilities =>
        AchievementProviderCapabilities.Snapshot |
        AchievementProviderCapabilities.Rarity |
        AchievementProviderCapabilities.CompleteCatalog;
    public TimeSpan SuggestedPollInterval => TimeSpan.FromSeconds(12);

    public bool Supports(GameEntry game) =>
        game.Store == StoreKind.Gog &&
        (game.Id.StartsWith("gog:", StringComparison.OrdinalIgnoreCase) ||
         IsGogProductId(game.LaunchTarget));

    public string? GetCurrentCoverageKey(GameEntry game)
    {
        if (!Supports(game)) return null;
        var session = _resolveSession();
        return session is null
            ? null
            : AchievementCoverageKeys.FromAccount("gog", session.Value.UserId);
    }

    public async Task<AchievementSnapshot> GetSnapshotAsync(
        GameEntry game,
        CancellationToken cancellationToken = default)
    {
        var sourceGameId = SourceGameId(game);
        if (game.Store != StoreKind.Gog || sourceGameId is null)
            return Unavailable(sourceGameId ?? string.Empty, "This entry has no valid GOG product id.");

        var session = _resolveSession();
        if (session is null)
            return Unavailable(sourceGameId, "GOG is not signed in.");

        var coverageKey = AchievementCoverageKeys.FromAccount("gog", session.Value.UserId);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(QueryTimeout);
        try
        {
            var uri = new Uri(AchievementsUri(session.Value.UserId, sourceGameId), UriKind.Absolute);
            var json = await _fetch(uri, session.Value.AccessToken, timeout.Token).ConfigureAwait(false);
            if (!IsCurrentSession(session.Value.UserId))
                return Unavailable(sourceGameId, "GOG account changed during achievement refresh.");
            return ParseSnapshotJson(json, sourceGameId, coverageKey, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Unavailable(sourceGameId, "GOG achievement sync timed out.", coverageKey);
        }
        catch
        {
            return Unavailable(sourceGameId, "GOG achievements are temporarily unavailable.", coverageKey);
        }
    }

    internal static string AchievementsUri(string userId, string productId) =>
        "https://gameplay.gog.com/clients/" + Uri.EscapeDataString(productId) +
        "/users/" + Uri.EscapeDataString(userId) + "/achievements";

    internal static AchievementSnapshot ParseSnapshotJson(
        string? json,
        string sourceGameId,
        string coverageKey,
        DateTimeOffset observedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxPayloadBytes)
            return Unavailable(sourceGameId, "GOG returned no usable achievement data.", coverageKey, observedAtUtc);

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("items", out var items) ||
                items.ValueKind != JsonValueKind.Array)
                return Unavailable(sourceGameId, "GOG returned no usable achievement data.", coverageKey, observedAtUtc);

            var entries = new Dictionary<string, AchievementEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in items.EnumerateArray())
            {
                if (entries.Count >= MaxAchievements)
                    return Unavailable(sourceGameId,
                        "GOG returned an unexpectedly large achievement catalog.", coverageKey, observedAtUtc);
                var entry = MapEntry(row, sourceGameId, observedAtUtc);
                if (entry is null) continue;
                if (!entries.TryAdd(entry.Definition.ExternalId, entry))
                    return Unavailable(sourceGameId,
                        "GOG returned duplicate achievement identities.", coverageKey, observedAtUtc);
            }

            var unlocked = entries.Values.Count(row => row.State.Unlocked);
            return new AchievementSnapshot
            {
                ProviderId = "gog",
                SourceGameId = sourceGameId,
                CoverageKey = coverageKey,
                Coverage = AchievementCoverageStatus.Complete,
                Capabilities = AchievementProviderCapabilities.Snapshot |
                               AchievementProviderCapabilities.Rarity |
                               AchievementProviderCapabilities.CompleteCatalog,
                ReportedTotal = entries.Count,
                ReportedUnlocked = unlocked,
                ObservedAtUtc = observedAtUtc,
                Entries = entries.Values.OrderBy(row => row.Definition.ExternalId, StringComparer.Ordinal).ToArray(),
                Message = "GOG gameplay API achievement progress.",
            };
        }
        catch (JsonException)
        {
            return Unavailable(sourceGameId, "GOG returned no usable achievement data.", coverageKey, observedAtUtc);
        }
    }

    private static AchievementEntry? MapEntry(
        JsonElement row,
        string sourceGameId,
        DateTimeOffset observedAtUtc)
    {
        if (row.ValueKind != JsonValueKind.Object) return null;
        var externalId = ReadText(row, "achievement_key", 512) ?? ReadText(row, "achievement_id", 512);
        if (string.IsNullOrWhiteSpace(externalId)) return null;

        var (unlocked, unlockedAt) = ReadUnlock(row);
        var hidden = ReadBool(row, "visible") is false;
        var name = ReadText(row, "name", 512);
        var description = ReadText(row, "description", 4_096) ?? string.Empty;
        if (hidden && !unlocked)
        {
            name = "Hidden achievement";
            description = string.Empty;
        }

        var rarity = ReadDouble(row, "rarity");
        if (rarity is < 0 or > 100) rarity = null;
        var tier = ReadText(row, "rarity_level_slug", 64);

        return new AchievementEntry
        {
            Definition = new AchievementDefinition
            {
                ProviderId = "gog",
                SourceGameId = sourceGameId,
                ExternalId = externalId,
                Name = string.IsNullOrWhiteSpace(name) ? externalId : name,
                Description = description,
                Hidden = hidden,
                IconUnlockedUrl = ReadHttpsUrl(row, "image_url_unlocked"),
                IconLockedUrl = ReadHttpsUrl(row, "image_url_locked"),
                GlobalUnlockPercent = rarity,
                Tier = tier,
            },
            State = new AchievementState
            {
                ExternalId = externalId,
                Unlocked = unlocked,
                UnlockedAtUtc = unlocked ? unlockedAt : null,
                ObservedAtUtc = observedAtUtc,
            },
        };
    }

    private bool IsCurrentSession(string expectedUserId)
    {
        try
        {
            var current = _resolveSession();
            return current is not null &&
                   string.Equals(current.Value.UserId, expectedUserId, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static string? SourceGameId(GameEntry game)
    {
        var fromId = game.Id.StartsWith("gog:", StringComparison.OrdinalIgnoreCase)
            ? game.Id[4..].Trim()
            : null;
        var target = game.LaunchTarget?.Trim();
        if (!string.IsNullOrWhiteSpace(fromId) && !IsGogProductId(fromId)) return null;
        if (!string.IsNullOrWhiteSpace(target) && !IsGogProductId(target)) return null;
        if (fromId is not null && !string.IsNullOrWhiteSpace(target) &&
            !string.Equals(fromId, target, StringComparison.Ordinal))
            return null;
        return fromId ?? (IsGogProductId(target) ? target : null);
    }

    private static bool IsGogProductId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 20 && value.All(char.IsDigit);

    private AchievementSnapshot Unavailable(string sourceGameId, string message, string? coverageKey = null) =>
        Unavailable(sourceGameId, message, coverageKey ?? "gog:unavailable", DateTimeOffset.UtcNow);

    private static AchievementSnapshot Unavailable(
        string sourceGameId,
        string message,
        string coverageKey,
        DateTimeOffset observedAtUtc) => new()
    {
        ProviderId = "gog",
        SourceGameId = sourceGameId,
        CoverageKey = coverageKey,
        Coverage = AchievementCoverageStatus.Unavailable,
        Capabilities = AchievementProviderCapabilities.Snapshot |
                       AchievementProviderCapabilities.Rarity |
                       AchievementProviderCapabilities.CompleteCatalog,
        ObservedAtUtc = observedAtUtc,
        Message = message,
    };

    private static async Task<string?> FetchAsync(Uri uri, string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("X-Gog-Lc", "en-US");
        using var response = await Http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK ||
            response.Content.Headers.ContentLength is > MaxPayloadBytes)
            return null;

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var memory = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (memory.Length + read > MaxPayloadBytes) return null;
            await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        return Encoding.UTF8.GetString(memory.ToArray());
    }

    private static HttpClient CreateHttp()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ExoLauncher/1.0");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    private static string? ReadText(JsonElement element, string property, int maxLength)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
            return null;
        var text = value.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(text) || text.Length > maxLength ? null : text;
    }

    private static double? ReadDouble(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number))
            return number;
        return value.ValueKind == JsonValueKind.String &&
               double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) &&
               double.IsFinite(number)
            ? number
            : null;
    }

    private static bool? ReadBool(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return null;
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) return value.GetBoolean();
        return null;
    }

    /// <summary>
    /// GOG's gameplay API has no separate achieved flag. A non-null
    /// <c>date_unlocked</c> is the official unlock field. An explicit
    /// <c>unlocked</c>/<c>achieved</c> boolean, when present, wins — a leftover
    /// timestamp must not invent an unlock.
    /// </summary>
    private static (bool Unlocked, DateTimeOffset? At) ReadUnlock(JsonElement row)
    {
        var flag = ReadBool(row, "unlocked") ?? ReadBool(row, "achieved");
        var at = ReadTimestamp(row, "date_unlocked");
        if (flag is false)
            return (false, null);
        if (flag is true)
            return (true, at);
        if (!row.TryGetProperty("date_unlocked", out var raw) ||
            raw.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return (false, null);
        if (raw.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(raw.GetString()))
            return (false, null);
        if (raw.ValueKind == JsonValueKind.Number &&
            raw.TryGetInt64(out var unix) && unix <= 0)
            return (false, null);
        return (true, at);
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp))
                return timestamp;
        }
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var unix) && unix > 0)
        {
            try { return DateTimeOffset.FromUnixTimeSeconds(unix); }
            catch { return null; }
        }
        return null;
    }

    private static string? ReadHttpsUrl(JsonElement element, string property)
    {
        var text = ReadText(element, property, 2_048);
        return AchievementIconCache.SanitizeProviderImageUrl(text);
    }
}

/// <summary>gogdl credentials already on disk. Never logs the token.</summary>
internal static class GogAchievementSession
{
    internal static (string UserId, string AccessToken)? TryRead()
    {
        foreach (var path in CandidatePaths())
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
                var info = new FileInfo(path);
                if (info.Length is <= 0 or > 1024 * 1024) continue;
                if (!GogdlCli.TryReadCredentials(File.ReadAllText(path), out var credentials)) continue;
                if (string.IsNullOrWhiteSpace(credentials.UserId) ||
                    string.IsNullOrWhiteSpace(credentials.AccessToken))
                    continue;
                if (credentials.IsExpired(DateTimeOffset.UtcNow)) continue;
                return (credentials.UserId, credentials.AccessToken);
            }
            catch
            {
                // Try the next known location.
            }
        }

        return null;
    }

    internal static IEnumerable<string> CandidatePaths()
    {
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        yield return Path.Combine(PathHelper.AppDataDir, "gogdl", "credentials.json");
        yield return Path.Combine(roaming, "heroic", "gog_store", "auth.json");
        yield return Path.Combine(user, ".config", "heroic", "gog_store", "auth.json");
        yield return Path.Combine(user, ".config", "heroic", "gog_store", "credentials.json");
        yield return Path.Combine(user, ".config", "gogdl", "credentials.json");
    }
}
