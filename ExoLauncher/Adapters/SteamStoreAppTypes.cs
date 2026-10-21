using System.Net.Http.Headers;
using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Adapters;

/// <summary>
/// The Steam store's type for an owned app the local appinfo has no record
/// of, so a game the Web API names is not dropped and a tool or DLC is not
/// promoted. Answers are kept on disk; a few new ids are asked per scan.
/// </summary>
internal static class SteamStoreAppTypes
{
    private const int MaxLookupsPerScan = 10;
    private static readonly TimeSpan ScanBudget = TimeSpan.FromSeconds(6);
    private static readonly object Gate = new();
    private static Dictionary<string, string>? _types;

    private static string CachePath => Path.Combine(PathHelper.AppDataDir, "steam-app-types.json");

    /// <summary>
    /// Known store types for <paramref name="appIds"/>, asking the store for
    /// up to <see cref="MaxLookupsPerScan"/> it has not answered yet. An app
    /// with no store page, or a DLC (it names its full game), stays unknown.
    /// </summary>
    internal static async Task<IReadOnlyDictionary<string, string>> ResolveAsync(
        IReadOnlyCollection<string> appIds,
        CancellationToken ct)
    {
        var types = Load();
        List<string> ask;
        lock (Gate)
            ask = appIds.Where(id => !types.ContainsKey(id)).Take(MaxLookupsPerScan).ToList();
        if (ask.Count > 0)
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(ScanBudget);
            using var http = ExoHttp.Create(TimeSpan.FromSeconds(3), new HttpClientHandler { AllowAutoRedirect = false });
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ExoLauncher/1.0");
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            var learned = false;
            foreach (var id in ask)
            {
                try
                {
                    var json = await http.GetStringAsync(
                        $"https://store.steampowered.com/api/appdetails?appids={id}&filters=basic", budget.Token).ConfigureAwait(false);
                    if (ParseAppDetailsType(json, id) is not { } type) break;
                    lock (Gate) types[id] = type;
                    learned = true;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { break; }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    AppLog.Debug($"Steam store type lookup failed: {ex.Message}");
                    break;
                }
            }
            if (learned) Save(types);
        }

        lock (Gate)
            return appIds.Where(id => types.TryGetValue(id, out var type) && type.Length > 0)
                .ToDictionary(id => id, id => types[id], StringComparer.Ordinal);
    }

    /// <summary>
    /// The <c>type</c> of an appdetails answer; "" when the store has no page
    /// for it or it belongs to another app (DLC names its full game); null
    /// when there was no answer to keep (rate limit, malformed body).
    /// </summary>
    internal static string? ParseAppDetailsType(string? json, string appId)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty(appId, out var wrap) || wrap.ValueKind != JsonValueKind.Object ||
                !wrap.TryGetProperty("success", out var success))
                return null;
            if (success.ValueKind != JsonValueKind.True ||
                !wrap.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
                data.TryGetProperty("fullgame", out _) ||
                !data.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
                return "";
            return type.GetString()!.Trim().ToLowerInvariant();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Dictionary<string, string> Load()
    {
        lock (Gate)
        {
            if (_types is not null) return _types;
            _types = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                if (File.Exists(CachePath) &&
                    JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(CachePath)) is { } saved)
                {
                    foreach (var pair in saved)
                        if (Cli.SteamProtocol.IsValidAppId(pair.Key)) _types[pair.Key] = pair.Value ?? "";
                }
            }
            catch { /* a lost cache only means asking again */ }
            return _types;
        }
    }

    private static void Save(Dictionary<string, string> types)
    {
        try
        {
            string payload;
            lock (Gate) payload = JsonSerializer.Serialize(types);
            Directory.CreateDirectory(PathHelper.AppDataDir);
            var temp = CachePath + ".tmp";
            File.WriteAllText(temp, payload);
            File.Move(temp, CachePath, overwrite: true);
        }
        catch { /* a lost cache only means asking again */ }
    }
}
