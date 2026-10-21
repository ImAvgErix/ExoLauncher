using System.Net.Http.Headers;
using System.Text.Json;

namespace ExoLauncher.Services;

/// <summary>
/// SteamGridDB portrait fallback after official Steam CDN. Used only when a key
/// is saved. Never a primary cover source. A game with no Steam app id (osu!,
/// a local game) is found by its exact title instead, never by a near match.
/// </summary>
internal static class SteamGridDbArt
{
    internal static bool TryParseSteamGameId(string? json, out int gameId)
    {
        gameId = 0;
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
                return false;
            if (!root.TryGetProperty("data", out var data)) return false;
            if (data.ValueKind == JsonValueKind.Object &&
                data.TryGetProperty("id", out var id) &&
                id.TryGetInt32(out gameId) &&
                gameId > 0)
                return true;
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static string? ParsePortraitUrl(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 12 });
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return null;
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (item.TryGetProperty("url", out var url) &&
                    url.ValueKind == JsonValueKind.String &&
                    Uri.TryCreate(url.GetString(), UriKind.Absolute, out var parsed) &&
                    parsed.Scheme is "https")
                    return parsed.ToString();
            }
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The SteamGridDB game whose name is exactly this title (case, marks and
    /// punctuation aside), a verified one first; 0 when none is. A near name
    /// (McOsu for osu!) is never taken.
    /// </summary>
    internal static int ParseExactTitleGameId(string? json, string title)
    {
        var wanted = Key(title);
        if (wanted.Length == 0 || string.IsNullOrWhiteSpace(json)) return 0;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 12 });
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return 0;
            var best = 0;
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("id", out var id) || !id.TryGetInt32(out var gameId) || gameId <= 0 ||
                    !item.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String ||
                    Key(name.GetString()!) != wanted)
                    continue;
                if (item.TryGetProperty("verified", out var verified) && verified.ValueKind == JsonValueKind.True) return gameId;
                if (best == 0) best = gameId;
            }
            return best;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    /// <summary>Letters and digits only, lower case: "osu!" and "OSU" are one name.</summary>
    private static string Key(string title) =>
        new(title.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>A portrait for a game Steam does not sell, by its exact title on SteamGridDB.</summary>
    internal static async Task<string?> TryPortraitUrlByTitleAsync(HttpClient http, string title, CancellationToken cancellationToken)
    {
        var key = CatalogApiKeyStore.TryReadSteamGridDb();
        if (key is null || string.IsNullOrWhiteSpace(title)) return null;
        try
        {
            var json = await GetAsync(http, key,
                "https://www.steamgriddb.com/api/v2/search/autocomplete/" + Uri.EscapeDataString(title.Trim()), cancellationToken).ConfigureAwait(false);
            var gameId = ParseExactTitleGameId(json, title);
            return gameId > 0 ? await GridAsync(http, key, gameId, cancellationToken).ConfigureAwait(false) : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> GridAsync(HttpClient http, string key, int gameId, CancellationToken cancellationToken) =>
        ParsePortraitUrl(await GetAsync(http, key,
            "https://www.steamgriddb.com/api/v2/grids/game/" + gameId + "?dimensions=600x900&types=static", cancellationToken).ConfigureAwait(false));

    /// <summary>One authorised GET, six seconds at most; null when it did not succeed.</summary>
    private static async Task<string?> GetAsync(HttpClient http, string key, string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(6));
        using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false) : null;
    }

    internal static async Task<string?> TryPortraitUrlAsync(
        HttpClient http,
        string steamAppId,
        CancellationToken cancellationToken)
    {
        var key = CatalogApiKeyStore.TryReadSteamGridDb();
        if (key is null || string.IsNullOrWhiteSpace(steamAppId) || !steamAppId.All(char.IsDigit))
            return null;
        try
        {
            using var gameRequest = new HttpRequestMessage(
                HttpMethod.Get,
                "https://www.steamgriddb.com/api/v2/games/steam/" + steamAppId);
            gameRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var gameTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            gameTimeout.CancelAfter(TimeSpan.FromSeconds(6));
            using var gameResponse = await http.SendAsync(gameRequest, gameTimeout.Token).ConfigureAwait(false);
            if (!gameResponse.IsSuccessStatusCode) return null;
            var gameJson = await gameResponse.Content.ReadAsStringAsync(gameTimeout.Token).ConfigureAwait(false);
            if (!TryParseSteamGameId(gameJson, out var gameId)) return null;

            using var gridRequest = new HttpRequestMessage(
                HttpMethod.Get,
                "https://www.steamgriddb.com/api/v2/grids/game/" + gameId + "?dimensions=600x900&types=static");
            gridRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var gridTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            gridTimeout.CancelAfter(TimeSpan.FromSeconds(6));
            using var gridResponse = await http.SendAsync(gridRequest, gridTimeout.Token).ConfigureAwait(false);
            if (!gridResponse.IsSuccessStatusCode) return null;
            var gridJson = await gridResponse.Content.ReadAsStringAsync(gridTimeout.Token).ConfigureAwait(false);
            return ParsePortraitUrl(gridJson);
        }
        catch
        {
            return null;
        }
    }
}
