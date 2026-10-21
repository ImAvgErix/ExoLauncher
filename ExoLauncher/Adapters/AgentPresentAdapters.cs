using Microsoft.Win32;
using ExoLauncher.Models;
using ExoLauncher.Services;

namespace ExoLauncher.Adapters;

/// <summary>
/// Official-store clients Exo can identify. Library rows exist only for proven
/// on-disk installs; an empty client library stays empty. Install/update/uninstall
/// command that official client (protocol or Windows uninstall entry) and wait
/// until the on-disk evidence matches.
/// </summary>
public abstract class AgentPresentAdapterBase : IStoreAdapter, IOfficialStoreClient
{
    public abstract StoreKind Store { get; }
    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    protected abstract OfficialClientDefinition ClientDefinition { get; }
    public abstract IReadOnlyList<string> ClientProcessNames { get; }

    public virtual bool IsAgentPresent() => GetClientLaunchCommand() is not null;

    public bool IsClientPresent() => IsAgentPresent();

    public StoreClientLaunchCommand? GetClientLaunchCommand() =>
        OfficialClientLocator.Resolve(ClientDefinition);

    public virtual Task<AuthResult> AuthenticateAsync(CancellationToken ct = default) =>
        Task.FromResult(ConnectResult());

    protected AuthResult ConnectResult()
    {
        if (LocalStoreSessions.HasSession(Id))
        {
            return new AuthResult
            {
                Ok = true,
                RequiresUserAction = false,
                Message = $"Using the signed-in {DisplayName} account on this PC.",
            };
        }

        return IsAgentPresent()
            ? AuthResult.SignInInClient(DisplayName)
            : new AuthResult { Ok = false, Message = $"{DisplayName} is not installed." };
    }

    public virtual Task<IReadOnlyList<GameEntry>> GetLibraryAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<GameEntry>>(Array.Empty<GameEntry>());

    public Task<InstallResult> InstallAsync(
        GameEntry game,
        string? installPath,
        IProgress<InstallProgress>? progress,
        CancellationToken ct = default) =>
        OfficialInstalledLibraries.InstallAsync(game, DisplayName, ct);

    public Task<InstallResult> UpdateAsync(
        GameEntry game,
        IProgress<InstallProgress>? progress,
        CancellationToken ct = default) =>
        OfficialInstalledLibraries.UpdateAsync(game, DisplayName, ct);

    public virtual Task<LaunchResult> LaunchAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default) =>
        Task.FromResult(new LaunchResult
        {
            Ok = false,
            Message = $"Exo can open {DisplayName}, but cannot launch individual {DisplayName} games yet.",
        });

    public Task<InstallResult> UninstallAsync(GameEntry game, CancellationToken ct = default) =>
        OfficialInstalledLibraries.UninstallAsync(game, DisplayName, ct);

    public InstallProgress GetDownloadProgress(string gameId) =>
        new() { GameId = gameId, Phase = InstallPhase.Idle };

    public Task CleanupAfterExitAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default) =>
        Task.CompletedTask;
}

public sealed class XboxAdapter : AgentPresentAdapterBase
{
    private static readonly OfficialClientDefinition Definition = new(
        ExecutableNames: ["XboxPcApp.exe"],
        DefaultPaths:
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WindowsApps", "XboxPcApp.exe"),
        ],
        UninstallDisplayNames: ["Xbox"],
        AppxPackagePrefix: "Microsoft.GamingApp_",
        AppxApplicationUserModelId: "Microsoft.GamingApp_8wekyb3d8bbwe!Microsoft.XboxPcApp");

    public override StoreKind Store => StoreKind.Xbox;
    public override string Id => "xbox";
    public override string DisplayName => "Xbox app";
    protected override OfficialClientDefinition ClientDefinition => Definition;
    public override IReadOnlyList<string> ClientProcessNames => ["XboxPcApp", "GamingApp"];

    public override Task<IReadOnlyList<GameEntry>> GetLibraryAsync(CancellationToken ct = default) =>
        Task.FromResult(OwnedLibraryMerge.Merge(
            OfficialInstalledLibraries.ScanXbox(),
            LocalOwnedSources.Xbox()));

    public override Task<LaunchResult> LaunchAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default) =>
        OfficialInstalledLibraries.LaunchWithOptionsAsync(game, options, ct);
}

public sealed class EaAdapter : AgentPresentAdapterBase
{
    private static readonly OfficialClientDefinition Definition = new(
        ExecutableNames: ["EADesktop.exe"],
        DefaultPaths:
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Electronic Arts", "EA Desktop", "EA Desktop", "EADesktop.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Electronic Arts", "EA Desktop", "EA Desktop", "EADesktop.exe"),
        ],
        UninstallDisplayNames: ["EA app", "EA Desktop"]);

    public override StoreKind Store => StoreKind.Ea;
    public override string Id => "ea";
    public override string DisplayName => "EA app";
    protected override OfficialClientDefinition ClientDefinition => Definition;
    public override IReadOnlyList<string> ClientProcessNames => ["EADesktop"];

    public override Task<IReadOnlyList<GameEntry>> GetLibraryAsync(CancellationToken ct = default) =>
        Task.FromResult(OwnedLibraryMerge.Merge(
            OfficialInstalledLibraries.ScanEa(),
            LocalOwnedSources.Ea()));

    public override Task<LaunchResult> LaunchAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default) =>
        OfficialInstalledLibraries.LaunchWithOptionsAsync(game, options, ct);
}

public sealed class UbisoftAdapter : AgentPresentAdapterBase
{
    private static readonly OfficialClientDefinition Definition = new(
        ExecutableNames: ["UbisoftConnect.exe", "upc.exe"],
        DefaultPaths:
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Ubisoft", "Ubisoft Game Launcher", "UbisoftConnect.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Ubisoft", "Ubisoft Game Launcher", "upc.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Ubisoft", "Ubisoft Game Launcher", "UbisoftConnect.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Ubisoft", "Ubisoft Game Launcher", "upc.exe"),
        ],
        UninstallDisplayNames: ["Ubisoft Connect", "Ubisoft Game Launcher"]);

    public override StoreKind Store => StoreKind.Ubisoft;
    public override string Id => "ubisoft";
    public override string DisplayName => "Ubisoft Connect";
    protected override OfficialClientDefinition ClientDefinition => Definition;
    public override IReadOnlyList<string> ClientProcessNames => ["UbisoftConnect", "upc", "UplayWebCore"];

    public override Task<IReadOnlyList<GameEntry>> GetLibraryAsync(CancellationToken ct = default) =>
        Task.FromResult(OwnedLibraryMerge.Merge(
            OfficialInstalledLibraries.ScanUbisoft(),
            LocalOwnedSources.Ubisoft()));

    public override Task<LaunchResult> LaunchAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default) =>
        OfficialInstalledLibraries.LaunchWithOptionsAsync(game, options, ct);
}

public sealed class BattleNetAdapter : AgentPresentAdapterBase
{
    private static readonly OfficialClientDefinition Definition = new(
        ExecutableNames: ["Battle.net.exe"],
        DefaultPaths:
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Battle.net", "Battle.net.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Battle.net", "Battle.net.exe"),
        ],
        UninstallDisplayNames: ["Battle.net"]);

    public override StoreKind Store => StoreKind.BattleNet;
    public override string Id => "battlenet";
    public override string DisplayName => "Battle.net";
    protected override OfficialClientDefinition ClientDefinition => Definition;
    public override IReadOnlyList<string> ClientProcessNames => ["Battle.net"];

    public override Task<IReadOnlyList<GameEntry>> GetLibraryAsync(CancellationToken ct = default) =>
        Task.FromResult(OwnedLibraryMerge.Merge(
            OfficialInstalledLibraries.ScanBattleNet(),
            LocalOwnedSources.BattleNet()));

    public override Task<LaunchResult> LaunchAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default) =>
        OfficialInstalledLibraries.LaunchWithOptionsAsync(game, options, ct);
}

public sealed class RockstarAdapter : AgentPresentAdapterBase
{
    private static readonly OfficialClientDefinition Definition = new(
        ExecutableNames: ["Launcher.exe"],
        DefaultPaths:
        [
            // Rockstar documents this as the launcher's default location.
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Rockstar Games", "Launcher", "Launcher.exe"),
        ],
        UninstallDisplayNames: ["Rockstar Games Launcher"]);

    public override StoreKind Store => StoreKind.Rockstar;
    public override string Id => "rockstar";
    public override string DisplayName => "Rockstar Games Launcher";
    protected override OfficialClientDefinition ClientDefinition => Definition;
    public override IReadOnlyList<string> ClientProcessNames =>
        ["Launcher", "LauncherPatcher", "RockstarService", "SocialClubHelper"];

    public override Task<IReadOnlyList<GameEntry>> GetLibraryAsync(CancellationToken ct = default) =>
        Task.FromResult(OwnedLibraryMerge.Merge(
            OfficialInstalledLibraries.ScanRockstar(),
            LocalOwnedSources.Rockstar()));

    public override Task<LaunchResult> LaunchAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default) =>
        OfficialInstalledLibraries.LaunchWithOptionsAsync(game, options, ct);
}

public sealed class ItchAdapter : AgentPresentAdapterBase, IStoreAccountScope
{
    private static readonly OfficialClientDefinition Definition = new(
        ExecutableNames: ["itch.exe"],
        DefaultPaths:
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "itch", "itch.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "itch", "itch.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "itch", "itch.exe"),
        ],
        UninstallDisplayNames: ["itch"]);

    public override StoreKind Store => StoreKind.Itch;
    public override string Id => "itch";
    public override string DisplayName => "itch";
    protected override OfficialClientDefinition ClientDefinition => Definition;
    public override IReadOnlyList<string> ClientProcessNames => ["itch"];

    public string? GetActiveAccountScope() => ItchApiKeyStore.TryRead() is { } key
        ? "itch:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key))) : null;

    private static readonly HttpMessageHandler SharedItchHandler = new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AllowAutoRedirect = false,
    };

    public override async Task<IReadOnlyList<GameEntry>> GetLibraryAsync(CancellationToken ct = default)
    {
        var installed = OfficialInstalledLibraries.ScanItch();
        var key = ItchApiKeyStore.TryRead();
        if (key is null) return installed;
        // A partial/failed owned response is a failed scan, so LibraryService
        // keeps the last account-scoped result instead of treating it as empty.
        var owned = await ItchOwnedApi.LoadOwnedAsync(key, SharedItchHandler, ct).ConfigureAwait(false);
        return OwnedLibraryMerge.Merge(installed, owned);
    }

    public override Task<LaunchResult> LaunchAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default) =>
        OfficialInstalledLibraries.LaunchWithOptionsAsync(game, options, ct);
}

public sealed class MinecraftAdapter : AgentPresentAdapterBase
{
    private static readonly OfficialClientDefinition Definition = new(
        ExecutableNames: ["MinecraftLauncher.exe", "Minecraft.exe"],
        DefaultPaths:
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Minecraft Launcher", "MinecraftLauncher.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Minecraft Launcher", "MinecraftLauncher.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "Minecraft Launcher", "MinecraftLauncher.exe"),
        ],
        UninstallDisplayNames: ["Minecraft Launcher", "Minecraft"],
        AppxPackagePrefix: "Microsoft.4297127D64EC6_",
        AppxApplicationUserModelId: "Microsoft.4297127D64EC6_8wekyb3d8bbwe!Minecraft");

    public override StoreKind Store => StoreKind.Minecraft;
    public override string Id => "minecraft";
    public override string DisplayName => "Minecraft";
    protected override OfficialClientDefinition ClientDefinition => Definition;
    public override IReadOnlyList<string> ClientProcessNames => ["MinecraftLauncher"];

    public override Task<IReadOnlyList<GameEntry>> GetLibraryAsync(CancellationToken ct = default) =>
        Task.FromResult(OwnedLibraryMerge.Merge(
            OfficialInstalledLibraries.ScanMinecraft(),
            LocalOwnedSources.Minecraft()));

    public override Task<LaunchResult> LaunchAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default) =>
        OfficialInstalledLibraries.LaunchWithOptionsAsync(game, options, ct);
}

public sealed class RobloxAdapter : AgentPresentAdapterBase
{
    private static readonly OfficialClientDefinition Definition = new(
        ExecutableNames: ["RobloxPlayerBeta.exe"],
        DefaultPaths: [],
        UninstallDisplayNames: ["Roblox Player", "Roblox"]);

    public override StoreKind Store => StoreKind.Roblox;
    public override string Id => "roblox";
    public override string DisplayName => "Roblox";
    protected override OfficialClientDefinition ClientDefinition => Definition with { DefaultPaths = RobloxDefaultPaths() };
    public override IReadOnlyList<string> ClientProcessNames => ["RobloxPlayerLauncher"];

    public override Task<IReadOnlyList<GameEntry>> GetLibraryAsync(CancellationToken ct = default) =>
        Task.FromResult(OfficialInstalledLibraries.ScanRoblox());

    public override Task<LaunchResult> LaunchAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default) =>
        OfficialInstalledLibraries.LaunchWithOptionsAsync(game, options, ct);

    private static IReadOnlyList<string> RobloxDefaultPaths() =>
        OfficialInstalledLibraries.RobloxPlayerCandidates().ToArray();
}

public sealed class ParadoxAdapter : AgentPresentAdapterBase
{
    internal static readonly OfficialClientDefinition Definition = new(
        ExecutableNames: ["Paradox Launcher.exe", "ParadoxLauncher.exe", "launcher.exe"],
        DefaultPaths:
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Paradox Interactive", "launcher", "Paradox Launcher.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Paradox Interactive", "launcher", "Paradox Launcher.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "Paradox Interactive", "Paradox Launcher.exe"),
        ],
        UninstallDisplayNames: ["Paradox Launcher", "Paradox Interactive"]);

    public override StoreKind Store => StoreKind.Paradox;
    public override string Id => "paradox";
    public override string DisplayName => "Paradox";
    protected override OfficialClientDefinition ClientDefinition => Definition with
    {
        DefaultPaths = ParadoxDefaultPaths(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "Paradox Interactive")).Concat(Definition.DefaultPaths).ToArray(),
    };
    public override IReadOnlyList<string> ClientProcessNames => ["Paradox Launcher", "ParadoxLauncher"];

    internal static IEnumerable<string> ParadoxDefaultPaths(string root)
    {
        yield return Path.Combine(root, "launcher.exe");
        string[] versions;
        try { versions = Directory.GetDirectories(root, "launcher-v2.*", SearchOption.TopDirectoryOnly); }
        catch { versions = []; }
        foreach (var version in versions.OrderByDescending(Directory.GetLastWriteTimeUtc))
            yield return Path.Combine(version, "launcher.exe");
    }

    public override Task<IReadOnlyList<GameEntry>> GetLibraryAsync(CancellationToken ct = default) =>
        Task.FromResult(OwnedLibraryMerge.Merge(
            OfficialInstalledLibraries.ScanParadox(),
            LocalOwnedSources.Paradox()));

    public override Task<LaunchResult> LaunchAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default) =>
        OfficialInstalledLibraries.LaunchWithOptionsAsync(game, options, ct);
}

public sealed class WargamingAdapter : AgentPresentAdapterBase
{
    private static readonly OfficialClientDefinition Definition = new(
        ExecutableNames: ["wgc.exe"],
        DefaultPaths:
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Wargaming.net", "GameCenter", "wgc.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Wargaming.net", "GameCenter", "wgc.exe"),
        ],
        UninstallDisplayNames: ["Wargaming.net Game Center", "Wargaming Game Center"]);

    public override StoreKind Store => StoreKind.Wargaming;
    public override string Id => "wargaming";
    public override string DisplayName => "Wargaming";
    protected override OfficialClientDefinition ClientDefinition => Definition;
    public override IReadOnlyList<string> ClientProcessNames => ["wgc"];

    /// <summary>Installed games from Game Center's local game roots and library.json. No web API.</summary>
    public override Task<IReadOnlyList<GameEntry>> GetLibraryAsync(CancellationToken ct = default) =>
        Task.FromResult(OwnedLibraryMerge.Merge(
            OfficialInstalledLibraries.ScanWargaming(),
            LocalOwnedSources.Wargaming()));

    public override Task<LaunchResult> LaunchAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default) =>
        OfficialInstalledLibraries.LaunchWithOptionsAsync(game, options, ct);
}

/// <summary>Known official-client evidence. Every positive file result must match one of <see cref="ExecutableNames"/>.</summary>
public sealed record OfficialClientDefinition(
    IReadOnlyList<string> ExecutableNames,
    IReadOnlyList<string> DefaultPaths,
    IReadOnlyList<string> UninstallDisplayNames,
    string? AppxPackagePrefix = null,
    string? AppxApplicationUserModelId = null,
    IReadOnlyList<string>? InstallRelativePaths = null);

/// <summary>Safe command to open a verified official client. AppX commands are opened via Explorer's AppsFolder verb.</summary>
public sealed record StoreClientLaunchCommand(string FileName, string Arguments = "", bool IsAppx = false);

/// <summary>
/// Resolves only an existing, named official executable from known install paths,
/// App Paths, or matching uninstall records. Xbox may instead use a registered
/// Gaming App package's documented AppsFolder activation target.
/// </summary>
public static class OfficialClientLocator
{
    private static readonly string[] UninstallKeyPaths =
    [
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
    ];

    private static readonly string[] AppPathKeyRoots =
    [
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\",
    ];

    public static StoreClientLaunchCommand? Resolve(OfficialClientDefinition definition)
    {
        var appPaths = ReadAppPathValues(definition.ExecutableNames);
        var uninstallEntries = ReadUninstallEntries();
        var packages = ReadAppxPackageNames();
        return ResolveFromEvidence(definition, File.Exists, appPaths, uninstallEntries, packages);
    }

    /// <summary>Pure resolution seam used by tests; no registry or machine state is required.</summary>
    public static StoreClientLaunchCommand? ResolveFromEvidence(
        OfficialClientDefinition definition,
        Func<string, bool> fileExists,
        IEnumerable<string?> appPathValues,
        IEnumerable<OfficialClientUninstallEntry> uninstallEntries,
        IEnumerable<string> appxPackageNames)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(fileExists);

        // Activate a registered packaged client through Windows. Its execution
        // alias is outside the real install directory and may be disabled.
        if (!string.IsNullOrWhiteSpace(definition.AppxPackagePrefix) &&
            !string.IsNullOrWhiteSpace(definition.AppxApplicationUserModelId) &&
            appxPackageNames.Any(package => package.StartsWith(
                definition.AppxPackagePrefix, StringComparison.OrdinalIgnoreCase)))
        {
            return new StoreClientLaunchCommand("explorer.exe",
                $"shell:AppsFolder\\{definition.AppxApplicationUserModelId}", IsAppx: true);
        }

        foreach (var path in definition.DefaultPaths)
        {
            var command = ToVerifiedExecutable(definition, path, fileExists);
            if (command is not null) return command;
        }

        foreach (var value in appPathValues)
        {
            var command = ToVerifiedExecutable(definition, ExtractExecutablePath(value), fileExists);
            if (command is not null) return command;
        }

        foreach (var entry in uninstallEntries)
        {
            if (!definition.UninstallDisplayNames.Any(name =>
                    string.Equals(name.Trim(), entry.DisplayName.Trim(), StringComparison.OrdinalIgnoreCase)))
                continue;

            foreach (var executable in definition.ExecutableNames)
            {
                var fromInstallLocation = string.IsNullOrWhiteSpace(entry.InstallLocation)
                    ? null
                    : Path.Combine(entry.InstallLocation, executable);
                var command = ToVerifiedExecutable(definition, fromInstallLocation, fileExists)
                    ?? ToVerifiedExecutable(definition, ExtractExecutablePath(entry.DisplayIcon), fileExists);
                if (command is not null) return command;
            }
            if (!string.IsNullOrWhiteSpace(entry.InstallLocation))
            foreach (var relative in definition.InstallRelativePaths ?? [])
            {
                var command = ToVerifiedExecutable(definition,
                    Path.Combine(entry.InstallLocation, relative), fileExists);
                if (command is not null) return command;
            }
        }

        return null;
    }

    private static StoreClientLaunchCommand? ToVerifiedExecutable(
        OfficialClientDefinition definition,
        string? path,
        Func<string, bool> fileExists)
    {
        if (string.IsNullOrWhiteSpace(path) || !fileExists(path)) return null;
        var name = Path.GetFileName(path);
        return definition.ExecutableNames.Any(expected =>
            string.Equals(expected, name, StringComparison.OrdinalIgnoreCase))
            ? new StoreClientLaunchCommand(path)
            : null;
    }

    private static IEnumerable<string?> ReadAppPathValues(IEnumerable<string> executableNames)
    {
        var values = new List<string?>();
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        foreach (var appPathRoot in AppPathKeyRoots)
        foreach (var executable in executableNames)
        {
            try
            {
                using var key = root.OpenSubKey(appPathRoot + executable);
                values.Add(key?.GetValue(null) as string);
            }
            catch { values.Add(null); }
        }
        return values;
    }

    private static IEnumerable<OfficialClientUninstallEntry> ReadUninstallEntries()
    {
        var entries = new List<OfficialClientUninstallEntry>();
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        foreach (var keyPath in UninstallKeyPaths)
        {
            RegistryKey? uninstall = null;
            try { uninstall = root.OpenSubKey(keyPath); }
            catch { /* unavailable registry view */ }
            if (uninstall is null) continue;
            using (uninstall)
            {
                foreach (var name in uninstall.GetSubKeyNames())
                {
                    try
                    {
                        using var entry = uninstall.OpenSubKey(name);
                        var displayName = entry?.GetValue("DisplayName") as string;
                        if (string.IsNullOrWhiteSpace(displayName)) continue;
                        entries.Add(new OfficialClientUninstallEntry(
                            displayName,
                            entry?.GetValue("InstallLocation") as string,
                            entry?.GetValue("DisplayIcon") as string));
                    }
                    catch { /* ignore one malformed uninstall entry */ }
                }
            }
        }
        return entries;
    }

    private static IEnumerable<string> ReadAppxPackageNames()
    {
        try
        {
            using var packages = Registry.CurrentUser.OpenSubKey(@"Software\Classes\ActivatableClasses\Package");
            return packages?.GetSubKeyNames() ?? Array.Empty<string>();
        }
        catch { return Array.Empty<string>(); }
    }

    private static string? ExtractExecutablePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        if (trimmed.StartsWith('"'))
        {
            var closingQuote = trimmed.IndexOf('"', 1);
            return closingQuote > 1 ? trimmed[1..closingQuote] : null;
        }

        var exeEnd = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exeEnd < 0 ? trimmed : trimmed[..(exeEnd + 4)];
    }
}

public sealed record OfficialClientUninstallEntry(
    string DisplayName,
    string? InstallLocation,
    string? DisplayIcon);
