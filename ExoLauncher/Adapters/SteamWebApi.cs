using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Adapters;

/// <summary>
/// Steam Web API presence, requested only while Friends is open and only
/// with a key the user pasted. Official HTTPS — not a tray agent, not a socket.
///
/// <see href="https://partner.steamgames.com/doc/webapi/ISteamUser"/>
/// GetPlayerSummaries v2: up to 100 SteamIDs. Public profiles may include
/// personastate, lastlogoff, gameid, and gameextrainfo. A row with neither a
/// persona state nor a valid last-logoff time stays unknown. personastate 0
/// from the live API is offline.
///
/// GetFriendList is not called: the names already come from the local cache,
/// and a private friends list answers 401.
/// </summary>
internal static class SteamWebApi
{
    internal const string SummariesUrl =
        "https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v2/";
    internal const string OwnedGamesUrl =
        "https://api.steampowered.com/IPlayerService/GetOwnedGames/v0001/";
    internal const int BatchSize = 100;

    internal const string LiveNote =
        "Steam presence is live when Steam returns a persona state or last-logoff time. Rows without either stay unknown.";
    internal const string RefusedNote =
        "That Steam Web API key was refused. Check it in Settings.";
    internal const string ThrottledNote =
        "Steam is rate limiting Exo. Backing off, then trying again.";
    internal const string UnreachableNote =
        "Steam did not answer just now. Exo will try again shortly.";

    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan FailureRetry = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MaxThrottleBackoff = TimeSpan.FromMinutes(15);

    internal sealed record Summary(
        string Status,
        string? StatusText,
        string? PlayingId,
        string? PlayingTitle,
        string? LastSeenUtc = null,
        string? AvatarUrl = null);

    /// <summary>
    /// Parses Steam's account-owned game list. An explicit empty games array is
    /// authoritative; a private or partial response without the array is not.
    /// </summary>
    internal static bool TryParseOwnedGames(
        string? json,
        out IReadOnlySet<string> appIds,
        out bool authoritative)
    {
        var parsed = new HashSet<string>(StringComparer.Ordinal);
        appIds = parsed;
        authoritative = false;
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxJsonPayloadBytes)
            return false;

        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("response", out var response) ||
                response.ValueKind != JsonValueKind.Object ||
                !response.TryGetProperty("games", out var games) ||
                games.ValueKind != JsonValueKind.Array)
                return false;

            authoritative = true;
            foreach (var game in games.EnumerateArray())
            {
                if (game.ValueKind != JsonValueKind.Object ||
                    !game.TryGetProperty("appid", out var appIdElement))
                    continue;
                var raw = appIdElement.ValueKind == JsonValueKind.Number
                    ? appIdElement.GetRawText()
                    : appIdElement.ValueKind == JsonValueKind.String
                        ? appIdElement.GetString()
                        : null;
                if (raw is { Length: > 0 and <= 10 } && raw.All(char.IsDigit))
                    parsed.Add(raw);
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal sealed record Result(bool Live, string Note, IReadOnlyDictionary<string, Summary> Players)
    {
        public static Result Empty(string note) =>
            new(false, note, new Dictionary<string, Summary>(StringComparer.Ordinal));
    }

    private sealed record RetryState(DateTimeOffset RetryAfter, string Note);

    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly object Gate = new();
    private static Result _cache = Result.Empty("");
    private static string _cacheKey = "";
    private static DateTimeOffset _freshAt = DateTimeOffset.MinValue;
    private static readonly Dictionary<string, Task<Result>> InFlight = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, RetryState> RetryByIdentity = new(StringComparer.Ordinal);
    private static readonly TimeSpan OwnedGamesTtl = TimeSpan.FromMinutes(5);
    private const int MaxJsonPayloadBytes = 8 * 1024 * 1024;
    private static string _ownedGamesKey = "";
    private static DateTimeOffset _ownedGamesAt = DateTimeOffset.MinValue;
    private static OwnedGamesResult _ownedGamesCache = OwnedGamesResult.Unavailable;

    internal sealed record OwnedGamesResult(bool Authoritative, IReadOnlySet<string> AppIds)
    {
        public static OwnedGamesResult Unavailable { get; } =
            new(false, new HashSet<string>(StringComparer.Ordinal));
    }

    internal sealed record OwnedGameInfo(string AppId, string Name, int PlaytimeMinutes);

    internal sealed record FriendOwnedGamesResult(bool Authoritative, IReadOnlyList<OwnedGameInfo> Games)
    {
        public static FriendOwnedGamesResult Unavailable { get; } =
            new(false, Array.Empty<OwnedGameInfo>());
    }

    private static readonly Dictionary<string, (DateTimeOffset At, FriendOwnedGamesResult Result)> FriendOwnedCache =
        new(StringComparer.Ordinal);
    private static readonly TimeSpan FriendOwnedTtl = TimeSpan.FromMinutes(10);
    private const int FriendOwnedParseCap = 400;

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
                      "&include_appinfo=0&include_played_free_games=1&format=json";
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
            if (!TryParseOwnedGames(json, out var ids, out var authoritative) || !authoritative)
                return OwnedGamesResult.Unavailable;
            var result = new OwnedGamesResult(true, ids);
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

    /// <summary>
    /// Public games on another Steam account. include_appinfo is required for
    /// names; the SteamID is the request identity and is never logged.
    /// </summary>
    internal static async Task<FriendOwnedGamesResult> LoadFriendOwnedGamesAsync(
        string key,
        string steamId64,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(steamId64))
            return FriendOwnedGamesResult.Unavailable;

        var identity = RequestIdentity(key, [steamId64]);
        lock (Gate)
        {
            if (FriendOwnedCache.TryGetValue(identity, out var cached) &&
                DateTimeOffset.UtcNow - cached.At < FriendOwnedTtl)
                return cached.Result;
        }

        try
        {
            var url = OwnedGamesUrl +
                      "?key=" + Uri.EscapeDataString(key) +
                      "&steamid=" + Uri.EscapeDataString(steamId64) +
                      "&include_appinfo=1&include_played_free_games=1&format=json";
            using var response = await Http.GetAsync(
                url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return FriendOwnedGamesResult.Unavailable;
            if (response.Content.Headers.ContentLength is > MaxJsonPayloadBytes)
                return FriendOwnedGamesResult.Unavailable;
            var json = await ReadBodyLimitedAsync(response.Content, MaxJsonPayloadBytes, ct)
                .ConfigureAwait(false);
            if (json is null)
                return FriendOwnedGamesResult.Unavailable;
            if (!TryParseOwnedGameCatalog(json, out var games, out var authoritative) || !authoritative)
                return FriendOwnedGamesResult.Unavailable;
            var result = new FriendOwnedGamesResult(true, games);
            lock (Gate)
            {
                FriendOwnedCache[identity] = (DateTimeOffset.UtcNow, result);
            }
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Debug("Steam friend library query unavailable: " + ex.GetType().Name);
            return FriendOwnedGamesResult.Unavailable;
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
                if (parsed.Count >= FriendOwnedParseCap) break;
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

    internal static async Task<Result> LoadSummariesAsync(
        string key,
        IReadOnlyList<string> steamId64s,
        CancellationToken ct = default)
    {
        var ids = DistinctIds(steamId64s);
        if (ids.Count == 0 || string.IsNullOrWhiteSpace(key))
            return Result.Empty(UnreachableNote);

        var requestIdentity = RequestIdentity(key, ids);
        Task<Result> pending;
        lock (Gate)
        {
            var now = DateTimeOffset.UtcNow;
            if (InFlight.TryGetValue(requestIdentity, out var current) && !current.IsCompleted)
            {
                pending = current;
            }
            else if (_freshAt != DateTimeOffset.MinValue &&
                     now - _freshAt < Ttl &&
                     string.Equals(_cacheKey, requestIdentity, StringComparison.Ordinal) &&
                     _cache.Live)
            {
                return _cache;
            }
            else if (RetryByIdentity.TryGetValue(requestIdentity, out var retry) &&
                     now < retry.RetryAfter)
            {
                return string.Equals(_cacheKey, requestIdentity, StringComparison.Ordinal)
                    ? _cache
                    : Result.Empty(retry.Note);
            }
            else
            {
                RetryByIdentity.Remove(requestIdentity);
                pending = Task.Run(() => FetchAsync(key, ids, requestIdentity, CancellationToken.None));
                InFlight[requestIdentity] = pending;
            }
        }

        try
        {
            return await pending.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            lock (Gate)
            {
                return string.Equals(_cacheKey, requestIdentity, StringComparison.Ordinal)
                    ? _cache
                    : Result.Empty(UnreachableNote);
            }
        }
    }

    private static async Task<Result> FetchAsync(
        string key,
        IReadOnlyList<string> ids,
        string requestIdentity,
        CancellationToken ct)
    {
        Result result;
        try
        {
            result = await QueryAsync(key, ids, requestIdentity, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Debug("Steam presence query unavailable: " + ex.GetType().Name);
            result = Result.Empty(UnreachableNote);
        }

        lock (Gate)
        {
            var now = DateTimeOffset.UtcNow;
            _cache = result;
            _cacheKey = requestIdentity;
            if (result.Live)
            {
                _freshAt = now;
                RetryByIdentity.Remove(requestIdentity);
            }
            else
            {
                _freshAt = DateTimeOffset.MinValue;
                var note = result.Note.Length > 0 ? result.Note : UnreachableNote;
                if (RetryByIdentity.TryGetValue(requestIdentity, out var retry) &&
                    retry.RetryAfter > now)
                {
                    RetryByIdentity[requestIdentity] = retry with { Note = note };
                }
                else
                {
                    RetryByIdentity[requestIdentity] = new RetryState(now + FailureRetry, note);
                }
            }

            InFlight.Remove(requestIdentity);
            return result;
        }
    }

    private static async Task<Result> QueryAsync(
        string key,
        IReadOnlyList<string> ids,
        string requestIdentity,
        CancellationToken ct)
    {
        var players = new Dictionary<string, Summary>(StringComparer.Ordinal);
        for (var start = 0; start < ids.Count; start += BatchSize)
        {
            var batch = ids.Skip(start).Take(BatchSize).ToList();
            var payload = await GetAsync(key, batch, requestIdentity, ct).ConfigureAwait(false);
            if (payload is null) return Result.Empty(UnreachableNote);
            if (payload.Throttled) return Result.Empty(ThrottledNote);
            if (payload.Refused) return Result.Empty(RefusedNote);
            if (!TryParseSummaries(payload.Body, players)) return Result.Empty(UnreachableNote);
        }

        return new Result(true, LiveNote, players);
    }

    /// <summary>
    /// Official GetPlayerSummaries v2 body: <c>response.players[]</c>.
    /// A row with neither personastate nor a valid positive lastlogoff stays
    /// unknown. Friends still publish personastate 0 when they are offline.
    /// </summary>
    internal static bool TryParseSummaries(string? json, IDictionary<string, Summary> players)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxJsonPayloadBytes) return false;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("response", out var response) ||
                response.ValueKind != JsonValueKind.Object ||
                !response.TryGetProperty("players", out var list) ||
                list.ValueKind != JsonValueKind.Array)
                return false;

            foreach (var player in list.EnumerateArray())
            {
                if (player.ValueKind != JsonValueKind.Object) continue;
                if (!player.TryGetProperty("steamid", out var idElement) ||
                    idElement.ValueKind != JsonValueKind.String)
                    continue;
                var steamId = idElement.GetString()?.Trim();
                if (string.IsNullOrWhiteSpace(steamId) || !steamId.All(char.IsDigit)) continue;

                var state = ReadInt(player, "personastate");
                var gameId = ReadString(player, "gameid");
                var gameName = ReadString(player, "gameextrainfo");
                var avatarUrl = ReadString(player, "avatarfull") ?? ReadString(player, "avatarmedium");
                var lastSeenUtc = ReadUnixTimeUtc(player, "lastlogoff");
                var inGame = (!string.IsNullOrWhiteSpace(gameId) && gameId != "0") ||
                             !string.IsNullOrWhiteSpace(gameName);
                var (status, statusText) = MapState(
                    state ?? (lastSeenUtc is null ? null : 0),
                    inGame);
                string? playingId = null;
                if (inGame && !string.IsNullOrWhiteSpace(gameId) && gameId.All(char.IsDigit) && gameId != "0")
                    playingId = "steam:" + gameId;

                players[steamId] = new Summary(
                    status,
                    statusText,
                    playingId,
                    string.IsNullOrWhiteSpace(gameName) ? null : gameName.Trim(),
                    lastSeenUtc,
                    avatarUrl);
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static (string Status, string? StatusText) MapState(int? personastate, bool inGame)
    {
        if (inGame) return ("ingame", null);
        return personastate switch
        {
            1 => ("online", null),
            2 => ("dnd", null),
            3 => ("away", null),
            4 => ("away", "Snooze"),
            5 => ("online", "Looking to trade"),
            6 => ("online", "Looking to play"),
            0 => ("offline", null),
            _ => ("unknown", null),
        };
    }

    private static int? ReadInt(JsonElement player, string name)
    {
        if (!player.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String &&
            int.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        return null;
    }

    private static string? ReadString(JsonElement player, string name)
    {
        if (!player.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()?.Trim();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        if (value.ValueKind == JsonValueKind.Number)
            return value.GetRawText();
        return null;
    }

    private static string? ReadUnixTimeUtc(JsonElement player, string name)
    {
        if (!player.TryGetProperty(name, out var value)) return null;

        long unix;
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (!value.TryGetInt64(out unix)) return null;
        }
        else if (value.ValueKind == JsonValueKind.String)
        {
            if (!long.TryParse(
                    value.GetString(),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out unix))
                return null;
        }
        else
        {
            return null;
        }

        if (unix <= 0) return null;
        try
        {
            return DateTimeOffset
                .FromUnixTimeSeconds(unix)
                .ToString("O", CultureInfo.InvariantCulture);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
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

    private sealed record Payload(string Body, bool Throttled, bool Refused);

    private static async Task<Payload?> GetAsync(
        string key,
        IReadOnlyList<string> ids,
        string requestIdentity,
        CancellationToken ct)
    {
        var url = SummariesUrl +
                  "?key=" + Uri.EscapeDataString(key) +
                  "&steamids=" + Uri.EscapeDataString(string.Join(',', ids));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", "ExoLauncher/1.0");

        using var response = await Http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            HoldOff(requestIdentity, response.Headers.RetryAfter?.Delta);
            AppLog.Debug("Steam presence query is being rate limited.");
            return new Payload(string.Empty, Throttled: true, Refused: false);
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            AppLog.Debug("Steam presence query returned HTTP " + (int)response.StatusCode + ".");
            return new Payload(string.Empty, Throttled: false, Refused: true);
        }

        if (!response.IsSuccessStatusCode)
        {
            AppLog.Debug("Steam presence query returned HTTP " + (int)response.StatusCode + ".");
            return null;
        }

        if (response.Content.Headers.ContentLength is > MaxJsonPayloadBytes)
            return null;
        var body = await ReadBodyLimitedAsync(response.Content, MaxJsonPayloadBytes, ct)
            .ConfigureAwait(false);
        return body is null ? null : new Payload(body, false, false);
    }

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

    private static void HoldOff(string requestIdentity, TimeSpan? retryAfter)
    {
        var wait = retryAfter is { } delta && delta > FailureRetry ? delta : FailureRetry;
        if (wait > MaxThrottleBackoff) wait = MaxThrottleBackoff;
        lock (Gate)
            RetryByIdentity[requestIdentity] = new RetryState(
                DateTimeOffset.UtcNow + wait,
                ThrottledNote);
    }

    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(5),
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ExoLauncher/1.0");
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return http;
    }
}
