using ExoLauncher.Helpers;
using Microsoft.Win32;

namespace ExoLauncher.Services;

/// <summary>
/// Removing Microsoft Edge from System › Apps while the WebView2 runtime stays (Exo and other apps run on it). Edge's entry
/// says NoRemove, and outside the EEA its setup refuses unless Edge Update allows it (EdgeUpdateDev\AllowUninstall) and the
/// old Edge's stub is in SystemApps: Win11Debloat's way, with Edge's own setup doing the removing (--force-uninstall).
/// Edge Update stays, since it keeps WebView2 current; its documented policy stops it installing Edge again
/// (Install{Edge's app id} = 0) and leaves WebView2's own id alone. Allow Edge again takes that policy back.
/// </summary>
internal static class EdgeRemoval
{
    /// <summary>Edge Stable's app id in Edge Update (WebView2's is {F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}).</summary>
    private const string EdgeAppId = "{56EB18F8-B008-4CBD-B6D2-8C97FE7E9062}";
    private const string PolicyKey = @"SOFTWARE\Policies\Microsoft\EdgeUpdate";
    private const string PolicyValue = "Install" + EdgeAppId;

    /// <summary>The Edge browser's own entry (not WebView2's, not Edge Update's).</summary>
    internal static bool IsEdge(string name, string? command) =>
        name.Equals("Microsoft Edge", StringComparison.OrdinalIgnoreCase) && command?.Contains("--msedge", StringComparison.OrdinalIgnoreCase) == true;

    internal static bool IsEdge(InstalledProgram program) => !program.Store && IsEdge(program.Name, program.Uninstall);

    /// <summary>The WebView2 runtime: Exo runs on it, so it never goes from here.</summary>
    internal static bool IsWebView(string name) => name.StartsWith("Microsoft Edge WebView2", StringComparison.OrdinalIgnoreCase);

    /// <summary>Edge Update is told not to install Edge (Exo removed it).</summary>
    internal static bool Blocked()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(PolicyKey);
            return key?.GetValue(PolicyValue) is int value && value == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return false; }
    }

    /// <summary>Edge's own setup.exe, named by its entry and where Edge installs (Program Files (x86)\Microsoft\Edge\Application\…\Installer); else null.</summary>
    internal static string? Setup(string command)
    {
        var file = Adapters.OfficialInstalledLibraries.SplitCommand(command).FileName;
        var edge = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application") + "\\";
        return !string.IsNullOrWhiteSpace(file) && Path.IsPathFullyQualified(file) && Path.GetFullPath(file) is var full &&
            full.StartsWith(edge, StringComparison.OrdinalIgnoreCase) && full.EndsWith(@"\Installer\setup.exe", StringComparison.OrdinalIgnoreCase) &&
            full.IndexOfAny(['"', '%', '&', '|', '<', '>', '^', '!']) < 0 && File.Exists(full) ? full : null;
    }

    /// <summary>Edge's browser itself, beside the versioned folder its setup sits in (…\Application\msedge.exe).</summary>
    internal static string Browser(string setup) =>
        Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(setup)!)!)!, "msedge.exe");

    /// <summary>
    /// The elevated steps, in order. The stub (and its folder) go again only when Exo made them. Edge Update is told not to
    /// install Edge again only once Edge is really gone: the steps run on whatever each one did, and a setup that refused
    /// must not leave Edge installed but barred from being put back.
    /// </summary>
    internal static IReadOnlyList<string> Commands(string setup, bool stubThere, bool folderThere)
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SystemApps", "Microsoft.MicrosoftEdge_8wekyb3d8bbwe");
        var stub = Path.Combine(folder, "MicrosoftEdge.exe");
        var steps = new List<string>
        {
            @"reg add ""HKLM\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdateDev"" /v AllowUninstall /t REG_SZ /d """" /f",
            // Edge's own windows only: Exo's WebView2 is msedgewebview2.exe and stays.
            "taskkill /im msedge.exe /f",
        };
        if (!folderThere) steps.Add($@"mkdir ""{folder}""");
        if (!stubThere) steps.Add($@"type nul > ""{stub}""");
        steps.Add($@"""{setup}"" --uninstall --msedge --channel=stable --system-level --verbose-logging --force-uninstall");
        if (!stubThere) steps.Add($@"del /f /q ""{stub}""");
        if (!folderThere) steps.Add($@"rd ""{folder}""");
        steps.Add($@"if not exist ""{Browser(setup)}"" reg add ""HKLM\{PolicyKey}"" /v {PolicyValue} /t REG_DWORD /d 0 /f");
        return steps;
    }

    /// <summary>Removes Edge (one Windows prompt); a problem in words, or null once Edge is gone.</summary>
    internal static async Task<string?> RunAsync(InstalledProgram program, CancellationToken ct)
    {
        if (program.Uninstall is not { } command || Setup(command) is not { } setup) return "Edge's own setup isn't where Edge installs it.";
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SystemApps", "Microsoft.MicrosoftEdge_8wekyb3d8bbwe");
        var steps = Commands(setup, File.Exists(Path.Combine(folder, "MicrosoftEdge.exe")), Directory.Exists(folder));
        AppLog.Info("Apps: removing Microsoft Edge with its own setup; WebView2 and Edge Update stay.");
        if (!await ReplacedApps.RemoveAsAdminAsync(steps, ct).ConfigureAwait(false)) return "Windows didn't allow it.";
        return File.Exists(Browser(setup)) ? "Edge's setup didn't remove it. Edge is still installed." : null;
    }

    /// <summary>Lets Edge Update install Edge again (Microsoft's installer, or Windows).</summary>
    internal static Task<bool> AllowAgainAsync(CancellationToken ct) =>
        ReplacedApps.RemoveAsAdminAsync([$@"reg delete ""HKLM\{PolicyKey}"" /v {PolicyValue} /f"], ct);

    /// <summary>What Edge leaves that isn't Edge's alone: WebView2, Edge Update and their shared core stay.</summary>
    internal static IEnumerable<string> Shared()
    {
        var x86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var part in new[] { "EdgeWebView", "EdgeCore", "EdgeUpdate", "Temp" })
        {
            yield return Path.Combine(x86, "Microsoft", part);
            yield return Path.Combine(local, "Microsoft", part);
        }
    }
}
