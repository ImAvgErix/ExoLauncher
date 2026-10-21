using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExoLauncher.Helpers;
using ExoLauncher.Services;
using ExoLauncher.Adapters.Cli;

namespace ExoLauncher.Adapters;

/// <summary>
/// Public tracker.gg profile JSON. Hours are match-derived on their side.
/// A private profile is reported as private, not as zero hours.
/// </summary>
internal static class TrackerGgPlaytime
{
    internal const string ValorantProfileUrl = "https://api.tracker.gg/api/v2/valorant/standard/profile/riot/";
    internal const string LeagueProfileUrl = "https://api.tracker.gg/api/v2/lol/standard/profile/riot/";

    private static readonly Regex Hours = new(
        @"([\d,.]+)\s*h",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MinutesPart = new(
        @"([\d,.]+)\s*m",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly CookieContainer Cookies = new();
    private static readonly HttpMessageHandler SharedHandler = new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        UseCookies = true,
        CookieContainer = Cookies,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        EnableMultipleHttp2Connections = false,
    };

    internal sealed record Result(
        bool Ok,
        bool ProfilePrivate,
        IReadOnlyDictionary<string, int> Minutes,
        string Message);

    internal static string ProfilePageUrl(string riotId) =>
        "https://tracker.gg/valorant/profile/riot/" + EncodeRiotId(riotId) + "/overview";

    internal static string EncodeRiotId(string riotId)
    {
        var normalized = RiotIdStore.Normalize(riotId) ?? riotId.Trim();
        var hash = normalized.IndexOf('#');
        if (hash <= 0) return Uri.EscapeDataString(normalized);
        return Uri.EscapeDataString(normalized[..hash]) + "%23" + Uri.EscapeDataString(normalized[(hash + 1)..]);
    }

    internal static bool IsPrivateProfile(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("errors", out var errors) ||
                errors.ValueKind != JsonValueKind.Array)
                return false;
            foreach (var error in errors.EnumerateArray())
            {
                if (error.ValueKind != JsonValueKind.Object) continue;
                var code = ReadString(error, "code") ?? "";
                var message = ReadString(error, "message") ?? "";
                if (code.Contains("Private", StringComparison.OrdinalIgnoreCase) ||
                    message.Contains("private", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    internal static IReadOnlyDictionary<string, int> ParseMinutes(string? json, string gameId)
    {
        var minutes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var value = ParseBestMinutes(json);
        if (value is int parsed && parsed > 0)
            minutes[gameId] = parsed;
        return minutes;
    }

    internal static int? ParseBestMinutes(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || IsPrivateProfile(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("data", out var data))
                return null;
            if (!TryEnumerateSegments(data, out var segments))
                return null;

            int? overview = null;
            var playlists = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var seasons = 0;
            foreach (var segment in segments)
            {
                if (segment.ValueKind != JsonValueKind.Object) continue;
                if (!segment.TryGetProperty("stats", out var stats) ||
                    !stats.TryGetProperty("timePlayed", out var stat))
                    continue;
                var minutes = MinutesFromStat(stat);
                if (minutes is not int value || value <= 0) continue;

                var type = ReadString(segment, "type") ?? "";
                if (type.Equals("overview", StringComparison.OrdinalIgnoreCase) ||
                    type.Equals("profile", StringComparison.OrdinalIgnoreCase))
                {
                    if (overview is null || value > overview) overview = value;
                    continue;
                }

                if (type.Equals("playlist", StringComparison.OrdinalIgnoreCase))
                {
                    var name = SegmentName(segment) ?? playlists.Count.ToString(CultureInfo.InvariantCulture);
                    if (!playlists.TryGetValue(name, out var current) || value > current)
                        playlists[name] = value;
                    continue;
                }

                if (type.Equals("season", StringComparison.OrdinalIgnoreCase) && value > seasons)
                    seasons = value;
            }

            // Overview/profile is tracker.gg lifetime. Playlist sums are only
            // a fallback: they can be current-act or overlapping queues.
            if (overview is > 0) return overview;
            if (playlists.Count > 0) return playlists.Values.Sum();
            return seasons > 0 ? seasons : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static async Task<Result> LoadAsync(string riotId, HttpMessageHandler handler, CancellationToken ct)
    {
        var id = RiotIdStore.Normalize(riotId);
        if (id is null)
            return new Result(false, false, new Dictionary<string, int>(), "Riot ID must look like Name#TAG.");

        var minutes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var anyPrivate = false;
        var anyBlocked = false;
        var encoded = EncodeRiotId(id);
        var allowCurl = ReferenceEquals(handler, SharedHandler);
        foreach (var (urls, gameId) in new[]
                 {
                     (new[]
                     {
                         ValorantProfileUrl + encoded + "?season=all",
                         ValorantProfileUrl + encoded + "/segments/playlist?season=all",
                     }, "riot:valorant"),
                     (new[]
                     {
                         LeagueProfileUrl + encoded + "?season=all",
                         LeagueProfileUrl + encoded + "/segments/playlist?season=all",
                     }, "riot:league_of_legends"),
                 })
        {
            string? json = null;
            var blocked = false;
            var priv = false;
            foreach (var url in urls)
            {
                var body = await GetJsonAsync(url, handler, allowCurl, ct).ConfigureAwait(false);
                if (body is null)
                {
                    blocked = true;
                    continue;
                }

                if (IsPrivateProfile(body))
                {
                    priv = true;
                    continue;
                }

                if (ParseBestMinutes(body) is > 0)
                {
                    json = body;
                    break;
                }
            }

            if (json is null)
            {
                if (priv) anyPrivate = true;
                else if (blocked) anyBlocked = true;
                continue;
            }

            var parsed = ParseMinutes(json, gameId);
            foreach (var pair in parsed)
            {
                if (!minutes.TryGetValue(pair.Key, out var current) || pair.Value > current)
                    minutes[pair.Key] = pair.Value;
            }
        }

        if (minutes.Count > 0)
            return new Result(true, false, minutes, "Imported hours from tracker.gg.");
        if (anyPrivate)
            return new Result(
                false,
                true,
                minutes,
                "This tracker.gg profile is private. Sign in with Riot on tracker.gg once so the profile is public.");
        if (anyBlocked)
            return new Result(false, false, minutes, "tracker.gg blocked the request. Try again in a minute.");
        return new Result(false, false, minutes, "tracker.gg did not return hours for that Riot ID.");
    }

    internal static Task<Result> LoadAsync(string riotId, CancellationToken ct) =>
        LoadAsync(riotId, SharedHandler, ct);

    private static async Task<string?> GetJsonAsync(
        string url,
        HttpMessageHandler handler,
        bool allowCurlFallback,
        CancellationToken ct)
    {
        var json = await HttpGetJsonAsync(url, handler, ct).ConfigureAwait(false);
        if (IsJsonBody(json)) return json;
        if (allowCurlFallback)
        {
            json = await CurlGetJsonAsync(url, ct).ConfigureAwait(false);
            if (IsJsonBody(json)) return json;
        }

        return null;
    }

    private static async Task<string?> HttpGetJsonAsync(string url, HttpMessageHandler handler, CancellationToken ct)
    {
        using var http = ExoHttp.Create(TimeSpan.FromSeconds(20), handler, disposeHandler: false);
        http.DefaultRequestVersion = HttpVersion.Version11;
        http.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        using var request = new HttpRequestMessage(HttpMethod.Get, url)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        request.Headers.TryAddWithoutValidation("Origin", "https://tracker.gg");
        request.Headers.TryAddWithoutValidation("Referer", "https://tracker.gg/");
        request.Headers.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
        try
        {
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Debug("tracker.gg playtime query unavailable: " + ex.GetType().Name);
            return null;
        }
    }

    private static async Task<string?> CurlGetJsonAsync(string url, CancellationToken ct)
    {
        var curl = Path.Combine(Environment.SystemDirectory, "curl.exe");
        if (!File.Exists(curl)) return null;

        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(25));
            var result = await CliRunner.RunJsonAsync(curl,
                ["-sS", "--compressed", "--max-time", "20", "--max-filesize", "16777216",
                 "-H", "Accept: application/json", "-H", "Accept-Language: en-US,en;q=0.9",
                 "-H", "Origin: https://tracker.gg", "-H", "Referer: https://tracker.gg/",
                 "-H", "User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36", url],
                null, deadline.Token).ConfigureAwait(false);
            return result.ExitCode == 0 ? result.StdOut : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Debug("tracker.gg curl fallback unavailable: " + ex.GetType().Name);
            return null;
        }
    }

    private static bool IsJsonBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        var trimmed = body.TrimStart();
        return trimmed.StartsWith('{') || trimmed.StartsWith('[');
    }

    private static bool TryEnumerateSegments(JsonElement data, out List<JsonElement> segments)
    {
        segments = [];
        if (data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                    segments.Add(item);
            }

            return segments.Count > 0;
        }

        if (data.ValueKind != JsonValueKind.Object) return false;
        if (data.TryGetProperty("segments", out var nested) && nested.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in nested.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                    segments.Add(item);
            }

            return segments.Count > 0;
        }

        foreach (var property in data.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Object &&
                property.Value.TryGetProperty("stats", out _))
                segments.Add(property.Value);
        }

        return segments.Count > 0;
    }

    private static string? SegmentName(JsonElement segment)
    {
        if (!segment.TryGetProperty("metadata", out var metadata) ||
            metadata.ValueKind != JsonValueKind.Object)
            return null;
        return ReadString(metadata, "name") ?? ReadString(metadata, "shortName");
    }

    internal static int? MinutesFromStat(JsonElement stat)
    {
        if (stat.ValueKind == JsonValueKind.Number && stat.TryGetDouble(out var bare) && bare > 0)
            return (int)Math.Clamp(bare / 60d, 1, int.MaxValue);
        if (stat.ValueKind != JsonValueKind.Object) return null;
        // The raw value in its declared unit; the display text is rounded, so it is the last resort.
        if (StatMinutes(stat) is { } minutes) return minutes;
        return stat.TryGetProperty("displayValue", out var display) &&
               display.ValueKind == JsonValueKind.String &&
               TryParseDisplay(display.GetString(), out var fromDisplay)
            ? fromDisplay
            : null;
    }

    /// <summary>
    /// A stat's minutes from its raw value and declared unit (TimeSeconds,
    /// TimeMilliseconds, TimeMinutes, TotalHoursFromSeconds…); null for an unknown unit.
    /// </summary>
    internal static int? StatMinutes(JsonElement stat)
    {
        if (stat.ValueKind != JsonValueKind.Object || !stat.TryGetProperty("value", out var node) ||
            node.ValueKind != JsonValueKind.Number || !node.TryGetDouble(out var value) || value <= 0)
            return null;
        var unit = ReadString(stat, "displayType") ?? "";
        double minutes;
        if (unit.Contains("Millis", StringComparison.OrdinalIgnoreCase)) minutes = value / 60_000d;
        else if (unit.Contains("Seconds", StringComparison.OrdinalIgnoreCase)) minutes = value / 60d;
        else if (unit.Contains("Minutes", StringComparison.OrdinalIgnoreCase)) minutes = value;
        else if (unit.Contains("Hours", StringComparison.OrdinalIgnoreCase)) minutes = value * 60d;
        else return null;
        return minutes >= 1 && minutes < 100_000_000 ? (int)minutes : null;
    }

    internal static bool TryParseDisplay(string? text, out int minutes)
    {
        minutes = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var hoursMatch = Hours.Match(text);
        var minuteMatch = MinutesPart.Match(text);
        var hours = hoursMatch.Success &&
                    double.TryParse(hoursMatch.Groups[1].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var h)
            ? h
            : 0;
        var extra = minuteMatch.Success &&
                    double.TryParse(minuteMatch.Groups[1].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var m)
            ? m
            : 0;
        var total = (int)Math.Clamp((hours * 60d) + extra, 0, int.MaxValue);
        if (total <= 0) return false;
        minutes = total;
        return true;
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
