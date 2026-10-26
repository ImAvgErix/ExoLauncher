using System.Text.Json;
using ExoLauncher.Helpers;
using ExoLauncher.Models;
using Microsoft.Win32;

namespace ExoLauncher.Services;

/// <summary>
/// A PC with two graphics chips (a laptop's built-in one and its GeForce or Radeon) leaves Windows to pick which runs each game,
/// and it sometimes picks the slow one. Windows keeps the choice per program in the user's own registry
/// (<c>Settings > System > Display > Graphics</c>): <c>HKCU\Software\Microsoft\DirectX\UserGpuPreferences</c>, the program's full path
/// naming a string such as <c>GpuPreference=2;</c> (2 high performance). With the switch on, Exo writes that for the programs of a game
/// it starts, keeping what each value held before in <c>gpu-preference.json</c>, and Undo puts every one back. No administrator, no
/// change to a game's files, and never for a Riot game or an anti-cheat's own program.
/// </summary>
internal sealed class GpuPreference(GpuPreference.IStore store, string statePath)
{
    internal const string KeyPath = @"Software\Microsoft\DirectX\UserGpuPreferences";
    internal const int HighPerformance = 2;
    private const int MaxProgramsPerGame = 24;
    private static readonly object Gate = new();

    /// <summary>One program's value in the user's graphics preferences.</summary>
    internal interface IStore
    {
        string? Get(string program);
        void Set(string program, string data);
        void Delete(string program);
    }

    private sealed class RegistryStore : IStore
    {
        public string? Get(string program)
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue(program) as string;
        }

        public void Set(string program, string data)
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
            key.SetValue(program, data, RegistryValueKind.String);
        }

        public void Delete(string program)
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
            key?.DeleteValue(program, throwOnMissingValue: false);
        }
    }

    internal static GpuPreference Shared { get; } = new(new RegistryStore(), Path.Combine(PathHelper.AppDataDir, "gpu-preference.json"));

    /// <summary>The value's text with GpuPreference set to <paramref name="preference"/>, other entries (SwapEffectUpgradeEnable=1;) kept.</summary>
    internal static string With(string? data, int preference)
    {
        var parts = (data ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => !part.StartsWith("GpuPreference=", StringComparison.OrdinalIgnoreCase)).ToList();
        parts.Insert(0, "GpuPreference=" + preference);
        return string.Join(';', parts) + ";";
    }

    /// <summary>The GpuPreference in a value's text (0 Windows decides, 1 power saving, 2 high performance); null when it has none.</summary>
    internal static int? Preference(string? data)
    {
        foreach (var part in (data ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (part.StartsWith("GpuPreference=", StringComparison.OrdinalIgnoreCase) && int.TryParse(part["GpuPreference=".Length..], out var value))
                return value;
        return null;
    }

    /// <summary>Programs that are not the game's (an uninstaller, a crash reporter, a redistributable) or are anti-cheat's own.</summary>
    internal static bool IsGameProgram(string fileName)
    {
        if (!fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
        var name = Path.GetFileNameWithoutExtension(fileName);
        string[] helpers = ["unins", "uninst", "crash", "report", "setup", "install", "redist", "vcredist", "dxsetup", "dotnet", "directx", "ndp", "oalinst", "updater", "diagnos", "bugsplat", "sentry"];
        if (helpers.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase))) return false;
        return DlssSwapService.AntiCheatForFile(fileName) is null;
    }

    /// <summary>
    /// The programs that can be a game: its own launch program, then the programs in its folder and in the usual places below it
    /// (bin, Binaries\Win64, x64, Win64). Few and bounded: this is a preference per path, not a scan of the disk. None for a Riot game.
    /// </summary>
    internal static IReadOnlyList<string> ProgramsOf(GameEntry game)
    {
        if (game.Store == StoreKind.Riot || string.IsNullOrWhiteSpace(game.Path) || !Directory.Exists(game.Path)) return [];
        var found = new List<string>();
        void Add(string path)
        {
            try
            {
                var full = Path.GetFullPath(path);
                if (found.Count < MaxProgramsPerGame && IsGameProgram(Path.GetFileName(full)) && File.Exists(full) &&
                    PathHelper.IsUnderRoot(full, game.Path) && !found.Contains(full, StringComparer.OrdinalIgnoreCase))
                    found.Add(full);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { /* not a path */ }
        }
        if (game.LaunchTarget is { } target && target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) Add(target);
        foreach (var folder in new[] { "", "bin", "Bin", "x64", "Win64", @"Binaries\Win64", @"Binaries\WinGDK", @"bin\x64" })
        {
            try
            {
                var dir = Path.Combine(game.Path, folder);
                if (!Directory.Exists(dir)) continue;
                foreach (var file in Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly).Take(40)) Add(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a folder that can't be read has no programs here */ }
        }
        return found;
    }

    private sealed record State(Dictionary<string, string?> Previous);

    private State Load()
    {
        try
        {
            if (File.Exists(statePath) && new FileInfo(statePath).Length < 4 * 1024 * 1024 &&
                JsonSerializer.Deserialize<State>(File.ReadAllText(statePath)) is { Previous: { } previous })
                return new State(new Dictionary<string, string?>(previous, StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { AppLog.Warn("GPU preference record unreadable: " + ex.GetType().Name); }
        return new State(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
    }

    private void Save(State state)
    {
        var temp = statePath + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
            File.WriteAllText(temp, JsonSerializer.Serialize(state));
            File.Move(temp, statePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("GPU preference record not saved: " + ex.GetType().Name);
            try { File.Delete(temp); } catch (Exception inner) when (inner is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>High performance for these programs; how many were changed. What each held is kept first, once.</summary>
    internal int Use(IEnumerable<string> programs)
    {
        lock (Gate)
        {
            var state = Load();
            var changed = 0;
            foreach (var program in programs)
            {
                try
                {
                    var now = store.Get(program);
                    if (Preference(now) == HighPerformance) continue;
                    // Written before the record changes hands: a program with no record is never touched by Undo.
                    state.Previous.TryAdd(program, now);
                    Save(state);
                    store.Set(program, With(now, HighPerformance));
                    changed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    AppLog.Warn("GPU preference not set for one program: " + ex.GetType().Name);
                }
            }
            return changed;
        }
    }

    /// <summary>Every program Exo set goes back to what it held; one the user changed since is left as they have it.</summary>
    internal int Undo()
    {
        lock (Gate)
        {
            var state = Load();
            var restored = 0;
            foreach (var (program, previous) in state.Previous.ToList())
            {
                try
                {
                    if (Preference(store.Get(program)) == HighPerformance)
                    {
                        if (previous is null) store.Delete(program);
                        else store.Set(program, previous);
                        restored++;
                    }
                    state.Previous.Remove(program);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    AppLog.Warn("GPU preference not put back for one program: " + ex.GetType().Name);
                }
            }
            Save(state);
            return restored;
        }
    }
}
