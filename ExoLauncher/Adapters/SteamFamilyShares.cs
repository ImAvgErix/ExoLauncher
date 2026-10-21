using System.Text.Json;
using ExoLauncher.Adapters.Cli;
using ExoLauncher.Helpers;

namespace ExoLauncher.Adapters;

/// <summary>
/// The games a Steam account's family shares with it, as the running Steam client last listed them, kept per account
/// (<c>steam-family.json</c>). Only the client knows family licenses (the Web API leaves them out), and Exo closes an
/// idle Steam: without this, every family game left the library until Steam ran again (107 of 217 games). Written
/// only from a client answer; read only while the client can't answer. A share someone stops is dropped at Steam's next
/// answer, and a game bought since counts as the account's own.
/// </summary>
internal static class SteamFamilyShares
{
    internal static string FilePath { get; set; } = Path.Combine(PathHelper.AppDataDir, "steam-family.json");

    private static readonly object Gate = new();

    /// <summary>The client's current family list for <paramref name="scope"/> (empty when the account left its family).</summary>
    internal static void Remember(string? scope, IReadOnlySet<string> appIds)
    {
        if (string.IsNullOrWhiteSpace(scope)) return;
        lock (Gate)
        {
            var all = Read();
            if (all.TryGetValue(scope, out var kept) && kept.SetEquals(appIds)) return;
            all[scope] = appIds.ToHashSet(StringComparer.Ordinal);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var temp = FilePath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(all.ToDictionary(p => p.Key, p => p.Value.Order(StringComparer.Ordinal).ToArray())));
                File.Move(temp, FilePath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Debug("Steam family shares not saved: " + ex.GetType().Name);
            }
        }
    }

    /// <summary>The family list the client last gave for <paramref name="scope"/>, or null when it never did.</summary>
    internal static IReadOnlySet<string>? Recall(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope)) return null;
        lock (Gate) return Read().TryGetValue(scope, out var kept) ? kept : null;
    }

    private static Dictionary<string, HashSet<string>> Read()
    {
        try
        {
            if (!File.Exists(FilePath)) return new(StringComparer.Ordinal);
            var raw = JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(FilePath));
            return raw?.ToDictionary(p => p.Key, p => p.Value.Where(SteamProtocol.IsValidAppId).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal)
                ?? new(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new(StringComparer.Ordinal);
        }
    }
}
