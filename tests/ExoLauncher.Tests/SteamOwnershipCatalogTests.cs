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
        var adapter = new MutableSteamAdapter(InstalledSteamGame("424242", "Known Steam Game"))
        {
            LastAuthoritativeOwnedAppIds = Owned("424242"),
        };
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
        var installed = new MutableSteamAdapter(InstalledSteamGame("515151", "Restart Proof"))
        {
            LastAuthoritativeOwnedAppIds = Owned("515151"),
        };
        var first = new LibraryService(
            new IStoreAdapter[] { installed },
            new SettingsService(),
            new SteamOwnershipCatalog(path));
        await first.GetLibraryAsync(force: true);

        var restarted = new LibraryService(
            new IStoreAdapter[] { new MutableSteamAdapter { AccountScope = installed.AccountScope } },
            new SettingsService(),
            new SteamOwnershipCatalog(path));
        var games = await restarted.GetLibraryAsync(force: true);

        var recovered = Assert.Single(games, game => game.Id == "steam:515151");
        Assert.False(recovered.Installed);
        Assert.True(recovered.Owned);
        Assert.True(recovered.CanInstall);
    }

    [Fact]
    public void RestoreMissing_DropsEntriesExcludedByAuthoritativeOwnershipSnapshot()
    {
        var path = NewCatalogPath();
        var catalog = new SteamOwnershipCatalog(path);
        catalog.RememberVerifiedInstalled(
            "scope",
            new[] { InstalledSteamGame("515151", "Refunded Proof") },
            Owned("515151"));

        var restored = catalog.RestoreMissing("scope", Array.Empty<GameEntry>(),
            new HashSet<string>(StringComparer.Ordinal));

        Assert.Empty(restored);
    }

    [Fact]
    public async Task LibraryRefresh_DropsStaleManifestProofWhenAuthoritativeSnapshotExcludesIt()
    {
        var path = NewCatalogPath();
        var adapter = new MutableSteamAdapter(InstalledSteamGame("717171", "Refunded Library Entry"))
        {
            LastAuthoritativeOwnedAppIds = Owned("717171"),
        };
        var library = new LibraryService(
            new IStoreAdapter[] { adapter },
            new SettingsService(),
            new SteamOwnershipCatalog(path));

        await library.GetLibraryAsync(force: true);
        adapter.LastAuthoritativeOwnedAppIds = new HashSet<string>(StringComparer.Ordinal);
        adapter.Games = Array.Empty<GameEntry>();
        library.Invalidate();

        var refreshed = await library.GetLibraryAsync(force: true);

        Assert.DoesNotContain(refreshed, game => game.Id == "steam:717171");

        // A later offline refresh must not resurrect a title already revoked by
        // an authoritative empty snapshot.
        var reloaded = new SteamOwnershipCatalog(path);
        Assert.Empty(reloaded.RestoreMissing("scope-a", Array.Empty<GameEntry>()));
    }

    [Fact]
    public async Task AuthoritativeExclusion_KeepsInstalledFilesButDoesNotClaimOwnershipOrDownload()
    {
        var adapter = new MutableSteamAdapter(InstalledSteamGame("818181", "Refunded But Installed"))
        {
            LastAuthoritativeOwnedAppIds = new HashSet<string>(StringComparer.Ordinal),
        };
        var library = new LibraryService(
            new IStoreAdapter[] { adapter },
            new SettingsService(),
            new SteamOwnershipCatalog(NewCatalogPath()));

        var game = Assert.Single(await library.GetLibraryAsync(force: true));

        Assert.True(game.Installed);
        Assert.False(game.Owned);
        Assert.False(game.CanInstall);
        Assert.Equal(EntitlementState.NotOwned, game.EntitlementState);
        Assert.Equal("Buy again", game.Status);
        Assert.Equal("none", game.PrimaryAction);
    }

    [Fact]
    public async Task OwnershipUnavailable_KeepsInstalledFilesAsUnverifiedNotOwned()
    {
        var adapter = new MutableSteamAdapter(InstalledSteamGame("828282", "Offline Install"));
        var library = new LibraryService(
            new IStoreAdapter[] { adapter },
            new SettingsService(),
            new SteamOwnershipCatalog(NewCatalogPath()));

        var game = Assert.Single(await library.GetLibraryAsync(force: true));

        Assert.True(game.Installed);
        Assert.False(game.Owned);
        Assert.False(game.CanInstall);
        Assert.Equal(EntitlementState.Unverified, game.EntitlementState);
        Assert.Equal("Ownership unverified", game.Status);
    }

    [Fact]
    public async Task SameAccountOffline_RestoresOnlyLastVerifiedOwnedTitle()
    {
        var path = NewCatalogPath();
        var adapter = new MutableSteamAdapter(InstalledSteamGame("838383", "Same Account Proof"))
        {
            AccountScope = "scope-a",
            LastAuthoritativeOwnedAppIds = Owned("838383"),
        };
        var first = new LibraryService(
            new IStoreAdapter[] { adapter },
            new SettingsService(),
            new SteamOwnershipCatalog(path));
        await first.GetLibraryAsync(force: true);

        adapter.Games = Array.Empty<GameEntry>();
        adapter.LastAuthoritativeOwnedAppIds = null;
        var offline = new LibraryService(
            new IStoreAdapter[] { adapter },
            new SettingsService(),
            new SteamOwnershipCatalog(path));

        var restored = Assert.Single(await offline.GetLibraryAsync(force: true));
        Assert.Equal("steam:838383", restored.Id);
        Assert.True(restored.Owned);
        Assert.True(restored.CanInstall);
        Assert.Equal(EntitlementState.Owned, restored.EntitlementState);
        Assert.Contains("last verified", restored.LaunchNote, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AccountSwitch_DoesNotRestorePreviousAccountsVerifiedOwnership()
    {
        var path = NewCatalogPath();
        var adapter = new MutableSteamAdapter(InstalledSteamGame("848484", "First Account Game"))
        {
            AccountScope = "scope-a",
            LastAuthoritativeOwnedAppIds = Owned("848484"),
        };
        var first = new LibraryService(
            new IStoreAdapter[] { adapter },
            new SettingsService(),
            new SteamOwnershipCatalog(path));
        await first.GetLibraryAsync(force: true);

        adapter.AccountScope = "scope-b";
        adapter.Games = Array.Empty<GameEntry>();
        adapter.LastAuthoritativeOwnedAppIds = null;
        var switched = new LibraryService(
            new IStoreAdapter[] { adapter },
            new SettingsService(),
            new SteamOwnershipCatalog(path));

        Assert.Empty(await switched.GetLibraryAsync(force: true));
    }

    [Fact]
    public void CorruptPrimary_RecoversLastKnownGoodCatalogBackup()
    {
        var path = NewCatalogPath();
        var catalog = new SteamOwnershipCatalog(path);
        catalog.RememberVerifiedInstalled(
            "legacy-unscoped",
            new[] { InstalledSteamGame("626262", "Backup Proof") },
            Owned("626262"));
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
    public void LegacyManifestOnlyCatalog_IsRejectedAndItsBackupIsReplacedAfterVerifiedWrite()
    {
        var path = NewCatalogPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        const string legacy = """
            {"version":2,"games":[{"accountScope":"scope-a","appId":"636363","title":"Legacy Manifest Claim","sizeBytes":1}]}
            """;
        File.WriteAllText(path, legacy);
        File.WriteAllText(path + ".bak", legacy);

        var catalog = new SteamOwnershipCatalog(path);
        Assert.Empty(catalog.RestoreMissing("scope-a", Array.Empty<GameEntry>()));

        catalog.RememberVerifiedInstalled(
            "scope-a",
            new[] { InstalledSteamGame("646464", "Verified Replacement") },
            Owned("646464"));
        File.WriteAllText(path, "{ corrupt primary");

        var recovered = new SteamOwnershipCatalog(path);
        var replacement = Assert.Single(
            recovered.RestoreMissing("scope-a", Array.Empty<GameEntry>()));
        Assert.Equal("steam:646464", replacement.Id);
    }

    [Fact]
    public void PruneToAuthoritative_RevocationAlsoUpdatesRecoveryBackup()
    {
        var path = NewCatalogPath();
        var catalog = new SteamOwnershipCatalog(path);
        catalog.RememberVerifiedInstalled(
            "scope-a",
            new[] { InstalledSteamGame("656565", "Refunded Before Corruption") },
            Owned("656565"));
        catalog.PruneToAuthoritative(
            "scope-a",
            new HashSet<string>(StringComparer.Ordinal));
        File.WriteAllText(path, "{ corrupt primary");

        var recovered = new SteamOwnershipCatalog(path);

        Assert.Empty(recovered.RestoreMissing("scope-a", Array.Empty<GameEntry>()));
    }

    [Fact]
    public void RememberVerifiedInstalled_DoesNotPromoteManifestHistoryExcludedBySnapshot()
    {
        var path = NewCatalogPath();
        var catalog = new SteamOwnershipCatalog(path);
        catalog.RememberVerifiedInstalled(
            "legacy-unscoped",
            new[]
            {
                new GameEntry
                {
                    Id = "steam:424242",
                    Title = "Installed Without Current License",
                    Store = StoreKind.Steam,
                    Installed = true,
                    Owned = true,
                    CanInstall = true,
                    LaunchTarget = "424242",
                },
            },
            new HashSet<string>(StringComparer.Ordinal));

        Assert.Empty(catalog.RestoreMissing(Array.Empty<GameEntry>()));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void RememberVerifiedInstalled_DoesNotTrustUninstalledOrNonSteamEntries()
    {
        var path = NewCatalogPath();
        var catalog = new SteamOwnershipCatalog(path);
        catalog.RememberVerifiedInstalled(
            "legacy-unscoped",
            new[]
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
            },
            Owned("737373"));

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
        var adapter = new MutableSteamAdapter(InstalledSteamGame("848484", "Still Visible"))
        {
            LastAuthoritativeOwnedAppIds = Owned("848484"),
        };
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
            restarted.RestoreMissing(adapter.AccountScope!, Array.Empty<GameEntry>()),
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

    private static IReadOnlySet<string> Owned(params string[] appIds) =>
        appIds.ToHashSet(StringComparer.Ordinal);

    private sealed class MutableSteamAdapter(params GameEntry[] games) : IStoreAdapter, IInstalledSteamManifestSource, IAuthoritativeOwnershipSource, IStoreAccountScope
    {
        public IReadOnlyList<GameEntry> Games { get; set; } = games;
        public IReadOnlySet<string>? LastAuthoritativeOwnedAppIds { get; set; }
        public string? AccountScope { get; set; } = "scope-a";
        public string? GetActiveAccountScope() => AccountScope;
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
