using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
using ExoLauncher.Models;
using Microsoft.Win32;

namespace ExoLauncher.Adapters;

/// <summary>
/// Proven on-disk installs for official clients that Exo can launch without
/// inventing ownership. A missing folder is not a library row.
/// </summary>
internal static class OfficialInstalledLibraries
{
    internal static IReadOnlyList<GameEntry> ScanEa()
    {
        foreach (var path in EaInstallDatPaths())
        {
            try
            {
                if (!File.Exists(path)) continue;
                var json = File.ReadAllText(path);
                var games = ParseEaInstallDat(json, Directory.Exists);
                if (games.Count > 0) return games;
            }
            catch { /* next candidate */ }
        }
        return Array.Empty<GameEntry>();
    }

    internal static IReadOnlyList<GameEntry> ScanUbisoft()
    {
        var records = ReadUbisoftInstallRegistry();
        return ParseUbisoftInstalls(records, Directory.Exists);
    }

    internal static IReadOnlyList<GameEntry> ScanXbox()
    {
        var roots = new List<string>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                roots.Add(Path.Combine(drive.RootDirectory.FullName, "XboxGames"));
            }
            catch { /* skip */ }
        }
        return ScanXboxGamesFolders(roots, Directory.Exists, File.Exists);
    }

    internal static IReadOnlyList<GameEntry> ScanBattleNet() =>
        ParseBattleNetInstalls(ReadBattleNetUninstallRecords(), Directory.Exists);

    internal static IReadOnlyList<GameEntry> ScanAmazon() =>
        ScanAmazonFuelFolders(AmazonGameRoots(), Directory.Exists, File.Exists, path =>
        {
            try { return File.ReadAllText(path); }
            catch { return null; }
        });

    internal static IReadOnlyList<GameEntry> ScanRockstar() =>
        ParseRockstarInstalls(ReadRockstarInstallRecords(), Directory.Exists, File.Exists);

    internal static IReadOnlyList<GameEntry> ParseEaInstallDat(
        string json,
        Func<string, bool> directoryExists)
    {
        var games = new List<GameEntry>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("installInfos", out var infos) ||
                infos.ValueKind != JsonValueKind.Array)
                return games;

            foreach (var info in infos.EnumerateArray())
            {
                var path = ReadString(info, "baseInstallPath") ?? ReadString(info, "localInstallPath");
                if (string.IsNullOrWhiteSpace(path) || !directoryExists(path)) continue;
                var id = ReadString(info, "contentId") ?? ReadString(info, "softwareId");
                if (string.IsNullOrWhiteSpace(id)) continue;
                var title = ReadString(info, "displayName");
                if (string.IsNullOrWhiteSpace(title))
                    title = new DirectoryInfo(path).Name;
                if (string.IsNullOrWhiteSpace(title)) continue;
                games.Add(new GameEntry
                {
                    Id = "ea:" + SanitizeId(id),
                    Title = title.Trim(),
                    Store = StoreKind.Ea,
                    Installed = true,
                    Owned = true,
                    Path = path,
                    LaunchTarget = id,
                    Status = "Ready",
                    LaunchNote = "Launches through the EA app.",
                });
            }
        }
        catch { /* malformed install.dat */ }
        return games;
    }

    internal static IReadOnlyList<GameEntry> ParseUbisoftInstalls(
        IEnumerable<UbisoftInstallRecord> records,
        Func<string, bool> directoryExists)
    {
        var games = new List<GameEntry>();
        foreach (var record in records)
        {
            if (string.IsNullOrWhiteSpace(record.InstallId) ||
                string.IsNullOrWhiteSpace(record.InstallDir) ||
                !directoryExists(record.InstallDir))
                continue;
            var title = string.IsNullOrWhiteSpace(record.Title)
                ? new DirectoryInfo(record.InstallDir).Name
                : record.Title;
            if (string.IsNullOrWhiteSpace(title)) continue;
            games.Add(new GameEntry
            {
                Id = "ubisoft:" + SanitizeId(record.InstallId),
                Title = title.Trim(),
                Store = StoreKind.Ubisoft,
                Installed = true,
                Owned = true,
                Path = record.InstallDir,
                LaunchTarget = record.InstallId,
                Status = "Ready",
                LaunchNote = "Launches through Ubisoft Connect.",
            });
        }
        return games;
    }

    internal static IReadOnlyList<GameEntry> ScanXboxGamesFolders(
        IEnumerable<string> roots,
        Func<string, bool> directoryExists,
        Func<string, bool> fileExists)
    {
        var games = new List<GameEntry>();
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !directoryExists(root)) continue;
            string[] titles;
            try { titles = Directory.GetDirectories(root); }
            catch { continue; }
            foreach (var titleDir in titles)
            {
                var content = Path.Combine(titleDir, "Content");
                if (!directoryExists(content)) continue;
                var exe = FindXboxExecutable(content, fileExists);
                if (string.IsNullOrWhiteSpace(exe)) continue;
                var title = new DirectoryInfo(titleDir).Name;
                if (string.IsNullOrWhiteSpace(title) ||
                    title.Equals("Content", StringComparison.OrdinalIgnoreCase))
                    continue;
                games.Add(new GameEntry
                {
                    Id = "xbox:" + SanitizeId(title),
                    Title = title,
                    Store = StoreKind.Xbox,
                    Installed = true,
                    Owned = true,
                    Path = content,
                    LaunchTarget = exe,
                    Status = "Ready",
                    LaunchNote = "Launches the installed Xbox PC executable.",
                });
            }
        }
        return games;
    }

    internal static LaunchResult LaunchEa(GameEntry game)
    {
        if (string.IsNullOrWhiteSpace(game.LaunchTarget))
            return MissingTarget("EA app");
        return LaunchProtocol(
            "origin2://game/launch/?offerIds=" + Uri.EscapeDataString(game.LaunchTarget),
            "Launching through the EA app.");
    }

    internal static LaunchResult LaunchUbisoft(GameEntry game)
    {
        if (string.IsNullOrWhiteSpace(game.LaunchTarget))
            return MissingTarget("Ubisoft Connect");
        return LaunchProtocol(
            "uplay://launch/" + Uri.EscapeDataString(game.LaunchTarget) + "/0",
            "Launching through Ubisoft Connect.");
    }

    internal static LaunchResult LaunchXbox(GameEntry game)
    {
        if (string.IsNullOrWhiteSpace(game.LaunchTarget) || !File.Exists(game.LaunchTarget))
            return MissingTarget("Xbox");
        try
        {
            var start = Process.Start(new ProcessStartInfo
            {
                FileName = game.LaunchTarget,
                WorkingDirectory = Path.GetDirectoryName(game.LaunchTarget) ?? game.Path,
                UseShellExecute = true,
            });
            return new LaunchResult
            {
                Ok = true,
                Message = "Launching.",
                ProcessId = start?.Id,
            };
        }
        catch (Exception ex)
        {
            return new LaunchResult { Ok = false, Message = ex.Message };
        }
    }

    internal static LaunchResult LaunchBattleNet(GameEntry game)
    {
        if (string.IsNullOrWhiteSpace(game.LaunchTarget))
            return MissingTarget("Battle.net");
        return LaunchProtocol(
            "battlenet://" + Uri.EscapeDataString(game.LaunchTarget) + "/",
            "Launching through Battle.net.");
    }

    internal static LaunchResult LaunchAmazon(GameEntry game)
    {
        if (string.IsNullOrWhiteSpace(game.LaunchTarget) || !File.Exists(game.LaunchTarget))
            return MissingTarget("Amazon Games");
        return LaunchExecutable(game.LaunchTarget, game.Path, "Launching.");
    }

    internal static LaunchResult LaunchRockstar(GameEntry game)
    {
        var launcher = FindRockstarLauncher();
        if (!string.IsNullOrWhiteSpace(launcher) &&
            File.Exists(launcher) &&
            !string.IsNullOrWhiteSpace(game.Path) &&
            Directory.Exists(game.Path))
        {
            return LaunchExecutable(
                launcher,
                Path.GetDirectoryName(launcher),
                "Launching through Rockstar Games Launcher.",
                "-skipPatcherCheck -launchTitleInFolder=\"" + game.Path + "\"");
        }

        if (!string.IsNullOrWhiteSpace(game.LaunchTarget) && File.Exists(game.LaunchTarget))
            return LaunchExecutable(game.LaunchTarget, game.Path, "Launching.");
        return MissingTarget("Rockstar Games Launcher");
    }

    internal readonly record struct UbisoftInstallRecord(string InstallId, string InstallDir, string? Title);
    internal readonly record struct BattleNetInstallRecord(string Uid, string InstallDir, string Title);
    internal readonly record struct RockstarInstallRecord(string Title, string InstallDir);

    private static IEnumerable<string> EaInstallDatPaths()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        yield return Path.Combine(programData, "EA Desktop", "install.dat");
        yield return Path.Combine(programData, "Electronic Arts", "EA Desktop", "install.dat");
    }

    private static IReadOnlyList<UbisoftInstallRecord> ReadUbisoftInstallRegistry()
    {
        var records = new List<UbisoftInstallRecord>();
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        foreach (var path in new[]
                 {
                     @"SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs",
                     @"SOFTWARE\Ubisoft\Launcher\Installs",
                 })
        {
            try
            {
                using var key = hive.OpenSubKey(path);
                if (key is null) continue;
                foreach (var id in key.GetSubKeyNames())
                {
                    try
                    {
                        using var game = key.OpenSubKey(id);
                        var dir = game?.GetValue("InstallDir") as string
                                  ?? game?.GetValue("InstallDir", null)?.ToString();
                        if (string.IsNullOrWhiteSpace(dir)) continue;
                        records.Add(new UbisoftInstallRecord(id, dir, null));
                    }
                    catch { /* skip one */ }
                }
            }
            catch { /* hive unavailable */ }
        }
        return records;
    }

    private static string? FindXboxExecutable(string contentDir, Func<string, bool> fileExists)
    {
        var config = Path.Combine(contentDir, "MicrosoftGame.config");
        if (fileExists(config))
        {
            try
            {
                var xml = XDocument.Load(config);
                var name = xml.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "Executable")
                    ?.Attribute("Name")?.Value
                    ?? xml.Descendants()
                        .FirstOrDefault(e => e.Name.LocalName == "Executable")
                        ?.Attribute("ImageName")?.Value;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    var exe = Path.Combine(contentDir, name);
                    if (fileExists(exe)) return exe;
                }
            }
            catch { /* fall through to directory scan */ }
        }

        try
        {
            foreach (var exe in Directory.EnumerateFiles(contentDir, "*.exe", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileNameWithoutExtension(exe);
                if (name.Equals("gamelaunchhelper", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("SplashScreen", StringComparison.OrdinalIgnoreCase) ||
                    ProcessHelper.IsNonGameProcessName(name))
                    continue;
                return exe;
            }
        }
        catch { /* */ }
        return null;
    }

    internal static IReadOnlyList<GameEntry> ParseBattleNetInstalls(
        IEnumerable<BattleNetInstallRecord> records,
        Func<string, bool> directoryExists)
    {
        var games = new List<GameEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records)
        {
            if (string.IsNullOrWhiteSpace(record.Uid) ||
                string.IsNullOrWhiteSpace(record.InstallDir) ||
                !directoryExists(record.InstallDir))
                continue;
            var uid = record.Uid.Trim();
            if (!seen.Add(uid)) continue;
            var title = string.IsNullOrWhiteSpace(record.Title)
                ? BattleNetTitle(uid)
                : record.Title.Trim();
            if (string.IsNullOrWhiteSpace(title))
                title = new DirectoryInfo(record.InstallDir).Name;
            if (string.IsNullOrWhiteSpace(title)) continue;
            games.Add(new GameEntry
            {
                Id = "battlenet:" + SanitizeId(uid),
                Title = title,
                Store = StoreKind.BattleNet,
                Installed = true,
                Owned = true,
                Path = record.InstallDir,
                LaunchTarget = uid,
                Status = "Ready",
                LaunchNote = "Launches through Battle.net.",
            });
        }
        return games;
    }

    internal static IReadOnlyList<GameEntry> ScanAmazonFuelFolders(
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
            string[] dirs;
            try { dirs = Directory.GetDirectories(root); }
            catch { continue; }
            foreach (var dir in dirs)
            {
                var fuel = Path.Combine(dir, "fuel.json");
                if (!fileExists(fuel)) continue;
                var json = readText(fuel);
                if (string.IsNullOrWhiteSpace(json)) continue;
                var parsed = ParseAmazonFuel(json, dir, fileExists);
                if (parsed is null) continue;
                if (!seen.Add(parsed.Id)) continue;
                games.Add(parsed);
            }
        }
        return games;
    }

    internal static GameEntry? ParseAmazonFuel(
        string json,
        string installDir,
        Func<string, bool> fileExists)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var title = ReadString(root, "label") ?? ReadString(root, "Label")
                        ?? new DirectoryInfo(installDir).Name;
            if (string.IsNullOrWhiteSpace(title)) return null;
            var main = Prop(root, "main") ?? Prop(root, "Main");
            if (main is null) return null;
            var command = ReadString(main.Value, "command") ?? ReadString(main.Value, "Command");
            if (string.IsNullOrWhiteSpace(command)) return null;
            command = command.Trim().Trim('"');
            if (command.StartsWith("./", StringComparison.Ordinal) ||
                command.StartsWith(".\\", StringComparison.Ordinal))
                command = command[2..];
            var exe = Path.IsPathRooted(command)
                ? command
                : Path.GetFullPath(Path.Combine(installDir, command));
            if (!fileExists(exe)) return null;
            var folderId = new DirectoryInfo(installDir).Name;
            return new GameEntry
            {
                Id = "amazon:" + SanitizeId(folderId),
                Title = title.Trim(),
                Store = StoreKind.Amazon,
                Installed = true,
                Owned = true,
                Path = installDir,
                LaunchTarget = exe,
                Status = "Ready",
                LaunchNote = "Launches the installed Amazon Games executable.",
            };
        }
        catch
        {
            return null;
        }
    }

    internal static IReadOnlyList<GameEntry> ParseRockstarInstalls(
        IEnumerable<RockstarInstallRecord> records,
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
            if (IsRockstarClientFolder(full)) continue;
            var title = string.IsNullOrWhiteSpace(record.Title)
                ? new DirectoryInfo(full).Name
                : record.Title.Trim();
            if (string.IsNullOrWhiteSpace(title)) continue;
            var exe = FindRockstarExecutable(full, title, fileExists);
            if (string.IsNullOrWhiteSpace(exe)) continue;
            games.Add(new GameEntry
            {
                Id = "rockstar:" + SanitizeId(title),
                Title = title,
                Store = StoreKind.Rockstar,
                Installed = true,
                Owned = true,
                Path = full,
                LaunchTarget = exe,
                Status = "Ready",
                LaunchNote = "Launches through Rockstar Games Launcher.",
            });
        }
        return games;
    }

    private static IReadOnlyList<BattleNetInstallRecord> ReadBattleNetUninstallRecords()
    {
        var records = new List<BattleNetInstallRecord>();
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        foreach (var path in new[]
                 {
                     @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                     @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
                 })
        {
            try
            {
                using var key = hive.OpenSubKey(path);
                if (key is null) continue;
                foreach (var name in key.GetSubKeyNames())
                {
                    try
                    {
                        using var entry = key.OpenSubKey(name);
                        var uninstall = entry?.GetValue("UninstallString") as string
                                        ?? entry?.GetValue("UninstallString", null)?.ToString();
                        if (string.IsNullOrWhiteSpace(uninstall) ||
                            uninstall.IndexOf("Battle.net Uninstaller", StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                        var uid = ReadFlag(uninstall, "--uid=");
                        if (string.IsNullOrWhiteSpace(uid)) continue;
                        var dir = entry?.GetValue("InstallLocation") as string
                                  ?? entry?.GetValue("InstallLocation", null)?.ToString();
                        if (string.IsNullOrWhiteSpace(dir)) continue;
                        var title = entry?.GetValue("DisplayName") as string
                                    ?? ReadFlag(uninstall, "--displayname=")
                                    ?? BattleNetTitle(uid);
                        records.Add(new BattleNetInstallRecord(uid, dir, title ?? uid));
                    }
                    catch { /* skip one */ }
                }
            }
            catch { /* hive unavailable */ }
        }
        return records;
    }

    private static IEnumerable<string> AmazonGameRoots()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(local, "Amazon Games", "Data", "Games");
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        yield return Path.Combine(programData, "Amazon Games", "Installed");
    }

    private static IReadOnlyList<RockstarInstallRecord> ReadRockstarInstallRecords()
    {
        var records = new List<RockstarInstallRecord>();
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        foreach (var path in new[]
                 {
                     @"SOFTWARE\WOW6432Node\Rockstar Games",
                     @"SOFTWARE\Rockstar Games",
                 })
        {
            try
            {
                using var key = hive.OpenSubKey(path);
                if (key is null) continue;
                foreach (var name in key.GetSubKeyNames())
                {
                    if (IsRockstarClientName(name)) continue;
                    try
                    {
                        using var game = key.OpenSubKey(name);
                        var dir = game?.GetValue("InstallFolder") as string
                                  ?? game?.GetValue("Install Path") as string
                                  ?? game?.GetValue("InstallDir") as string;
                        if (string.IsNullOrWhiteSpace(dir)) continue;
                        records.Add(new RockstarInstallRecord(name, dir));
                    }
                    catch { /* skip one */ }
                }
            }
            catch { /* hive unavailable */ }
        }

        foreach (var root in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Rockstar Games"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Rockstar Games"),
                 })
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (var dir in Directory.GetDirectories(root))
                {
                    var name = new DirectoryInfo(dir).Name;
                    if (IsRockstarClientName(name)) continue;
                    records.Add(new RockstarInstallRecord(name, dir));
                }
            }
            catch { /* skip */ }
        }

        return records;
    }

    private static string? FindRockstarLauncher()
    {
        foreach (var path in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         "Rockstar Games", "Launcher", "Launcher.exe"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                         "Rockstar Games", "Launcher", "Launcher.exe"),
                 })
        {
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private static string? FindRockstarExecutable(string installDir, string title, Func<string, bool> fileExists)
    {
        string[] files;
        try { files = Directory.GetFiles(installDir, "*.exe", SearchOption.TopDirectoryOnly); }
        catch { return null; }

        string? fallback = null;
        foreach (var exe in files)
        {
            var name = Path.GetFileNameWithoutExtension(exe);
            if (ProcessHelper.IsNonGameProcessName(name) || IsRockstarHelperName(name))
                continue;
            if (!fileExists(exe)) continue;
            if (title.Contains(name, StringComparison.OrdinalIgnoreCase) ||
                name.Contains(title.Replace(" ", ""), StringComparison.OrdinalIgnoreCase))
                return exe;
            fallback ??= exe;
        }

        return fallback;
    }

    private static bool IsRockstarClientFolder(string path)
    {
        var name = new DirectoryInfo(path).Name;
        return IsRockstarClientName(name);
    }

    private static bool IsRockstarClientName(string name) =>
        name.Equals("Launcher", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Rockstar Games Launcher", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Social Club", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Rockstar Games Social Club", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Distribution", StringComparison.OrdinalIgnoreCase);

    private static bool IsRockstarHelperName(string name) =>
        name.Equals("PlayGTAV", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("LauncherPatcher", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("RockstarService", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("SocialClubHelper", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("SocialClub", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("uninstall", StringComparison.OrdinalIgnoreCase);

    private static string BattleNetTitle(string uid) => uid.ToLowerInvariant() switch
    {
        "wow" => "World of Warcraft",
        "wow_classic" => "World of Warcraft Classic",
        "wow_classic_era" => "World of Warcraft Classic Era",
        "wow_tbc" => "World of Warcraft Classic",
        "d3" => "Diablo III",
        "osi" => "Diablo II: Resurrected",
        "fenris" => "Diablo IV",
        "anbs" => "Diablo Immortal",
        "hs_beta" or "wtcg" => "Hearthstone",
        "s2" => "StarCraft II",
        "s1" => "StarCraft Remastered",
        "heroes" => "Heroes of the Storm",
        "prometheus" or "pro" => "Overwatch 2",
        "odin" => "Call of Duty",
        "vipr" => "Call of Duty: Modern Warfare",
        "lazr" => "Call of Duty: Modern Warfare II",
        "auks" => "Call of Duty: Modern Warfare III",
        "zeus" => "Call of Duty: Black Ops 6",
        "fore" => "Call of Duty: Vanguard",
        "rtro" => "Blizzard Arcade Collection",
        "wlby" => "Crash Bandicoot 4",
        "grtc" => "Warcraft Arclight Rumble",
        _ => uid,
    };

    private static string? ReadFlag(string command, string flag)
    {
        var start = command.IndexOf(flag, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        start += flag.Length;
        if (start >= command.Length) return null;
        if (command[start] == '"')
        {
            var end = command.IndexOf('"', start + 1);
            return end > start ? command[(start + 1)..end] : null;
        }
        var stop = command.IndexOf(' ', start);
        return stop < 0 ? command[start..] : command[start..stop];
    }

    private static JsonElement? Prop(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value : null;

    private static LaunchResult LaunchExecutable(
        string fileName,
        string? workingDirectory,
        string message,
        string arguments = "")
    {
        try
        {
            var start = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(fileName) ?? "",
                UseShellExecute = true,
            });
            return new LaunchResult
            {
                Ok = true,
                Message = message,
                ProcessId = start?.Id,
            };
        }
        catch (Exception ex)
        {
            return new LaunchResult { Ok = false, Message = ex.Message };
        }
    }

    private static LaunchResult LaunchProtocol(string uri, string message)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = uri, UseShellExecute = true });
            return new LaunchResult { Ok = true, Message = message };
        }
        catch (Exception ex)
        {
            return new LaunchResult { Ok = false, Message = ex.Message };
        }
    }

    private static LaunchResult MissingTarget(string client) => new()
    {
        Ok = false,
        Message = $"Exo can open {client}, but this title has no proven launch target.",
    };

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string SanitizeId(string value)
    {
        var chars = value.Trim().ToLowerInvariant().ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (!char.IsLetterOrDigit(chars[i]) && chars[i] is not '-' and not '_' and not '.')
                chars[i] = '-';
        }
        return new string(chars).Trim('-');
    }
}
