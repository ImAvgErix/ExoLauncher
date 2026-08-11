namespace ExoLauncher.Models;

public enum StoreKind
{
    Local,
    Steam,
    Epic,
    Gog,
    Riot,
    Xbox,
    Ea,
    Ubisoft,
    BattleNet,
    Amazon
}

/// <summary>Canonical game model shared by adapters and the UI.</summary>
public sealed class GameEntry
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required StoreKind Store { get; init; }
    public bool Installed { get; init; }
    public bool Owned { get; init; } = true;
    public bool UpdateAvailable { get; init; }
    public bool CanInstall { get; init; }
    public string? Path { get; init; }
    public string? CoverUrl { get; init; }
    /// <summary>Verified cover provenance: steam | epic | gog | riot | local.</summary>
    public string? CoverSource { get; init; }
    /// <summary>Total playtime in minutes when known.</summary>
    public int? PlaytimeMinutes { get; init; }
    /// <summary>Install size in bytes when known.</summary>
    public long? SizeBytes { get; init; }
    public string Status { get; init; } = "Ready";
    public IReadOnlyList<string> Deps { get; init; } = Array.Empty<string>();
    /// <summary>Honest one-line note about what launch/install actually does for this store.</summary>
    public string LaunchNote { get; init; } = string.Empty;
    /// <summary>Store-specific launch target (app id, product id, exe path).</summary>
    public string? LaunchTarget { get; init; }
    /// <summary>UTC last played when known (user prefs or store metadata).</summary>
    public DateTimeOffset? LastPlayedUtc { get; init; }
    /// <summary>User-pinned favorite (from settings overlay).</summary>
    public bool IsFavorite { get; init; }
    /// <summary>Primary action the UI should offer: play | install | update | none.</summary>
    public string PrimaryAction
    {
        get
        {
            if (string.Equals(Id, "local:add", StringComparison.OrdinalIgnoreCase))
                return "install";
            if (Installed && UpdateAvailable) return "update";
            if (Installed) return "play";
            if (CanInstall || Owned) return "install";
            return "none";
        }
    }
}

public sealed class DependencyInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Status { get; init; }
    public string Detail { get; init; } = string.Empty;
    public bool CanOfferInstall { get; init; }
    public string? OfficialUrl { get; init; }
}

public sealed class LaunchResult
{
    public bool Ok { get; init; }
    public string Message { get; init; } = string.Empty;
    public int? ProcessId { get; init; }
    public string? BackendStarted { get; init; }
    /// <summary>When true, agent opened but title launch is not fully wired (phase-2 honesty).</summary>
    public bool HandoffOnly { get; init; }
    /// <summary>Missing runtimes — UI should prompt Install (consent) before retrying with skipDeps.</summary>
    public bool NeedsDependencies { get; init; }
    public IReadOnlyList<DependencyInfo> MissingDependencies { get; init; } = Array.Empty<DependencyInfo>();
}

public sealed class InstallResult
{
    public bool Ok { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? Path { get; init; }
    public bool HandoffOnly { get; init; }
    public bool NeedsDependencies { get; init; }
    public IReadOnlyList<DependencyInfo> MissingDependencies { get; init; } = Array.Empty<DependencyInfo>();
}

public enum InstallPhase
{
    Idle,
    Preparing,
    Downloading,
    Installing,
    Finalizing,
    Completed,
    Failed,
    Cancelled
}

public sealed class InstallProgress
{
    public string GameId { get; init; } = string.Empty;
    public InstallPhase Phase { get; init; } = InstallPhase.Idle;
    public double? Percent { get; init; }
    /// <summary>Bytes per second when known.</summary>
    public double? BytesPerSecond { get; init; }
    public string Status { get; init; } = string.Empty;
    public bool CanCancel { get; init; } = true;
    public bool IsActive => Phase is InstallPhase.Preparing or InstallPhase.Downloading
        or InstallPhase.Installing or InstallPhase.Finalizing;
}

public sealed class AppSettings
{
    /// <summary>Always on — not exposed in Settings UI.</summary>
    public bool CloseStoreClientsAfterLaunch { get; set; } = true;
    /// <summary>Always on — offers official redistributable installers with consent.</summary>
    /// <summary>Always on — prompt for missing redistributables on launch/install.</summary>
    public bool AutoInstallRedistributables { get; set; } = true;
    /// <summary>Always on — not exposed in Settings.</summary>
    public bool MinimizeWhilePlaying { get; set; } = true;
    /// <summary>Always true. Not a user toggle.</summary>
    public bool AntiCheatSafeMode { get; set; } = true;
    public string AppVersion { get; set; } = "1.0.0";
    /// <summary>Optional override; null = auto <c>%LOCALAPPDATA%\ExoLauncher\Games</c>.</summary>
    public string? DefaultInstallRoot { get; set; }
    /// <summary>Legacy field; Local portable path removed — always false.</summary>
    public bool CopyPortableIntoLibrary { get; set; } = false;
    /// <summary>Always false — fixed 1400×900 shell.</summary>
    public bool AllowResize { get; set; } = false;
    /// <summary>Always true — in-app update checks on start.</summary>
    public bool CheckForUpdates { get; set; } = true;
    /// <summary>Library sort: name | recent | size | store.</summary>
    public string SortMode { get; set; } = "name";
    /// <summary>Pinned game ids.</summary>
    public List<string> Favorites { get; set; } = new();
    /// <summary>Recently launched game ids (newest first, capped).</summary>
    public List<string> Recent { get; set; } = new();
    /// <summary>Last played timestamps by game id (ISO).</summary>
    public Dictionary<string, string> LastPlayed { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>True after the first-run launcher setup flow.</summary>
    public bool OnboardingComplete { get; set; }
    /// <summary>Show session-bound achievement notifications when a provider reports a new unlock.</summary>
    public bool TrophyNotificationsEnabled { get; set; } = true;
    /// <summary>Legacy visual field retained for settings-file compatibility. Exo has one notification design.</summary>
    public string TrophyNotificationPreset { get; set; } = "exo";
    /// <summary>Canonical work-area anchor: top/center/bottom by left/center/right.</summary>
    public string TrophyNotificationPosition { get; set; } = "bottom-right";
    /// <summary>Canonical horizontal anchor: 0 (left), .5 (center), or 1 (right).</summary>
    public double TrophyNotificationPositionX { get; set; } = 1d;
    /// <summary>Canonical vertical anchor: 0 (top), .5 (center), or 1 (bottom).</summary>
    public double TrophyNotificationPositionY { get; set; } = 1d;
    /// <summary>Legacy compatibility field; the product duration is fixed at 3.5 seconds.</summary>
    public int TrophyNotificationDurationSeconds { get; set; } = 5;
    /// <summary>Legacy sound switch retained for settings-file and bridge compatibility.</summary>
    public bool TrophyNotificationSound { get; set; } = true;
    /// <summary>Legacy compatibility field; enabled notifications always use the authored Exo cue.</summary>
    public string TrophyNotificationSoundCue { get; set; } = "exo";
}

/// <summary>Result row from cross-store live search (Store tab).</summary>
public sealed class StoreSearchHit
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required StoreKind Store { get; init; }
    public string? LaunchTarget { get; init; }
    public string? CoverUrl { get; init; }
    public string? CoverSource { get; init; }
    public bool Owned { get; init; }
    public bool Installed { get; init; }
    public bool CanInstall { get; init; }
    public string Source { get; init; } = "";
}
