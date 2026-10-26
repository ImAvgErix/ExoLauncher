using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// A program's own uninstaller, run as Windows' Apps &amp; features runs it (its window, its questions), waited out to the
/// end: not only the process started, but every one it starts. NSIS and Inno Setup copy their uninstaller to Temp, start
/// the copy and exit at once; Squirrel starts the app to clean up after itself; an MSI's client waits for the installer
/// service. A child is followed while its parent is the uninstaller or one of its own (a pid it had before the run is
/// not), except a browser or Explorer it opens (a "sorry to see you go" page): those stay open after it finishes.
/// </summary>
internal static class UninstallRun
{
    private static readonly string[] NotWaitedFor = ["msedge.exe", "chrome.exe", "firefox.exe", "brave.exe", "opera.exe", "iexplore.exe", "explorer.exe", "msedgewebview2.exe"];

    /// <summary>Why it couldn't run (null when it ran to its end, whatever it chose to do).</summary>
    internal static async Task<string?> RunAsync(string command, TimeSpan limit, CancellationToken ct)
    {
        var before = BackgroundTrim.ProcessTable().Select(row => row.Pid).ToHashSet();
        using var process = Adapters.OfficialInstalledLibraries.StartUninstallString(command, out var declined);
        if (declined) return "Windows' permission was declined.";
        if (process is null) return "Its uninstaller didn't start.";
        var family = new HashSet<int> { process.Id };
        before.Remove(process.Id);
        var until = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(400, ct).ConfigureAwait(false);
            var table = BackgroundTrim.ProcessTable();
            // A parent's pid stays in the family after it exits, so a copy it started a moment before is still found.
            for (var grew = true; grew;)
            {
                grew = false;
                foreach (var (pid, parent, name) in table)
                    if (family.Contains(parent) && !before.Contains(pid) && !NotWaitedFor.Contains(name, StringComparer.OrdinalIgnoreCase) && family.Add(pid)) grew = true;
            }
            if (!table.Any(row => family.Contains(row.Pid))) return null;
        }
        return "Its uninstaller is still running.";
    }
}
