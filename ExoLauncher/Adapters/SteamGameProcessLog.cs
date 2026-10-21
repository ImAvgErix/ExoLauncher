using System.Globalization;
using System.Text.RegularExpressions;
using ExoLauncher.Adapters.Cli;
using ExoLauncher.Models;

namespace ExoLauncher.Adapters;

/// <summary>
/// Steam can identify an anti-cheat handoff after Windows stops exposing its
/// image path. Use that evidence only with a matching app, start time, and an
/// executable that Steam named under this game's installed directory.
/// </summary>
internal sealed class SteamGameProcessLog(Func<string>? readLog = null)
{
    internal sealed record Entry(string AppId, int ProcessId, DateTime StartedTrackingUtc, string? Executable);
    internal sealed record Snapshot(IReadOnlyDictionary<int, Entry> Active, IReadOnlyList<Entry> History);
    private static readonly Regex Event = new(
        @"^\[(?<time>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\] AppID (?<app>\d+) (?:(?:adding PID (?<add>\d+) as a tracked process)(?<command>.*)|no longer tracking PID (?<remove>\d+).*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private readonly object _gate = new();
    private Snapshot _snapshot = new(new Dictionary<int, Entry>(), []);
    private DateTime _refreshAt;
    private (long Length, long WriteTicks) _parsedStamp;
    private string? _steamRoot;

    internal string? Resolve(GameEntry game, int pid, string name, long startedUtcTicks)
    {
        if (game.Store != StoreKind.Steam || !SteamProtocol.IsValidAppId(game.LaunchTarget ?? "") ||
            startedUtcTicks <= 0) return null;
        lock (_gate)
        {
            if (DateTime.UtcNow >= _refreshAt)
            {
                _refreshAt = DateTime.UtcNow.AddSeconds(1);
                try
                {
                    var text = readLog is null ? ReadCurrentLogIfChanged() : readLog();
                    if (text is not null) _snapshot = Parse(text);
                }
                catch
                {
                    _snapshot = new(new Dictionary<int, Entry>(), []);
                    _parsedStamp = default;
                }
            }
            return Resolve(_snapshot, game, pid, name, startedUtcTicks, File.Exists);
        }
    }

    internal static string? Resolve(Snapshot snapshot, GameEntry game, int pid, string name,
        long startedUtcTicks, Func<string, bool> exists)
    {
        if (game.Store != StoreKind.Steam || !SteamProtocol.IsValidAppId(game.LaunchTarget ?? "") ||
            !game.Installed || string.IsNullOrWhiteSpace(game.Path) || startedUtcTicks <= 0 ||
            !snapshot.Active.TryGetValue(pid, out var current) || current.AppId != game.LaunchTarget)
            return null;
        var started = new DateTime(startedUtcTicks, DateTimeKind.Utc);
        // Log timestamps have second precision. A newly reused PID cannot
        // inherit an old entry, and a long-unrelated process is not a handoff.
        if (current.StartedTrackingUtc < started.AddSeconds(-1) ||
            current.StartedTrackingUtc > started.AddMinutes(2)) return null;
        bool Eligible(Entry entry) => entry.AppId == game.LaunchTarget &&
            !string.IsNullOrWhiteSpace(entry.Executable) &&
            string.Equals(Path.GetFileNameWithoutExtension(entry.Executable), name, StringComparison.OrdinalIgnoreCase) &&
            Services.GameProcessRegistry.IsEligibleExecutableForStop(game, name, entry.Executable) &&
            exists(entry.Executable);
        // A named image outside the install root is contrary evidence.
        if (current.Executable is not null) return Eligible(current) ? current.Executable : null;
        return snapshot.History.LastOrDefault(entry =>
            entry.StartedTrackingUtc >= started.AddMinutes(-5) &&
            entry.StartedTrackingUtc <= started.AddSeconds(1) && Eligible(entry))?.Executable;
    }

    internal static Snapshot Parse(string text)
    {
        var active = new Dictionary<int, Entry>();
        var history = new List<Entry>();
        foreach (var line in text.Split('\n'))
        {
            var match = Event.Match(line.TrimEnd('\r'));
            if (!match.Success || !DateTime.TryParseExact(match.Groups["time"].Value,
                "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var local)) continue;
            var appId = match.Groups["app"].Value;
            if (int.TryParse(match.Groups["remove"].Value, out var removed))
            {
                if (active.TryGetValue(removed, out var old) && old.AppId == appId) active.Remove(removed);
                continue;
            }
            if (!int.TryParse(match.Groups["add"].Value, out var pid) || pid <= 0) continue;
            var command = match.Groups["command"].Value.TrimStart(' ', '"');
            var end = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            string? executable = null;
            if (end >= 0)
            {
                try
                {
                    var path = command[..(end + 4)];
                    if (Path.IsPathFullyQualified(path)) executable = Path.GetFullPath(path);
                }
                catch { /* malformed command provides no path evidence */ }
            }
            var entry = new Entry(appId, pid, local.ToUniversalTime(), executable);
            active[pid] = entry;
            history.Add(entry);
        }
        return new(active, history);
    }

    /// <summary>Null when the log is unchanged since the last parse.</summary>
    private string? ReadCurrentLogIfChanged()
    {
        _steamRoot ??= SteamInstallation.ResolveRoot();
        if (_steamRoot is null) return "";
        var path = Path.Combine(_steamRoot, "logs", "gameprocess_log.txt");
        using var stream = new FileStream(path,
            FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        // Length comes from the open handle, so it is exact while Steam appends.
        var stamp = (stream.Length, File.GetLastWriteTimeUtc(path).Ticks);
        if (stamp == _parsedStamp) return null;
        _parsedStamp = stamp;
        const int maxBytes = 1024 * 1024;
        if (stream.Length > maxBytes) stream.Seek(-maxBytes, SeekOrigin.End);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
