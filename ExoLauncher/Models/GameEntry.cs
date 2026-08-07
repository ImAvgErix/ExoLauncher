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
    /// <summary>Primary action the UI should offer: play | install | update | none.</summary>
    public string PrimaryAction
    {
        get
        {
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
}

public sealed class InstallResult
{
    public bool Ok { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? Path { get; init; }
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
    public bool CloseStoreClientsAfterLaunch { get; set; } = true;
    public bool AutoInstallRedistributables { get; set; } = false; // ask first
    public bool MinimizeWhilePlaying { get; set; } = true;
    /// <summary>Always true. Documented; not a user toggle that can be turned off into unsafe mode.</summary>
    public bool AntiCheatSafeMode { get; set; } = true;
    public string AppVersion { get; set; } = "0.1.0";
    /// <summary>Default install root for Local/Legendary/gogdl when not overridden.</summary>
    public string? DefaultInstallRoot { get; set; }
}
