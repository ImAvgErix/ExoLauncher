using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ExoLauncher.Helpers;
using ExoLauncher.Models;
using ExoLauncher.Services.Achievements;

namespace ExoLauncher.Services;

/// <summary>
/// Normalizes provider snapshots, persists a baseline before notifying, and
/// polls only while an explicitly started game session is active.
/// </summary>
public sealed class AchievementService : IDisposable
{
    // The v2 state format accepts additive fields. The durable presentation
    // outbox is therefore added without discarding existing account baselines.
    private const int SchemaVersion = 2;
    private const int MaxAchievements = 10_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly IReadOnlyList<IAchievementProvider> _providers;
    private readonly string _statePath;
    private readonly TimeSpan _pollInterval;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly object _sessionGate = new();
    private readonly Dictionary<string, SessionState> _sessions =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dispatchedDeliveryIds =
        new(StringComparer.Ordinal);
    private PersistentState _state;
    private bool _disposed;

    public AchievementService()
        : this(
            CreateDefaultProviders(),
            Path.Combine(PathHelper.AppDataDir, "achievements.json"),
            TimeSpan.FromSeconds(12))
    {
    }

    internal AchievementService(
        IEnumerable<IAchievementProvider> providers,
        string statePath,
        TimeSpan pollInterval)
    {
        _providers = providers.ToArray();
        _statePath = statePath;
        _pollInterval = pollInterval <= TimeSpan.Zero ? TimeSpan.FromSeconds(12) : pollInterval;
        _state = LoadState(statePath);
    }

    public event Action<AchievementUnlock>? AchievementUnlocked;
    /// <summary>
    /// Raised only for an outbox item persisted for the currently verified
    /// provider/account snapshot. Call <see cref="AcknowledgeNotificationDelivery"/>
    /// after the presentation surface has been created.
    /// </summary>
    public event Action<AchievementNotificationDelivery>? NotificationDeliveryRequested;
    public event Action<AchievementSnapshot>? SnapshotUpdated;

    /// <summary>
    /// Returns the durable outbox for diagnostics/tests. Callers must not
    /// present these blindly: account-safe replay happens after a verified
    /// snapshot for the same provider/source/account.
    /// </summary>
    public IReadOnlyList<AchievementNotificationDelivery> GetPendingNotificationDeliveries()
    {
        lock (_stateGate)
        {
            return _state.PendingNotificationDeliveries.Values
                .OrderBy(row => row.CreatedAtUtc)
                .ThenBy(row => row.DeliveryId, StringComparer.Ordinal)
                .ToArray();
        }
    }

    /// <summary>
    /// Removes an outbox item only after the native presenter has successfully
    /// created its notification window. Returns false for an unknown/already
    /// acknowledged id.
    /// </summary>
    public bool AcknowledgeNotificationDelivery(string? deliveryId)
    {
        if (string.IsNullOrWhiteSpace(deliveryId)) return false;
        lock (_stateGate)
        {
            if (!_state.PendingNotificationDeliveries.Remove(deliveryId, out var delivery))
                return false;
            try
            {
                SaveStateAtomic();
                _dispatchedDeliveryIds.Remove(deliveryId);
                return true;
            }
            catch
            {
                // Keep the item in memory as well as on disk so a later
                // successful save/restart can retry it instead of losing it.
                _state.PendingNotificationDeliveries[deliveryId] = delivery;
                throw;
            }
        }
    }

    public AchievementCoverageInfo GetCoverage(GameEntry game)
    {
        var provider = FindProvider(game);
        if (provider is null)
        {
            return new AchievementCoverageInfo
            {
                Status = AchievementCoverageStatus.Unsupported,
                Message = "Achievement sync is not available for this source.",
            };
        }

        if (!provider.CanObserveUnlocks)
        {
            return new AchievementCoverageInfo
            {
                ProviderId = provider.Id,
                Status = AchievementCoverageStatus.Unsupported,
                Capabilities = provider.Capabilities,
                Message = string.IsNullOrWhiteSpace(provider.CoverageMessage)
                    ? "Achievement sync is not available for this source."
                    : provider.CoverageMessage,
            };
        }

        // Surface last known good coverage immediately so the detail rail is not blank.
        var latest = GetLatestSnapshot(game);
        if (latest is not null &&
            latest.Coverage is AchievementCoverageStatus.Partial or AchievementCoverageStatus.Complete)
        {
            return new AchievementCoverageInfo
            {
                ProviderId = latest.ProviderId,
                Status = latest.Coverage,
                Capabilities = latest.Capabilities,
                Message = latest.Message ?? string.Empty,
            };
        }

        return new AchievementCoverageInfo
        {
            ProviderId = provider.Id,
            Status = AchievementCoverageStatus.Unavailable,
            Capabilities = provider.Capabilities,
            Message = "Achievement coverage is available after a successful source refresh.",
        };
    }

    /// <summary>Latest persisted snapshot for the unambiguously active account and one Exo library id.</summary>
    public AchievementSnapshot? GetLatestSnapshot(string gameId)
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
            // Earlier versions persisted Steam's empty 0 / 0 local-cache
            // placeholders as valid partial coverage.
            // Do not keep displaying that stale claim; returning null lets the
            // bridge expose unavailable coverage and trigger a fresh source read.
            return state is null ? null : ToSnapshot(state);
        }
    }

    /// <summary>
    /// Latest persisted snapshot only for the account that is unambiguously
    /// active now. If account provenance cannot be resolved, fail closed.
    /// </summary>
    public AchievementSnapshot? GetLatestSnapshot(GameEntry game)
    {
        var provider = FindProvider(game);
        string? coverageKey;
        try { coverageKey = provider?.GetCurrentCoverageKey(game); }
        catch { return null; }
        if (provider is null || string.IsNullOrWhiteSpace(coverageKey)) return null;
        lock (_stateGate)
        {
            var state = _state.Games.Values
                .Where(row => string.Equals(row.GameId, game.Id, StringComparison.OrdinalIgnoreCase))
                .Where(row => string.Equals(row.ProviderId, provider.Id, StringComparison.OrdinalIgnoreCase))
                .Where(row => string.Equals(row.CoverageKey, coverageKey, StringComparison.Ordinal))
                .Where(row => !IsStaleSteamZeroPlaceholder(row))
                .Where(row => !IsUnverifiedSteamCommunitySnapshot(row))
                .OrderByDescending(row => row.LastObservedAtUtc)
                .FirstOrDefault();
            return state is null ? null : ToSnapshot(state);
        }
    }

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

    /// <summary>Newest persisted snapshots, capped so a bridge call cannot dump unbounded state.</summary>
    public IReadOnlyList<AchievementSnapshot> GetCurrentSnapshots(int limit = 100)
    {
        if (limit <= 0) return Array.Empty<AchievementSnapshot>();
        limit = Math.Min(limit, 200);
        lock (_stateGate)
        {
            return _state.Games.Values
                .Where(IsCurrentCoverage)
                .Where(state => !IsStaleSteamZeroPlaceholder(state))
                .Where(state => !IsUnverifiedSteamCommunitySnapshot(state))
                .OrderByDescending(row => row.LastObservedAtUtc)
                .ThenBy(row => row.GameId, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .Select(ToSnapshot)
                .ToArray();
        }
    }

    public async Task<AchievementSnapshot> RefreshAsync(
        GameEntry game,
        CancellationToken cancellationToken = default)
    {
        // A detail refresh proves current account data, but it does not prove
        // that Exo observed this game being played. Do not turn a delayed
        // provider sync, another device's unlock, or an account correction
        // into an on-screen toast. Session polls opt in below.
        return await RefreshCoreAsync(
            game,
            allowNotificationTransitions: IsSessionActive(game.Id),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<AchievementSnapshot> RefreshCoreAsync(
        GameEntry game,
        bool allowNotificationTransitions,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var provider = FindProvider(game);
        if (provider is null)
            return Unsupported(game);

        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        AchievementSnapshot snapshot;
        List<AchievementUnlock> unlocks = [];
        List<AchievementNotificationDelivery> deliveries = [];
        try
        {
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
                    unlocks = ApplySnapshot(game.Id, snapshot, allowNotificationTransitions);
                    deliveries = PrepareDeliveryDispatches(snapshot);
                }
                catch (InvalidDataException ex)
                {
                    // Providers are independent parsers. Keep this final gate
                    // fail-closed so one malformed cache/CLI response can never
                    // become a plausible-looking count or unlock notification.
                    AppLog.Debug($"{provider.Id} achievement consistency check failed: {ex.Message}");
                    snapshot = Unavailable(
                        provider,
                        game,
                        "Achievement data failed consistency checks and was not used.");
                }
            }
        }
        finally
        {
            _refreshGate.Release();
        }

        RaiseSnapshotUpdated(snapshot);
        foreach (var unlock in unlocks) RaiseAchievementUnlocked(unlock);
        foreach (var delivery in deliveries) RaiseNotificationDeliveryRequested(delivery);
        return snapshot;
    }

    /// <summary>Primes a baseline, then starts low-frequency polling for this game only.</summary>
    public async Task<AchievementSnapshot> BeginSessionAsync(
        GameEntry game,
        CancellationToken cancellationToken = default) =>
        await BeginSessionCoreAsync(game, activate: true, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Captures the before-handoff baseline without allowing notifications or
    /// polling until <see cref="ActivatePreparedSession"/> confirms launch.
    /// </summary>
    public async Task<AchievementSnapshot> PrepareSessionAsync(
        GameEntry game,
        CancellationToken cancellationToken = default) =>
        await BeginSessionCoreAsync(game, activate: false, cancellationToken).ConfigureAwait(false);

    private async Task<AchievementSnapshot> BeginSessionCoreAsync(
        GameEntry game,
        bool activate,
        CancellationToken cancellationToken)
    {
        await StopPollingOnlyAsync(game.Id).ConfigureAwait(false);
        // Establish the before-session account baseline without presenting any
        // historical/provider-delayed transitions as an unlock from this run.
        var initial = await RefreshCoreAsync(
            game,
            allowNotificationTransitions: false,
            cancellationToken).ConfigureAwait(false);
        if (initial.Coverage is not (AchievementCoverageStatus.Partial or AchievementCoverageStatus.Complete))
            return initial;

        var cts = new CancellationTokenSource();
        var session = new SessionState(game, cts);
        lock (_sessionGate)
        {
            if (_disposed)
            {
                cts.Dispose();
                throw new ObjectDisposedException(nameof(AchievementService));
            }
            _sessions[game.Id] = session;
            if (activate)
            {
                session.Active = true;
                session.PollTask = PollSessionAsync(session);
            }
        }
        return initial;
    }

    /// <summary>Marks a prepared baseline as a real launched session.</summary>
    public bool ActivatePreparedSession(string gameId)
    {
        if (string.IsNullOrWhiteSpace(gameId)) return false;
        lock (_sessionGate)
        {
            if (!_sessions.TryGetValue(gameId, out var session)) return false;
            if (session.Active) return true;
            session.Active = true;
            session.PollTask = PollSessionAsync(session);
            return true;
        }
    }

    /// <summary>Stops polling and performs one final source refresh.</summary>
    public async Task<AchievementSnapshot?> EndSessionAsync(
        string gameId,
        CancellationToken cancellationToken = default)
    {
        var session = await RemoveAndStopSessionAsync(gameId).ConfigureAwait(false);
        if (session is null) return null;
        try
        {
            // The final after-session sample is part of the same observed
            // launch, even though the polling registration is already gone.
            return await RefreshCoreAsync(
                session.Game,
                allowNotificationTransitions: session.Active,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            session.Cts.Dispose();
        }
    }

    /// <summary>
    /// Stops a prepared session without taking an after-snapshot. Used when a
    /// game handoff fails or is cancelled after the before-snapshot was taken.
    /// Historical/provider-delayed changes must not be attributed to a game
    /// that never actually launched.
    /// </summary>
    public async Task CancelSessionAsync(string gameId)
    {
        var session = await RemoveAndStopSessionAsync(gameId).ConfigureAwait(false);
        session?.Cts.Dispose();
    }

    private async Task PollSessionAsync(SessionState session)
    {
        var delay = PollIntervalFor(session.Game);
        try
        {
            while (true)
            {
                await Task.Delay(delay, session.Cts.Token).ConfigureAwait(false);
                var snapshot = await RefreshAsync(session.Game, session.Cts.Token).ConfigureAwait(false);
                delay = snapshot.Coverage == AchievementCoverageStatus.Unavailable
                    ? DoubleUpTo(delay, TimeSpan.FromMinutes(2))
                    : PollIntervalFor(session.Game);
            }
        }
        catch (OperationCanceledException) when (session.Cts.IsCancellationRequested)
        {
            // Expected when the tracked game exits.
        }
        catch (ObjectDisposedException) when (_disposed)
        {
            // Expected during launcher shutdown.
        }
        catch (Exception ex)
        {
            AppLog.Debug("Achievement session polling stopped: " + ex.GetType().Name);
        }
    }

    private List<AchievementUnlock> ApplySnapshot(
        string gameId,
        AchievementSnapshot snapshot,
        bool allowNotificationTransitions)
    {
        lock (_stateGate)
        {
            var key = SnapshotKey(snapshot);
            if (!_state.Games.TryGetValue(key, out var gameState))
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
            gameState.EnsureComparer();

            var unlocks = new List<AchievementUnlock>();
            var firstSeenUnlocked = new List<AchievementEntry>();
            var previousReportedUnlocked = gameState.ReportedUnlocked;
            if (snapshot.Coverage == AchievementCoverageStatus.Complete)
            {
                var currentCatalog = snapshot.Entries
                    .Select(row => row.Definition.ExternalId)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var retracted in gameState.Entries.Keys
                             .Where(externalId => !currentCatalog.Contains(externalId))
                             .ToArray())
                    gameState.Entries.Remove(retracted);
            }
            foreach (var incoming in snapshot.Entries)
            {
                var externalId = incoming.Definition.ExternalId;
                if (!gameState.Entries.TryGetValue(externalId, out var previous))
                {
                    // Providers can reveal more of a catalog over time. Treat a newly
                    // visible unlocked row as baseline unless the account summary
                    // proves that this exact single row is the new unlock.
                    gameState.Entries[externalId] = incoming;
                    if (incoming.State.Unlocked)
                        firstSeenUnlocked.Add(incoming);
                    continue;
                }

                var merged = MergeCurrent(previous, incoming);
                gameState.Entries[externalId] = merged;
                if (gameState.BaselineEstablished &&
                    !previous.State.Unlocked &&
                    incoming.State.Unlocked &&
                    allowNotificationTransitions &&
                    gameState.NotifiedUnlockedExternalIds.Add(externalId))
                {
                    var unlock = new AchievementUnlock
                    {
                        GameId = gameId,
                        Entry = merged,
                        ObservedAtUtc = snapshot.ObservedAtUtc,
                    };
                    unlocks.Add(unlock);
                    var delivery = new AchievementNotificationDelivery
                    {
                        DeliveryId = Guid.NewGuid().ToString("N"),
                        ProviderId = snapshot.ProviderId,
                        SourceGameId = snapshot.SourceGameId,
                        CoverageKey = snapshot.CoverageKey,
                        Unlock = unlock,
                        CreatedAtUtc = DateTimeOffset.UtcNow,
                    };
                    _state.PendingNotificationDeliveries[delivery.DeliveryId] = delivery;
                }
                else if (incoming.State.Unlocked)
                {
                    // This is a real provider correction, but Exo did not
                    // observe the game session that produced it. Keep the
                    // baseline current and make the achievement permanently
                    // ineligible for a duplicate historical toast.
                    gameState.NotifiedUnlockedExternalIds.Add(externalId);
                }
            }

            // Partial Steam/Epic responses may first expose an achievement after
            // it was unlocked. Most such rows are historical and must remain
            // baseline-only. There is one defensible exception: a previously
            // established account summary rose by exactly one and this response
            // contains exactly one previously unseen unlocked row. In that case
            // the source itself identifies the row responsible for the delta.
            var canIdentifyFirstSeenUnlock = allowNotificationTransitions &&
                                              gameState.BaselineEstablished &&
                                              snapshot.Coverage == AchievementCoverageStatus.Partial &&
                                              previousReportedUnlocked is >= 0 &&
                                              snapshot.ReportedUnlocked == previousReportedUnlocked + 1 &&
                                              firstSeenUnlocked.Count == 1 &&
                                              unlocks.Count == 0;
            foreach (var firstSeen in firstSeenUnlocked)
            {
                if (!canIdentifyFirstSeenUnlock ||
                    !gameState.NotifiedUnlockedExternalIds.Add(firstSeen.Definition.ExternalId))
                {
                    gameState.NotifiedUnlockedExternalIds.Add(firstSeen.Definition.ExternalId);
                    continue;
                }

                var unlock = new AchievementUnlock
                {
                    GameId = gameId,
                    Entry = firstSeen,
                    ObservedAtUtc = snapshot.ObservedAtUtc,
                };
                unlocks.Add(unlock);
                var delivery = new AchievementNotificationDelivery
                {
                    DeliveryId = Guid.NewGuid().ToString("N"),
                    ProviderId = snapshot.ProviderId,
                    SourceGameId = snapshot.SourceGameId,
                    CoverageKey = snapshot.CoverageKey,
                    Unlock = unlock,
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                };
                _state.PendingNotificationDeliveries[delivery.DeliveryId] = delivery;
            }

            var perfected = IsPerfected(snapshot);
            if (!gameState.BaselineEstablished)
            {
                gameState.BaselineEstablished = true;
                gameState.PerfectedNotified = perfected;
                foreach (var externalId in gameState.Entries
                             .Where(value => value.Value.State.Unlocked)
                             .Select(value => value.Key))
                    gameState.NotifiedUnlockedExternalIds.Add(externalId);
                unlocks.Clear();
            }
            else if (perfected && !gameState.PerfectedNotified)
            {
                gameState.PerfectedNotified = true;
                if (unlocks.Count > 0)
                {
                    var last = unlocks
                        .OrderBy(unlock => unlock.Entry.State.UnlockedAtUtc ?? DateTimeOffset.MaxValue)
                        .ThenBy(unlock => unlock.Entry.Definition.ExternalId, StringComparer.Ordinal)
                        .Last();
                    var index = unlocks.IndexOf(last);
                    var perfectedUnlock = last with { IsPerfected = true };
                    unlocks[index] = perfectedUnlock;
                    // The outbox owns the presentation payload. Keep its copy
                    // aligned with the transition so a restarted Exo does not
                    // lose the perfected treatment before it is shown.
                    var pending = _state.PendingNotificationDeliveries.Values
                        .FirstOrDefault(delivery =>
                            string.Equals(delivery.Unlock.GameId, last.GameId, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(delivery.Unlock.Entry.Definition.ExternalId,
                                last.Entry.Definition.ExternalId, StringComparison.OrdinalIgnoreCase) &&
                            delivery.Unlock.ObservedAtUtc == last.ObservedAtUtc);
                    if (pending is not null)
                        _state.PendingNotificationDeliveries[pending.DeliveryId] = pending with
                        {
                            Unlock = perfectedUnlock,
                        };
                }
            }

            gameState.GameId = gameId;
            gameState.LastObservedAtUtc = snapshot.ObservedAtUtc;
            gameState.ReportedTotal = snapshot.ReportedTotal;
            gameState.ReportedUnlocked = snapshot.ReportedUnlocked;
            gameState.Coverage = snapshot.Coverage;
            gameState.Capabilities = snapshot.Capabilities;
            gameState.Message = snapshot.Message;
            SaveStateAtomic();
            return unlocks;
        }
    }

    /// <summary>
    /// Claims matching durable deliveries for this process only after the
    /// provider has verified the same account/source snapshot. The in-memory
    /// claim prevents a twelve-second poll from queuing duplicate windows while
    /// the first request is waiting for the native presenter acknowledgement.
    /// A restart intentionally clears these claims and replays the outbox only
    /// when that account is verified again.
    /// </summary>
    private List<AchievementNotificationDelivery> PrepareDeliveryDispatches(
        AchievementSnapshot snapshot)
    {
        lock (_stateGate)
        {
            return _state.PendingNotificationDeliveries.Values
                .Where(delivery =>
                    string.Equals(delivery.ProviderId, snapshot.ProviderId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(delivery.SourceGameId, snapshot.SourceGameId, StringComparison.Ordinal) &&
                    string.Equals(delivery.CoverageKey, snapshot.CoverageKey, StringComparison.Ordinal))
                .OrderBy(delivery => delivery.CreatedAtUtc)
                .ThenBy(delivery => delivery.DeliveryId, StringComparer.Ordinal)
                .Where(delivery => _dispatchedDeliveryIds.Add(delivery.DeliveryId))
                .ToList();
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
        // are highlights, not a catalog, and historical rows retained solely
        // for notification diffs must never inflate N / total.
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
            // Partial snapshots retain older rows only as a durable transition
            // baseline. Details expose rows actually observed in the newest
            // provider response, never a union of unrelated cache generations.
            entries = entries.Where(row => row.State.ObservedAtUtc == state.LastObservedAtUtc);
        }
        return entries.ToArray();
    }

    private static AchievementEntry MergeCurrent(
        AchievementEntry previous,
        AchievementEntry incoming)
    {
        return incoming with
        {
            State = incoming.State with
            {
                // Provider corrections must be reflected in game details.
                // Notification de-duplication lives in its own durable
                // ledger instead of making the achievement state monotonic.
                UnlockedAtUtc = incoming.State.Unlocked
                    ? incoming.State.UnlockedAtUtc ??
                      (previous.State.Unlocked ? previous.State.UnlockedAtUtc : null)
                    : null,
            },
        };
    }

    private static bool IsPerfected(AchievementSnapshot snapshot)
    {
        if (snapshot.Coverage != AchievementCoverageStatus.Complete ||
            snapshot.ReportedTotal is not > 0 ||
            snapshot.Entries.Count < snapshot.ReportedTotal.Value)
            return false;
        return snapshot.Entries.Count(row => row.State.Unlocked) >= snapshot.ReportedTotal.Value;
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

    /// <summary>
    /// Production uses a 12s default. Providers may go faster (Steam Web API
    /// at 8s, floored at 5s) or slower (Epic CLI at 20s, capped at 2 minutes).
    /// Tests inject a non-12s ctor interval and must keep it.
    /// </summary>
    internal TimeSpan PollIntervalFor(GameEntry game)
    {
        if (_pollInterval != TimeSpan.FromSeconds(12))
            return _pollInterval <= TimeSpan.Zero ? TimeSpan.FromSeconds(12) : _pollInterval;

        var hinted = FindProvider(game)?.SuggestedPollInterval ?? _pollInterval;
        if (hinted < TimeSpan.FromSeconds(5)) return TimeSpan.FromSeconds(5);
        if (hinted > TimeSpan.FromMinutes(2)) return TimeSpan.FromMinutes(2);
        return hinted;
    }

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

    private void RaiseAchievementUnlocked(AchievementUnlock unlock)
    {
        var handlers = AchievementUnlocked?.GetInvocationList();
        if (handlers is null) return;
        foreach (var handler in handlers)
        {
            try { ((Action<AchievementUnlock>)handler)(unlock); }
            catch (Exception ex) { AppLog.Debug("Achievement listener failed: " + ex.GetType().Name); }
        }
    }

    private void RaiseNotificationDeliveryRequested(AchievementNotificationDelivery delivery)
    {
        var handlers = NotificationDeliveryRequested?.GetInvocationList();
        if (handlers is null) return;
        foreach (var handler in handlers)
        {
            try { ((Action<AchievementNotificationDelivery>)handler)(delivery); }
            catch (Exception ex) { AppLog.Debug("Achievement delivery listener failed: " + ex.GetType().Name); }
        }
    }

    private void RaiseSnapshotUpdated(AchievementSnapshot snapshot)
    {
        var handlers = SnapshotUpdated?.GetInvocationList();
        if (handlers is null) return;
        foreach (var handler in handlers)
        {
            try { ((Action<AchievementSnapshot>)handler)(snapshot); }
            catch (Exception ex) { AppLog.Debug("Achievement snapshot listener failed: " + ex.GetType().Name); }
        }
    }

    private async Task StopPollingOnlyAsync(string gameId)
    {
        var session = await RemoveAndStopSessionAsync(gameId).ConfigureAwait(false);
        session?.Cts.Dispose();
    }

    private async Task<SessionState?> RemoveAndStopSessionAsync(string gameId)
    {
        SessionState? session;
        lock (_sessionGate)
        {
            if (!_sessions.Remove(gameId, out session)) return null;
            session.Cts.Cancel();
        }
        try { await session.PollTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { /* expected */ }
        return session;
    }

    private bool IsSessionActive(string gameId)
    {
        if (string.IsNullOrWhiteSpace(gameId)) return false;
        lock (_sessionGate)
            return _sessions.TryGetValue(gameId, out var session) && session.Active;
    }

    private PersistentState LoadState(string path)
    {
        try
        {
            if (!File.Exists(path)) return new PersistentState();
            var info = new FileInfo(path);
            if (info.Length is <= 0 or > 32 * 1024 * 1024) return new PersistentState();
            var state = JsonSerializer.Deserialize<PersistentState>(File.ReadAllText(path), JsonOptions);
            if (state is null || state.Version is < 1 or > SchemaVersion) return new PersistentState();
            // v2 added a durable presentation outbox only. Keep v1 account
            // baselines intact and start it with an empty outbox instead of
            // re-baselining every existing user after an update.
            if (state.Version == 1)
            {
                state.Version = SchemaVersion;
                state.PendingNotificationDeliveries = new Dictionary<string, AchievementNotificationDelivery>(StringComparer.Ordinal);
            }
            state.EnsureComparers();
            foreach (var pair in state.Games.ToArray())
            {
                try
                {
                    var row = pair.Value;
                    if (string.IsNullOrWhiteSpace(row.GameId) ||
                        row.LastObservedAtUtc == default)
                        throw new InvalidDataException("Persisted achievement identity is incomplete.");
                    ValidateSnapshot(ToSnapshot(row));
                }
                catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
                {
                    // Syntactically valid JSON can still contain old unions,
                    // duplicate IDs, future timestamps, or impossible totals.
                    // Drop only that baseline and re-establish it from the
                    // current provider/account; never surface logical corruption.
                    state.Games.Remove(pair.Key);
                    AppLog.Debug("Discarded invalid achievement baseline: " + ex.Message);
                }
            }
            return state;
        }
        catch (Exception ex)
        {
            // A corrupt state safely re-baselines; it never emits historical unlocks.
            AppLog.Debug("Achievement state load failed: " + ex.GetType().Name);
            return new PersistentState();
        }
    }

    private void SaveStateAtomic()
    {
        var directory = Path.GetDirectoryName(_statePath);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("Achievement state path has no directory.");
        Directory.CreateDirectory(directory);
        var temp = _statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var json = JsonSerializer.Serialize(_state, JsonOptions);
            using (var stream = new FileStream(
                       temp,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 64 * 1024,
                       FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, _statePath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best-effort */ }
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SessionState[] sessions;
        lock (_sessionGate)
        {
            sessions = _sessions.Values.ToArray();
            _sessions.Clear();
            foreach (var session in sessions) session.Cts.Cancel();
        }
        foreach (var session in sessions) session.Cts.Dispose();
    }

    private static TimeSpan DoubleUpTo(TimeSpan value, TimeSpan maximum) =>
        value >= maximum || value.Ticks > maximum.Ticks / 2
            ? maximum
            : TimeSpan.FromTicks(value.Ticks * 2);

    private sealed class SessionState(GameEntry game, CancellationTokenSource cts)
    {
        public GameEntry Game { get; } = game;
        public CancellationTokenSource Cts { get; } = cts;
        public Task PollTask { get; set; } = Task.CompletedTask;
        public bool Active { get; set; }
    }

    private sealed class PersistentState
    {
        public int Version { get; set; } = SchemaVersion;
        public Dictionary<string, PersistedGameState> Games { get; set; } =
            new(StringComparer.Ordinal);
        public Dictionary<string, AchievementNotificationDelivery> PendingNotificationDeliveries { get; set; } =
            new(StringComparer.Ordinal);

        public void EnsureComparers()
        {
            Games = new Dictionary<string, PersistedGameState>(Games, StringComparer.Ordinal);
            foreach (var game in Games.Values) game.EnsureComparer();
            var normalizedDeliveries = new Dictionary<string, AchievementNotificationDelivery>(StringComparer.Ordinal);
            foreach (var pair in PendingNotificationDeliveries ?? [])
            {
                var delivery = pair.Value;
                if (delivery is null || string.IsNullOrWhiteSpace(delivery.DeliveryId) ||
                    string.IsNullOrWhiteSpace(delivery.ProviderId) ||
                    string.IsNullOrWhiteSpace(delivery.SourceGameId) ||
                    !HasPrivateCoverageKey(delivery.ProviderId, delivery.CoverageKey) ||
                    delivery.Unlock is null || string.IsNullOrWhiteSpace(delivery.Unlock.GameId) ||
                    string.IsNullOrWhiteSpace(delivery.Unlock.Entry?.Definition?.ExternalId) ||
                    !string.Equals(delivery.ProviderId, delivery.Unlock.Entry.Definition.ProviderId,
                        StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(delivery.SourceGameId, delivery.Unlock.Entry.Definition.SourceGameId,
                        StringComparison.Ordinal) ||
                    !string.Equals(delivery.DeliveryId, pair.Key, StringComparison.Ordinal) ||
                    !Guid.TryParseExact(delivery.DeliveryId, "N", out _) ||
                    delivery.CreatedAtUtc == default ||
                    delivery.CreatedAtUtc > DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5))
                    continue;
                normalizedDeliveries[delivery.DeliveryId] = delivery;
            }
            PendingNotificationDeliveries = normalizedDeliveries;
        }
    }

    private sealed class PersistedGameState
    {
        public string GameId { get; set; } = string.Empty;
        public string ProviderId { get; set; } = string.Empty;
        public string SourceGameId { get; set; } = string.Empty;
        public string CoverageKey { get; set; } = string.Empty;
        public bool BaselineEstablished { get; set; }
        public bool PerfectedNotified { get; set; }
        public DateTimeOffset LastObservedAtUtc { get; set; }
        public int? ReportedTotal { get; set; }
        public int? ReportedUnlocked { get; set; }
        public AchievementCoverageStatus Coverage { get; set; }
        public AchievementProviderCapabilities Capabilities { get; set; }
        public string? Message { get; set; }
        public Dictionary<string, AchievementEntry> Entries { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> NotifiedUnlockedExternalIds { get; set; } =
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
            NotifiedUnlockedExternalIds = new HashSet<string>(
                NotifiedUnlockedExternalIds ?? [],
                StringComparer.OrdinalIgnoreCase);
            // Files written before the notification ledger existed already used
            // monotonic unlocks. Seed those rows so upgrading cannot replay them.
            if (BaselineEstablished && NotifiedUnlockedExternalIds.Count == 0)
            {
                foreach (var externalId in Entries
                             .Where(value => value.Value.State.Unlocked)
                             .Select(value => value.Key))
                    NotifiedUnlockedExternalIds.Add(externalId);
            }
        }
    }
}
