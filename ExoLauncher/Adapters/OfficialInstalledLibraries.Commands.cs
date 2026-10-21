using System.Diagnostics;
using ExoLauncher.Helpers;
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

        var protocol = UpdateProtocol(game.Store, XboxStoreId(game) ?? game.LaunchTarget);
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
            // EA publishes no verified install/update URI here; launch is a
            // different operation. Offer its client for an explicit handoff.
            StoreKind.Ea => null,
            StoreKind.Ubisoft => "uplay://install/" + Uri.EscapeDataString(launchTarget),
            StoreKind.BattleNet => "battlenet://" + Uri.EscapeDataString(launchTarget) + "/",
            StoreKind.Xbox => Storefront.LooksLikeMicrosoftStoreId(launchTarget)
                ? "ms-windows-store://pdp/?ProductId=" + Uri.EscapeDataString(launchTarget)
                : null,
            StoreKind.Wargaming => "wgc://open/game/" + Uri.EscapeDataString(launchTarget),
            StoreKind.Minecraft => MinecraftInstallProtocol(launchTarget),
            StoreKind.Roblox => RobloxInstallProtocol(launchTarget),
            _ => null,
        };
    }

    internal static string? MinecraftInstallProtocol(string launchTarget)
    {
        if (launchTarget.Contains("MinecraftUWP", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(launchTarget, "minecraft:bedrock", StringComparison.OrdinalIgnoreCase))
            return "ms-windows-store://pdp/?PFN=Microsoft.MinecraftUWP_8wekyb3d8bbwe";
        if (string.Equals(launchTarget, "minecraft:java", StringComparison.OrdinalIgnoreCase))
            return "https://www.minecraft.net/en-us/download";
        if (launchTarget.StartsWith("minecraft:", StringComparison.OrdinalIgnoreCase))
            return "minecraft://";
        return null;
    }

    internal static string? RobloxInstallProtocol(string launchTarget)
    {
        if (Storefront.LooksLikeMicrosoftStoreId(launchTarget))
            return "ms-windows-store://pdp/?ProductId=" + Uri.EscapeDataString(launchTarget);
        return "ms-windows-store://pdp/?ProductId=9PMF91N3LZ3M";
    }

    internal static bool LooksLikeFilesystemTarget(string value) =>
        Storefront.LooksLikeFilesystemTarget(value);

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
                if (IsExternalStorefront(uri))
                {
                    try
                    {
                        ProcessHelper.StartProtocol(uri);
                        return new InstallResult
                        {
                            Ok = true,
                            HandoffOnly = true,
                            Message = $"Opened the official {displayName} download.",
                        };
                    }
                    catch
                    {
                        return new InstallResult
                        {
                            Ok = false,
                            Message = $"Exo could not open the official {displayName} download.",
                        };
                    }
                }
            }

            // A removal shows its own confirmation (Ubisoft Connect's, Battle.net's, an uninstaller's), and Exo
            // cannot press it: hiding the store's windows made Remove wait 20 minutes on a dialog nobody saw.
            using var hider = HiderFor(game.Store);
            if (uninstall) HiddenStoreRuntime.SuspendFor(game.Store, TimeSpan.FromMinutes(20));
            else hider.Start(TimeSpan.FromSeconds(10));
            Process? uninstaller = null;
            if (uninstall)
            {
                ct.ThrowIfCancellationRequested();
                uninstaller = StartArpUninstall(game, out var declined, out var found);
                if (declined)
                    return new InstallResult { Ok = false, Message = "Windows did not get permission to remove this game, so nothing was removed." };
                // No uninstaller of its own (Xbox games never have one): the store's app removes it.
                if (uninstaller is null)
                    return RemoveInClient(game, displayName, found);
            }
            else
            {
                StartInstall(game);
            }
            using var watchedUninstaller = uninstaller;

        var start = DateTimeOffset.UtcNow;
        var limit = uninstall ? TimeSpan.FromMinutes(20) : TimeSpan.FromSeconds(10);
        DateTimeOffset? failedExitAt = null;
        while (!ct.IsCancellationRequested)
        {
            var installed = IsStillInstalled(game);
            if (uninstall && !installed)
                return new InstallResult { Ok = true, Message = $"Removed through {displayName}." };
            if (!uninstall && installed)
                return new InstallResult { Ok = true, Message = $"Installed through {displayName}.", Path = game.Path };

            // An uninstaller that exits with an error (declined, canceled, no
            // rights) is done: say so instead of waiting out the 20 minutes.
            if (UninstallerFailure(watchedUninstaller) is { } exitCode)
            {
                failedExitAt ??= DateTimeOffset.UtcNow;
                if (DateTimeOffset.UtcNow - failedExitAt > TimeSpan.FromSeconds(5))
                {
                    AppLog.Warn($"Uninstaller for \"{game.Title}\" exited {exitCode} with the game still installed.");
                    return new InstallResult
                    {
                        Ok = false,
                        Message = exitCode == 1602
                            ? "Removal was canceled, so the game is still installed."
                            : $"The uninstaller for {game.Title} stopped before removing it. Open {displayName} to uninstall it there.",
                    };
                }
            }

            if (DateTimeOffset.UtcNow - start > limit)
            {
                if (!uninstall)
                {
                    // A protocol dispatch is no evidence of a live download.
                    // Release the hidden window so required vendor prompts can
                    // be completed, and keep installed state evidence-based.
                    hider.Dispose();
                    return ClientHandoff(displayName, TryOpenOfficialClient(game.Store), "install");
                }
                return new InstallResult
                {
                    Ok = false,
                    Message = uninstall
                        ? $"{displayName} did not finish removing this game."
                        : $"{displayName} did not finish installing this game.",
                };
            }

            // Removal is confirmed by evidence disappearing, so keep that quick.
            // A brief install watch can confirm a fast completion; an
            // unconfirmed request returns a visible client handoff.
            await Task.Delay(uninstall ? UninstallPollInterval : InstallPollInterval, ct)
                .ConfigureAwait(false);
        }

        return new InstallResult { Ok = false, Message = "Canceled." };
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

    private static bool IsExternalStorefront(string uri) =>
        uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
        uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        uri.StartsWith("ms-windows-store:", StringComparison.OrdinalIgnoreCase);

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

    /// <summary>The exit code of an uninstaller that already failed, or null while it runs or when it succeeded.</summary>
    internal static int? UninstallerFailure(Process? uninstaller)
    {
        try
        {
            if (uninstaller is null || !uninstaller.HasExited) return null;
            // 3010 and 1641: removed, a restart finishes it.
            return uninstaller.ExitCode is 0 or 3010 or 1641 ? null : uninstaller.ExitCode;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    // Do not fall through from missing or ambiguous install evidence to a
    // protocol or a broad client command. Without one exact uninstaller the
    // store's app is opened and the user removes the game there (RemoveInClient).
    /// <param name="found">True when the game has its own uninstaller, whether or not it started.</param>
    internal static Process? StartArpUninstall(GameEntry game, out bool declined, out bool found)
    {
        declined = false;
        var record = SelectArpUninstall(game, ReadArpUninstallRecords());
        found = record is not null;
        return record is null ? null : StartUninstallString(record.Command, out declined);
    }

    /// <summary>
    /// A game Windows has no uninstaller for (every Xbox game, or one whose record Exo cannot match) is removed
    /// in its store's app. The library notices once its install is gone.
    /// </summary>
    private static InstallResult RemoveInClient(GameEntry game, string displayName, bool uninstallerFailed)
    {
        if (TryOpenOfficialClient(game.Store))
            return new InstallResult { Ok = true, HandoffOnly = true, Message = $"Opened {displayName}. Remove {game.Title} there." };
        return new InstallResult
        {
            Ok = false,
            Message = uninstallerFailed
                ? $"{game.Title}'s uninstaller did not start, and {displayName} could not be opened to remove it."
                : $"Windows has no uninstaller for {game.Title}, and {displayName} could not be opened to remove it.",
        };
    }

    internal sealed record ArpUninstallRecord(string? Title, string? Location, string Command);

    internal static ArpUninstallRecord? SelectArpUninstall(GameEntry game, IEnumerable<ArpUninstallRecord> records)
    {
        var target = CanonicalUninstallPath(game.Path);
        if (target is null || string.IsNullOrWhiteSpace(game.Title)) return null;
        // Installation roots must be exact. An ancestor, sibling, same title at
        // another path, or a shared root is not authority to remove a product.
        var atPath = records.Where(record =>
            target.Equals(CanonicalUninstallPath(record.Location), StringComparison.OrdinalIgnoreCase)).ToArray();
        if (atPath.Any(record => IsClientUninstallName(record.Title) || !SameUninstallTitle(record.Title, game.Title)))
            return null;
        var matches = atPath.Where(record => !string.IsNullOrWhiteSpace(record.Command))
            .DistinctBy(record => record.Command.Trim(), StringComparer.OrdinalIgnoreCase).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    /// <summary>
    /// The same title but for case, spacing, punctuation and marks: Windows can list "Battlefield™ 2042" where the
    /// store says "Battlefield 2042", and an exact compare refused the game's own uninstaller.
    /// </summary>
    internal static bool SameUninstallTitle(string? left, string? right)
    {
        static string Letters(string? title) =>
            new((title ?? "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        var a = Letters(left);
        return a.Length > 0 && a == Letters(right);
    }

    private static string? CanonicalUninstallPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var full = Path.GetFullPath(path.Trim().Trim('"')).TrimEnd(Path.DirectorySeparatorChar);
            if (!Path.IsPathFullyQualified(path.Trim().Trim('"')) ||
                full.Equals(Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                return null;
            return full;
        }
        catch { return null; }
    }

    private static IReadOnlyList<ArpUninstallRecord> ReadArpUninstallRecords()
    {
        var records = new List<ArpUninstallRecord>();
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
                        records.Add(new(display, location, uninstall));
                    }
                    catch { /* skip one */ }
                }
            }
            catch { /* hive unavailable */ }
        }

        return records;
    }

    /// <param name="declined">True when the user said no to the Windows permission prompt.</param>
    internal static Process? StartUninstallString(string uninstallString, out bool declined)
    {
        declined = false;
        var command = BuildUninstallStartInfo(uninstallString);
        if (command is null || !File.Exists(command.FileName)) return null;
        try
        {
            return Process.Start(command);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            declined = true;
            return null;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Uninstaller did not start: " + ex.GetType().Name + ": " + ex.Message);
            return null;
        }
    }

    internal static ProcessStartInfo? BuildUninstallStartInfo(string uninstallString)
    {
        var (fileName, arguments) = SplitCommand(uninstallString);
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        var values = Cli.WindowsCommandLine.Parse(arguments).ToList();
        var systemMsi = Path.Combine(Environment.SystemDirectory, "msiexec.exe");
        var isMsi = fileName.Equals("msiexec", StringComparison.OrdinalIgnoreCase) ||
                    fileName.Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase) ||
                    fileName.Equals(systemMsi, StringComparison.OrdinalIgnoreCase);
        if (isMsi)
        {
            // ARP frequently stores /I. Removal must use one product GUID with
            // /X; never run an installer or accept a same-named arbitrary exe.
            if (values.Count == 0) return null;
            var first = values[0];
            if (!(first.StartsWith("/I", StringComparison.OrdinalIgnoreCase) ||
                  first.StartsWith("/X", StringComparison.OrdinalIgnoreCase))) return null;
            var product = first.Length > 2 ? first[2..] : values.ElementAtOrDefault(1);
            if (!Guid.TryParseExact(product, "B", out var productId)) return null;
            var rest = values.Skip(first.Length > 2 ? 1 : 2);
            if (rest.Any(value => !new[] { "/qn", "/quiet", "/passive", "/norestart", "REBOOT=ReallySuppress" }
                .Contains(value, StringComparer.OrdinalIgnoreCase))) return null;
            // /passive, not /qn: a per-machine product needs admin rights, and
            // only a UI level above /qn lets Windows ask for them. /qn failed
            // silently and Exo then waited 20 minutes.
            return new ProcessStartInfo(systemMsi, $"/X {productId:B} /passive /norestart")
            {
                UseShellExecute = true,
                WorkingDirectory = Environment.SystemDirectory,
            };
        }
        if (!Path.IsPathFullyQualified(fileName) ||
            !Path.GetExtension(fileName).Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(fileName).Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase))
            return null;
        // Shell execute so an uninstaller that requires admin rights gets the
        // Windows permission prompt; without it the start failed outright.
        return new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(fileName) ?? "",
        };
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

        // Unquoted paths with spaces are common (C:\Program Files\Foo\unins000.exe /SILENT):
        // the program ends at its ".exe", as Windows reads such a command.
        var exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe > 0 && (exe + 4 == text.Length || text[exe + 4] == ' '))
            return (text[..(exe + 4)], text[(exe + 4)..].Trim());
        var space = text.IndexOf(' ');
        return space < 0 ? (text, "") : (text[..space], text[(space + 1)..].Trim());
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
            HiddenStoreRuntime.AllowUserOpen(store);
            using var started = Process.Start(BuildAppxClientStartInfo(command));
            _ = StoreClientOpener.RevealPendingAsync(store, command, ProcessNames(store));
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static ProcessStartInfo BuildAppxClientStartInfo(StoreClientLaunchCommand command) => new()
    {
        FileName = command.FileName,
        Arguments = command.Arguments,
        UseShellExecute = true,
        WorkingDirectory = command.IsAppx ? "" : Path.GetDirectoryName(command.FileName) ?? "",
    };

    private static StoreWindowHider HiderFor(StoreKind store) =>
        StoreWindowHider.For(store) ?? StoreWindowHider.ForAllStoreChrome();

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
