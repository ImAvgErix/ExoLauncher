using ExoLauncher.Adapters;
using ExoLauncher.Models;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class LibraryUpdatedPublisherTests
{
    [Fact]
    public async Task DisposeDuringDebounce_DoesNotThrow_AndStillPushesSnapshot()
    {
        var snaps = 0;
        using var publisher = new LibraryUpdatedPublisher(_ =>
        {
            Interlocked.Increment(ref snaps);
            return Task.CompletedTask;
        });

        var thrown = await Record.ExceptionAsync(async () =>
        {
            publisher.Request();
            await Task.Delay(40);
            publisher.Request();
            publisher.Dispose();
            await Task.Delay(LibraryUpdatedPublisher.Debounce + TimeSpan.FromMilliseconds(250));
        });

        Assert.Null(thrown);
        Assert.True(snaps >= 1);
    }

    [Fact]
    public async Task ObjectDisposedFromPublish_DoesNotEscapeTheBridgeBoundary()
    {
        using var publisher = new LibraryUpdatedPublisher(_ =>
            throw new ObjectDisposedException(nameof(CancellationTokenSource)));

        var thrown = await Record.ExceptionAsync(async () =>
        {
            publisher.Request();
            await Task.Delay(LibraryUpdatedPublisher.Debounce + TimeSpan.FromMilliseconds(200));
        });

        Assert.Null(thrown);
    }

    [Fact]
    public async Task WatcherDisposeDuringDebounce_DoesNotThrow()
    {
        using var watchers = new LibraryWatchers();
        var thrown = await Record.ExceptionAsync(async () =>
        {
            watchers.NotifyChangedForTests();
            await Task.Delay(30);
            watchers.Dispose();
            await Task.Delay(80);
        });
        Assert.Null(thrown);
    }

    [Fact]
    public void Bridge_UsesPublisherAndDoesNotDisposeCtsWhileHeld()
    {
        var bridge = File.ReadAllText(FindRepoFile(Path.Combine("ExoLauncher", "Services", "ShellController.cs")));
        Assert.Contains("new LibraryUpdatedPublisher(PublishLibrarySnapshotAsync)", bridge, StringComparison.Ordinal);
        Assert.Contains("PostLibrarySnapshot", bridge, StringComparison.Ordinal);
        Assert.Contains("PeekCachedLibrary()", bridge, StringComparison.Ordinal);
        Assert.DoesNotContain("previous?.Dispose();\n        _ = PublishLibraryUpdatedAsync", bridge, StringComparison.Ordinal);
        Assert.Contains("catch (ObjectDisposedException)", bridge, StringComparison.Ordinal);
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

public sealed class EmptySetupHonestyTests
{
    [Fact]
    public async Task ZeroClientScan_OpensAnEmptyLibraryWithoutThrowingOrFakeCatalogs()
    {
        var library = new LibraryService(
            new IStoreAdapter[]
            {
                new AbsentStore(StoreKind.Steam, "steam", "Steam"),
                new AbsentStore(StoreKind.Epic, "epic", "Epic"),
                new AbsentStore(StoreKind.Gog, "gog", "GOG"),
                new AbsentStore(StoreKind.Riot, "riot", "Riot"),
                new AbsentStore(StoreKind.Xbox, "xbox", "Xbox app"),
                new AbsentStore(StoreKind.Ea, "ea", "EA app"),
                new AbsentStore(StoreKind.Ubisoft, "ubisoft", "Ubisoft Connect"),
                new AbsentStore(StoreKind.BattleNet, "battlenet", "Battle.net"),
                new AbsentStore(StoreKind.Amazon, "amazon", "Amazon Games"),
                new AbsentStore(StoreKind.Rockstar, "rockstar", "Rockstar Games Launcher"),
                new LocalAdapter(new SettingsService()),
            },
            new SettingsService());

        var thrown = await Record.ExceptionAsync(async () =>
        {
            var games = await library.GetLibraryAsync(force: true);
            Assert.DoesNotContain(games, game => game.Id.StartsWith("mock:", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(games, game =>
                string.Equals(game.Status, "Not installed", StringComparison.OrdinalIgnoreCase) &&
                !game.Installed &&
                game.Store is StoreKind.Xbox or StoreKind.Ea or StoreKind.Ubisoft or StoreKind.BattleNet or StoreKind.Amazon or StoreKind.Rockstar);
            var matrix = library.StoreMatrix();
            Assert.DoesNotContain(matrix, row =>
                string.Equals(row.detail, "Not installed", StringComparison.Ordinal));
            Assert.True(matrix.Count <= 1);
        });
        Assert.Null(thrown);
    }

    [Fact]
    public void StoreMatrix_OmitsAbsentOfficialClients()
    {
        var library = new LibraryService(
            new IStoreAdapter[]
            {
                new AbsentStore(StoreKind.Xbox, "xbox", "Xbox app"),
                new AbsentStore(StoreKind.Ea, "ea", "EA app"),
                new AbsentStore(StoreKind.Ubisoft, "ubisoft", "Ubisoft Connect"),
                new AbsentStore(StoreKind.BattleNet, "battlenet", "Battle.net"),
                new AbsentStore(StoreKind.Amazon, "amazon", "Amazon Games"),
                new AbsentStore(StoreKind.Rockstar, "rockstar", "Rockstar Games Launcher"),
            },
            new SettingsService());

        Assert.Empty(library.StoreMatrix());
    }

    [Fact]
    public async Task OfficialUpdateHandoff_DoesNotPublishCompleted()
    {
        var adapter = new HandoffUpdateAdapter();
        var launcher = new LaunchOrchestrator(
            new IStoreAdapter[] { adapter },
            new SettingsService(),
            new DependencyService());
        InstallProgress? last = null;
        launcher.ProgressChanged += p => last = p;

        var result = await launcher.UpdateAsync(new GameEntry
        {
            Id = "ea:apex",
            Title = "Apex Legends",
            Store = StoreKind.Ea,
            Installed = true,
            LaunchTarget = "Origin.OFR.50.0000001",
            Path = Path.GetTempPath(),
        });

        Assert.True(result.Ok);
        Assert.True(result.HandoffOnly);
        Assert.NotNull(last);
        Assert.NotEqual(InstallPhase.Completed, last!.Phase);
        Assert.Contains("Opened", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FirstRun_ZeroClients_CanFinishWithoutStuckOnboarding()
    {
        var onboarding = File.ReadAllText(FindHonestyFile(Path.Combine("ui", "src", "components", "OnboardingPanel.tsx")));
        var settings = File.ReadAllText(FindHonestyFile(Path.Combine("ui", "src", "components", "SettingsPanel.tsx")));
        Assert.Contains("Finish setup", onboarding, StringComparison.Ordinal);
        Assert.Contains("continue with an empty library", onboarding, StringComparison.Ordinal);
        Assert.Contains("No store apps were found in the last local check.", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("onSkip", onboarding, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Apply_NoOpsWhenTitleHasNoUpscalerDlls()
    {
        var root = Path.Combine(Path.GetTempPath(), "exo-apply-noop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var result = await new DlssSwapService().UpdateGameAsync(
                new GameEntry
                {
                    Id = "local:empty",
                    Title = "Empty",
                    Store = StoreKind.Local,
                    Installed = true,
                    Path = root,
                },
                CancellationToken.None);
            Assert.False(result.Ok);
            Assert.Equal(0, result.Updated);
            Assert.Contains("no swappable", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private sealed class AbsentStore : IStoreAdapter, IStoreClientPresence
    {
        public AbsentStore(StoreKind store, string id, string displayName)
        {
            Store = store;
            Id = id;
            DisplayName = displayName;
        }

        public StoreKind Store { get; }
        public string Id { get; }
        public string DisplayName { get; }
        public bool IsAgentPresent() => false;
        public bool IsClientPresent() => false;
        public Task<AuthResult> AuthenticateAsync(CancellationToken ct = default) =>
            Task.FromResult(new AuthResult { Ok = false, Message = DisplayName + " is not installed." });
        public Task<IReadOnlyList<GameEntry>> GetLibraryAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GameEntry>>(Array.Empty<GameEntry>());
        public Task<InstallResult> InstallAsync(GameEntry game, string? installPath, IProgress<InstallProgress>? progress, CancellationToken ct = default) =>
            Task.FromResult(new InstallResult { Ok = false, Message = "not used" });
        public Task<InstallResult> UpdateAsync(GameEntry game, IProgress<InstallProgress>? progress, CancellationToken ct = default) =>
            Task.FromResult(new InstallResult { Ok = false, Message = "not used" });
        public Task<LaunchResult> LaunchAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default) =>
            Task.FromResult(new LaunchResult { Ok = false, Message = "not used" });
        public Task<InstallResult> UninstallAsync(GameEntry game, CancellationToken ct = default) =>
            Task.FromResult(new InstallResult { Ok = false, Message = "not used" });
        public InstallProgress GetDownloadProgress(string gameId) =>
            new() { GameId = gameId, Phase = InstallPhase.Idle };
        public Task CleanupAfterExitAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class HandoffUpdateAdapter : IStoreAdapter
    {
        public StoreKind Store => StoreKind.Ea;
        public string Id => "ea";
        public string DisplayName => "EA app";
        public bool IsAgentPresent() => true;
        public Task<AuthResult> AuthenticateAsync(CancellationToken ct = default) =>
            Task.FromResult(new AuthResult { Ok = true });
        public Task<IReadOnlyList<GameEntry>> GetLibraryAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GameEntry>>(Array.Empty<GameEntry>());
        public Task<InstallResult> InstallAsync(GameEntry game, string? installPath, IProgress<InstallProgress>? progress, CancellationToken ct = default) =>
            Task.FromResult(new InstallResult { Ok = false, Message = "not used" });
        public Task<InstallResult> UpdateAsync(GameEntry game, IProgress<InstallProgress>? progress, CancellationToken ct = default) =>
            Task.FromResult(new InstallResult
            {
                Ok = true,
                HandoffOnly = true,
                Message = "Opened EA app to update Apex Legends.",
            });
        public Task<LaunchResult> LaunchAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default) =>
            Task.FromResult(new LaunchResult { Ok = false, Message = "not used" });
        public Task<InstallResult> UninstallAsync(GameEntry game, CancellationToken ct = default) =>
            Task.FromResult(new InstallResult { Ok = false, Message = "not used" });
        public InstallProgress GetDownloadProgress(string gameId) =>
            new() { GameId = gameId, Phase = InstallPhase.Idle };
        public Task CleanupAfterExitAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
private static string FindHonestyFile(string relative)
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
