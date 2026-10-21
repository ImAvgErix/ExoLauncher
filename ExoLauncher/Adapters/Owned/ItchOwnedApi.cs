using System.Net.Http.Headers;
using System.Text.Json;
using ExoLauncher.Models;

namespace ExoLauncher.Adapters;

/// <summary>
/// Official itch.io owned-keys API.
/// https://api.itch.io/profile/owned-keys
/// </summary>
internal static class ItchOwnedApi
{
    internal const string OwnedKeysUrl = "https://api.itch.io/profile/owned-keys";

    internal static IReadOnlyList<GameEntry> ParseOwnedKeys(string? json)
    {
        var games = new List<GameEntry>();
        if (string.IsNullOrWhiteSpace(json)) return games;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!TryGetArray(root, out var keys)) return games;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in keys.EnumerateArray())
            {
                if (key.ValueKind != JsonValueKind.Object) continue;
                var game = Prop(key, "game") ?? key;
                var id = ReadNumberOrString(game, "id") ?? ReadNumberOrString(key, "game_id")
                    ?? ReadNumberOrString(key, "gameId");
                var title = ReadString(game, "title") ?? ReadString(game, "name");
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title)) continue;
                // itch classification: game | tool | assets | game_mod |
                // physical_game | soundtrack | other | comic | book. Only a
                // game belongs on the owned-not-installed shelf.
                var classification = ReadString(game, "classification");
                if (!string.IsNullOrWhiteSpace(classification) &&
                    !classification.Equals("game", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!long.TryParse(id, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var numericId) || numericId <= 0) continue;
                id = numericId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var rowId = "itch:" + id;
                if (!seen.Add(rowId)) continue;
                games.Add(new GameEntry
                {
                    Id = rowId,
                    Title = title.Trim(),
                    Store = StoreKind.Itch,
                    Installed = false,
                    Owned = true,
                    EntitlementState = EntitlementState.Owned,
                    CanInstall = true,
                    CoverUrl = ReadString(game, "cover_url") ?? ReadString(game, "coverUrl"),
                    LaunchTarget = id,
                    Status = "Not installed",
                    LaunchNote = "Owned on itch.io. Install through the itch app.",
                });
            }
        }
        catch (JsonException)
        {
            return Array.Empty<GameEntry>();
        }

        return games;
    }

    internal static async Task<IReadOnlyList<GameEntry>> LoadOwnedAsync(
        string apiKey,
        HttpMessageHandler handler,
        CancellationToken ct)
    {
        using var http = ExoHttp.Create(TimeSpan.FromSeconds(20), handler, disposeHandler: false);
        var games = new Dictionary<string, GameEntry>(StringComparer.Ordinal);
        var pageHashes = new HashSet<string>(StringComparer.Ordinal);
        long totalBytes = 0;
        for (var page = 1; page <= 1000; page++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, OwnedKeysUrl + "?page=" + page);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var body = new MemoryStream();
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var buffer = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                totalBytes += count;
                if (body.Length + count > 2 * 1024 * 1024 || totalBytes > 32 * 1024 * 1024)
                    throw new InvalidDataException("The itch.io library exceeded the safe response limit. Retry the scan.");
                body.Write(buffer, 0, count);
            }
            var json = System.Text.Encoding.UTF8.GetString(body.GetBuffer(), 0, checked((int)body.Length));
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!TryGetArray(root, out var keys)) throw new InvalidDataException("The itch.io library response was incomplete.");
            var perPage = 100;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("errors", out _)) throw new InvalidDataException("itch.io could not verify the library.");
                if (root.TryGetProperty("page", out var returnedPage) && (!returnedPage.TryGetInt32(out var index) || index != page))
                    throw new InvalidDataException("itch.io returned an unexpected library page.");
                if ((root.TryGetProperty("perPage", out var pageSize) || root.TryGetProperty("per_page", out pageSize)) &&
                    (pageSize.ValueKind != JsonValueKind.Number || !pageSize.TryGetInt32(out perPage) || perPage is < 1 or > 500))
                    throw new InvalidDataException("itch.io returned invalid library pagination.");
            }
            var rawCount = keys.GetArrayLength();
            if (rawCount > perPage) throw new InvalidDataException("itch.io returned invalid library pagination.");
            foreach (var game in ParseOwnedKeys(json)) games[game.Id] = game;
            if (rawCount < perPage) return games.Values.ToArray();
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(keys.GetRawText())));
            if (!pageHashes.Add(hash)) throw new InvalidDataException("itch.io repeated a library page. Retry the scan.");
        }
        throw new InvalidDataException("The itch.io library exceeded the page limit. Retry the scan.");
    }

    private static bool TryGetArray(JsonElement root, out JsonElement array)
    {
        foreach (var name in new[] { "owned_keys", "ownedKeys", "keys" })
        {
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty(name, out array) &&
                array.ValueKind == JsonValueKind.Array)
                return true;
        }

        array = root;
        return root.ValueKind == JsonValueKind.Array;
    }

    private static JsonElement? Prop(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    private static string? ReadString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
            return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static string? ReadNumberOrString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            _ => null,
        };
    }

}
