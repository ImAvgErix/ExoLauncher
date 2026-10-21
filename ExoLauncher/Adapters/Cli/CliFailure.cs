using ExoLauncher.Helpers;

namespace ExoLauncher.Adapters.Cli;

/// <summary>
/// Turns a store CLI's failure output (Legendary, gogdl, Nile) into one plain
/// sentence. Raw stderr is often a full log dump; it goes to the app log, never
/// to the screen.
/// </summary>
internal static class CliFailure
{
    private static readonly string[] DiskFull =
    [
        "not enough space", "no space left", "disk full", "insufficient disk", "insufficient space",
        "not enough free space", "errno 28", "0x80070070",
    ];

    private static readonly string[] Network =
    [
        "connectionerror", "connection error", "connection reset", "connection refused", "connection aborted",
        "timed out", "timeout", "max retries exceeded", "name or service not known", "getaddrinfo failed",
        "temporary failure in name resolution", "network is unreachable", "no route to host",
        "sslerror", "ssl:", "remote end closed", "failed to establish a new connection",
    ];

    private static readonly string[] SignedOut =
    [
        "not logged in", "log in first", "login required", "no saved credentials", "invalid credentials",
        "credentials", "unauthorized", "401 client error", "session expired", "token expired",
        "refresh token", "authentication failed", "not authenticated", "please login", "please log in",
    ];

    private static readonly string[] NotOwned =
    [
        "not in list of available games", "in list of available games", "do not own", "does not own",
        "not owned", "no access to", "game not found in library",
    ];

    private static readonly string[] AccessDenied =
    [
        "permission denied", "access is denied", "permissionerror", "winerror 5", "read-only file system",
    ];

    /// <param name="store">Store name the user knows, e.g. "Epic".</param>
    /// <param name="action">Lower-case verb: "install", "update", "verify".</param>
    internal static string Describe(string store, string action, string title, int? exitCode, string? output)
    {
        var raw = output?.Trim() ?? string.Empty;
        AppLog.Warn($"{store} {action} failed for \"{title}\" (exit {exitCode?.ToString() ?? "n/a"}): " +
                    (raw.Length == 0 ? "(no output)" : Tail(raw, 2000)));
        // These tools log progress to stderr too; the reason is at the end.
        return Classify(store, action, title, Tail(raw, 1500));
    }

    /// <summary>Plain message for an exception thrown while running a store job.</summary>
    internal static string Describe(string store, string action, string title, Exception error)
    {
        AppLog.Warn($"{store} {action} threw for \"{title}\": {error.GetType().Name}: {error.Message}");
        return error is IOException { HResult: unchecked((int)0x80070070) }
            ? DiskFullMessage(action, title)
            : Classify(store, action, title, error.Message);
    }

    /// <summary>Plain message for a game or store client that threw while starting; details go to the log.</summary>
    internal static string Launch(string? title, Exception error)
    {
        AppLog.Warn($"Launch threw for \"{title ?? "(no title)"}\": {error.GetType().Name}: {error.Message}");
        var name = string.IsNullOrWhiteSpace(title) ? null : title;
        return error switch
        {
            System.ComponentModel.Win32Exception { NativeErrorCode: 1223 } =>
                $"Windows did not get permission to start {name ?? "the game"}.",
            System.ComponentModel.Win32Exception { NativeErrorCode: 2 or 3 } or FileNotFoundException or DirectoryNotFoundException =>
                $"Exo could not find {(name is null ? "the game's" : name + "'s")} files. Refresh the library, or check the game's launch settings.",
            _ => $"{name ?? "The game"} did not start. Try Play again.",
        };
    }

    internal static string Classify(string store, string action, string title, string raw)
    {
        var text = raw.ToLowerInvariant();
        if (Matches(text, DiskFull)) return DiskFullMessage(action, title);
        if (Matches(text, AccessDenied))
            return $"Exo could not write to the game folder. Choose another folder, then {action} again.";
        if (Matches(text, SignedOut))
            return $"Sign in to {store} again under Settings, Game stores, then try again.";
        if (Matches(text, NotOwned))
            return $"The signed-in {store} account does not have {title}.";
        if (Matches(text, Network))
            return $"Could not reach {store}. Check your connection, then try again.";
        return $"Could not {action} {title}. Try again.";
    }

    private static string DiskFullMessage(string action, string title) =>
        $"There isn't enough disk space to {action} {title}. Free up space, then try again.";

    private static bool Matches(string text, string[] patterns) =>
        patterns.Any(pattern => text.Contains(pattern, StringComparison.Ordinal));

    private static string Tail(string text, int max) => text.Length <= max ? text : "…" + text[^max..];
}
