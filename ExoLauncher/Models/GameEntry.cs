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
    Amazon,
    Rockstar,
    Itch,
    Minecraft,
    Roblox,
    Paradox,
    Wargaming
}

/// <summary>
/// Current-account entitlement truth. Unknown preserves legacy adapters that
/// have not established this boundary; Unverified and NotOwned are explicit
/// states and must never fall through to Play/Update/Install.
/// </summary>
public enum EntitlementState
{
    Unknown,
    Owned,
    Unverified,
    NotOwned,
}

/// <summary>Canonical game model shared by adapters and the UI.</summary>
public sealed class GameEntry
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required StoreKind Store { get; init; }
    public bool Installed { get; init; }
    public bool Owned { get; init; } = true;
    public EntitlementState EntitlementState { get; init; } = EntitlementState.Unknown;
    public bool UpdateAvailable { get; init; }
    public bool CanInstall { get; init; }
    public string? Path { get; init; }
    public string? CoverUrl { get; init; }
    /// <summary>Verified cover provenance: steam | epic | gog | riot | local.</summary>
    public string? CoverSource { get; init; }
    /// <summary>
    /// Process-local cache generation for this visual card. It is not synced or
    /// persisted; the WebView uses it only to stop reusing stale decoded art.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public long ArtRevision { get; init; }
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
    /// <summary>
    /// Stable, display-only key used to group exact title matches from different
    /// stores. It is never an account identifier and must not be used for a
    /// launch, install, achievement, or playtime lookup.
    /// </summary>
    public string? CanonicalTitleKey { get; init; }
    /// <summary>
    /// The exact store entry currently projected into this backwards-compatible
    /// top-level row. It always equals <see cref="Id"/> for grouped cards.
    /// </summary>
    public string? SelectedVariantId { get; init; }
    /// <summary>
    /// Other exact store entries represented by this visual card. These entries
    /// are presentation metadata only; callers must resolve their id through
    /// the library before performing a store action.
    /// </summary>
    public IReadOnlyList<GameVariant> Variants { get; init; } = Array.Empty<GameVariant>();
    /// <summary>Primary action the UI should offer: play | install | update | none.</summary>
    public string PrimaryAction
    {
        get
        {
            if (string.Equals(Id, "local:add", StringComparison.OrdinalIgnoreCase))
                return "install";
            if (EntitlementState is EntitlementState.Unverified or EntitlementState.NotOwned)
                return "none";
            if (Installed && UpdateAvailable) return "update";
            if (Installed) return "play";
            if (CanInstall || Owned) return "install";
            return "none";
        }
    }
}

/// <summary>
/// A store-specific source behind a canonical library card. Every id, launch
/// target, install path, and state remains source-specific so cross-store cards
/// never turn into a synthetic game that an adapter cannot safely act on.
/// </summary>
public sealed record GameVariant
{
    public required string Id { get; init; }
    public required StoreKind Store { get; init; }
    public bool Installed { get; init; }
    public bool Owned { get; init; }
    public EntitlementState EntitlementState { get; init; } = EntitlementState.Unknown;
    public bool UpdateAvailable { get; init; }
    public bool CanInstall { get; init; }
    public string? Path { get; init; }
    public string? LaunchTarget { get; init; }
    public int? PlaytimeMinutes { get; init; }
    public DateTimeOffset? LastPlayedUtc { get; init; }
    public string Status { get; init; } = "Ready";
    public string PrimaryAction =>
        EntitlementState is EntitlementState.Unverified or EntitlementState.NotOwned ? "none" :
        Installed && UpdateAvailable ? "update" :
        Installed ? "play" :
        CanInstall || Owned ? "install" : "none";

    internal static GameVariant FromGame(GameEntry game) => new()
    {
        Id = game.Id,
        Store = game.Store,
        Installed = game.Installed,
        Owned = game.Owned,
        EntitlementState = game.EntitlementState,
        UpdateAvailable = game.UpdateAvailable,
        CanInstall = game.CanInstall,
        Path = game.Path,
        LaunchTarget = game.LaunchTarget,
        PlaytimeMinutes = game.PlaytimeMinutes,
        LastPlayedUtc = game.LastPlayedUtc,
        Status = game.Status,
    };

    internal GameEntry ToGameEntry(GameEntry card) => new()
    {
        Id = Id,
        Title = card.Title,
        Store = Store,
        Installed = Installed,
        Owned = Owned,
        EntitlementState = EntitlementState,
        UpdateAvailable = UpdateAvailable,
        CanInstall = CanInstall,
        Path = Path,
        CoverUrl = card.CoverUrl,
        CoverSource = card.CoverSource,
        ArtRevision = card.ArtRevision,
        PlaytimeMinutes = PlaytimeMinutes,
        SizeBytes = card.SizeBytes,
        Status = Status,
        Deps = card.Deps,
        LaunchNote = card.LaunchNote,
        LaunchTarget = LaunchTarget,
        LastPlayedUtc = LastPlayedUtc,
        // Preserve the canonical card's pin while variants are expanded for a
        // refresh. The settings overlay can still add the exact source pin.
        IsFavorite = card.IsFavorite,
        CanonicalTitleKey = card.CanonicalTitleKey,
        SelectedVariantId = Id,
    };
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

public sealed record InstallResult
{
    public bool Ok { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? Path { get; init; }
    public bool HandoffOnly { get; init; }
    public bool Queued { get; init; }
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
    /// <summary>Bytes copied this job, when the store reports them. 0 is not a reading.</summary>
    public long? BytesDownloaded { get; init; }
    /// <summary>Total bytes this job, when the store reports them.</summary>
    public long? BytesToDownload { get; init; }
    /// <summary>Bytes per second when known.</summary>
    public double? BytesPerSecond { get; init; }
    public string Status { get; init; } = string.Empty;
    public bool CanCancel { get; init; } = true;
    public bool IsActive => Phase is InstallPhase.Preparing or InstallPhase.Downloading
        or InstallPhase.Installing or InstallPhase.Finalizing;
}

public sealed class AppSettings
{
    /// <summary>Always on. Not in Settings.</summary>
    public bool CloseStoreClientsAfterLaunch { get; set; } = true;
    /// <summary>Always on. Prompt for missing redistributables on launch or install.</summary>
    public bool AutoInstallRedistributables { get; set; } = true;
    /// <summary>Always on. Not in Settings.</summary>
    public bool MinimizeWhilePlaying { get; set; } = true;
    /// <summary>Always true. Not a user toggle.</summary>
    public bool AntiCheatSafeMode { get; set; } = true;
    public string AppVersion { get; set; } = "1.0.0";
    /// <summary>Optional override; null = auto <c>%LOCALAPPDATA%\ExoLauncher\Games</c>.</summary>
    public string? DefaultInstallRoot { get; set; }
    /// <summary>Legacy field; Local portable path removed — always false.</summary>
    public bool CopyPortableIntoLibrary { get; set; } = false;
    /// <summary>Unused. The window is always resizable.</summary>
    public bool AllowResize { get; set; } = false;
    /// <summary>Always true — in-app update checks on start.</summary>
    public bool CheckForUpdates { get; set; } = true;
    /// <summary>Library sort: name | recent | size | store.</summary>
    public string SortMode { get; set; } = "name";
    /// <summary>Pinned game ids.</summary>
    public List<string> Favorites { get; set; } = new();
    /// <summary>Recently launched game ids (newest first, capped).</summary>
    public List<string> Recent { get; set; } = new();
    /// <summary>Game ids the user pinned to their profile shelf (max ten), in shelf order.</summary>
    public List<string> ProfileShowcase { get; set; } = new();
    /// <summary>Exo profile display name. Authored by the user — never a store persona.</summary>
    public string? ProfileName { get; set; }
    /// <summary>Exo handle the user typed: lowercase letters, digits, underscore.</summary>
    public string? ProfileHandle { get; set; }
    public string? ProfilePronouns { get; set; }
    /// <summary>One-line status the user wrote. Not presence.</summary>
    public string? ProfileStatusText { get; set; }
    public string? ProfileBio { get; set; }
    /// <summary>Library id whose cover stands in for an avatar.</summary>
    public string? ProfileAvatarGameId { get; set; }
    /// <summary>Library id whose store art fills the profile banner.</summary>
    public string? ProfileBannerGameId { get; set; }
    /// <summary>File name of the avatar the user uploaded, inside Exo's own cover cache. Never a path.</summary>
    public string? ProfileAvatarImage { get; set; }
    /// <summary>File name of the uploaded banner, inside Exo's own cover cache. Never a path.</summary>
    public string? ProfileBannerImage { get; set; }
    /// <summary>Up to six local profile-gallery media file names. Never paths.</summary>
    public Dictionary<string, string> ProfileGalleryImages { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>One of the fixed Exo accent keys.</summary>
    public string ProfileAccent { get; set; } = "ash";
    /// <summary>Profile head alignment: left | center. Fresh installs start centred.</summary>
    public string ProfileLayout { get; set; } = "center";
    /// <summary>Banner size: short | standard | tall.</summary>
    public string ProfileBannerHeight { get; set; } = "standard";
    /// <summary>Showcase presentation: grid | rows.</summary>
    public string ProfileShowcaseStyle { get; set; } = "grid";
    /// <summary>Whether the authored Exo handle is printed on the profile header.</summary>
    public bool ProfileShowHandle { get; set; } = true;
    /// <summary>Profile section keys in the order the user arranged them.</summary>
    public List<string> ProfileSections { get; set; } = new();
    /// <summary>Profile section keys the user turned off.</summary>
    public List<string> ProfileHiddenSections { get; set; } = new();
    /// <summary>People the user added on Exo by hand. Local list, not a social graph.</summary>
    public List<ProfilePerson> ProfileRoster { get; set; } = new();
    /// <summary>Last played timestamps by game id (ISO).</summary>
    public Dictionary<string, string> LastPlayed { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>True after the first-run launcher setup flow.</summary>
    public bool OnboardingComplete { get; set; }
    /// <summary>Show session-bound achievement notifications when a provider reports a new unlock.</summary>
    public bool TrophyNotificationsEnabled { get; set; } = true;
    /// <summary>Legacy visual field retained for settings-file compatibility. Exo has one notification design.</summary>
    public string TrophyNotificationPreset { get; set; } = "exo";
    /// <summary>Canonical work-area anchor: top/center/bottom by left/center/right.</summary>
    public string TrophyNotificationPosition { get; set; } = "top-right";
    /// <summary>Canonical horizontal anchor: 0 (left), .5 (center), or 1 (right).</summary>
    public double TrophyNotificationPositionX { get; set; } = 1d;
    /// <summary>Canonical vertical anchor: 0 (top), .5 (center), or 1 (bottom).</summary>
    public double TrophyNotificationPositionY { get; set; } = 0d;
    /// <summary>Legacy compatibility field; the product duration is fixed at 3.5 seconds.</summary>
    public int TrophyNotificationDurationSeconds { get; set; } = 5;
    /// <summary>Legacy sound switch retained for settings-file and bridge compatibility.</summary>
    public bool TrophyNotificationSound { get; set; } = true;
    /// <summary>Legacy compatibility field; enabled notifications always use the authored Exo cue.</summary>
    public string TrophyNotificationSoundCue { get; set; } = "exo";
    /// <summary>Per-game launch extras (args / cwd / admin). Never applied to anti-cheat titles.</summary>
    public Dictionary<string, GameLaunchOverride> LaunchOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// Exo-owned custom cover file names keyed by exact library source id.
    /// Device-local by design; account sync never reads this dictionary.
    /// </summary>
    public Dictionary<string, string> CustomCoverImages { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// One person the user added on Exo. There is no directory to look them up in
/// and no server to ask, so the handle is whatever the user typed.
/// </summary>
public sealed class ProfilePerson
{
    public string Handle { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? Note { get; set; }
    /// <summary>When the user added them (ISO), for ordering only.</summary>
    public string? AddedUtc { get; set; }
}

/// <summary>User-supplied launch extras for one library id.</summary>
public sealed class GameLaunchOverride
{
    public string? ExtraArgs { get; set; }
    public string? WorkingDirectory { get; set; }
    public bool RunAsAdmin { get; set; }

    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(ExtraArgs) &&
        string.IsNullOrWhiteSpace(WorkingDirectory) &&
        !RunAsAdmin;
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
