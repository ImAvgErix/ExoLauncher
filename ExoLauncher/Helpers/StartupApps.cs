using Microsoft.Win32;

namespace ExoLauncher.Helpers;

/// <summary>An app whose start with Windows Exo makes redundant; <paramref name="PackageFamily"/> names its Microsoft Store edition.</summary>
internal sealed record StartupApp(string Id, string[] Executables, string? PackageFamily = null);

internal sealed record StartupAppState(string Id, bool Enabled);

/// <summary>
/// Store clients and apps that start with Windows although Exo starts them
/// (or does their job) when needed, so Exo turns each off by itself, quietly,
/// the way Task Manager's Startup apps page does: the vendor's own HKCU Run
/// value stays as it is and its StartupApproved\Run entry is marked disabled,
/// so the vendor cannot re-add it on its next start and turning it back on in
/// Task Manager restores it exactly. A Microsoft Store edition's startup task
/// is marked disabled by the user the same way. Each app is turned off once:
/// one the user turns back on afterwards stays on. The Epic Games Launcher
/// (2026) reads that same StartupApproved key itself. Only per-user entries:
/// machine-wide ones need admin rights, and Riot Vanguard's tray (HKLM) is
/// anti-cheat, which Exo never touches.
/// shortcut: shortcuts in the Startup folder are not handed off.
/// </summary>
internal static class StartupApps
{
    internal const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string ApprovedPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    /// <summary>Per package family, one subkey per startup task holding its State.</summary>
    internal const string PackagedTasksPath = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData";

    internal static readonly StartupApp[] Known =
    [
        new("steam", ["steam.exe"]),
        new("epic", ["EpicGamesLauncher.exe"]),
        new("ea", ["EADesktop.exe", "EALauncher.exe", "EABackgroundService.exe"]),
        new("ubisoft", ["upc.exe", "UbisoftConnect.exe"]),
        new("battlenet", ["Battle.net.exe", "Battle.net Launcher.exe"]),
        new("gog", ["GalaxyClient.exe"]),
        new("riot", ["RiotClientServices.exe"]),
        new("amazon", ["Amazon Games.exe", "Amazon Games UI.exe"]),
        // Exo has its own Discord; the app is only kept from starting.
        // Squirrel's entry runs Update.exe --processStart Discord.exe.
        new("discord", ["Discord.exe", "DiscordPTB.exe", "DiscordCanary.exe"]),
        new("spotify", ["Spotify.exe"], SpotifyInstalls.StoreFamily),
    ];

    /// <summary>Never matched, whatever else a command names.</summary>
    private static readonly string[] AntiCheat = ["vgtray.exe", "vgc.exe", "vgk.sys", "EasyAntiCheat.exe", "BEService.exe"];

    /// <summary>The file names a Run command starts or hands to its updater, in order.</summary>
    internal static IReadOnlyList<string> ExecutableNames(string? command)
    {
        var names = new List<string>();
        if (string.IsNullOrWhiteSpace(command)) return names;
        foreach (var token in Tokens(command))
        {
            if (!token.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            var name = token.Replace('/', '\\');
            var slash = name.LastIndexOf('\\');
            names.Add(slash >= 0 ? name[(slash + 1)..] : name);
        }
        return names;
    }

    private static IEnumerable<string> Tokens(string command)
    {
        var i = 0;
        while (i < command.Length)
        {
            while (i < command.Length && char.IsWhiteSpace(command[i])) i++;
            if (i >= command.Length) yield break;
            if (command[i] == '"')
            {
                var end = command.IndexOf('"', i + 1);
                if (end < 0) end = command.Length;
                yield return command[(i + 1)..end];
                i = end + 1;
                continue;
            }
            var start = i;
            while (i < command.Length && !char.IsWhiteSpace(command[i])) i++;
            var token = command[start..i];
            // An unquoted path with spaces: "C:\Program Files (x86)\Steam\steam.exe -silent".
            if (start == 0 && !token.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                var exe = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                if (exe > 0)
                {
                    i = exe + 4;
                    token = command[..i];
                }
            }
            yield return token;
        }
    }

    internal static StartupApp? Match(string? command)
    {
        var names = ExecutableNames(command);
        if (names.Any(name => AntiCheat.Contains(name, StringComparer.OrdinalIgnoreCase))) return null;
        return Known.FirstOrDefault(app => names.Any(name => app.Executables.Contains(name, StringComparer.OrdinalIgnoreCase)));
    }

    /// <summary>Windows reads an even first byte (or no entry) as on and an odd one as off.</summary>
    internal static bool IsEnabled(byte[]? approved) => approved is not { Length: > 0 } || (approved[0] & 1) == 0;

    /// <summary>What Task Manager writes: 2 (on) or 3 (off), then the time it was turned off.</summary>
    internal static byte[] Approved(bool enabled, DateTime utcNow)
    {
        var value = new byte[12];
        value[0] = enabled ? (byte)2 : (byte)3;
        if (!enabled) BitConverter.GetBytes(utcNow.ToFileTimeUtc()).CopyTo(value, 4);
        return value;
    }

    internal static IReadOnlyList<StartupAppState> Read(RegistryKey? run, RegistryKey? approved)
    {
        var states = new Dictionary<string, StartupAppState>(StringComparer.Ordinal);
        if (run is null) return [];
        foreach (var name in run.GetValueNames())
        {
            if (Match(run.GetValue(name) as string) is not { } app) continue;
            var enabled = IsEnabled(approved?.GetValue(name) as byte[]);
            // Two entries for one app: it starts if either is on.
            states[app.Id] = new StartupAppState(app.Id, enabled || (states.TryGetValue(app.Id, out var seen) && seen.Enabled));
        }
        return Known.Where(app => states.ContainsKey(app.Id)).Select(app => states[app.Id]).ToArray();
    }

    /// <summary>Turns every Run entry of <paramref name="id"/> on or off; false when it has none.</summary>
    internal static bool Set(RegistryKey? run, RegistryKey approved, string id, bool enabled, DateTime utcNow)
    {
        if (run is null) return false;
        var changed = false;
        foreach (var name in run.GetValueNames())
        {
            if (Match(run.GetValue(name) as string)?.Id != id) continue;
            approved.SetValue(name, Approved(enabled, utcNow), RegistryValueKind.Binary);
            changed = true;
        }
        return changed;
    }

    /// <summary>A Store app's startup task states (Windows.ApplicationModel.StartupTaskState).</summary>
    private const int TaskDisabledByUser = 1, TaskEnabled = 2;

    /// <summary>
    /// Turns off the start with Windows of every known app not in <paramref name="handed"/>
    /// (the ones Exo turned off before); returns the ids it turned off now.
    /// </summary>
    internal static IReadOnlyList<string> HandOff(RegistryKey? run, RegistryKey approved, RegistryKey? packages,
        IReadOnlySet<string> handed, DateTime utcNow)
    {
        var off = new List<string>();
        foreach (var state in Read(run, approved))
            if (state.Enabled && !handed.Contains(state.Id) && Set(run, approved, state.Id, enabled: false, utcNow))
                off.Add(state.Id);
        foreach (var app in Known)
        {
            if (app.PackageFamily is null || handed.Contains(app.Id)) continue;
            using var family = packages?.OpenSubKey(app.PackageFamily, writable: true);
            if (family is null) continue;
            foreach (var name in family.GetSubKeyNames())
            {
                using var task = family.OpenSubKey(name, writable: true);
                if (task?.GetValue("State") is not TaskEnabled) continue;
                task.SetValue("State", TaskDisabledByUser, RegistryValueKind.DWord);
                if (!off.Contains(app.Id)) off.Add(app.Id);
            }
        }
        return off;
    }

    /// <summary>
    /// Exo's uninstall: every app Exo turned off (<paramref name="handed"/>) and that is still off starts with Windows
    /// again, as Task Manager turns it back on. Returns the ids turned back on.
    /// </summary>
    internal static IReadOnlyList<string> GiveBack(IReadOnlySet<string> handed)
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunPath);
        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedPath, writable: true);
        using var packages = Registry.CurrentUser.OpenSubKey(PackagedTasksPath);
        var on = new List<string>();
        if (approved is not null)
            foreach (var state in Read(run, approved))
                if (!state.Enabled && handed.Contains(state.Id) && Set(run, approved, state.Id, enabled: true, DateTime.UtcNow)) on.Add(state.Id);
        foreach (var app in Known.Where(app => app.PackageFamily is not null && handed.Contains(app.Id)))
        {
            using var family = packages?.OpenSubKey(app.PackageFamily!, writable: true);
            foreach (var name in family?.GetSubKeyNames() ?? [])
            {
                using var task = family!.OpenSubKey(name, writable: true);
                if (task?.GetValue("State") is not TaskDisabledByUser) continue;
                task.SetValue("State", TaskEnabled, RegistryValueKind.DWord);
                if (!on.Contains(app.Id)) on.Add(app.Id);
            }
        }
        return on;
    }

    /// <summary>At Exo's start: every known app that still starts with Windows stops, once. Returns the ids turned off.</summary>
    internal static IReadOnlyList<string> HandOff(IReadOnlySet<string> handed)
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunPath);
            using var approved = Registry.CurrentUser.CreateSubKey(ApprovedPath);
            using var packages = Registry.CurrentUser.OpenSubKey(PackagedTasksPath);
            var off = HandOff(run, approved, packages, handed, DateTime.UtcNow);
            if (off.Count > 0) AppLog.Info("Startup: " + string.Join(", ", off) + " no longer start with Windows; Exo starts them when needed.");
            return off;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("Startup: apps could not be handed to Exo: " + ex.Message);
            return [];
        }
    }
}
