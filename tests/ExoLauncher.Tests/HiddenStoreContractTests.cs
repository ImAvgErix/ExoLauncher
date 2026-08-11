using ExoLauncher.Adapters;
using ExoLauncher.Models;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class HiddenStoreContractTests
{
    [Fact]
    public void WiredStoreAdaptersDoNotStartVisibleOrMinimizedClients()
    {
        foreach (var relative in new[]
                 {
                     Path.Combine("ExoLauncher", "Adapters", "SteamAdapter.cs"),
                     Path.Combine("ExoLauncher", "Adapters", "EpicAdapter.cs"),
                     Path.Combine("ExoLauncher", "Adapters", "GogAdapter.cs"),
                     Path.Combine("ExoLauncher", "Adapters", "RiotAdapter.cs"),
                 })
        {
            var path = FindRepoFile(relative);
            var text = File.ReadAllText(path);
            Assert.DoesNotContain("StartVisibleShell(", text, StringComparison.Ordinal);
            Assert.DoesNotContain("StartMinimized(", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void StoreAutomatorsNeverForegroundOrMoveTheUserCursor()
    {
        foreach (var relative in new[]
                 {
                     Path.Combine("ExoLauncher", "Adapters", "SteamInstallDialogAutomator.cs"),
                     Path.Combine("ExoLauncher", "Adapters", "SteamTargetedQueuePromotionAutomator.cs"),
                 })
        {
            var path = FindRepoFile(relative);
            var text = File.ReadAllText(path);
            Assert.DoesNotContain("SetForegroundWindow", text, StringComparison.Ordinal);
            Assert.DoesNotContain("SetCursorPos", text, StringComparison.Ordinal);
            Assert.DoesNotContain("mouse_event", text, StringComparison.Ordinal);
            Assert.DoesNotContain("SendInput", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TargetVerifiedSteamAutomationRendersOnlyOffscreenAndNoActivate()
    {
        var hider = File.ReadAllText(FindRepoFile(
            Path.Combine("ExoLauncher", "Adapters", "StoreWindowHider.cs")));
        var automator = File.ReadAllText(FindRepoFile(
            Path.Combine("ExoLauncher", "Adapters", "SteamTargetedQueuePromotionAutomator.cs")));

        Assert.Contains("WsExNoActivate", hider, StringComparison.Ordinal);
        Assert.Contains("offscreenX", hider, StringComparison.Ordinal);
        Assert.Contains("BeginOffscreenAutomationWindow", hider, StringComparison.Ordinal);
        Assert.Contains("s_offscreenAutomationWindows", hider, StringComparison.Ordinal);
        Assert.Contains("ShowWindow(hWnd, SwHide)", hider, StringComparison.Ordinal);
        Assert.Contains("StoreWindowHider.BeginOffscreenAutomationWindow", automator, StringComparison.Ordinal);
        Assert.Contains("PrintWindow", automator, StringComparison.Ordinal);
        Assert.DoesNotContain("SetForegroundWindow", automator, StringComparison.Ordinal);
    }

    [Fact]
    public void GameSessionKeepsStoreSuppressionAliveUntilSessionWatcherFinishes()
    {
        var runtime = File.ReadAllText(FindRepoFile(
            Path.Combine("ExoLauncher", "Adapters", "HiddenStoreRuntime.cs")));
        var orchestrator = File.ReadAllText(FindRepoFile(
            Path.Combine("ExoLauncher", "Services", "LaunchOrchestrator.cs")));

        Assert.Contains("public static IDisposable GameSession(StoreKind activeProvider)", runtime, StringComparison.Ordinal);
        Assert.Contains("beginQuietGameSession ?? Adapters.HiddenStoreRuntime.GameSession", orchestrator,
            StringComparison.Ordinal);
        Assert.Contains("_beginQuietGameSession(game.Store)", orchestrator, StringComparison.Ordinal);
        Assert.Contains("DisposeQuietGameSessionAsync", orchestrator, StringComparison.Ordinal);
        Assert.Contains("Task.Run(quietGameSession.Dispose)", orchestrator, StringComparison.Ordinal);
        Assert.Contains("finally", orchestrator, StringComparison.Ordinal);

        var sessionRegistration = orchestrator.IndexOf(
            "_beginQuietGameSession(game.Store)",
            StringComparison.Ordinal);
        var unusedCleanup = orchestrator.IndexOf(
            "CloseUnusedStoreClientsAsync(game.Store)",
            StringComparison.Ordinal);
        Assert.True(sessionRegistration >= 0 && sessionRegistration < unusedCleanup,
            "The active provider must be registered before sibling cleanup starts.");
    }

    [Fact]
    public void GogNotificationChromeIsPartOfTheHiddenStoreSurfaceSet()
    {
        Assert.Contains(
            StoreWindowHider.GalaxyProcessNames,
            name => string.Equals(name, "GOG Galaxy Notifications", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AudioSilencingHasAnExactClientOnlyCatalog()
    {
        Assert.Contains("steam", StoreAudioSilencer.ProcessNamesFor(StoreKind.Steam), StringComparer.OrdinalIgnoreCase);
        Assert.Contains("GOG Galaxy Notifications", StoreAudioSilencer.ProcessNamesFor(StoreKind.Gog), StringComparer.OrdinalIgnoreCase);
        Assert.Contains("RiotClientUx", StoreAudioSilencer.ProcessNamesFor(StoreKind.Riot), StringComparer.OrdinalIgnoreCase);

        var all = Enum.GetValues<StoreKind>()
            .SelectMany(StoreAudioSilencer.ProcessNamesFor)
            .ToArray();
        Assert.DoesNotContain(all, name => name.Contains("overlay", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(all, name => name.Contains("EasyAntiCheat", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(all, name => name is "vgc" or "vgk");
    }

    [Fact]
    public void AudioSilencerRestoresOnlySessionsMutedByExo()
    {
        var silencer = File.ReadAllText(FindRepoFile(
            Path.Combine("ExoLauncher", "Adapters", "StoreAudioSilencer.cs")));

        Assert.Contains("if (wasMuted) continue", silencer, StringComparison.Ordinal);
        Assert.Contains("_exoMuted.Add(key)", silencer, StringComparison.Ordinal);
        Assert.Contains("if (!stillMuted)", silencer, StringComparison.Ordinal);
        Assert.Contains("if (_exoMuted.Remove(key))", silencer, StringComparison.Ordinal);
        Assert.Contains("RestoreAll", silencer, StringComparison.Ordinal);
        Assert.Contains("SetMute(false", silencer, StringComparison.Ordinal);
        Assert.Contains("SetApartmentState(ApartmentState.MTA)", silencer, StringComparison.Ordinal);
        Assert.Contains("RegisterSessionNotification(_sessionCreatedNotification)", silencer, StringComparison.Ordinal);
        Assert.Contains("UnregisterSessionNotification(_sessionCreatedNotification)", silencer, StringComparison.Ordinal);
        Assert.Contains("SessionCreatedNotification", silencer, StringComparison.Ordinal);
        Assert.Contains("EnumAudioEndpoints(EDataFlow.ERender, DeviceStateActive", silencer, StringComparison.Ordinal);
        Assert.Contains("RebindSessionNotifications", silencer, StringComparison.Ordinal);
        Assert.Contains("catch (ObjectDisposedException)", silencer, StringComparison.Ordinal);
        Assert.True(
            silencer.IndexOf("Sweep();", StringComparison.Ordinal) <
            silencer.IndexOf("RegisterSessionNotification(_sessionCreatedNotification)", StringComparison.Ordinal),
            "Core Audio sessions must be enumerated before the creation callback is registered.");
    }

    [Fact]
    public void GameSessionWindowGuardExcludesInGameOverlays()
    {
        var hider = File.ReadAllText(FindRepoFile(
            Path.Combine("ExoLauncher", "Adapters", "StoreWindowHider.cs")));
        var runtime = File.ReadAllText(FindRepoFile(
            Path.Combine("ExoLauncher", "Adapters", "HiddenStoreRuntime.cs")));

        Assert.Contains("StartUntilStopped", hider, StringComparison.Ordinal);
        Assert.Contains("SteamMainProcessNames.Concat", hider, StringComparison.Ordinal);
        Assert.Contains("HiddenStoreRuntime.IsStoreSurfaceSuppressed", hider, StringComparison.Ordinal);
        Assert.Contains("!IsTrackedProcess(pid)", hider, StringComparison.Ordinal);
        Assert.Contains("StoreWindowHider.ForAllStoreChrome", runtime, StringComparison.Ordinal);
        Assert.Contains("_windowGuard.StartUntilStopped", runtime, StringComparison.Ordinal);
        Assert.Contains("IsStoreSurfaceSuppressed", runtime, StringComparison.Ordinal);
        Assert.Contains("!IsSuspended(StoreKind.Steam)", runtime, StringComparison.Ordinal);
    }

    private static string FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException(relative);
    }
}
