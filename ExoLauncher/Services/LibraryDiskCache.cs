using System.Text.Json;
using System.Text.Json.Serialization;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>
/// Last-good library snapshot so the grid can paint before a cold store scan
/// finishes. Scoped to the account tags that produced it.
/// </summary>
internal static class LibraryDiskCache
{
    private const int CurrentVersion = 2;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static IReadOnlyList<GameEntry>? TryLoad(IReadOnlyDictionary<string, string?> scopes)
    {
        try
        {
            var path = PathHelper.LibraryCachePath;
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            var snapshot = JsonSerializer.Deserialize<Snapshot>(json, JsonOpts);
            if (snapshot?.Version != CurrentVersion || snapshot.Games is null || snapshot.Games.Count == 0)
                return null;
            if (!ScopesMatch(snapshot.Scopes, scopes)) return null;
            if (DateTimeOffset.UtcNow - snapshot.SavedAtUtc > TimeSpan.FromDays(14))
                return null;
            return snapshot.Games.Select(ToEntry).Where(game => game is not null).Cast<GameEntry>().ToList();
        }
        catch (Exception ex)
        {
            AppLog.Debug("Library disk cache load failed: " + ex.Message);
            return null;
        }
    }

    public static void Save(IReadOnlyList<GameEntry> games, IReadOnlyDictionary<string, string?> scopes)
    {
        try
        {
            var snapshot = new Snapshot
            {
                Version = CurrentVersion,
                SavedAtUtc = DateTimeOffset.UtcNow,
                Scopes = scopes.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
                Games = games.Select(FromEntry).ToList(),
            };
            var json = JsonSerializer.Serialize(snapshot, JsonOpts);
            var path = PathHelper.LibraryCachePath;
            var temp = path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLog.Debug("Library disk cache save failed: " + ex.Message);
        }
    }

    private static bool ScopesMatch(
        Dictionary<string, string?>? saved,
        IReadOnlyDictionary<string, string?> current)
    {
        if (saved is null) return current.Count == 0;
        foreach (var pair in current)
        {
            saved.TryGetValue(pair.Key, out var previous);
            if (!string.Equals(previous, pair.Value, StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private static CachedGame FromEntry(GameEntry game) => new()
    {
        Id = game.Id,
        Title = game.Title,
        Store = game.Store.ToString(),
        Installed = game.Installed,
        Owned = game.Owned,
        EntitlementState = game.EntitlementState,
        UpdateAvailable = game.UpdateAvailable,
        CanInstall = game.CanInstall,
        Path = game.Path,
        CoverUrl = game.CoverUrl,
        CoverSource = game.CoverSource,
        PlaytimeMinutes = game.PlaytimeMinutes,
        SizeBytes = game.SizeBytes,
        Status = game.Status,
        LaunchNote = game.LaunchNote,
        LaunchTarget = game.LaunchTarget,
        LastPlayedUtc = game.LastPlayedUtc,
        IsFavorite = game.IsFavorite,
        CanonicalTitleKey = game.CanonicalTitleKey,
        SelectedVariantId = game.SelectedVariantId,
        Variants = game.Variants.Select(variant => new CachedVariant
        {
            Id = variant.Id,
            Store = variant.Store.ToString(),
            Installed = variant.Installed,
            Owned = variant.Owned,
            EntitlementState = variant.EntitlementState,
            UpdateAvailable = variant.UpdateAvailable,
            CanInstall = variant.CanInstall,
            Path = variant.Path,
            LaunchTarget = variant.LaunchTarget,
            PlaytimeMinutes = variant.PlaytimeMinutes,
            LastPlayedUtc = variant.LastPlayedUtc,
            Status = variant.Status,
        }).ToList(),
    };

    private static GameEntry? ToEntry(CachedGame game)
    {
        if (string.IsNullOrWhiteSpace(game.Id) || string.IsNullOrWhiteSpace(game.Title))
            return null;
        if (!Enum.TryParse<StoreKind>(game.Store, ignoreCase: true, out var store))
            return null;
        return new GameEntry
        {
            Id = game.Id,
            Title = game.Title,
            Store = store,
            Installed = game.Installed,
            Owned = game.Owned,
            EntitlementState = game.EntitlementState,
            UpdateAvailable = game.UpdateAvailable,
            CanInstall = game.CanInstall,
            Path = game.Path,
            CoverUrl = game.CoverUrl,
            CoverSource = game.CoverSource,
            PlaytimeMinutes = game.PlaytimeMinutes,
            SizeBytes = game.SizeBytes,
            Status = string.IsNullOrWhiteSpace(game.Status) ? "Ready" : game.Status,
            LaunchNote = game.LaunchNote ?? string.Empty,
            LaunchTarget = game.LaunchTarget,
            LastPlayedUtc = game.LastPlayedUtc,
            IsFavorite = game.IsFavorite,
            CanonicalTitleKey = game.CanonicalTitleKey,
            SelectedVariantId = game.SelectedVariantId,
            Variants = (game.Variants ?? []).Select(ToVariant).Where(v => v is not null).Cast<GameVariant>().ToList(),
        };
    }

    private static GameVariant? ToVariant(CachedVariant variant)
    {
        if (string.IsNullOrWhiteSpace(variant.Id)) return null;
        if (!Enum.TryParse<StoreKind>(variant.Store, ignoreCase: true, out var store))
            return null;
        return new GameVariant
        {
            Id = variant.Id,
            Store = store,
            Installed = variant.Installed,
            Owned = variant.Owned,
            EntitlementState = variant.EntitlementState,
            UpdateAvailable = variant.UpdateAvailable,
            CanInstall = variant.CanInstall,
            Path = variant.Path,
            LaunchTarget = variant.LaunchTarget,
            PlaytimeMinutes = variant.PlaytimeMinutes,
            LastPlayedUtc = variant.LastPlayedUtc,
            Status = string.IsNullOrWhiteSpace(variant.Status) ? "Ready" : variant.Status,
        };
    }

    private sealed class Snapshot
    {
        public int Version { get; set; }
        public DateTimeOffset SavedAtUtc { get; set; }
        public Dictionary<string, string?> Scopes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<CachedGame> Games { get; set; } = [];
    }

    private sealed class CachedGame
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string Store { get; set; } = "";
        public bool Installed { get; set; }
        public bool Owned { get; set; }
        public EntitlementState EntitlementState { get; set; } = EntitlementState.Unknown;
        public bool UpdateAvailable { get; set; }
        public bool CanInstall { get; set; }
        public string? Path { get; set; }
        public string? CoverUrl { get; set; }
        public string? CoverSource { get; set; }
        public int? PlaytimeMinutes { get; set; }
        public long? SizeBytes { get; set; }
        public string? Status { get; set; }
        public string? LaunchNote { get; set; }
        public string? LaunchTarget { get; set; }
        public DateTimeOffset? LastPlayedUtc { get; set; }
        public bool IsFavorite { get; set; }
        public string? CanonicalTitleKey { get; set; }
        public string? SelectedVariantId { get; set; }
        public List<CachedVariant>? Variants { get; set; }
    }

    private sealed class CachedVariant
    {
        public string Id { get; set; } = "";
        public string Store { get; set; } = "";
        public bool Installed { get; set; }
        public bool Owned { get; set; }
        public EntitlementState EntitlementState { get; set; } = EntitlementState.Unknown;
        public bool UpdateAvailable { get; set; }
        public bool CanInstall { get; set; }
        public string? Path { get; set; }
        public string? LaunchTarget { get; set; }
        public int? PlaytimeMinutes { get; set; }
        public DateTimeOffset? LastPlayedUtc { get; set; }
        public string? Status { get; set; }
    }
}
