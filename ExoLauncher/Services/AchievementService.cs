using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ExoLauncher.Helpers;
using ExoLauncher.Models;
using ExoLauncher.Services.Achievements;

namespace ExoLauncher.Services;

/// <summary>
/// Normalizes provider snapshots and persists the latest verified account
/// state so library activity can show unlocked / total counts.
/// </summary>
public sealed partial class AchievementService : IDisposable
{
    // v2 files may still carry the retired notification outbox and ledger;
    // those fields are ignored on load and dropped on the next save.
    private const int SchemaVersion = 2;
    private const int MaxAchievements = 10_000;
    // Counts move mostly during play, which RefreshAfterSessionAsync covers.
    // Opening a game only fills a missing or old count, once per window.
    private static readonly TimeSpan OpenRefreshMaxAge = TimeSpan.FromHours(12);
    private static readonly TimeSpan OpenRefreshRetry = TimeSpan.FromMinutes(30);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Raised off the dispatcher with the game id whose stored counts changed.</summary>
    public event Action<string>? SummaryChanged;

    private readonly Dictionary<string, DateTimeOffset> _openRefreshAttempts = new(StringComparer.OrdinalIgnoreCase);

    private readonly IReadOnlyList<IAchievementProvider> _providers;
    private readonly string _statePath;
    private readonly object _refreshRequestsGate = new();
    private readonly Dictionary<RefreshRequestKey, RefreshLane> _refreshRequests = [];
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly object _stateGate = new();
    private PersistentState _state;
    private readonly Lazy<Task> _initialization;
    private bool _disposed;

    public AchievementService()
        : this(
            CreateDefaultProviders(),
            Path.Combine(PathHelper.AppDataDir, "achievements.json"),
            deferStateLoad: true)
    {
    }

    internal AchievementService(
        IEnumerable<IAchievementProvider> providers,
        string statePath,
        bool deferStateLoad = false,
        Func<DateTimeOffset>? persistenceClock = null)
    {
        _providers = providers.ToArray();
        _statePath = statePath;
        _persistenceClock = persistenceClock ?? (() => DateTimeOffset.UtcNow);
        _lastTimestampCheckpoint = _persistenceClock();
        _state = deferStateLoad ? new PersistentState() : LoadState(statePath);
        _initialization = new Lazy<Task>(() => deferStateLoad ? Task.Run(() =>
        {
            var loaded = LoadState(statePath);
            lock (_stateGate) _state = loaded;
        }) : Task.CompletedTask);
    }

    /// <summary>Hydrate off the dispatcher, before any provider can persist new state.</summary>
    public Task InitializeAsync(CancellationToken ct = default) => _initialization.Value.WaitAsync(ct);

    /// <summary>Bridge-safe summary for the unambiguously active provider account.</summary>
    public GameAchievementSummary? GetSummary(string gameId)
    {
        if (string.IsNullOrWhiteSpace(gameId)) return null;
        lock (_stateGate)
        {
            var state = _state.Games.Values
                .Where(row => string.Equals(row.GameId, gameId, StringComparison.OrdinalIgnoreCase))
                .Where(IsCurrentCoverage)
                .Where(row => !IsStaleSteamZeroPlaceholder(row))
                .Where(row => !IsUnverifiedSteamCommunitySnapshot(row))
                .OrderByDescending(row => row.LastObservedAtUtc)
                .FirstOrDefault();
            return state is null ? null : ToSummary(state);
        }
    }

    public async Task<AchievementSnapshot> RefreshAsync(
        GameEntry game,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var provider = FindProvider(game);
        if (provider is null)
            return Unsupported(game);

        string? coverage;
        try { coverage = provider.GetCurrentCoverageKey(game); }
        catch { coverage = null; }
        var key = new RefreshRequestKey(provider.Id.ToLowerInvariant(), game.Id.ToLowerInvariant(), coverage ?? string.Empty);
        Task<AchievementSnapshot> shared;
        Task previous = Task.CompletedTask;
        TaskCompletionSource<AchievementSnapshot>? owner = null;
        RefreshLane lane;
        lock (_refreshRequestsGate)
        {
            if (!_refreshRequests.TryGetValue(key, out lane!)) _refreshRequests[key] = lane = new();
            if (lane.Current is { } current)
            {
                shared = current;
            }
            else
            {
                owner = new(TaskCreationOptions.RunContinuationsAsynchronously);
                shared = owner.Task;
                previous = lane.Tail;
                lane.Tail = shared;
                lane.Current = shared;
            }
        }
        if (owner is not null)
            _ = CompleteRefreshRequestAsync(key, lane, previous, owner, provider, game);
        // Cancel only this caller's wait, never another caller's coalesced refresh.
        return await shared.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// One refresh after a launched game exits, so account activity reflects
    /// what was unlocked during play. Pending state is written immediately.
    /// </summary>
    public async Task RefreshAfterSessionAsync(GameEntry game, CancellationToken cancellationToken = default)
    {
        try
        {
            _ = await RefreshAsync(game, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            FlushDeferredState();
        }
    }

    /// <summary>
    /// Fills a missing or stale count when a game's sheet opens, at most once
    /// per game per retry window. SummaryChanged reports a new count.
    /// </summary>
    public void RefreshInBackgroundIfStale(GameEntry game, GameAchievementSummary? cached)
    {
        if (_disposed || FindProvider(game) is null) return;
        var now = DateTimeOffset.UtcNow;
        if (cached is not null && now - cached.LastUpdatedUtc < OpenRefreshMaxAge) return;
        lock (_openRefreshAttempts)
        {
            if (_openRefreshAttempts.TryGetValue(game.Id, out var last) && now - last < OpenRefreshRetry) return;
            _openRefreshAttempts[game.Id] = now;
        }
        _ = Task.Run(async () =>
        {
            try { _ = await RefreshAsync(game, _lifetimeCts.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { }
            catch (Exception ex) { AppLog.Debug($"Achievement refresh on open failed: {ex.GetType().Name}"); }
            finally { FlushDeferredState(); }
        });
    }

    private async Task CompleteRefreshRequestAsync(RefreshRequestKey key, RefreshLane lane, Task previous,
        TaskCompletionSource<AchievementSnapshot> completion, IAchievementProvider provider, GameEntry game)
    {
        try
        {
            try { await previous.ConfigureAwait(false); } catch { /* A failed prior request cannot stop recovery. */ }
            _lifetimeCts.Token.ThrowIfCancellationRequested();
            completion.TrySetResult(await RefreshProviderAsync(provider, game, _lifetimeCts.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { completion.TrySetCanceled(); }
        catch (Exception ex) { completion.TrySetException(ex); }
        finally
        {
            lock (_refreshRequestsGate)
            {
                if (lane.Current == completion.Task)
                    lane.Current = null;
                if (lane.Current is null && _refreshRequests.TryGetValue(key, out var current) && current == lane)
                    _refreshRequests.Remove(key);
            }
        }
    }

    private async Task<AchievementSnapshot> RefreshProviderAsync(IAchievementProvider provider, GameEntry game,
        CancellationToken cancellationToken)
    {
        AchievementSnapshot snapshot;
        try
        {
            snapshot = await provider.GetSnapshotAsync(game, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Debug($"{provider.Id} achievement provider failed: {ex.GetType().Name}");
            snapshot = Unavailable(provider, game, "Achievement data is temporarily unavailable.");
        }

        snapshot = SanitizeSnapshot(snapshot with { GameId = game.Id });

        if ((snapshot.Coverage is AchievementCoverageStatus.Partial or AchievementCoverageStatus.Complete) &&
            !IsStaleSteamZeroPlaceholder(snapshot))
        {
            try
            {
                if (!string.Equals(snapshot.ProviderId, provider.Id, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Achievement provider identity does not match the selected source.");
                ValidateSnapshot(snapshot);
                if (ApplySnapshot(game.Id, snapshot))
                {
                    try { SummaryChanged?.Invoke(game.Id); }
                    catch (Exception ex) { AppLog.Debug("Achievement change listener failed: " + ex.GetType().Name); }
                }
            }
            catch (InvalidDataException ex)
            {
                // Providers are independent parsers. Keep this final gate
                // fail-closed so one malformed cache/CLI response can never
                // become a plausible-looking count.
                AppLog.Debug($"{provider.Id} achievement consistency check failed: {ex.Message}");
                snapshot = Unavailable(
                    provider,
                    game,
                    "Achievement data failed consistency checks and was not used.");
            }
        }

        return snapshot;
    }

    private bool ApplySnapshot(string gameId, AchievementSnapshot snapshot)
    {
        lock (_stateGate)
        {
            var key = SnapshotKey(snapshot);
            if (_stateReadFailure is not null)
                throw new InvalidDataException(_stateReadFailure);
            var isNew = !_state.Games.TryGetValue(key, out var gameState);
            if (gameState is null)
            {
                gameState = new PersistedGameState
                {
                    GameId = gameId,
                    ProviderId = snapshot.ProviderId,
                    SourceGameId = snapshot.SourceGameId,
                    CoverageKey = snapshot.CoverageKey,
                };
                _state.Games[key] = gameState;
            }
            var changed = isNew || !EquivalentSnapshot(gameState, snapshot);

            // Keep only the rows this response reports; nothing else reads
            // older generations.
            var currentRows = snapshot.Entries
                .Select(row => row.Definition.ExternalId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var retracted in gameState.Entries.Keys
                         .Where(externalId => !currentRows.Contains(externalId))
                         .ToArray())
                gameState.Entries.Remove(retracted);
            foreach (var incoming in snapshot.Entries)
            {
                var externalId = incoming.Definition.ExternalId;
                gameState.Entries[externalId] = gameState.Entries.TryGetValue(externalId, out var previous)
                    ? MergeCurrent(previous, incoming)
                    : incoming;
            }

            gameState.GameId = gameId;
            gameState.LastObservedAtUtc = snapshot.ObservedAtUtc;
            gameState.ReportedTotal = snapshot.ReportedTotal;
            gameState.ReportedUnlocked = snapshot.ReportedUnlocked;
            gameState.Coverage = snapshot.Coverage;
            gameState.Capabilities = snapshot.Capabilities;
            gameState.Message = snapshot.Message;
            if (changed)
            {
                _dirtyGameKeys.Add(key);
                _stateDirty = true;
            }
            _timestampDirtyGameKeys.Add(key);
            // Changes are coalesced; RefreshAfterSessionAsync and Dispose
            // flush whatever is still pending.
            var now = _persistenceClock();
            if ((_stateDirty && now - _lastStateSave >= DeferredSaveInterval) ||
                now - _lastTimestampCheckpoint >= TimestampCheckpointInterval)
                SaveStateAtomic();
            return changed;
        }
    }

    private void FlushDeferredState()
    {
        // Never write the empty placeholder over a state file still loading.
        if (!_initialization.IsValueCreated || !_initialization.Value.IsCompletedSuccessfully) return;
        lock (_stateGate)
        {
            if (!_stateDirty) return;
            try { SaveStateAtomic(); }
            catch (Exception ex) { AppLog.Debug("Achievement state flush failed: " + ex.GetType().Name); }
        }
    }

    private static AchievementSnapshot ToSnapshot(PersistedGameState state) => new()
    {
        GameId = state.GameId,
        ProviderId = state.ProviderId,
        SourceGameId = state.SourceGameId,
        CoverageKey = state.CoverageKey,
        Coverage = state.Coverage,
        Capabilities = state.Capabilities,
        ReportedTotal = state.ReportedTotal,
        ReportedUnlocked = state.ReportedUnlocked,
        ObservedAtUtc = state.LastObservedAtUtc,
        Entries = VisibleEntries(state)
            .Select(SanitizeEntry)
            .OrderBy(row => row.Definition.ExternalId, StringComparer.OrdinalIgnoreCase)
            .ToArray(),
        Message = state.Message,
    };

    private static bool IsStaleSteamZeroPlaceholder(PersistedGameState state) =>
        string.Equals(state.ProviderId, "steam", StringComparison.OrdinalIgnoreCase) &&
        state.Coverage == AchievementCoverageStatus.Partial &&
        state.ReportedTotal == 0 &&
        state.ReportedUnlocked == 0 &&
        state.Entries.Count == 0;

    private static bool IsStaleSteamZeroPlaceholder(AchievementSnapshot snapshot) =>
        string.Equals(snapshot.ProviderId, "steam", StringComparison.OrdinalIgnoreCase) &&
        snapshot.Coverage == AchievementCoverageStatus.Partial &&
        snapshot.ReportedTotal == 0 &&
        snapshot.ReportedUnlocked == 0 &&
        snapshot.Entries.Count == 0;

    // Builds before the local-progress fix persisted a public Steam Community
    // catalog as a partial 0 / N account snapshot. It has no account authority;
    // hide it immediately so opening the game requests current local data.
    private static bool IsUnverifiedSteamCommunitySnapshot(PersistedGameState state) =>
        string.Equals(state.ProviderId, "steam", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(state.Message, "Steam Community achievement data.", StringComparison.Ordinal);

    private bool IsCurrentCoverage(PersistedGameState state)
    {
        try
        {
            var provider = _providers.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, state.ProviderId, StringComparison.OrdinalIgnoreCase));
            if (provider is null) return false;
            var game = new GameEntry
            {
                Id = state.GameId,
                Title = state.GameId,
                Store = provider.Store,
                LaunchTarget = state.SourceGameId,
            };
            var currentCoverageKey = provider.GetCurrentCoverageKey(game);
            return !string.IsNullOrWhiteSpace(currentCoverageKey) &&
                   string.Equals(currentCoverageKey, state.CoverageKey, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static AchievementSnapshot SanitizeSnapshot(AchievementSnapshot snapshot) =>
        snapshot with
        {
            Entries = snapshot.Entries.Select(SanitizeEntry).ToArray(),
        };

    private static AchievementEntry SanitizeEntry(AchievementEntry entry)
    {
        var definition = entry.Definition with
        {
            IconUnlockedUrl = AchievementIconCache.SanitizeProviderImageUrl(
                entry.Definition.IconUnlockedUrl),
            IconLockedUrl = AchievementIconCache.SanitizeProviderImageUrl(
                entry.Definition.IconLockedUrl),
        };
        if (entry.Definition.Hidden && !entry.State.Unlocked)
        {
            definition = definition with
            {
                Name = "Hidden achievement",
                Description = string.Empty,
                IconUnlockedUrl = null,
                IconLockedUrl = null,
            };
        }
        return entry with { Definition = definition };
    }

    private static GameAchievementSummary ToSummary(PersistedGameState state)
    {
        var visibleEntries = VisibleEntries(state);
        var observedUnlocked = visibleEntries.Count(row => row.State.Unlocked);
        // Provider account summaries are authoritative. Partial Steam vectors
        // are highlights, not a catalog, and must never inflate N / total.
        var total = state.ReportedTotal ?? visibleEntries.Count;
        var unlocked = state.ReportedUnlocked ?? observedUnlocked;
        if (total > 0) unlocked = Math.Min(unlocked, total);
        return new GameAchievementSummary
        {
            GameId = state.GameId,
            ProviderId = state.ProviderId,
            SourceGameId = state.SourceGameId,
            Coverage = state.Coverage,
            Total = total,
            Unlocked = unlocked,
            CompletionPercent = total > 0
                ? Math.Round(unlocked * 100d / total, 1, MidpointRounding.AwayFromZero)
                : null,
            Perfected = state.Coverage == AchievementCoverageStatus.Complete &&
                        total > 0 && unlocked >= total,
            LastUpdatedUtc = state.LastObservedAtUtc,
            Message = state.Message,
        };
    }

    private static IReadOnlyList<AchievementEntry> VisibleEntries(PersistedGameState state)
    {
        var entries = state.Entries.Values.AsEnumerable();
        if (state.Coverage == AchievementCoverageStatus.Partial)
        {
            // Files from older builds may still hold rows from earlier partial
            // responses. Expose only rows observed in the newest provider
            // response, never a union of unrelated cache generations.
            entries = entries.Where(row => row.State.ObservedAtUtc == state.LastObservedAtUtc);
        }
        return entries.ToArray();
    }

    private static bool EquivalentSnapshot(PersistedGameState previous, AchievementSnapshot current)
    {
        if (previous.Coverage != current.Coverage || previous.Capabilities != current.Capabilities ||
            previous.ReportedTotal != current.ReportedTotal || previous.ReportedUnlocked != current.ReportedUnlocked ||
            previous.Message != current.Message || previous.GameId != current.GameId)
            return false;
        // Partial responses expose only their current rows. A changed visible
        // subset must be durable even if historical entries happen to match.
        var visible = VisibleEntries(previous);
        if (visible.Count != current.Entries.Count) return false;
        foreach (var incoming in current.Entries)
        {
            if (!previous.Entries.TryGetValue(incoming.Definition.ExternalId, out var old)) return false;
            var merged = MergeCurrent(old, incoming);
            if (old.Definition != merged.Definition || old.State != (merged.State with { ObservedAtUtc = old.State.ObservedAtUtc }))
                return false;
            if (previous.Coverage == AchievementCoverageStatus.Partial && old.State.ObservedAtUtc != previous.LastObservedAtUtc)
                return false;
        }
        return true;
    }

    private static AchievementEntry MergeCurrent(
        AchievementEntry previous,
        AchievementEntry incoming)
    {
        return incoming with
        {
            State = incoming.State with
            {
                // Provider corrections must be reflected, so unlock state is
                // not monotonic; only a missing timestamp is carried forward.
                UnlockedAtUtc = incoming.State.Unlocked
                    ? incoming.State.UnlockedAtUtc ??
                      (previous.State.Unlocked ? previous.State.UnlockedAtUtc : null)
                    : null,
            },
        };
    }

    private static void ValidateSnapshot(AchievementSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.ProviderId) ||
            string.IsNullOrWhiteSpace(snapshot.SourceGameId) ||
            string.IsNullOrWhiteSpace(snapshot.CoverageKey))
            throw new InvalidDataException("Achievement snapshot identity is incomplete.");
        if (!HasPrivateCoverageKey(snapshot.ProviderId, snapshot.CoverageKey))
            throw new InvalidDataException("Achievement snapshot coverage must use a hashed account key.");
        if (snapshot.ObservedAtUtc == default ||
            snapshot.ObservedAtUtc > DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5))
            throw new InvalidDataException("Achievement snapshot timestamp is invalid.");
        if (snapshot.Entries.Count > MaxAchievements)
            throw new InvalidDataException("Achievement snapshot exceeds the safety limit.");

        if (snapshot.ReportedTotal is not int total ||
            snapshot.ReportedUnlocked is not int unlocked ||
            total < 0 || total > MaxAchievements || unlocked < 0 || unlocked > total)
            throw new InvalidDataException("Achievement snapshot totals are incomplete or contradictory.");
        if (snapshot.Entries.Count > total)
            throw new InvalidDataException("Achievement snapshot contains more rows than its reported catalog.");
        if (!snapshot.Capabilities.HasFlag(AchievementProviderCapabilities.Snapshot))
            throw new InvalidDataException("Achievement snapshot capability is missing.");

        var externalIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var observedUnlocked = 0;
        foreach (var row in snapshot.Entries)
        {
            if (string.IsNullOrWhiteSpace(row.Definition.ExternalId) ||
                row.Definition.ExternalId.Length > 512 ||
                !string.Equals(row.Definition.ExternalId, row.Definition.ExternalId.Trim(), StringComparison.Ordinal) ||
                !string.Equals(row.Definition.ExternalId, row.State.ExternalId, StringComparison.Ordinal) ||
                !string.Equals(row.Definition.ProviderId, snapshot.ProviderId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(row.Definition.SourceGameId, snapshot.SourceGameId, StringComparison.Ordinal))
                throw new InvalidDataException("Achievement snapshot contains mismatched row identity.");
            if (!externalIds.Add(row.Definition.ExternalId))
                throw new InvalidDataException("Achievement snapshot contains duplicate achievement ids.");
            if (row.State.ObservedAtUtc == default ||
                row.State.ObservedAtUtc > snapshot.ObservedAtUtc + TimeSpan.FromMinutes(5))
                throw new InvalidDataException("Achievement row timestamp is invalid.");
            if (row.State.Unlocked) observedUnlocked++;
            if (!row.State.Unlocked && row.State.UnlockedAtUtc is not null)
                throw new InvalidDataException("A locked achievement contains an unlock timestamp.");
            if (row.State.ProgressCurrent is double current &&
                (!double.IsFinite(current) || current < 0))
                throw new InvalidDataException("Achievement progress is invalid.");
            if (row.State.ProgressTarget is double target &&
                (!double.IsFinite(target) || target <= 0))
                throw new InvalidDataException("Achievement progress target is invalid.");
            if (row.State.ProgressCurrent is double progress &&
                row.State.ProgressTarget is double maximum && progress > maximum)
                throw new InvalidDataException("Achievement progress exceeds its target.");
            if (row.Definition.GlobalUnlockPercent is double rarity &&
                (!double.IsFinite(rarity) || rarity is < 0 or > 100))
                throw new InvalidDataException("Achievement rarity is invalid.");
            if (row.Definition.Points is < 0)
                throw new InvalidDataException("Achievement points are invalid.");
        }

        if (observedUnlocked > unlocked)
            throw new InvalidDataException("Achievement rows exceed the reported unlocked count.");
        if (snapshot.Coverage == AchievementCoverageStatus.Complete &&
            (snapshot.Entries.Count != total || observedUnlocked != unlocked ||
             !snapshot.Capabilities.HasFlag(AchievementProviderCapabilities.CompleteCatalog)))
            throw new InvalidDataException("Complete achievement coverage is internally contradictory.");
    }

    private static bool HasPrivateCoverageKey(string providerId, string coverageKey)
    {
        var prefix = providerId + ":";
        if (!coverageKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            coverageKey.Length != prefix.Length + 32)
            return false;
        return coverageKey.AsSpan(prefix.Length).IndexOfAnyExcept(
            "0123456789abcdefABCDEF".AsSpan()) < 0;
    }

    private static string SnapshotKey(AchievementSnapshot snapshot)
    {
        var bytes = Encoding.UTF8.GetBytes(
            snapshot.ProviderId.ToLowerInvariant() + "\0" +
            snapshot.CoverageKey + "\0" + snapshot.SourceGameId);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    internal static IAchievementProvider[] CreateDefaultProviders() =>
    [
        new EpicLegendaryAchievementProvider(),
        new SteamLibraryCacheAchievementProvider(),
        new GogGameplayAchievementProvider(),
        .. UnsupportedStoreAchievementProvider.All(),
    ];

    private IAchievementProvider? FindProvider(GameEntry game) =>
        _providers.FirstOrDefault(provider => provider.Store == game.Store && provider.Supports(game));

    private static AchievementSnapshot Unsupported(GameEntry game) => new()
    {
        GameId = game.Id,
        ProviderId = game.Store.ToString().ToLowerInvariant(),
        SourceGameId = game.LaunchTarget ?? game.Id,
        CoverageKey = "unsupported",
        Coverage = AchievementCoverageStatus.Unsupported,
        ObservedAtUtc = DateTimeOffset.UtcNow,
        Message = "Achievement sync is not available for this source.",
    };

    private static AchievementSnapshot Unavailable(
        IAchievementProvider provider,
        GameEntry game,
        string message) => new()
    {
        GameId = game.Id,
        ProviderId = provider.Id,
        SourceGameId = game.LaunchTarget ?? game.Id,
        CoverageKey = provider.Id + ":unavailable",
        Coverage = AchievementCoverageStatus.Unavailable,
        Capabilities = provider.Capabilities,
        ObservedAtUtc = DateTimeOffset.UtcNow,
        Message = message,
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetimeCts.Cancel();
        FlushDeferredState();
    }

    private readonly record struct RefreshRequestKey(string ProviderId, string GameId, string CoverageKey);
    private sealed class RefreshLane
    {
        public Task Tail { get; set; } = Task.CompletedTask;
        public Task<AchievementSnapshot>? Current { get; set; }
    }

    private sealed class PersistentState
    {
        public int Version { get; set; } = SchemaVersion;
        public Dictionary<string, PersistedGameState> Games { get; set; } =
            new(StringComparer.Ordinal);

        public void EnsureComparers()
        {
            Games = new Dictionary<string, PersistedGameState>(Games ?? [], StringComparer.Ordinal);
            foreach (var game in Games.Values) game.EnsureComparer();
        }
    }

    private sealed class PersistedGameState
    {
        public string GameId { get; set; } = string.Empty;
        public string ProviderId { get; set; } = string.Empty;
        public string SourceGameId { get; set; } = string.Empty;
        public string CoverageKey { get; set; } = string.Empty;
        public DateTimeOffset LastObservedAtUtc { get; set; }
        public int? ReportedTotal { get; set; }
        public int? ReportedUnlocked { get; set; }
        public AchievementCoverageStatus Coverage { get; set; }
        public AchievementProviderCapabilities Capabilities { get; set; }
        public string? Message { get; set; }
        public Dictionary<string, AchievementEntry> Entries { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);

        public void EnsureComparer()
        {
            // Older Steam caches changed API-name casing and v1 preserved both
            // spellings. Prefer the most recently observed row without OR-ing
            // state across generations, then use a case-insensitive key going
            // forward so the duplicate cannot return.
            var normalizedEntries = new Dictionary<string, AchievementEntry>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var pair in Entries ?? [])
            {
                var row = pair.Value;
                if (row is null || string.IsNullOrWhiteSpace(row.Definition?.ExternalId))
                    continue;
                var externalId = row.Definition.ExternalId;
                if (!normalizedEntries.TryGetValue(externalId, out var existing) ||
                    row.State.ObservedAtUtc > existing.State.ObservedAtUtc)
                    normalizedEntries[externalId] = row;
            }
            Entries = normalizedEntries;
        }
    }
}
