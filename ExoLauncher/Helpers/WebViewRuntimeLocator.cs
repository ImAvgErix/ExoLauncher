namespace ExoLauncher.Helpers;

/// <summary>
/// Picks the WebView2 browser folder. A stale Clients <c>pv</c> with no
/// <c>msedgewebview2.exe</c> makes environment creation fail with
/// "Unable to find the specified file" — verify the binary, not the registry.
/// </summary>
internal static class WebViewRuntimeLocator
{
    internal const string EvergreenClientGuid = "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
    internal const string EnvFolderVariable = "WEBVIEW2_BROWSER_EXECUTABLE_FOLDER";

    /// <summary>
    /// WebView2Loader reads this env var and ignores
    /// <c>browserExecutableFolder</c> when it is set. A pin to a folder that
    /// no longer contains <c>msedgewebview2.exe</c> (Exo OS uninstalled, a
    /// previous session's temp runtime) fails environment creation with
    /// "Unable to find the specified file" even when Evergreen is healthy.
    /// </summary>
    internal static void ClearStaleBrowserFolderPin()
    {
        ClearStalePin(EnvironmentVariableTarget.Process);
        try { ClearStalePin(EnvironmentVariableTarget.User); }
        catch { /* user env is advisory */ }
    }

    internal static bool ShouldClearBrowserFolderPin(string? folder) =>
        !string.IsNullOrWhiteSpace(folder) && !IsRuntimeFolder(folder);

    private static void ClearStalePin(EnvironmentVariableTarget target)
    {
        string? value;
        try { value = Environment.GetEnvironmentVariable(EnvFolderVariable, target); }
        catch { return; }
        if (!ShouldClearBrowserFolderPin(value)) return;
        try { Environment.SetEnvironmentVariable(EnvFolderVariable, null, target); }
        catch { return; }
        AppLog.Warn("webview-runtime-env-ignored=" + value);
    }

    internal static string? ResolveBrowserExecutableFolder()
    {
        ClearStaleBrowserFolderPin();
        return Resolve(
            Environment.GetEnvironmentVariable(EnvFolderVariable),
            Path.Combine(AppContext.BaseDirectory, "Runtime", "WebView2"),
            RegisteredEvergreenExe(),
            DiscoverInstalledRuntimeFolder(),
            SiblingExoRuntimeFolder());
    }

    /// <summary>
    /// The explicit folder used on a retry after "file not found": evergreen
    /// resolution (null) already failed, so name a real installed runtime
    /// folder instead. Order: env pin, the registered evergreen version's
    /// folder, newest installed Application\&lt;version&gt;, bundled, sibling Exo.
    /// </summary>
    internal static string? ResolveExplicitInstalledFolder()
    {
        ClearStaleBrowserFolderPin();
        var registeredExe = RegisteredEvergreenExe();
        return ResolveExplicit(
            Environment.GetEnvironmentVariable(EnvFolderVariable),
            IsRuntimeExe(registeredExe) ? Path.GetDirectoryName(registeredExe) : null,
            DiscoverInstalledRuntimeFolder(),
            Path.Combine(AppContext.BaseDirectory, "Runtime", "WebView2"),
            SiblingExoRuntimeFolder());
    }

    internal static string? ResolveExplicit(params string?[] candidates)
    {
        foreach (var candidate in candidates)
            if (IsRuntimeFolder(candidate)) return candidate;
        return null;
    }

    /// <summary>One log line naming every runtime candidate and whether it holds msedgewebview2.exe.</summary>
    internal static string DescribeCandidates()
    {
        static string Folder(string? path) =>
            string.IsNullOrWhiteSpace(path) ? "<none>" : path + (IsRuntimeFolder(path) ? " [ok]" : " [missing exe]");
        static string Exe(string? path) =>
            string.IsNullOrWhiteSpace(path) ? "<none>" : path + (IsRuntimeExe(path) ? " [ok]" : " [missing]");
        string? env = null;
        try { env = Environment.GetEnvironmentVariable(EnvFolderVariable); } catch { }
        return "env=" + Folder(env) +
            "; registered(pv=" + (RegisteredVersion() ?? "<none>") + ")=" + Exe(RegisteredEvergreenExe()) +
            "; discovered=" + Folder(DiscoverInstalledRuntimeFolder()) +
            "; bundled=" + Folder(Path.Combine(AppContext.BaseDirectory, "Runtime", "WebView2")) +
            "; sibling=" + Folder(SiblingExoRuntimeFolder());
    }

    /// <summary>
    /// ERROR_FILE_NOT_FOUND / ERROR_PATH_NOT_FOUND from environment or
    /// controller creation: typically a runtime mid-update or a stale pv.
    /// </summary>
    internal static bool IsFileNotFound(Exception? error)
    {
        for (var current = error; current is not null; current = current.InnerException)
        {
            if (current is FileNotFoundException or DirectoryNotFoundException) return true;
            if (current.HResult is unchecked((int)0x80070002) or unchecked((int)0x80070003)) return true;
        }
        return false;
    }

    /// <summary>Delays before each retry after a file-not-found init failure.</summary>
    internal static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(0.5),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
    ];

    /// <summary>
    /// <c>null</c> means healthy evergreen (pass null to CreateWithOptionsAsync).
    /// A path is a Fixed Version / explicit folder used when evergreen is missing or stale.
    /// </summary>
    internal static string? Resolve(
        string? envFolder,
        string? bundledFolder,
        string? registeredExe,
        string? discoveredFolder,
        string? siblingExoFolder)
    {
        if (IsRuntimeFolder(envFolder)) return envFolder;
        if (IsRuntimeExe(registeredExe)) return null;
        if (IsRuntimeFolder(bundledFolder)) return bundledFolder;
        if (IsRuntimeFolder(discoveredFolder)) return discoveredFolder;
        if (IsRuntimeFolder(siblingExoFolder)) return siblingExoFolder;
        return null;
    }

    internal static bool IsRuntimeFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return false;
        try { return File.Exists(Path.Combine(folder, "msedgewebview2.exe")); }
        catch { return false; }
    }

    internal static bool IsRuntimeExe(string? exe)
    {
        if (string.IsNullOrWhiteSpace(exe)) return false;
        try { return File.Exists(exe); }
        catch { return false; }
    }

    internal static string? RegisteredEvergreenExe()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\" + EvergreenClientGuid)
                ?? Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\EdgeUpdate\Clients\" + EvergreenClientGuid)
                ?? Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\EdgeUpdate\Clients\" + EvergreenClientGuid);
            var pv = key?.GetValue("pv") as string;
            var location = key?.GetValue("location") as string;
            if (string.IsNullOrWhiteSpace(pv)) return null;
            if (!string.IsNullOrWhiteSpace(location))
                return Path.Combine(location, pv, "msedgewebview2.exe");

            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            foreach (var root in new[] { programFilesX86, programFiles })
            {
                if (string.IsNullOrWhiteSpace(root)) continue;
                var exe = Path.Combine(root, "Microsoft", "EdgeWebView", "Application", pv, "msedgewebview2.exe");
                if (File.Exists(exe)) return exe;
            }
        }
        catch { }
        return null;
    }

    internal static string? RegisteredVersion()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\" + EvergreenClientGuid)
                ?? Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\EdgeUpdate\Clients\" + EvergreenClientGuid)
                ?? Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\EdgeUpdate\Clients\" + EvergreenClientGuid);
            var pv = key?.GetValue("pv") as string;
            return string.IsNullOrWhiteSpace(pv) ? null : pv;
        }
        catch { return null; }
    }

    internal static string? DiscoverInstalledRuntimeFolder()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var root in new[] { programFilesX86, programFiles, local })
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            var application = Path.Combine(root, "Microsoft", "EdgeWebView", "Application");
            var found = NewestRuntimeFolder(application);
            if (found is not null) return found;
        }
        return null;
    }

    internal static string? SiblingExoRuntimeFolder()
    {
        try
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(local)) return null;
            return Path.Combine(local, "Exo", "app", "Runtime", "WebView2");
        }
        catch { return null; }
    }

    internal static string? NewestRuntimeFolder(string applicationRoot)
    {
        try
        {
            if (!Directory.Exists(applicationRoot)) return null;
            string? best = null;
            Version? bestVersion = null;
            foreach (var dir in Directory.EnumerateDirectories(applicationRoot))
            {
                if (!IsRuntimeFolder(dir)) continue;
                if (!Version.TryParse(Path.GetFileName(dir), out var version))
                {
                    best ??= dir;
                    continue;
                }
                if (bestVersion is null || version > bestVersion)
                {
                    bestVersion = version;
                    best = dir;
                }
            }
            return best;
        }
        catch { return null; }
    }
}
