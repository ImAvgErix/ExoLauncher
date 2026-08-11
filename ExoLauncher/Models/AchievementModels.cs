namespace ExoLauncher.Models;

[Flags]
public enum AchievementProviderCapabilities
{
    None = 0,
    Snapshot = 1 << 0,
    Progress = 1 << 1,
    Rarity = 1 << 2,
    CompleteCatalog = 1 << 3,
}

public enum AchievementCoverageStatus
{
    Unsupported,
    Unavailable,
    Partial,
    Complete,
}

/// <summary>Provider-owned achievement metadata normalized for local storage.</summary>
public sealed record AchievementDefinition
{
    public required string ProviderId { get; init; }
    public required string SourceGameId { get; init; }
    public required string ExternalId { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = string.Empty;
    public bool Hidden { get; init; }
    public string? IconUnlockedUrl { get; init; }
    public string? IconLockedUrl { get; init; }
    public double? GlobalUnlockPercent { get; init; }
    public int? Points { get; init; }
    public string? Tier { get; init; }
}

/// <summary>Account-scoped state observed for one achievement.</summary>
public sealed record AchievementState
{
    public required string ExternalId { get; init; }
    public bool Unlocked { get; init; }
    public DateTimeOffset? UnlockedAtUtc { get; init; }
    public double? ProgressCurrent { get; init; }
    public double? ProgressTarget { get; init; }
    public DateTimeOffset ObservedAtUtc { get; init; }
}

public sealed record AchievementEntry
{
    public required AchievementDefinition Definition { get; init; }
    public required AchievementState State { get; init; }
}

/// <summary>
/// A provider snapshot. Coverage is explicit: callers must never present a partial
/// local cache as the title's complete achievement catalog.
/// </summary>
public sealed record AchievementSnapshot
{
    /// <summary>Current Exo library id. Providers may leave this null; the service fills it.</summary>
    public string? GameId { get; init; }
    public required string ProviderId { get; init; }
    public required string SourceGameId { get; init; }
    /// <summary>Contains only provider name plus a one-way account hash.</summary>
    public required string CoverageKey { get; init; }
    public AchievementCoverageStatus Coverage { get; init; }
    public AchievementProviderCapabilities Capabilities { get; init; }
    public int? ReportedTotal { get; init; }
    public int? ReportedUnlocked { get; init; }
    public DateTimeOffset ObservedAtUtc { get; init; }
    public IReadOnlyList<AchievementEntry> Entries { get; init; } = Array.Empty<AchievementEntry>();
    public string? Message { get; init; }
}

/// <summary>Bridge-safe aggregate. Account coverage provenance is intentionally omitted.</summary>
public sealed record GameAchievementSummary
{
    public required string GameId { get; init; }
    public required string ProviderId { get; init; }
    public required string SourceGameId { get; init; }
    public AchievementCoverageStatus Coverage { get; init; }
    public int Total { get; init; }
    public int Unlocked { get; init; }
    public double? CompletionPercent { get; init; }
    public bool Perfected { get; init; }
    public DateTimeOffset LastUpdatedUtc { get; init; }
    public string? Message { get; init; }
}

public sealed record AchievementCoverageInfo
{
    public string? ProviderId { get; init; }
    public AchievementCoverageStatus Status { get; init; }
    public AchievementProviderCapabilities Capabilities { get; init; }
    public string Message { get; init; } = string.Empty;
}

/// <summary>A locked-to-unlocked transition safe to present exactly once.</summary>
public sealed record AchievementUnlock
{
    public required string GameId { get; init; }
    public required AchievementEntry Entry { get; init; }
    public bool IsPerfected { get; init; }
    public DateTimeOffset ObservedAtUtc { get; init; }
}

/// <summary>
/// A durable, account-scoped notification delivery. The unlock transition and
/// its presentation acknowledgement are intentionally separate: a process
/// exit after detection must not silently lose the player's notification.
/// </summary>
public sealed record AchievementNotificationDelivery
{
    public required string DeliveryId { get; init; }
    public required string ProviderId { get; init; }
    public required string SourceGameId { get; init; }
    /// <summary>One-way account identifier; never a raw account id.</summary>
    public required string CoverageKey { get; init; }
    public required AchievementUnlock Unlock { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
}
