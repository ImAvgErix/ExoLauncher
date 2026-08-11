using ExoLauncher.Adapters;
using ExoLauncher.Models;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class InstallDurabilityTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "exo-install-durability-" + Guid.NewGuid().ToString("N"));

    public InstallDurabilityTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best effort */ }
    }

    [Fact]
    public async Task GogManagedInstall_UsesPerTitlePath_AndSurvivesRestartForUpdateAndUninstall()
    {
        var requestedBase = Path.Combine(_root, "Games");
        Assert.True(
            InstalledGameCatalog.TryCreateGogInstallLocation(
                requestedBase,
                "1423049311",
                out var location,
                out var locationError),
            locationError);
        Assert.Equal(Path.Combine(requestedBase, "GOG"), location.ManagedRoot);
        Assert.Equal(Path.Combine(requestedBase, "GOG", "1423049311"), location.InstallPath);

        var catalogPath = Path.Combine(_root, "state", "installed-games.json");
        var catalog = new InstalledGameCatalog(catalogPath);
        using var ownedLibrary = new GogOwnedLibraryService(
            cachePath: Path.Combine(_root, "state", "gog-owned.json"));
        IReadOnlyList<string>? installArgs = null;
        var installingAdapter = new GogAdapter(
            authService: null,
            ownedLibrary,
            catalog,
            gogdlPathOverride: "test-gogdl.exe",
            commandRunner: (fileName, args, _, _, _) =>
            {
                Assert.Equal("test-gogdl.exe", fileName);
                installArgs = args;
                var downloadPath = GetOptionValue(args, "--path");
                Directory.CreateDirectory(downloadPath);
                File.WriteAllText(Path.Combine(downloadPath, "game.bin"), "installed");
                return Task.FromResult((0, string.Empty, string.Empty));
            });
        var install = await installingAdapter.InstallAsync(
            new GameEntry
            {
                Id = "gog:1423049311",
                Title = "Durable GOG Game",
                Store = StoreKind.Gog,
                Installed = false,
                Owned = true,
                CanInstall = true,
            },
            requestedBase,
            progress: null);
        Assert.True(install.Ok, install.Message);
        Assert.Equal(location.InstallPath, install.Path);
        Assert.NotNull(installArgs);
        var stagedDownloadPath = GetOptionValue(installArgs!, "--path");
        Assert.NotEqual(location.InstallPath, stagedDownloadPath);
        Assert.Equal(location.ManagedRoot, Path.GetDirectoryName(stagedDownloadPath));
        Assert.StartsWith(
            GogAdapter.ManagedInstallStagingPrefix,
            Path.GetFileName(stagedDownloadPath),
            StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(location.InstallPath, "game.bin")));
        Assert.DoesNotContain(
            Directory.EnumerateDirectories(location.ManagedRoot),
            directory => Path.GetFileName(directory).StartsWith(
                GogAdapter.ManagedInstallStagingPrefix,
                StringComparison.OrdinalIgnoreCase));

        var afterForcedRescan = Assert.Single(
            await installingAdapter.GetLibraryAsync(),
            game => game.Id == "gog:1423049311");
        Assert.Equal(location.InstallPath, afterForcedRescan.Path);

        // A new catalog and adapter instance model a complete process restart.
        var restarted = new InstalledGameCatalog(catalogPath);
        var restartedAdapter = new GogAdapter(
            authService: null,
            ownedLibrary,
            restarted);
        var restored = Assert.Single(
            await restartedAdapter.GetLibraryAsync(),
            game => game.Id == "gog:1423049311");
        Assert.Equal(location.InstallPath, restored.Path); // UpdateAsync receives this exact path.
        Assert.True(restored.Installed);

        var uninstall = await restartedAdapter.UninstallAsync(restored);
        Assert.True(uninstall.Ok, uninstall.Message);
        Assert.False(Directory.Exists(location.InstallPath));
        Assert.Empty(new InstalledGameCatalog(catalogPath).GetInstalledGames(StoreKind.Gog));
    }

    [Fact]
    public void GogUninstall_DoesNotDeleteAnArbitraryExistingInstall()
    {
        var external = Path.Combine(_root, "Existing GOG Game");
        Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(external, "keep.sav"), "keep");
        var catalog = new InstalledGameCatalog(Path.Combine(_root, "installed-games.json"));
        var game = new GameEntry
        {
            Id = "gog:1207659000",
            Title = "Existing GOG Game",
            Store = StoreKind.Gog,
            Installed = true,
            Path = external,
        };

        var uninstall = catalog.UninstallRegistered(game);

        Assert.False(uninstall.Ok);
        Assert.True(File.Exists(Path.Combine(external, "keep.sav")));
    }

    [Fact]
    public async Task GogInstall_RefusesToAdoptAnExistingUnregisteredTitleDirectory()
    {
        var requestedBase = Path.Combine(_root, "Games");
        Assert.True(InstalledGameCatalog.TryCreateGogInstallLocation(
            requestedBase,
            "1423049311",
            out var location,
            out var locationError), locationError);
        Directory.CreateDirectory(location.InstallPath);
        var existingFile = Path.Combine(location.InstallPath, "keep.sav");
        await File.WriteAllTextAsync(existingFile, "user data");
        var commandRan = false;
        var catalog = new InstalledGameCatalog(Path.Combine(_root, "state", "installed-games.json"));
        var adapter = new GogAdapter(
            authService: null,
            ownedLibraryService: null,
            catalog,
            gogdlPathOverride: "test-gogdl.exe",
            commandRunner: (_, _, _, _, _) =>
            {
                commandRan = true;
                return Task.FromResult((0, string.Empty, string.Empty));
            });

        var result = await adapter.InstallAsync(
            new GameEntry
            {
                Id = "gog:1423049311",
                Title = "Existing destination",
                Store = StoreKind.Gog,
                Owned = true,
                CanInstall = true,
            },
            requestedBase,
            progress: null);

        Assert.False(result.Ok);
        Assert.Contains("already exists", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(commandRan);
        Assert.True(File.Exists(existingFile));
        Assert.Empty(catalog.GetInstalledGames(StoreKind.Gog));
    }

    [Fact]
    public async Task GogInstall_FailedDownloadRemovesStagingAndRetryCanSucceed()
    {
        var requestedBase = Path.Combine(_root, "Gog Retry Games");
        Assert.True(InstalledGameCatalog.TryCreateGogInstallLocation(
            requestedBase,
            "1445250340",
            out var location,
            out var locationError), locationError);
        var catalog = new InstalledGameCatalog(Path.Combine(_root, "gog-retry-state", "installed-games.json"));
        var game = new GameEntry
        {
            Id = "gog:1445250340",
            Title = "Retryable GOG Game",
            Store = StoreKind.Gog,
            Owned = true,
            CanInstall = true,
        };
        var failingAdapter = new GogAdapter(
            authService: null,
            ownedLibraryService: null,
            catalog,
            gogdlPathOverride: "test-gogdl.exe",
            commandRunner: (_, args, _, _, _) =>
            {
                var downloadPath = GetOptionValue(args, "--path");
                Directory.CreateDirectory(downloadPath);
                File.WriteAllText(Path.Combine(downloadPath, "partial.bin"), "partial");
                return Task.FromResult((7, string.Empty, "download failed"));
            });

        var failed = await failingAdapter.InstallAsync(game, requestedBase, progress: null);

        Assert.False(failed.Ok);
        Assert.False(Directory.Exists(location.InstallPath));
        Assert.Empty(catalog.GetInstalledGames(StoreKind.Gog));
        Assert.Empty(Directory.EnumerateFileSystemEntries(location.ManagedRoot));

        var retryAdapter = new GogAdapter(
            authService: null,
            ownedLibraryService: null,
            catalog,
            gogdlPathOverride: "test-gogdl.exe",
            commandRunner: (_, args, _, _, _) =>
            {
                var downloadPath = GetOptionValue(args, "--path");
                Directory.CreateDirectory(downloadPath);
                File.WriteAllText(Path.Combine(downloadPath, "game.bin"), "installed");
                return Task.FromResult((0, string.Empty, string.Empty));
            });

        var retry = await retryAdapter.InstallAsync(game, requestedBase, progress: null);

        Assert.True(retry.Ok, retry.Message);
        Assert.Equal(location.InstallPath, retry.Path);
        Assert.True(File.Exists(Path.Combine(location.InstallPath, "game.bin")));
    }

    [Fact]
    public async Task GogInstall_CancellationRemovesOnlyAttemptStaging()
    {
        var requestedBase = Path.Combine(_root, "Gog Cancel Games");
        Assert.True(InstalledGameCatalog.TryCreateGogInstallLocation(
            requestedBase,
            "1297352192",
            out var location,
            out var locationError), locationError);
        var catalog = new InstalledGameCatalog(Path.Combine(_root, "gog-cancel-state", "installed-games.json"));
        var adapter = new GogAdapter(
            authService: null,
            ownedLibraryService: null,
            catalog,
            gogdlPathOverride: "test-gogdl.exe",
            commandRunner: (_, args, _, _, ct) =>
            {
                var downloadPath = GetOptionValue(args, "--path");
                Directory.CreateDirectory(downloadPath);
                File.WriteAllText(Path.Combine(downloadPath, "partial.bin"), "partial");
                throw new OperationCanceledException(ct);
            });

        var result = await adapter.InstallAsync(
            new GameEntry
            {
                Id = "gog:1297352192",
                Title = "Cancelled GOG Game",
                Store = StoreKind.Gog,
                Owned = true,
                CanInstall = true,
            },
            requestedBase,
            progress: null);

        Assert.False(result.Ok);
        Assert.Contains("cancel", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(location.InstallPath));
        Assert.Empty(catalog.GetInstalledGames(StoreKind.Gog));
        Assert.Empty(Directory.EnumerateFileSystemEntries(location.ManagedRoot));
    }

    [Fact]
    public async Task GogInstall_RegistrationFailureRollsBackPromotedInstall()
    {
        var requestedBase = Path.Combine(_root, "Gog Catalog Failure Games");
        Assert.True(InstalledGameCatalog.TryCreateGogInstallLocation(
            requestedBase,
            "1706297109",
            out var location,
            out var locationError), locationError);
        var blockedStateParent = Path.Combine(_root, "blocked-gog-state-parent");
        var catalog = new InstalledGameCatalog(Path.Combine(blockedStateParent, "installed-games.json"));
        File.WriteAllText(blockedStateParent, "this file prevents catalog directory creation");
        var adapter = new GogAdapter(
            authService: null,
            ownedLibraryService: null,
            catalog,
            gogdlPathOverride: "test-gogdl.exe",
            commandRunner: (_, args, _, _, _) =>
            {
                var downloadPath = GetOptionValue(args, "--path");
                Directory.CreateDirectory(downloadPath);
                File.WriteAllText(Path.Combine(downloadPath, "game.bin"), "installed");
                return Task.FromResult((0, string.Empty, string.Empty));
            });

        var result = await adapter.InstallAsync(
            new GameEntry
            {
                Id = "gog:1706297109",
                Title = "Manifest Failure GOG Game",
                Store = StoreKind.Gog,
                Owned = true,
                CanInstall = true,
            },
            requestedBase,
            progress: null);

        Assert.False(result.Ok);
        Assert.Contains("manifest", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(location.InstallPath));
        Assert.Empty(catalog.GetInstalledGames(StoreKind.Gog));
        Assert.Empty(Directory.EnumerateFileSystemEntries(location.ManagedRoot));
    }

    [Fact]
    public async Task PortableInPlace_InstallRescanRestartAndUninstall_OnlyChangesRegistration()
    {
        var portable = Path.Combine(_root, "Portable Outside Scan Roots");
        Directory.CreateDirectory(portable);
        var exe = Path.Combine(portable, "PortableGame.exe");
        File.WriteAllText(exe, "MZ-fake");
        var catalogPath = Path.Combine(_root, "state", "installed-games.json");
        var adapter = new LocalAdapter(
            settings: null,
            new InstalledGameCatalog(catalogPath),
            copyPortableIntoLibrary: false);

        var install = await adapter.InstallAsync(LocalAdapter.CreateAddPortableEntry(), portable, null);
        Assert.True(install.Ok, install.Message);

        var forcedRescan = await adapter.GetLibraryAsync();
        var installed = Assert.Single(forcedRescan, game =>
            game.Id != LocalAdapter.AddPortableId &&
            string.Equals(game.Path, portable, StringComparison.OrdinalIgnoreCase));

        var restarted = new LocalAdapter(
            settings: null,
            new InstalledGameCatalog(catalogPath),
            copyPortableIntoLibrary: false);
        var afterRestart = Assert.Single(
            await restarted.GetLibraryAsync(),
            game => game.Id == installed.Id);
        Assert.Equal(exe, afterRestart.LaunchTarget);

        var uninstall = await restarted.UninstallAsync(afterRestart);
        Assert.True(uninstall.Ok, uninstall.Message);
        Assert.True(File.Exists(exe));
        Assert.DoesNotContain(
            await new LocalAdapter(
                    settings: null,
                    new InstalledGameCatalog(catalogPath),
                    copyPortableIntoLibrary: false)
                .GetLibraryAsync(),
            game => game.Id == installed.Id);
    }

    [Fact]
    public async Task PortableCopiedInstall_UninstallDeletesOnlyManagedCopy()
    {
        var portable = Path.Combine(_root, "Portable Copy Source");
        Directory.CreateDirectory(portable);
        var sourceExe = Path.Combine(portable, "PortableGame.exe");
        File.WriteAllText(sourceExe, "MZ-fake");
        var libraryRoot = Path.Combine(_root, "managed-library");
        var catalogPath = Path.Combine(_root, "state", "installed-games.json");
        var adapter = new LocalAdapter(
            settings: null,
            new InstalledGameCatalog(catalogPath),
            copyPortableIntoLibrary: true,
            managedLibraryRoot: libraryRoot);

        var install = await adapter.InstallAsync(LocalAdapter.CreateAddPortableEntry(), portable, null);
        Assert.True(install.Ok, install.Message);
        Assert.NotEqual(Path.GetFullPath(portable), Path.GetFullPath(install.Path!));
        Assert.True(File.Exists(sourceExe));
        Assert.True(Directory.Exists(install.Path));
        Assert.DoesNotContain(
            Directory.EnumerateDirectories(libraryRoot),
            directory => LocalAdapter.IsManagedCopyStagingDirectory(Path.GetFileName(directory)));

        var restarted = new LocalAdapter(
            settings: null,
            new InstalledGameCatalog(catalogPath),
            copyPortableIntoLibrary: false);
        var copied = Assert.Single(
            await restarted.GetLibraryAsync(),
            game => string.Equals(game.Path, install.Path, StringComparison.OrdinalIgnoreCase));
        var uninstall = await restarted.UninstallAsync(copied);

        Assert.True(uninstall.Ok, uninstall.Message);
        Assert.False(Directory.Exists(install.Path));
        Assert.True(File.Exists(sourceExe));
    }

    [Fact]
    public async Task PortableCopiedInstall_CancellationRemovesStagingAndKeepsSource()
    {
        var portable = Path.Combine(_root, "Portable Cancel Source");
        Directory.CreateDirectory(portable);
        var sourceExe = Path.Combine(portable, "PortableGame.exe");
        File.WriteAllText(sourceExe, "MZ-fake");
        File.WriteAllText(Path.Combine(portable, "data.bin"), "data");
        var libraryRoot = Path.Combine(_root, "cancelled-managed-library");
        var catalog = new InstalledGameCatalog(Path.Combine(_root, "cancel-state", "installed-games.json"));
        var adapter = new LocalAdapter(
            settings: null,
            catalog,
            copyPortableIntoLibrary: true,
            managedLibraryRoot: libraryRoot);
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress<InstallProgress>(value =>
        {
            if (value.Phase == InstallPhase.Installing)
                cancellation.Cancel();
        });

        var install = await adapter.InstallAsync(
            LocalAdapter.CreateAddPortableEntry(),
            portable,
            progress,
            cancellation.Token);

        Assert.False(install.Ok);
        Assert.Contains("cancel", install.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(sourceExe));
        Assert.Empty(catalog.GetInstalledGames(StoreKind.Local));
        Assert.Empty(Directory.EnumerateFileSystemEntries(libraryRoot));
    }

    [Fact]
    public async Task PortableCopiedInstall_PreservesPreexistingFinalDestination()
    {
        var portable = Path.Combine(_root, "Portable Existing Destination Source");
        Directory.CreateDirectory(portable);
        var sourceExe = Path.Combine(portable, "PortableGame.exe");
        File.WriteAllText(sourceExe, "MZ-fake");
        var displayTitle = Path.GetFileName(portable);
        var libraryRoot = Path.Combine(_root, "existing-managed-library");
        var destination = Path.Combine(
            libraryRoot,
            LocalAdapter.ManagedPortableFolderName(portable, displayTitle));
        Directory.CreateDirectory(destination);
        var marker = Path.Combine(destination, "keep.sav");
        File.WriteAllText(marker, "keep");
        var catalog = new InstalledGameCatalog(Path.Combine(_root, "existing-state", "installed-games.json"));
        var adapter = new LocalAdapter(
            settings: null,
            catalog,
            copyPortableIntoLibrary: true,
            managedLibraryRoot: libraryRoot);

        var install = await adapter.InstallAsync(LocalAdapter.CreateAddPortableEntry(), portable, null);

        Assert.False(install.Ok);
        Assert.Contains("already exists", install.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("keep", File.ReadAllText(marker));
        Assert.True(File.Exists(sourceExe));
        Assert.Empty(catalog.GetInstalledGames(StoreKind.Local));
        Assert.DoesNotContain(
            Directory.EnumerateDirectories(libraryRoot),
            directory => LocalAdapter.IsManagedCopyStagingDirectory(Path.GetFileName(directory)));
    }

    [Fact]
    public async Task PortableCopiedInstall_RegistrationFailureRollsBackPromotedCopy()
    {
        var portable = Path.Combine(_root, "Portable Catalog Failure Source");
        Directory.CreateDirectory(portable);
        var sourceExe = Path.Combine(portable, "PortableGame.exe");
        File.WriteAllText(sourceExe, "MZ-fake");
        var libraryRoot = Path.Combine(_root, "failed-registration-library");
        var blockedStateParent = Path.Combine(_root, "blocked-state-parent");
        var catalog = new InstalledGameCatalog(Path.Combine(blockedStateParent, "installed-games.json"));
        File.WriteAllText(blockedStateParent, "this file prevents catalog directory creation");
        var adapter = new LocalAdapter(
            settings: null,
            catalog,
            copyPortableIntoLibrary: true,
            managedLibraryRoot: libraryRoot);

        var install = await adapter.InstallAsync(LocalAdapter.CreateAddPortableEntry(), portable, null);

        Assert.False(install.Ok);
        Assert.Contains("registration", install.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(sourceExe));
        Assert.Empty(catalog.GetInstalledGames(StoreKind.Local));
        Assert.Empty(Directory.EnumerateFileSystemEntries(libraryRoot));
    }

    [Fact]
    public async Task PortableCopyStagingDirectory_IsNeverDiscoveredAsAGame()
    {
        var libraryRoot = Path.Combine(_root, "orphan-staging-library");
        var staging = Path.Combine(libraryRoot, LocalAdapter.ManagedCopyStagingPrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "PartialGame.exe"), "MZ-partial");
        var adapter = new LocalAdapter(
            settings: null,
            new InstalledGameCatalog(Path.Combine(_root, "orphan-state", "installed-games.json")),
            copyPortableIntoLibrary: false,
            managedLibraryRoot: libraryRoot);

        var library = await adapter.GetLibraryAsync();

        Assert.DoesNotContain(
            library,
            game => string.Equals(game.Path, staging, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    private static string GetOptionValue(IReadOnlyList<string> args, string option)
    {
        for (var i = 0; i + 1 < args.Count; i++)
        {
            if (string.Equals(args[i], option, StringComparison.Ordinal))
                return args[i + 1];
        }

        throw new Xunit.Sdk.XunitException($"Missing command option {option}.");
    }
}
