using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ExoLauncher.Helpers;

/// <summary>
/// The GraphQL persisted queries Exo's Spotify room asks Spotify's own client
/// to run. Each build declares them as <c>new X("name","query","sha256")</c>;
/// the hashes change between builds, the names have not. They are read from
/// the installed xpui.spa (every route chunk, including ones the client has not
/// loaded yet, such as search) and cached until the archive changes. Queries
/// of the main bundle (home, getAlbum) are not in the archive: Spotify loads
/// that bundle from its V8 snapshot, so the page adapter finds them at runtime
/// (<see cref="SpotifyNative"/>) and Exo remembers what it found for that build
/// (<see cref="Remember"/>), so from the next launch on the room never waits for it.
/// </summary>
internal static partial class SpotifyQueries
{
    internal static readonly string[] Names = ["home", "searchDesktop", "getAlbum", "queryArtistOverview", "queryArtistDiscographyAll", "queryTrackCreditsGroupedModal"];
    private const long MaxEntryBytes = 16L << 20;

    private static (string Path, long Length, DateTime Written, IReadOnlyDictionary<string, string> Hashes)? _cache;
    private static readonly Lock Gate = new();

    private static string RememberedPath => Path.Combine(PathHelper.AppDataDir, "spotify-queries.json");

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256();

    // Spotify 1.3 builds append a fourth constructor argument (",null)").
    [GeneratedRegex("""\("([A-Za-z0-9_]{1,80})","query","([0-9a-f]{64})"[,)]""")]
    private static partial Regex Definition();

    /// name → sha256 for <see cref="Names"/>; names this build lacks are absent.
    internal static IReadOnlyDictionary<string, string> Read(string spotifyDirectory)
    {
        var archive = Path.Combine(spotifyDirectory, "Apps", "xpui.spa");
        var info = new FileInfo(archive);
        if (!info.Exists) return new Dictionary<string, string>();
        lock (Gate)
        {
            if (_cache is { } cached && cached.Path == archive && cached.Length == info.Length && cached.Written == info.LastWriteTimeUtc)
                return cached.Hashes;
        }
        var wanted = new HashSet<string>(Names, StringComparer.Ordinal);
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var zip = ZipFile.OpenRead(archive);
            foreach (var entry in zip.Entries)
            {
                if (!entry.FullName.EndsWith(".js", StringComparison.OrdinalIgnoreCase) || entry.Length > MaxEntryBytes) continue;
                using var reader = new StreamReader(entry.Open());
                foreach (Match match in Definition().Matches(reader.ReadToEnd()))
                    if (wanted.Contains(match.Groups[1].Value)) hashes.TryAdd(match.Groups[1].Value, match.Groups[2].Value);
                if (hashes.Count == wanted.Count) break;
            }
        }
        catch (Exception error)
        {
            AppLog.Warn("Spotify queries could not be read from xpui.spa: " + error.Message);
            return hashes;
        }
        foreach (var pair in Remembered(Build(spotifyDirectory, info))) hashes.TryAdd(pair.Key, pair.Value);
        var missing = Names.Where(name => !hashes.ContainsKey(name)).ToArray();
        if (missing.Length > 0) AppLog.Info("Spotify queries not in xpui.spa (looked up in the page): " + string.Join(", ", missing));
        lock (Gate) _cache = (archive, info.Length, info.LastWriteTimeUtc, hashes);
        return hashes;
    }

    /// <summary>
    /// The queries the page found at runtime, kept for this Spotify build. Only the names Exo
    /// asks for and well-formed hashes are kept; a new build starts over.
    /// </summary>
    internal static void Remember(string spotifyDirectory, IReadOnlyDictionary<string, string> found)
    {
        var archive = Path.Combine(spotifyDirectory, "Apps", "xpui.spa");
        var info = new FileInfo(archive);
        if (!info.Exists) return;
        var known = new Dictionary<string, string>(Read(spotifyDirectory), StringComparer.Ordinal);
        var learned = found.Where(pair => Names.Contains(pair.Key) && Sha256().IsMatch(pair.Value) && !known.ContainsKey(pair.Key)).ToArray();
        if (learned.Length == 0) return;
        foreach (var pair in learned) known[pair.Key] = pair.Value;
        try
        {
            var saved = Remembered(Build(spotifyDirectory, info)).Concat(learned).GroupBy(pair => pair.Key).ToDictionary(group => group.Key, group => group.Last().Value);
            Directory.CreateDirectory(PathHelper.AppDataDir);
            File.WriteAllText(RememberedPath, JsonSerializer.Serialize(new { build = Build(spotifyDirectory, info), hashes = saved }));
            lock (Gate) _cache = (archive, info.Length, info.LastWriteTimeUtc, known);
            AppLog.Info("Spotify queries remembered for this build: " + string.Join(", ", learned.Select(pair => pair.Key)));
        }
        catch (Exception error) { AppLog.Debug("Spotify queries not remembered: " + error.GetType().Name); }
    }

    /// The Spotify build the remembered queries belong to: Spotify.exe's own version. Not the
    /// archive's size and date, which change whenever xpui.spa is rewritten (Exo's own AMOLED
    /// theme does) while its queries stay the same; the archive is the fallback.
    private static string Build(string spotifyDirectory, FileInfo archive)
    {
        try
        {
            var exe = Path.Combine(spotifyDirectory, "Spotify.exe");
            if (File.Exists(exe) && FileVersionInfo.GetVersionInfo(exe).FileVersion is { Length: > 0 } version) return "v" + version;
        }
        catch (Exception) { }
        return archive.Length + ":" + archive.LastWriteTimeUtc.Ticks;
    }

    private static IEnumerable<KeyValuePair<string, string>> Remembered(string build)
    {
        try
        {
            if (!File.Exists(RememberedPath)) return [];
            using var document = JsonDocument.Parse(File.ReadAllText(RememberedPath));
            var root = document.RootElement;
            if (!root.TryGetProperty("build", out var saved) || saved.GetString() != build || !root.TryGetProperty("hashes", out var hashes) || hashes.ValueKind != JsonValueKind.Object) return [];
            return hashes.EnumerateObject()
                .Where(pair => pair.Value.ValueKind == JsonValueKind.String && Names.Contains(pair.Name) && Sha256().IsMatch(pair.Value.GetString()!))
                .Select(pair => KeyValuePair.Create(pair.Name, pair.Value.GetString()!))
                .ToArray();
        }
        catch (Exception) { return []; }
    }
}
