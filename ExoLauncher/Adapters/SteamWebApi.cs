using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Adapters;

/// <summary>
/// Steam Web API owned-games list (IPlayerService/GetOwnedGames), requested
/// only with a key the user pasted. Official HTTPS — not a tray agent, not a socket.
/// </summary>
internal static class SteamWebApi
{
    internal const string OwnedGamesUrl =
        "https://api.steampowered.com/IPlayerService/GetOwnedGames/v0001/";

    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly object Gate = new();
    private static readonly TimeSpan OwnedGamesTtl = TimeSpan.FromMinutes(5);
    // No per-game cap: the list is only authoritative when complete, and the
    // 8 MB payload limit already bounds parsing (large libraries exceed 400).
    private const int MaxJsonPayloadBytes = 8 * 1024 * 1024;
    private static string _ownedGamesKey = "";
    private static DateTimeOffset _ownedGamesAt = DateTimeOffset.MinValue;
    private static OwnedGamesResult _ownedGamesCache = OwnedGamesResult.Unavailable;

    internal sealed record OwnedGamesResult(
        bool Authoritative,
        IReadOnlySet<string> AppIds,
        IReadOnlyDictionary<string, int> PlaytimeMinutes,
        IReadOnlyDictionary<string, string> Names)
    {
        public static OwnedGamesResult Unavailable { get; } =
            new(
                false,
                new HashSet<string>(StringComparer.Ordinal),
                new Dictionary<string, int>(StringComparer.Ordinal),
                new Dictionary<string, string>(StringComparer.Ordinal));
    }

    internal sealed record OwnedGameInfo(string AppId, string Name, int PlaytimeMinutes);

    internal static void InvalidateOwnedGames()
    {
        lock (Gate)
        {
            _ownedGamesKey = "";
            _ownedGamesAt = DateTimeOffset.MinValue;
            _ownedGamesCache = OwnedGamesResult.Unavailable;
        }
    }

    internal static async Task<OwnedGamesResult> LoadOwnedGamesAsync(
        string key,
        string steamId64,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(steamId64))
            return OwnedGamesResult.Unavailable;

        var identity = RequestIdentity(key, [steamId64]);
        lock (Gate)
        {
            if (string.Equals(_ownedGamesKey, identity, StringComparison.Ordinal) &&
                DateTimeOffset.UtcNow - _ownedGamesAt < OwnedGamesTtl)
                return _ownedGamesCache;
        }

        try
        {
            var url = OwnedGamesUrl +
                      "?key=" + Uri.EscapeDataString(key) +
                      "&steamid=" + Uri.EscapeDataString(steamId64) +
                      "&include_appinfo=1&include_played_free_games=1&include_free_sub=1&skip_unvetted_apps=0&format=json";
            using var response = await Http.GetAsync(
                url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return OwnedGamesResult.Unavailable;
            if (response.Content.Headers.ContentLength is > MaxJsonPayloadBytes)
                return OwnedGamesResult.Unavailable;
            var json = await ReadBodyLimitedAsync(response.Content, MaxJsonPayloadBytes, ct)
                .ConfigureAwait(false);
            if (json is null)
                return OwnedGamesResult.Unavailable;
            if (!TryParseOwnedGameCatalog(json, out var games, out var authoritative) || !authoritative)
                return OwnedGamesResult.Unavailable;
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var minutes = new Dictionary<string, int>(StringComparer.Ordinal);
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var game in games)
            {
                ids.Add(game.AppId);
                if (game.PlaytimeMinutes > 0)
                    minutes[game.AppId] = game.PlaytimeMinutes;
                if (!string.IsNullOrWhiteSpace(game.Name))
                    names[game.AppId] = game.Name.Trim();
            }
            var result = new OwnedGamesResult(true, ids, minutes, names);
            lock (Gate)
            {
                _ownedGamesKey = identity;
                _ownedGamesAt = DateTimeOffset.UtcNow;
                _ownedGamesCache = result;
            }
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Debug("Steam owned-games query unavailable: " + ex.GetType().Name);
            return OwnedGamesResult.Unavailable;
        }
    }

    internal static bool TryParseOwnedGameCatalog(
        string? json,
        out IReadOnlyList<OwnedGameInfo> games,
        out bool authoritative)
    {
        var parsed = new List<OwnedGameInfo>();
        games = parsed;
        authoritative = false;
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxJsonPayloadBytes)
            return false;

        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("response", out var response) ||
                response.ValueKind != JsonValueKind.Object ||
                !response.TryGetProperty("games", out var list) ||
                list.ValueKind != JsonValueKind.Array)
                return false;

            authoritative = true;
            foreach (var game in list.EnumerateArray())
            {
                if (game.ValueKind != JsonValueKind.Object ||
                    !game.TryGetProperty("appid", out var appIdElement))
                    continue;
                var raw = appIdElement.ValueKind == JsonValueKind.Number
                    ? appIdElement.GetRawText()
                    : appIdElement.ValueKind == JsonValueKind.String
                        ? appIdElement.GetString()
                        : null;
                if (raw is not { Length: > 0 and <= 10 } || !raw.All(char.IsDigit))
                    continue;
                var name = game.TryGetProperty("name", out var nameElement) &&
                           nameElement.ValueKind == JsonValueKind.String
                    ? (nameElement.GetString() ?? "").Trim()
                    : "";
                if (name.Length > 120) name = name[..120];
                var minutes = 0;
                if (game.TryGetProperty("playtime_forever", out var playtime) &&
                    playtime.ValueKind == JsonValueKind.Number)
                    minutes = Math.Max(0, playtime.TryGetInt32(out var value) ? value : 0);
                parsed.Add(new OwnedGameInfo(raw, name, minutes));
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static string RequestIdentity(string key, IReadOnlyList<string> steamId64s)
    {
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return fingerprint + ":" + string.Join(',', DistinctIds(steamId64s));
    }

    private static IReadOnlyList<string> DistinctIds(IReadOnlyList<string> steamId64s) =>
        steamId64s
            .Where(id => !string.IsNullOrWhiteSpace(id) && id.All(char.IsDigit))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

    private static async Task<string?> ReadBodyLimitedAsync(
        HttpContent content,
        int maxBytes,
        CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream(capacity: Math.Min(maxBytes, 64 * 1024));
        var chunk = new byte[64 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(), ct).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > maxBytes) return null;
            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
    }

    private static HttpClient CreateHttpClient()
    {
        var http = ExoHttp.Create(TimeSpan.FromSeconds(5), new HttpClientHandler { AllowAutoRedirect = false });
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ExoLauncher/1.0");
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return http;
    }
}
