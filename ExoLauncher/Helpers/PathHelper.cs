namespace ExoLauncher.Helpers;

public static class PathHelper
{
    public static string AppDirectory =>
        AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>
    /// Redirects all app state (settings, covers, logs, library cache).
    /// Tests must set this: they share this process-wide path otherwise, and a
    /// test run would overwrite the real user's settings.json — which silently
    /// erased pins, favorites, and the onboarding flag.
    /// </summary>
    public const string DataDirOverrideVariable = "EXO_LAUNCHER_DATA_DIR";

    public static string AppDataDir
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable(DataDirOverrideVariable);
            var dir = !string.IsNullOrWhiteSpace(overridden)
                ? overridden!
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ExoLauncher");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string SettingsPath => Path.Combine(AppDataDir, "settings.json");

    /// <summary>Written once when onboarding finishes. Survives a lost settings.json
    /// so first-run setup can never be shown a second time.</summary>
    public static string OnboardedMarkerPath => Path.Combine(AppDataDir, ".onboarded");

    /// <summary>Auto install root when Settings has no override.</summary>
    public static string GamesRoot
    {
        get
        {
            var dir = Path.Combine(AppDataDir, "Games");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string LogsDir
    {
        get
        {
            var dir = Path.Combine(AppDataDir, "logs");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string LibraryCachePath => Path.Combine(AppDataDir, "library-cache.json");

    /// <summary>The quoted (or bare) executable at the start of a command or icon value ("C:\x\a.exe" /startup, C:\x\a.exe,0).</summary>
    internal static string? CommandExe(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var text = command.Trim();
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            return end > 1 ? text[1..end] : null;
        }
        var exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? text[..(exe + 4)] : null;
    }

    /// <summary>
    /// True when candidatePath resolves to a location strictly inside root
    /// (root itself is false). Rejects traversal like "..\" and unrelated absolute
    /// targets. Used wherever registry or manifest data becomes a path.
    /// </summary>
    public static bool IsUnderRoot(string? candidatePath, string? root)
    {
        if (string.IsNullOrWhiteSpace(candidatePath) || string.IsNullOrWhiteSpace(root))
            return false;
        try
        {
            var fullCandidate = Path.GetFullPath(candidatePath);
            var fullRoot = Path.GetFullPath(root);
            var rootWithSeparator = fullRoot.EndsWith(Path.DirectorySeparatorChar)
                ? fullRoot
                : fullRoot + Path.DirectorySeparatorChar;
            return fullCandidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Renames an unreadable state file to <c>&lt;name&gt;.unreadable-&lt;utc&gt;</c>
    /// so its contents are kept for recovery and the next save can start fresh.
    /// False when the file could not be moved.
    /// </summary>
    internal static bool MoveAsideUnreadable(string path)
    {
        try
        {
            if (!File.Exists(path)) return true;
            File.Move(path, $"{path}.unreadable-{DateTime.UtcNow:yyyyMMddHHmmss}", overwrite: true);
            AppLog.Warn($"Moved an unreadable {Path.GetFileName(path)} aside; starting fresh.");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"Could not move an unreadable {Path.GetFileName(path)} aside: {ex.GetType().Name}");
            return false;
        }
    }
}
