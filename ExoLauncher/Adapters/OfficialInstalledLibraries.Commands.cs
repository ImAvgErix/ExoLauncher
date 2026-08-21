using System.Diagnostics;
using ExoLauncher.Models;
using Microsoft.Win32;

namespace ExoLauncher.Adapters;

internal static partial class OfficialInstalledLibraries
{
    private static readonly TimeSpan UninstallPollInterval = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan InstallPollInterval = TimeSpan.FromSeconds(2);

    private static readonly string[] ClientUninstallNames =
    [
        "Xbox", "EA app", "EA Desktop", "Ubisoft Connect", "Ubisoft Game Launcher",
        "Battle.net", "Amazon Games", "Amazon Games App", "Rockstar Games Launcher",
        "itch", "Minecraft Launcher", "Minecraft", "Roblox Player", "Roblox",
        "Paradox Launcher", "Paradox Interactive", "Wargaming.net Game Center",
        "Wargaming Game Center",
    ];

    internal static Task<InstallResult> InstallAsync(GameEntry game, string displayName, CancellationToken ct) =>
        CommandAsync(game, displayName, uninstall: false, ct);

    internal static Task<InstallResult> UpdateAsync(GameEntry game, string displayName, CancellationToken ct)
    {
        var plan = PlanUpdate(game, displayName, IsStillInstalled(game));
        if (plan.UseInstallPath)
            return InstallAsync(game, displayName, ct);

        var opened = false;
        if (!string.IsNullOrWhiteSpace(plan.Protocol))
        {
            try
            {
                ProcessHelper.StartProtocol(plan.Protocol);
                opened = true;
            }
            catch
            {
                opened = TryOpenOfficialClient(game.Store);
            }
        }
        else
        {
            opened = TryOpenOfficialClient(game.Store);
        }

        var handoff = ClientHandoff(displayName, opened, "update");
        return Task.FromResult(handoff with { Path = game.Path, Message = opened ? plan.Message : handoff.Message });
    }

    internal static Task<InstallResult> UninstallAsync(GameEntry game, string displayName, CancellationToken ct) =>
        CommandAsync(game, displayName, uninstall: true, ct);

    internal readonly record struct OfficialUpdatePlan(
        bool UseInstallPath,
        string? Protocol,
        string Message);

    /// <summary>
    /// Installed official titles cannot reuse InstallAsync: that path returns
    /// "Already installed" and never talks to the vendor client.
    /// </summary>
    internal static OfficialUpdatePlan PlanUpdate(GameEntry game, string displayName, bool stillInstalled)
    {
        if (!stillInstalled)
            return new OfficialUpdatePlan(true, null, "");

        var protocol = UpdateProtocol(game.Store, game.LaunchTarget);
        return new OfficialUpdatePlan(
            false,
            protocol,
            protocol is null
                ? $"Opened {displayName} to continue the update."
                : $"Opened {displayName} to update {game.Title}.");
    }

    internal static string? UpdateProtocol(StoreKind store, string? launchTarget)
    {
        if (string.IsNullOrWhiteSpace(launchTarget)) return null;
        return InstallProtocol(store, launchTarget);
    }

    internal static bool IsStillInstalled(GameEntry game)
    {
        if (game.Store == StoreKind.Ea)
            return ScanEa().Any(row => SameGame(row, game));
        if (game.Store == StoreKind.Ubisoft)
            return ScanUbisoft().Any(row => SameGame(row, game));
        if (game.Store == StoreKind.Xbox)
            return ScanXbox().Any(row => SameGame(row, game));
        if (game.Store == StoreKind.BattleNet)
            return ScanBattleNet().Any(row => SameGame(row, game));
        if (game.Store == StoreKind.Amazon)
            return ScanAmazon().Any(row => SameGame(row, game));
        if (game.Store == StoreKind.Rockstar)
            return ScanRockstar().Any(row => SameGame(row, game));
        if (game.Store == StoreKind.Itch)
            return ScanItch().Any(row => SameGame(row, game));
        if (game.Store == StoreKind.Minecraft)
            return ScanMinecraft().Any(row => SameGame(row, game));
        if (game.Store == StoreKind.Roblox)
            return ScanRoblox().Any(row => SameGame(row, game));
        if (game.Store == StoreKind.Paradox)
            return ScanParadox().Any(row => SameGame(row, game));
        if (game.Store == StoreKind.Wargaming)
            return ScanWargaming().Any(row => SameGame(row, game));
        return Directory.Exists(game.Path);
    }

    internal static string? InstallProtocol(StoreKind store, string launchTarget)
    {
        if (string.IsNullOrWhiteSpace(launchTarget) || LooksLikeFilesystemTarget(launchTarget))
            return null;
        return store switch
        {
            StoreKind.Ea => "origin2://game/launch/?offerIds=" + Uri.EscapeDataString(launchTarget),
            StoreKind.Ubisoft => "uplay://install/" + Uri.EscapeDataString(launchTarget),
            StoreKind.BattleNet => "battlenet://" + Uri.EscapeDataString(launchTarget) + "/",
            StoreKind.Xbox => Storefront.LooksLikeMicrosoftStoreId(launchTarget)
                ? "ms-windows-store://pdp/?ProductId=" + Uri.EscapeDataString(launchTarget)
                : null,
            StoreKind.Wargaming => "wgc://open/game/" + Uri.EscapeDataString(launchTarget),
            StoreKind.Minecraft => MinecraftInstallProtocol(launchTarget),
            _ => null,
        };
    }

    internal static string? MinecraftInstallProtocol(string launchTarget)
    {
        if (launchTarget.Contains("MinecraftUWP", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(launchTarget, "minecraft:bedrock", StringComparison.OrdinalIgnoreCase))
            return "ms-windows-store://pdp/?PFN=Microsoft.MinecraftUWP_8wekyb3d8bbwe";
        if (string.Equals(launchTarget, "minecraft:java", StringComparison.OrdinalIgnoreCase) ||
            launchTarget.StartsWith("minecraft:", StringComparison.OrdinalIgnoreCase))
            return "minecraft://";
        return null;
    }

    internal static bool LooksLikeFilesystemTarget(string value) =>
        Storefront.LooksLikeFilesystemTarget(value);

    internal static string? UninstallProtocol(StoreKind store, string launchTarget) => store switch
    {
        StoreKind.Ubisoft => "uplay://uninstall/" + Uri.EscapeDataString(launchTarget),
        _ => null,
    };

    internal static bool PathsRelated(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;
        try
        {
            var a = Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var b = Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return a.Equals(b, StringComparison.OrdinalIgnoreCase) ||
                   a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                   b.StartsWith(a + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<InstallResult> CommandAsync(
        GameEntry game,
        string displayName,
        bool uninstall,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(game.LaunchTarget) && string.IsNullOrWhiteSpace(game.Path))
        {
            return new InstallResult
            {
                Ok = false,
                Message = uninstall
                    ? "This title has no install to remove."
                    : "This title has no install target.",
            };
        }

        if (uninstall && !IsStillInstalled(game))
            return new InstallResult { Ok = true, Message = "Already removed." };
        if (!uninstall && IsStillInstalled(game))
            return new InstallResult { Ok = true, Message = "Already installed.", Path = game.Path };

            if (!uninstall)
            {
                var uri = string.IsNullOrWhiteSpace(game.LaunchTarget)
                    ? null
                    : InstallProtocol(game.Store, game.LaunchTarget);
                if (uri is null)
                    return ClientHandoff(displayName, TryOpenOfficialClient(game.Store), "install");
            }

            using var hider = HiderFor(game.Store);
            hider.Start(uninstall ? TimeSpan.FromMinutes(20) : TimeSpan.FromHours(2), restoreOnStop: false);
            if (uninstall)
            {
                StoreUninstallPromptAutomator.Arm(
                    game.Title,
                    TimeSpan.FromMinutes(2),
                    ProcessNames(game.Store));
                if (!TryStartUninstall(game))
                {
                    return new InstallResult
                    {
                        Ok = false,
                        Message = $"{displayName} could not start removing this game. Open {displayName} to uninstall it there.",
                    };
                }
            }
            else
            {
                StartInstall(game);
            }

        var start = DateTimeOffset.UtcNow;
        var limit = uninstall ? TimeSpan.FromMinutes(20) : TimeSpan.FromHours(2);
        while (!ct.IsCancellationRequested)
        {
            var installed = IsStillInstalled(game);
            if (uninstall && !installed)
                return new InstallResult { Ok = true, Message = $"Removed through {displayName}." };
            if (!uninstall && installed)
                return new InstallResult { Ok = true, Message = $"Installed through {displayName}.", Path = game.Path };

            if (DateTimeOffset.UtcNow - start > limit)
            {
                return new InstallResult
                {
                    Ok = false,
                    Message = uninstall
                        ? $"{displayName} did not finish removing this game."
                        : $"{displayName} did not finish installing this game.",
                };
            }

            // Removal is confirmed by evidence disappearing, so keep that quick.
            // An install watch can run for two hours; re-scanning the registry
            // and the XboxGames folders every 800ms bought nothing.
            await Task.Delay(uninstall ? UninstallPollInterval : InstallPollInterval, ct)
                .ConfigureAwait(false);
        }

        return new InstallResult { Ok = false, Message = "Cancelled." };
    }

    private static void StartInstall(GameEntry game)
    {
        if (!string.IsNullOrWhiteSpace(game.LaunchTarget))
        {
            var uri = InstallProtocol(game.Store, game.LaunchTarget);
            if (uri is not null)
            {
                ProcessHelper.StartProtocol(uri);
                return;
            }
        }

        TryOpenOfficialClient(game.Store);
    }

    internal static InstallResult ClientHandoff(string displayName, bool opened, string action)
    {
        if (!opened)
        {
            return new InstallResult
            {
                Ok = false,
                Message = $"{displayName} is not installed, so Exo cannot {action} this title from here.",
            };
        }

        return new InstallResult
        {
            Ok = true,
            HandoffOnly = true,
            Message = $"Opened {displayName} to continue the {action}.",
        };
    }

    private static bool TryStartUninstall(GameEntry game)
    {
        if (game.Store == StoreKind.BattleNet &&
            TryStartBattleNetUninstall(game.LaunchTarget))
            return true;

        if (TryStartArpUninstall(game))
            return true;

        if (!string.IsNullOrWhiteSpace(game.LaunchTarget))
        {
            var uri = UninstallProtocol(game.Store, game.LaunchTarget);
            if (uri is not null)
            {
                try
                {
                    ProcessHelper.StartProtocol(uri);
                    return true;
                }
                catch
                {
                    /* fall through to the official client */
                }
            }
        }

        return TryOpenOfficialClient(game.Store);
    }

    private static bool TryStartBattleNetUninstall(string? uid)
    {
        if (string.IsNullOrWhiteSpace(uid)) return false;
        foreach (var record in ReadBattleNetUninstallRecords())
        {
            if (!string.Equals(record.Uid, uid, StringComparison.OrdinalIgnoreCase))
                continue;
            var uninstall = FindBattleNetUninstallString(record.Uid);
            if (string.IsNullOrWhiteSpace(uninstall)) continue;
            return TryStartUninstallString(uninstall);
        }

        return false;
    }

    private static string? FindBattleNetUninstallString(string uid)
    {
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
                        if (!uninstall.Contains("--uid=" + uid, StringComparison.OrdinalIgnoreCase) &&
                            !uninstall.Contains("--uid=\"" + uid, StringComparison.OrdinalIgnoreCase))
                            continue;
                        return uninstall;
                    }
                    catch { /* skip one */ }
                }
            }
            catch { /* hive unavailable */ }
        }

        return null;
    }

    internal static bool TryStartArpUninstall(GameEntry game)
    {
        if (string.IsNullOrWhiteSpace(game.Path) && string.IsNullOrWhiteSpace(game.Title))
            return false;

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
                        var display = entry?.GetValue("DisplayName") as string;
                        if (IsClientUninstallName(display)) continue;
                        var location = entry?.GetValue("InstallLocation") as string
                                       ?? entry?.GetValue("InstallLocation", null)?.ToString();
                        var uninstall = entry?.GetValue("UninstallString") as string
                                        ?? entry?.GetValue("UninstallString", null)?.ToString();
                        if (string.IsNullOrWhiteSpace(uninstall)) continue;
                        var pathMatch = PathsRelated(location, game.Path);
                        var titleMatch = !string.IsNullOrWhiteSpace(display) &&
                                         !string.IsNullOrWhiteSpace(game.Title) &&
                                         display.Trim().Equals(game.Title.Trim(), StringComparison.OrdinalIgnoreCase);
                        if (!pathMatch && !(titleMatch && !string.IsNullOrWhiteSpace(location)))
                            continue;
                        if (TryStartUninstallString(uninstall))
                            return true;
                    }
                    catch { /* skip one */ }
                }
            }
            catch { /* hive unavailable */ }
        }

        return false;
    }

    internal static bool TryStartUninstallString(string uninstallString)
    {
        var (fileName, arguments) = SplitCommand(uninstallString);
        if (string.IsNullOrWhiteSpace(fileName)) return false;
        try
        {
            if (fileName.Contains("msiexec", StringComparison.OrdinalIgnoreCase))
            {
                ProcessHelper.StartHidden(fileName, AppendQuiet(arguments));
                return true;
            }

            ProcessHelper.StartHidden(fileName, arguments);
            return true;
        }
        catch
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = uninstallString,
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true,
                });
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    internal static (string FileName, string Arguments) SplitCommand(string command)
    {
        var text = command.Trim();
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            if (end > 0)
                return (text[1..end], text[(end + 1)..].Trim());
        }

        var space = text.IndexOf(' ');
        return space < 0 ? (text, "") : (text[..space], text[(space + 1)..].Trim());
    }

    private static string AppendQuiet(string arguments)
    {
        if (arguments.Contains("/qn", StringComparison.OrdinalIgnoreCase) ||
            arguments.Contains("/quiet", StringComparison.OrdinalIgnoreCase))
            return arguments;
        return string.IsNullOrWhiteSpace(arguments) ? "/qn" : arguments + " /qn";
    }

    private static bool IsClientUninstallName(string? display) =>
        !string.IsNullOrWhiteSpace(display) &&
        ClientUninstallNames.Any(name => string.Equals(name, display.Trim(), StringComparison.OrdinalIgnoreCase));

    private static bool SameGame(GameEntry left, GameEntry right) =>
        string.Equals(left.Id, right.Id, StringComparison.OrdinalIgnoreCase) ||
        (!string.IsNullOrWhiteSpace(left.LaunchTarget) &&
         string.Equals(left.LaunchTarget, right.LaunchTarget, StringComparison.OrdinalIgnoreCase)) ||
        PathsRelated(left.Path, right.Path);

    internal static bool TryOpenOfficialClient(StoreKind store)
    {
        try
        {
            IOfficialStoreClient? client = store switch
            {
                StoreKind.Xbox => new XboxAdapter(),
                StoreKind.Ea => new EaAdapter(),
                StoreKind.Ubisoft => new UbisoftAdapter(),
                StoreKind.BattleNet => new BattleNetAdapter(),
                StoreKind.Amazon => new AmazonAdapter(),
                StoreKind.Rockstar => new RockstarAdapter(),
                StoreKind.Itch => new ItchAdapter(),
                StoreKind.Minecraft => new MinecraftAdapter(),
                StoreKind.Roblox => new RobloxAdapter(),
                StoreKind.Paradox => new ParadoxAdapter(),
                StoreKind.Wargaming => new WargamingAdapter(),
                _ => null,
            };
            var command = client?.GetClientLaunchCommand();
            if (command is null) return false;
            if (command.IsAppx)
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = command.FileName,
                    UseShellExecute = true,
                });
                return true;
            }

            ProcessHelper.StartHidden(command.FileName, command.Arguments);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static StoreWindowHider HiderFor(StoreKind store) => store switch
    {
        StoreKind.Xbox => StoreWindowHider.ForXbox(),
        StoreKind.Ea => StoreWindowHider.ForEa(),
        StoreKind.Ubisoft => StoreWindowHider.ForUbisoft(),
        StoreKind.BattleNet => StoreWindowHider.ForBattleNet(),
        StoreKind.Amazon => StoreWindowHider.ForAmazon(),
        StoreKind.Rockstar => StoreWindowHider.ForRockstar(),
        StoreKind.Itch => StoreWindowHider.ForItch(),
        StoreKind.Minecraft => StoreWindowHider.ForMinecraft(),
        StoreKind.Roblox => StoreWindowHider.ForRoblox(),
        StoreKind.Paradox => StoreWindowHider.ForParadox(),
        StoreKind.Wargaming => StoreWindowHider.ForWargaming(),
        _ => StoreWindowHider.ForAllStoreChrome(),
    };

    private static string[] ProcessNames(StoreKind store) => store switch
    {
        StoreKind.Xbox => StoreWindowHider.XboxClientProcessNames,
        StoreKind.Ea => StoreWindowHider.EaClientProcessNames,
        StoreKind.Ubisoft => StoreWindowHider.UbisoftClientProcessNames,
        StoreKind.BattleNet => StoreWindowHider.BattleNetClientProcessNames,
        StoreKind.Amazon => StoreWindowHider.AmazonClientProcessNames,
        StoreKind.Rockstar => StoreWindowHider.RockstarClientProcessNames,
        StoreKind.Itch => StoreWindowHider.ItchClientProcessNames,
        StoreKind.Minecraft => StoreWindowHider.MinecraftClientProcessNames,
        StoreKind.Roblox => StoreWindowHider.RobloxClientProcessNames,
        StoreKind.Paradox => StoreWindowHider.ParadoxClientProcessNames,
        StoreKind.Wargaming => StoreWindowHider.WargamingClientProcessNames,
        _ => StoreWindowHider.SteamMainProcessNames,
    };
}
