namespace ExoLauncher.Adapters;

/// <summary>Separator-bounded Steam process ownership checks.</summary>
internal static class SteamProcessPath
{
    // Keep chrome suppressed past the full cold-client handoff budget.
    internal static readonly TimeSpan LaunchHandoffTimeout = TimeSpan.FromSeconds(45);
    internal static readonly TimeSpan LaunchProcessConfirmationWindow = TimeSpan.FromMilliseconds(750);
    internal static readonly TimeSpan LaunchChromeSuppressionTimeout = TimeSpan.FromSeconds(50);

    private static readonly HashSet<string> LaunchHelperNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "steam",
        "steamwebhelper",
        "steamservice",
        "gameoverlayui",
        "gameoverlayui64",
        "steamerrorreporter",
        "crashreportclient",
        "unitycrashhandler",
        "unitycrashhandler64",
        "easyanticheat",
        "easyanticheat_eos",
        "epiconlineservices",
        "eosoverlayrenderer-win64-shipping",
    };

    internal static bool IsWithinInstall(string installPath, string executablePath)
    {
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installPath));
            var executable = Path.GetFullPath(executablePath);
            var relative = Path.GetRelativePath(root, executable);
            return relative != "." &&
                   !Path.IsPathFullyQualified(relative) &&
                   relative != ".." &&
                   !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                   !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// A Steam handoff only succeeds when it produces a process that was not
    /// present before the protocol request, lives below the selected install,
    /// and is not a known client/bootstrap/crash helper.
    /// </summary>
    internal static bool IsEligibleGameProcess(
        int processId,
        string? processName,
        string? executablePath,
        string? installPath)
    {
        return processId > 0 &&
               !string.IsNullOrWhiteSpace(processName) &&
               !LaunchHelperNames.Contains(processName) &&
               !ProcessHelper.IsNonGameProcessName(processName) &&
               !string.IsNullOrWhiteSpace(executablePath) &&
               !string.IsNullOrWhiteSpace(installPath) &&
               IsWithinInstall(installPath, executablePath);
    }

    internal static bool IsEligibleNewGameProcess(
        int processId,
        string? processName,
        string? executablePath,
        string? installPath,
        ISet<int> processIdsBeforeLaunch)
    {
        return !processIdsBeforeLaunch.Contains(processId) &&
               IsEligibleGameProcess(processId, processName, executablePath, installPath);
    }

    /// <summary>Credits a freshly observed process only if it survives the confirmation grace.</summary>
    internal static int? ConfirmFreshGameProcess(int? candidatePid, Func<int, bool> isStillAlive)
    {
        if (candidatePid is not int pid || pid <= 0) return null;
        try { return isStillAlive(pid) ? pid : null; }
        catch { return null; }
    }
}
