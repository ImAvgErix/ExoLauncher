using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Adapters;

/// <summary>
/// Xbox time played: the Xbox Live user stat MinutesPlayed, the figure the
/// Xbox app shows, read through OpenXBL (api.xbl.io) with the player's own
/// OpenXBL key. Exo signs in to nothing and borrows no Microsoft client id:
/// the key is the player's, made at xbl.io with their own Microsoft account.
/// Three calls per refresh: the account (its xuid), the title history, and
/// MinutesPlayed for those titles in batches of 100.
/// </summary>
internal static class XboxPlaytime
{
    internal const string ApiBase = "https://api.xbl.io/v2/";
    internal const string StatName = "MinutesPlayed";
    private const int StatsBatch = 100;
    private const int MaxTitles = 2000;
    private const long MaxBody = 16 * 1024 * 1024;

    internal sealed record TitleHours(string TitleId, string Name, string? Pfn, int Minutes, DateTimeOffset? LastPlayedUtc);

    internal sealed record Title(string TitleId, string Name, string? Pfn, DateTimeOffset? LastPlayedUtc);

    internal sealed record Result(bool Ok, IReadOnlyList<TitleHours> Titles, string Message);

    internal static Task<Result> LoadAsync(string apiKey, CancellationToken ct) =>
        LoadAsync(apiKey, new HttpClientHandler { AllowAutoRedirect = false }, ct);

    internal static async Task<Result> LoadAsync(string apiKey, HttpMessageHandler handler, CancellationToken ct)
    {
        using var http = ExoHttp.Create(TimeSpan.FromSeconds(20), handler);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ExoLauncher/1.0");
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        // OpenXBL rejects a wildcard language; name one.
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US");
        http.DefaultRequestHeaders.TryAddWithoutValidation("X-Authorization", apiKey);

        var (accountStatus, account) = await SendAsync(http, HttpMethod.Get, "account", null, ct).ConfigureAwait(false);
        if (accountStatus is 401 or 403 || ErrorCode(account) is 401 or 403)
            return new Result(false, [], "OpenXBL did not accept this key.");
        if (accountStatus == 429 || ErrorCode(account) == 429)
            return new Result(false, [], "OpenXBL's hourly limit is used up. Try again later.");
        var xuid = ParseXuid(account);
        if (xuid is null)
            return new Result(false, [], "OpenXBL did not answer. Try again in a minute.");

        var (_, history) = await SendAsync(http, HttpMethod.Get, "titles", null, ct).ConfigureAwait(false);
        var titles = ParseTitles(history);
        if (titles.Count == 0)
            return new Result(false, [], "Xbox has no games played on this account.");

        var minutes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var batch in titles.Chunk(StatsBatch))
        {
            var body = JsonSerializer.Serialize(new
            {
                xuids = new[] { xuid },
                stats = batch.Select(title => new { name = StatName, titleId = title.TitleId }).ToArray(),
            });
            var (status, json) = await SendAsync(http, HttpMethod.Post, "player/stats", body, ct).ConfigureAwait(false);
            if (status == 429 || ErrorCode(json) == 429) break;
            foreach (var (id, value) in ParseMinutesPlayed(json))
                minutes[id] = value;
        }

        var hours = titles
            .Where(title => minutes.GetValueOrDefault(title.TitleId) > 0)
            .Select(title => new TitleHours(title.TitleId, title.Name, title.Pfn, minutes[title.TitleId], title.LastPlayedUtc))
            .ToList();
        return hours.Count > 0
            ? new Result(true, hours, "Xbox hours for " + hours.Count.ToString(CultureInfo.InvariantCulture) + " games.")
            : new Result(false, [], "Xbox reported no time played for these games.");
    }

    private static async Task<(int Status, string? Body)> SendAsync(
        HttpClient http, HttpMethod method, string path, string? json, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(method, ApiBase + path);
            if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength > MaxBody) return ((int)response.StatusCode, null);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ((int)response.StatusCode, body.Length > MaxBody ? null : body);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLog.Debug("OpenXBL request unavailable: " + ex.GetType().Name);
            return (0, null);
        }
    }

    /// <summary>OpenXBL wraps answers as { content, code }; older answers are bare. Returns the payload.</summary>
    private static JsonElement Payload(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("content", out var content) &&
        content.ValueKind == JsonValueKind.Object
            ? content
            : root;

    /// <summary>The envelope's code when it is not 200 (OpenXBL answers errors with HTTP 200 and a code).</summary>
    internal static int? ErrorCode(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("code", out var code) &&
                code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var value) && value != 200)
                return value;
        }
        catch (JsonException) { }
        return null;
    }

    internal static string? ParseXuid(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var payload = Payload(document.RootElement);
            if (!payload.TryGetProperty("profileUsers", out var users) || users.ValueKind != JsonValueKind.Array) return null;
            foreach (var user in users.EnumerateArray())
            {
                var id = Text(user, "id");
                if (id is { Length: > 0 and <= 20 } && id.All(char.IsAsciiDigit)) return id;
            }
        }
        catch (JsonException) { }
        return null;
    }

    /// <summary>Games in the title history (apps and hubs left out), with their numeric title id.</summary>
    internal static IReadOnlyList<Title> ParseTitles(string? json)
    {
        var titles = new List<Title>();
        if (string.IsNullOrWhiteSpace(json)) return titles;
        try
        {
            using var document = JsonDocument.Parse(json);
            var payload = Payload(document.RootElement);
            if (!payload.TryGetProperty("titles", out var list) || list.ValueKind != JsonValueKind.Array) return titles;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in list.EnumerateArray())
            {
                if (titles.Count >= MaxTitles) break;
                if (item.ValueKind != JsonValueKind.Object) continue;
                var id = Text(item, "titleId");
                var name = Text(item, "name")?.Trim();
                if (id is null || !uint.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out _) ||
                    string.IsNullOrEmpty(name) || name.Length > 300 || !seen.Add(id)) continue;
                var type = Text(item, "type");
                if (type is not null && !type.Equals("Game", StringComparison.OrdinalIgnoreCase)) continue;
                DateTimeOffset? last = null;
                if (item.TryGetProperty("titleHistory", out var history) && history.ValueKind == JsonValueKind.Object &&
                    DateTimeOffset.TryParse(Text(history, "lastTimePlayed"), CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed) &&
                    parsed.Year > 2000)
                    last = parsed;
                var pfn = Text(item, "pfn")?.Trim();
                titles.Add(new Title(id, name, string.IsNullOrEmpty(pfn) || pfn.Length > 200 ? null : pfn, last));
            }
        }
        catch (JsonException) { }
        return titles;
    }

    /// <summary>
    /// title id → MinutesPlayed from a user-stats answer
    /// (statlistscollection[].stats[] { titleid, name, value }). A title that
    /// has no MinutesPlayed stat is absent, never zero.
    /// </summary>
    internal static IReadOnlyDictionary<string, int> ParseMinutesPlayed(string? json)
    {
        var minutes = new Dictionary<string, int>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json)) return minutes;
        try
        {
            using var document = JsonDocument.Parse(json);
            var payload = Payload(document.RootElement);
            if (!payload.TryGetProperty("statlistscollection", out var lists) || lists.ValueKind != JsonValueKind.Array)
                return minutes;
            foreach (var list in lists.EnumerateArray())
            {
                if (list.ValueKind != JsonValueKind.Object ||
                    !list.TryGetProperty("stats", out var stats) || stats.ValueKind != JsonValueKind.Array) continue;
                foreach (var stat in stats.EnumerateArray())
                {
                    if (!string.Equals(Text(stat, "name"), StatName, StringComparison.OrdinalIgnoreCase)) continue;
                    var id = Text(stat, "titleid") ?? Text(stat, "titleId");
                    var value = Text(stat, "value");
                    if (id is null || !long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) ||
                        count is <= 0 or >= 100_000_000) continue;
                    if (!minutes.TryGetValue(id, out var current) || count > current) minutes[id] = (int)count;
                }
            }
        }
        catch (JsonException) { }
        return minutes;
    }

    private static string? Text(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }
}
