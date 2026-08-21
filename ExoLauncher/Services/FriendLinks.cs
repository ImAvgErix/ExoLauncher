using System.Text.Json;
using System.Text.Json.Serialization;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// Which store accounts the user says are the same human as someone on their
/// Exo list.
///
/// Exo cannot work this out. Two accounts sharing a name, an avatar, or a
/// library are still two accounts, so nothing here is ever inferred — every
/// entry is a deliberate act by the user, and it lives on this PC in its own
/// small file next to settings.json.
/// </summary>
internal static class FriendLinks
{
    private const int MaxPerPerson = 12;
    private const int MaxTotal = 400;
    private const int HandleMax = 24;
    private const int FriendIdMax = 64;
    private const int StoreMax = 16;

    private static readonly object Gate = new();

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>One store row the user tied to an Exo handle.</summary>
    internal sealed record Link(string Id, string Store);

    private static string LinksPath => Path.Combine(PathHelper.AppDataDir, "friend-links.json");

    internal static IReadOnlyDictionary<string, IReadOnlyList<Link>> All()
    {
        lock (Gate) return Read();
    }

    /// <summary>Links for one Exo handle, in the order the user made them.</summary>
    internal static IReadOnlyList<Link> For(string? handle)
    {
        var key = NormalizeHandle(handle);
        if (key.Length == 0) return Array.Empty<Link>();
        lock (Gate)
        {
            return Read().TryGetValue(key, out var links) ? links : Array.Empty<Link>();
        }
    }

    /// <summary>Every store row that now belongs to an Exo person instead of a store list.</summary>
    internal static IReadOnlySet<string> LinkedIds()
    {
        lock (Gate)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var links in Read().Values)
                foreach (var link in links)
                    ids.Add(link.Id);
            return ids;
        }
    }

    /// <returns>Null on success, otherwise the one thing the caller should say.</returns>
    internal static string? Add(string? handle, string? friendId, string? store)
    {
        var key = NormalizeHandle(handle);
        var id = NormalizeFriendId(friendId);
        var storeKey = NormalizeStore(store);
        if (key.Length == 0 || id is null || storeKey is null) return "That link is not valid.";

        lock (Gate)
        {
            var all = Read().ToDictionary(
                pair => pair.Key,
                pair => pair.Value.ToList(),
                StringComparer.Ordinal);

            if (all.Values.Sum(links => links.Count) >= MaxTotal)
                return "That is as many linked accounts as Exo keeps.";

            foreach (var (owner, links) in all)
            {
                if (!links.Any(link => string.Equals(link.Id, id, StringComparison.Ordinal))) continue;
                return string.Equals(owner, key, StringComparison.Ordinal)
                    ? "Already linked to this person."
                    : $"That account is already linked to @{owner}.";
            }

            if (!all.TryGetValue(key, out var mine))
            {
                mine = new List<Link>();
                all[key] = mine;
            }
            if (mine.Count >= MaxPerPerson) return $"One person holds {MaxPerPerson} linked accounts.";

            mine.Add(new Link(id, storeKey));
            Write(all);
            return null;
        }
    }

    internal static bool Remove(string? handle, string? friendId)
    {
        var key = NormalizeHandle(handle);
        var id = NormalizeFriendId(friendId);
        if (key.Length == 0 || id is null) return false;

        lock (Gate)
        {
            var all = Read().ToDictionary(
                pair => pair.Key,
                pair => pair.Value.ToList(),
                StringComparer.Ordinal);
            if (!all.TryGetValue(key, out var mine)) return false;
            if (mine.RemoveAll(link => string.Equals(link.Id, id, StringComparison.Ordinal)) == 0)
                return false;
            if (mine.Count == 0) all.Remove(key);
            Write(all);
            return true;
        }
    }

    /// <summary>Drops every link for a handle. Called when that person is removed.</summary>
    internal static void Forget(string? handle)
    {
        var key = NormalizeHandle(handle);
        if (key.Length == 0) return;

        lock (Gate)
        {
            var all = Read().ToDictionary(
                pair => pair.Key,
                pair => pair.Value.ToList(),
                StringComparer.Ordinal);
            if (!all.Remove(key)) return;
            Write(all);
        }
    }

    private static Dictionary<string, IReadOnlyList<Link>> Read()
    {
        var result = new Dictionary<string, IReadOnlyList<Link>>(StringComparer.Ordinal);
        try
        {
            var path = LinksPath;
            if (!File.Exists(path)) return result;
            var document = JsonSerializer.Deserialize<LinkDocument>(File.ReadAllText(path), Json);
            if (document?.Links is null) return result;

            foreach (var (rawHandle, rawLinks) in document.Links)
            {
                var key = NormalizeHandle(rawHandle);
                if (key.Length == 0 || rawLinks is null) continue;
                var links = new List<Link>();
                foreach (var raw in rawLinks)
                {
                    var id = NormalizeFriendId(raw?.Id);
                    var store = NormalizeStore(raw?.Store);
                    if (id is null || store is null) continue;
                    if (links.Any(link => string.Equals(link.Id, id, StringComparison.Ordinal))) continue;
                    links.Add(new Link(id, store));
                    if (links.Count >= MaxPerPerson) break;
                }

                if (links.Count > 0) result[key] = links;
            }
        }
        catch (Exception ex)
        {
            AppLog.Debug("Friend links read failed: " + ex.GetType().Name);
        }

        return result;
    }

    private static void Write(Dictionary<string, List<Link>> all)
    {
        try
        {
            Directory.CreateDirectory(PathHelper.AppDataDir);
            var document = new LinkDocument(all
                .Where(pair => pair.Value.Count > 0)
                .ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.Select(link => new StoredLink(link.Id, link.Store)).ToList(),
                    StringComparer.Ordinal));
            var path = LinksPath;
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(document, Json));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLog.Debug("Friend links write failed: " + ex.GetType().Name);
        }
    }

    private static string NormalizeHandle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var raw = value.Trim();
        if (raw.StartsWith("exo:", StringComparison.OrdinalIgnoreCase)) raw = raw[4..];
        var chars = raw
            .ToLowerInvariant()
            .Where(ch => ch is '_' || (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9'))
            .Take(HandleMax)
            .ToArray();
        return new string(chars);
    }

    private static string? NormalizeFriendId(string? value)
    {
        var raw = (value ?? string.Empty).Trim();
        if (raw.Length is 0 or > FriendIdMax) return null;
        return raw.All(ch => ch is ':' or '_' or '-' || char.IsAsciiLetterOrDigit(ch)) ? raw : null;
    }

    private static string? NormalizeStore(string? value)
    {
        var raw = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (raw.Length is 0 or > StoreMax) return null;
        return raw.All(char.IsAsciiLetterLower) ? raw : null;
    }

    private sealed record StoredLink(string? Id, string? Store);

    private sealed record LinkDocument(Dictionary<string, List<StoredLink>>? Links);
}
