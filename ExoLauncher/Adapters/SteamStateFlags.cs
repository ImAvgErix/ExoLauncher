namespace ExoLauncher.Adapters;

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
    public const int UpdateStarted = 512;
    public const int Uninstalling = 1024;
    public const int BackupRunning = 2048;
    public const int Reconfiguring = 4096;
    public const int Validating = 8192;
    public const int AddingFiles = 16384;
    public const int Preallocating = 32768;
    public const int Downloading = 65536;
    public const int Staging = 131072;
    public const int Committing = 262144;

    private const int BusyMask =
        UpdateRunning | UpdateStarted | Uninstalling | BackupRunning | Reconfiguring |
        Validating | AddingFiles | Preallocating | Downloading | Staging | Committing;

    public static bool TryParse(string? raw, out int flags)
    {
        flags = 0;
        return !string.IsNullOrWhiteSpace(raw) && int.TryParse(raw.Trim(), out flags);
    }

    public static bool IsFullyInstalled(string? raw) =>
        TryParse(raw, out var f) && (f & FullyInstalled) != 0;

    /// <summary>Installed title needs an update (UpdateRequired / missing / corrupt bits only).</summary>
    public static bool IsUpdateAvailable(string? raw, bool installed)
    {
        if (!installed || !TryParse(raw, out var f)) return false;
        if ((f & UpdateRequired) != 0) return true;
        if ((f & FilesMissing) != 0 || (f & FilesCorrupt) != 0) return true;
        return false;
    }

    /// <summary>
    /// True when Steam is actively working. StateFlags 6 (FullyInstalled|UpdateRequired)
    /// means an update is available — not that a download is running.
    /// </summary>
    public static bool IsBusy(string? raw, long? bytesToDownload, long? bytesDownloaded)
    {
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
        !IsBusy(raw, null, null) &&
        !IsUpdateAvailable(raw, installed: true);
}
