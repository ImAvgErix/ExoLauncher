using System.Net;
using System.Text;
using System.Text.Json;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>
/// The catalog layer: genre, release year, and a one-line description keyed by
/// the store's own product id. Covers already come from the store CDN; this is
/// the text that sits beside them. Fetched only for the card the user opened,
/// then cached on disk so the details overlay never waits twice.
/// </summary>
internal sealed class StoreMetadataService
{
    private const long MaxPayloadBytes = 2 * 1024 * 1024;
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(8);
    private static readonly HttpClient Client = CreateClient();

    private readonly object _gate = new();
    private readonly Dictionary<string, StoreMetadata> _memory = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _cachePath;
    private readonly Func<Uri, CancellationToken, Task<string?>> _fetch;
    private bool _loaded;

    public StoreMetadataService()
        : this(Path.Combine(PathHelper.AppDataDir, "store-metadata.json"), FetchAsync)
    {
    }

    internal StoreMetadataService(string cachePath, Func<Uri, CancellationToken, Task<string?>> fetch)
    {
        _cachePath = cachePath;
        _fetch = fetch;
    }

    public sealed record StoreMetadata(string? Genre, int? Year, string? Description);

    /// <summary>Cached metadata only. Never blocks and never hits the network.</summary>
    public StoreMetadata? Peek(string gameId)
    {
        var key = Key(gameId);
        if (key is null) return null;
        LoadOnce();
        lock (_gate)
        {
            return _memory.TryGetValue(key, out var hit) ? hit : null;
        }
    }

    public StoreMetadata? Peek(GameEntry game)
    {
        var builtIn = BuiltIn(game);
        var appId = CoverArtService.MetadataSteamAppId(game);
        if (appId is null) return builtIn;
        return Merge(Peek("steam:" + appId), builtIn);
    }

    public async Task<StoreMetadata?> GetAsync(GameEntry game, CancellationToken cancellationToken = default)
    {
        var builtIn = BuiltIn(game);
        var appId = CoverArtService.MetadataSteamAppId(game);
        if (appId is null) return builtIn;
        var catalog = await GetAsync("steam:" + appId, cancellationToken).ConfigureAwait(false);
        return Merge(catalog, builtIn);
    }

    public async Task<StoreMetadata?> GetAsync(string gameId, CancellationToken cancellationToken = default)
    {
        var key = Key(gameId);
        if (key is null) return null;

        var cached = Peek(gameId);
        if (cached is not null) return cached;

        var appId = key["steam:".Length..];
        var uri = new Uri(
            $"https://store.steampowered.com/api/appdetails?appids={appId}&l=english",
            UriKind.Absolute);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(FetchTimeout);
        string? json;
        try
        {
            json = await _fetch(uri, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }

        var parsed = Parse(json, appId);
        if (parsed is null) return null;

        lock (_gate)
        {
            _memory[key] = parsed;
        }
        Persist();
        return parsed;
    }

    /// <summary>Only Steam publishes a catalog Exo can key by product id today.</summary>
    internal static string? Key(string? gameId)
    {
        if (string.IsNullOrWhiteSpace(gameId)) return null;
        var match = System.Text.RegularExpressions.Regex.Match(gameId, @"^steam:(\d+)");
        return match.Success ? "steam:" + match.Groups[1].Value : null;
    }

    internal static StoreMetadata? Parse(string? json, string appId)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxPayloadBytes) return null;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!document.RootElement.TryGetProperty(appId, out var entry)) return null;
            if (!entry.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True)
                return null;
            if (!entry.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                return null;

            string? genre = null;
            if (data.TryGetProperty("genres", out var genres) && genres.ValueKind == JsonValueKind.Array)
            {
                genre = genres.EnumerateArray()
                    .Select(item => item.TryGetProperty("description", out var d) ? d.GetString() : null)
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
                    ?.Trim();
            }

            int? year = null;
            if (data.TryGetProperty("release_date", out var release) &&
                release.ValueKind == JsonValueKind.Object &&
                release.TryGetProperty("date", out var date) &&
                date.ValueKind == JsonValueKind.String)
            {
                var match = System.Text.RegularExpressions.Regex.Match(date.GetString() ?? string.Empty, @"(19|20)\d{2}");
                if (match.Success && int.TryParse(match.Value, out var parsedYear)) year = parsedYear;
            }

            string? description = null;
            if (data.TryGetProperty("short_description", out var shortDescription) &&
                shortDescription.ValueKind == JsonValueKind.String)
            {
                description = shortDescription.GetString()?.Trim();
                if (description is { Length: > 300 }) description = description[..300].TrimEnd() + "…";
            }

            if (genre is null && year is null && string.IsNullOrWhiteSpace(description)) return null;
            return new StoreMetadata(
                string.IsNullOrWhiteSpace(genre) ? null : genre,
                year,
                string.IsNullOrWhiteSpace(description) ? null : description);
        }
        catch
        {
            return null;
        }
    }

    private static StoreMetadata? Merge(StoreMetadata? catalog, StoreMetadata? fallback)
    {
        if (catalog is null) return fallback;
        if (fallback is null) return catalog;
        return new StoreMetadata(
            catalog.Genre ?? fallback.Genre,
            catalog.Year ?? fallback.Year,
            catalog.Description ?? fallback.Description);
    }

    /// <summary>
    /// Exact first-party product identities whose public client metadata does
    /// not expose a consumer catalog endpoint. These values fill only missing
    /// fields; a live catalog response always wins.
    /// </summary>
    internal static StoreMetadata? BuiltIn(GameEntry game)
    {
        var title = game.Title.Trim().ToLowerInvariant();
        return title switch
        {
            "valorant" => new StoreMetadata("Tactical shooter", 2020, null),
            "league of legends" => new StoreMetadata("MOBA", 2009, null),
            "teamfight tactics" => new StoreMetadata("Strategy", 2019, null),
            "legends of runeterra" => new StoreMetadata("Card game", 2020, null),
            "2xko" => new StoreMetadata("Fighting", 2025, null),
            "deadlock" when CoverArtService.MetadataSteamAppId(game) == "1422450" =>
                new StoreMetadata("Action", 2024, null),
            _ => null,
        };
    }

    private void LoadOnce()
    {
        lock (_gate)
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                if (!File.Exists(_cachePath)) return;
                var text = File.ReadAllText(_cachePath);
                var map = JsonSerializer.Deserialize<Dictionary<string, StoreMetadata>>(text);
                if (map is null) return;
                foreach (var pair in map)
                {
                    if (pair.Value is not null) _memory[pair.Key] = pair.Value;
                }
            }
            catch
            {
                /* a stale or partial cache is not worth failing a details open */
            }
        }
    }

    private void Persist()
    {
        try
        {
            Dictionary<string, StoreMetadata> snapshot;
            lock (_gate)
            {
                snapshot = new Dictionary<string, StoreMetadata>(_memory, StringComparer.OrdinalIgnoreCase);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            File.WriteAllText(_cachePath, JsonSerializer.Serialize(snapshot));
        }
        catch
        {
            /* cache is an optimization, never a requirement */
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ExoLauncher/1.0");
        return client;
    }

    private static async Task<string?> FetchAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await Client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK ||
            response.Content.Headers.ContentLength is > MaxPayloadBytes)
            return null;

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
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
}
