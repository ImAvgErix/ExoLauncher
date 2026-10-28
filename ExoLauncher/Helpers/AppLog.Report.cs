using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ExoLauncher.Helpers;

/// <summary>
/// What an error's Copy logs button copies: Exo's and Windows' versions, the error the page showed and where, then the
/// log entries that explain it: that part of Exo's from the last two hours, and every warning, error and crash from the
/// last 30 minutes, in time order. The Windows user name and profile folder are taken out, as the log's secrets already are.
/// </summary>
public static partial class AppLog
{
    /// <summary>The words that mark each room's entries (its log prefixes and host method names), by the room's name.</summary>
    private static readonly Dictionary<string, string[]> Areas = new(StringComparer.OrdinalIgnoreCase)
    {
        ["devices"] = ["Controller", "Mouse", "Devices", "Peripheral", "Lucid", "Audio", "Usb", "Keyboard", "Dictation", "steamInput"],
        ["pc"] = ["Runtimes", "Apps", "Leftovers", "Edge", "Gpu", "Driver", "Display", "Chipset", "Hdr", "Graphics", "Upscaler", "Dlss", "Vsr", "Performance", "Signature"],
        ["library"] = ["Launch", "Install", "Uninstall", "Library", "Steam", "Epic", "Legendary", "Gog", "EA ", "Ubisoft", "Battle.net", "Riot", "Xbox", "Shaders", "Cover", "Achievement", "game."],
        ["store"] = ["Store"],
        ["discord"] = ["Discord", "Voice", "Equicord"],
        ["music"] = ["Spotify", "Music", "Apple", "YouTube", "Jam", "Lyrics"],
        ["settings"] = ["Settings", "Stores", "stores.", "accounts.", "Capture:"],
    };

    /// <summary>Words in an error that name no one thing, so they pick no log lines.</summary>
    private static readonly HashSet<string> CommonWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "Exo", "Exo's", "Windows", "Try", "Again", "Open", "Opening", "Close", "Client", "This", "That", "The", "Your", "Could",
        "Couldn't", "Didn't", "Can't", "Not", "Nothing", "Check", "Sign", "Make", "Start", "Error", "Failed", "Unknown",
    };

    [GeneratedRegex(@"\b[A-Z][A-Za-z0-9+]{2,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex ProperWord();

    /// <summary>The names an error says (Riot, OpenAL, Spotify): log lines that mention one belong with it, whatever the room.</summary>
    internal static string[] NamesIn(string error) =>
        [.. ProperWord().Matches(error).Select(m => m.Value).Where(word => !CommonWords.Contains(word)).Distinct(StringComparer.OrdinalIgnoreCase).Take(6)];

    private const int MaxReportEntries = 300;

    [GeneratedRegex(@"^\[(?<at>[0-9T:.\-]+Z)\] (?<level>[A-Z]+) ", RegexOptions.CultureInvariant)]
    private static partial Regex EntryHead();

    /// <summary>The report for the error <paramref name="error"/> shown in <paramref name="area"/>, from the log's <paramref name="lines"/>.</summary>
    internal static string ReportFor(string error, string area, IEnumerable<string> lines, DateTime nowUtc, string version, string windows, string userName, string profile)
    {
        string[] words = [.. Areas.TryGetValue(area, out var known) ? known : [], .. NamesIn(error)];
        var picked = new List<string>();
        foreach (var entry in Entries(lines))
        {
            var head = EntryHead().Match(entry);
            if (!head.Success || !DateTime.TryParse(head.Groups["at"].Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)) continue;
            var age = nowUtc - at.ToUniversalTime();
            if (age > TimeSpan.FromHours(2) || age < -TimeSpan.FromMinutes(5)) continue;
            var trouble = head.Groups["level"].Value is "WARN" or "ERROR" or "CRASH";
            var mine = words.Any(word => entry.Contains(word, StringComparison.OrdinalIgnoreCase));
            if (mine || trouble && age <= TimeSpan.FromMinutes(30)) picked.Add(entry);
        }
        if (picked.Count > MaxReportEntries) picked = picked[^MaxReportEntries..];
        var report = new StringBuilder()
            .AppendLine($"Exo Launcher {version} · Windows {windows} · {nowUtc:yyyy-MM-dd HH:mm} UTC")
            .AppendLine($"Error ({(string.IsNullOrWhiteSpace(area) ? "app" : area)}): {error.Trim()}")
            .AppendLine()
            .AppendLine(picked.Count == 0 ? "Nothing in Exo's log about it from the last two hours." : $"Exo's log ({picked.Count} entries):");
        foreach (var entry in picked) report.AppendLine(entry);
        return Personal(Redact(report.ToString()), userName, profile);
    }

    /// <summary>The log's entries: a line that doesn't start with a time (a crash's stack) belongs to the entry above it.</summary>
    private static IEnumerable<string> Entries(IEnumerable<string> lines)
    {
        StringBuilder? entry = null;
        foreach (var line in lines)
        {
            if (line.StartsWith('[') && entry is not null) { yield return entry.ToString(); entry = null; }
            if (line.Length == 0) continue;
            if (entry is null) entry = new StringBuilder(line);
            else entry.Append('\n').Append(line);
        }
        if (entry is not null) yield return entry.ToString();
    }

    /// <summary>The profile folder as %USERPROFILE% and the user name as &lt;user&gt;, wherever they stand alone.</summary>
    internal static string Personal(string text, string userName, string profile)
    {
        if (profile.Length > 3) text = text.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        if (userName.Length >= 3) text = Regex.Replace(text, $@"(?<![A-Za-z0-9]){Regex.Escape(userName)}(?![A-Za-z0-9])", "<user>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return text;
    }

    /// <summary>This run's and the last run's log, oldest first, read beside the writer.</summary>
    internal static IEnumerable<string> RecentLines()
    {
        foreach (var path in new[] { LogPath.Value + ".1", LogPath.Value })
        {
            string[] lines;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                lines = reader.ReadToEnd().Split('\n');
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var line in lines) yield return line.TrimEnd('\r');
        }
    }
}
