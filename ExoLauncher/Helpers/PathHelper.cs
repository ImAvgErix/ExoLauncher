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
}
