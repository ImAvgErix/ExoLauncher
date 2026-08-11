using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>
/// Durable registrations for installs Exo must be able to rediscover without a
/// vendor manifest. The managed bit is deletion authority: entries registered
/// in place are forgotten on uninstall, while only catalog-proven managed
/// copies may be recursively deleted.
/// </summary>
internal sealed class InstalledGameCatalog
{
    private const int CurrentVersion = 1;
    private static readonly object FileGate = new();
    private static readonly Lazy<InstalledGameCatalog> Shared = new(
        () => new InstalledGameCatalog(Path.Combine(PathHelper.AppDataDir, "installed-games.json")));
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly object _gate = new();
    private readonly string _path;
    private readonly string _backupPath;
    private readonly Dictionary<string, CatalogEntry> _entries =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _primaryHealthy;

    internal static InstalledGameCatalog Default => Shared.Value;

    internal InstalledGameCatalog(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _backupPath = _path + ".bak";
        Load();
    }

    internal static bool TryCreateGogInstallLocation(
        string requestedBase,
        string gameId,
        out ManagedInstallLocation location,
        out string error)
    {
        location = default!;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(requestedBase) || string.IsNullOrWhiteSpace(gameId))
        {
            error = "A GOG install root and product id are required.";
            return false;
        }

        try
        {
            var baseRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(requestedBase));
            var managedRoot = Path.Combine(baseRoot, "GOG");
            var installPath = Path.Combine(managedRoot, SafePathSegment(gameId));
            location = new ManagedInstallLocation(managedRoot, installPath);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            error = "The GOG install path is invalid.";
            return false;
        }
    }

    internal void Register(
        GameEntry game,
        string? launchTarget,
        bool managed,
        string? managedRoot)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (!TryCreateEntry(game, launchTarget, managed, managedRoot, out var entry, out var error))
            throw new InvalidOperationException(error);

        lock (_gate)
        {
            var key = Key(entry.Store, entry.Id);
            _entries.TryGetValue(key, out var previous);
            _entries[key] = entry;
            try
            {
                Save();
            }
            catch
            {
                if (previous is null) _entries.Remove(key);
                else _entries[key] = previous;
                throw;
            }
        }
    }

    internal IReadOnlyList<GameEntry> GetInstalledGames(StoreKind store)
    {
        lock (_gate)
        {
            return _entries.Values
                .Where(entry => entry.Store == store && IsPresent(entry))
                .OrderBy(entry => entry.Title, StringComparer.OrdinalIgnoreCase)
                .Select(ToGameEntry)
                .ToList();
        }
    }

    /// <summary>
    /// Returns true only when the durable catalog already grants Exo deletion
    /// authority over this exact managed install. Callers must not adopt an
    /// arbitrary pre-existing directory merely because it sits below a
    /// preferred games root.
    /// </summary>
    internal bool IsRegisteredManagedInstall(
        StoreKind store,
        string gameId,
        string installPath,
        string managedRoot)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(Key(store, gameId), out var entry) &&
                   entry.Managed &&
                   !string.IsNullOrWhiteSpace(entry.ManagedRoot) &&
                   SamePath(entry.InstallPath, installPath) &&
                   SamePath(entry.ManagedRoot, managedRoot);
        }
    }

    internal InstallResult UninstallRegistered(GameEntry game)
    {
        ArgumentNullException.ThrowIfNull(game);
        lock (_gate)
        {
            var key = Key(game.Store, game.Id);
            if (!_entries.TryGetValue(key, out var entry) ||
                (!string.IsNullOrWhiteSpace(game.Path) && !SamePath(game.Path, entry.InstallPath)))
            {
                return new InstallResult
                {
                    Ok = false,
                    Message = "This install is not registered as Exo-managed. No files were deleted.",
                };
            }

            if (!entry.Managed)
            {
                return RemoveRegistration(
                    key,
                    entry,
                    "Removed the portable registration. The original files were left in place.");
            }

            if (string.IsNullOrWhiteSpace(entry.ManagedRoot))
            {
                return new InstallResult
                {
                    Ok = false,
                    Message = "Refusing to delete an install without its managed-root proof.",
                };
            }

            if (!Directory.Exists(entry.InstallPath))
                return RemoveRegistration(key, entry, "Removed the missing managed install registration.");

            if (!RecursiveDeleteGuard.TryValidateManagedChild(
                    entry.ManagedRoot,
                    entry.InstallPath,
                    out var validatedPath,
                    out var validationError))
            {
                return new InstallResult { Ok = false, Message = validationError };
            }

            try
            {
                Directory.Delete(validatedPath, recursive: true);
            }
            catch (Exception ex)
            {
                return new InstallResult { Ok = false, Message = ex.Message };
            }

            return RemoveRegistration(key, entry, "Removed the Exo-managed install.");
        }
    }

    private InstallResult RemoveRegistration(string key, CatalogEntry entry, string message)
    {
        _entries.Remove(key);
        try
        {
            Save();
            return new InstallResult { Ok = true, Message = message };
        }
        catch (Exception ex)
        {
            // If the files still exist, keep the in-memory proof so a retry is
            // safe. A deleted managed directory is already absent and will be
            // ignored by discovery even if an old on-disk record survives.
            if (Directory.Exists(entry.InstallPath)) _entries[key] = entry;
            return new InstallResult
            {
                Ok = false,
                Message = "The install changed, but its registration could not be saved: " + ex.Message,
            };
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
                catch (Exception ex) { AppLog.Debug("Installed-game catalog quarantine: " + ex.Message); }
            }

            if (!TryRead(_backupPath, out var backup)) return;
            ReplaceEntries(backup);
            AppLog.Warn("Installed-game catalog recovered from backup.");
            try { Save(); }
            catch (Exception ex) { AppLog.Warn("Installed-game catalog repair failed: " + ex.Message); }
        }
    }

    private bool TryRead(string path, out IReadOnlyList<CatalogEntry> entries)
    {
        entries = Array.Empty<CatalogEntry>();
        if (!File.Exists(path)) return false;
        try
        {
            var document = JsonSerializer.Deserialize<CatalogDocument>(File.ReadAllText(path), JsonOptions);
            if (document?.Version != CurrentVersion || document.Games is null) return false;
            entries = document.Games
                .Where(IsValidLoadedEntry)
                .GroupBy(entry => Key(entry.Store, entry.Id), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Last())
                .ToList();
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Debug("Installed-game catalog read failed: " + ex.Message);
            return false;
        }
    }

    private void ReplaceEntries(IEnumerable<CatalogEntry> entries)
    {
        _entries.Clear();
        foreach (var entry in entries)
            _entries[Key(entry.Store, entry.Id)] = entry;
    }

    private void Save()
    {
        lock (FileGate)
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            var document = new CatalogDocument
            {
                Version = CurrentVersion,
                Games = _entries.Values
                    .OrderBy(entry => entry.Store)
                    .ThenBy(entry => entry.Title, StringComparer.OrdinalIgnoreCase)
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
                if (!File.Exists(_backupPath)) File.Copy(_path, _backupPath, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch { /* best effort */ }
            }
        }
    }

    private static bool TryCreateEntry(
        GameEntry game,
        string? launchTarget,
        bool managed,
        string? managedRoot,
        out CatalogEntry entry,
        out string error)
    {
        entry = default!;
        error = string.Empty;
        if (game.Store is not (StoreKind.Gog or StoreKind.Local) ||
            string.IsNullOrWhiteSpace(game.Id) ||
            !HasExpectedIdPrefix(game.Store, game.Id) ||
            string.IsNullOrWhiteSpace(game.Title) ||
            game.Title.Length > 512 ||
            string.IsNullOrWhiteSpace(game.Path))
        {
            error = "The install registration is incomplete.";
            return false;
        }

        try
        {
            var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(game.Path));
            var target = string.IsNullOrWhiteSpace(launchTarget)
                ? null
                : Path.GetFullPath(launchTarget);
            var root = string.IsNullOrWhiteSpace(managedRoot)
                ? null
                : Path.TrimEndingDirectorySeparator(Path.GetFullPath(managedRoot));

            if (managed && (root is null || !IsStrictChild(root, path)))
            {
                error = "The managed install is not a child of its managed root.";
                return false;
            }

            if (managed && Directory.Exists(path) &&
                !RecursiveDeleteGuard.TryValidateManagedChild(
                    root!,
                    path,
                    out _,
                    out var validationError))
            {
                error = validationError;
                return false;
            }

            if (game.Store == StoreKind.Local &&
                (target is null || !IsWithin(path, target)))
            {
                error = "The portable executable must be inside its registered folder.";
                return false;
            }

            entry = new CatalogEntry(
                game.Id.Trim(),
                game.Title.Trim(),
                game.Store,
                path,
                target,
                managed,
                managed ? root : null,
                DateTimeOffset.UtcNow);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            error = "The install registration contains an invalid path.";
            return false;
        }
    }

    private static bool IsValidLoadedEntry(CatalogEntry entry)
    {
        if (entry.Store is not (StoreKind.Gog or StoreKind.Local) ||
            string.IsNullOrWhiteSpace(entry.Id) ||
            !HasExpectedIdPrefix(entry.Store, entry.Id) ||
            string.IsNullOrWhiteSpace(entry.Title) ||
            entry.Title.Length > 512 ||
            string.IsNullOrWhiteSpace(entry.InstallPath) ||
            !Path.IsPathFullyQualified(entry.InstallPath))
            return false;
        if (entry.Managed &&
            (string.IsNullOrWhiteSpace(entry.ManagedRoot) ||
             !Path.IsPathFullyQualified(entry.ManagedRoot) ||
             !IsStrictChild(entry.ManagedRoot, entry.InstallPath)))
            return false;
        return entry.Store != StoreKind.Local ||
               (!string.IsNullOrWhiteSpace(entry.LaunchTarget) &&
                Path.IsPathFullyQualified(entry.LaunchTarget) &&
                IsWithin(entry.InstallPath, entry.LaunchTarget));
    }

    private static bool IsPresent(CatalogEntry entry) =>
        Directory.Exists(entry.InstallPath) &&
        (entry.Store != StoreKind.Local || File.Exists(entry.LaunchTarget));

    private static GameEntry ToGameEntry(CatalogEntry entry) => new()
    {
        Id = entry.Id,
        Title = entry.Title,
        Store = entry.Store,
        Installed = true,
        Owned = true,
        CanInstall = false,
        Path = entry.InstallPath,
        LaunchTarget = entry.LaunchTarget ?? entry.InstallPath,
        Status = "Ready",
        Deps = entry.Store == StoreKind.Gog ? new[] { "gogdl" } : Array.Empty<string>(),
        LaunchNote = entry.Store == StoreKind.Gog
            ? "Launches the Exo-managed GOG build."
            : "Launches the registered executable directly. No store client.",
    };

    private static string SafePathSegment(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length <= 80 &&
            trimmed is not ("." or "..") &&
            trimmed.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_'))
            return trimmed;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(trimmed)));
        return "game-" + hash[..16].ToLowerInvariant();
    }

    private static string Key(StoreKind store, string id) => $"{store}:{id.Trim()}";

    private static bool HasExpectedIdPrefix(StoreKind store, string id) => store switch
    {
        StoreKind.Gog => id.StartsWith("gog:", StringComparison.OrdinalIgnoreCase),
        StoreKind.Local => id.StartsWith("local:", StringComparison.OrdinalIgnoreCase) &&
                           !id.Equals("local:add", StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    private static bool SamePath(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);

    private static bool IsStrictChild(string root, string candidate)
    {
        var relative = Path.GetRelativePath(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate)));
        return relative != "." && !Path.IsPathFullyQualified(relative) && !IsParentTraversal(relative);
    }

    private static bool IsWithin(string root, string candidate)
    {
        var relative = Path.GetRelativePath(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)),
            Path.GetFullPath(candidate));
        return relative == "." || (!Path.IsPathFullyQualified(relative) && !IsParentTraversal(relative));
    }

    private static bool IsParentTraversal(string relative) =>
        relative == ".." ||
        relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
        relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);

    private sealed class CatalogDocument
    {
        public int Version { get; init; }
        public List<CatalogEntry>? Games { get; init; }
    }

    private sealed record CatalogEntry(
        string Id,
        string Title,
        StoreKind Store,
        string InstallPath,
        string? LaunchTarget,
        bool Managed,
        string? ManagedRoot,
        DateTimeOffset RegisteredAtUtc);
}

internal sealed record ManagedInstallLocation(string ManagedRoot, string InstallPath);
