using System.Diagnostics;
using ExoLauncher.Helpers;

namespace ExoLauncher.Adapters;

internal enum SteamIpcStatus
{
    Ok,
    CommandFailed,
    Unavailable,

    /// <summary>
    /// The helper is not deployed next to Exo. Retrying cannot change that, so
    /// callers must fall straight through to the protocol request.
    /// </summary>
    HostMissing,
}

/// <summary>
/// Runs ExoLauncher.SteamIpc.exe against the live Steam client. Steam stays
/// the official backend; Exo does not copy Steam files into its own tree.
/// </summary>
internal static class SteamClientIpc
{
    internal const string HostExeName = "ExoLauncher.SteamIpc.exe";

    /// <summary>
    /// The helper takes the verb and the app id only. Steam owns its library
    /// folders — IClientAppManager::InstallApp selects one by index, never by
    /// path — so an install directory is not part of this contract.
    /// </summary>
    public static SteamIpcStatus Command(string action, string appId)
    {
        var exe = ResolveHost();
        if (exe is null)
        {
            AppLog.Info("Steam IPC host is not installed next to Exo.");
            return SteamIpcStatus.HostMissing;
        }

        try
        {
            var start = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exe),
            };
            start.ArgumentList.Add(action);
            start.ArgumentList.Add(appId);

            using var process = Process.Start(start);
            if (process is null)
                return SteamIpcStatus.Unavailable;
            if (!process.WaitForExit(20_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* hung IPC */ }
                AppLog.Info("Steam IPC host timed out.");
                return SteamIpcStatus.Unavailable;
            }

            AppLog.Info($"Steam IPC host exited {process.ExitCode} for {action} {appId}.");
            return process.ExitCode switch
            {
                0 => SteamIpcStatus.Ok,
                1 => SteamIpcStatus.CommandFailed,
                _ => SteamIpcStatus.Unavailable,
            };
        }
        catch (Exception ex)
        {
            AppLog.Info($"Steam IPC host spawn failed: {ex.GetType().Name}: {ex.Message}");
            return SteamIpcStatus.Unavailable;
        }
    }

    private static string? ResolveHost()
    {
        var dir = Path.GetDirectoryName(Environment.ProcessPath);
        if (string.IsNullOrWhiteSpace(dir))
            return null;
        foreach (var candidate in new[]
                 {
                     Path.Combine(dir, "steam-ipc", HostExeName),
                     Path.Combine(dir, HostExeName),
                 })
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }
}
