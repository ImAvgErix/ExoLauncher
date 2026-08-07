using ExoLauncher.Models;

namespace ExoLauncher.Adapters;

/// <summary>
/// Shared stub for stores where the official agent must remain installed.
/// Exo is the UI; the agent is an invisible dependency. Discovery and launch
/// shapes compile and report honestly — deeper integration is phase 2+.
/// </summary>
public abstract class AgentPresentAdapterBase : IStoreAdapter
{
    public abstract StoreKind Store { get; }
    public abstract string DisplayName { get; }
    protected abstract string[] ProcessNames { get; }
    protected abstract string[] AgentPaths { get; }
    protected abstract string LaunchNote { get; }
    protected abstract string[] DefaultDeps { get; }

    public bool IsAgentPresent() => AgentPaths.Any(File.Exists);

    public virtual Task<IReadOnlyList<GameEntry>> DiscoverAsync(CancellationToken ct = default)
    {
        // Phase 1: no deep library scan yet — surface agent status only.
        IReadOnlyList<GameEntry> empty = Array.Empty<GameEntry>();
        return Task.FromResult(empty);
    }

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
                BackendStarted = DisplayName.ToLowerInvariant(),
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new LaunchResult { Ok = false, Message = ex.Message });
        }
    }

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
    public override string DisplayName => "Xbox";
    protected override string[] ProcessNames => new[] { "GamingServices", "GameBar", "XboxPcApp" };
    protected override string[] AgentPaths => new[]
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", "Microsoft.GamingApp_8wekyb3d8bbwe", "XboxPcApp.exe"),
    };
    protected override string LaunchNote =>
        "Xbox / Microsoft Store titles need Gaming Services. Exo is the UI; the agent stays installed.";
    protected override string[] DefaultDeps => new[] { "Gaming Services", "Xbox app (optional)" };
}

public sealed class EaAdapter : AgentPresentAdapterBase
{
    public override StoreKind Store => StoreKind.Ea;
    public override string DisplayName => "EA";
    protected override string[] ProcessNames => new[] { "EADesktop", "EABackgroundService" };
    protected override string[] AgentPaths => new[]
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Electronic Arts", "EA Desktop", "EA Desktop", "EADesktop.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Electronic Arts", "EA Desktop", "EA Desktop", "EADesktop.exe"),
    };
    protected override string LaunchNote =>
        "EA Desktop remains the backend for EA App titles. Anti-cheat (EAC) may also be required.";
    protected override string[] DefaultDeps => new[] { "EA Desktop" };
}

public sealed class UbisoftAdapter : AgentPresentAdapterBase
{
    public override StoreKind Store => StoreKind.Ubisoft;
    public override string DisplayName => "Ubisoft";
    protected override string[] ProcessNames => new[] { "upc", "UplayWebCore", "UbisoftGameLauncher" };
    protected override string[] AgentPaths => new[]
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Ubisoft", "Ubisoft Game Launcher", "upc.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Ubisoft", "Ubisoft Game Launcher", "upc.exe"),
    };
    protected override string LaunchNote =>
        "Ubisoft Connect stays as the ownership/DRM backend. Exo does not replace upc.exe.";
    protected override string[] DefaultDeps => new[] { "Ubisoft Connect" };
}

public sealed class BattleNetAdapter : AgentPresentAdapterBase
{
    public override StoreKind Store => StoreKind.BattleNet;
    public override string DisplayName => "Battle.net";
    protected override string[] ProcessNames => new[] { "Battle.net", "Agent" };
    protected override string[] AgentPaths => new[]
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Battle.net", "Battle.net.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Battle.net", "Battle.net.exe"),
    };
    protected override string LaunchNote =>
        "Battle.net agent required for Blizzard titles. Exo never bypasses Battle.net DRM.";
    protected override string[] DefaultDeps => new[] { "Battle.net" };
}
