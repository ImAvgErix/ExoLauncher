using ExoLauncher.Models;

namespace ExoLauncher.Adapters;

/// <summary>
/// Phase-2 stubs: ensure agent present → start minimized → best-effort protocol.
/// Compile-ready shapes for Xbox / EA / Ubisoft / Battle.net / Amazon (Nile).
/// </summary>
public abstract class AgentPresentAdapterBase : IStoreAdapter
{
    private readonly Dictionary<string, InstallProgress> _progress = new(StringComparer.OrdinalIgnoreCase);

    public abstract StoreKind Store { get; }
    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    protected abstract string[] ProcessNames { get; }
    protected abstract string[] AgentPaths { get; }
    protected abstract string LaunchNote { get; }
    protected abstract string[] DefaultDeps { get; }

    public bool IsAgentPresent() => AgentPaths.Any(File.Exists);

    public Task<AuthResult> AuthenticateAsync(CancellationToken ct = default) =>
        Task.FromResult(new AuthResult
        {
            Ok = IsAgentPresent(),
            RequiresUserAction = true,
            Message = IsAgentPresent()
                ? $"{DisplayName} agent present. Full auth wiring is phase 2."
                : $"{DisplayName} agent not found.",
        });

    public virtual Task<IReadOnlyList<GameEntry>> GetLibraryAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<GameEntry>>(Array.Empty<GameEntry>());

    public virtual Task<InstallResult> InstallAsync(
        GameEntry game,
        string? installPath,
        IProgress<InstallProgress>? progress,
        CancellationToken ct = default)
    {
        if (!IsAgentPresent())
        {
            return Task.FromResult(new InstallResult
            {
                Ok = false,
                Message = $"{DisplayName} agent not found. Install the official client; Exo does not replace it.",
            });
        }

        var agent = AgentPaths.First(File.Exists);
        try
        {
            progress?.Report(new InstallProgress
            {
                GameId = game.Id,
                Phase = InstallPhase.Preparing,
                Percent = 5,
                Status = $"Opening {DisplayName} minimized (phase-2 install path)…",
            });
            ProcessHelper.StartMinimized(agent);
            return Task.FromResult(new InstallResult
            {
                Ok = true,
                Message = $"{DisplayName} agent started minimized. Full install progress wiring is phase 2.",
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new InstallResult { Ok = false, Message = ex.Message });
        }
    }

    public Task<InstallResult> UpdateAsync(GameEntry game, IProgress<InstallProgress>? progress, CancellationToken ct = default) =>
        InstallAsync(game, game.Path, progress, ct);

    public virtual Task<LaunchResult> LaunchAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default)
    {
        if (!IsAgentPresent())
        {
            return Task.FromResult(new LaunchResult
            {
                Ok = false,
                Message = $"{DisplayName} agent not found. Install the official client; Exo does not replace it.",
            });
        }

        var agent = AgentPaths.First(File.Exists);
        try
        {
            var p = ProcessHelper.StartMinimized(agent);
            return Task.FromResult(new LaunchResult
            {
                Ok = p is not null,
                Message = p is not null
                    ? $"{DisplayName} agent started minimized. Full title launch wiring is phase 2."
                    : "Agent did not start.",
                ProcessId = p?.Id,
                BackendStarted = Id,
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new LaunchResult { Ok = false, Message = ex.Message });
        }
    }

    public Task<InstallResult> UninstallAsync(GameEntry game, CancellationToken ct = default) =>
        Task.FromResult(new InstallResult
        {
            Ok = false,
            Message = $"{DisplayName} uninstall is phase 2 — use the official client for now.",
        });

    public InstallProgress GetDownloadProgress(string gameId) =>
        _progress.TryGetValue(gameId, out var p) ? p : new InstallProgress { GameId = gameId, Phase = InstallPhase.Idle };

    public Task CleanupAfterExitAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default)
    {
        if (options.CloseStoreUiAfterExit)
            ProcessHelper.TryCloseProcesses(ProcessNames);
        return Task.CompletedTask;
    }
}

public sealed class XboxAdapter : AgentPresentAdapterBase
{
    public override StoreKind Store => StoreKind.Xbox;
    public override string Id => "xbox";
    public override string DisplayName => "Xbox";
    protected override string[] ProcessNames => ["GamingServices", "GameBar", "XboxPcAppFT"];
    protected override string[] AgentPaths =>
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", "XboxPcApp.exe"),
    ];
    protected override string LaunchNote =>
        "Xbox / Microsoft Store titles need Gaming Services. Exo is the UI; the agent stays installed.";
    protected override string[] DefaultDeps => ["Gaming Services", "Xbox app (optional)"];
}

public sealed class EaAdapter : AgentPresentAdapterBase
{
    public override StoreKind Store => StoreKind.Ea;
    public override string Id => "ea";
    public override string DisplayName => "EA";
    protected override string[] ProcessNames => ["EADesktop", "EABackgroundService"];
    protected override string[] AgentPaths =>
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Electronic Arts", "EA Desktop", "EA Desktop", "EADesktop.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Electronic Arts", "EA Desktop", "EA Desktop", "EADesktop.exe"),
    ];
    protected override string LaunchNote =>
        "EA Desktop remains the backend. Anti-cheat (EAC) may also be required.";
    protected override string[] DefaultDeps => ["EA Desktop"];
}

public sealed class UbisoftAdapter : AgentPresentAdapterBase
{
    public override StoreKind Store => StoreKind.Ubisoft;
    public override string Id => "ubisoft";
    public override string DisplayName => "Ubisoft";
    protected override string[] ProcessNames => ["upc", "UplayWebCore", "UbisoftGameLauncher"];
    protected override string[] AgentPaths =>
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Ubisoft", "Ubisoft Game Launcher", "upc.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Ubisoft", "Ubisoft Game Launcher", "upc.exe"),
    ];
    protected override string LaunchNote =>
        "Ubisoft Connect stays as the ownership/DRM backend. Exo does not replace upc.exe.";
    protected override string[] DefaultDeps => ["Ubisoft Connect"];
}

public sealed class BattleNetAdapter : AgentPresentAdapterBase
{
    public override StoreKind Store => StoreKind.BattleNet;
    public override string Id => "battlenet";
    public override string DisplayName => "Battle.net";
    protected override string[] ProcessNames => ["Battle.net", "Agent"];
    protected override string[] AgentPaths =>
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Battle.net", "Battle.net.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Battle.net", "Battle.net.exe"),
    ];
    protected override string LaunchNote =>
        "Battle.net agent required for Blizzard titles. Exo never bypasses Battle.net DRM.";
    protected override string[] DefaultDeps => ["Battle.net"];
}

/// <summary>Amazon Games via Nile when present — optional first-class if low cost.</summary>
public sealed class AmazonAdapter : AgentPresentAdapterBase
{
    public override StoreKind Store => StoreKind.Amazon;
    public override string Id => "amazon";
    public override string DisplayName => "Amazon";
    protected override string[] ProcessNames => ["Amazon Games", "AmazonGamesUI"];
    protected override string[] AgentPaths
    {
        get
        {
            var nile = Cli.CliRunner.ResolveOnPath("nile.exe") ?? Cli.CliRunner.ResolveOnPath("nile");
            var list = new List<string>();
            if (nile is not null) list.Add(nile);
            list.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Amazon Games", "App", "Amazon Games.exe"));
            return list.ToArray();
        }
    }
    protected override string LaunchNote =>
        "Prefer Nile (https://github.com/imLinguin/nile) when present. Amazon Games app is the fallback agent.";
    protected override string[] DefaultDeps => ["Nile (preferred)", "Amazon Games"];
}
