namespace ExoLauncher.Helpers;

/// <summary>Local rolling diagnostics — no network, no analytics.</summary>
public static class AppLog
{
    private static readonly object Gate = new();
    private const long MaxBytes = 2 * 1024 * 1024;

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message} | {ex.GetType().Name}: {ex.Message}");

    public static void Debug(string message)
    {
#if DEBUG
        Write("DEBUG", message);
#endif
    }

    private static void Write(string level, string message)
    {
        try
        {
            var path = Path.Combine(PathHelper.LogsDir, "app.log");
            var line = $"[{DateTime.UtcNow:O}] {level} {message}{Environment.NewLine}";
            lock (Gate)
            {
                try
                {
                    if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                    {
                        var bak = path + ".1";
                        try { File.Delete(bak); } catch { /* */ }
                        try { File.Move(path, bak); } catch { /* */ }
                    }
                }
                catch { /* */ }

                File.AppendAllText(path, line);
            }
        }
        catch
        {
            /* best-effort */
        }
    }
}
