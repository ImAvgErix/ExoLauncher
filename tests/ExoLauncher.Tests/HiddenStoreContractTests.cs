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
        var riot = File.ReadAllText(FindRepoFile(
            Path.Combine("ExoLauncher", "Adapters", "StoreUninstallPromptAutomator.cs")));
        var steam = File.ReadAllText(FindRepoFile(
            Path.Combine("ExoLauncher", "Adapters", "SteamAdapter.cs")));
        Assert.DoesNotContain("SetCursorPos", riot + steam, StringComparison.Ordinal);
        Assert.DoesNotContain("mouse_event", riot + steam, StringComparison.Ordinal);
        Assert.DoesNotContain("SendInput", riot + steam, StringComparison.Ordinal);
        Assert.DoesNotContain("SetForegroundWindow", steam, StringComparison.Ordinal);
    }

    [Fact]
    public void SteamCommandsTheClientWithoutCaptureOrOcr()
    {
        var root = new DirectoryInfo(FindRepoFile(Path.Combine("ExoLauncher.sln"))).Parent!.FullName;
        var steam = File.ReadAllText(FindRepoFile(Path.Combine("ExoLauncher", "Adapters", "SteamAdapter.cs")));
        var hider = File.ReadAllText(FindRepoFile(Path.Combine("ExoLauncher", "Adapters", "StoreWindowHider.cs")));
        var csproj = File.ReadAllText(FindRepoFile(Path.Combine("ExoLauncher", "ExoLauncher.csproj")));
        var sln = File.ReadAllText(FindRepoFile(Path.Combine("ExoLauncher.sln")));

        Assert.Contains("SteamClientIpc.Command", steam, StringComparison.Ordinal);
        Assert.DoesNotContain("SteamGpuCapture", steam, StringComparison.Ordinal);
        Assert.DoesNotContain("SteamOcr", steam, StringComparison.Ordinal);
        Assert.DoesNotContain("BeginOffscreenAutomationWindow", steam + hider, StringComparison.Ordinal);
        Assert.DoesNotContain("s_offscreenAutomationWindows", hider, StringComparison.Ordinal);
        Assert.Contains("ShowWindow(hWnd, SwHide)", hider, StringComparison.Ordinal);
        Assert.Contains("IsAgreementTitle", hider, StringComparison.Ordinal);
        Assert.DoesNotContain("SteamEulaAcceptance", steam, StringComparison.Ordinal);
        Assert.DoesNotContain("StoreAgreementPromptAutomator", steam, StringComparison.Ordinal);
        Assert.DoesNotContain("CaptureHost", csproj + sln, StringComparison.Ordinal);
        Assert.DoesNotContain("Vortice.Direct3D11", csproj, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(root, "ExoLauncher", "Adapters", "SteamGpuCapture.cs")));
        Assert.False(File.Exists(Path.Combine(root, "ExoLauncher", "Adapters", "SteamOcr.cs")));
        Assert.False(File.Exists(Path.Combine(root, "ExoLauncher", "Adapters", "SteamInstallDialogAutomator.cs")));
        Assert.False(File.Exists(Path.Combine(root, "ExoLauncher", "Adapters", "SteamTargetedQueuePromotionAutomator.cs")));
        Assert.False(File.Exists(Path.Combine(root, "ExoLauncher", "Adapters", "SteamUninstallPromptAutomator.cs")));
        Assert.False(File.Exists(Path.Combine(root, "ExoLauncher", "Adapters", "SteamEulaAcceptance.cs")));
        Assert.False(File.Exists(Path.Combine(root, "ExoLauncher", "Adapters", "StoreAgreementPromptAutomator.cs")));
        Assert.False(File.Exists(Path.Combine(root, "ExoLauncher.CaptureHost", "ExoLauncher.CaptureHost.csproj")));
    }

    [Fact]
    public void GameSessionKeepsStoreSuppressionAliveUntilSessionWatcherFinishes()
    {
        var runtime = File.ReadAllText(FindRepoFile(
            Path.Combine("ExoLauncher", "Adapters", "HiddenStoreRuntime.cs")));
        var orchestrator = File.ReadAllText(FindRepoFile(
            Path.Combine("ExoLauncher", "Services", "LaunchOrchestrator.cs")));
        var cleanup = File.ReadAllText(FindRepoFile(
            Path.Combine("ExoLauncher", "Adapters", "StoreClientCleanup.cs")));

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
        Assert.True(
            orchestrator.Split("CloseUnusedStoreClientsAsync(", StringSplitOptions.None).Length - 1 >= 2,
            "Install/update must also close unused store clients, not only Play.");
        Assert.Contains("CloseUnusedStoreClientsAsync(currentGame.Store)", orchestrator, StringComparison.Ordinal);
        Assert.Contains("QuietKeptBackend", cleanup, StringComparison.Ordinal);
        Assert.Contains("StoreClientActivity.ShouldKeepRunning", cleanup, StringComparison.Ordinal);
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
        Assert.Contains("XboxPcApp", StoreAudioSilencer.ProcessNamesFor(StoreKind.Xbox), StringComparer.OrdinalIgnoreCase);
        Assert.Contains("EADesktop", StoreAudioSilencer.ProcessNamesFor(StoreKind.Ea), StringComparer.OrdinalIgnoreCase);
        Assert.Contains("UbisoftConnect", StoreAudioSilencer.ProcessNamesFor(StoreKind.Ubisoft), StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Battle.net", StoreAudioSilencer.ProcessNamesFor(StoreKind.BattleNet), StringComparer.OrdinalIgnoreCase);
        Assert.Contains("AmazonGames", StoreAudioSilencer.ProcessNamesFor(StoreKind.Amazon), StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Launcher", StoreAudioSilencer.ProcessNamesFor(StoreKind.Rockstar), StringComparer.OrdinalIgnoreCase);

        var all = Enum.GetValues<StoreKind>()
            .SelectMany(StoreAudioSilencer.ProcessNamesFor)
            .ToArray();
        Assert.DoesNotContain(all, name => name.Contains("overlay", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(all, name => name.Contains("EasyAntiCheat", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(all, name => name is "vgc" or "vgk");
        Assert.DoesNotContain(all, name => name.Contains("service", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(all, name => name.Contains("socialclub", StringComparison.OrdinalIgnoreCase));
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
    public void IdleAudioGuardSkipsSessionEnumerationAndUsesLowFrequencyWakeups()
    {
        Assert.False(StoreAudioSilencer.ShouldEnumerateSessions(
            activeProcessNameCount: 0,
            ownedMuteCount: 0));
        Assert.True(StoreAudioSilencer.ShouldEnumerateSessions(
            activeProcessNameCount: 1,
            ownedMuteCount: 0));
        Assert.True(StoreAudioSilencer.ShouldEnumerateSessions(
            activeProcessNameCount: 0,
            ownedMuteCount: 1));

        var activeChecksPerMinute = (int)(TimeSpan.FromMinutes(1).Ticks /
            StoreAudioSilencer.ActiveSweepInterval.Ticks);
        var idleChecksPerMinute = (int)(TimeSpan.FromMinutes(1).Ticks /
            StoreAudioSilencer.IdleSweepInterval.Ticks);

        Assert.Equal(240, activeChecksPerMinute);
        Assert.InRange(idleChecksPerMinute, 1, 12);
        Assert.True(idleChecksPerMinute <= activeChecksPerMinute / 20,
            $"Idle audio checks regressed to {idleChecksPerMinute}/minute.");
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
        Assert.Contains("IsSuspended(StoreKind.Steam)", hider, StringComparison.Ordinal);
        Assert.Contains("ForSteam() =>", hider, StringComparison.Ordinal);
        Assert.Contains("ForEa() =>", hider, StringComparison.Ordinal);
        Assert.Contains("ForXbox() =>", hider, StringComparison.Ordinal);
        Assert.Contains("!IsTrackedProcess(pid)", hider, StringComparison.Ordinal);
        Assert.Contains("StoreWindowHider.ForAllStoreChrome", runtime, StringComparison.Ordinal);
        Assert.Contains("_windowGuard.StartUntilStopped", runtime, StringComparison.Ordinal);
        Assert.Contains("IsStoreSurfaceSuppressed", runtime, StringComparison.Ordinal);
        Assert.Contains("!IsSuspended(StoreKind.Steam)", runtime, StringComparison.Ordinal);
        Assert.Contains("StoreWindowHider.XboxClientProcessNames", runtime, StringComparison.Ordinal);
        Assert.Contains("StoreWindowHider.EaClientProcessNames", runtime, StringComparison.Ordinal);
        Assert.Contains("StoreWindowHider.UbisoftClientProcessNames", runtime, StringComparison.Ordinal);
        Assert.Contains("StoreWindowHider.BattleNetClientProcessNames", runtime, StringComparison.Ordinal);
        Assert.Contains("StoreWindowHider.AmazonClientProcessNames", runtime, StringComparison.Ordinal);
        Assert.Contains("StoreWindowHider.RockstarClientProcessNames", runtime, StringComparison.Ordinal);
        Assert.Contains("StoreWindowHider.ItchClientProcessNames", runtime, StringComparison.Ordinal);
        Assert.Contains("StoreWindowHider.MinecraftClientProcessNames", runtime, StringComparison.Ordinal);
        Assert.Contains("StoreWindowHider.RobloxClientProcessNames", runtime, StringComparison.Ordinal);
        Assert.Contains("StoreWindowHider.ParadoxClientProcessNames", runtime, StringComparison.Ordinal);
        Assert.Contains("StoreWindowHider.WargamingClientProcessNames", runtime, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitOfficialClientOpenNeverRestoresRockstarSupportProcesses()
    {
        var bridge = File.ReadAllText(FindRepoFile(
            Path.Combine("ExoLauncher", "Services", "ShellController.cs")));
        var openStart = bridge.IndexOf("private object OpenOfficialClient", StringComparison.Ordinal);
        var openEnd = bridge.IndexOf("private static string[] OfficialClientUiProcessNames", StringComparison.Ordinal);
        var officialOpen = bridge[openStart..openEnd];
        var processMap = bridge[openEnd..];

        Assert.True(openStart >= 0 && openEnd > openStart);
        Assert.Contains("OfficialClientUiProcessNames(kind)", officialOpen, StringComparison.Ordinal);
        Assert.DoesNotContain("adapter.ClientProcessNames", officialOpen, StringComparison.Ordinal);
        Assert.Contains("StoreKind.Rockstar => StoreWindowHider.RockstarClientProcessNames", processMap,
            StringComparison.Ordinal);
        Assert.DoesNotContain("RockstarService", processMap, StringComparison.Ordinal);
        Assert.DoesNotContain("SocialClubHelper", processMap, StringComparison.Ordinal);
    }

    /// <summary>
    /// Adding a <see cref="StoreKind"/> without extending both Settings switches
    /// leaves the store visible in the library but "Unknown store." on Open.
    /// </summary>
    [Fact]
    public void EveryOfficialClientStoreIsReachableFromSettingsOpen()
    {
        // Local has no vendor client; Steam/Epic/GOG/Riot resolve their own executables.
        StoreKind[] dedicated = [StoreKind.Local, StoreKind.Steam, StoreKind.Epic, StoreKind.Gog, StoreKind.Riot];
        var bridge = File.ReadAllText(FindRepoFile(Path.Combine("ExoLauncher", "Services", "WebHostBridge.cs")));
        var shell = File.ReadAllText(FindRepoFile(Path.Combine("ExoLauncher", "Services", "ShellController.cs")));

        foreach (var kind in Enum.GetValues<StoreKind>().Except(dedicated))
        {
            var open = $"OpenOfficialClient(\"{kind.ToString().ToLowerInvariant()}\", StoreKind.{kind},";
            var names = $"StoreKind.{kind} => StoreWindowHider.{kind}ClientProcessNames";
            foreach (var source in new[] { bridge, shell })
            {
                Assert.Contains(open, source, StringComparison.Ordinal);
                Assert.Contains(names, source, StringComparison.Ordinal);
            }
        }
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
