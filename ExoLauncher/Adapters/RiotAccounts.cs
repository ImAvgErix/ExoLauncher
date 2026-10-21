using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ExoLauncher.Helpers;
using ExoLauncher.Services;

namespace ExoLauncher.Adapters;

/// <summary>
/// Riot accounts on this PC. The Riot Client keeps one "Stay signed in" session
/// in its Data folder (RiotGamesPrivateSettings.yaml, plus Sessions and Cookies
/// when it writes them), and established switchers swap exactly those files while
/// the Riot Client is closed. Exo does the same: each account's files are saved
/// DPAPI-wrapped (<see cref="ExoDpapi"/>) under Exo's data folder, readable only by
/// this Windows user, and restored only while the Riot Client and every Riot game
/// are closed. Exo never reads or stores a password, never sends a token anywhere,
/// never logs one, and never touches Vanguard or game files. The account's name and
/// PUUID come from the id_token Riot already wrote; the UI gets the name and a one-way key.
/// </summary>
internal static class RiotAccounts
{
    internal const string SettingsFile = "RiotGamesPrivateSettings.yaml";
    private static readonly string[] SessionFolders = ["Sessions", "Cookies"];
    private const int MaxFiles = 200;
    private const long MaxBytes = 16L * 1024 * 1024;
    private static readonly Lock Gate = new();

    internal sealed record Identity(string Puuid, string Name)
    {
        public string Key => KeyFor(Puuid);
    }

    internal sealed record Saved(string Key, string Puuid, string Name);

    internal static string LiveDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Riot Games", "Riot Client", "Data");

    internal static string StoreDir => Path.Combine(PathHelper.AppDataDir, "riot-accounts");

    internal static string KeyFor(string puuid)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes("riot-account\0" + puuid));
        return Convert.ToHexString(digest.AsSpan(0, 12)).ToLowerInvariant();
    }

    internal static bool IsKey(string? value) =>
        value is { Length: 24 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>
    /// The account a private-settings file is signed in to with Stay signed in:
    /// it needs a refresh token, and the id_token's <c>sub</c> (the PUUID) and
    /// <c>acct.game_name</c>/<c>tag_line</c> name it. Null without a saved session.
    /// </summary>
    internal static Identity? ReadIdentity(string? yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml)) return null;
        string? idToken = null;
        var hasRefresh = false;
        foreach (var raw in yaml.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("refresh_token:", StringComparison.Ordinal))
                hasRefresh = Value(line).Length > 0;
            else if (line.StartsWith("id_token:", StringComparison.Ordinal))
                idToken = Value(line);
        }
        if (!hasRefresh || string.IsNullOrEmpty(idToken)) return null;
        var parts = idToken.Split('.');
        if (parts.Length < 2) return null;
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload += new string('=', (4 - payload.Length % 4) % 4);
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            var root = doc.RootElement;
            if (!root.TryGetProperty("sub", out var sub) || sub.GetString() is not { Length: > 0 } puuid) return null;
            string? name = null;
            if (root.TryGetProperty("acct", out var acct) && acct.ValueKind == JsonValueKind.Object &&
                acct.TryGetProperty("game_name", out var game) && game.GetString() is { Length: > 0 } gameName)
            {
                name = acct.TryGetProperty("tag_line", out var tag) && tag.GetString() is { Length: > 0 } tagLine
                    ? gameName + "#" + tagLine
                    : gameName;
            }
            return new Identity(puuid, name ?? "Riot account");
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException)
        {
            return null;
        }

        static string Value(string line) => line[(line.IndexOf(':') + 1)..].Trim().Trim('"', '\'');
    }

    internal static Identity? ReadLive(string dataDir) =>
        ReadIdentity(SteamPlaytime.ReadSharedText(Path.Combine(dataDir, SettingsFile)));

    /// <summary>Every saved account, by name. Unreadable files (another Windows user's) are skipped.</summary>
    internal static IReadOnlyList<Saved> List(string storeDir)
    {
        var result = new List<Saved>();
        try
        {
            if (!Directory.Exists(storeDir)) return result;
            foreach (var path in Directory.EnumerateFiles(storeDir, "*.bin"))
            {
                var key = Path.GetFileNameWithoutExtension(path);
                if (!IsKey(key) || ReadSnapshot(path) is not { } snapshot || snapshot.Key != key) continue;
                result.Add(new Saved(key, snapshot.Puuid, snapshot.Name));
            }
        }
        catch (Exception ex) { AppLog.Debug("Riot saved accounts unreadable: " + ex.GetType().Name); }
        return result.OrderBy(account => account.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private sealed record Snapshot(string Key, string Puuid, string Name, Dictionary<string, byte[]> Files);

    /// <summary>
    /// Saves the Riot Client's current session for its account (replacing that
    /// account's older copy, since Riot rotates the refresh token). Call it only
    /// while the Riot Client is closed, or the copy may be mid-write.
    /// </summary>
    internal static Identity? SaveLive(string dataDir, string storeDir)
    {
        lock (Gate)
        {
            var settings = Path.Combine(dataDir, SettingsFile);
            var settingsBytes = ReadBytes(settings);
            var identity = settingsBytes is null ? null : ReadIdentity(Encoding.UTF8.GetString(settingsBytes));
            if (identity is null) return null;

            var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase) { [SettingsFile] = settingsBytes! };
            long total = settingsBytes!.Length;
            foreach (var folder in SessionFolders)
            {
                var dir = Path.Combine(dataDir, folder);
                if (!Directory.Exists(dir)) continue;
                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    var bytes = ReadBytes(file);
                    if (bytes is null) continue;
                    total += bytes.Length;
                    if (files.Count >= MaxFiles || total > MaxBytes)
                    {
                        AppLog.Warn("Riot: the session is larger than Exo saves; account not saved.");
                        return null;
                    }
                    files[Path.GetRelativePath(dataDir, file).Replace('\\', '/')] = bytes;
                }
            }

            var json = Serialize(identity, files);
            try
            {
                Directory.CreateDirectory(storeDir);
                var path = Path.Combine(storeDir, identity.Key + ".bin");
                var tmp = path + ".tmp";
                File.WriteAllBytes(tmp, ExoDpapi.Protect(json));
                try { SecretFileAcl.RestrictToCurrentUser(tmp); }
                catch (Exception aclEx) { AppLog.Debug("Riot saved account ACL: " + aclEx.GetType().Name); }
                File.Move(tmp, path, overwrite: true);
                return identity;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(json);
                foreach (var bytes in files.Values) CryptographicOperations.ZeroMemory(bytes);
            }
        }
    }

    /// <summary>
    /// Replaces the Riot Client's session files with the saved account's. The
    /// Riot Client and its games must be closed. Only the session files change.
    /// </summary>
    internal static bool Restore(string dataDir, string storeDir, string key)
    {
        lock (Gate)
        {
            if (!IsKey(key) || ReadSnapshot(Path.Combine(storeDir, key + ".bin")) is not { } snapshot) return false;
            try
            {
                var root = Path.GetFullPath(dataDir);
                var targets = snapshot.Files.Select(pair => (Path: Path.GetFullPath(Path.Combine(root, pair.Key)), pair.Value)).ToList();
                // A saved path must stay inside the Data folder and be one of the session files.
                if (targets.Any(target => !IsSessionPath(root, target.Path))) return false;
                ClearLive(dataDir);
                foreach (var (path, bytes) in targets)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    var tmp = path + ".exo-tmp";
                    File.WriteAllBytes(tmp, bytes);
                    File.Move(tmp, path, overwrite: true);
                }
                return true;
            }
            finally
            {
                foreach (var bytes in snapshot.Files.Values) CryptographicOperations.ZeroMemory(bytes);
            }
        }
    }

    /// <summary>Removes the session files so the Riot Client asks for a sign-in. Save first.</summary>
    internal static void ClearLive(string dataDir)
    {
        var settings = Path.Combine(dataDir, SettingsFile);
        if (File.Exists(settings)) File.Delete(settings);
        foreach (var folder in SessionFolders)
        {
            var dir = Path.Combine(dataDir, folder);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    internal static bool Remove(string storeDir, string key)
    {
        lock (Gate)
        {
            if (!IsKey(key)) return false;
            var path = Path.Combine(storeDir, key + ".bin");
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
    }

    /// <summary>
    /// The PUUID VALORANT last signed in with (<c>[UserInfo] LastKnownUser</c> in
    /// RiotLocalMachine.ini). League keeps no PUUID on disk, so it is not known.
    /// </summary>
    internal static string? ReadValorantLastUser(string? ini)
    {
        if (string.IsNullOrWhiteSpace(ini)) return null;
        foreach (var raw in ini.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("LastKnownUser=", StringComparison.OrdinalIgnoreCase))
                return line["LastKnownUser=".Length..].Trim() is { Length: > 0 } value ? value : null;
        }
        return null;
    }

    internal static string ValorantMachineIni => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VALORANT", "Saved", "Config", "WindowsClient", "RiotLocalMachine.ini");

    private static bool IsSessionPath(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)) return false;
        if (string.Equals(relative, SettingsFile, StringComparison.OrdinalIgnoreCase)) return true;
        var first = relative.Split(Path.DirectorySeparatorChar)[0];
        return SessionFolders.Any(folder => string.Equals(folder, first, StringComparison.OrdinalIgnoreCase)) &&
               relative.Length > first.Length + 1;
    }

    private static byte[]? ReadBytes(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaxBytes) return null;
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            return bytes;
        }
        catch { return null; }
    }

    private static byte[] Serialize(Identity identity, Dictionary<string, byte[]> files)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", 1);
            writer.WriteString("puuid", identity.Puuid);
            writer.WriteString("name", identity.Name);
            writer.WriteStartObject("files");
            foreach (var (path, bytes) in files) writer.WriteBase64String(path, bytes);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    private static Snapshot? ReadSnapshot(string path)
    {
        byte[]? json = null;
        try
        {
            if (!File.Exists(path)) return null;
            json = ExoDpapi.Unprotect(File.ReadAllBytes(path));
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("v", out var v) || v.GetInt32() != 1) return null;
            var puuid = root.GetProperty("puuid").GetString();
            var name = root.GetProperty("name").GetString();
            if (string.IsNullOrEmpty(puuid) || string.IsNullOrEmpty(name)) return null;
            var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in root.GetProperty("files").EnumerateObject())
                files[file.Name] = file.Value.GetBytesFromBase64();
            if (!files.ContainsKey(SettingsFile)) return null;
            return new Snapshot(KeyFor(puuid), puuid, name, files);
        }
        catch (Exception ex)
        {
            AppLog.Debug("Riot saved account unreadable: " + ex.GetType().Name);
            return null;
        }
        finally
        {
            if (json is not null) CryptographicOperations.ZeroMemory(json);
        }
    }

    /// <summary>A Riot game or its client is running (LeagueClient counts): the Riot Client must not close.</summary>
    internal static string? RunningGame()
    {
        foreach (var (productId, title) in Cli.RiotCli.FixedCatalog)
        {
            if (RiotAdapter.LaunchReadyProcessNames(productId).Concat(RiotAdapter.GameProcessNames(productId))
                .Any(ProcessHelper.IsProcessRunning))
                return title;
        }
        return null;
    }
}
