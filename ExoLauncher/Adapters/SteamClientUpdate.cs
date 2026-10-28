namespace ExoLauncher.Adapters;

/// <summary>
/// Steam's own client, kept current out of sight. Steam downloads a client update while it runs and installs it only as
/// it next starts, which is when its "Updating Steam" box shows; its log (logs\bootstrap_log.txt) tells which: "Download
/// complete." since its last start is an update waiting. Its Beta Update is the branch named in package\beta
/// ("publicbeta", as Steam's own Client Beta Participation writes it), read as Steam starts, which logs "Opted in to
/// client beta '…' via beta file".
/// </summary>
internal static class SteamClientUpdate
{
    internal const string Beta = "publicbeta";

    private static string BetaPath(string root) => Path.Combine(root, "package", "beta");

    /// <summary>The client beta Steam starts on (package\beta), or null for its stable client.</summary>
    internal static string? ReadBeta(string root)
    {
        var path = BetaPath(root);
        return File.Exists(path) && File.ReadAllText(path).Trim() is { Length: > 0 } beta ? beta : null;
    }

    /// <summary>Puts Steam on its Beta Update from its next start, as its own setting does.</summary>
    internal static void JoinBeta(string root) => File.WriteAllText(BetaPath(root), Beta);

    /// <summary>The end of Steam's update log (its last few hundred lines), read while Steam writes it.</summary>
    internal static IReadOnlyList<string> ReadLog(string root, int lines = 400)
    {
        var path = Path.Combine(root, "logs", "bootstrap_log.txt");
        if (!File.Exists(path)) return [];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var tail = new Queue<string>(lines);
        while (reader.ReadLine() is { } line)
        {
            if (tail.Count == lines) tail.Dequeue();
            tail.Enqueue(line);
        }
        return [.. tail];
    }

    /// <summary>
    /// Why Steam should start again now, or null: a client update it downloaded and installs only as it starts, or the
    /// beta its package\beta names, which it hasn't started on since.
    /// </summary>
    internal static string? RestartFor(IReadOnlyList<string> log, string? beta)
    {
        var start = log.Count;
        for (var i = log.Count - 1; i >= 0; i--)
            if (log[i].Contains("Startup - updater", StringComparison.Ordinal)) { start = i; break; }
        // Read from the end back to the last start: "Download complete." there is an update waiting, unless Steam went
        // on to install it (an update found as it starts is installed then and there).
        for (var i = log.Count - 1; i > start; i--)
        {
            if (log[i].Contains("Installing update", StringComparison.Ordinal) || log[i].Contains("Update complete", StringComparison.Ordinal)) break;
            if (log[i].Contains("Download complete.", StringComparison.Ordinal)) return "a client update is waiting";
        }
        if (start == log.Count) return null;
        if (beta is not null && !log.Skip(start).Any(line => line.Contains($"Opted in to client beta '{beta}'", StringComparison.Ordinal)))
            return $"it isn't on its {beta} client yet";
        return null;
    }
}
