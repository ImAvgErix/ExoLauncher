using ExoLauncher.Adapters;
using ExoLauncher.Adapters.Cli;
using ExoLauncher.Models;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways installs, updates and launches could fail silently:
/// - a game Exo installs from GOG is recorded one folder above gogdl's game
///   folder, so gogdl launch finds no goggame-&lt;id&gt;.info and the game never starts;
/// - GOG Update runs repair, which verifies the old build and reports up to date;
/// - gogdl's "Progress: 45.23" (no '%') shows no percentage, the disk write speed
///   is shown as download speed, and raw tool lines (with the install path) reach the UI;
/// - an unreadable saved download queue refuses every later save, so no install
///   or update can start for the session;
/// - Cancel runs the job's continuations inline, the next queued job starts first,
///   and Cancel then marks that job "Cancelling…" (or throws on a cleared job);
/// - a play hold, meant to keep downloads waiting, also keeps a queued uninstall
///   or repair waiting, so its request gets no answer until the game closes;
/// - a restart rewrites a download the user paused as "paused by the restart",
///   so the start after that resumes it, while one a hold paused never resumes;
/// - a protocol launch that never produced a game holds it as starting for 30
///   minutes, and Stop, finding no process, cannot clear it.
/// </summary>
public sealed class InstallLaunchTests : IDisposable
{
    private readonly string _root = Path.Combine(TestDataDirectory.Root, "install-" + Guid.NewGuid().ToString("N"));

    public InstallLaunchTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp */ }
    }

    private const string GogId = "1207658924";

    [Fact]
    public async Task GogInstallRecordsTheFolderHoldingTheGameInfo()
    {
        var catalog = new InstalledGameCatalog(Path.Combine(_root, "installed-games.json"));
        IReadOnlyList<string>? ran = null;
        var adapter = new GogAdapter(null, null, catalog, gogdlPathOverride: "gogdl.exe",
            commandRunner: (_, args, _, onLine, _) =>
            {
                ran = args;
                // gogdl download appends the game's own folder name to --path.
                var path = args[args.ToList().IndexOf("--path") + 1];
                var gameFolder = Path.Combine(path, "Exo Fixture Game");
                Directory.CreateDirectory(gameFolder);
                File.WriteAllText(Path.Combine(gameFolder, $"goggame-{GogId}.info"), "{}");
                File.WriteAllText(Path.Combine(gameFolder, "game.exe"), "");
                onLine?.Invoke(@"[INFO] Install path: C:\Users\someone\Games\GOG");
                return Task.FromResult((0, "", ""));
            });

        var result = await adapter.InstallAsync(Game(), _root, progress: null, TestContext.Current.CancellationToken);

        Assert.True(result.Ok, result.Message);
        Assert.Contains("download", ran!);
        var expected = Path.Combine(_root, "GOG", GogId);
        Assert.Equal(expected, result.Path);
        Assert.True(File.Exists(Path.Combine(expected, $"goggame-{GogId}.info")));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(_root, "GOG"), GogAdapter.ManagedInstallStagingPrefix + "*"));
        Assert.Equal(expected, Assert.Single(catalog.GetInstalledGames(StoreKind.Gog)).Path);
        Assert.DoesNotContain(@"C:\", adapter.GetDownloadProgress("gog:" + GogId).Status);

        var removed = await adapter.UninstallAsync(Game(installedAt: expected), TestContext.Current.CancellationToken);
        Assert.True(removed.Ok, removed.Message);
        Assert.False(Directory.Exists(expected));
        Assert.Empty(catalog.GetInstalledGames(StoreKind.Gog));
    }

    [Fact]
    public async Task GogUpdateRunsUpdateOnTheGameFolderOfAnOlderInstall()
    {
        // Earlier builds recorded the parent of gogdl's game folder.
        var recorded = Path.Combine(_root, "GOG", GogId);
        var gameFolder = Path.Combine(recorded, "Exo Fixture Game");
        Directory.CreateDirectory(gameFolder);
        File.WriteAllText(Path.Combine(gameFolder, $"goggame-{GogId}.info"), "{}");
        IReadOnlyList<string>? ran = null;
        var adapter = new GogAdapter(null, null, new InstalledGameCatalog(Path.Combine(_root, "c.json")),
            gogdlPathOverride: "gogdl.exe",
            commandRunner: (_, args, _, _, _) => { ran = args; return Task.FromResult((0, "", "")); });

        var result = await adapter.UpdateAsync(Game(installedAt: recorded), progress: null, TestContext.Current.CancellationToken);

        Assert.True(result.Ok, result.Message);
        var command = ran!.SkipWhile(arg => arg != "update").ToArray();
        Assert.Equal(["update", GogId, "--platform", "windows", "--path", gameFolder], command);
        Assert.Equal(gameFolder, GogAdapter.GameFolder(recorded, GogId));
        Assert.Equal(gameFolder, GogAdapter.GameFolder(gameFolder, GogId));
    }

    [Theory]
    [InlineData("[DLManager] INFO: = Progress: 45.23% (8/1693), Running for 00:00:03, ETA: 00:11:50", 45.23)]
    [InlineData("[PROGRESS] INFO: = Progress: 45.23 8/1693, Running for: 00:00:03, ETA: 00:11:50", 45.23)]
    public void ProgressReadsPercentWithOrWithoutPercentSign(string line, double expected) =>
        Assert.Equal(expected, CliProgress.Percent(line));

    [Fact]
    public void ProgressSpeedIsTheNetworkFigureNotDiskWrites()
    {
        Assert.Equal(1.5 * 1024 * 1024,
            CliProgress.DownloadBytesPerSecond("[DLManager] INFO:  + Download\t- 1.50 MiB/s (raw) / 5.14 MiB/s (decompressed)"));
        Assert.Null(CliProgress.DownloadBytesPerSecond("[DLManager] INFO:  + Disk\t- 5.14 MiB/s (write) / 0.00 MiB/s (read)"));
        Assert.Null(CliProgress.DownloadBytesPerSecond("[DLManager] INFO:  - Downloaded: 4.53 MiB, Written: 15.42 MiB"));

        var sample = GogdlCli.ToProgress("gog:1", @"= Progress: 10.00 1/10, Install path: C:\Games\GOG", "Downloading…");
        Assert.Equal(10, sample.Percent);
        Assert.Equal("Downloading…", sample.Status);
    }

    [Fact]
    public void UnreadableDownloadQueueIsMovedAsideAndSavingContinues()
    {
        var path = Path.Combine(_root, "download-intents.json");
        File.WriteAllText(path, "{ not json");
        var store = new PendingDownloadStore(path);

        Assert.Empty(store.Load());
        Assert.Single(Directory.EnumerateFiles(_root, "download-intents.json.unreadable-*"));
        Assert.True(store.TrySave([], out var error), error);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void OversizedDownloadQueueDoesNotThrow()
    {
        var path = Path.Combine(_root, "download-intents.json");
        File.WriteAllText(path, new string(' ', PendingDownloadStore.ByteLimit + 1));
        Assert.Empty(new PendingDownloadStore(path).Load());
    }

    [Fact]
    public async Task CancelNeverMarksTheNextQueuedJob()
    {
        var adapter = new FakeAdapter();
        var orchestrator = Orchestrator(adapter);
        var first = orchestrator.InstallAsync(Local("a"), outer: TestContext.Current.CancellationToken);
        await adapter.Started["local:a"].Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var queued = await orchestrator.InstallAsync(Local("b"), outer: TestContext.Current.CancellationToken);
        Assert.True(queued.Queued, queued.Message);

        orchestrator.Cancel();

        await adapter.Started["local:b"].Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False((await first).Ok);
        var progress = orchestrator.CurrentProgress;
        Assert.Equal("local:b", progress.GameId);
        Assert.NotEqual("Cancelling…", progress.Status);
        adapter.Finish["local:b"].TrySetResult(new InstallResult { Ok = true, Message = "Installed." });
    }

    [Fact]
    public async Task AHoldKeepsDownloadsWaitingButNotAQueuedUninstall()
    {
        var adapter = new FakeAdapter();
        var orchestrator = Orchestrator(adapter);
        orchestrator.Hold("play", "Paused while you play.");
        var first = orchestrator.InstallAsync(Local("a"), outer: TestContext.Current.CancellationToken);
        await adapter.Started["local:a"].Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var uninstall = orchestrator.UninstallAsync(Local("b"), TestContext.Current.CancellationToken);

        orchestrator.Cancel();

        Assert.False((await first).Ok);
        var removed = await uninstall.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(removed.Ok, removed.Message);
    }

    [Fact]
    public void ARestartKeepsWhyADownloadWasPausedAndCarriesOnHeldOnes()
    {
        var path = Path.Combine(_root, "downloads.json");
        DownloadIntent Saved(string id, string state, string? reason) => new()
        {
            GameId = "epic:" + id, Title = "Exo Fixture " + id, Store = StoreKind.Epic, SourceId = "epic",
            Action = "update", State = state, ReviewReason = reason,
        };
        Assert.True(new PendingDownloadStore(path).TrySave(
            [Saved("paused", "review", "Paused."), Saved("held", LaunchOrchestrator.HeldState, "Downloads are paused while you play X."), Saved("gone", "review", "Paused.")],
            out var error), error);

        var orchestrator = new LaunchOrchestrator(new IStoreAdapter[] { new FakeAdapter(StoreKind.Epic) }, new SettingsService(),
            new DependencyService(), achievements: null, stopGame: null, downloadIntentStore: new PendingDownloadStore(path));
        orchestrator.Dequeue("epic:gone");

        var saved = new PendingDownloadStore(path).Load().ToDictionary(intent => intent.GameId);
        // The user's own Pause stays a pause: rewritten as "paused by the restart", the next start resumed it.
        Assert.Equal("Paused.", saved["epic:paused"].ReviewReason);
        Assert.Equal(2, saved.Count);
        Assert.StartsWith("Paused after restart", saved["epic:held"].ReviewReason);
    }

    [Fact]
    public async Task StopEndsAProtocolLaunchThatNeverStartedAGame()
    {
        var adapter = new FakeAdapter(StoreKind.Ea);
        var orchestrator = Orchestrator(adapter,
            stopGame: (_, _) => Task.FromResult(new GameStopResult(false, "Exo could not find this game running.", NothingRunning: true)));
        var game = new GameEntry
        {
            Id = "ea:exo-fixture", Title = "Exo Fixture Qx", Store = StoreKind.Ea, Installed = true, Owned = true,
            Path = _root, LaunchTarget = "exo-fixture",
        };
        Assert.True((await orchestrator.LaunchAsync(game, skipDeps: true, TestContext.Current.CancellationToken)).Ok);
        Assert.Contains("already running or starting", (await orchestrator.LaunchAsync(game, skipDeps: true, TestContext.Current.CancellationToken)).Message);

        var stop = await orchestrator.StopGameAsync(game, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.True(stop.Ok, stop.Message);
        Assert.True((await orchestrator.LaunchAsync(game, skipDeps: true, TestContext.Current.CancellationToken)).Ok);
        await orchestrator.StopGameAsync(game, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    private static GameEntry Game(string? installedAt = null) => new()
    {
        Id = "gog:" + GogId, Title = "Exo Fixture Game", Store = StoreKind.Gog, Owned = true,
        CanInstall = installedAt is null, Installed = installedAt is not null, Path = installedAt,
    };

    private static GameEntry Local(string id) => new()
    {
        Id = "local:" + id, Title = "Exo Fixture " + id, Store = StoreKind.Local, Owned = true, CanInstall = true,
    };

    private LaunchOrchestrator Orchestrator(
        IStoreAdapter adapter,
        Func<GameEntry, CancellationToken, Task<GameStopResult>>? stopGame = null) =>
        new(new IStoreAdapter[] { adapter }, new SettingsService(), new DependencyService(),
            new AchievementService([], Path.Combine(_root, "achievements.json"), deferStateLoad: true),
            stopGame,
            beginQuietGameSession: _ => new Nothing(),
            missingDependencies: _ => [],
            closeUnusedStoreClients: _ => Task.CompletedTask);

    private sealed class Nothing : IDisposable
    {
        public void Dispose() { }
    }

    /// <summary>Install waits until cancelled or finished by the test; launch returns a store handoff.</summary>
    private sealed class FakeAdapter(StoreKind store = StoreKind.Local) : IStoreAdapter
    {
        internal readonly Dictionary<string, TaskCompletionSource> Started = new()
        {
            ["local:a"] = new(TaskCreationOptions.RunContinuationsAsynchronously),
            ["local:b"] = new(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        internal readonly Dictionary<string, TaskCompletionSource<InstallResult>> Finish = new()
        {
            ["local:a"] = new(TaskCreationOptions.RunContinuationsAsynchronously),
            ["local:b"] = new(TaskCreationOptions.RunContinuationsAsynchronously),
        };

        public StoreKind Store => store;
        public string Id => store.ToString().ToLowerInvariant();
        public string DisplayName => "Fixture";
        public bool IsAgentPresent() => true;
        public Task<AuthResult> AuthenticateAsync(CancellationToken ct = default) => Task.FromResult(new AuthResult());
        public Task<IReadOnlyList<GameEntry>> GetLibraryAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GameEntry>>([]);

        public async Task<InstallResult> InstallAsync(GameEntry game, string? installPath,
            IProgress<InstallProgress>? progress, CancellationToken ct = default)
        {
            progress?.Report(new InstallProgress { GameId = game.Id, Phase = InstallPhase.Downloading, Status = "Downloading…" });
            Started[game.Id].TrySetResult();
            if (game.Id != "local:a") return await Finish[game.Id].Task;
            try
            {
                // Task.Delay resumes inline on the thread that cancels it, as a
                // real backend's awaited process exit does.
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException) { }
            return new InstallResult { Ok = false, Message = "Canceled." };
        }

        public Task<InstallResult> UpdateAsync(GameEntry game, IProgress<InstallProgress>? progress, CancellationToken ct = default) =>
            InstallAsync(game, null, progress, ct);

        public Task<LaunchResult> LaunchAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default) =>
            Task.FromResult(new LaunchResult { Ok = true, HandoffOnly = true, Message = "Opened the store." });

        public Task<InstallResult> UninstallAsync(GameEntry game, CancellationToken ct = default) =>
            Task.FromResult(new InstallResult { Ok = true });

        public InstallProgress GetDownloadProgress(string gameId) => new() { GameId = gameId };
        public Task CleanupAfterExitAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default) => Task.CompletedTask;
    }
}

/// <summary>
/// Ways saved state and uninstallers could fail silently:
/// - one wishlist read that fails (file busy or corrupt) is cached as empty and
///   the next change saves that, erasing the list; or a busy one is answered as
///   empty, so the Store says the wishlist is empty;
/// - an MSI uninstall runs /qn, which cannot ask for admin rights, so a
///   per-machine game fails to uninstall and Exo waits 20 minutes;
/// - an exe uninstaller that needs admin rights cannot start without shell execute;
/// - an unquoted uninstall command is cut at the first space ("C:\Program") and never starts;
/// - paused Amazon downloads never resume because Amazon has no account scope.
/// </summary>
public sealed class SavedStateTests : IDisposable
{
    private readonly string _root = Path.Combine(TestDataDirectory.Root, "state-" + Guid.NewGuid().ToString("N"));
    private readonly string? _prior = Environment.GetEnvironmentVariable(Helpers.PathHelper.DataDirOverrideVariable);

    public SavedStateTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "store"));
        Environment.SetEnvironmentVariable(Helpers.PathHelper.DataDirOverrideVariable, _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(Helpers.PathHelper.DataDirOverrideVariable, _prior);
        try { Directory.Delete(_root, recursive: true); } catch { /* temp */ }
    }

    [Fact]
    public void ABusyWishlistIsNeverOverwritten()
    {
        var file = Path.Combine(_root, "store", "wishlist-v2.json");
        const string saved = """[{"AppId":"620","Name":"Portal 2","AddedAt":"2026-01-01T00:00:00+00:00"}]""";
        File.WriteAllText(file, saved);
        using var store = new StoreService("test");
        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            // Busy reads as unknown, never as an empty list, and a change is refused rather than saved over it.
            Assert.Null(store.Wishlist());
            Assert.Null(store.Wish("400", "Portal", add: false));
        }
        Assert.Equal(saved, File.ReadAllText(file));
        Assert.Equal("620", Assert.Single(store.Wishlist()!).AppId);
    }

    [Fact]
    public void ACorruptWishlistIsMovedAsideAndTheListCarriesOn()
    {
        var file = Path.Combine(_root, "store", "wishlist-v2.json");
        File.WriteAllText(file, "[{ broken");
        using var store = new StoreService("test");
        Assert.Empty(store.Wishlist()!);
        Assert.Single(Directory.EnumerateFiles(Path.Combine(_root, "store"), "wishlist-v2.json.unreadable-*"));
    }

    [Fact]
    public void MsiUninstallCanAskForAdminRights()
    {
        var start = OfficialInstalledLibraries.BuildUninstallStartInfo(
            "MsiExec.exe /I{11111111-2222-3333-4444-555555555555}")!;
        Assert.True(start.UseShellExecute);
        Assert.Equal("/X {11111111-2222-3333-4444-555555555555} /passive /norestart", start.Arguments);
        Assert.Null(OfficialInstalledLibraries.BuildUninstallStartInfo("msiexec /I{11111111-2222-3333-4444-555555555555} TRANSFORMS=x.mst"));

        var exe = OfficialInstalledLibraries.BuildUninstallStartInfo(@"""C:\Games\Fixture\unins000.exe"" /SILENT")!;
        Assert.True(exe.UseShellExecute);
        Assert.Equal("/SILENT", exe.Arguments);

        // Unquoted, with a space in the path: the program is the whole path up to ".exe".
        var unquoted = OfficialInstalledLibraries.BuildUninstallStartInfo(@"C:\Program Files\Exo Fixture\unins000.exe /SILENT")!;
        Assert.Equal(@"C:\Program Files\Exo Fixture\unins000.exe", unquoted.FileName);
        Assert.Equal("/SILENT", unquoted.Arguments);
    }

    [Fact]
    public void NileAccountIdComesFromEitherSessionFile()
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [Path.Combine("a", "user.json")] = """{"extensions":{"customer_info":{"user_id":"amzn1.account.X"}},"tokens":{}}""",
            [Path.Combine("b", "current_user.json")] = """{"user_id":"amzn1.account.Y"}""",
        };
        string? Read(string path) => files.GetValueOrDefault(path);

        Assert.Equal("amzn1.account.X", NileCli.ActiveAccountId(new[] { "a" }, Read));
        Assert.Equal("amzn1.account.Y", NileCli.ActiveAccountId(new[] { "b" }, Read));
        Assert.Null(NileCli.ActiveAccountId(new[] { "c" }, Read));
    }
}
