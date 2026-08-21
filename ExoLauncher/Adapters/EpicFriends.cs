using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Adapters;

/// <summary>
/// The friends list Epic itself will hand out over HTTP, read with the OAuth
/// session Legendary already holds. <see cref="EpicPlaytime"/> owns that session
/// — this adapter borrows it and never reads, refreshes, or persists a token of
/// its own.
///
/// Three endpoints, each verified against a live account before being wired:
///
/// <list type="bullet">
/// <item>friends summary — the account ids the user is actually friends with</item>
/// <item>public account lookup — display names for those ids, in batches</item>
/// <item>presence last-online — the timestamp Epic last saw each of them</item>
/// </list>
///
/// What Epic will not give up over HTTP is live presence. Every presence
/// subscription path answers 404 or 403 for a launcher token; the real feed is
/// Epic's XMPP chat service, which Exo does not hold open. So these rows carry a
/// name and a last-seen and nothing else — never an invented online state.
/// </summary>
internal static class EpicFriends
{
    private const string SummaryUrl =
        "https://friends-public-service-prod.ol.epicgames.com/friends/api/v1/{0}/summary";
    private const string AccountLookupUrl =
        "https://account-public-service-prod.ol.epicgames.com/account/api/public/account?";
    private const string LastOnlineUrl =
        "https://presence-public-service-prod.ol.epicgames.com/presence/api/v1/_/{0}/last-online";

    /// <summary>Epic's public account lookup takes a batch; stay well under its cap.</summary>
    private const int AccountLookupBatch = 50;
    private const int MaxFriends = 1000;
    private const long MaxResponseBytes = 4 * 1024 * 1024;

    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FailureRetry = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MinThrottleBackoff = TimeSpan.FromMinutes(1);
    /// <summary>A time box, not a session-wide kill: Exo always tries again.</summary>
    private static readonly TimeSpan MaxThrottleBackoff = TimeSpan.FromMinutes(15);

    internal const string NoSessionNote =
        "No Epic session on this PC. Sign in to Epic once and Exo can read its friends list.";
    internal const string UnreachableNote =
        "Epic did not answer just now. Exo will try again shortly.";
    internal const string ThrottledNote =
        "Epic is rate limiting Exo. Backing off, then trying again.";
    internal const string ReachableNote =
        "Epic gives Exo the list and when it last saw each person. It does not hand out live presence, so nobody here is counted as online.";

    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly object Gate = new();
    private static Snapshot _snapshot = Snapshot.NoSession;
    private static DateTimeOffset _freshAt = DateTimeOffset.MinValue;
    private static DateTimeOffset _retryAfter = DateTimeOffset.MinValue;
    private static Task<Snapshot>? _inFlight;

    /// <summary>One Epic friend. The account id is hashed before it leaves here.</summary>
    internal sealed record Friend(string Id, string Name, string? LastOnlineUtc);

    /// <param name="SessionPresent">
    /// False means there is no Epic sign-in on this PC at all, so Exo says
    /// nothing about Epic rather than nagging someone who does not use it.
    /// </param>
    internal sealed record Snapshot(
        bool Reachable,
        string Note,
        IReadOnlyList<Friend> Friends,
        bool SessionPresent = true,
        IReadOnlyList<string>? MutualExternalIds = null)
    {
        public static Snapshot NoSession { get; } =
            new(false, NoSessionNote, Array.Empty<Friend>(), SessionPresent: false);

        public static Snapshot Unreachable(string note) =>
            new(false, note, Array.Empty<Friend>());
    }

    /// <summary>
    /// The last verified snapshot, with no network call. Callers that must not
    /// wait — a library scan, a first paint — use this.
    /// </summary>
    internal static Snapshot Cached()
    {
        lock (Gate) return _snapshot;
    }

    /// <summary>
    /// A fresh list if the cache is stale and Epic is not being given a rest,
    /// otherwise the last verified one. Bounded by <paramref name="ct"/> and by
    /// the client's own timeout, so a slow Epic degrades to the cache.
    /// </summary>
    internal static async Task<Snapshot> LoadAsync(CancellationToken ct = default)
    {
        Task<Snapshot> pending;
        lock (Gate)
        {
            var now = DateTimeOffset.UtcNow;
            if (_inFlight is { IsCompleted: false })
            {
                pending = _inFlight;
            }
            else if (_freshAt != DateTimeOffset.MinValue && now - _freshAt < Ttl)
            {
                return _snapshot;
            }
            else if (now < _retryAfter)
            {
                return _snapshot;
            }
            else
            {
                pending = _inFlight = Task.Run(() => FetchAsync(CancellationToken.None));
            }
        }

        try
        {
            return await pending.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The caller gave up waiting; the fetch keeps going and fills the
            // cache for the next call rather than being thrown away.
            return Cached();
        }
    }

    private static async Task<Snapshot> FetchAsync(CancellationToken ct)
    {
        Snapshot result;
        try
        {
            result = await QueryAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Debug("Epic friends query unavailable: " + ex.GetType().Name);
            result = Snapshot.Unreachable(UnreachableNote);
        }

        lock (Gate)
        {
            var now = DateTimeOffset.UtcNow;
            if (result.Reachable)
            {
                _snapshot = result;
                _freshAt = now;
                _retryAfter = DateTimeOffset.MinValue;
            }
            else
            {
                // Keep the last verified names on screen when Epic blips, but
                // never keep them once the session behind them is gone.
                if (ReferenceEquals(result, Snapshot.NoSession) || _snapshot.Friends.Count == 0)
                    _snapshot = result;
                else
                    _snapshot = _snapshot with { Note = result.Note };
                if (_retryAfter < now) _retryAfter = now + FailureRetry;
            }

            _inFlight = null;
            return _snapshot;
        }
    }

    private static async Task<Snapshot> QueryAsync(CancellationToken ct)
    {
        var session = await EpicPlaytime.ResolveSessionAsync(ct).ConfigureAwait(false);
        if (session is null) return Snapshot.NoSession;

        var account = Uri.EscapeDataString(session.AccountId);
        var summary = await GetAsync(
            string.Format(SummaryUrl, account), session.AccessToken, ct).ConfigureAwait(false);
        if (summary is null) return Snapshot.Unreachable(UnreachableNote);
        if (summary.Throttled) return Snapshot.Unreachable(ThrottledNote);
        if (!TryParseFriendIds(summary.Body, out var ids)) return Snapshot.Unreachable(UnreachableNote);
        if (ids.Count == 0)
            return new Snapshot(true, ReachableNote, Array.Empty<Friend>(), MutualExternalIds: Array.Empty<string>());

        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var start = 0; start < ids.Count; start += AccountLookupBatch)
        {
            var batch = ids.Skip(start).Take(AccountLookupBatch).ToList();
            var query = string.Join('&', batch.Select(id => "accountId=" + Uri.EscapeDataString(id)));
            var lookup = await GetAsync(AccountLookupUrl + query, session.AccessToken, ct)
                .ConfigureAwait(false);
            if (lookup is null) continue;
            if (lookup.Throttled) return Snapshot.Unreachable(ThrottledNote);
            ReadAccountNames(lookup.Body, names);
        }

        // A friends list Exo cannot put names to is not a friends list.
        if (names.Count == 0) return Snapshot.Unreachable(UnreachableNote);

        var lastOnline = new Dictionary<string, string>(StringComparer.Ordinal);
        var presence = await GetAsync(
            string.Format(LastOnlineUrl, account), session.AccessToken, ct).ConfigureAwait(false);
        // Last-seen is enrichment on top of the list. Losing it is not a failure.
        if (presence is { Throttled: false }) ReadLastOnline(presence.Body, lastOnline);

        return new Snapshot(true, ReachableNote, Build(ids, names, lastOnline), MutualExternalIds: ids);
    }

    internal static IReadOnlyList<Friend> Build(
        IReadOnlyList<string> ids,
        IReadOnlyDictionary<string, string> names,
        IReadOnlyDictionary<string, string> lastOnline)
    {
        var friends = new List<Friend>(ids.Count);
        foreach (var id in ids)
        {
            // No name, no row. Exo will not print a bare account id as a person.
            if (!names.TryGetValue(id, out var name) || string.IsNullOrWhiteSpace(name)) continue;
            lastOnline.TryGetValue(id, out var seen);
            friends.Add(new Friend("epic:" + HashAccount(id), name.Trim(), seen));
        }

        return friends
            .GroupBy(friend => friend.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(friend => friend.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Row keys are one-way. Like the Steam cache, a raw store account id never
    /// reaches a bridge payload, a log, or the link file.
    /// </summary>
    internal static string HashAccount(string accountId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes("exo-epic-friend:" + accountId));
        return Convert.ToHexString(bytes.AsSpan(0, 8)).ToLowerInvariant();
    }

    /// <summary>friends/api/v1/{account}/summary — <c>friends[].accountId</c>.</summary>
    internal static bool TryParseFriendIds(string? json, out IReadOnlyList<string> ids)
    {
        var parsed = new List<string>();
        ids = parsed;
        if (string.IsNullOrWhiteSpace(json)) return false;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("friends", out var friends) ||
                friends.ValueKind != JsonValueKind.Array)
                return false;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var friend in friends.EnumerateArray())
            {
                if (parsed.Count >= MaxFriends) break;
                if (friend.ValueKind != JsonValueKind.Object ||
                    !friend.TryGetProperty("accountId", out var idElement) ||
                    idElement.ValueKind != JsonValueKind.String)
                    continue;
                var id = idElement.GetString()?.Trim();
                if (IsAccountId(id) && seen.Add(id!)) parsed.Add(id!);
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>account/api/public/account?accountId=… — <c>[{ id, displayName }]</c>.</summary>
    internal static void ReadAccountNames(string? json, IDictionary<string, string> names)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return;
            foreach (var account in doc.RootElement.EnumerateArray())
            {
                if (account.ValueKind != JsonValueKind.Object ||
                    !account.TryGetProperty("id", out var idElement) ||
                    idElement.ValueKind != JsonValueKind.String ||
                    !account.TryGetProperty("displayName", out var nameElement) ||
                    nameElement.ValueKind != JsonValueKind.String)
                    continue;

                var id = idElement.GetString()?.Trim();
                var name = nameElement.GetString()?.Trim();
                if (!IsAccountId(id) || string.IsNullOrWhiteSpace(name) || name.Length > 64) continue;
                names[id!] = name;
            }
        }
        catch (JsonException)
        {
            // A changed payload means no names, not wrong names.
        }
    }

    /// <summary>
    /// presence/api/v1/_/{account}/last-online — an object keyed by friend
    /// account id whose value is <c>[{ last_online }]</c>. This is a last-seen
    /// timestamp, never a live state: a recent one must not be read as online.
    /// </summary>
    internal static void ReadLastOnline(string? json, IDictionary<string, string> lastOnline)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
            foreach (var entry in doc.RootElement.EnumerateObject())
            {
                if (!IsAccountId(entry.Name)) continue;
                var stamp = ReadStamp(entry.Value);
                if (stamp is not null) lastOnline[entry.Name] = stamp;
            }
        }
        catch (JsonException)
        {
            // Losing last-seen is fine; the list still stands on its own.
        }
    }

    private static string? ReadStamp(JsonElement value)
    {
        var row = value.ValueKind switch
        {
            JsonValueKind.Array when value.GetArrayLength() > 0 => value[0],
            JsonValueKind.Object => value,
            _ => default,
        };

        if (row.ValueKind != JsonValueKind.Object ||
            !row.TryGetProperty("last_online", out var stampElement) ||
            stampElement.ValueKind != JsonValueKind.String)
            return null;

        var raw = stampElement.GetString()?.Trim();
        return DateTimeOffset.TryParse(
            raw,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed.ToUniversalTime().ToString("O")
            : null;
    }

    private static bool IsAccountId(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length is > 0 and <= 64 &&
        value.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private sealed record Payload(string Body, bool Throttled);

    private static async Task<Payload?> GetAsync(string url, string accessToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation(
            "User-Agent",
            "UELauncher/11.0.1-14907503+++Portal+Release-Live Windows/10.0.19041.1.256.64bit");

        using var response = await Http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            HoldOff(response.Headers.RetryAfter?.Delta);
            AppLog.Debug("Epic friends query is being rate limited.");
            return new Payload(string.Empty, Throttled: true);
        }

        if (!response.IsSuccessStatusCode)
        {
            AppLog.Debug($"Epic friends query returned HTTP {(int)response.StatusCode}.");
            return null;
        }

        if (response.Content.Headers.ContentLength is > MaxResponseBytes) return null;
        await response.Content.LoadIntoBufferAsync(MaxResponseBytes, ct).ConfigureAwait(false);
        return new Payload(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false), false);
    }

    private static void HoldOff(TimeSpan? retryAfter)
    {
        var wait = retryAfter is { } delta && delta > MinThrottleBackoff ? delta : MinThrottleBackoff;
        if (wait > MaxThrottleBackoff) wait = MaxThrottleBackoff;
        lock (Gate) _retryAfter = DateTimeOffset.UtcNow + wait;
    }

    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient(new HttpClientHandler
        {
            // Never forward the bearer session to a redirected host.
            AllowAutoRedirect = false,
        })
        {
            Timeout = TimeSpan.FromSeconds(8),
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ExoLauncher/1.0");
        return http;
    }
}
