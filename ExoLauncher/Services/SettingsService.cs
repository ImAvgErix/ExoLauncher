using System.Text.Json;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly object _stateGate = new();
    private readonly object _persistenceGate = new();
    private AppSettings _current = new();
    private bool _loadFailed;
    private long _revision;
    private long _persistedRevision = -1;

    public SettingsService()
    {
    }

    internal SettingsService(AppSettings initialSettings)
    {
        ArgumentNullException.ThrowIfNull(initialSettings);
        _current = Clone(initialSettings);
    }

    /// <summary>
    /// Returns a detached snapshot. Callers can safely enumerate its collections
    /// while settings are being changed on another thread, and cannot mutate the
    /// service by holding on to a list or dictionary reference.
    /// </summary>
    public AppSettings Current
    {
        get
        {
            lock (_stateGate) return Clone(_current);
        }
    }

    /// <summary>True when settings.json existed but could not be read. Writing
    /// defaults over it would destroy the user's pins, favorites, and setup, so
    /// saving is refused until something deliberately replaces the state.</summary>
    public bool LoadFailed
    {
        get
        {
            lock (_stateGate) return _loadFailed;
        }
    }

    public void Load()
    {
        // Load participates in the same disk gate as saves. Startup is the only
        // normal caller, but serializing it also prevents a diagnostic reload
        // from racing an in-flight atomic replacement.
        lock (_persistenceGate)
        {
            AppSettings loaded = new();
            var failed = false;
            var existed = false;
            try
            {
                if (File.Exists(PathHelper.SettingsPath))
                {
                    existed = true;
                    var json = File.ReadAllText(PathHelper.SettingsPath);
                    using var settingsDocument = JsonDocument.Parse(json);
                    var settingsRoot = settingsDocument.RootElement;
                    loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts)
                        ?? throw new JsonException("settings.json deserialized to null");
                    NormalizeLoaded(loaded, settingsRoot);
                }
            }
            catch (Exception ex)
            {
                failed = true;
                AppLog.Warn("Settings load failed: " + ex.Message);
                QuarantineUnreadable(ex.Message);
            }

            ApplyOnboardingMarker(loaded);
            lock (_stateGate)
            {
                _current = loaded;
                _loadFailed = failed;
                _revision++;
                _persistedRevision = existed && !failed ? _revision : -1;
            }
        }
    }

    private static void NormalizeLoaded(AppSettings loaded, JsonElement settingsRoot)
    {
        // Product defaults — not user-toggleable (UI removed).
        EnforceProductDefaults(loaded);
        loaded.AllowResize = false;
        loaded.CheckForUpdates = true;
        loaded.CopyPortableIntoLibrary = false;
        loaded.Favorites ??= new List<string>();
        loaded.Recent ??= new List<string>();
        loaded.LastPlayed = loaded.LastPlayed is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(loaded.LastPlayed, StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(loaded.SortMode)) loaded.SortMode = "name";
        // Older builds allowed arbitrary normalized coordinates. They now map
        // to one of the nine screen anchors, so the preview and native surface
        // always describe the same placement.
        loaded.TrophyNotificationPreset = NormalizeTrophyPreset(loaded.TrophyNotificationPreset);
        loaded.TrophyNotificationPosition = NormalizeTrophyPosition(loaded.TrophyNotificationPosition);
        var legacyPosition = ResolveTrophyAnchor(loaded.TrophyNotificationPosition);
        if (!HasFiniteNumber(settingsRoot, "trophyNotificationPositionX"))
            loaded.TrophyNotificationPositionX = legacyPosition.X;
        if (!HasFiniteNumber(settingsRoot, "trophyNotificationPositionY"))
            loaded.TrophyNotificationPositionY = legacyPosition.Y;
        CanonicalizeTrophyPlacement(loaded, legacyPosition);
        // Sound selection was intentionally removed. Existing files keep their
        // shape for backwards compatibility, but every enabled notification
        // now gets the same authored Exo cue.
        loaded.TrophyNotificationSoundCue = "exo";
        loaded.TrophyNotificationSound = true;
    }

    /// <summary>Keep an unreadable settings file for recovery instead of letting
    /// the next save silently overwrite it with defaults.</summary>
    private static void QuarantineUnreadable(string reason)
    {
        try
        {
            var path = PathHelper.SettingsPath;
            if (!File.Exists(path)) return;
            var backup = path + ".corrupt";
            File.Copy(path, backup, overwrite: true);
            AppLog.Warn($"Settings quarantined to {backup} ({reason})");
        }
        catch (Exception ex)
        {
            AppLog.Warn("Settings quarantine failed: " + ex.Message);
        }
    }

    private static void ApplyOnboardingMarker(AppSettings settings)
    {
        try
        {
            if (File.Exists(PathHelper.OnboardedMarkerPath))
                settings.OnboardingComplete = true;
        }
        catch { /* marker is advisory */ }
    }

    /// <summary>Record first-run completion outside settings.json so it holds even
    /// if that file is later lost or replaced.</summary>
    private static void WriteOnboardingMarker()
    {
        try
        {
            if (File.Exists(PathHelper.OnboardedMarkerPath)) return;
            File.WriteAllText(PathHelper.OnboardedMarkerPath,
                DateTimeOffset.UtcNow.ToString("O"));
        }
        catch (Exception ex)
        {
            AppLog.Warn("Onboarding marker write failed: " + ex.Message);
        }
    }

    /// <summary>Persist settings. Returns false and sets error when disk write fails.</summary>
    public bool TrySave(out string? error)
    {
        lock (_stateGate)
        {
            if (_loadFailed)
            {
                error = "Settings were not saved because settings.json could not be read. " +
                        "The original file is preserved as settings.json.corrupt.";
                AppLog.Warn(error);
                return false;
            }

            EnforceProductDefaults(_current);
            // Never persist a stale appVersion forever — callers set it from AppServices.AppVersion.
            if (string.IsNullOrWhiteSpace(_current.AppVersion))
                _current.AppVersion = "0.0.0";
            _revision++;
        }

        return TryPersistLatest(out error);
    }

    /// <summary>
    /// Writes the newest revision available when the disk gate is acquired. If
    /// another thread changes state during I/O, this writer loops and commits
    /// that newer revision before returning. This prevents an older snapshot
    /// from being the last file on disk even if task scheduling is adversarial.
    /// </summary>
    private bool TryPersistLatest(out string? error)
    {
        error = null;
        lock (_persistenceGate)
        {
            while (true)
            {
                AppSettings snapshot;
                long snapshotRevision;
                lock (_stateGate)
                {
                    if (_loadFailed)
                    {
                        error = "Settings were not saved because settings.json could not be read. " +
                                "The original file is preserved as settings.json.corrupt.";
                        AppLog.Warn(error);
                        return false;
                    }
                    if (_persistedRevision == _revision)
                        return true;
                    snapshot = Clone(_current);
                    snapshotRevision = _revision;
                }

                try
                {
                    WriteSnapshotAtomically(snapshot);
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    AppLog.Warn("Settings save failed: " + ex.Message);
                    return false;
                }

                bool stable;
                lock (_stateGate)
                {
                    _persistedRevision = Math.Max(_persistedRevision, snapshotRevision);
                    stable = snapshotRevision == _revision;
                }
                if (!stable) continue;
                if (snapshot.OnboardingComplete) WriteOnboardingMarker();
                return true;
            }
        }
    }

    private static void WriteSnapshotAtomically(AppSettings snapshot)
    {
        var path = PathHelper.SettingsPath;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(snapshot, JsonOpts);
        // A unique sibling temp protects against another SettingsService (for
        // example a recovery tool) sharing this path. The final move remains a
        // single same-volume filesystem replacement.
        var tmp = path + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
            if (!File.Exists(path))
                throw new IOException("Settings file missing after write.");
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); }
            catch { /* an orphaned temp is safer than deleting settings.json */ }
        }
    }

    public void Save() => TrySave(out _);

    /// <summary>Force settings.appVersion to the running build (call after Load).</summary>
    public void SyncAppVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version)) return;
        lock (_stateGate)
        {
            if (string.Equals(_current.AppVersion, version, StringComparison.Ordinal)) return;
            // A version bump must never be the thing that overwrites unreadable
            // settings with defaults — that is how first-run setup came back.
            if (_loadFailed)
            {
                AppLog.Warn("Skipping appVersion write: settings.json was unreadable.");
                return;
            }
            _current.AppVersion = version;
            _revision++;
        }
        TryPersistLatest(out _);
    }

    public void Flush() => Save();

    public void ApplyPatch(
        bool? closeStore = null,
        bool? autoRedist = null,
        bool? minimizeWhilePlaying = null,
        bool? copyPortable = null,
        bool? allowResize = null,
        bool? checkUpdates = null,
        string? sortMode = null,
        string? defaultInstallRoot = null,
        bool? onboardingComplete = null,
        bool? trophyNotificationsEnabled = null,
        string? trophyNotificationPreset = null,
        string? trophyNotificationPosition = null,
        double? trophyNotificationPositionX = null,
        double? trophyNotificationPositionY = null,
        int? trophyNotificationDurationSeconds = null,
        bool? trophyNotificationSound = null,
        string? trophyNotificationSoundCue = null)
    {
        lock (_stateGate)
        {
            // Hard-locked product defaults (ignore UI patches that try to flip them).
            _ = closeStore;
            _ = autoRedist;
            _ = copyPortable;
            _ = allowResize;
            _ = checkUpdates;
            if (minimizeWhilePlaying is not null) _current.MinimizeWhilePlaying = minimizeWhilePlaying.Value;
            if (!string.IsNullOrWhiteSpace(sortMode)) _current.SortMode = sortMode!;
            if (defaultInstallRoot is not null)
                _current.DefaultInstallRoot = string.IsNullOrWhiteSpace(defaultInstallRoot) ? null : defaultInstallRoot;
            if (onboardingComplete is not null) _current.OnboardingComplete = onboardingComplete.Value;
            if (trophyNotificationsEnabled is not null)
                _current.TrophyNotificationsEnabled = trophyNotificationsEnabled.Value;
            if (trophyNotificationPreset is not null)
                _current.TrophyNotificationPreset = NormalizeTrophyPreset(trophyNotificationPreset);
            if (trophyNotificationPosition is not null)
            {
                _current.TrophyNotificationPosition = NormalizeTrophyPosition(trophyNotificationPosition);
                var anchor = ResolveTrophyAnchor(_current.TrophyNotificationPosition);
                _current.TrophyNotificationPositionX = anchor.X;
                _current.TrophyNotificationPositionY = anchor.Y;
            }
            if (trophyNotificationPositionX is not null)
                _current.TrophyNotificationPositionX = NormalizeTrophyCoordinate(
                    trophyNotificationPositionX.Value,
                    _current.TrophyNotificationPositionX);
            if (trophyNotificationPositionY is not null)
                _current.TrophyNotificationPositionY = NormalizeTrophyCoordinate(
                    trophyNotificationPositionY.Value,
                    _current.TrophyNotificationPositionY);
            if (trophyNotificationPositionX is not null || trophyNotificationPositionY is not null)
                CanonicalizeTrophyPlacement(_current, ResolveTrophyAnchor(_current.TrophyNotificationPosition));
            if (trophyNotificationDurationSeconds is not null)
                _current.TrophyNotificationDurationSeconds = Math.Clamp(trophyNotificationDurationSeconds.Value, 3, 12);
            if (trophyNotificationSound is not null)
            {
                _current.TrophyNotificationSound = trophyNotificationSound.Value;
                _current.TrophyNotificationSoundCue = trophyNotificationSound.Value
                    ? _current.TrophyNotificationSoundCue == "off" ? "exo" : NormalizeTrophySoundCue(_current.TrophyNotificationSoundCue, true)
                    : "off";
            }
            if (trophyNotificationSoundCue is not null)
            {
                _current.TrophyNotificationSoundCue = NormalizeTrophySoundCue(trophyNotificationSoundCue, true);
                _current.TrophyNotificationSound = _current.TrophyNotificationSoundCue != "off";
            }
            _current.TrophyNotificationSoundCue = "exo";
            _current.TrophyNotificationSound = true;
            EnforceProductDefaults(_current);
            _revision++;
        }
        if (!TryPersistLatest(out var err))
            throw new InvalidOperationException(err ?? "Could not save settings.");
    }

    public void ToggleFavorite(string gameId)
    {
        if (string.IsNullOrWhiteSpace(gameId)) return;
        lock (_stateGate)
        {
            var list = _current.Favorites;
            var idx = list.FindIndex(x => string.Equals(x, gameId, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) list.RemoveAt(idx);
            else list.Insert(0, gameId);
            if (list.Count > 200) list.RemoveRange(200, list.Count - 200);
            _revision++;
        }
        if (!TryPersistLatest(out var err))
            throw new InvalidOperationException(err ?? "Could not save favorites.");
    }

    public void SetFavoriteState(IEnumerable<string> gameIds, bool isFavorite)
    {
        ArgumentNullException.ThrowIfNull(gameIds);
        var ids = gameIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (ids.Length == 0) return;

        lock (_stateGate)
        {
            foreach (var id in ids)
                _current.Favorites.RemoveAll(existing =>
                    string.Equals(existing, id, StringComparison.OrdinalIgnoreCase));
            if (isFavorite)
                _current.Favorites.InsertRange(0, ids);
            if (_current.Favorites.Count > 200)
                _current.Favorites.RemoveRange(200, _current.Favorites.Count - 200);
            _revision++;
        }
        if (!TryPersistLatest(out var err))
            throw new InvalidOperationException(err ?? "Could not save favorites.");
    }

    public void RecordLaunch(string gameId)
    {
        if (string.IsNullOrWhiteSpace(gameId)) return;
        lock (_stateGate)
        {
            _current.LastPlayed[gameId] = DateTimeOffset.UtcNow.ToString("O");
            _current.Recent.RemoveAll(x => string.Equals(x, gameId, StringComparison.OrdinalIgnoreCase));
            _current.Recent.Insert(0, gameId);
            if (_current.Recent.Count > 40)
                _current.Recent.RemoveRange(40, _current.Recent.Count - 40);
            _revision++;
        }
        TryPersistLatest(out _);
    }

    public bool IsFavorite(string gameId)
    {
        lock (_stateGate)
            return _current.Favorites.Any(x => string.Equals(x, gameId, StringComparison.OrdinalIgnoreCase));
    }

    public DateTimeOffset? GetLastPlayed(string gameId)
    {
        lock (_stateGate)
        {
            if (_current.LastPlayed.TryGetValue(gameId, out var raw) &&
                DateTimeOffset.TryParse(raw, out var dt))
                return dt;
            return null;
        }
    }

    private static void EnforceProductDefaults(AppSettings settings)
    {
        settings.AntiCheatSafeMode = true;
        settings.MinimizeWhilePlaying = true;
        settings.AutoInstallRedistributables = true;
        settings.CloseStoreClientsAfterLaunch = true;
        settings.CopyPortableIntoLibrary = false;
        settings.AllowResize = false;
        settings.CheckForUpdates = true;
    }

    private static AppSettings Clone(AppSettings source) => new()
    {
        CloseStoreClientsAfterLaunch = source.CloseStoreClientsAfterLaunch,
        AutoInstallRedistributables = source.AutoInstallRedistributables,
        MinimizeWhilePlaying = source.MinimizeWhilePlaying,
        AntiCheatSafeMode = source.AntiCheatSafeMode,
        AppVersion = source.AppVersion,
        DefaultInstallRoot = source.DefaultInstallRoot,
        CopyPortableIntoLibrary = source.CopyPortableIntoLibrary,
        AllowResize = source.AllowResize,
        CheckForUpdates = source.CheckForUpdates,
        SortMode = source.SortMode,
        Favorites = source.Favorites.ToList(),
        Recent = source.Recent.ToList(),
        LastPlayed = new Dictionary<string, string>(
            source.LastPlayed,
            StringComparer.OrdinalIgnoreCase),
        OnboardingComplete = source.OnboardingComplete,
        TrophyNotificationsEnabled = source.TrophyNotificationsEnabled,
        TrophyNotificationPreset = source.TrophyNotificationPreset,
        TrophyNotificationPosition = source.TrophyNotificationPosition,
        TrophyNotificationPositionX = source.TrophyNotificationPositionX,
        TrophyNotificationPositionY = source.TrophyNotificationPositionY,
        TrophyNotificationDurationSeconds = source.TrophyNotificationDurationSeconds,
        TrophyNotificationSound = source.TrophyNotificationSound,
        TrophyNotificationSoundCue = source.TrophyNotificationSoundCue,
    };

    private static string NormalizeTrophyPreset(string? value)
    {
        _ = value; // Legacy presets all migrate to the one canonical Exo surface.
        return "exo";
    }

    private static string NormalizeTrophyPosition(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "top-left" => "top-left",
        "top-center" => "top-center",
        "top-right" => "top-right",
        "center-left" or "middle-left" => "center-left",
        "center" or "middle-center" => "center",
        "center-right" or "middle-right" => "center-right",
        "bottom-left" => "bottom-left",
        "bottom-center" => "bottom-center",
        _ => "bottom-right",
    };

    private static (double X, double Y) ResolveTrophyAnchor(string position) => position switch
    {
        "top-left" => (0d, 0d),
        "top-center" => (0.5d, 0d),
        "top-right" => (1d, 0d),
        "center-left" => (0d, 0.5d),
        "center" => (0.5d, 0.5d),
        "center-right" => (1d, 0.5d),
        "bottom-left" => (0d, 1d),
        "bottom-center" => (0.5d, 1d),
        _ => (1d, 1d),
    };

    private static double NormalizeTrophyCoordinate(double value, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, 0d, 1d) : Math.Clamp(fallback, 0d, 1d);

    private static void CanonicalizeTrophyPlacement(AppSettings settings, (double X, double Y) fallback)
    {
        var x = QuantizeTrophyCoordinate(NormalizeTrophyCoordinate(settings.TrophyNotificationPositionX, fallback.X));
        var y = QuantizeTrophyCoordinate(NormalizeTrophyCoordinate(settings.TrophyNotificationPositionY, fallback.Y));
        settings.TrophyNotificationPositionX = x;
        settings.TrophyNotificationPositionY = y;
        settings.TrophyNotificationPosition = PositionForTrophyAnchor(x, y);
    }

    private static double QuantizeTrophyCoordinate(double value) => value < 0.25d ? 0d : value < 0.75d ? 0.5d : 1d;

    private static string PositionForTrophyAnchor(double x, double y) => (x, y) switch
    {
        (0d, 0d) => "top-left",
        (0.5d, 0d) => "top-center",
        (1d, 0d) => "top-right",
        (0d, 0.5d) => "center-left",
        (0.5d, 0.5d) => "center",
        (1d, 0.5d) => "center-right",
        (0d, 1d) => "bottom-left",
        (0.5d, 1d) => "bottom-center",
        _ => "bottom-right",
    };

    private static string NormalizeTrophySoundCue(string? value, bool soundEnabled)
    {
        if (!soundEnabled) return "off";
        return value?.Trim().ToLowerInvariant() switch
        {
            "soft" => "soft",
            "off" => "off",
            _ => "exo",
        };
    }

    private static bool HasFiniteNumber(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetDouble(out var number) &&
        double.IsFinite(number);
}
