using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>
/// Best settings for a game: Exo's values for this PC written into the game's own settings file when Optimize is pressed
/// on the game's page (each group can also be put back on its own). Only the lines Exo knows change, each to a value the line itself lists as allowed
/// (an update that drops a choice drops that change); everything else stays as the game wrote it. Exo keeps what each
/// line held before (game-settings.json in its data folder, and the whole file as it first found it), so turning a
/// group off puts those back, unless the line was changed in the game since, which then stays. Nothing is written
/// while the game runs: it writes the file itself as it closes. Modern Warfare 4 first (GameSettings.CallOfDuty.cs).
/// </summary>
internal static partial class GameSettings
{
    /// <summary>
    /// A value for every line named <paramref name="Key"/> (Call of Duty lists some settings twice under different ids).
    /// Free: written as it is to a line that lists no allowed values (a refresh rate's "Auto:165.000"), never to one that does.
    /// </summary>
    internal sealed record Change(string Key, string Value, bool Free = false);

    /// <summary>Settings that go together (and are put back together).</summary>
    internal sealed record Group(string Id, string Label, IReadOnlyList<Change> Changes);

    /// <summary>
    /// A game Exo has best settings for: its name, its settings file, its processes, and its groups for this PC and what
    /// the file itself says (the graphics card the game found, the resolution it outputs).
    /// </summary>
    internal sealed record Profile(string Id, string Title, string File, Func<SettingsFile, IReadOnlyList<Group>> Groups, IReadOnlyList<string> Processes,
        Func<IReadOnlyList<string>>? PlayerFiles = null);

    /// <summary>One of a profile's files as read, with the key its kept lines go under ("" for the main file).</summary>
    internal sealed record OpenFile(string Path, string Key, SettingsFile File);

    /// <summary>
    /// A group as it stands. On: every line it changes holds Exo's value. Exo: Exo changed at least one of
    /// them, so turning it off puts something back (a group already set that way in the game has nothing to put back).
    /// </summary>
    internal sealed record GroupState(string Id, string Label, bool On, bool Exo);

    /// <summary>
    /// What the values depend on: the graphics card's maker, the render threads the processor suits, the video memory,
    /// how many screens there are, the main screen's highest refresh rate and whether it runs variable refresh, and
    /// whether you listen on headphones (Windows' default output is headphones or a headset, or Lucid is tuned for a pair).
    /// </summary>
    internal sealed record Pc(string? Vendor, int? RenderThreads, double? VideoMemoryGb, int Screens = 1, int TopRefresh = 0, bool Vrr = false,
        bool Headphones = false);

    /// <summary>One setting line: "Name@ids = value // allowed", where allowed is "one of a, b" or "low to high".</summary>
    internal sealed record Line(int Index, string Name, string Id, string Value, string? Allowed, int ValueStart, int ValueLength);

    /// <summary>A settings file as read: its lines (rejoined with its own line break), its BOM and its setting lines.</summary>
    internal sealed record SettingsFile(string[] Lines, string NewLine, bool Bom, IReadOnlyList<Line> Settings);

    /// <summary>What a line held before Exo changed it, and what Exo set.</summary>
    internal sealed record Remembered(string Was, string Set);

    /// <summary>The profile for a game, if Exo has one: by what its install folder holds.</summary>
    internal static Profile? For(GameEntry game, Pc pc)
    {
        if (!game.Installed || string.IsNullOrEmpty(game.Path)) return null;
        return Directory.Exists(Path.Combine(game.Path, "cod26")) ? ModernWarfare4(pc) : null;
    }

    /// <summary>
    /// The render threads a Call of Duty engine suits on this processor: its fast cores less one, which stays for the
    /// game's own main thread. Fast cores: a hybrid's performance cores (threads on E-cores stutter), and on a Ryzen with
    /// two CCDs one CCD's cores (the V-Cache one on an X3D: crossing CCDs costs more than it gives). At least 2, at most
    /// 16, the game's own range. Null when Windows doesn't say.
    /// </summary>
    internal static int? RenderThreads(MouseCore.Topology topology)
    {
        if (topology.Cores.Count == 0) return null;
        var fastest = topology.Cores.Max(core => core.Efficiency);
        var fast = topology.Cores.Where(core => core.Efficiency == fastest).ToList();
        if (topology.L3.Count > 1)
        {
            var ccd = topology.L3.OrderByDescending(cache => cache.Bytes).First().Mask;
            var inCcd = fast.Count(core => core.Threads.Length > 0 && core.Threads[0] < 64 && (ccd & (1UL << core.Threads[0])) != 0);
            if (inCcd > 0) return Math.Clamp(inCcd - 1, 2, 16);
        }
        return Math.Clamp(fast.Count - 1, 2, 16);
    }

    /// <summary>This PC's facts, read now.</summary>
    internal static Pc ThisPc()
    {
        var vendor = GraphicsService.Vendor();
        var screens = WindowsHdr.All();
        var top = WindowsHdr.Primary() is { } main && DisplayOutput.RefreshRates(main.GdiName).Rates is { Length: > 0 } rates ? rates[^1] : 0;
        return new(vendor, RenderThreads(MouseCore.Sampler.ReadTopology()), GpuCapability.VideoMemoryGb(), Math.Max(1, screens.Count), top,
            GpuProfileService.MainScreen(vendor).Vrr, ListensOnHeadphones());
    }

    private static bool ListensOnHeadphones()
    {
        try
        {
            if (Peripherals.DefaultEndpoints().Any(endpoint => !endpoint.Capture && endpoint.FormFactor is 3 or 5)) return true;
            return LucidSettings.Load(LucidSettings.FilePath) is { } root && LucidSettings.Read(root) is { HeadphoneChosen: true };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return false;
        }
    }

    internal static bool Running(Profile profile)
    {
        foreach (var name in profile.Processes)
        {
            var found = Process.GetProcessesByName(name);
            foreach (var process in found) process.Dispose();
            if (found.Length > 0) return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ reading and writing the game's file

    [GeneratedRegex(@"^(?<name>[A-Za-z_][A-Za-z0-9_]*)(?<ids>@[0-9;]+)?[ \t]*=[ \t]*(?<value>.*?)[ \t]*(?://[ \t]*(?<allowed>.*?))?[ \t]*$")]
    private static partial Regex SettingLine();

    [GeneratedRegex(@"^(-?\d+(?:\.\d+)?) to (-?\d+(?:\.\d+)?)$")]
    private static partial Regex Range();

    internal static SettingsFile Parse(byte[] bytes)
    {
        var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var text = new UTF8Encoding(false).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
        var newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Split(newLine);
        var settings = new List<Line>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("//", StringComparison.Ordinal) || SettingLine().Match(lines[i]) is not { Success: true } m) continue;
            var value = m.Groups["value"];
            settings.Add(new Line(i, m.Groups["name"].Value, m.Groups["name"].Value + m.Groups["ids"].Value, value.Value,
                m.Groups["allowed"].Success ? m.Groups["allowed"].Value : null, value.Index, value.Length));
        }
        return new SettingsFile(lines, newLine, bom, settings);
    }

    /// <summary>The file with the given lines' values changed, everything else byte for byte.</summary>
    internal static byte[] Write(SettingsFile file, IReadOnlyDictionary<int, string> values)
    {
        var lines = (string[])file.Lines.Clone();
        foreach (var line in file.Settings)
            if (values.TryGetValue(line.Index, out var value))
                lines[line.Index] = lines[line.Index][..line.ValueStart] + value + lines[line.Index][(line.ValueStart + line.ValueLength)..];
        var body = Encoding.UTF8.GetBytes(string.Join(file.NewLine, lines));
        return file.Bom ? [0xEF, 0xBB, 0xBF, .. body] : body;
    }

    /// <summary>
    /// The value as this line takes it: one of its listed choices (in the list's own spelling), a number in its range
    /// (written as the line writes its numbers), or true or false for a line that holds one; null when it can't.
    /// </summary>
    internal static string? Fit(Line line, string value)
    {
        if (line.Allowed is not { } allowed)
            return line.Value is "true" or "false" && value is "true" or "false" ? value : null;
        if (allowed.StartsWith("one of ", StringComparison.Ordinal))
            return allowed[7..].Split(", ").FirstOrDefault(choice => choice.Equals(value, StringComparison.OrdinalIgnoreCase));
        if (Range().Match(allowed) is not { Success: true } range ||
            !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ||
            number < double.Parse(range.Groups[1].Value, CultureInfo.InvariantCulture) ||
            number > double.Parse(range.Groups[2].Value, CultureInfo.InvariantCulture))
            return null;
        var decimals = line.Value.IndexOf('.', StringComparison.Ordinal) is var dot and >= 0 ? line.Value.Length - dot - 1 : 0;
        return number.ToString("F" + decimals, CultureInfo.InvariantCulture);
    }

    /// <summary>The lines a group changes in this file and the value each takes; a line that can't take Exo's value is left out.</summary>
    internal static List<(Line Line, string Value)> Targets(SettingsFile file, Group group)
    {
        var targets = new List<(Line, string)>();
        foreach (var change in group.Changes)
            foreach (var line in file.Settings)
                if (line.Name == change.Key && (change.Free ? line.Allowed is null ? change.Value : null : Fit(line, change.Value)) is { } value)
                    targets.Add((line, value));
        return targets;
    }

    /// <summary>Each group as it stands; groups with nothing to change in this version of the file are left out.</summary>
    internal static List<GroupState> States(Profile profile, SettingsFile file, IReadOnlyDictionary<string, Remembered> kept) =>
        States(profile, [new OpenFile(profile.File, "", file)], kept);

    /// <summary>Each group as it stands across the profile's files (the main one first, whose facts pick the groups).</summary>
    internal static List<GroupState> States(Profile profile, IReadOnlyList<OpenFile> files, IReadOnlyDictionary<string, Remembered> kept)
    {
        var states = new List<GroupState>();
        if (files.Count == 0) return states;
        foreach (var group in profile.Groups(files[0].File))
        {
            var targets = files.SelectMany(open => Targets(open.File, group).Select(t => (open.Key, t.Line, t.Value))).ToList();
            if (targets.Count == 0) continue;
            states.Add(new GroupState(group.Id, group.Label, targets.TrueForAll(t => t.Line.Value == t.Value),
                targets.Exists(t => kept.TryGetValue(t.Key + t.Line.Id, out var was) && was.Set == t.Line.Value)));
        }
        return states;
    }

    /// <summary>
    /// The edits that turn a group on or off, with what Exo now keeps. On: every line to Exo's value, remembering what
    /// it held the first time. Off: a line Exo set goes back to what it held, unless it was changed in the game since.
    /// </summary>
    internal static Dictionary<int, string> Plan(SettingsFile file, Group group, bool on, Dictionary<string, Remembered> kept, string key = "")
    {
        var edits = new Dictionary<int, string>();
        foreach (var (line, value) in Targets(file, group))
        {
            var id = key + line.Id;
            if (on)
            {
                if (line.Value == value) continue;
                kept[id] = kept.TryGetValue(id, out var was) ? was with { Set = value } : new Remembered(line.Value, value);
                edits[line.Index] = value;
            }
            else if (kept.Remove(id, out var was) && line.Value == was.Set)
                edits[line.Index] = was.Was;
        }
        return edits;
    }

    // ------------------------------------------------------------------ on this PC

    private static readonly Lock Gate = new();
    private static string Folder => Path.Combine(PathHelper.AppDataDir, "game-settings");
    private static string KeptPath => Path.Combine(Folder, "kept.json");

    /// <summary>The game's file as it is now; null when the game hasn't written one yet or it can't be read.</summary>
    internal static SettingsFile? Read(Profile profile)
    {
        try { return File.Exists(profile.File) ? Parse(File.ReadAllBytes(profile.File)) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"Game settings: {profile.Title}'s file couldn't be read: {ex.GetType().Name}");
            return null;
        }
    }

    /// <summary>
    /// The profile's files that exist, the main one first (none without it), each player file keyed by its name and the
    /// folder it sits in ("76561198000000001/g.cod26.1.0.l.txt|") so its kept lines never meet another's.
    /// </summary>
    internal static List<OpenFile> ReadAll(Profile profile)
    {
        if (Read(profile) is not { } main) return [];
        List<OpenFile> files = [new(profile.File, "", main)];
        foreach (var path in profile.PlayerFiles?.Invoke() ?? [])
        {
            try
            {
                if (File.Exists(path))
                    files.Add(new(path, $"{Path.GetFileName(Path.GetDirectoryName(path))}/{Path.GetFileName(path)}|", Parse(File.ReadAllBytes(path))));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Warn($"Game settings: a {profile.Title} player file couldn't be read: {ex.GetType().Name}");
            }
        }
        return files;
    }

    internal static Dictionary<string, Remembered> Kept(string profileId)
    {
        lock (Gate) return LoadKept().GetValueOrDefault(profileId) ?? [];
    }

    /// <summary>Turns a group on or off (all groups for null); false when the file couldn't be read or written.</summary>
    internal static bool Set(Profile profile, string? groupId, bool on)
    {
        lock (Gate)
        {
            var files = ReadAll(profile);
            if (files.Count == 0) return false;
            var all = LoadKept();
            var kept = all.GetValueOrDefault(profile.Id) ?? [];
            var groups = profile.Groups(files[0].File).Where(g => groupId is null || g.Id == groupId).ToList();
            var writes = new List<(OpenFile Open, Dictionary<int, string> Edits)>();
            foreach (var open in files)
            {
                var edits = new Dictionary<int, string>();
                foreach (var group in groups)
                    foreach (var (index, value) in Plan(open.File, group, on, kept, open.Key))
                        edits[index] = value;
                if (edits.Count > 0) writes.Add((open, edits));
            }
            if (writes.Count == 0) return true;
            try
            {
                Directory.CreateDirectory(Folder);
                foreach (var (open, edits) in writes)
                {
                    var name = open.Key.Length == 0 ? profile.Id + ".original" + Path.GetExtension(open.Path)
                        : profile.Id + "." + open.Key.TrimEnd('|').Replace('/', '.') + ".original";
                    var original = Path.Combine(Folder, name);
                    if (!File.Exists(original)) File.Copy(open.Path, original);
                    WriteFile(open.Path, Write(open.File, edits));
                }
                all[profile.Id] = kept;
                var temporary = KeptPath + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(all));
                File.Move(temporary, KeptPath, overwrite: true);
                AppLog.Info($"Game settings: {profile.Title}: {(groupId ?? "all")} {(on ? "on" : "off")}, {writes.Sum(w => w.Edits.Count)} lines in {writes.Count} files.");
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Warn($"Game settings: {profile.Title}'s file couldn't be written: {ex.GetType().Name}");
                return false;
            }
        }
    }

    /// <summary>Beside it, then over it, keeping it read-only if it was.</summary>
    private static void WriteFile(string path, byte[] bytes)
    {
        var attributes = File.GetAttributes(path);
        var temporary = path + ".exo-tmp";
        File.WriteAllBytes(temporary, bytes);
        if (attributes.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        File.Move(temporary, path, overwrite: true);
        if (attributes.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
    }

    private static Dictionary<string, Dictionary<string, Remembered>> LoadKept()
    {
        try
        {
            return File.Exists(KeptPath)
                ? JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, Remembered>>>(File.ReadAllText(KeptPath)) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Warn($"Game settings: what Exo kept couldn't be read: {ex.GetType().Name}");
            return [];
        }
    }
}
