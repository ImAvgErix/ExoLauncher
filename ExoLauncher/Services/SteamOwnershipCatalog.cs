using System.Text.Json;
using ExoLauncher.Adapters.Cli;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>
/// Durable proof that a Steam title was previously discovered from an installed
/// appmanifest. Search results never write this catalog. Keeping that proof lets
/// Exo offer Install after Steam removes the manifest during an Exo uninstall.
/// </summary>
internal sealed class SteamOwnershipCatalog
{
    private const int CurrentVersion = 1;
    private static readonly object FileGate = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly object _gate = new();
    private readonly string _path;
    private readonly string _backupPath;
    private readonly Dictionary<string, CatalogEntry> _entries =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _primaryHealthy;
    private bool _dirty;

    public SteamOwnershipCatalog()
        : this(Path.Combine(PathHelper.AppDataDir, "steam-owned.json"))
    {
    }

    internal SteamOwnershipCatalog(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _backupPath = _path + ".bak";
        Load();
    }

    /// <summary>Record only manifest-backed, currently installed Steam entries.</summary>
    internal void RememberInstalled(IEnumerable<GameEntry> games)
    {
        ArgumentNullException.ThrowIfNull(games);
        lock (_gate)
        {
            var changed = false;
            foreach (var game in games)
            {
                if (!TryCreateEntry(game, out var entry)) continue;
                if (_entries.TryGetValue(entry.AppId, out var existing) && existing == entry)
                    continue;
                _entries[entry.AppId] = entry;
                changed = true;
            }

            if (changed) _dirty = true;
            if (_dirty)
            {
                try
                {
                    Save();
                    _dirty = false;
                }
                catch (Exception ex) { AppLog.Warn("Steam ownership catalog save failed: " + ex.Message); }
            }
        }
    }

    /// <summary>Return proven-owned titles absent from the current manifest scan.</summary>
    internal IReadOnlyList<GameEntry> RestoreMissing(IEnumerable<GameEntry> currentGames)
    {
        ArgumentNullException.ThrowIfNull(currentGames);
        lock (_gate)
        {
            var present = currentGames
                .Select(game => game.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return _entries.Values
                .Where(entry => !present.Contains("steam:" + entry.AppId))
                .OrderBy(entry => entry.Title, StringComparer.OrdinalIgnoreCase)
                .Select(ToUninstalledGame)
                .ToList();
        }
    }

    private void Load()
    {
        lock (FileGate)
        {
            if (TryRead(_path, out var primary))
            {
                ReplaceEntries(primary);
                _primaryHealthy = true;
                return;
            }

            if (File.Exists(_path))
            {
                try { File.Copy(_path, _path + ".corrupt", overwrite: true); }
                catch (Exception ex) { AppLog.Debug("Steam ownership quarantine: " + ex.Message); }
            }

            if (TryRead(_backupPath, out var backup))
            {
                ReplaceEntries(backup);
                AppLog.Warn("Steam ownership catalog recovered from backup.");
                _dirty = true;
                try
                {
                    Save();
                    _dirty = false;
                }
                catch (Exception ex)
                {
                    AppLog.Warn("Steam ownership catalog primary repair failed: " + ex.Message);
                }
            }
        }
    }

    private bool TryRead(string path, out IReadOnlyList<CatalogEntry> entries)
    {
        entries = Array.Empty<CatalogEntry>();
        if (!File.Exists(path)) return false;
        try
        {
            var document = JsonSerializer.Deserialize<CatalogDocument>(
                File.ReadAllText(path),
                JsonOptions);
            if (document?.Version != CurrentVersion || document.Games is null)
                return false;
            entries = document.Games
                .Where(IsValidEntry)
                .GroupBy(entry => entry.AppId, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Last())
                .ToList();
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Debug("Steam ownership catalog read: " + ex.Message);
            return false;
        }
    }

    private void ReplaceEntries(IEnumerable<CatalogEntry> entries)
    {
        _entries.Clear();
        foreach (var entry in entries)
            _entries[entry.AppId] = entry;
    }

    private void Save()
    {
        lock (FileGate)
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var document = new CatalogDocument
            {
                Version = CurrentVersion,
                Games = _entries.Values
                    .OrderBy(entry => entry.Title, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(entry => entry.AppId, StringComparer.Ordinal)
                    .ToList(),
            };
            var temp = _path + ".tmp";
            try
            {
                File.WriteAllText(temp, JsonSerializer.Serialize(document, JsonOptions));
                if (_primaryHealthy && TryRead(_path, out _))
                    File.Copy(_path, _backupPath, overwrite: true);
                File.Move(temp, _path, overwrite: true);
                _primaryHealthy = true;
                if (!File.Exists(_backupPath))
                    File.Copy(_path, _backupPath, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
            }
        }
    }

    private static bool TryCreateEntry(GameEntry game, out CatalogEntry entry)
    {
        entry = default!;
        if (game.Store != StoreKind.Steam || !game.Installed || !game.Owned)
            return false;
        var appId = (game.LaunchTarget ?? "").Trim();
        if (!IsValidAppId(appId) ||
            !string.Equals(game.Id, "steam:" + appId, StringComparison.OrdinalIgnoreCase))
            return false;
        var title = (game.Title ?? "").Trim();
        if (title.Length is 0 or > 512) return false;
        entry = new CatalogEntry(appId, title, game.SizeBytes);
        return true;
    }

    private static bool IsValidEntry(CatalogEntry entry) =>
        IsValidAppId(entry.AppId) &&
        !string.IsNullOrWhiteSpace(entry.Title) &&
        entry.Title.Length <= 512;

    private static bool IsValidAppId(string appId) => SteamProtocol.IsValidAppId(appId);

    private static GameEntry ToUninstalledGame(CatalogEntry entry) => new()
    {
        Id = "steam:" + entry.AppId,
        Title = entry.Title,
        Store = StoreKind.Steam,
        Installed = false,
        Owned = true,
        CanInstall = true,
        UpdateAvailable = false,
        Path = null,
        CoverUrl = null,
        CoverSource = "steam",
        SizeBytes = entry.SizeBytes,
        Status = "Not installed",
        Deps = new[] { "Steam client" },
        LaunchNote = "Installs through Steam quietly — Steam stays a backend, not a window you use.",
        LaunchTarget = entry.AppId,
    };

    private sealed class CatalogDocument
    {
        public int Version { get; init; }
        public List<CatalogEntry>? Games { get; init; }
    }

    private sealed record CatalogEntry(string AppId, string Title, long? SizeBytes);
}
