using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>
/// Whether a game outputs real HDR, from its PCGamingWiki page (the "hdr" field of its video
/// settings: true, false, hackable or unknown). Asked once per game and kept 30 days
/// (7 for a game the wiki has no answer for) in hdr-games.json.
/// </summary>
internal static partial class HdrGames
{
    private sealed record Entry(string Hdr, DateTimeOffset At);

    private static readonly object Gate = new();
    private static readonly ConcurrentDictionary<string, Task<string>> Pending = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, Entry>? _cache;
    private static readonly HttpClient Http = CreateClient();

    // The wiki refuses requests that do not say who is asking.
    private static HttpClient CreateClient()
    {
        var http = ExoHttp.Create(TimeSpan.FromSeconds(12), new HttpClientHandler { AllowAutoRedirect = false });
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ExoLauncher (+https://github.com/UhhErix/ExoLauncher)");
        return http;
    }

    private static string FilePath => Path.Combine(PathHelper.AppDataDir, "hdr-games.json");

    /// <summary>The wiki's answer as kept: "true", "false", "hackable", "unknown", or null when not asked yet.</summary>
    internal static string? Cached(string gameId)
    {
        lock (Gate) return Load().TryGetValue(gameId, out var entry) ? entry.Hdr : null;
    }

    /// <summary>True only when the wiki says the game outputs HDR itself.</summary>
    internal static bool? Supports(string gameId) => Cached(gameId) switch
    {
        null or "unknown" => null,
        "true" => true,
        _ => false,
    };

    /// <summary>The kept answer while fresh, else one wiki request per game at a time.</summary>
    internal static Task<string> LookupAsync(GameEntry game, CancellationToken ct)
    {
        lock (Gate)
        {
            if (Load().TryGetValue(game.Id, out var kept) &&
                DateTimeOffset.UtcNow - kept.At < TimeSpan.FromDays(kept.Hdr == "unknown" ? 7 : 30))
                return Task.FromResult(kept.Hdr);
        }
        return Pending.GetOrAdd(game.Id, _ => Task.Run(async () =>
        {
            try { return await AskAsync(game, ct).ConfigureAwait(false); }
            finally { Pending.TryRemove(game.Id, out Task<string>? _); }
        }, CancellationToken.None));
    }

    private static async Task<string> AskAsync(GameEntry game, CancellationToken ct)
    {
        string hdr;
        try { hdr = await FetchAsync(game, ct).ConfigureAwait(false) ?? "unknown"; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            AppLog.Debug("HDR lookup: " + ex.GetType().Name);
            return Cached(game.Id) ?? "unknown";
        }
        lock (Gate)
        {
            Load()[game.Id] = new Entry(hdr, DateTimeOffset.UtcNow);
            Save();
        }
        return hdr;
    }

    private static async Task<string?> FetchAsync(GameEntry game, CancellationToken ct)
    {
        var title = await PageTitleAsync(game, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(title)) return null;
        var url = "https://www.pcgamingwiki.com/w/api.php?action=parse&format=json&formatversion=2&redirects=1&prop=wikitext&page=" +
            Uri.EscapeDataString(title);
        using var deadline = ExoHttpTransport.Deadline(Http, ct);
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        using var doc = await ExoHttpTransport.ReadJsonAsync(response.Content, 4 * 1024 * 1024, deadline.Token).ConfigureAwait(false);
        if (doc is null || !doc.RootElement.TryGetProperty("parse", out var parse) ||
            !parse.TryGetProperty("wikitext", out var text) || text.ValueKind != JsonValueKind.String)
            return null;
        return ReadHdrField(text.GetString());
    }

    /// <summary>The page for a Steam app id (the wiki's own lookup), else the game's title.</summary>
    private static async Task<string?> PageTitleAsync(GameEntry game, CancellationToken ct)
    {
        if (CoverArtService.SteamAppId(game) is { } appId)
        {
            using var deadline = ExoHttpTransport.Deadline(Http, ct);
            using var response = await Http.GetAsync("https://www.pcgamingwiki.com/api/appid.php?appid=" + Uri.EscapeDataString(appId),
                HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (PageFromWikiUrl(response.Headers.Location) is { } page) return page;
        }
        return TrademarkMarks().Replace(game.Title, "").Trim();
    }

    /// <summary>"https://www.pcgamingwiki.com/wiki/Hell_is_Us" gives "Hell is Us"; any other host gives null.</summary>
    internal static string? PageFromWikiUrl(Uri? location)
    {
        if (location is null || !location.IsAbsoluteUri || location.Scheme != Uri.UriSchemeHttps ||
            !location.Host.Equals("www.pcgamingwiki.com", StringComparison.OrdinalIgnoreCase) ||
            !location.AbsolutePath.StartsWith("/wiki/", StringComparison.Ordinal))
            return null;
        var page = Uri.UnescapeDataString(location.AbsolutePath["/wiki/".Length..]).Replace('_', ' ').Trim();
        return page.Length == 0 ? null : page;
    }

    /// <summary>The value of the page's "|hdr = ..." field, lower case; null when the page has none.</summary>
    internal static string? ReadHdrField(string? wikitext)
    {
        if (wikitext is null) return null;
        var match = HdrField().Match(wikitext);
        if (!match.Success) return null;
        var value = match.Groups[1].Value.Trim().ToLowerInvariant();
        return value is "true" or "false" or "hackable" ? value : "unknown";
    }

    [GeneratedRegex(@"^\s*\|\s*hdr\s*=\s*([^\r\n|]*)", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex HdrField();

    [GeneratedRegex(@"[™®©]")]
    private static partial Regex TrademarkMarks();

    private static Dictionary<string, Entry> Load()
    {
        if (_cache is not null) return _cache;
        _cache = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(FilePath) && new FileInfo(FilePath).Length < 4 * 1024 * 1024 &&
                JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(FilePath)) is { } read)
                foreach (var pair in read) _cache[pair.Key] = pair.Value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Debug("HDR cache unreadable: " + ex.GetType().Name);
        }
        return _cache;
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(PathHelper.AppDataDir);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_cache));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Debug("HDR cache not saved: " + ex.GetType().Name);
        }
    }
}
