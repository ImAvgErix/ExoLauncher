using ExoLauncher.Adapters;
using ExoLauncher.Models;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class SteamOwnershipCatalogTests
{
    [Fact]
    public async Task RefreshAfterUninstall_PreservesProvenSteamOwnershipForSearchAndInstall()
    {
        var path = NewCatalogPath();
        var adapter = new MutableSteamAdapter(InstalledSteamGame("424242", "Known Steam Game"));
        var catalog = new SteamOwnershipCatalog(path);
        var library = new LibraryService(
            new IStoreAdapter[] { adapter },
            new SettingsService(),
            catalog);

        var before = await library.GetLibraryAsync(force: true);
        Assert.Contains(before, game => game.Id == "steam:424242" && game.Installed);

        adapter.Games = Array.Empty<GameEntry>();
        library.Invalidate();
        var after = await library.GetLibraryAsync(force: true);

        var preserved = Assert.Single(after, game => game.Id == "steam:424242");
        Assert.False(preserved.Installed);
        Assert.True(preserved.Owned);
        Assert.True(preserved.CanInstall);
        Assert.Equal("install", preserved.PrimaryAction);
        var installTarget = library.Find("steam:424242");
        Assert.NotNull(installTarget);
        Assert.Equal("install", installTarget.PrimaryAction);

        var search = new StoreSearchService();
        var hits = await search.SearchAsync("424242", after);
        var hit = Assert.Single(hits, result => result.Id == "steam:424242");
        Assert.True(hit.Owned);
        Assert.True(hit.CanInstall);
        Assert.False(hit.Installed);
    }

    [Fact]
    public async Task RestartWithNoManifest_RecoversProvenSteamOwnershipFromDisk()
    {
        var path = NewCatalogPath();
        var installed = new MutableSteamAdapter(InstalledSteamGame("515151", "Restart Proof"));
        var first = new LibraryService(
            new IStoreAdapter[] { installed },
            new SettingsService(),
            new SteamOwnershipCatalog(path));
        await first.GetLibraryAsync(force: true);

        var restarted = new LibraryService(
            new IStoreAdapter[] { new MutableSteamAdapter() },
            new SettingsService(),
            new SteamOwnershipCatalog(path));
        var games = await restarted.GetLibraryAsync(force: true);

        var recovered = Assert.Single(games, game => game.Id == "steam:515151");
        Assert.False(recovered.Installed);
        Assert.True(recovered.Owned);
        Assert.True(recovered.CanInstall);
    }

    [Fact]
    public void CorruptPrimary_RecoversLastKnownGoodCatalogBackup()
    {
        var path = NewCatalogPath();
        var catalog = new SteamOwnershipCatalog(path);
        catalog.RememberInstalled(new[] { InstalledSteamGame("626262", "Backup Proof") });
        File.WriteAllText(path, "{ this is not valid json");

        var recovered = new SteamOwnershipCatalog(path);
        var games = recovered.RestoreMissing(Array.Empty<GameEntry>());

        var game = Assert.Single(games);
        Assert.Equal("steam:626262", game.Id);
        Assert.True(game.Owned);
        Assert.True(game.CanInstall);

        File.Delete(path + ".bak");
        var healedPrimary = new SteamOwnershipCatalog(path);
        Assert.Single(healedPrimary.RestoreMissing(Array.Empty<GameEntry>()));
    }

    [Fact]
    public void RememberInstalled_KeepsManifestProofWhenSteamTicketIsMissing()
    {
        var path = NewCatalogPath();
        var catalog = new SteamOwnershipCatalog(path);
        catalog.RememberInstalled(new[]
        {
            new GameEntry
            {
                Id = "steam:424242",
                Title = "Installed Without Ticket",
                Store = StoreKind.Steam,
                Installed = true,
                Owned = false,
                CanInstall = true,
                LaunchTarget = "424242",
            },
        });

        var recovered = Assert.Single(catalog.RestoreMissing(Array.Empty<GameEntry>()));
        Assert.Equal("steam:424242", recovered.Id);
        Assert.True(recovered.Owned);
        Assert.True(recovered.CanInstall);
        Assert.False(recovered.Installed);
    }

    [Fact]
    public void RememberInstalled_DoesNotTrustUninstalledOrNonSteamEntries()
    {
        var path = NewCatalogPath();
        var catalog = new SteamOwnershipCatalog(path);
        catalog.RememberInstalled(new[]
        {
            new GameEntry
            {
                Id = "steam:737373",
                Title = "Unproven Search Result",
                Store = StoreKind.Steam,
                Installed = false,
                Owned = true,
                CanInstall = true,
                LaunchTarget = "737373",
            },
            new GameEntry
            {
                Id = "epic:not-steam",
                Title = "Different Store",
                Store = StoreKind.Epic,
                Installed = true,
                Owned = true,
                CanInstall = true,
                LaunchTarget = "not-steam",
            },
        });

        Assert.Empty(catalog.RestoreMissing(Array.Empty<GameEntry>()));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task CatalogWriteFailure_DoesNotBreakInstalledLibraryRefresh()
    {
        var blocker = NewCatalogPath();
        Directory.CreateDirectory(Path.GetDirectoryName(blocker)!);
        File.WriteAllText(blocker, "this file deliberately blocks a child directory");
        var unwritableCatalogPath = Path.Combine(blocker, "steam-owned.json");
        var adapter = new MutableSteamAdapter(InstalledSteamGame("848484", "Still Visible"));
        var library = new LibraryService(
            new IStoreAdapter[] { adapter },
            new SettingsService(),
            new SteamOwnershipCatalog(unwritableCatalogPath));

        var games = await library.GetLibraryAsync(force: true);

        Assert.Contains(games, game => game.Id == "steam:848484" && game.Installed);

        File.Delete(blocker);
        Directory.CreateDirectory(blocker);
        await library.GetLibraryAsync(force: true);

        var restarted = new SteamOwnershipCatalog(unwritableCatalogPath);
        Assert.Contains(
            restarted.RestoreMissing(Array.Empty<GameEntry>()),
            game => game.Id == "steam:848484");
    }

    [Fact]
    public async Task UnmarkedSteamAdapter_DoesNotSeedManifestOwnershipCatalog()
    {
        var path = NewCatalogPath();
        var adapter = new UnprovenSteamAdapter(InstalledSteamGame("959595", "Not Manifest Proven"));
        var library = new LibraryService(
            new IStoreAdapter[] { adapter },
            new SettingsService(),
            new SteamOwnershipCatalog(path));
        await library.GetLibraryAsync(force: true);

        adapter.Games = Array.Empty<GameEntry>();
        library.Invalidate();
        var after = await library.GetLibraryAsync(force: true);

        Assert.DoesNotContain(after, game => game.Id == "steam:959595");
        Assert.False(File.Exists(path));
    }

    private static GameEntry InstalledSteamGame(string appId, string title) => new()
    {
        Id = "steam:" + appId,
        Title = title,
        Store = StoreKind.Steam,
        Installed = true,
        Owned = true,
        CanInstall = true,
        LaunchTarget = appId,
        SizeBytes = 123456,
        Status = "Ready",
        Deps = new[] { "Steam client" },
    };

    private static string NewCatalogPath()
    {
        var dir = Path.Combine(
            Path.GetTempPath(),
            "ExoLauncherTests",
            "steam-ownership",
            Guid.NewGuid().ToString("N"));
        return Path.Combine(dir, "steam-owned.json");
    }

    private sealed class MutableSteamAdapter(params GameEntry[] games) : IStoreAdapter, IInstalledSteamManifestSource
    {
        public IReadOnlyList<GameEntry> Games { get; set; } = games;
        public StoreKind Store => StoreKind.Steam;
        public string Id => "steam";
        public string DisplayName => "Steam";
        public bool IsAgentPresent() => true;
        public Task<AuthResult> AuthenticateAsync(CancellationToken ct = default) =>
            Task.FromResult(new AuthResult { Ok = true });
        public Task<IReadOnlyList<GameEntry>> GetLibraryAsync(CancellationToken ct = default) =>
            Task.FromResult(Games);
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

    private sealed class UnprovenSteamAdapter(params GameEntry[] games) : IStoreAdapter
    {
        public IReadOnlyList<GameEntry> Games { get; set; } = games;
        public StoreKind Store => StoreKind.Steam;
        public string Id => "steam-unproven";
        public string DisplayName => "Unproven Steam-shaped adapter";
        public bool IsAgentPresent() => true;
        public Task<AuthResult> AuthenticateAsync(CancellationToken ct = default) =>
            Task.FromResult(new AuthResult { Ok = true });
        public Task<IReadOnlyList<GameEntry>> GetLibraryAsync(CancellationToken ct = default) =>
            Task.FromResult(Games);
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
}
