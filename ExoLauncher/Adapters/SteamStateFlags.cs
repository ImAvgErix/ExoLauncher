namespace ExoLauncher.Adapters;

internal enum SteamVerificationStatus { Waiting, Verifying, Confirmed, Unconfirmed, TimedOut, Cancelled }

internal sealed class SteamVerificationEvidence
{
    private bool _observed;

    internal SteamVerificationStatus Observe(string? flags, long? total, long? downloaded,
        TimeSpan elapsed, bool cancelled = false)
    {
        if (cancelled) return SteamVerificationStatus.Cancelled;
        var busy = SteamStateFlags.IsBusy(flags, total, downloaded);
        if (SteamStateFlags.TryParse(flags, out var parsed) && (parsed & SteamStateFlags.Validating) != 0)
            _observed = true;
        var ready = SteamStateFlags.IsFullyInstalled(flags) && !busy &&
                    (parsed & SteamStateFlags.Uninstalled) == 0 &&
                    !SteamStateFlags.IsUpdateAvailable(flags, true, total, downloaded);
        if (_observed && ready)
            return SteamVerificationStatus.Confirmed;
        if (elapsed > TimeSpan.FromMinutes(30)) return SteamVerificationStatus.TimedOut;
        if (!_observed && elapsed > TimeSpan.FromSeconds(30)) return SteamVerificationStatus.Unconfirmed;
        return _observed ? SteamVerificationStatus.Verifying : SteamVerificationStatus.Waiting;
    }
}

/// <summary>
/// Steam appmanifest StateFlags are a bitfield, not a single enum value.
/// Exact string compares like <c>flags == "4"</c> miss FullyInstalled|UpdateRequired (6), etc.
/// </summary>
internal static class SteamStateFlags
{
    public const int Uninstalled = 1;
    public const int UpdateRequired = 2;
    public const int FullyInstalled = 4;
    public const int FilesMissing = 32;
    public const int AppRunning = 64;
    public const int FilesCorrupt = 128;
    public const int UpdateRunning = 256;
    // These are appmanifest AppState bits, distinct from IPC EAppState.
    public const int UpdatePaused = 512;
    public const int UpdateStarted = 1024;
    public const int Uninstalling = 2048;
    public const int BackupRunning = 4096;
    public const int Reconfiguring = 65536;
    public const int Validating = 131072;
    public const int AddingFiles = 262144;
    public const int Preallocating = 524288;
    public const int Downloading = 1048576;
    public const int Staging = 2097152;
    public const int Committing = 4194304;
    public const int UpdateStopping = 8388608;

    private const int BusyMask =
        UpdateRunning | UpdateStarted | Uninstalling | BackupRunning | Reconfiguring |
        Validating | AddingFiles | Preallocating | Downloading | Staging | Committing | UpdateStopping;

    public static bool TryParse(string? raw, out int flags)
    {
        flags = 0;
        return !string.IsNullOrWhiteSpace(raw) && int.TryParse(raw.Trim(), out flags);
    }

    public static bool IsFullyInstalled(string? raw) =>
        TryParse(raw, out var f) && (f & FullyInstalled) != 0;

    /// <summary>Steam set AppRunning on the manifest. The title started even if Exo has not seen a PID yet.</summary>
    public static bool IsAppRunning(string? raw) =>
        TryParse(raw, out var f) && (f & AppRunning) != 0;

    /// <summary>
    /// A leftover <c>common</c> folder is not enough. Steam's Downloads row is
    /// the source of truth: FullyInstalled and not Uninstalled.
    /// Missing flags keep the previous path-exists behavior.
    /// An update Steam started, paused or could not finish clears FullyInstalled
    /// (1026, 1538) while the game it patches stays on disk: a build landed once
    /// (buildid is not 0), so it is installed and needs its update. A first
    /// install never finished has build 0.
    /// </summary>
    public static bool IsInstalledPresence(bool pathExists, string? flags, string? buildId = null)
    {
        if (!pathExists) return false;
        if (!TryParse(flags, out var f) || f == 0) return true;
        if ((f & (Uninstalled | Uninstalling)) != 0) return false;
        return (f & FullyInstalled) != 0 ||
               (!string.IsNullOrWhiteSpace(buildId) && buildId.Trim() != "0");
    }

    /// <summary>Installed title needs an update (UpdateRequired / missing / corrupt bits, or pending byte delta).</summary>
    public static bool IsUpdateAvailable(string? raw, bool installed) =>
        IsUpdateAvailable(raw, installed, null, null);

    public static bool IsUpdateAvailable(string? raw, bool installed, long? bytesToDownload, long? bytesDownloaded)
    {
        if (!installed) return false;
        if (TryParse(raw, out var f))
        {
            if ((f & UpdateRequired) != 0) return true;
            if ((f & FilesMissing) != 0 || (f & FilesCorrupt) != 0) return true;
        }
        // Steam often queues a patch with StateFlags=4 while BytesToDownload still
        // exceeds BytesDownloaded. Equal leftover counters after a finished patch
        // must not keep the card on Update.
        if (bytesToDownload is > 0 && (bytesDownloaded is null || bytesDownloaded.Value < bytesToDownload.Value))
            return true;
        return false;
    }

    /// <summary>
    /// Steam often leaves StateFlags=4 while <c>buildid</c> and <c>TargetBuildID</c>
    /// disagree. A non-zero target that does not match the installed build is a pending patch.
    /// </summary>
    public static bool HasPendingTargetBuild(string? buildId, string? targetBuildId)
    {
        if (string.IsNullOrWhiteSpace(targetBuildId)) return false;
        var target = targetBuildId.Trim();
        if (target == "0") return false;
        if (string.IsNullOrWhiteSpace(buildId)) return true;
        return !string.Equals(buildId.Trim(), target, StringComparison.Ordinal);
    }

    /// <summary>
    /// True when Steam is actively working. StateFlags 6 (FullyInstalled|UpdateRequired)
    /// means an update is available — not that a download is running.
    /// </summary>
    public static bool IsBusy(string? raw, long? bytesToDownload, long? bytesDownloaded)
    {
        // Paused bytes describe unfinished work, not a transfer moving now.
        if (TryParse(raw, out var pausedFlags) && (pausedFlags & UpdatePaused) != 0 &&
            (pausedFlags & (UpdateRunning | Downloading | Staging | Committing)) == 0)
            return false;
        // Only count byte progress once Steam has actually moved data.
        if (bytesToDownload is > 0 && bytesDownloaded is long d && d > 0 && d < bytesToDownload.Value)
            return true;
        if (!TryParse(raw, out var f) || f == 0) return false;
        if ((f & BusyMask) != 0) return true;
        return false;
    }

    /// <summary>Install/update watch considers the title ready.</summary>
    public static bool IsReady(string? raw) =>
        IsFullyInstalled(raw) &&
        !(TryParse(raw, out var flags) && (flags & UpdatePaused) != 0) &&
        !IsBusy(raw, null, null) &&
        !IsUpdateAvailable(raw, installed: true);

    internal static bool IsTransferComplete(string? raw, long? downloaded, long? toDownload,
        long? staged, long? toStage, string? buildId, string? targetBuildId) =>
        IsReady(raw) &&
        !IsUpdateAvailable(raw, true, toDownload, downloaded) &&
        !(toStage is > 0 && (staged is null || staged < toStage)) &&
        !HasPendingTargetBuild(buildId, targetBuildId);

    /// <summary>
    /// Cancelling an install may only roll back what Exo's request created. An
    /// appmanifest that already existed when the request was sent belongs to the
    /// user's library — a paused or partially downloaded game still has one —
    /// and uninstalling it on cancel would delete files nobody asked to remove.
    /// </summary>
    public static bool CanRollBackCancelledInstall(bool appManifestExistedBeforeRequest) =>
        !appManifestExistedBeforeRequest;

    /// <summary>
    /// Queued patch with no bytes moved. Steam's Downloads row still needs a
    /// start click. <see cref="IsBusy"/> after <c>steam://install</c> is not
    /// enough — that URI often sets UpdateStarted before any bytes flow.
    /// </summary>
    public static bool IsQueuedForTargetedPromotion(
        string? raw,
        long? bytesToDownload,
        long? bytesDownloaded,
        string? buildId,
        string? targetBuildId)
    {
        return bytesToDownload is > 0 &&
               bytesDownloaded is null or 0 &&
               (IsUpdateAvailable(raw, installed: true, bytesToDownload, bytesDownloaded) ||
                HasPendingTargetBuild(buildId, targetBuildId));
    }
}
