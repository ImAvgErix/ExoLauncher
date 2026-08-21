using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using ExoLauncher.Models;

namespace ExoLauncher.Adapters;

internal static partial class OfficialInstalledLibraries
{
    internal static IReadOnlyList<GameEntry> ScanItch() =>
        ScanItchReceiptFolders(ItchInstallRoots(), Directory.Exists, File.Exists, TryReadAllBytes);

    internal static IReadOnlyList<GameEntry> ScanMinecraft() =>
        ParseMinecraftInstalls(
            MinecraftJavaRoot(),
            MinecraftBedrockEvidence(),
            MinecraftLauncherPath(),
            Directory.Exists,
            File.Exists);

    internal static IReadOnlyList<GameEntry> ScanRoblox() =>
        ParseRobloxInstalls(RobloxPlayerCandidates(), File.Exists);

    internal static IReadOnlyList<GameEntry> ScanParadox() =>
        ParseParadoxInstalls(ReadParadoxInstallRecords(), Directory.Exists, File.Exists);

    internal static IReadOnlyList<GameEntry> ScanWargaming() =>
        ScanWargamingGameInfo(WargamingGameRoots(), Directory.Exists, File.Exists, TryReadText);

    internal readonly record struct ParadoxInstallRecord(string Title, string InstallDir);
    internal readonly record struct RobloxPlayerRecord(string Path);

    internal static IEnumerable<string> ItchInstallRoots()
    {
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new List<string>
        {
            Path.Combine(user, "Games"),
            Path.Combine(user, "itch"),
        };
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                roots.Add(Path.Combine(drive.RootDirectory.FullName, "Games", "itch"));
                roots.Add(Path.Combine(drive.RootDirectory.FullName, "itch"));
            }
            catch { /* skip */ }
        }
        return roots;
    }

    internal static IReadOnlyList<GameEntry> ScanItchReceiptFolders(
        IEnumerable<string> roots,
        Func<string, bool> directoryExists,
        Func<string, bool> fileExists,
        Func<string, byte[]?> readBytes)
    {
        var games = new List<GameEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !directoryExists(root)) continue;
            foreach (var receipt in EnumerateItchReceipts(root, fileExists))
            {
                var parsed = ParseItchReceipt(receipt, Path.GetDirectoryName(receipt) ?? root, fileExists, readBytes);
                if (parsed is null || !seen.Add(parsed.Id)) continue;
                games.Add(parsed);
            }
        }
        return games;
    }

    internal static GameEntry? ParseItchReceipt(
        string receiptPath,
        string installDir,
        Func<string, bool> fileExists,
        Func<string, byte[]?> readBytes)
    {
        var json = ReadGzipJson(receiptPath, readBytes);
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var game = Prop(root, "game") ?? root;
            var title = ReadString(game, "title") ?? new DirectoryInfo(installDir).Name;
            if (string.IsNullOrWhiteSpace(title)) return null;
            var gameId = ReadNumberOrString(game, "id") ?? new DirectoryInfo(installDir).Name;
            var searchDir = Path.GetDirectoryName(receiptPath) is { } receiptDir &&
                            string.Equals(Path.GetFileName(receiptDir), ".itch", StringComparison.OrdinalIgnoreCase)
                ? Path.GetDirectoryName(receiptDir) ?? installDir
                : installDir;
            var exe = FindGameExecutable(searchDir, title, fileExists);
            if (string.IsNullOrWhiteSpace(exe)) return null;
            return new GameEntry
            {
                Id = "itch:" + SanitizeId(gameId),
                Title = title.Trim(),
                Store = StoreKind.Itch,
                Installed = true,
                Owned = true,
                Path = searchDir,
                LaunchTarget = exe,
                Status = "Ready",
                LaunchNote = "Launches the installed itch.io build.",
            };
        }
        catch
        {
            return null;
        }
    }

    internal static IReadOnlyList<GameEntry> ParseMinecraftInstalls(
        string? javaRoot,
        bool bedrockPresent,
        string? launcherPath,
        Func<string, bool> directoryExists,
        Func<string, bool> fileExists)
    {
        var games = new List<GameEntry>();
        if (!string.IsNullOrWhiteSpace(javaRoot) &&
            directoryExists(javaRoot) &&
            MinecraftHasVersion(javaRoot, directoryExists, fileExists))
        {
            var launchTarget = !string.IsNullOrWhiteSpace(launcherPath) && fileExists(launcherPath)
                ? launcherPath
                : "minecraft://";
            games.Add(new GameEntry
            {
                Id = "minecraft:java",
                Title = "Minecraft",
                Store = StoreKind.Minecraft,
                Installed = true,
                Owned = true,
                Path = javaRoot,
                LaunchTarget = launchTarget,
                Status = "Ready",
                LaunchNote = "Launches through the official Minecraft Launcher.",
            });
        }

        if (bedrockPresent)
        {
            games.Add(new GameEntry
            {
                Id = "minecraft:bedrock",
                Title = "Minecraft Bedrock",
                Store = StoreKind.Minecraft,
                Installed = true,
                Owned = true,
                Path = null,
                LaunchTarget = "shell:AppsFolder\\Microsoft.MinecraftUWP_8wekyb3d8bbwe!App",
                Status = "Ready",
                LaunchNote = "Launches the Microsoft Store Minecraft Bedrock package.",
            });
        }

        return games;
    }

    internal static IReadOnlyList<GameEntry> ParseRobloxInstalls(
        IEnumerable<string> playerCandidates,
        Func<string, bool> fileExists)
    {
        foreach (var candidate in playerCandidates)
        {
            if (string.IsNullOrWhiteSpace(candidate) || !fileExists(candidate)) continue;
            return
            [
                new GameEntry
                {
                    Id = "roblox:player",
                    Title = "Roblox",
                    Store = StoreKind.Roblox,
                    Installed = true,
                    Owned = true,
                    Path = Path.GetDirectoryName(candidate),
                    LaunchTarget = candidate,
                    Status = "Ready",
                    LaunchNote = "Launches the installed Roblox Player.",
                },
            ];
        }
        return Array.Empty<GameEntry>();
    }

    internal static IReadOnlyList<GameEntry> ParseParadoxInstalls(
        IEnumerable<ParadoxInstallRecord> records,
        Func<string, bool> directoryExists,
        Func<string, bool> fileExists)
    {
        var games = new List<GameEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records)
        {
            if (string.IsNullOrWhiteSpace(record.InstallDir) || !directoryExists(record.InstallDir))
                continue;
            var full = Path.GetFullPath(record.InstallDir);
            if (!seen.Add(full)) continue;
            if (IsParadoxClientFolder(full)) continue;
            var title = string.IsNullOrWhiteSpace(record.Title)
                ? new DirectoryInfo(full).Name
                : record.Title.Trim();
            if (string.IsNullOrWhiteSpace(title)) continue;
            var exe = FindGameExecutable(full, title, fileExists);
            if (string.IsNullOrWhiteSpace(exe)) continue;
            games.Add(new GameEntry
            {
                Id = "paradox:" + SanitizeId(title),
                Title = title,
                Store = StoreKind.Paradox,
                Installed = true,
                Owned = true,
                Path = full,
                LaunchTarget = exe,
                Status = "Ready",
                LaunchNote = "Launches the installed Paradox title.",
            });
        }
        return games;
    }

    internal static IReadOnlyList<GameEntry> ScanWargamingGameInfo(
        IEnumerable<string> roots,
        Func<string, bool> directoryExists,
        Func<string, bool> fileExists,
        Func<string, string?> readText)
    {
        var games = new List<GameEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !directoryExists(root)) continue;
            TryAddWargaming(games, seen, root, fileExists, readText);
            string[] dirs;
            try { dirs = Directory.GetDirectories(root); }
            catch { continue; }
            foreach (var dir in dirs)
                TryAddWargaming(games, seen, dir, fileExists, readText);
        }
        return games;
    }

    private static void TryAddWargaming(
        List<GameEntry> games,
        HashSet<string> seen,
        string dir,
        Func<string, bool> fileExists,
        Func<string, string?> readText)
    {
        var info = Path.Combine(dir, "game_info.xml");
        if (!fileExists(info)) info = Path.Combine(dir, "wgc_gameinfo.xml");
        if (!fileExists(info)) return;
        var parsed = ParseWargamingGameInfo(readText(info) ?? "", dir, fileExists);
        if (parsed is null || !seen.Add(parsed.Id)) return;
        games.Add(parsed);
    }

    internal static GameEntry? ParseWargamingGameInfo(
        string xml,
        string installDir,
        Func<string, bool> fileExists)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;
        try
        {
            var doc = XDocument.Parse(xml);
            var id = doc.Descendants().FirstOrDefault(e => e.Name.LocalName is "game_id" or "id")?.Value;
            var title = doc.Descendants().FirstOrDefault(e => e.Name.LocalName is "name" or "title")?.Value
                        ?? new DirectoryInfo(installDir).Name;
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title)) return null;
            var exe = FindGameExecutable(installDir, title, fileExists);
            return new GameEntry
            {
                Id = "wargaming:" + SanitizeId(id),
                Title = title.Trim(),
                Store = StoreKind.Wargaming,
                Installed = true,
                Owned = true,
                Path = installDir,
                LaunchTarget = string.IsNullOrWhiteSpace(exe) ? id : exe,
                Status = "Ready",
                LaunchNote = "Launches through Wargaming Game Center.",
            };
        }
        catch
        {
            return null;
        }
    }

    internal static LaunchResult LaunchItch(GameEntry game) =>
        LaunchExistingExecutable(game, "itch");

    internal static LaunchResult LaunchMinecraft(GameEntry game)
    {
        if (string.IsNullOrWhiteSpace(game.LaunchTarget))
            return MissingTarget("Minecraft Launcher");
        if (game.LaunchTarget.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) ||
            game.LaunchTarget.StartsWith("minecraft:", StringComparison.OrdinalIgnoreCase))
        {
            return game.LaunchTarget.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)
                ? LaunchAppx(game.LaunchTarget, "Launching Minecraft Bedrock.")
                : LaunchProtocol(game.LaunchTarget, "Launching through Minecraft Launcher.");
        }
        return LaunchExistingExecutable(game, "Minecraft Launcher");
    }

    internal static LaunchResult LaunchRoblox(GameEntry game) =>
        LaunchExistingExecutable(game, "Roblox");

    internal static LaunchResult LaunchParadox(GameEntry game) =>
        LaunchExistingExecutable(game, "Paradox Launcher");

    internal static LaunchResult LaunchWargaming(GameEntry game)
    {
        if (!string.IsNullOrWhiteSpace(game.LaunchTarget) && File.Exists(game.LaunchTarget))
            return LaunchExistingExecutable(game, "Wargaming Game Center");
        if (string.IsNullOrWhiteSpace(game.LaunchTarget))
            return MissingTarget("Wargaming Game Center");
        return LaunchProtocol(
            "wgc://open/game/" + Uri.EscapeDataString(game.LaunchTarget),
            "Launching through Wargaming Game Center.");
    }

    internal static string? MinecraftJavaRoot()
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var java = Path.Combine(roaming, ".minecraft");
        return Directory.Exists(java) ? java : null;
    }

    internal static bool MinecraftBedrockEvidence()
    {
        try
        {
            using var packages = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Classes\ActivatableClasses\Package");
            return packages?.GetSubKeyNames().Any(name =>
                name.StartsWith("Microsoft.MinecraftUWP_", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("Microsoft.MinecraftWindowsBeta_", StringComparison.OrdinalIgnoreCase))
                == true;
        }
        catch
        {
            return false;
        }
    }

    internal static string? MinecraftLauncherPath()
    {
        foreach (var path in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         "Minecraft Launcher", "MinecraftLauncher.exe"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                         "Minecraft Launcher", "MinecraftLauncher.exe"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "Programs", "Minecraft Launcher", "MinecraftLauncher.exe"),
                 })
        {
            if (File.Exists(path)) return path;
        }
        return null;
    }

    internal static IEnumerable<string> RobloxPlayerCandidates()
    {
        var versions = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Roblox", "Versions");
        if (Directory.Exists(versions))
        {
            string[] dirs;
            try { dirs = Directory.GetDirectories(versions); }
            catch { dirs = []; }
            foreach (var dir in dirs.OrderByDescending(Directory.GetLastWriteTimeUtc))
                yield return Path.Combine(dir, "RobloxPlayerBeta.exe");
        }

        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Roblox", "Versions", "RobloxPlayerBeta.exe");
    }

    internal static IEnumerable<string> WargamingGameRoots()
    {
        var roots = new List<string>
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Wargaming.net", "Games"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Wargaming.net", "Games"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Games", "Wargaming.net"),
        };
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                roots.Add(Path.Combine(drive.RootDirectory.FullName, "Games", "World_of_Tanks"));
                roots.Add(Path.Combine(drive.RootDirectory.FullName, "Games", "World_of_Warships"));
                roots.Add(Path.Combine(drive.RootDirectory.FullName, "Games", "World_of_Warplanes"));
            }
            catch { /* skip */ }
        }
        return roots;
    }

    private static IReadOnlyList<ParadoxInstallRecord> ReadParadoxInstallRecords()
    {
        var records = new List<ParadoxInstallRecord>();
        foreach (var hive in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
        foreach (var path in new[]
                 {
                     @"SOFTWARE\WOW6432Node\Paradox Interactive",
                     @"SOFTWARE\Paradox Interactive",
                 })
        {
            try
            {
                using var key = hive.OpenSubKey(path);
                if (key is null) continue;
                foreach (var name in key.GetSubKeyNames())
                {
                    if (IsParadoxClientName(name)) continue;
                    try
                    {
                        using var game = key.OpenSubKey(name);
                        var dir = game?.GetValue("InstallDir") as string
                                  ?? game?.GetValue("Install Location") as string
                                  ?? game?.GetValue("Path") as string;
                        if (string.IsNullOrWhiteSpace(dir)) continue;
                        records.Add(new ParadoxInstallRecord(name, dir));
                    }
                    catch { /* skip one */ }
                }
            }
            catch { /* hive unavailable */ }
        }

        foreach (var root in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         "Paradox Interactive"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                         "Paradox Interactive"),
                 })
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (var dir in Directory.GetDirectories(root))
                {
                    var name = new DirectoryInfo(dir).Name;
                    if (IsParadoxClientName(name)) continue;
                    records.Add(new ParadoxInstallRecord(name, dir));
                }
            }
            catch { /* skip */ }
        }

        return records;
    }

    private static IEnumerable<string> EnumerateItchReceipts(string root, Func<string, bool> fileExists)
    {
        var matches = new List<string>();
        void Consider(string dir)
        {
            var receipt = Path.Combine(dir, ".itch", "receipt.json.gz");
            if (fileExists(receipt)) matches.Add(receipt);
        }

        Consider(root);
        try
        {
            foreach (var dir in Directory.GetDirectories(root))
            {
                Consider(dir);
                if (matches.Count >= 256) break;
            }
        }
        catch { /* ACL */ }
        return matches;
    }

    private static bool MinecraftHasVersion(
        string javaRoot,
        Func<string, bool> directoryExists,
        Func<string, bool> fileExists)
    {
        var versions = Path.Combine(javaRoot, "versions");
        if (!directoryExists(versions)) return false;
        try
        {
            foreach (var dir in Directory.GetDirectories(versions))
            {
                var name = new DirectoryInfo(dir).Name;
                if (fileExists(Path.Combine(dir, name + ".json")))
                    return true;
            }
        }
        catch { /* skip */ }
        return false;
    }

    private static string? FindGameExecutable(string installDir, string title, Func<string, bool> fileExists)
    {
        string[] files;
        try { files = Directory.GetFiles(installDir, "*.exe", SearchOption.TopDirectoryOnly); }
        catch { return null; }

        string? fallback = null;
        foreach (var exe in files)
        {
            var name = Path.GetFileNameWithoutExtension(exe);
            if (ProcessHelper.IsNonGameProcessName(name)) continue;
            if (!fileExists(exe)) continue;
            if (title.Contains(name, StringComparison.OrdinalIgnoreCase) ||
                name.Contains(title.Replace(" ", ""), StringComparison.OrdinalIgnoreCase))
                return exe;
            fallback ??= exe;
        }

        if (fallback is not null) return fallback;
        try
        {
            foreach (var exe in Directory.EnumerateFiles(installDir, "*.exe", SearchOption.AllDirectories))
            {
                var name = Path.GetFileNameWithoutExtension(exe);
                if (ProcessHelper.IsNonGameProcessName(name)) continue;
                if (fileExists(exe)) return exe;
            }
        }
        catch { /* skip */ }
        return null;
    }

    private static bool IsParadoxClientFolder(string path) =>
        IsParadoxClientName(new DirectoryInfo(path).Name);

    private static bool IsParadoxClientName(string name) =>
        name.Contains("launcher", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Paradox Interactive", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Bootstrapper", StringComparison.OrdinalIgnoreCase);

    private static LaunchResult LaunchExistingExecutable(GameEntry game, string client)
    {
        if (string.IsNullOrWhiteSpace(game.LaunchTarget) || !File.Exists(game.LaunchTarget))
            return MissingTarget(client);
        return LaunchExecutable(game.LaunchTarget, game.Path, "Launching.");
    }

    private static LaunchResult LaunchAppx(string appsFolderTarget, string message)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = appsFolderTarget,
                UseShellExecute = true,
            });
            return new LaunchResult { Ok = true, Message = message };
        }
        catch (Exception ex)
        {
            return new LaunchResult { Ok = false, Message = ex.Message };
        }
    }

    private static string? ReadGzipJson(string path, Func<string, byte[]?> readBytes)
    {
        var bytes = readBytes(path);
        if (bytes is null || bytes.Length == 0) return null;
        try
        {
            using var input = new MemoryStream(bytes);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip);
            return reader.ReadToEnd();
        }
        catch
        {
            try { return System.Text.Encoding.UTF8.GetString(bytes); }
            catch { return null; }
        }
    }

    private static string? ReadNumberOrString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            _ => null,
        };
    }

    private static byte[]? TryReadAllBytes(string path)
    {
        try { return File.ReadAllBytes(path); }
        catch { return null; }
    }

    private static string? TryReadText(string path)
    {
        try { return File.ReadAllText(path); }
        catch { return null; }
    }
}
