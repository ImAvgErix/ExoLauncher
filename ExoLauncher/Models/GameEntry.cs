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
    /// <summary>Steam Family / borrowed license. Playable through Steam, not in GetOwnedGames.</summary>
    public bool FamilyShared { get; init; }
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
    /// <summary>
    /// Steam parent app id when this row is DLC, a soundtrack, or another
    /// child. Empty means the row is its own parent for library dedup.
    /// </summary>
    public string? ParentAppId { get; init; }
    /// <summary>Steam appinfo <c>common/type</c> when known. Empty on ACF-only rows.</summary>
    public string? SteamAppType { get; init; }
    /// <summary>UTC last played when known (user prefs or store metadata).</summary>
    public DateTimeOffset? LastPlayedUtc { get; init; }
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

    /// <summary>
    /// Copy of every property with new playtime readings. Hand-written clones
    /// dropped fields (ParentAppId broke store-title dedup); add new properties here.
    /// </summary>
    internal GameEntry WithPlaytime(int? playtimeMinutes, DateTimeOffset? lastPlayedUtc) => new()
    {
        Id = Id,
        Title = Title,
        Store = Store,
        Installed = Installed,
        Owned = Owned,
        FamilyShared = FamilyShared,
        EntitlementState = EntitlementState,
        UpdateAvailable = UpdateAvailable,
        CanInstall = CanInstall,
        Path = Path,
        CoverUrl = CoverUrl,
        CoverSource = CoverSource,
        ArtRevision = ArtRevision,
        PlaytimeMinutes = playtimeMinutes,
        SizeBytes = SizeBytes,
        Status = Status,
        Deps = Deps,
        LaunchNote = LaunchNote,
        LaunchTarget = LaunchTarget,
        ParentAppId = ParentAppId,
        SteamAppType = SteamAppType,
        LastPlayedUtc = lastPlayedUtc,
        CanonicalTitleKey = CanonicalTitleKey,
        SelectedVariantId = SelectedVariantId,
        Variants = Variants,
    };

    /// <summary>Primary action the UI should offer: play | install | update | none.</summary>
    public string PrimaryAction
    {
        get
        {
            if (string.Equals(Id, "local:add", StringComparison.OrdinalIgnoreCase))
                return "install";
            if (EntitlementState == EntitlementState.NotOwned)
                return "none";
            if (EntitlementState == EntitlementState.Unverified && !Installed)
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
        EntitlementState == EntitlementState.NotOwned ? "none" :
        EntitlementState == EntitlementState.Unverified && !Installed ? "none" :
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
    /// <summary>A folder this install created and left unfinished: removed on Cancel, kept on Pause so it can resume.</summary>
    public string? PartialPath { get; init; }
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
    private static long _sequence;
    [System.Text.Json.Serialization.JsonIgnore]
    internal long Sequence { get; init; } = Interlocked.Increment(ref _sequence);
    public string GameId { get; init; } = string.Empty;
    public InstallPhase Phase { get; init; } = InstallPhase.Idle;
    public double? Percent { get; init; }
    /// <summary>Downloaded bytes for the current download phase. Null means unknown; zero can be a valid reading.</summary>
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
    /// <summary>When true, Exo keeps motion short. When false, Exo plays its animations even if Windows reduce-animations is on.</summary>
    public bool ReduceMotion { get; set; }
    /// <summary>Always on. Chrome hides until an edge hover; pin insets the page.</summary>
    public bool FramelessMode { get; set; } = true;
    public bool FramelessPinTop { get; set; }
    public bool FramelessPinSide { get; set; }
    /// <summary>Shell text color: "white" or "grey".</summary>
    public string TextColour { get; set; } = "white";
    /// <summary>Shell text size: "small", "normal" or "large".</summary>
    public string TextSize { get; set; } = "normal";
    /// <summary>Liquid Glass: "clear", "tinted" (more opaque) or "solid".</summary>
    public string Glass { get; set; } = "clear";
    public string AppVersion { get; set; } = "1.0.0";
    /// <summary>Optional override; null = auto <c>%LOCALAPPDATA%\ExoLauncher\Games</c>.</summary>
    public string? DefaultInstallRoot { get; set; }
    /// <summary>Legacy field; Local portable path removed — always false.</summary>
    public bool CopyPortableIntoLibrary { get; set; } = false;
    /// <summary>Unused. The window is always resizable.</summary>
    public bool AllowResize { get; set; } = false;
    /// <summary>Always true — in-app update checks on start.</summary>
    public bool CheckForUpdates { get; set; } = true;
    /// <summary>Library sort: name | installed | recent | played | size | store.</summary>
    public string SortMode { get; set; } = "installed";
    /// <summary>Preferred exact store source for each canonical multi-store game. Local to this PC.</summary>
    public Dictionary<string, string> PreferredGameSources { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Recently launched game ids (newest first, capped).</summary>
    public List<string> Recent { get; set; } = new();
    /// <summary>Last played timestamps by game id (ISO).</summary>
    public Dictionary<string, string> LastPlayed { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>True after the first-run launcher setup flow.</summary>
    public bool OnboardingComplete { get; set; }
    /// <summary>Per-game launch extras (args / cwd / admin). Never applied to anti-cheat titles.</summary>
    public Dictionary<string, GameLaunchOverride> LaunchOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// Exo-owned custom cover file names keyed by exact library source id.
    /// Device-local by design; account sync never reads this dictionary.
    /// </summary>
    public Dictionary<string, string> CustomCoverImages { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Legacy field retained for settings-file compatibility. Not read or exposed.</summary>
    public bool LibraryShowInstalled { get; set; } = true;
    /// <summary>Legacy field retained for settings-file compatibility. Not read or exposed.</summary>
    public bool LibraryShowOwned { get; set; } = true;
    /// <summary>Show demos, soundtracks, and tools on the library shelf.</summary>
    public bool ShowLibraryTools { get; set; }
    /// <summary>Hours sum every Steam account and every Epic account seen on this PC. Off keeps a shared PC separate.</summary>
    public bool CountHoursAcrossAccounts { get; set; }
    /// <summary>HDR on for the main screen while a game runs, back off after (System > Graphics > HDR).</summary>
    public bool HdrForGames { get; set; }
    /// <summary>SDR content drawn with gamma 2.2 while a screen is in HDR (System > Graphics > HDR; <see cref="ExoLauncher.Helpers.SdrInHdr"/>).</summary>
    public bool HdrSdrGamma { get; set; } = true;
    /// <summary>DeepFilterNet 3 on the microphone in Exo's own Discord calls (<see cref="ExoLauncher.Services.DiscordClient.Voice.NoiseSuppressor"/>).</summary>
    public bool DiscordNoiseSuppression { get; set; } = true;
    /// <summary>The same on everyone else's voice in a call, each their own (<see cref="ExoLauncher.Services.DiscordClient.Voice.InboundCleaner"/>); off by default.</summary>
    public bool DiscordOthersNoiseSuppression { get; set; }
    /// <summary>Game focus: no Discord notifications while a game runs (the unread badge still counts).</summary>
    public bool DiscordQuietWhilePlaying { get; set; } = true;
    /// <summary>Exo's Discord calls: the microphone and speakers chosen (Windows endpoint ids; none is Windows' default) and their volume, 0-200 like Discord's.</summary>
    public string? DiscordInputDevice { get; set; }
    public string? DiscordOutputDevice { get; set; }
    public int DiscordInputVolume { get; set; } = 100;
    public int DiscordOutputVolume { get; set; } = 100;
    /// <summary>Others' soundboard sounds in a call: how loud (0-100%, as Discord's Soundboard Volume) and whether they play at all.</summary>
    public int DiscordSoundboardVolume { get; set; } = 100;
    public bool DiscordSoundboardMuted { get; set; }
    /// <summary>Discord settings > Voice & Video > Input Profile: "exo" (Exo Voice) or "custom"; none, or an
    /// earlier profile, is Exo Voice. In Custom, <see cref="DiscordNoiseSuppression"/> is the Noise Suppression choice.</summary>
    public string? DiscordInputProfile { get; set; }
    /// <summary>Push to Talk instead of Voice Activity, and how long the mic stays open after the key is let go (ms, Discord's 0-2000).</summary>
    public bool DiscordPushToTalk { get; set; }
    public int DiscordPttDelay { get; set; } = 20;
    /// <summary>Input Sensitivity: automatic, or a fixed threshold in dB (-100 to 0, Discord's default -60).</summary>
    public bool DiscordAutoSensitivity { get; set; } = true;
    public int DiscordSensitivity { get; set; } = -60;
    /// <summary>Echo Cancellation (Exo's, on speakers that aren't on your head: <see cref="ExoLauncher.Services.DiscordClient.Voice.EchoCanceller"/>) and Automatic Gain Control.</summary>
    public bool DiscordEchoCancellation { get; set; } = true;
    public bool DiscordAutoGain { get; set; } = true;
    /// <summary>Voice Focus in the Custom profile (Voice Isolation always has it): Exo's gate, leveler and limiter after noise suppression.</summary>
    public bool DiscordVoiceFocus { get; set; } = true;
    /// <summary>Voice Tune's tuning kept per microphone (by its Windows endpoint id), in the one-line form of
    /// <see cref="ExoLauncher.Services.DiscordClient.Voice.VoiceTune.Format"/>.</summary>
    public Dictionary<string, string> DiscordVoiceTunes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Global Attenuation: other apps' volume lowered by this percent while someone speaks (0 is off).</summary>
    public int DiscordAttenuation { get; set; }
    public bool DiscordAttenuateSelf { get; set; }
    public bool DiscordAttenuateOthers { get; set; } = true;
    /// <summary>Discord settings > Keybinds: actions on keys held anywhere in Windows during a call.</summary>
    public List<DiscordKeybind> DiscordKeybinds { get; set; } = [];
    /// <summary>Discord settings > Notifications: Windows notifications, the taskbar's unread badge, and flashing the taskbar button.</summary>
    public bool DiscordDesktopNotifications { get; set; } = true;
    public bool DiscordUnreadBadge { get; set; } = true;
    public bool DiscordTaskbarFlash { get; set; } = true;
    /// <summary>Discord's sounds turned off one by one (Discord's names: message1, mute, user_join, ...), all of them at once,
    /// and New Message in the channel I'm currently reading (off unless chosen, as in Discord).</summary>
    public List<string> DiscordDisabledSounds { get; set; } = [];
    public bool DiscordDisableAllSounds { get; set; }
    public bool DiscordSelectedChannelSound { get; set; }
    /// <summary>How loud all of Discord's sounds play, in percent (60 until chosen, the level they always had).</summary>
    public int DiscordSoundVolume { get; set; } = 60;
    /// <summary>Per game: "on" or "off" overrides what PCGamingWiki says; no entry is Auto.</summary>
    public Dictionary<string, string> HdrGameModes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>System > Display > Use NVIDIA color settings, by screen device path (format in <see cref="ExoLauncher.Helpers.NvidiaColor"/>). No entry: other apps control color.</summary>
    public Dictionary<string, string> NvidiaColor { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>System > Graphics > HDR > SDR looks right in HDR: by screen device path, the advanced-color profile the
    /// screen had before Exo's SDR gamma 2.2 one ("" for none), put back when HDR goes off.</summary>
    public Dictionary<string, string> SdrGammaBefore { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Downloads Exo runs itself (Epic, GOG, Amazon) pause while a game runs and resume after it.</summary>
    public bool PauseDownloadsWhilePlaying { get; set; } = true;
    /// <summary>Download hours: downloads start only from this hour (0-23) until <see cref="DownloadHoursEnd"/>; null = any time.</summary>
    public int? DownloadHoursStart { get; set; }
    public int? DownloadHoursEnd { get; set; }
    /// <summary>Games from Epic, GOG and Amazon update by themselves while the PC is idle.</summary>
    public bool AutoUpdateGames { get; set; } = true;
    /// <summary>Lucid (<see cref="ExoLauncher.Services.LucidCompanion"/>) starts with Exo, out of sight; off until the user turns it on.</summary>
    public bool StartLucidWithExo { get; set; }
    /// <summary>Apps whose start with Windows Exo turned off (<see cref="ExoLauncher.Helpers.StartupApps"/>); one turned back on stays on.</summary>
    public List<string> StartupHandedToExo { get; set; } = [];
    /// <summary>Where Exo last found Lucid.exe (it can live anywhere, even Documents).</summary>
    public string? LucidPath { get; set; }
    /// <summary>
    /// Retired with Exo's own OpenMouse bridge, read once by
    /// <see cref="ExoLauncher.Services.MouseBridgeRetirement"/> and then
    /// cleared: "exo" means Use Exo's bridge had stopped the OpenMouse Bridge app.
    /// </summary>
    public string? MouseBridge { get; set; }
    /// <summary>Retired: the OpenMouse Bridge app's Windows sign-in entry as Exo removed it.</summary>
    public string? MouseBridgeAppAutostart { get; set; }
    /// <summary>Retired: the OpenMouse Bridge app Exo stopped.</summary>
    public string? MouseBridgeAppPath { get; set; }
    /// <summary>
    /// Game sheet > Upscalers: games with anti-cheat whose upscaler files the
    /// user allowed Exo to change at their own risk, keyed by exact library id.
    /// The value is the anti-cheat named in the confirmation; the consent lapses
    /// when a different one is detected. Only <c>dlss.consent</c> writes it, and
    /// it refuses Riot games and Riot Vanguard.
    /// </summary>
    public Dictionary<string, string> UpscalerAntiCheatConsent { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Settings > Game stores > Steam overlay: on for a Steam game Exo has no record of. Off by default: it costs frames.</summary>
    public bool SteamOverlayDefaultOn { get; set; }
    /// <summary>
    /// Each installed Steam game's overlay switch as Exo set it, by app id: "on" or "off", with "?" until
    /// Steam has it (<see cref="ExoLauncher.Adapters.SteamOverlayConfig.Plan"/>).
    /// </summary>
    public Dictionary<string, string> SteamOverlay { get; set; } = new(StringComparer.OrdinalIgnoreCase);
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

/// <summary>One of Discord's keybinds in Exo's Discord client: an action and the keys (Windows virtual-key codes) held together.</summary>
public sealed class DiscordKeybind
{
    public string Action { get; set; } = "";
    public List<int> Keys { get; set; } = [];
}
