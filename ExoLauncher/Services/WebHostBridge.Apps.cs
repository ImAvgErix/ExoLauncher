using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>
/// System › Apps, Exo's uninstaller. apps.list: every program on this PC (<see cref="InstalledPrograms"/>); apps.icons:
/// their icons; apps.uninstall: one removed with its own uninstaller (or Windows', for a Store app), waited out to the end
/// (<see cref="UninstallRun"/>), then the deep clean's find (<see cref="LeftoverScan"/>) handed to the page to look at;
/// apps.scan: the same find for a program whose uninstaller is gone (Revo's forced uninstall); apps.clean: what the
/// user chose of a find deleted. A find stays on this side under a token: the page only names which of its items go, so
/// nothing the page says becomes a path. Removing and deleting are each asked in Exo's own window.
/// </summary>
public sealed partial class WebHostBridge
{
    private static readonly SemaphoreSlim AppsGate = new(1, 1);
    private static readonly ConcurrentDictionary<string, (string Name, List<Leftover> Items, DateTime At)> Finds = new();
    private static readonly ConcurrentDictionary<string, string?> Icons = new(StringComparer.OrdinalIgnoreCase);

    private object AppsList()
    {
        var programs = Programs();
        // Sizes the entries don't give, measured once (a few seconds at most), then the list again with them.
        if (InstalledPrograms.Measure(programs, TimeSpan.FromSeconds(3))) programs = Programs();
        return new
        {
            ok = true, programs = programs.Select(Shown), cleared = ClearedLog.All().Select(pair => new { name = ClearedName(pair.Key), summary = pair.Value }),
            // Edge is gone and Edge Update is told not to bring it back: Allow Edge again takes that back.
            edgeRemoved = EdgeRemoval.Blocked() && !programs.Any(EdgeRemoval.IsEdge),
        };
    }

    /// <summary>
    /// Every program but games: a game is uninstalled from its page in Library, by its store, and its traces cleared
    /// after (<see cref="ClearGameTraces"/>). A game is one in Library's folders, or a store's own entry for one (Steam's,
    /// Riot's and Ubisoft's uninstall links).
    /// </summary>
    private List<InstalledProgram> Programs()
    {
        var games = _services.Library.PeekCachedLibrary().Where(game => !string.IsNullOrWhiteSpace(game.Path)).Select(game => game.Path!.TrimEnd('\\') + '\\').ToList();
        return InstalledPrograms.List().Where(program => !IsGame(program, games)).ToList();
    }

    private static bool IsGame(InstalledProgram program, List<string> games) =>
        program.Uninstall is { } command && new[] { "steam://uninstall/", "--uninstall-product=", "uplay://uninstall/" }.Any(link => command.Contains(link, StringComparison.OrdinalIgnoreCase)) ||
        program.Key is { } key && new[] { @"\Steam App ", @"\Uplay Install " }.Any(store => key.Contains(store, StringComparison.OrdinalIgnoreCase)) ||
        program.Location is { } folder && games.Any(game => (folder.TrimEnd('\\') + '\\').StartsWith(game, StringComparison.OrdinalIgnoreCase));

    private static object Shown(InstalledProgram program) => new
    {
        id = program.Id,
        name = program.Name,
        publisher = program.Publisher,
        version = program.Version,
        installedOn = program.InstalledOn,
        bytes = program.Bytes is > 0 ? program.Bytes : null,
        store = program.Store,
        removable = program.Removable,
        uninstaller = program.Store || program.Uninstall is not null && UninstallerThere(program.Uninstall),
        care = program.Care,
        replaces = program.Replaces,
    };

    private static string ClearedName(string key) =>
        ReplacedApps.Known.FirstOrDefault(app => app.Id == key)?.Name ?? (key.StartsWith("app:", StringComparison.Ordinal) ? key[4..] : key);

    // Its uninstaller is on this PC: Windows Installer's (an MSI's command names MsiExec, which is always there), or the file
    // its command starts.
    private static bool UninstallerThere(string command)
    {
        var text = command.TrimStart().TrimStart('"');
        return text.StartsWith("msiexec", StringComparison.OrdinalIgnoreCase) || text.Contains(@"\msiexec", StringComparison.OrdinalIgnoreCase) || Rooted(command) is not null;
    }

    /// <summary>
    /// The command with its program's full path: a bare name (rundll32.exe advpack.dll,LaunchINFSection …, an INF-installed
    /// runtime's; Prounstl.exe, Intel's) is Windows' own, looked for only in System32 and the Windows folder, never on PATH.
    /// Null when its program isn't there.
    /// </summary>
    private static string? Rooted(string command)
    {
        var (file, arguments) = Adapters.OfficialInstalledLibraries.SplitCommand(command);
        file = Environment.ExpandEnvironmentVariables(file);
        if (Path.IsPathFullyQualified(file)) return File.Exists(file) ? command : null;
        if (file.Length == 0 || file.IndexOfAny(['\\', '/']) >= 0) return null;
        foreach (var folder in new[] { Environment.SystemDirectory, Environment.GetFolderPath(Environment.SpecialFolder.Windows) })
        {
            var full = Path.Combine(folder, file);
            if (!File.Exists(full) && File.Exists(full + ".exe")) full += ".exe";
            if (File.Exists(full)) return $"\"{full}\" {arguments}".TrimEnd();
        }
        return null;
    }

    private static async Task<object> AppsIconsAsync(JsonElement p, bool hasParams)
    {
        var ids = hasParams && p.ValueKind == JsonValueKind.Object && p.TryGetProperty("ids", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(id => id.ValueKind == JsonValueKind.String).Select(id => id.GetString()!).Take(400).ToHashSet() : [];
        var icons = new Dictionary<string, string?>();
        var all = InstalledPrograms.List();
        // An entry that names no icon gets one the way Installed apps finds it (ProgramIcons); the Start menu is read once.
        var menu = new Lazy<ProgramIcons.StartMenu>(() => new ProgramIcons.StartMenu());
        foreach (var program in all.Where(program => ids.Contains(program.Id)))
        {
            var path = program.Icon ?? await Task.Run(() => ProgramIcons.Find(program, all, menu.Value)).ConfigureAwait(false);
            icons[program.Id] = path is null ? null : Icons.TryGetValue(path, out var cached) ? cached : Icons[path] = await IconAsync(path).ConfigureAwait(false);
        }
        return new { ok = true, icons };
    }

    /// <summary>A program's icon at 32px as a data URL: Windows' own thumbnail of its icon file (an .exe's, an .ico, a Store logo).</summary>
    private static async Task<string?> IconAsync(string path)
    {
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(StoreLogo(path));
            using var thumbnail = await file.GetThumbnailAsync(Windows.Storage.FileProperties.ThumbnailMode.SingleItem, 32, Windows.Storage.FileProperties.ThumbnailOptions.ResizeThumbnail);
            if (thumbnail is null || thumbnail.Size == 0 || thumbnail.Size > 512 * 1024) return null;
            var bytes = new byte[thumbnail.Size];
            using var reader = new Windows.Storage.Streams.DataReader(thumbnail.GetInputStreamAt(0));
            await reader.LoadAsync((uint)thumbnail.Size);
            reader.ReadBytes(bytes);
            return $"data:{(string.IsNullOrEmpty(thumbnail.ContentType) ? "image/png" : thumbnail.ContentType)};base64,{Convert.ToBase64String(bytes)}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    // A Store logo is named without its scale: the file on disk is Logo.scale-100.png or Logo.targetsize-32.png.
    private static string StoreLogo(string path)
    {
        if (File.Exists(path) || Path.GetDirectoryName(path) is not { } folder || !Directory.Exists(folder)) return path;
        var stem = Path.GetFileNameWithoutExtension(path);
        return Directory.EnumerateFiles(folder, stem + ".*" + Path.GetExtension(path))
            .OrderBy(file => file.Contains("targetsize-32", StringComparison.OrdinalIgnoreCase) ? 0 : file.Contains("scale-100", StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .FirstOrDefault() ?? path;
    }

    private async Task<object> AppsUninstallAsync(string id, CancellationToken ct)
    {
        if (!await AppsGate.WaitAsync(0, ct).ConfigureAwait(false)) return new { ok = false, message = "Another app is being removed." };
        try
        {
            if (await Task.Run(() => InstalledPrograms.Find(id), ct).ConfigureAwait(true) is not { } program) return new { ok = false, message = "That app isn't on this PC anymore." };
            if (!program.Removable) return new { ok = false, message = Why(program) };
            var edge = EdgeRemoval.IsEdge(program);
            var how = program.Store ? "Windows removes it" : edge ? "Edge's own setup removes it (Windows asks once)" : $"{program.Name}'s own uninstaller opens";
            if (App.MainAppWindow is not { } asker || !await asker.ConfirmNativeAsync($"Uninstall {program.Name}?",
                    $"{how}. When it's done, Exo looks for what it left behind: its folders, registry entries, " +
                    $"shortcuts and what Windows keeps about it, and shows you before anything goes.{Warning(program)}", "Uninstall", danger: true).ConfigureAwait(true))
                return new { ok = false, declined = true, message = "Nothing was removed." };
            var trail = await Task.Run(() => TrailFor(program), ct).ConfigureAwait(true);
            AppLog.Info($"Apps: uninstalling {program.Name} ({(program.Store ? "Store" : program.ProductCode is null ? "own uninstaller" : "MSI")}).");
            string? problem;
            if (program.Store) problem = await new WindowsAppPackages().RemoveAsync(program.PackageFullName!, ct).ConfigureAwait(true) ? null : "Windows didn't remove it.";
            else if (edge) problem = await EdgeRemoval.RunAsync(program, ct).ConfigureAwait(true);
            else problem = await UninstallRun.RunAsync(Rooted(program.Uninstall!) ?? program.Uninstall!, TimeSpan.FromMinutes(60), ct).ConfigureAwait(true);
            // A store's game goes in its app after the uninstaller hands it over (Steam asks first): give it a minute and a half.
            for (var waited = 0; waited < 90 && await Task.Run(() => StillThere(program), ct).ConfigureAwait(true); waited += 3)
                await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(true);
            if (await Task.Run(() => StillThere(program), ct).ConfigureAwait(true))
            {
                AppLog.Info($"Apps: {program.Name} is still installed{(problem is null ? "" : $" ({problem})")}.");
                return new { ok = false, still = true, message = problem ?? $"{program.Name} is still installed: its uninstaller was closed before it finished." };
            }
            return await Task.Run(() => Found(program.Name, trail), ct).ConfigureAwait(true);
        }
        finally { AppsGate.Release(); }
    }

    /// <summary>apps.allowEdge: Edge Update may install Edge again (Exo told it not to when Edge was removed here).</summary>
    private static async Task<object> AppsAllowEdgeAsync(CancellationToken ct)
    {
        if (!EdgeRemoval.Blocked()) return new { ok = true };
        if (App.MainAppWindow is not { } asker || !await asker.ConfirmNativeAsync("Allow Edge again?",
                "Edge Update may install Microsoft Edge again, and so may Microsoft's installer or Windows. Exo doesn't install it.", "Allow", danger: false).ConfigureAwait(true))
            return new { ok = false, declined = true, message = "Nothing changed." };
        return await EdgeRemoval.AllowAgainAsync(ct).ConfigureAwait(true) && !EdgeRemoval.Blocked()
            ? new { ok = true } : new { ok = false, message = "Windows didn't allow it." };
    }

    /// <summary>A program whose uninstaller is gone: what is left of it, found as after an uninstall (its folder included).</summary>
    private static async Task<object> AppsScanAsync(string id, CancellationToken ct)
    {
        if (await Task.Run(() => InstalledPrograms.Find(id), ct).ConfigureAwait(true) is not { } program) return new { ok = false, message = "That app isn't on this PC anymore." };
        if (!program.Removable) return new { ok = false, message = Why(program) };
        if (program.Store || program.Uninstall is { } command && UninstallerThere(command))
            return new { ok = false, message = $"{program.Name} still has its uninstaller: uninstall it instead." };
        var trail = await Task.Run(() => TrailFor(program) with { Entries = program.Key is { } key ? [key] : [] }, ct).ConfigureAwait(true);
        return await Task.Run(() => Found(program.Name, trail), ct).ConfigureAwait(true);
    }

    private static object Found(string name, Trail trail)
    {
        var scan = new LeftoverScan(LeftoverScan.ThisPc());
        var items = scan.Find(trail, deep: true);
        var sizes = LeftoverScan.Sizes(items);
        var token = Guid.NewGuid().ToString("N");
        foreach (var old in Finds.Where(pair => pair.Value.At < DateTime.UtcNow.AddHours(-1)).Select(pair => pair.Key).ToList()) Finds.TryRemove(old, out _);
        Finds[token] = (name, items, DateTime.UtcNow);
        AppLog.Info($"Apps: {name} left {items.Count(item => !item.Likely)} things, {items.Count(item => item.Likely)} more by name.");
        return new
        {
            ok = true,
            token,
            name,
            items = items.Select((item, index) => new
            {
                id = index,
                kind = item.Kind.ToString().ToLowerInvariant(),
                path = Readable(item),
                bytes = sizes.GetValueOrDefault(item),
                likely = item.Likely,
                machine = item.Machine,
            }),
            sure = LeftoverScan.Summarize(items.Where(item => !item.Likely).ToList(), sizes),
            likely = LeftoverScan.Summarize(items.Where(item => item.Likely).ToList(), sizes),
        };
    }

    private async Task<object> AppsCleanAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        var token = ReadString(p, hasParams, "token") ?? "";
        if (!Finds.TryGetValue(token, out var find)) return new { ok = false, message = "Look for its leftovers again." };
        var chosen = hasParams && p.TryGetProperty("ids", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(id => id.ValueKind == JsonValueKind.Number).Select(id => id.GetInt32()).Where(id => id >= 0 && id < find.Items.Count).Distinct()
                .Select(id => find.Items[id]).ToList() : [];
        if (chosen.Count == 0) return new { ok = false, message = "Nothing was chosen." };
        var sizes = LeftoverScan.Sizes(chosen);
        if (App.MainAppWindow is not { } asker || !await asker.ConfirmNativeAsync($"Delete what {find.Name} left?", CleanBody(find.Name, chosen, sizes), "Delete", danger: true).ConfigureAwait(true))
            return new { ok = false, declined = true, message = "Nothing was deleted." };
        var result = await Task.Run(async () =>
        {
            var scan = new LeftoverScan(LeftoverScan.ThisPc());
            var backup = scan.Backup(find.Name, chosen);
            var removed = chosen.Where(item => !item.Machine).Where(scan.Delete).ToList();
            var machine = chosen.Where(item => item.Machine).ToList();
            var commands = machine.Select(scan.AdminCommand).OfType<string>().ToList();
            if (commands.Count > 0 && await ReplacedApps.RemoveAsAdminAsync(commands, ct).ConfigureAwait(false))
                removed.AddRange(machine.Where(item => !scan.Exists(item)));
            var left = chosen.Where(scan.Exists).ToList();
            return (Removed: removed, Left: left.Count, Why: scan.Why(left), Backup: backup);
        }, ct).ConfigureAwait(true);
        var cleared = LeftoverScan.Summarize(result.Removed, sizes);
        if (cleared.Count > 0) ClearedLog.Add("app:" + find.Name, cleared);
        Finds.TryRemove(token, out _);
        AppLog.Info($"Apps: cleared {result.Removed.Count} of what {find.Name} left; {result.Left} stayed{(result.Why is null ? "" : $" ({result.Why})")}" +
            $"{(result.Backup is null ? "" : "; registry saved first")}.");
        return new
        {
            ok = result.Left == 0,
            cleared,
            left = result.Left,
            message = result.Left == 0 ? null : $"{result.Left} {(result.Left == 1 ? "item" : "items")} stayed: {result.Why ?? LeftoverScan.InUse}.",
        };
    }

    /// <summary>
    /// What Exo's window asks before a clean: how much goes and how it comes back, and each one found by its name alone,
    /// listed (the 8 shortest, then how many more), so what isn't sure is seen, and can be left out, before it goes.
    /// </summary>
    internal static string CleanBody(string name, IReadOnlyCollection<Leftover> chosen, IReadOnlyDictionary<Leftover, long> sizes)
    {
        var body = $"Exo will delete {Said(LeftoverScan.Summarize(chosen, sizes))}. Files go to the Recycle Bin and registry entries are saved to a file first, so they can come back.";
        // What an administrator removes doesn't go to the Recycle Bin: rd and del delete for good.
        if (chosen.Any(item => item.Machine))
            body += " Windows will ask for permission for those in Program Files and the system's registry" +
                (chosen.Any(item => item.Machine && item.Kind is LeftoverKind.Folder or LeftoverKind.File or LeftoverKind.Shortcut) ? "; files there are deleted for good." : ".");
        var likely = chosen.Where(item => item.Likely).Select(Readable).OrderBy(path => path.Length).ThenBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
        if (likely.Count == 0) return body;
        return $"{body}\n\nFound by name only. Cancel and leave out any that aren't {name}'s:\n{string.Join('\n', likely.Take(8))}" +
            (likely.Count > 8 ? $"\nand {likely.Count - 8} more" : "");
    }

    // A leftover as the page shows it: its path, and a value after its key.
    private static string Readable(Leftover item) => item.Value is null ? item.Path : $"{item.Path} › {item.Value}";

    /// <summary>
    /// Who a program is, read before it goes: its folder (the deep clean's sure lead), its names (its folder's, and its
    /// own without version or bitness: leads by name only), the executables in its folder (crash reports), its publisher,
    /// its MSI product code, a Store app's family, and what else is installed (kept, with what holds it).
    /// </summary>
    private static Trail TrailFor(InstalledProgram program)
    {
        var home = program.Store || program.Location is null || !InstalledPrograms.Own(program.Location) ? null : program.Location;
        var names = new List<string>();
        if (home is not null && Path.GetFileName(home.TrimEnd('\\')) is { } folder && !FolderWords.Contains(folder)) names.Add(folder);
        if (BareName(program.Name) is { Length: > 0 } bare) names.Add(bare);
        var exes = new List<string>();
        if (home is not null)
        {
            try
            {
                exes.AddRange(Directory.EnumerateFiles(home, "*.exe", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 1, IgnoreInaccessible = true })
                    .Select(Path.GetFileName).OfType<string>()
                    .Where(exe => !exe.StartsWith("unins", StringComparison.OrdinalIgnoreCase) && !exe.Equals("Uninstall.exe", StringComparison.OrdinalIgnoreCase))
                    .Take(40));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        var others = InstalledPrograms.List().Where(other => other.Id != program.Id && !other.Store && other.Location is not null).Select(other => other.Location!);
        return new Trail(program.Name)
        {
            Homes = home is null ? [] : [home],
            Names = names.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Exes = exes,
            Ids = program.Store && program.Id.StartsWith("store:", StringComparison.Ordinal) ? [program.Id[6..] + '_'] : [],
            Entries = !program.Store && program.Key is { } key ? [key] : [],
            DisplayNames = program.Store ? [] : [program.Name],
            Publisher = program.Publisher,
            ProductCode = program.ProductCode,
            Keep = [.. others, PathHelper.AppDataDir, .. Adapters.RecompInstallations.DataFolders(), .. EdgeRemoval.IsEdge(program) ? EdgeRemoval.Shared() : []],
        };
    }

    // An install folder named for what it holds, not for the app (Edge's is ...\Edge\Application): never a name to look for.
    private static readonly HashSet<string> FolderWords = new(StringComparer.OrdinalIgnoreCase) { "Application", "App", "Apps", "bin", "Program", "Current", "Client", "Launcher", "x64", "x86" };

    /// <summary>"Stremio" of "Stremio version 5.0.27", "7-Zip" of "7-Zip 26.01 (x64)": a program's name without its version or bitness.</summary>
    internal static string BareName(string name)
    {
        var bare = Regex.Replace(name, @"\s*\((x64|x86|64-bit|32-bit|User|Machine)\)\s*", " ", RegexOptions.IgnoreCase);
        bare = Regex.Replace(bare, @"\s+(version\s+)?v?\d+(\.\d+)+.*$", "", RegexOptions.IgnoreCase);
        return bare.Trim();
    }

    // Still listed with its uninstaller there (a Store app: its package still there).
    private static bool StillThere(InstalledProgram program) =>
        InstalledPrograms.Find(program.Id) is { } now && (now.Store || now.Uninstall is { } command && UninstallerThere(command));

    private static string Why(InstalledProgram program) => EdgeRemoval.IsWebView(program.Name) ? "Exo and other apps run on WebView2: it stays." : program.Care switch
    {
        InstalledPrograms.Cares.Exo => "To remove Exo, use Windows' Installed apps.",
        InstalledPrograms.Cares.AntiCheat => "Anti-cheat goes with its game: Exo never removes it.",
        InstalledPrograms.Cares.Driver => "Your graphics driver is changed in System › Driver.",
        _ => "Windows doesn't allow removing it.",
    };

    private static string Warning(InstalledProgram program) => EdgeRemoval.IsEdge(program)
        ? " The WebView2 runtime stays (Exo and other apps run on it), and so does Edge Update, which keeps it current but won't install Edge again. A link Windows opens in Edge (from Widgets or Search) may not open."
        : program.Care switch
    {
        InstalledPrograms.Cares.Runtime => " Games and apps that need it stop working until it's installed again.",
        InstalledPrograms.Cares.Hardware => " The hardware it drives may stop working until it's installed again.",
        InstalledPrograms.Cares.Store => " Your games from it need it to start.",
        InstalledPrograms.Cares.ExoUses => " Exo uses it: what needs it stops working.",
        _ => "",
    };

    /// <summary>
    /// A game, for clearing what Windows keeps about it once its store has removed it: its folder (anything pointing into
    /// it), its executables (crash reports), a Steam game's link (its desktop shortcut is a steam://rungameid/ one), and
    /// every other game's and program's folder, kept. Null for a game without a folder of its own.
    /// </summary>
    private Trail? GameTrail(GameEntry game)
    {
        if (string.IsNullOrWhiteSpace(game.Path) || !Directory.Exists(game.Path) || !InstalledPrograms.Own(game.Path)) return null;
        var exes = new List<string>();
        try
        {
            exes.AddRange(Directory.EnumerateFiles(game.Path, "*.exe", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true })
                .Select(Path.GetFileName).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Take(40));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        var others = _services.Library.PeekCachedLibrary().Where(other => other.Id != game.Id && !string.IsNullOrWhiteSpace(other.Path)).Select(other => other.Path!)
            .Concat(InstalledPrograms.List().Where(program => !program.Store && program.Location is not null && !string.Equals(program.Location, game.Path, StringComparison.OrdinalIgnoreCase)).Select(program => program.Location!));
        return new Trail(game.Title)
        {
            Homes = [game.Path],
            Exes = exes,
            Ids = game.Store == StoreKind.Steam && game.Id.StartsWith("steam:", StringComparison.Ordinal) ? [$"steam://rungameid/{game.Id[6..]}"] : [],
            Keep = [.. others, .. Adapters.RecompInstallations.DataFolders()],
        };
    }

    /// <summary>
    /// Once a game is gone: Windows' records of it (shortcuts, compatibility, recent apps, UserAssist, Game Bar's entry,
    /// crash dumps) go, as after any uninstall here, without asking: they point at nothing now. Only this user's; its
    /// folder only when the store left it empty (some games keep saves and mods there); nothing found by name alone.
    /// </summary>
    private static void ClearGameTraces(string title, Trail trail)
    {
        try
        {
            var scan = new LeftoverScan(LeftoverScan.ThisPc());
            var homes = scan.Homes(trail.Homes);
            var found = scan.Find(trail, deep: false).Where(item => !item.Machine && !item.Likely &&
                !(item.Kind == LeftoverKind.Folder && homes.Any(home => string.Equals(home.TrimEnd('\\'), item.Path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) &&
                  Directory.EnumerateFiles(item.Path, "*", SearchOption.AllDirectories).Any())).ToList();
            if (found.Count == 0) return;
            scan.Backup(title, found);
            var gone = found.Count(scan.Delete);
            AppLog.Info($"Game removed: cleared {gone} of Windows' {found.Count} records of {title}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            AppLog.Debug("Game removed: its records stayed: " + ex.GetType().Name);
        }
    }
}
