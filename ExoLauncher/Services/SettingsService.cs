using System.Text.Json;
using System.Collections.Frozen;
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
    // Guarded by _persistenceGate. An unchanged snapshot is not rewritten.
    private string? _lastWrittenJson;
    private long _revision;
    private long _libraryPreferencesRevision = -1;
    private LibraryPreferences? _libraryPreferences;

    internal sealed record LibraryPreferences(
        FrozenDictionary<string, DateTimeOffset> LastPlayed,
        FrozenDictionary<string, string> CustomCovers,
        FrozenDictionary<string, string> PreferredGameSources);

    /// <summary>One immutable, parsed view for a whole library projection.</summary>
    internal LibraryPreferences GetLibraryPreferences()
    {
        lock (_stateGate)
        {
            if (_libraryPreferences is not null && _libraryPreferencesRevision == _revision)
                return _libraryPreferences;
            var played = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in _current.LastPlayed)
                if (DateTimeOffset.TryParse(pair.Value, out var value)) played[pair.Key] = value;
            var covers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in _current.CustomCoverImages)
                if (GameCoverImageStore.FileName(pair.Value) is { } name) covers[pair.Key] = name;
            _libraryPreferences = new(
                played.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
                covers.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
                _current.PreferredGameSources.ToFrozenDictionary(StringComparer.Ordinal));
            _libraryPreferencesRevision = _revision;
            return _libraryPreferences;
        }
    }

    public SettingsService()
    {
    }

    /// <summary>
    /// <see cref="AppSettings.CountHoursAcrossAccounts"/> as last loaded or
    /// saved, for the static playtime readers (Steam localconfig, Epic cache).
    /// </summary>
    internal static volatile bool CountHoursAcrossAccounts;

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

    /// <summary>
    /// The same snapshot for as long as nothing changed, for code that reads settings many times a
    /// second (a call's sound reads them every 20 ms). Never mutate it.
    /// </summary>
    public AppSettings Shared
    {
        get
        {
            lock (_stateGate)
            {
                if (_shared is null || _sharedRevision != _revision) { _shared = Clone(_current); _sharedRevision = _revision; }
                return _shared;
            }
        }
    }

    private AppSettings? _shared;
    private long _sharedRevision = -1;

    public void Load()
    {
        // Load participates in the same disk gate as saves. Startup is the only
        // normal caller, but serializing it also prevents a diagnostic reload
        // from racing an in-flight atomic replacement.
        lock (_persistenceGate)
        {
            AppSettings loaded = new();
            var failed = false;
            var corrupt = false;
            var hasExplicitOnboardingState = false;
            try
            {
                if (File.Exists(PathHelper.SettingsPath))
                {
                    var json = File.ReadAllText(PathHelper.SettingsPath);
                    using var settingsDocument = JsonDocument.Parse(json);
                    var settingsRoot = settingsDocument.RootElement;
                    hasExplicitOnboardingState = settingsRoot.TryGetProperty("onboardingComplete", out var onboarding) &&
                        onboarding.ValueKind is JsonValueKind.True or JsonValueKind.False;
                    loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts)
                        ?? throw new JsonException("settings.json deserialized to null");
                    NormalizeLoaded(loaded);
                }
            }
            catch (Exception ex)
            {
                failed = true;
                corrupt = ex is JsonException or NotSupportedException;
                loaded = new();
                AppLog.Warn("Settings load failed: " + ex.Message);
            }

            // Once a corrupt file is moved aside, defaults can be saved without
            // destroying it. A file that could not be opened (locked by
            // antivirus or a backup) is intact: it is only copied aside, and
            // saving stays blocked for the session so defaults never replace
            // it; the next start reads it normally.
            var blockSaves = failed && !(corrupt && QuarantineUnreadable());
            if (failed && !corrupt) CopyUnreadable();
            // A successfully persisted explicit value is authoritative. An old
            // advisory marker must not undo a completed "run setup again" save.
            if (failed || !hasExplicitOnboardingState) ApplyOnboardingMarker(loaded);
            _lastWrittenJson = null;
            lock (_stateGate)
            {
                _current = loaded;
                _loadFailed = blockSaves;
                _revision++;
                CountHoursAcrossAccounts = loaded.CountHoursAcrossAccounts;
            }
        }
    }

    private static void NormalizeLoaded(AppSettings loaded)
    {
        // Product defaults — not user-toggleable (UI removed).
        EnforceProductDefaults(loaded);
        loaded.AllowResize = false;
        loaded.CheckForUpdates = true;
        loaded.CopyPortableIntoLibrary = false;
        loaded.Recent ??= new List<string>();
        loaded.LaunchOverrides = CopyLaunchOverrides(loaded.LaunchOverrides);
        loaded.CustomCoverImages = NormalizeCustomCoverImages(loaded.CustomCoverImages);
        loaded.PreferredGameSources = NormalizePreferredGameSources(loaded.PreferredGameSources);
        loaded.LastPlayed = CopyStrings(loaded.LastPlayed);
        loaded.UpscalerAntiCheatConsent = CopyStrings(loaded.UpscalerAntiCheatConsent);
        loaded.HdrGameModes = CopyStrings(loaded.HdrGameModes);
        loaded.NvidiaColor = CopyStrings(loaded.NvidiaColor);
        loaded.SdrGammaBefore = CopyStrings(loaded.SdrGammaBefore);
        loaded.DiscordVoiceTunes = CopyStrings(loaded.DiscordVoiceTunes);
        loaded.LibraryFolders = NormalizeLibraryFolders(loaded.LibraryFolders);
        if (string.IsNullOrWhiteSpace(loaded.SortMode) || !IsLibrarySortMode(loaded.SortMode))
            loaded.SortMode = "name";
    }

    /// <summary>Move an unreadable settings file aside for recovery instead of
    /// letting the next save silently overwrite it with defaults. Returns true
    /// when nothing unreadable is left at the settings path.</summary>
    private static bool QuarantineUnreadable()
    {
        try
        {
            var path = PathHelper.SettingsPath;
            if (!File.Exists(path)) return true;
            var backup = $"{path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}";
            File.Move(path, backup, overwrite: true);
            AppLog.Warn($"Settings quarantined to {backup}; starting from defaults.");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Settings quarantine failed: " + ex.Message);
            return false;
        }
    }

    private static void CopyUnreadable()
    {
        try
        {
            var path = PathHelper.SettingsPath;
            if (!File.Exists(path)) return;
            var backup = $"{path}.unreadable-{DateTime.UtcNow:yyyyMMddHHmmss}";
            File.Copy(path, backup, overwrite: true);
            AppLog.Warn($"Settings copied to {backup}; saving is off for this session.");
        }
        catch (Exception ex)
        {
            AppLog.Warn("Settings copy failed: " + ex.Message + "; saving is off for this session.");
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

    /// <summary>
    /// Keep the advisory marker aligned with the persisted setting. Setting
    /// onboarding to false is the non-destructive "run setup again" path, so it
    /// clears only this marker; completing setup recreates it.
    /// </summary>
    private static bool TryReconcileOnboardingMarker(bool complete, out string? error)
    {
        error = null;
        try
        {
            if (complete)
            {
                if (!File.Exists(PathHelper.OnboardedMarkerPath))
                {
                    File.WriteAllText(PathHelper.OnboardedMarkerPath,
                        DateTimeOffset.UtcNow.ToString("O"));
                }
            }
            else
            {
                File.Delete(PathHelper.OnboardedMarkerPath);
            }
            return true;
        }
        catch (Exception ex)
        {
            error = "Onboarding state could not be saved: " + ex.Message;
            AppLog.Warn(error);
            return false;
        }
    }

    /// <summary>Persist settings. Returns false and sets error when disk write fails.</summary>
    public bool TrySave(out string? error)
    {
        return TryCommitMutation(settings =>
        {
            if (string.IsNullOrWhiteSpace(settings.AppVersion)) settings.AppVersion = "0.0.0";
        }, out error);
    }

    /// <summary>
    /// Serializes mutations with persistence, applying each edit to a detached
    /// candidate. Readers keep seeing the last committed state until its atomic
    /// replacement succeeds. A failed write cannot change the meaning of a retry
    /// or roll back a different writer's successful change.
    /// </summary>
    private bool TryCommitMutation(Action<AppSettings> mutate, out string? error)
    {
        error = null;
        lock (_persistenceGate)
        {
            AppSettings candidate;
            lock (_stateGate)
            {
                if (_loadFailed)
                {
                    error = "Settings were not saved because settings.json could not be read.";
                    AppLog.Warn(error);
                    return false;
                }
                candidate = Clone(_current);
            }

            mutate(candidate);
            EnforceProductDefaults(candidate);
            // A profile callback may retain its argument; never publish that
            // caller-owned object as mutable service state.
            var committed = Clone(candidate);
            var json = JsonSerializer.Serialize(committed, JsonOpts);
            if (string.Equals(json, _lastWrittenJson, StringComparison.Ordinal))
                return true;
            try
            {
                WriteSnapshotAtomically(json);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                AppLog.Warn("Settings save failed: " + ex.Message);
                return false;
            }

            _lastWrittenJson = json;
            lock (_stateGate)
            {
                _current = committed;
                _revision++;
                CountHoursAcrossAccounts = committed.CountHoursAcrossAccounts;
            }
            // The settings file is the commit point. A stale advisory marker
            // cannot override that explicit value on the next load.
            TryReconcileOnboardingMarker(committed.OnboardingComplete, out _);
            return true;
        }
    }

    private void CommitMutation(Action<AppSettings> mutate)
    {
        if (!TryCommitMutation(mutate, out var error))
            throw new InvalidOperationException(error ?? "Could not save settings.");
    }

    private static void WriteSnapshotAtomically(string json)
    {
        var path = PathHelper.SettingsPath;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        // A unique sibling temp protects against another SettingsService (for
        // example a recovery tool) sharing this path. The final move remains a
        // single same-volume filesystem replacement.
        var tmp = path + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(
                       tmp,
                       FileMode.Create,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(json);
                writer.Flush();
                // Power loss between write and replace must never leave an
                // empty or truncated settings.json behind after the rename.
                stream.Flush(flushToDisk: true);
            }

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
        }
        TryCommitMutation(settings => settings.AppVersion = version, out _);
    }

    public void Flush() => Save();

    /// <remarks>
    /// Locked product defaults (see <see cref="EnforceProductDefaults"/>) and
    /// the dead libraryShowInstalled/libraryShowOwned chips are not patchable;
    /// their AppSettings properties stay so older settings.json files load.
    /// </remarks>
    /// <summary>Settings > Downloads. Hours: both 0-23, or <paramref name="anyTime"/> to clear them.</summary>
    public void ApplyDownloadPatch(bool? pauseWhilePlaying, int? hoursStart, int? hoursEnd, bool anyTime, bool? autoUpdate)
    {
        CommitMutation(settings =>
        {
            if (pauseWhilePlaying is { } pause) settings.PauseDownloadsWhilePlaying = pause;
            if (anyTime) (settings.DownloadHoursStart, settings.DownloadHoursEnd) = (null, null);
            else if (hoursStart is >= 0 and <= 23 && hoursEnd is >= 0 and <= 23 && hoursStart != hoursEnd)
                (settings.DownloadHoursStart, settings.DownloadHoursEnd) = (hoursStart, hoursEnd);
            if (autoUpdate is { } auto) settings.AutoUpdateGames = auto;
        });
    }

    public void ApplyPatch(
        string? sortMode = null,
        string? defaultInstallRoot = null,
        bool? onboardingComplete = null,
        bool? reduceMotion = null,
        string? textColour = null,
        string? textSize = null,
        string? glass = null,
        bool? framelessPinTop = null,
        bool? framelessPinSide = null,
        bool? showLibraryTools = null,
        IReadOnlyDictionary<string, string>? preferredGameSources = null,
        bool? countHoursAcrossAccounts = null,
        bool? hdrForGames = null,
        bool? hdrSdrGamma = null,
        bool? discordNoiseSuppression = null,
        bool? discordQuietWhilePlaying = null)
    {
        CommitMutation(settings =>
        {
            if (!string.IsNullOrWhiteSpace(sortMode))
                settings.SortMode = IsLibrarySortMode(sortMode) ? sortMode! : "name";
            if (defaultInstallRoot is not null)
                settings.DefaultInstallRoot = string.IsNullOrWhiteSpace(defaultInstallRoot) ? null : defaultInstallRoot;
            if (onboardingComplete is not null) settings.OnboardingComplete = onboardingComplete.Value;
            if (reduceMotion is not null)
                settings.ReduceMotion = reduceMotion.Value;
            if (textColour is not null)
                settings.TextColour = NormalizeTextColour(textColour);
            if (textSize is not null)
                settings.TextSize = NormalizeTextSize(textSize);
            if (glass is not null)
                settings.Glass = NormalizeGlass(glass);
            if (framelessPinTop is not null)
                settings.FramelessPinTop = framelessPinTop.Value;
            if (framelessPinSide is not null)
                settings.FramelessPinSide = framelessPinSide.Value;
            if (showLibraryTools is not null)
                settings.ShowLibraryTools = showLibraryTools.Value;
            if (countHoursAcrossAccounts is not null)
                settings.CountHoursAcrossAccounts = countHoursAcrossAccounts.Value;
            if (hdrForGames is not null)
                settings.HdrForGames = hdrForGames.Value;
            if (hdrSdrGamma is not null)
                settings.HdrSdrGamma = hdrSdrGamma.Value;
            if (discordNoiseSuppression is not null)
                settings.DiscordNoiseSuppression = discordNoiseSuppression.Value;
            if (discordQuietWhilePlaying is not null)
                settings.DiscordQuietWhilePlaying = discordQuietWhilePlaying.Value;
            if (preferredGameSources is not null)
            {
                foreach (var pair in preferredGameSources)
                {
                    var key = pair.Key.Trim().ToLowerInvariant();
                    var source = pair.Value?.Trim();
                    if (!IsSafePreferenceKey(key) || source is null ||
                        source.Length > 512 || source.Any(char.IsControl))
                        throw new InvalidDataException("Invalid preferred game source.");
                    if (source.Length == 0) settings.PreferredGameSources.Remove(key);
                    else settings.PreferredGameSources[key] = source;
                }
                if (settings.PreferredGameSources.Count > 5_000)
                    throw new InvalidDataException("Too many preferred game sources are saved.");
            }
            EnforceProductDefaults(settings);
        });
    }

    /// <summary>
    /// Applies a profile edit to a detached candidate and commits it to disk
    /// before publishing. Throws without changing state when persistence fails.
    /// </summary>
    public void UpdateProfile(Action<AppSettings> apply)
    {
        ArgumentNullException.ThrowIfNull(apply);
        CommitMutation(settings =>
        {
            apply(settings);
        });
    }

    /// <summary>The anti-cheat the user accepted the risk of for this game's upscaler files, or null.</summary>
    public string? UpscalerConsent(string gameId)
    {
        lock (_stateGate)
            return _current.UpscalerAntiCheatConsent.TryGetValue(gameId, out var antiCheat) ? antiCheat : null;
    }

    /// <summary>Game sheet > Upscalers: Allow changes stores the anti-cheat shown, Stop allowing removes it.</summary>
    public void SetUpscalerConsent(string gameId, string? antiCheat)
    {
        if (string.IsNullOrWhiteSpace(gameId) || gameId.Length > 512 || gameId.Any(char.IsControl))
            throw new InvalidDataException("Invalid game id.");
        CommitMutation(settings =>
        {
            if (antiCheat is null) settings.UpscalerAntiCheatConsent.Remove(gameId);
            else settings.UpscalerAntiCheatConsent[gameId] = antiCheat;
            if (settings.UpscalerAntiCheatConsent.Count > 1_000)
                throw new InvalidDataException("Too many upscaler consents are saved.");
        });
    }

    /// <summary>"auto", "on" or "off" for HDR while this game runs.</summary>
    public string HdrGameMode(string gameId)
    {
        lock (_stateGate)
            return _current.HdrGameModes.TryGetValue(gameId, out var mode) ? mode : "auto";
    }

    /// <summary>Library > folders: the whole list, as the Library has it now.</summary>
    public void SetLibraryFolders(IEnumerable<LibraryFolder> folders)
    {
        var clean = NormalizeLibraryFolders(folders);
        CommitMutation(settings => settings.LibraryFolders = clean);
    }

    internal static readonly string[] FolderColors = ["gray", "red", "orange", "yellow", "green", "teal", "blue", "pink"];

    /// <summary>
    /// A copy that holds: up to 200 folders with an id each (64 characters at most, once), a name (40 characters,
    /// "Folder" when blank), one of <see cref="FolderColors"/> (gray otherwise), and up to 2,000 game ids, each game in
    /// the first folder that lists it.
    /// </summary>
    internal static List<LibraryFolder> NormalizeLibraryFolders(IEnumerable<LibraryFolder?>? folders)
    {
        var clean = new List<LibraryFolder>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static bool Valid(string? text, int max) => !string.IsNullOrWhiteSpace(text) && text.Length <= max && !text.Any(char.IsControl);
        foreach (var folder in folders ?? [])
        {
            if (folder is null || !Valid(folder.Id, 64) || !ids.Add(folder.Id)) continue;
            var name = new string((folder.Name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
            if (name.Length > 40) name = name[..40].TrimEnd();
            clean.Add(new LibraryFolder
            {
                Id = folder.Id,
                Name = name.Length > 0 ? name : "Folder",
                Color = FolderColors.Contains(folder.Color) ? folder.Color : "gray",
                Games = (folder.Games ?? []).Where(game => Valid(game, 512) && placed.Add(game)).Take(2_000).ToList(),
            });
            if (clean.Count == 200) break;
        }
        return clean;
    }

    /// <summary>Game sheet > HDR: Auto removes the override.</summary>
    public void SetHdrGameMode(string gameId, string mode)
    {
        if (string.IsNullOrWhiteSpace(gameId) || gameId.Length > 512 || gameId.Any(char.IsControl))
            throw new InvalidDataException("Invalid game id.");
        if (mode is not ("auto" or "on" or "off")) throw new InvalidDataException("Unknown HDR choice.");
        CommitMutation(settings =>
        {
            if (mode == "auto") settings.HdrGameModes.Remove(gameId);
            else settings.HdrGameModes[gameId] = mode;
            if (settings.HdrGameModes.Count > 5_000)
                throw new InvalidDataException("Too many HDR choices are saved.");
        });
    }

    /// <summary>Properties > Launch options: what Exo adds to this game's command line.</summary>
    public string LaunchArgs(string gameId)
    {
        lock (_stateGate)
            return _current.LaunchOverrides.TryGetValue(gameId, out var launch) ? launch.ExtraArgs ?? "" : "";
    }

    /// <summary>Saves this game's launch options; empty removes them (and the override once nothing else is set).</summary>
    public void SetLaunchArgs(string gameId, string args)
    {
        if (string.IsNullOrWhiteSpace(gameId) || gameId.Length > 512 || gameId.Any(char.IsControl))
            throw new InvalidDataException("Invalid game id.");
        var value = args.Trim();
        if (value.Length > 1024 || value.Any(char.IsControl)) throw new InvalidDataException("Launch options are too long.");
        CommitMutation(settings =>
        {
            settings.LaunchOverrides.TryGetValue(gameId, out var launch);
            launch ??= new GameLaunchOverride();
            launch.ExtraArgs = value.Length == 0 ? null : value;
            if (launch.IsEmpty) settings.LaunchOverrides.Remove(gameId);
            else settings.LaunchOverrides[gameId] = launch;
            if (settings.LaunchOverrides.Count > 5_000)
                throw new InvalidDataException("Too many launch options are saved.");
        });
    }

    /// <summary>Exo's start: apps still starting with Windows stop, once each (Epic's notifications go quiet with it).</summary>
    public void HandStartupToExo()
    {
        var off = StartupApps.HandOff(Current.StartupHandedToExo.ToHashSet(StringComparer.Ordinal));
        if (off.Count == 0) return;
        if (off.Contains("epic")) Adapters.EpicQuietSettings.HandToExo();
        TryCommitMutation(settings => settings.StartupHandedToExo = [.. settings.StartupHandedToExo.Union(off)], out _);
    }

    /// <summary>The retired bridge choice, once <see cref="MouseBridgeRetirement"/> has acted on it.</summary>
    public void ForgetMouseBridge() => CommitMutation(settings =>
    {
        settings.MouseBridge = null;
        settings.MouseBridgeAppAutostart = null;
        settings.MouseBridgeAppPath = null;
    });

    public void RecordLaunch(string gameId)
    {
        if (string.IsNullOrWhiteSpace(gameId)) return;
        TryCommitMutation(settings =>
        {
            settings.LastPlayed[gameId] = DateTimeOffset.UtcNow.ToString("O");
            settings.Recent.RemoveAll(x => string.Equals(x, gameId, StringComparison.OrdinalIgnoreCase));
            settings.Recent.Insert(0, gameId);
            if (settings.Recent.Count > 40)
                settings.Recent.RemoveRange(40, settings.Recent.Count - 40);
        }, out _);
    }

    /// <summary>
    /// Returns the first valid Exo-owned cover attached to any exact source id.
    /// A grouped card supplies all of its variants so one override paints them all.
    /// </summary>
    public string? GetCustomCoverImage(IEnumerable<string> gameIds)
    {
        ArgumentNullException.ThrowIfNull(gameIds);
        lock (_stateGate)
        {
            foreach (var gameId in gameIds)
            {
                if (string.IsNullOrWhiteSpace(gameId) ||
                    !_current.CustomCoverImages.TryGetValue(gameId, out var value))
                    continue;
                var fileName = GameCoverImageStore.FileName(value);
                if (fileName is not null) return fileName;
            }
        }
        return null;
    }

    /// <summary>
    /// Atomically sets or clears a visual card's exact source ids. Invalid file
    /// names are rejected before state changes, and a failed disk write restores
    /// the prior in-memory dictionary instead of leaving a half-applied group.
    /// </summary>
    public void SetCustomCoverImages(IEnumerable<string> gameIds, string? fileName)
    {
        ArgumentNullException.ThrowIfNull(gameIds);
        var ids = gameIds
            .Select(id => id?.Trim() ?? string.Empty)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (ids.Length == 0 || ids.Any(id => !IsSafeCustomCoverGameId(id)))
            throw new InvalidDataException("Invalid game id.");
        if (ids.Length > 32) throw new InvalidDataException("Too many game sources.");

        var normalizedFile = fileName is null ? null : GameCoverImageStore.FileName(fileName);
        if (fileName is not null && normalizedFile is null)
            throw new InvalidDataException("Invalid custom cover file name.");

        CommitMutation(settings =>
        {
            foreach (var id in ids)
            {
                if (normalizedFile is null) settings.CustomCoverImages.Remove(id);
                else settings.CustomCoverImages[id] = normalizedFile;
            }
            if (settings.CustomCoverImages.Count > 1_000)
                throw new InvalidDataException("Too many custom covers are saved.");
        });
    }

    public IReadOnlySet<string> CustomCoverImageFiles()
    {
        lock (_stateGate)
        {
            return _current.CustomCoverImages.Values
                .Select(GameCoverImageStore.FileName)
                .Where(name => name is not null)
                .Select(name => name!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void EnforceProductDefaults(AppSettings settings)
    {
        settings.AntiCheatSafeMode = true;
        settings.MinimizeWhilePlaying = true;
        settings.AutoInstallRedistributables = true;
        settings.CloseStoreClientsAfterLaunch = true;
        settings.FramelessMode = true;
        settings.CopyPortableIntoLibrary = false;
        settings.AllowResize = false;
        settings.CheckForUpdates = true;
        settings.TextColour = NormalizeTextColour(settings.TextColour);
        settings.TextSize = NormalizeTextSize(settings.TextSize);
        settings.Glass = NormalizeGlass(settings.Glass);
        if (!IsLibrarySortMode(settings.SortMode)) settings.SortMode = "name";
    }

    private static bool IsLibrarySortMode(string? value) =>
        value is "name" or "installed" or "recent" or "played" or "size" or "store";

    private static string NormalizeTextColour(string? value) =>
        string.Equals(value, "grey", StringComparison.OrdinalIgnoreCase) ? "grey" : "white";

    private static string NormalizeTextSize(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "small" => "small",
            "large" => "large",
            _ => "normal",
        };

    private static string NormalizeGlass(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "tinted" => "tinted",
            "solid" => "solid",
            _ => "clear",
        };

    internal static AppSettings Clone(AppSettings source) => new()
    {
        CloseStoreClientsAfterLaunch = source.CloseStoreClientsAfterLaunch,
        AutoInstallRedistributables = source.AutoInstallRedistributables,
        MinimizeWhilePlaying = source.MinimizeWhilePlaying,
        AntiCheatSafeMode = source.AntiCheatSafeMode,
        ReduceMotion = source.ReduceMotion,
        FramelessMode = source.FramelessMode,
        FramelessPinTop = source.FramelessPinTop,
        FramelessPinSide = source.FramelessPinSide,
        TextColour = source.TextColour,
        TextSize = source.TextSize,
        Glass = source.Glass,
        AppVersion = source.AppVersion,
        DefaultInstallRoot = source.DefaultInstallRoot,
        CopyPortableIntoLibrary = source.CopyPortableIntoLibrary,
        AllowResize = source.AllowResize,
        CheckForUpdates = source.CheckForUpdates,
        SortMode = source.SortMode,
        PreferredGameSources = NormalizePreferredGameSources(source.PreferredGameSources),
        Recent = source.Recent.ToList(),
        LastPlayed = CopyStrings(source.LastPlayed),
        UpscalerAntiCheatConsent = CopyStrings(source.UpscalerAntiCheatConsent),
        HdrGameModes = CopyStrings(source.HdrGameModes),
        NvidiaColor = CopyStrings(source.NvidiaColor),
        SdrGammaBefore = CopyStrings(source.SdrGammaBefore),
        HdrSwitchedOn = source.HdrSwitchedOn,

        OnboardingComplete = source.OnboardingComplete,
        LaunchOverrides = CopyLaunchOverrides(source.LaunchOverrides),
        CustomCoverImages = NormalizeCustomCoverImages(source.CustomCoverImages),
        LibraryShowInstalled = source.LibraryShowInstalled,
        LibraryShowOwned = source.LibraryShowOwned,
        ShowLibraryTools = source.ShowLibraryTools,
        CountHoursAcrossAccounts = source.CountHoursAcrossAccounts,
        HdrForGames = source.HdrForGames,
        HdrSdrGamma = source.HdrSdrGamma,
        DiscordNoiseSuppression = source.DiscordNoiseSuppression,
        DiscordOthersNoiseSuppression = source.DiscordOthersNoiseSuppression,
        DiscordQuietWhilePlaying = source.DiscordQuietWhilePlaying,
        DiscordInputDevice = source.DiscordInputDevice is { Length: > 0 and <= 512 } input ? input : null,
        DiscordOutputDevice = source.DiscordOutputDevice is { Length: > 0 and <= 512 } output ? output : null,
        DiscordInputVolume = Math.Clamp(source.DiscordInputVolume, 0, 200),
        DiscordOutputVolume = Math.Clamp(source.DiscordOutputVolume, 0, 200),
        DiscordSoundboardVolume = Math.Clamp(source.DiscordSoundboardVolume, 0, 100),
        DiscordSoundboardMuted = source.DiscordSoundboardMuted,
        DiscordInputProfile = source.DiscordInputProfile is "exo" or "custom" ? source.DiscordInputProfile : null,
        DiscordPushToTalk = source.DiscordPushToTalk,
        DiscordPttDelay = Math.Clamp(source.DiscordPttDelay, 0, 2000),
        DiscordAutoSensitivity = source.DiscordAutoSensitivity,
        DiscordSensitivity = Math.Clamp(source.DiscordSensitivity, -100, 0),
        DiscordEchoCancellation = source.DiscordEchoCancellation,
        DiscordAutoGain = source.DiscordAutoGain,
        DiscordVoiceFocus = source.DiscordVoiceFocus,
        DiscordVoiceTunes = CopyStrings(source.DiscordVoiceTunes),
        DiscordAttenuation = Math.Clamp(source.DiscordAttenuation, 0, 100),
        DiscordAttenuateSelf = source.DiscordAttenuateSelf,
        DiscordAttenuateOthers = source.DiscordAttenuateOthers,
        DiscordKeybinds = [.. (source.DiscordKeybinds ?? []).Where(k => k is not null && DiscordClient.Voice.Keybinds.IsAction(k.Action) && k.Keys is { Count: > 0 and <= 4 } keys && keys.All(key => key is > 0 and < 255))
            .Take(50).Select(k => new DiscordKeybind { Action = k.Action, Keys = [.. k.Keys] })],
        DiscordDesktopNotifications = source.DiscordDesktopNotifications,
        DiscordUnreadBadge = source.DiscordUnreadBadge,
        DiscordTaskbarFlash = source.DiscordTaskbarFlash,
        DiscordDisabledSounds = [.. (source.DiscordDisabledSounds ?? []).Where(DiscordClient.DiscordSounds.IsSound).Distinct()],
        DiscordDisableAllSounds = source.DiscordDisableAllSounds,
        DiscordSelectedChannelSound = source.DiscordSelectedChannelSound,
        DiscordSoundVolume = Math.Clamp(source.DiscordSoundVolume, 0, 100),
        PauseDownloadsWhilePlaying = source.PauseDownloadsWhilePlaying,
        DownloadHoursStart = source.DownloadHoursStart is >= 0 and <= 23 ? source.DownloadHoursStart : null,
        DownloadHoursEnd = source.DownloadHoursEnd is >= 0 and <= 23 ? source.DownloadHoursEnd : null,
        AutoUpdateGames = source.AutoUpdateGames,
        MouseBridge = source.MouseBridge is "exo" or "app" ? source.MouseBridge : null,
        MouseBridgeAppAutostart = source.MouseBridgeAppAutostart,
        MouseBridgeAppPath = source.MouseBridgeAppPath,
        StartLucidWithExo = source.StartLucidWithExo,
        SteamOverlayDefaultOn = source.SteamOverlayDefaultOn,
        QuietStoreApps = source.QuietStoreApps,
        LibraryFolders = NormalizeLibraryFolders(source.LibraryFolders),
        SteamOverlay = CopyStrings(source.SteamOverlay),
        StartupHandedToExo = [.. (source.StartupHandedToExo ?? []).Where(id => StartupApps.Known.Any(app => app.Id == id)).Distinct()],
        LucidPath = source.LucidPath is { Length: > 0 and <= 1024 } lucid ? lucid : null,
    };

    // settings.json is read with case-sensitive keys and may hold nulls. Copies
    // skip nulls and let the last case variant win instead of throwing.
    private static Dictionary<string, string> CopyStrings(IDictionary<string, string>? source)
    {
        var copy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (source is null) return copy;
        foreach (var pair in source)
            if (pair.Key is not null && pair.Value is not null) copy[pair.Key] = pair.Value;
        return copy;
    }

    private static Dictionary<string, GameLaunchOverride> CopyLaunchOverrides(
        IDictionary<string, GameLaunchOverride>? source)
    {
        var copy = new Dictionary<string, GameLaunchOverride>(StringComparer.OrdinalIgnoreCase);
        if (source is null) return copy;
        foreach (var pair in source)
        {
            if (pair.Key is null || pair.Value is null) continue;
            copy[pair.Key] = new GameLaunchOverride
            {
                ExtraArgs = pair.Value.ExtraArgs,
                WorkingDirectory = pair.Value.WorkingDirectory,
                RunAsAdmin = pair.Value.RunAsAdmin,
            };
        }
        return copy;
    }

    private static Dictionary<string, string> NormalizeCustomCoverImages(
        IDictionary<string, string>? source)
    {
        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (source is null) return normalized;
        foreach (var pair in source)
        {
            var id = pair.Key?.Trim() ?? string.Empty;
            var fileName = GameCoverImageStore.FileName(pair.Value);
            if (!IsSafeCustomCoverGameId(id) || fileName is null) continue;
            normalized[id] = fileName;
            if (normalized.Count >= 1_000) break;
        }
        return normalized;
    }

    private static bool IsSafeCustomCoverGameId(string id) =>
        id.Length is > 0 and <= 512 && !id.Any(char.IsControl);

    private static bool IsSafePreferenceKey(string key) =>
        key.Length is > 0 and <= 512 && !key.Any(character => char.IsControl(character) || char.IsWhiteSpace(character));

    private static Dictionary<string, string> NormalizePreferredGameSources(IDictionary<string, string>? source)
    {
        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        if (source is null) return normalized;
        foreach (var pair in source)
        {
            var key = pair.Key?.Trim().ToLowerInvariant() ?? string.Empty;
            var value = pair.Value?.Trim() ?? string.Empty;
            if (!IsSafePreferenceKey(key) || value.Length is < 1 or > 512 || value.Any(char.IsControl)) continue;
            normalized[key] = value;
            if (normalized.Count >= 5_000) break;
        }
        return normalized;
    }
}
