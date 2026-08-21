using ExoLauncher.Adapters;
using ExoLauncher.Helpers;
using ExoLauncher.Models;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class CommanderPassTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "exo-commander-" + Guid.NewGuid().ToString("N"));

    public CommanderPassTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void GogReleaseKey_ParsesGalaxyAndBareIds()
    {
        Assert.True(GogGalaxySqlite.TryParseReleaseKey("gog_1423049311", out var id));
        Assert.Equal("1423049311", id);
        Assert.True(GogGalaxySqlite.TryParseReleaseKey("12345", out id));
        Assert.Equal("12345", id);
        Assert.False(GogGalaxySqlite.TryParseReleaseKey("steam_480", out _));
        Assert.False(GogGalaxySqlite.TryParseReleaseKey("", out _));
    }

    [Fact]
    public void SteamLeftoverCleanup_OnlyDeletesStaleNumericDownloadFolders()
    {
        var steam = Path.Combine(_root, "Steam");
        var downloading = Path.Combine(steam, "steamapps", "downloading");
        var stale = Path.Combine(downloading, "480");
        var keep = Path.Combine(downloading, "notes");
        Directory.CreateDirectory(stale);
        Directory.CreateDirectory(keep);
        File.WriteAllText(Path.Combine(stale, "chunk"), "x");
        File.WriteAllText(Path.Combine(keep, "x"), "x");
        Directory.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-3));
        File.SetLastWriteTimeUtc(Path.Combine(stale, "chunk"), DateTime.UtcNow.AddDays(-3));

        var removed = SteamLeftoverCleanup.CleanStale(steam, TimeSpan.FromHours(1));

        Assert.Equal(1, removed);
        Assert.False(Directory.Exists(stale));
        Assert.True(Directory.Exists(keep));
    }

    [Fact]
    public void SteamLeftoverCleanup_LeavesFoldersWithAppManifests()
    {
        var steam = Path.Combine(_root, "SteamLive");
        var downloading = Path.Combine(steam, "steamapps", "downloading", "570");
        Directory.CreateDirectory(downloading);
        File.WriteAllText(Path.Combine(steam, "steamapps", "appmanifest_570.acf"), "\"appid\" \"570\"");
        Directory.SetLastWriteTimeUtc(downloading, DateTime.UtcNow.AddDays(-4));

        Assert.Equal(0, SteamLeftoverCleanup.CleanStale(steam, TimeSpan.FromHours(1)));
        Assert.True(Directory.Exists(downloading));
    }

    [Fact]
    public void DlssSwap_DetectsAntiCheat_RejectsUnknownDlls_AndSwapsExisting()
    {
        var riot = new GameEntry { Id = "riot:valorant", Title = "VALORANT", Store = StoreKind.Riot, Installed = true, Path = _root };
        var fortnite = new GameEntry { Id = "epic:Fortnite", Title = "Fortnite", Store = StoreKind.Epic, Installed = true, Path = _root };
        var local = new GameEntry { Id = "local:ok", Title = "Control", Store = StoreKind.Local, Installed = true, Path = _root };
        Assert.True(DlssSwapService.IsAntiCheatProtected(riot, _root));
        Assert.True(DlssSwapService.IsAntiCheatProtected(fortnite, Path.Combine(_root, "EasyAntiCheat", "x.dll")));
        Assert.False(DlssSwapService.IsAntiCheatProtected(local, Path.Combine(_root, "bin", "nvngx_dlss.dll")));
        Assert.True(DlssSwapService.IsSafeDllName("nvngx_dlss.dll"));
        Assert.True(DlssSwapService.IsSafeDllName("nvngx_dlssg.dll"));
        Assert.True(DlssSwapService.IsSafeDllName("amd_fidelityfx_dx12.dll"));
        Assert.True(DlssSwapService.IsSafeDllName("amd_fidelityfx_loader_dx12.dll"));
        Assert.True(DlssSwapService.IsSafeDllName("amd_fidelityfx_upscaler_dx12.dll"));
        Assert.True(DlssSwapService.IsSafeDllName("libxess.dll"));
        Assert.True(DlssSwapService.IsSafeDllName("libxess_fg.dll"));
        Assert.True(DlssSwapService.IsSafeDllName("libxell.dll"));
        Assert.True(DlssSwapService.IsSafeDllName("amd_fidelityfx_framegeneration_dx12.dll"));
        Assert.True(DlssSwapService.IsSafeDllName("amd_fidelityfx_vk.dll"));
        Assert.Equal("DLSS", DlssSwapService.ClassifyKind("nvngx_dlss.dll"));
        Assert.Equal("Frame Generation", DlssSwapService.ClassifyKind("nvngx_dlssg.dll"));
        Assert.Equal("Ray Reconstruction", DlssSwapService.ClassifyKind("nvngx_dlssd.dll"));
        Assert.Equal("FSR", DlssSwapService.ClassifyKind("amd_fidelityfx_dx12.dll"));
        Assert.Equal("FSR 4", DlssSwapService.ClassifyKind("amd_fidelityfx_upscaler_dx12.dll"));
        Assert.Equal("FSR FG", DlssSwapService.ClassifyKind("amd_fidelityfx_framegeneration_dx12.dll"));
        Assert.Equal("FSR RR", DlssSwapService.ClassifyKind("amd_fidelityfx_denoiser_dx12.dll"));
        Assert.Equal("XeSS", DlssSwapService.ClassifyKind("libxess.dll"));
        Assert.Equal("XeSS FG", DlssSwapService.ClassifyKind("libxess_fg.dll"));
        Assert.Equal("XeLL", DlssSwapService.ClassifyKind("libxell.dll"));
        Assert.Equal("DLSS Super Resolution", DlssSwapService.DisplayName("DLSS"));
        Assert.Equal("FSR 4", DlssSwapService.DisplayName("FSR 4"));
        Assert.Equal("FSR Frame Generation", DlssSwapService.DisplayName("FSR FG"));
        Assert.Equal("XeSS Frame Generation", DlssSwapService.DisplayName("XeSS FG"));
        Assert.False(DlssSwapService.IsSafeDllName("version.dll"));
        Assert.False(DlssSwapService.IsSafeDllName("dxgi.dll"));
        Assert.False(DlssSwapService.IsSafeDllName("OptiScaler.dll"));
        Assert.True(DlssSwapService.VersionsCompatible("310.7.0.0", "310.7"));

        var gameDir = Path.Combine(_root, "Control");
        Directory.CreateDirectory(gameDir);
        var dest = Path.Combine(gameDir, "nvngx_dlss.dll");
        File.WriteAllBytes(dest, Enumerable.Repeat((byte)7, 1000).ToArray());
        var source = WriteFakeAmd64Pe(Path.Combine(_root, "pack", "nvngx_dlss.dll"));
        var game = new GameEntry
        {
            Id = "local:control",
            Title = "Control",
            Store = StoreKind.Local,
            Installed = true,
            Path = gameDir,
        };
        var svc = new DlssSwapService();
        var result = svc.ApplyPackToGame(game, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["nvngx_dlss.dll"] = source,
        }, "310.7");
        Assert.True(result.Ok);
        Assert.True(result.Updated >= 1);
        Assert.True(File.Exists(dest));
        Assert.True(File.Exists(dest + DlssSwapService.BackupSuffix));
        Assert.True(new FileInfo(dest).Length > 1000);

        var restore = svc.RestoreGame(game);
        Assert.True(restore.Updated >= 1);
        Assert.Equal(1000, new FileInfo(dest).Length);
    }

    [Fact]
    public void DlssSwap_RefusesGamesWithoutUpscalers()
    {
        var gameDir = Path.Combine(_root, "NoUpscaler");
        Directory.CreateDirectory(gameDir);
        var dlss = WriteFakeAmd64Pe(Path.Combine(_root, "pack", "inject", "nvngx_dlss.dll"));
        var fsr4 = WriteFakeAmd64Pe(Path.Combine(_root, "pack", "inject", "amd_fidelityfx_upscaler_dx12.dll"));
        var game = new GameEntry
        {
            Id = "local:noscale",
            Title = "No Scale",
            Store = StoreKind.Local,
            Installed = true,
            Path = gameDir,
        };
        var svc = new DlssSwapService();
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["nvngx_dlss.dll"] = dlss,
            ["amd_fidelityfx_upscaler_dx12.dll"] = fsr4,
        };
        var result = svc.ApplyPackToGame(game, files, "latest");
        Assert.False(result.Ok);
        Assert.Contains("no swappable", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(gameDir, "nvngx_dlss.dll")));
        Assert.False(File.Exists(Path.Combine(gameDir, "amd_fidelityfx_upscaler_dx12.dll")));
        Assert.False(File.Exists(Path.Combine(gameDir, DlssSwapService.SidecarName)));
        Assert.Empty(svc.Detect([game]));
    }

    [Fact]
    public void DlssSwap_SwapsExistingOnly_DoesNotAddMissingFiles()
    {
        var gameDir = Path.Combine(_root, "DlssOnly");
        Directory.CreateDirectory(gameDir);
        var dest = Path.Combine(gameDir, "nvngx_dlss.dll");
        File.WriteAllBytes(dest, Enumerable.Repeat((byte)7, 1000).ToArray());
        var dlss = WriteFakeAmd64Pe(Path.Combine(_root, "pack", "existing", "nvngx_dlss.dll"));
        var fsr = WriteFakeAmd64Pe(Path.Combine(_root, "pack", "existing", "amd_fidelityfx_dx12.dll"));
        var loader = WriteFakeAmd64Pe(Path.Combine(_root, "pack", "existing", "amd_fidelityfx_loader_dx12.dll"));
        var fsr4 = WriteFakeAmd64Pe(Path.Combine(_root, "pack", "existing", "amd_fidelityfx_upscaler_dx12.dll"));
        var game = new GameEntry
        {
            Id = "local:dlssonly",
            Title = "DLSS Only",
            Store = StoreKind.Local,
            Installed = true,
            Path = gameDir,
        };
        var svc = new DlssSwapService();
        var result = svc.ApplyPackToGame(game, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["nvngx_dlss.dll"] = dlss,
            ["amd_fidelityfx_dx12.dll"] = fsr,
            ["amd_fidelityfx_loader_dx12.dll"] = loader,
            ["amd_fidelityfx_upscaler_dx12.dll"] = fsr4,
        }, "latest");
        Assert.True(result.Ok);
        Assert.True(result.Updated >= 1);
        Assert.True(File.Exists(dest));
        Assert.True(new FileInfo(dest).Length > 1000);
        Assert.False(File.Exists(Path.Combine(gameDir, "amd_fidelityfx_dx12.dll")));
        Assert.False(File.Exists(Path.Combine(gameDir, "amd_fidelityfx_loader_dx12.dll")));
        Assert.False(File.Exists(Path.Combine(gameDir, "amd_fidelityfx_upscaler_dx12.dll")));
        Assert.False(File.Exists(Path.Combine(gameDir, DlssSwapService.SidecarName)));
    }

    [Fact]
    public void DlssSwap_SwapsFrameGenRayReconstructionAndXessWhenPresent()
    {
        var gameDir = Path.Combine(_root, "AllTech");
        Directory.CreateDirectory(gameDir);
        var names = new[]
        {
            "nvngx_dlssg.dll",
            "nvngx_dlssd.dll",
            "amd_fidelityfx_vk.dll",
            "amd_fidelityfx_framegeneration_dx12.dll",
            "libxess_fg.dll",
            "libxell.dll",
        };
        foreach (var name in names)
            File.WriteAllBytes(Path.Combine(gameDir, name), Enumerable.Repeat((byte)6, 1100).ToArray());

        var pack = names.ToDictionary(
            name => name,
            name =>
            {
                var path = WriteFakeAmd64Pe(Path.Combine(_root, "pack", "alltech", name));
                File.WriteAllBytes(path, File.ReadAllBytes(path).Concat(new byte[4000]).ToArray());
                return path;
            },
            StringComparer.OrdinalIgnoreCase);
        pack["nvngx_dlss.dll"] = WriteFakeAmd64Pe(Path.Combine(_root, "pack", "alltech", "nvngx_dlss.dll"));

        var game = new GameEntry
        {
            Id = "local:alltech",
            Title = "All Tech",
            Store = StoreKind.Local,
            Installed = true,
            Path = gameDir,
        };
        var result = new DlssSwapService().ApplyPackToGame(game, pack, "latest");
        Assert.True(result.Ok);
        Assert.True(result.Updated >= names.Length);
        foreach (var name in names)
            Assert.Equal(new FileInfo(pack[name]).Length, new FileInfo(Path.Combine(gameDir, name)).Length);
        Assert.False(File.Exists(Path.Combine(gameDir, "nvngx_dlss.dll")));
        Assert.False(File.Exists(Path.Combine(gameDir, "amd_fidelityfx_dx12.dll")));
    }

    [Fact]
    public void DlssSwap_WritesFsr31DestFromFsr31Pack_NotTheFsr4Loader()
    {
        var gameDir = Path.Combine(_root, "Fsr31Game");
        Directory.CreateDirectory(gameDir);
        var dest = Path.Combine(gameDir, "amd_fidelityfx_dx12.dll");
        File.WriteAllBytes(dest, Enumerable.Repeat((byte)5, 1200).ToArray());
        var fsr31 = WriteFakeAmd64Pe(Path.Combine(_root, "pack", "fsr", "amd_fidelityfx_dx12.dll"));
        File.WriteAllBytes(fsr31, File.ReadAllBytes(fsr31).Concat(new byte[4000]).ToArray());
        var loader = WriteFakeAmd64Pe(Path.Combine(_root, "pack", "fsr", "amd_fidelityfx_loader_dx12.dll"));
        File.WriteAllBytes(loader, File.ReadAllBytes(loader).Concat(new byte[8000]).ToArray());
        var upscaler = WriteFakeAmd64Pe(Path.Combine(_root, "pack", "fsr", "amd_fidelityfx_upscaler_dx12.dll"));
        File.WriteAllBytes(upscaler, File.ReadAllBytes(upscaler).Concat(new byte[16000]).ToArray());
        var frameGen = WriteFakeAmd64Pe(Path.Combine(_root, "pack", "fsr", "amd_fidelityfx_framegeneration_dx12.dll"));
        var game = new GameEntry
        {
            Id = "local:fsr31",
            Title = "FSR 3.1 Game",
            Store = StoreKind.Local,
            Installed = true,
            Path = gameDir,
        };
        var svc = new DlssSwapService();
        var result = svc.ApplyPackToGame(game, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["amd_fidelityfx_dx12.dll"] = fsr31,
            ["amd_fidelityfx_loader_dx12.dll"] = loader,
            ["amd_fidelityfx_upscaler_dx12.dll"] = upscaler,
            ["amd_fidelityfx_framegeneration_dx12.dll"] = frameGen,
        }, "latest");
        Assert.True(result.Ok);
        Assert.True(File.Exists(dest));
        Assert.Equal(new FileInfo(fsr31).Length, new FileInfo(dest).Length);
        Assert.NotEqual(new FileInfo(loader).Length, new FileInfo(dest).Length);
        Assert.False(File.Exists(Path.Combine(gameDir, "amd_fidelityfx_loader_dx12.dll")));
        Assert.False(File.Exists(Path.Combine(gameDir, "amd_fidelityfx_upscaler_dx12.dll")));
        Assert.False(File.Exists(Path.Combine(gameDir, "amd_fidelityfx_framegeneration_dx12.dll")));
        Assert.False(File.Exists(Path.Combine(gameDir, DlssSwapService.SidecarName)));

        var restore = svc.RestoreGame(game);
        Assert.True(restore.Ok);
        Assert.Equal(1200, new FileInfo(dest).Length);
        Assert.False(File.Exists(Path.Combine(gameDir, "amd_fidelityfx_upscaler_dx12.dll")));
        Assert.False(File.Exists(Path.Combine(gameDir, "amd_fidelityfx_framegeneration_dx12.dll")));
        Assert.False(File.Exists(Path.Combine(gameDir, DlssSwapService.SidecarName)));
        Assert.False(File.Exists(dest + DlssSwapService.BackupSuffix));
    }

    [Fact]
    public void DlssSwap_RestorePutsBackSwapperOriginals()
    {
        var gameDir = Path.Combine(_root, "SwapperGame");
        Directory.CreateDirectory(gameDir);
        var dest = Path.Combine(gameDir, "nvngx_dlss.dll");
        var original = Enumerable.Repeat((byte)3, 800).ToArray();
        var swapped = Enumerable.Repeat((byte)9, 2200).ToArray();
        File.WriteAllBytes(dest, swapped);
        File.WriteAllBytes(dest + DlssSwapService.SwapperBackupSuffix, original);
        File.WriteAllBytes(dest + DlssSwapService.BackupSuffix, swapped);
        File.WriteAllText(Path.Combine(gameDir, DlssSwapService.SidecarName),
            """{"Version":1,"Injected":["amd_fidelityfx_dx12.dll"]}""");
        File.WriteAllBytes(Path.Combine(gameDir, "amd_fidelityfx_dx12.dll"), Enumerable.Repeat((byte)4, 500).ToArray());

        var game = new GameEntry
        {
            Id = "local:swapper",
            Title = "Swapper Game",
            Store = StoreKind.Local,
            Installed = true,
            Path = gameDir,
        };
        var restore = new DlssSwapService().RestoreGame(game);
        Assert.True(restore.Ok);
        Assert.Equal(800, new FileInfo(dest).Length);
        Assert.True(File.Exists(dest + DlssSwapService.SwapperBackupSuffix));
        Assert.False(File.Exists(dest + DlssSwapService.BackupSuffix));
        Assert.False(File.Exists(Path.Combine(gameDir, "amd_fidelityfx_dx12.dll")));
        Assert.False(File.Exists(Path.Combine(gameDir, DlssSwapService.SidecarName)));
    }

    [Fact]
    public void DlssSwap_WriteWithBackupAdoptsSwapperOriginal()
    {
        var dest = Path.Combine(_root, "adopt", "nvngx_dlss.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var original = Enumerable.Repeat((byte)2, 600).ToArray();
        File.WriteAllBytes(dest, Enumerable.Repeat((byte)8, 1800).ToArray());
        File.WriteAllBytes(dest + DlssSwapService.SwapperBackupSuffix, original);
        var source = WriteFakeAmd64Pe(Path.Combine(_root, "adopt-pack", "nvngx_dlss.dll"));

        DlssSwapService.WriteWithBackup(dest, source);

        Assert.True(File.Exists(dest + DlssSwapService.BackupSuffix));
        Assert.Equal(600, new FileInfo(dest + DlssSwapService.BackupSuffix).Length);
        Assert.True(new FileInfo(dest).Length > 1800);
    }

    [Fact]
    public void DlssSwap_UnknownLatestIsNotNewest()
    {
        var present = new DlssSwapService.DetectedDll(
            Path.Combine(_root, "nvngx_dlss.dll"),
            "nvngx_dlss.dll",
            "DLSS",
            "local:gow",
            "God of War",
            "2.3.4.0",
            Eligible: true,
            SkipReason: null,
            LatestVersion: null,
            Present: true);
        Assert.False(DlssSwapService.IsPackCurrent([present]));

        var attached = DlssSwapService.AttachLatestVersions(
            [present],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["nvngx_dlss.dll"] = "310.4.0",
            });
        Assert.Equal("310.4.0", attached[0].LatestVersion);
        Assert.False(DlssSwapService.IsPackCurrent(attached));

        var current = attached[0] with { CurrentVersion = "310.4.0.0" };
        Assert.True(DlssSwapService.IsPackCurrent([current]));
    }

    [Fact]
    public void DlssSwap_FillMissingFromCache_RejectsUnsignedLocalPack()
    {
        var previous = Environment.GetEnvironmentVariable(PathHelper.DataDirOverrideVariable);
        Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, _root);
        try
        {
            WriteFakeAmd64Pe(Path.Combine(_root, "dlss", "dlss", "310.4.0", "nvngx_dlss.dll"));
            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            DlssSwapService.FillMissingFromCache(files);
            Assert.False(files.ContainsKey("nvngx_dlss.dll"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, previous);
        }
    }

    [Fact]
    public void DlssSwap_PruneOldCache_KeepsOnlyNewestFiles()
    {
        var cache = Path.Combine(_root, "dlss-cache");
        var keep = WriteFakeAmd64Pe(Path.Combine(cache, "dlss", "310.2.1", "nvngx_dlss.dll"));
        var stale = WriteFakeAmd64Pe(Path.Combine(cache, "dlss", "310.1.0", "nvngx_dlss.dll"));
        File.WriteAllBytes(Path.Combine(cache, "dlss", "310.2.1", "sdk.zip"), [1, 2, 3]);
        DlssSwapService.PruneOldCache(cache, [keep]);
        Assert.True(File.Exists(keep));
        Assert.False(File.Exists(stale));
        Assert.False(File.Exists(Path.Combine(cache, "dlss", "310.2.1", "sdk.zip")));
        Assert.False(Directory.Exists(Path.Combine(cache, "dlss", "310.1.0")));
    }

    private static string WriteFakeAmd64Pe(string path)
    {
        var bytes = new byte[40_000];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(bytes, 0x3C);
        bytes[0x80] = (byte)'P';
        bytes[0x81] = (byte)'E';
        BitConverter.GetBytes((ushort)0x8664).CopyTo(bytes, 0x84);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void LibraryDiskCache_RoundTripsAndRejectsOtherAccount()
    {
        var previous = Environment.GetEnvironmentVariable(PathHelper.DataDirOverrideVariable);
        Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, _root);
        try
        {
            var game = new GameEntry
            {
                Id = "steam:480",
                Title = "Spacewar",
                Store = StoreKind.Steam,
                Installed = true,
                Owned = false,
                EntitlementState = EntitlementState.NotOwned,
                LaunchTarget = "480",
            };
            LibraryDiskCache.Save([game], new Dictionary<string, string?> { ["epic"] = "acct-a" });
            var hit = LibraryDiskCache.TryLoad(new Dictionary<string, string?> { ["epic"] = "acct-a" });
            Assert.NotNull(hit);
            var restored = Assert.Single(hit!, item => item.Id == "steam:480");
            Assert.Equal(EntitlementState.NotOwned, restored.EntitlementState);
            Assert.Equal("none", restored.PrimaryAction);
            Assert.Null(LibraryDiskCache.TryLoad(new Dictionary<string, string?> { ["epic"] = "acct-b" }));
        }
        finally
        {
            Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, previous);
        }
    }

    [Fact]
    public async Task InstallQueuesADifferentGameAndRejectsSameGameOverlap()
    {
        var adapter = new QueueAdapter();
        var settings = new SettingsService(new AppSettings { AutoInstallRedistributables = false });
        var orchestrator = new LaunchOrchestrator(
            new IStoreAdapter[] { adapter },
            settings,
            new DependencyService());
        var first = new GameEntry { Id = "local:one", Title = "One", Store = StoreKind.Local, CanInstall = true };
        var second = new GameEntry { Id = "local:two", Title = "Two", Store = StoreKind.Local, CanInstall = true };

        var installOne = orchestrator.InstallAsync(first);
        await adapter.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var queued = await orchestrator.InstallAsync(second);
        var same = await orchestrator.InstallAsync(first);

        Assert.True(queued.Ok);
        Assert.True(queued.Queued);
        Assert.False(same.Ok);
        Assert.Contains("another", same.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("two", string.Join(',', orchestrator.QueuedGameIds), StringComparison.OrdinalIgnoreCase);

        adapter.Release.TrySetResult();
        var done = await installOne.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(done.Ok);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (Volatile.Read(ref adapter.Installs) < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(25);
        Assert.Equal(2, Volatile.Read(ref adapter.Installs));
    }

    private sealed class QueueAdapter : IStoreAdapter
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Installs;
        public StoreKind Store => StoreKind.Local;
        public string Id => "local";
        public string DisplayName => "Local";
        public bool IsAgentPresent() => true;
        public Task<AuthResult> AuthenticateAsync(CancellationToken ct = default) =>
            Task.FromResult(new AuthResult { Ok = true });
        public Task<IReadOnlyList<GameEntry>> GetLibraryAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GameEntry>>(Array.Empty<GameEntry>());
        public async Task<InstallResult> InstallAsync(
            GameEntry game, string? installPath, IProgress<InstallProgress>? progress, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Installs);
            Started.TrySetResult();
            await Release.Task.WaitAsync(ct);
            return new InstallResult { Ok = true, Message = "ok", Path = installPath };
        }
        public Task<InstallResult> UpdateAsync(GameEntry game, IProgress<InstallProgress>? progress, CancellationToken ct = default) =>
            Task.FromResult(new InstallResult { Ok = true, Message = "ok" });
        public Task<LaunchResult> LaunchAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default) =>
            Task.FromResult(new LaunchResult { Ok = true, Message = "ok" });
        public Task<InstallResult> UninstallAsync(GameEntry game, CancellationToken ct = default) =>
            Task.FromResult(new InstallResult { Ok = true, Message = "ok" });
        public InstallProgress GetDownloadProgress(string gameId) => new() { GameId = gameId };
        public Task CleanupAfterExitAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
