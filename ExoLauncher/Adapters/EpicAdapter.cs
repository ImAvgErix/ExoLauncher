using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using ExoLauncher.Adapters.Cli;
using ExoLauncher.Helpers;
using ExoLauncher.Models;
using ExoLauncher.Services;

namespace ExoLauncher.Adapters;

/// <summary>
/// Epic via Legendary CLI — true no-Epic-GUI path when Legendary is present.
/// https://github.com/legendary-gl/legendary
/// </summary>
public sealed class EpicAdapter : IStoreAdapter, IStoreClientPresence, IStoreAccountScope, IStoreRepair
{
    private readonly ConcurrentDictionary<string, InstallProgress> _progress = new(StringComparer.OrdinalIgnoreCase);
    private readonly EpicAuthService? _authService;
    private static readonly TimeSpan LegendaryUninstallTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan LegendarySessionProbeTimeout = TimeSpan.FromSeconds(45);
    private static readonly string[] EpicBootstrapProcessNames =
    [
        "EpicGamesLauncher", "EpicWebHelper", "CrashReportClient",
        "Launcher", "EasyAntiCheat", "EasyAntiCheat_EOS",
        "EpicOnlineServices", "EOSOverlayRenderer-Win64-Shipping",
    ];
    private static readonly TimeSpan NewGameProcessConfirmationDelay = TimeSpan.FromMilliseconds(750);

    public EpicAdapter(EpicAuthService? authService = null) => _authService = authService;

    public StoreKind Store => StoreKind.Epic;
    public string Id => "epic";
    public string DisplayName => "Epic";

    public string? GetActiveAccountScope() => EpicPlaytime.GetActiveAccountScope();

    // Legendary is an intentionally headless backend. Do not let it make
    // Settings claim that the separately installed Epic Games Launcher exists.
    public bool IsAgentPresent() => ResolveLegendary() is not null || ResolveEpicLauncher() is not null;
    public bool IsClientPresent() => ResolveEpicLauncher() is not null;

    public async Task<AuthResult> AuthenticateAsync(CancellationToken ct = default)
    {
        try
        {
            string? legendary;
            try
            {
                legendary = ResolveLegendary() ?? await DownloadLegendaryAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLog.Warn("EnsureLegendary failed: " + ex.Message);
                return new AuthResult
                {
                    Ok = false,
                    RequiresUserAction = true,
                    Message = "Exo could not download what it needs to sign in to Epic. Check your connection, then Sign in again.",
                };
            }

            if (await HasValidLegendarySessionAsync(legendary, ct).ConfigureAwait(false))
            {
                return new AuthResult
                {
                    Ok = true,
                    RequiresUserAction = false,
                    Message = "Epic account is already connected.",
                };
            }

            if (_authService is null)
            {
                return new AuthResult
                {
                    Ok = false,
                    RequiresUserAction = true,
                    Message = "Epic sign-in service is unavailable. Restart Exo and try again.",
                };
            }

            // Do not use `auth --import`: Legendary documents that it logs the
            // Epic Games Launcher out. Do not run bare `auth` either: the
            // frozen Windows build waits on stdin after opening a browser.
            return await _authService.SignInAsync(legendary, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new AuthResult
            {
                Ok = false,
                Message = "Epic sign-in was canceled.",
                RequiresUserAction = true,
            };
        }
        catch (Exception ex)
        {
            AppLog.Warn("Epic sign-in failed: " + ex.GetType().Name + ": " + ex.Message);
            return new AuthResult { Ok = false, Message = "Epic sign-in did not finish. Try again.", RequiresUserAction = true };
        }
    }

    private static async Task<bool> HasValidLegendarySessionAsync(string legendary, CancellationToken ct)
    {
        if (EpicPlaytime.HasUnexpiredLegendarySession(DateTimeOffset.UtcNow)) return true;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(LegendarySessionProbeTimeout);

        try
        {
            var (exitCode, stdout, _) = await CliRunner.RunJsonAsync(
                    legendary, LegendaryCli.ListOwnedArgs(includeNonInstallable: false), null, timeout.Token)
                .ConfigureAwait(false);
            return LegendaryCli.IsAuthenticatedLibraryResponse(exitCode, stdout);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            AppLog.Debug("Legendary session verification timed out.");
            return false;
        }
        catch (Exception ex)
        {
            AppLog.Debug("Legendary session verification failed: " + ex.Message);
            return false;
        }
    }

    /// <summary>AMD64 only. Reject ARM64 binaries (Windows shows "Machine Type Mismatch").</summary>
    private const ushort PeMachineAmd64 = 0x8664;
    private static readonly GitHubReleaseAsset LegendaryReleaseAsset = new(
        "derrod",
        "legendary",
        "0.21.0",
        "legendary_windows_x64.exe",
        ExpectedSize: 17_610_944,
        ExpectedSha256: "4c01a14c0acb0c46069b197ae7212ea4ea6b861661126ca0593cdac31658fb01",
        OwnerAliases: ["legendary-gl"]);

    /// <summary>Legendary as one of Exo's parts (<see cref="ExoComponents"/>): setup fetches it into Exo's tools.</summary>
    internal static readonly ExoComponent Component = ExoComponent.Pinned("Legendary for Epic Games", "Epic Games",
        LegendaryReleaseAsset, () => Path.Combine(PathHelper.AppDataDir, "tools", "legendary.exe"), IsValidAmd64Pe);

    /// <summary>Download legendary_windows_x64.exe into Exo tools if absent / wrong arch.</summary>
    private static Task<string> DownloadLegendaryAsync(CancellationToken ct)
    {
        var tools = Path.Combine(PathHelper.AppDataDir, "tools");
        Directory.CreateDirectory(tools);
        var dest = Path.Combine(tools, "legendary.exe");
        return VerifiedGitHubReleaseDownloader.Shared.DownloadPinnedAsync(
            LegendaryReleaseAsset,
            dest,
            IsValidAmd64Pe,
            ct);
    }

    private static async Task<string?> EnsureLegendaryAsync(CancellationToken ct)
    {
        try
        {
            return await DownloadLegendaryAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Warn("EnsureLegendary failed: " + ex.Message);
            return null;
        }
    }

    private static bool IsValidAmd64Pe(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
            var len = new FileInfo(path).Length;
            if (len < 1_000_000) return false;
            using var fs = File.OpenRead(path);
            Span<byte> dos = stackalloc byte[64];
            if (fs.Read(dos) < 64) return false;
            if (dos[0] != (byte)'M' || dos[1] != (byte)'Z') return false;
            var peOff = BitConverter.ToInt32(dos.Slice(0x3C, 4));
            if (peOff < 0 || peOff > len - 6) return false;
            fs.Position = peOff;
            Span<byte> pe = stackalloc byte[6];
            if (fs.Read(pe) < 6) return false;
            // PE\0\0
            if (pe[0] != (byte)'P' || pe[1] != (byte)'E') return false;
            var machine = BitConverter.ToUInt16(pe.Slice(4, 2));
            return machine == PeMachineAmd64;
        }
        catch
        {
            return false;
        }
    }

    private static int _eglSyncScheduled;
    private const int OwnedCacheSchemaVersion = 2;
    private static readonly TimeSpan OwnedListTimeout = TimeSpan.FromSeconds(300);
    private const string OwnedCacheProvenance = "legendary-authenticated-list";
    private static readonly ConcurrentDictionary<string, byte> OwnedRefreshInFlight =
        new(StringComparer.Ordinal);
    internal static event Action? OwnedLibraryCacheUpdated;
    private static int _installedRefreshScheduled;
    private static readonly object InstalledRowsGate = new();
    private static readonly TimeSpan InstalledRowsTtl = TimeSpan.FromMinutes(2);
    private static IReadOnlyList<LegendaryCli.GameRow>? _installedRows;
    private static DateTimeOffset _installedRowsAtUtc;

    public async Task<IReadOnlyList<GameEntry>> GetLibraryAsync(CancellationToken ct = default)
    {
        var playtimes = EpicPlaytime.GetCachedMinutes();
        // Hours live in Legendary/Heroic user.json, not legendary.exe. A friend
        // with Heroic or a session file and no CLI must still get a refresh.
        EpicPlaytime.RefreshCachedMinutes();

        var legendary = ResolveLegendary();
        var hasLegendary = legendary is not null;
        var accountScope = GetActiveAccountScope();

        // Native first. A hung `legendary list-installed` used to throw on the
        // library 25s timeout and skip EGL manifests, emptying Epic.
        var native = ReadNativeInstalledLibrary(hasLegendary);

        if (legendary is not null)
        {
            // Spawning legendary here cost about a second of every scan to enrich
            // rows the native read already produced. Use the last CLI answer and
            // refresh it off the scan; install watchers trigger the next rescan.
            var cliRows = CachedLegendaryInstalledRows();
            if (cliRows.Count > 0)
                native = EnrichWithLegendaryCli(native, cliRows);
            if (!ct.IsCancellationRequested)
            {
                ScheduleInstalledRowsRefresh(legendary);
                ScheduleEglSyncImport(legendary);
                ScheduleOwnedLibraryRefresh(legendary, accountScope);
            }
        }

        return EpicPlaytime.Apply(MergeOwned(native, ReadOwnedCache(accountScope), hasLegendary), playtimes);
    }

    /// <summary>Last `legendary list-installed` answer, or empty before the first one lands.</summary>
    internal static IReadOnlyList<LegendaryCli.GameRow> CachedLegendaryInstalledRows()
    {
        lock (InstalledRowsGate)
        {
            return _installedRows ?? Array.Empty<LegendaryCli.GameRow>();
        }
    }

    /// <summary>
    /// Refreshes the CLI view off the scan thread. Scans stay on disk reads; the
    /// install watchers already trigger the rescan that picks this up.
    /// </summary>
    private static void ScheduleInstalledRowsRefresh(string legendary)
    {
        lock (InstalledRowsGate)
        {
            if (_installedRows is not null &&
                DateTimeOffset.UtcNow - _installedRowsAtUtc < InstalledRowsTtl)
                return;
        }

        if (Interlocked.CompareExchange(ref _installedRefreshScheduled, 1, 0) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var rows = await TryListLegendaryInstalledAsync(legendary, timeout.Token).ConfigureAwait(false);
                lock (InstalledRowsGate)
                {
                    _installedRows = rows;
                    _installedRowsAtUtc = DateTimeOffset.UtcNow;
                }
            }
            catch { /* the native read already carries installed Epic titles */ }
            finally
            {
                Interlocked.Exchange(ref _installedRefreshScheduled, 0);
            }
        });
    }

    /// <summary>
    /// EGL item files, LauncherInstalled.dat, the new launcher's installations.jsonl,
    /// and Legendary installed.json.
    /// No process spawn — a missing or hung CLI cannot hide on-disk installs.
    /// </summary>
    internal static IReadOnlyList<GameEntry> ReadNativeInstalledLibrary(bool hasLegendary)
    {
        var latest = ReadLatestBuilds();
        // A record whose folder or executable is gone is a game removed outside Legendary, not an install.
        var games = ReadLegendaryInstalledJson()
            .Where(row => CatalogHygiene.KeepEpicRow(row.Title, row.Categories, installed: true) && InstallPresent(row.InstallPath, row.Executable))
            .Select(row => MapInstalledRow(row, latest.NewerThan(row.AppName, row.Version, row.ChangedUtc)))
            .ToList();
        var egl = ReadEpicManifests(hasLegendary, latest).Concat(ReadLauncherInstalled(games)).Concat(ReadLauncherV2(hasLegendary, games, latest)).ToList();
        return EpicEglMerge.ApplyInstalledOverlays(games, egl, preferEglInstallPath: ResolveEpicLauncher() is not null);
    }

    /// <summary>
    /// On disk: the folder is there and so is the executable its record names. An uninstall or a move elsewhere can
    /// leave the folder behind (saves, logs, an empty tree) with nothing left to play.
    /// </summary>
    internal static bool InstallPresent(string? dir, string? executable)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
        if (string.IsNullOrWhiteSpace(executable)) return true;
        try
        {
            return File.Exists(Path.Combine(dir, executable.TrimStart('/', '\\').Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    /// <summary>
    /// The newest build Epic has of each game for Windows (app name to build version), from Legendary's assets.json,
    /// which every owned-library refresh rewrites, and when that file was written.
    /// </summary>
    internal sealed record LatestBuilds(IReadOnlyDictionary<string, string> Builds, DateTime WrittenUtc)
    {
        /// <summary>
        /// Epic has a build other than the installed one, seen after the install last changed: a list older than an
        /// update that just landed would otherwise flag that update again.
        /// </summary>
        internal bool NewerThan(string appName, string? installedVersion, DateTime installChangedUtc) =>
            !string.IsNullOrWhiteSpace(installedVersion) && WrittenUtc > installChangedUtc &&
            Builds.TryGetValue(appName, out var build) && !string.Equals(build, installedVersion, StringComparison.Ordinal);

        internal static LatestBuilds Parse(string json, DateTime writtenUtc)
        {
            var builds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("Windows", out var windows) && windows.ValueKind == JsonValueKind.Array)
                foreach (var asset in windows.EnumerateArray())
                    if (asset.ValueKind == JsonValueKind.Object &&
                        asset.TryGetProperty("app_name", out var app) && app.ValueKind == JsonValueKind.String &&
                        asset.TryGetProperty("build_version", out var build) && build.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(app.GetString()) && !string.IsNullOrWhiteSpace(build.GetString()))
                        builds.TryAdd(app.GetString()!, build.GetString()!);
            return new LatestBuilds(builds, writtenUtc);
        }
    }

    /// <summary>The newest assets.json of every Legendary config Exo knows (its own, Heroic's); empty without one.</summary>
    internal static LatestBuilds ReadLatestBuilds()
    {
        var newest = LegendaryInstalledJsonCandidates()
            .Select(installed => Path.Combine(Path.GetDirectoryName(installed)!, "assets.json"))
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        try
        {
            if (newest is not null) return LatestBuilds.Parse(File.ReadAllText(newest), File.GetLastWriteTimeUtc(newest));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return new LatestBuilds(new Dictionary<string, string>(), DateTime.MinValue);
    }

    internal static IReadOnlyList<GameEntry> EnrichWithLegendaryCli(
        IReadOnlyList<GameEntry> native,
        IReadOnlyList<LegendaryCli.GameRow> cliRows)
    {
        if (cliRows.Count == 0) return native;
        // The CLI answer is cached between scans; the disk read is today's. It only adds detail to games still on
        // disk, so a game uninstalled since the last answer doesn't come back as installed.
        var onDisk = native.Select(game => game.LaunchTarget).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cliGames = cliRows
            .Where(row => onDisk.Contains(row.AppName))
            .Where(row => CatalogHygiene.KeepEpicRow(row.Title, row.Categories, installed: true))
            .Select(MapInstalledRow)
            .ToList();
        return EpicEglMerge.ApplyInstalledOverlays(cliGames, native, preferEglInstallPath: ResolveEpicLauncher() is not null);
    }

    /// <summary>
    /// Every installed.json Exo knows, its own Legendary's and Heroic's: each has installs the other lacks. The first
    /// record of a game wins.
    /// </summary>
    internal static IReadOnlyList<LegendaryCli.GameRow> ReadLegendaryInstalledJson()
    {
        var rows = new List<LegendaryCli.GameRow>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in LegendaryInstalledJsonCandidates().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!File.Exists(path)) continue;
                var changed = File.GetLastWriteTimeUtc(path);
                foreach (var row in LegendaryCli.ParseLibraryJson(File.ReadAllText(path), forceInstalled: true))
                    if (seen.Add(row.AppName)) rows.Add(row with { ChangedUtc = changed });
            }
            catch { /* next candidate */ }
        }

        return rows;
    }

    internal static IEnumerable<string> LegendaryInstalledJsonCandidates()
    {
        foreach (var userJson in EpicPlaytime.LegendaryUserJsonCandidates())
        {
            var dir = Path.GetDirectoryName(userJson);
            if (!string.IsNullOrWhiteSpace(dir))
                yield return Path.Combine(dir, "installed.json");
        }
    }

    internal static async Task<IReadOnlyList<LegendaryCli.GameRow>> TryListLegendaryInstalledAsync(
        string legendary,
        CancellationToken ct)
    {
        return await TryParseLegendaryListAsync(
            token => CliRunner.RunJsonAsync(legendary, LegendaryCli.ListInstalledArgs(), null, token),
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Bounded CLI probe. Timeout or caller cancel returns empty so native EGL
    /// results already collected by the caller stay in the library.
    /// </summary>
    internal static async Task<IReadOnlyList<LegendaryCli.GameRow>> TryParseLegendaryListAsync(
        Func<CancellationToken, Task<(int ExitCode, string StdOut, string StdErr)>> run,
        CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var (code, stdout, _) = await run(timeout.Token).ConfigureAwait(false);
            if (code == 0 && !string.IsNullOrWhiteSpace(stdout))
                return LegendaryCli.ParseLibraryJson(stdout, forceInstalled: true);
        }
        catch (OperationCanceledException)
        {
            AppLog.Debug("Legendary list-installed cancelled or timed out; using native EGL/Legendary manifests.");
        }
        catch (Exception ex)
        {
            AppLog.Debug("Legendary list-installed failed: " + ex.Message);
        }

        return Array.Empty<LegendaryCli.GameRow>();
    }

    private static void ScheduleEglSyncImport(string legendary)
    {
        if (Interlocked.CompareExchange(ref _eglSyncScheduled, 1, 0) != 0) return;
        _ = Task.Run(async () =>
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            await TryEglSyncImportOnceAsync(legendary, timeout.Token).ConfigureAwait(false);
        });
    }

    private static void ScheduleOwnedLibraryRefresh(string legendary, string? expectedAccountScope)
    {
        if (string.IsNullOrWhiteSpace(expectedAccountScope)) return;
        if (!OwnedRefreshInFlight.TryAdd(expectedAccountScope, 0)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                if (IsOwnedCacheFresh(expectedAccountScope)) return;
                // A first list fetches every title's metadata and can take
                // minutes on a large library. Legendary keeps what it fetched,
                // so a run cut short makes the next one faster.
                using var timeout = new CancellationTokenSource(OwnedListTimeout);
                var (code, stdout, _) = await CliRunner.RunJsonAsync(
                        legendary, LegendaryCli.ListOwnedArgs(), null, timeout.Token)
                    .ConfigureAwait(false);
                if (!LegendaryCli.IsAuthenticatedLibraryResponse(code, stdout)) return;
                CommitOwnedLibraryRefresh(
                    expectedAccountScope,
                    LegendaryCli.ParseLibraryJson(stdout, forceInstalled: false),
                    EpicPlaytime.GetActiveAccountScope,
                    ReadOwnedCache,
                    WriteOwnedCache);
            }
            catch (OperationCanceledException)
            {
                AppLog.Info($"Legendary owned list did not finish in {OwnedListTimeout.TotalSeconds:0}s; the next refresh continues it.");
            }
            catch { /* owned cache is best-effort */ }
            finally
            {
                OwnedRefreshInFlight.TryRemove(expectedAccountScope, out _);
            }
        });
    }

    /// <summary>
    /// Commits an authenticated Legendary snapshot only inside the account scope
    /// that requested it. The second scope read closes the account-switch race;
    /// only a durable, still-active snapshot can ask the library to repaint,
    /// and only when the owned rows actually changed.
    /// </summary>
    internal static bool CommitOwnedLibraryRefresh(
        string expectedAccountScope,
        IReadOnlyList<LegendaryCli.GameRow> rows,
        Func<string?> readActiveAccountScope,
        Func<string, IReadOnlyList<LegendaryCli.GameRow>> readCache,
        Func<string, IReadOnlyList<LegendaryCli.GameRow>, bool> writeCache)
    {
        if (string.IsNullOrWhiteSpace(expectedAccountScope) ||
            !string.Equals(readActiveAccountScope(), expectedAccountScope, StringComparison.Ordinal))
            return false;
        var unchanged = SameCachedOwnedRows(readCache(expectedAccountScope), rows);
        // Still written when unchanged: the write renews verifiedAtUtc.
        if (!writeCache(expectedAccountScope, rows)) return false;
        if (!string.Equals(readActiveAccountScope(), expectedAccountScope, StringComparison.Ordinal))
            return false;

        if (!unchanged)
        {
            try { OwnedLibraryCacheUpdated?.Invoke(); }
            catch { /* a repaint listener must not invalidate the verified cache */ }
        }
        return true;
    }

    /// <summary>Compares only the fields <see cref="SerializeOwnedCache"/> persists.</summary>
    private static bool SameCachedOwnedRows(
        IReadOnlyList<LegendaryCli.GameRow> cached,
        IReadOnlyList<LegendaryCli.GameRow> fresh)
    {
        if (cached.Count != fresh.Count) return false;
        for (var index = 0; index < cached.Count; index++)
        {
            var (a, b) = (cached[index], fresh[index]);
            if (!string.Equals(a.AppName, b.AppName, StringComparison.Ordinal) ||
                !string.Equals(a.Title, b.Title, StringComparison.Ordinal) ||
                a.SizeBytes != b.SizeBytes ||
                a.Installable != b.Installable ||
                !string.Equals(a.ThirdPartyStore, b.ThirdPartyStore, StringComparison.Ordinal) ||
                !a.Categories.SequenceEqual(b.Categories, StringComparer.Ordinal))
                return false;
        }
        return true;
    }

    internal static IReadOnlyList<GameEntry> MergeOwned(
        IReadOnlyList<GameEntry> installed,
        IEnumerable<LegendaryCli.GameRow> owned,
        bool hasLegendary)
    {
        var ownedRows = owned as IReadOnlyList<LegendaryCli.GameRow> ?? owned.ToList();
        if (ownedRows.Count == 0) return installed;
        var ownedByApp = ownedRows
            .Where(row => !string.IsNullOrWhiteSpace(row.AppName))
            .GroupBy(row => row.AppName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var extra = new List<GameEntry>(installed.Count + ownedByApp.Count);
        foreach (var game in installed)
        {
            if (!CatalogHygiene.KeepEpicRow(game.Title, [], installed: true))
                continue;
            var appName = EpicAppKey(game);
            if (string.IsNullOrWhiteSpace(appName))
            {
                extra.Add(game);
                continue;
            }
            present.Add(appName);
            extra.Add(ownedByApp.TryGetValue(appName, out var ownedRow)
                ? WithVerifiedEpicOwnership(game, hasLegendary, ownedRow.Title)
                : game);
        }
        foreach (var row in ownedRows)
        {
            if (string.IsNullOrWhiteSpace(row.AppName) || present.Contains(row.AppName))
                continue;
            if (!CatalogHygiene.KeepEpicRow(row.Title, row.Categories))
                continue;
            extra.Add(MapOwnedRow(row, hasLegendary));
            present.Add(row.AppName);
        }

        return extra;
    }

    private static string? EpicAppKey(GameEntry game)
    {
        if (!string.IsNullOrWhiteSpace(game.LaunchTarget))
            return game.LaunchTarget;
        return game.Id.StartsWith("epic:", StringComparison.OrdinalIgnoreCase) && game.Id.Length > 5
            ? game.Id[5..]
            : null;
    }

    internal static GameEntry MapOwnedRow(LegendaryCli.GameRow row, bool hasLegendary) =>
        new()
        {
            Id = "epic:" + row.AppName,
            Title = row.Title,
            Store = StoreKind.Epic,
            Installed = false,
            Owned = true,
            EntitlementState = EntitlementState.Owned,
            // A title Epic does not deliver is never a Legendary install:
            // Install hands it to Epic Games Launcher, which sends it on.
            CanInstall = hasLegendary || !row.Installable,
            CoverUrl = row.CoverUrl,
            CoverSource = row.CoverUrl is null ? null : "epic-catalog",
            LaunchTarget = row.AppName,
            SizeBytes = row.SizeBytes,
            Status = "Not installed",
            Deps = row.Installable ? new[] { "Legendary" } : new[] { "Epic Games Launcher" },
            LaunchNote = row.Installable
                ? "Ownership was last verified through Legendary for this Epic account. Install via Legendary."
                : $"Owned on Epic and installed through {ThirdPartyStoreName(row.ThirdPartyStore)}. Install opens Epic Games Launcher.",
        };

    internal static string ThirdPartyStoreName(string? store) => (store ?? "").Trim().ToLowerInvariant() switch
    {
        "" => "its own store",
        "origin" or "the ea app" or "ea app" => "the EA app",
        "uplay" or "ubisoft" or "ubisoft connect" => "Ubisoft Connect",
        _ => store!.Trim(),
    };

    private static GameEntry WithVerifiedEpicOwnership(GameEntry game, bool hasLegendary, string ownedTitle) => new()
    {
        Id = game.Id,
        // An install record without a title of its own is named by its app id (a codename or 32 hex digits); the
        // account's list has the game's name.
        Title = EpicEglMerge.IsAppIdTitle(game.Title, game.LaunchTarget) ? ownedTitle : game.Title,
        Store = game.Store,
        Installed = game.Installed,
        Owned = true,
        EntitlementState = EntitlementState.Owned,
        UpdateAvailable = game.UpdateAvailable,
        CanInstall = !game.Installed && hasLegendary,
        Path = game.Path,
        CoverUrl = game.CoverUrl,
        CoverSource = game.CoverSource,
        ArtRevision = game.ArtRevision,
        PlaytimeMinutes = game.PlaytimeMinutes,
        SizeBytes = game.SizeBytes,
        Status = game.Installed
            ? (game.UpdateAvailable ? "Update" : "Ready")
            : "Not installed",
        Deps = game.Deps,
        LaunchNote = game.Installed
            ? "Ownership was last verified through Legendary for this Epic account."
            : "Ownership was last verified through Legendary for this Epic account. Install via Legendary.",
        LaunchTarget = game.LaunchTarget,
        ParentAppId = game.ParentAppId,
        SteamAppType = game.SteamAppType,
        LastPlayedUtc = game.LastPlayedUtc,
        CanonicalTitleKey = game.CanonicalTitleKey,
        SelectedVariantId = game.SelectedVariantId,
        Variants = game.Variants,
    };

    private static string OwnedCachePath =>
        Path.Combine(PathHelper.AppDataDir, "epic-owned.json");

    private static bool IsOwnedCacheFresh(string expectedAccountScope)
    {
        try
        {
            if (!File.Exists(OwnedCachePath)) return false;
            using var document = JsonDocument.Parse(File.ReadAllText(OwnedCachePath));
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object &&
                   root.TryGetProperty("schemaVersion", out var schema) &&
                   schema.TryGetInt32(out var schemaVersion) &&
                   schemaVersion == OwnedCacheSchemaVersion &&
                   root.TryGetProperty("accountScope", out var scope) &&
                   string.Equals(scope.GetString(), expectedAccountScope, StringComparison.Ordinal) &&
                   root.TryGetProperty("provenance", out var provenance) &&
                   string.Equals(provenance.GetString(), OwnedCacheProvenance, StringComparison.Ordinal) &&
                   // A list from before titles activated elsewhere were asked
                   // for is still shown, but refreshed now.
                   root.TryGetProperty("includesNonInstallable", out var complete) &&
                   complete.ValueKind == JsonValueKind.True &&
                   root.TryGetProperty("verifiedAtUtc", out var verified) &&
                   verified.TryGetDateTimeOffset(out var verifiedAt) &&
                   DateTimeOffset.UtcNow - verifiedAt < TimeSpan.FromHours(6);
        }
        catch
        {
            return false;
        }
    }

    internal static IReadOnlyList<LegendaryCli.GameRow> ReadOwnedCache(string? expectedAccountScope)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(expectedAccountScope) || !File.Exists(OwnedCachePath))
                return Array.Empty<LegendaryCli.GameRow>();
            return ParseOwnedCache(File.ReadAllText(OwnedCachePath), expectedAccountScope);
        }
        catch
        {
            return Array.Empty<LegendaryCli.GameRow>();
        }
    }

    internal static IReadOnlyList<LegendaryCli.GameRow> ParseOwnedCache(
        string? json,
        string? expectedAccountScope)
    {
        if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(expectedAccountScope))
            return Array.Empty<LegendaryCli.GameRow>();
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("schemaVersion", out var schema) ||
                !schema.TryGetInt32(out var schemaVersion) ||
                schemaVersion != OwnedCacheSchemaVersion ||
                !root.TryGetProperty("accountScope", out var scope) ||
                !string.Equals(scope.GetString(), expectedAccountScope, StringComparison.Ordinal) ||
                !root.TryGetProperty("provenance", out var provenance) ||
                !string.Equals(provenance.GetString(), OwnedCacheProvenance, StringComparison.Ordinal) ||
                !root.TryGetProperty("verifiedAtUtc", out var verified) ||
                !verified.TryGetDateTimeOffset(out _) ||
                !root.TryGetProperty("games", out var games) ||
                games.ValueKind != JsonValueKind.Array)
                return Array.Empty<LegendaryCli.GameRow>();
            return LegendaryCli.ParseLibraryJson(games.GetRawText(), forceInstalled: false);
        }
        catch
        {
            return Array.Empty<LegendaryCli.GameRow>();
        }
    }

    internal static string SerializeOwnedCache(string accountScope, IReadOnlyList<LegendaryCli.GameRow> rows) =>
        JsonSerializer.Serialize(new
        {
            schemaVersion = OwnedCacheSchemaVersion,
            accountScope,
            verifiedAtUtc = DateTimeOffset.UtcNow,
            provenance = OwnedCacheProvenance,
            games = rows.Select(row => new
            {
                app_name = row.AppName,
                app_title = row.Title,
                title = row.Title,
                install_size = row.SizeBytes,
                // Without these the strict "empty categories are not a game"
                // rule dropped every cached title on the next offline start.
                categories = row.Categories,
                installable = row.Installable,
                third_party_store = row.ThirdPartyStore,
            }).ToArray(),
            includesNonInstallable = true,
        });

    internal static bool WriteOwnedCache(string accountScope, IReadOnlyList<LegendaryCli.GameRow> rows)
    {
        if (string.IsNullOrWhiteSpace(accountScope)) return false;
        try
        {
            Directory.CreateDirectory(PathHelper.AppDataDir);
            var payload = SerializeOwnedCache(accountScope, rows);
            var temp = OwnedCachePath + ".tmp";
            File.WriteAllText(temp, payload);
            File.Move(temp, OwnedCachePath, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task TryEglSyncImportOnceAsync(string legendary, CancellationToken ct)
    {
        try
        {
            // Import EGL *installs* only. Never `auth --import` — that logs the
            // Epic Games Launcher out. Re-run even when Legendary already has
            // some titles so later EGL installs are picked up.
            await CliRunner.RunAsync(
                legendary,
                LegendaryCli.EglImportOnlyArgs(),
                null, null, ct).ConfigureAwait(false);
        }
        catch { /* best-effort */ }
    }

    internal static GameEntry MapInstalledRow(LegendaryCli.GameRow row) => MapInstalledRow(row, updateAvailable: false);

    internal static GameEntry MapInstalledRow(LegendaryCli.GameRow row, bool updateAvailable)
    {
        return new GameEntry
        {
            Id = "epic:" + row.AppName,
            Title = row.Title,
            Store = StoreKind.Epic,
            Installed = row.Installed,
            // `legendary list-installed` is machine-local install evidence. It
            // does not prove that the active Epic account owns this title.
            Owned = false,
            EntitlementState = EntitlementState.Unverified,
            UpdateAvailable = row.Installed && updateAvailable,
            CanInstall = false,
            Path = row.InstallPath,
            CoverUrl = row.CoverUrl,
            CoverSource = row.CoverUrl is null ? null : "epic-catalog",
            LaunchTarget = row.AppName,
            SizeBytes = row.SizeBytes,
            Status = row.Installed ? (updateAvailable ? "Update" : "Ready") : "Not installed",
            Deps = new[] { "Legendary" },
            LaunchNote = row.Installed
                ? "Launches via Legendary when available."
                : "Install via Legendary when this account owns it.",
        };
    }

    public async Task<InstallResult> InstallAsync(
        GameEntry game,
        string? installPath,
        IProgress<InstallProgress>? progress,
        CancellationToken ct = default)
    {
        var appName = game.LaunchTarget;
        if (string.IsNullOrWhiteSpace(appName))
            return new InstallResult { Ok = false, Message = "Missing Epic app name." };
        if (ReadOwnedCache(GetActiveAccountScope()).FirstOrDefault(row =>
                string.Equals(row.AppName, appName, StringComparison.OrdinalIgnoreCase)) is { Installable: false } elsewhere)
            return HandOffToOtherStore(game, appName, ThirdPartyStoreName(elsewhere.ThirdPartyStore));

        Report(game.Id, progress, InstallPhase.Preparing, null, "Preparing…");
        var basePath = installPath ?? Path.Combine(PathHelper.AppDataDir, "Epic");
        // Legendary names the game's folder itself; the one that appears during this install is the partial one.
        HashSet<string>? before = null;

        // Everything after the first report is inside the try: a cancel or an error while Legendary is fetched, or while
        // the folder is made, must end the job too, or it stays "Preparing" and blocks DLSS updates and resumed downloads.
        try
        {
            var legendary = ResolveLegendary() ?? await EnsureLegendaryAsync(ct).ConfigureAwait(false);
            if (legendary is null)
                return await WatchEpicLauncherJobAsync(game, appName, progress, uninstall: false, ct).ConfigureAwait(false);

            Directory.CreateDirectory(basePath);
            before = Directory.GetDirectories(basePath).ToHashSet(StringComparer.OrdinalIgnoreCase);

            Report(game.Id, progress, InstallPhase.Preparing, null, "Starting download…");
            var args = LegendaryCli.InstallArgs(appName, basePath);
            var (code, _, err) = await CliRunner.RunAsync(
                legendary,
                args,
                null,
                line =>
                {
                    var p = LegendaryCli.ToProgress(game.Id, line, "Downloading…");
                    _progress[game.Id] = p;
                    progress?.Report(p);
                },
                ct).ConfigureAwait(false);

            // Report Legendary's failure at once. Handing every failure to the
            // Epic Games Launcher turned a full disk or a sign-out into a
            // silent wait of up to two hours.
            if (code != 0)
            {
                var failed = CliFailure.Describe("Epic", "install", game.Title, code, err);
                Report(game.Id, progress, InstallPhase.Failed, null, failed);
                return new InstallResult { Ok = false, Message = failed };
            }

            // Legendary installs under --base-path/<AppName>, not the parent alone.
            var titleDir = Path.Combine(basePath, appName);
            var installDir = Directory.Exists(titleDir)
                ? titleDir
                : Directory.GetDirectories(basePath)
                    .OrderByDescending(d => Directory.GetLastWriteTimeUtc(d))
                    .FirstOrDefault()
                  ?? basePath;

            Report(game.Id, progress, InstallPhase.Completed, 100, "Install complete.");
            return new InstallResult { Ok = true, Message = "Installed.", Path = installDir };
        }
        catch (OperationCanceledException)
        {
            Report(game.Id, progress, InstallPhase.Cancelled, null, "Canceled.");
            return new InstallResult { Ok = false, Message = "Canceled.", PartialPath = PartialInstallFolder(basePath, before) };
        }
        catch (Exception ex)
        {
            var failed = CliFailure.Describe("Epic", "install", game.Title, ex);
            Report(game.Id, progress, InstallPhase.Failed, null, failed);
            return new InstallResult { Ok = false, Message = failed };
        }
    }

    /// <summary>
    /// The one folder this install made under <paramref name="basePath"/>, for cleanup after a cancel: only a new folder
    /// that carries Legendary's own marker (.egstore). The base is shared with other programs and other installs, so a
    /// lone new folder is not proof it is ours, and the orchestrator deletes this one recursively. Null when unsure.
    /// </summary>
    internal static string? PartialInstallFolder(string basePath, IReadOnlySet<string>? before)
    {
        try
        {
            if (before is null || !Directory.Exists(basePath)) return null;
            var ours = Directory.GetDirectories(basePath)
                .Where(dir => !before.Contains(dir) && Directory.Exists(Path.Combine(dir, ".egstore")))
                .ToList();
            return ours.Count == 1 ? ours[0] : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public async Task<InstallResult> UpdateAsync(GameEntry game, IProgress<InstallProgress>? progress, CancellationToken ct = default)
    {
        // Every way the update can end reports it: the launcher's watch (up to hours) and its start sit outside Legendary's own
        // handling, and a cancel or an error there left the game "busy" (no upscaler change, no resume) until Exo restarted.
        try { return await UpdateCoreAsync(game, progress, ct).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            Report(game.Id, progress, InstallPhase.Cancelled, null, "Canceled.");
            return new InstallResult { Ok = false, Message = "Canceled." };
        }
        catch (Exception ex)
        {
            var failed = CliFailure.Describe("Epic", "update", game.Title, ex);
            Report(game.Id, progress, InstallPhase.Failed, null, failed);
            return new InstallResult { Ok = false, Message = failed };
        }
    }

    private async Task<InstallResult> UpdateCoreAsync(GameEntry game, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var appName = game.LaunchTarget;
        if (string.IsNullOrWhiteSpace(appName))
            return new InstallResult { Ok = false, Message = "Missing Epic app name." };

        Report(game.Id, progress, InstallPhase.Preparing, null, "Preparing Epic update…");

        // A game Epic's new launcher installed updates in that launcher, hidden: Legendary patching its files left the
        // launcher's own record saying the update never finished, so Exo showed Update forever and Epic would redo it.
        if (EglV2Install(appName, game.Path) is { } owned && ResolveEpicLauncher() is { } eglExe)
            return await UpdateInEglAsync(game, owned, eglExe, progress, ct).ConfigureAwait(false);

        var legendary = ResolveLegendary() ?? await EnsureLegendaryAsync(ct).ConfigureAwait(false);
        if (legendary is null)
        {
            // Epic documents updatecheck as a handoff. Required client UI is
            // user-controlled; install/update is never fabricated as complete.
            var meta = FindManifestMeta(appName, game.Path);
            var epic = ResolveEpicLauncher();
            if (epic is not null)
            {
                HiddenStoreRuntime.SuspendFor(StoreKind.Epic, TimeSpan.FromMinutes(30));
                if (!ProcessHelper.IsProcessRunning("EpicGamesLauncher"))
                {
                    Process.Start(new ProcessStartInfo(epic) { UseShellExecute = true });
                    await Task.Delay(2000, ct).ConfigureAwait(false);
                }
                foreach (var uri in BuildEpicActionUris(
                             appName, meta?.CatalogNamespace, meta?.CatalogItemId, "updatecheck"))
                {
                    try { ProcessHelper.StartProtocol(uri); }
                    catch { /* try next */ }
                }
                Report(game.Id, progress, InstallPhase.Idle, null,
                    "Finish the update in Epic Games Launcher.");
                // A handoff, not a failure: Epic's own window now owns the update.
                return new InstallResult
                {
                    Ok = true,
                    Message = "Finish the update in Epic Games Launcher.",
                    HandoffOnly = true,
                };
            }
            return new InstallResult
            {
                Ok = false,
                Message = "Exo cannot update this game right now. Update it in Epic Games Launcher.",
            };
        }

        Report(game.Id, progress, InstallPhase.Downloading, null, "Updating…");
        try
        {
            Task<(int, string, string)> Update() => CliRunner.RunAsync(
                legendary,
                LegendaryCli.UpdateArgs(appName),
                null,
                line =>
                {
                    var p = LegendaryCli.ToProgress(game.Id, line, "Updating…");
                    _progress[game.Id] = p;
                    progress?.Report(p);
                },
                ct);
            var (code, _, err) = await Update().ConfigureAwait(false);

            // A game Epic Games Launcher installed (its folder carries .egstore) that Legendary never imported: with the
            // launcher gone there is nothing for egl-sync to read, so the folder itself is imported, then updated.
            if (code != 0 && LegendaryCli.NotInstalled(err) && Directory.Exists(game.Path))
            {
                var (imported, _, _) = await CliRunner.RunAsync(legendary, LegendaryCli.ImportArgs(appName, game.Path!), null, null, ct).ConfigureAwait(false);
                if (imported == 0) (code, _, err) = await Update().ConfigureAwait(false);
            }

            if (code != 0)
            {
                var failed = CliFailure.Describe("Epic", "update", game.Title, code, err);
                Report(game.Id, progress, InstallPhase.Failed, null, failed);
                return new InstallResult { Ok = false, Message = failed };
            }

            Report(game.Id, progress, InstallPhase.Completed, 100, "Up to date.");
            return new InstallResult { Ok = true, Message = "Updated." };
        }
        catch (OperationCanceledException)
        {
            Report(game.Id, progress, InstallPhase.Cancelled, null, "Canceled.");
            return new InstallResult { Ok = false, Message = "Canceled." };
        }
        catch (Exception ex)
        {
            var failed = CliFailure.Describe("Epic", "update", game.Title, ex);
            Report(game.Id, progress, InstallPhase.Failed, null, failed);
            return new InstallResult { Ok = false, Message = failed };
        }
    }

    public bool CanRepair(GameEntry game) =>
        game.Installed && !string.IsNullOrWhiteSpace(game.LaunchTarget) && ResolveLegendary() is not null;

    public async Task<InstallResult> RepairAsync(
        GameEntry game,
        IProgress<InstallProgress>? progress,
        CancellationToken ct = default)
    {
        var appName = game.LaunchTarget;
        if (string.IsNullOrWhiteSpace(appName))
            return new InstallResult { Ok = false, Message = "Missing Epic app name." };

        var legendary = ResolveLegendary() ?? await EnsureLegendaryAsync(ct).ConfigureAwait(false);
        if (legendary is null)
            return new InstallResult { Ok = false, Message = "Exo cannot verify this game's files right now." };

        Report(game.Id, progress, InstallPhase.Preparing, null, "Verifying Epic files…");
        try
        {
            var (code, _, err) = await CliRunner.RunAsync(
                    legendary,
                    LegendaryCli.RepairArgs(appName),
                    null,
                    line =>
                    {
                        var p = LegendaryCli.ToProgress(game.Id, line, "Verifying files…", InstallPhase.Installing);
                        _progress[game.Id] = p;
                        progress?.Report(p);
                    },
                    ct)
                .ConfigureAwait(false);
            if (code != 0)
            {
                var failed = CliFailure.Describe("Epic", "verify", game.Title, code, err);
                Report(game.Id, progress, InstallPhase.Failed, null, failed);
                return new InstallResult { Ok = false, Message = failed };
            }

            Report(game.Id, progress, InstallPhase.Completed, 100, "Verified.");
            return new InstallResult { Ok = true, Message = "Files verified." };
        }
        catch (OperationCanceledException)
        {
            Report(game.Id, progress, InstallPhase.Cancelled, null, "Canceled.");
            return new InstallResult { Ok = false, Message = "Canceled." };
        }
        catch (Exception ex)
        {
            var failed = CliFailure.Describe("Epic", "verify", game.Title, ex);
            Report(game.Id, progress, InstallPhase.Failed, null, failed);
            return new InstallResult { Ok = false, Message = failed };
        }
    }

    public async Task<LaunchResult> LaunchAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default)
    {
        // Cancellation must win before probing routes or starting any store/game process.
        ct.ThrowIfCancellationRequested();
        var appName = game.LaunchTarget;
        var meta = FindManifestMeta(appName, game.Path);
        var epic = ResolveEpicLauncher();
        var fallbackRoute = SelectEpicFallbackRoute(
            launcherAvailable: epic is not null,
            launchTargetAvailable: !string.IsNullOrWhiteSpace(appName));
        var existingGamePids = ProcessHelper.SnapshotLiveProcessIdsUnderPath(
            game.Path,
            EpicBootstrapProcessNames);
        if (existingGamePids.FirstOrDefault() is var existingPid && existingPid > 0)
        {
            return new LaunchResult
            {
                Ok = true,
                Message = "Already running",
                ProcessId = existingPid,
                BackendStarted = "epic",
            };
        }

        // Use the official client's authenticated handoff when it is available.
        // A bundled but unconfigured Legendary helper can otherwise consume its
        // whole timeout before the user's signed-in launcher receives anything.
        ct.ThrowIfCancellationRequested();
        var legendary = ResolveLegendary();
        // A game Exo installed with Legendary is unknown to Epic's own launcher (no manifest there), which accepts
        // its launch URI and starts nothing: Legendary, which installed it, starts it.
        var installedByLegendary = meta is null && !string.IsNullOrWhiteSpace(appName)
            && ReadLegendaryInstalledJson().Any(row => string.Equals(row.AppName, appName, StringComparison.OrdinalIgnoreCase));
        if ((epic is null || installedByLegendary) && legendary is not null && !string.IsNullOrWhiteSpace(appName))
        {
            try
            {
                using var helper = ProcessHelper.StartHidden(
                    legendary,
                    LegendaryCli.LaunchArgs(appName, options.ExtraArgs))
                    ?? throw new InvalidOperationException("Legendary did not start.");
                using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var processWait = ProcessHelper.WaitForProcessUnderPathAsync(
                    game.Path,
                    TimeSpan.FromSeconds(30),
                    waitCts.Token,
                    EpicBootstrapProcessNames,
                    existingGamePids,
                    NewGameProcessConfirmationDelay);
                var helperExit = helper.WaitForExitAsync(ct);
                var completed = await Task.WhenAny(processWait, helperExit).ConfigureAwait(false);
                int? gamePid;
                if (completed == processWait)
                {
                    gamePid = await processWait.ConfigureAwait(false);
                }
                else
                {
                    await helperExit.ConfigureAwait(false);
                    waitCts.Cancel();
                    try { _ = await processWait.ConfigureAwait(false); }
                    catch (OperationCanceledException) { /* replaced by short handoff grace */ }

                    // Legendary commonly exits immediately after a successful
                    // handoff. Allow a short spawn grace, but never burn the full
                    // 30-second process scan after a definite helper failure.
                    gamePid = await ProcessHelper.WaitForProcessUnderPathAsync(
                            game.Path,
                            TimeSpan.FromSeconds(helper.ExitCode == 0 ? 3 : 1),
                            ct,
                            EpicBootstrapProcessNames,
                            existingGamePids,
                            NewGameProcessConfirmationDelay)
                        .ConfigureAwait(false);
                    if (gamePid is null)
                        AppLog.Info(
                            $"Legendary launch for '{appName}' exited {helper.ExitCode} before a game process appeared; trying the next Epic route.");
                }
                ct.ThrowIfCancellationRequested();
                if (gamePid is int pid)
                {
                    return new LaunchResult
                    {
                        Ok = true,
                        Message = "Game started.",
                        ProcessId = pid,
                        BackendStarted = "legendary",
                    };
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLog.Info($"Legendary launch for '{appName}' failed; trying the next Epic route: {ex.Message}");
            }
        }

        ct.ThrowIfCancellationRequested();

        // 2) Direct install launch is only a fallback when the official Epic
        // launcher route is unavailable. Epic-managed games can require
        // exchange-code/EOS/EAC arguments; starting their final executable first
        // produces a short-lived process and a false-positive success even when a
        // local manifest has temporarily gone stale or missing.
        if (fallbackRoute == EpicFallbackRoute.DirectExecutable)
        {
            ct.ThrowIfCancellationRequested();
            using var direct = TryStartInstalledGame(game.Path, meta?.LaunchExecutable, appName);
            if (direct is not null)
            {
                var pid = await ProcessHelper.ConfirmDirectLaunchAsync(
                        direct,
                        game.Path,
                        existingGamePids,
                        ct,
                        EpicBootstrapProcessNames,
                        NewGameProcessConfirmationDelay)
                    .ConfigureAwait(false);
                if (pid is not null)
                {
                    return new LaunchResult
                    {
                        Ok = true,
                        Message = "Launched installed Epic title.",
                        ProcessId = pid,
                        BackendStarted = "epic-direct",
                    };
                }

                AppLog.Info(
                    $"Direct Epic launch for '{appName}' did not produce a stable game process; trying any remaining Epic route.");
            }
        }
        else
        {
            AppLog.Info(
                $"Epic manifest-backed launch for '{appName}' is using the authenticated launcher handoff before any direct executable.");
        }

        // 3) Epic Games Launcher protocol — full CatalogNamespace:CatalogItemId:AppName form.
        ct.ThrowIfCancellationRequested();
        if (epic is not null && !string.IsNullOrWhiteSpace(appName))
        {
            try
            {
                using var hider = StoreWindowHider.ForEpic();
                hider.Start(TimeSpan.FromSeconds(55));
                if (!ProcessHelper.IsProcessRunning("EpicGamesLauncher"))
                {
                    ct.ThrowIfCancellationRequested();
                    EpicQuietSettings.ApplyIfHanded();
                    ProcessHelper.StartHidden(epic, QuietStartFlag(epic));
                    // A protocol request issued while a cold client is still
                    // constructing its command listener can be accepted by
                    // Windows yet silently discarded by Epic. Wait for the
                    // launcher handoff surface before submitting this exact
                    // title's URI. This is intentionally bounded; the normal
                    // URI retry path below still owns a slow or unhealthy
                    // client.
                    await WaitForEpicCommandListenerAsync(ct).ConfigureAwait(false);
                }
                StoreWindowHider.HideOnce(StoreWindowHider.EpicProcessNames);

                var requested = false;
                var gamePid = await TryEpicLaunchUrisAsync(
                    BuildEpicLaunchUris(appName!, meta).ToArray(),
                    async (uri, attempt, token) =>
                    {
                        try
                        {
                            token.ThrowIfCancellationRequested();
                            ProcessHelper.StartHidden(epic, [QuietStartFlag(epic), uri]);
                            StoreWindowHider.HideOnce(StoreWindowHider.EpicProcessNames);
                            requested = true;

                            // A cold Epic client can accept a protocol URI before it is
                            // ready to act on it. Give each supported URI a bounded turn,
                            // then try the non-silent/bare fallback rather than waiting
                            // the entire launch budget on the first shell invocation.
                            var pid = await ProcessHelper.WaitForProcessUnderPathAsync(
                                    game.Path,
                                    TimeSpan.FromSeconds(12),
                                    token,
                                    EpicBootstrapProcessNames,
                                    existingGamePids,
                                    NewGameProcessConfirmationDelay)
                                .ConfigureAwait(false);
                            token.ThrowIfCancellationRequested();
                            if (pid is not null)
                                AppLog.Info($"Epic launcher handoff for '{appName}' started game process {pid} on attempt {attempt}.");
                            else
                                AppLog.Info(
                                    $"Epic launcher handoff for '{appName}' produced no game process on attempt {attempt}; trying the next supported URI.");
                            return pid;
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            AppLog.Info($"Epic launcher handoff for '{appName}' failed on attempt {attempt}: {ex.Message}");
                            return null;
                        }
                    },
                    ct).ConfigureAwait(false);
                if (gamePid is int pid)
                {
                    return new LaunchResult
                    {
                        Ok = true,
                        Message = "Epic game started.",
                        ProcessId = pid,
                        BackendStarted = "epic",
                    };
                }
                if (requested)
                {
                    HiddenStoreRuntime.SuspendFor(StoreKind.Epic, TimeSpan.FromMinutes(30));
                    hider.Dispose();
                    StoreWindowHider.RestoreStoreWindows(StoreWindowHider.EpicProcessNames);
                    try { ProcessHelper.StartHidden(epic, ["com.epicgames.launcher://apps"]); } catch { }
                    AppLog.Warn(
                        $"Epic launcher accepted launch requests for '{appName}', but no game process appeared.");
                    return new LaunchResult
                    {
                        Ok = false,
                        HandoffOnly = true,
                        Message = "Epic did not start the game. Epic has been opened; check sign-in, updates, and the game drive, then try Play again.",
                        BackendStarted = "epic",
                    };
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new LaunchResult { Ok = false, Message = CliFailure.Launch(game.Title, ex) };
            }
        }

        return new LaunchResult
        {
            Ok = false,
            Message = legendary is null && epic is null
                ? "Epic Games Launcher is not installed, and Exo could not start this game without it."
                : $"Could not start {(string.IsNullOrWhiteSpace(game.Title) ? appName : game.Title)}. Check that it is still installed, or sign in to Epic again in Settings.",
        };
    }

    internal enum EpicFallbackRoute
    {
        DirectExecutable,
        LauncherHandoff,
    }

    internal static EpicFallbackRoute SelectEpicFallbackRoute(
        bool launcherAvailable,
        bool launchTargetAvailable) =>
        launcherAvailable && launchTargetAvailable
            ? EpicFallbackRoute.LauncherHandoff
            : EpicFallbackRoute.DirectExecutable;

    internal static async Task<int?> TryEpicLaunchUrisAsync(
        IReadOnlyList<string> uris,
        Func<string, int, CancellationToken, Task<int?>> tryUri,
        CancellationToken ct)
    {
        for (var index = 0; index < uris.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var pid = await tryUri(uris[index], index + 1, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (pid is not null) return pid;
        }

        return null;
    }

    /// <summary>
    /// Gives a cold Epic client a short, bounded chance to create the command
    /// listener which receives <c>com.epicgames.launcher://</c> launch URIs.
    /// A launcher process alone is not enough on a cold start: it can exist
    /// before its web helper is ready to dispatch title-specific requests.
    /// </summary>
    private static Task<bool> WaitForEpicCommandListenerAsync(CancellationToken ct) =>
        WaitForEpicCommandListenerAsync(
            () => ProcessHelper.IsProcessRunning("EpicGamesLauncher"),
            () => ProcessHelper.IsProcessRunning("EpicWebHelper"),
            static (delay, token) => Task.Delay(delay, token),
            ct);

    internal static async Task<bool> WaitForEpicCommandListenerAsync(
        Func<bool> launcherRunning,
        Func<bool> webHelperRunning,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        CancellationToken ct,
        int maxPolls = 20)
    {
        ArgumentNullException.ThrowIfNull(launcherRunning);
        ArgumentNullException.ThrowIfNull(webHelperRunning);
        ArgumentNullException.ThrowIfNull(delayAsync);
        if (maxPolls <= 0) throw new ArgumentOutOfRangeException(nameof(maxPolls));

        for (var attempt = 0; attempt < maxPolls; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var launcherReady = launcherRunning();
            var helperReady = webHelperRunning();

            // The main process alone is too early during a cold start. Prefer
            // the command helper; retain a bounded fallback for helperless builds.
            if (launcherReady && (helperReady || attempt == maxPolls - 1))
            {
                await delayAsync(TimeSpan.FromMilliseconds(750), ct).ConfigureAwait(false);
                return true;
            }

            await delayAsync(TimeSpan.FromMilliseconds(350), ct).ConfigureAwait(false);
        }

        return false;
    }

    private sealed record EpicManifestMeta(
        string AppName,
        string? CatalogNamespace,
        string? CatalogItemId,
        string? LaunchExecutable,
        string? InstallLocation,
        bool UpdatePending);

    private static EpicManifestMeta? FindManifestMeta(string? appName, string? installPath) =>
        FindItemManifestMeta(appName, installPath) ?? FindV2Meta(appName, installPath);

    /// <summary>The new launcher keeps no .item manifests: its installations.jsonl entry, matched the same way.</summary>
    private static EpicManifestMeta? FindV2Meta(string? appName, string? installPath)
    {
        foreach (var install in EpicLauncherV2.ReadInstallations())
        {
            var matchApp = !string.IsNullOrWhiteSpace(appName) && string.Equals(install.AppName, appName, StringComparison.OrdinalIgnoreCase);
            var matchPath = !string.IsNullOrWhiteSpace(installPath) && IsSamePath(install.Dir, installPath);
            if (matchApp || matchPath)
                return new EpicManifestMeta(install.AppName, install.Namespace, install.ItemId, install.LaunchExecutable, install.Dir, !install.Complete);
        }
        return null;
    }

    private static EpicManifestMeta? FindItemManifestMeta(string? appName, string? installPath)
    {
        var manifestDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(manifestDir)) return null;

        foreach (var file in Directory.EnumerateFiles(manifestDir, "*.item"))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var root = doc.RootElement;
                var name = root.TryGetProperty("AppName", out var a) ? a.GetString() : null;
                var loc = root.TryGetProperty("InstallLocation", out var i) ? i.GetString() : null;
                var matchApp = !string.IsNullOrWhiteSpace(appName) &&
                               string.Equals(name, appName, StringComparison.OrdinalIgnoreCase);
                var matchPath = !string.IsNullOrWhiteSpace(installPath) &&
                                !string.IsNullOrWhiteSpace(loc) &&
                                string.Equals(
                                    Path.GetFullPath(loc.TrimEnd('\\', '/')),
                                    Path.GetFullPath(installPath.TrimEnd('\\', '/')),
                                    StringComparison.OrdinalIgnoreCase);
                if (!matchApp && !matchPath) continue;

                return new EpicManifestMeta(
                    name ?? appName ?? "",
                    root.TryGetProperty("CatalogNamespace", out var ns) ? ns.GetString() : null,
                    root.TryGetProperty("CatalogItemId", out var ci) ? ci.GetString() : null,
                    root.TryGetProperty("LaunchExecutable", out var le) ? le.GetString() : null,
                    loc,
                    IsEglUpdatePending(root));
            }
            catch { /* skip */ }
        }
        return null;
    }

    /// <summary>This game's install in Epic's new launcher (2026), when it is the one at the game's folder.</summary>
    private static EpicLauncherV2.Installation? EglV2Install(string appName, string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : EpicLauncherV2.ReadInstallations().FirstOrDefault(install =>
            install.AppName.Equals(appName, StringComparison.OrdinalIgnoreCase) && install.Id is not null &&
            string.Equals(Path.GetFullPath(install.Dir).TrimEnd('\\', '/'), Path.GetFullPath(path).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Updates a game in Epic's new launcher without showing it: the launcher starts hidden (--autostart), takes the
    /// game's update link (action=update, which its deep links accept beside install, verify and uninstall), and
    /// Exo follows the launcher's own install record until the update has landed there.
    /// </summary>
    private async Task<InstallResult> UpdateInEglAsync(GameEntry game, EpicLauncherV2.Installation install, string epic,
        IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        AppLog.Info($"Epic update in Epic Games Launcher: gameId={game.Id}; pending={install.PendingVersion ?? "(launcher checks)"}.");
        Report(game.Id, progress, InstallPhase.Downloading, null, "Updating in Epic…");
        HiddenStoreRuntime.SuspendFor(StoreKind.Epic, TimeSpan.FromHours(3));
        using var hider = StoreWindowHider.ForEpic();
        hider.Start(TimeSpan.FromMinutes(2));
        if (!ProcessHelper.IsProcessRunning("EpicGamesLauncher"))
        {
            EpicQuietSettings.ApplyIfHanded();
            ProcessHelper.StartHidden(epic, QuietStartFlag(epic));
            await WaitForEpicCommandListenerAsync(ct).ConfigureAwait(false);
        }
        var link = BuildEpicActionUris(install.AppName, install.Namespace, install.ItemId, "update").First();
        var asked = DateTime.UtcNow;
        var askedAgain = false;
        ProcessHelper.StartHidden(epic, [QuietStartFlag(epic), link]);
        StoreWindowHider.HideOnce(StoreWindowHider.EpicProcessNames);
        var deadline = DateTime.UtcNow + TimeSpan.FromHours(3);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
            var now = EpicLauncherV2.ReadInstallations().FirstOrDefault(i => i.Id == install.Id);
            if (now is null)
                return new InstallResult { Ok = false, Message = "Epic Games Launcher no longer lists this game." };
            if (now.UpToDate && (now.Version != install.Version || install.UpToDate || DateTime.UtcNow - asked > TimeSpan.FromSeconds(20)))
            {
                AppLog.Info($"Epic update done in Epic Games Launcher: gameId={game.Id}; version={now.Version}.");
                Report(game.Id, progress, InstallPhase.Idle, null, "Up to date");
                return new InstallResult { Ok = true, Message = $"{game.Title} is up to date.", Path = now.Dir };
            }
            // A launcher still starting can take the link before it can act on it: ask once more after a minute.
            if (!askedAgain && DateTime.UtcNow - asked > TimeSpan.FromMinutes(1) && now == install)
            {
                askedAgain = true;
                ProcessHelper.StartHidden(epic, [QuietStartFlag(epic), link]);
                StoreWindowHider.HideOnce(StoreWindowHider.EpicProcessNames);
            }
            if (!ProcessHelper.IsProcessRunning("EpicGamesLauncher"))
                return new InstallResult { Ok = false, Message = "Epic Games Launcher closed before the update finished. Update again to continue." };
        }
        return new InstallResult { Ok = false, Message = "Epic Games Launcher has not finished the update. Open it to check." };
    }

    private static IEnumerable<string> BuildEpicLaunchUris(string appName, EpicManifestMeta? meta) =>
        BuildEpicLaunchUris(appName, meta?.CatalogNamespace, meta?.CatalogItemId);

    internal static IEnumerable<string> BuildEpicLaunchUris(
        string appName,
        string? catalogNamespace,
        string? catalogItemId)
    {
        // Preferred: Namespace:CatalogItemId:AppName (URL-encoded) — required for many titles.
        if (!string.IsNullOrWhiteSpace(catalogNamespace) &&
            !string.IsNullOrWhiteSpace(catalogItemId))
        {
            var triple = Uri.EscapeDataString($"{catalogNamespace}:{catalogItemId}:{appName}");
            yield return $"com.epicgames.launcher://apps/{triple}?action=launch&silent=true";
            yield return $"com.epicgames.launcher://apps/{triple}?action=launch";
        }

        // Fallback: bare AppName (works for some older titles).
        yield return $"com.epicgames.launcher://apps/{Uri.EscapeDataString(appName)}?action=launch&silent=true";
        yield return $"com.epicgames.launcher://apps/{Uri.EscapeDataString(appName)}?action=launch";
    }

    internal static IEnumerable<string> BuildEpicActionUris(
        string appName,
        string? catalogNamespace,
        string? catalogItemId,
        string action)
    {
        if (action is not ("launch" or "update" or "updatecheck" or "installer" or "uninstall"))
            yield break;
        foreach (var uri in BuildEpicLaunchUris(appName, catalogNamespace, catalogItemId))
        {
            var next = uri.Replace("action=launch", "action=" + action, StringComparison.Ordinal);
            if (action != "launch") next = next.Replace("&silent=true", "", StringComparison.Ordinal);
            yield return next;
        }
    }

    /// <summary>
    /// An Epic title another store delivers: Epic Games Launcher opens it and
    /// sends the install there. Exo watches nothing; the other store owns it.
    /// </summary>
    private static InstallResult HandOffToOtherStore(GameEntry game, string appName, string store)
    {
        var epic = ResolveEpicLauncher();
        if (epic is null)
            return new InstallResult { Ok = false, Message = $"Install {game.Title} from {store}." };
        HiddenStoreRuntime.SuspendFor(StoreKind.Epic, TimeSpan.FromMinutes(30));
        if (!ProcessHelper.IsProcessRunning("EpicGamesLauncher"))
            Process.Start(new ProcessStartInfo(epic) { UseShellExecute = true });
        StoreWindowHider.RestoreStoreWindows(StoreWindowHider.EpicProcessNames);
        foreach (var uri in BuildEpicActionUris(appName, null, null, "installer").Distinct(StringComparer.Ordinal))
        {
            try { ProcessHelper.StartProtocol(uri); }
            catch { /* try next */ }
        }
        return new InstallResult
        {
            Ok = true,
            HandoffOnly = true,
            Message = $"Epic Games Launcher hands {game.Title} to {store}.",
        };
    }

    private async Task<InstallResult> WatchEpicLauncherJobAsync(
        GameEntry game,
        string appName,
        IProgress<InstallProgress>? progress,
        bool uninstall,
        CancellationToken ct)
    {
        var epic = ResolveEpicLauncher();
        if (epic is null)
        {
            return new InstallResult
            {
                Ok = false,
                Message = uninstall
                    ? "Epic Games Launcher is not installed."
                    : "Epic Games Launcher is not installed, so Exo cannot install this game.",
            };
        }

        if (uninstall)
        {
            HiddenStoreRuntime.SuspendFor(StoreKind.Epic, TimeSpan.FromMinutes(30));
            Process.Start(new ProcessStartInfo(epic) { UseShellExecute = true });
            StoreWindowHider.RestoreStoreWindows(StoreWindowHider.EpicProcessNames);
            // A handoff, not a failure. Exo has not removed anything yet.
            return new InstallResult
            {
                Ok = true,
                HandoffOnly = true,
                Message = $"Choose Uninstall for {game.Title} in Epic Games Launcher.",
            };
        }

        Report(game.Id, progress, InstallPhase.Preparing, null,
            uninstall ? "Starting Epic uninstall…" : "Starting Epic install…");

        HiddenStoreRuntime.SuspendFor(StoreKind.Epic, TimeSpan.FromMinutes(30));
        if (!ProcessHelper.IsProcessRunning("EpicGamesLauncher"))
        {
            Process.Start(new ProcessStartInfo(epic) { UseShellExecute = true });
            await WaitForEpicCommandListenerAsync(ct).ConfigureAwait(false);
        }

        StoreWindowHider.RestoreStoreWindows(StoreWindowHider.EpicProcessNames);
        var meta = FindManifestMeta(appName, game.Path);
        foreach (var uri in BuildEpicActionUris(appName, meta?.CatalogNamespace, meta?.CatalogItemId, "installer"))
        {
            try { ProcessHelper.StartProtocol(uri); }
            catch { /* try next */ }
        }

        var start = DateTimeOffset.UtcNow;
        var limit = uninstall ? TimeSpan.FromMinutes(20) : TimeSpan.FromHours(2);
        while (!ct.IsCancellationRequested)
        {
            var present = FindManifestMeta(appName, game.Path) is not null ||
                          FindManifestMeta(appName, null) is not null;
            if (uninstall && !present)
            {
                Report(game.Id, progress, InstallPhase.Completed, 100, "Uninstalled.");
                return new InstallResult { Ok = true, Message = "Removed through Epic Games Launcher." };
            }

            if (!uninstall)
            {
                var installed = FindManifestMeta(appName, null);
                if (IsEglInstallComplete(
                        installed is not null,
                        installed is not null &&
                        !string.IsNullOrWhiteSpace(installed.InstallLocation) &&
                        Directory.Exists(installed.InstallLocation),
                        installed?.UpdatePending == true))
                {
                    Report(game.Id, progress, InstallPhase.Completed, 100, "Install complete.");
                    return new InstallResult
                    {
                        Ok = true,
                        Message = "Installed through Epic Games Launcher.",
                        Path = installed!.InstallLocation,
                    };
                }
            }

            if (DateTimeOffset.UtcNow - start > limit)
            {
                Report(game.Id, progress, InstallPhase.Failed, null,
                    uninstall ? "Epic uninstall timed out." : "Epic install timed out.");
                return new InstallResult
                {
                    Ok = false,
                    Message = uninstall
                        ? "Epic Games Launcher did not finish removing this game."
                        : "Epic Games Launcher did not finish installing this game.",
                };
            }

            Report(game.Id, progress, InstallPhase.Installing, null,
                uninstall ? $"Removing {game.Title}…" : $"Installing {game.Title}…");
            await Task.Delay(1000, ct).ConfigureAwait(false);
        }

        Report(game.Id, progress, InstallPhase.Cancelled, null, "Canceled.");
        return new InstallResult { Ok = false, Message = "Canceled." };
    }

    private static Process? TryStartInstalledGame(string? installPath, string? launchExecutable, string? appName)
    {
        if (string.IsNullOrWhiteSpace(installPath) || !Directory.Exists(installPath))
            return null;

        var candidates = new List<string>();
        // Prefer the real game binary over helper launchers (Rocket League: RocketLeague.exe > Launcher.exe).
        candidates.Add(Path.Combine(installPath, "Binaries", "Win64", "RocketLeague.exe"));
        if (!string.IsNullOrWhiteSpace(appName) &&
            string.Equals(appName, "Sugar", StringComparison.OrdinalIgnoreCase))
            candidates.Add(Path.Combine(installPath, "Binaries", "Win64", "RocketLeague.exe"));

        if (!string.IsNullOrWhiteSpace(launchExecutable))
        {
            var rel = launchExecutable.Replace('/', Path.DirectorySeparatorChar);
            var candidate = Path.Combine(installPath, rel);
            // Manifest JSON is writable by other local processes; only honor a
            // .exe that stays inside the install directory.
            if (string.Equals(Path.GetExtension(candidate), ".exe", StringComparison.OrdinalIgnoreCase) &&
                Helpers.PathHelper.IsUnderRoot(candidate, installPath))
            {
                // Skip generic Launcher.exe until after known game exes.
                if (!rel.EndsWith("Launcher.exe", StringComparison.OrdinalIgnoreCase))
                    candidates.Insert(0, candidate);
                else
                    candidates.Add(candidate);
            }
        }

        candidates.Add(Path.Combine(installPath, "Binaries", "Win64", "Launcher.exe"));
        if (!string.IsNullOrWhiteSpace(appName))
            candidates.Add(Path.Combine(installPath, "Binaries", "Win64", appName + ".exe"));

        foreach (var exe in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(exe)) continue;
            try
            {
                // Normal window — game should appear on the taskbar, not minimized.
                return ProcessHelper.StartGame(exe, "", Path.GetDirectoryName(exe));
            }
            catch (Exception ex)
            {
                AppLog.Debug($"Direct Epic launch fail {exe}: {ex.Message}");
            }
        }
        return null;
    }

    public async Task<InstallResult> UninstallAsync(GameEntry game, CancellationToken ct = default)
    {
        var appName = game.LaunchTarget;
        if (string.IsNullOrWhiteSpace(appName))
            return new InstallResult { Ok = false, Message = "Missing Epic app name." };

        // A game Epic's new launcher installed is removed by that launcher, which asks to confirm in its own dialog.
        // Legendary does not track it; removing its files under the launcher would leave the launcher's record behind.
        if (EglV2Install(appName, game.Path) is { } v2 && ResolveEpicLauncher() is { } eglExe)
            return RemoveInEgl(game, v2, eglExe);

        var legendary = ResolveLegendary();
        if (legendary is not null)
        {
            try
            {
                // An unbounded CLI wait wedges the orchestrator: the job never
                // returns, so every later install, update, or remove is refused
                // until Exo restarts.
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(LegendaryUninstallTimeout);
                var (code, stdout, err) = await CliRunner.RunAsync(
                    legendary, LegendaryCli.UninstallArgs(appName), null, null, timeout.Token)
                    .ConfigureAwait(false);
                // "Game X not installed, cannot uninstall!" exits 0: Legendary removed nothing, so it is not a success.
                if (code == 0 && !LegendaryCli.NotInstalled(err + stdout))
                    return new InstallResult { Ok = true, Message = "Removed." };
                AppLog.Info($"Legendary did not remove '{appName}' (exit {code}), trying Epic Games Launcher: " + err.Trim());
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                // Legendary owned the files for this attempt. Do not also ask the
                // Epic launcher to remove a title mid-delete.
                AppLog.Warn($"Legendary uninstall for '{appName}' timed out.");
                return new InstallResult
                {
                    Ok = false,
                    Message = "Exo did not finish removing this game. Check its install folder before trying again.",
                };
            }
            catch (Exception ex)
            {
                AppLog.Debug("Legendary uninstall failed, trying Epic launcher: " + ex.Message);
            }
        }

        // Only the old launcher's manifests leave a removal to that launcher's window. Anything else no store holds:
        // sending the user to look for it there was a Remove that did nothing.
        if (FindItemManifestMeta(appName, game.Path) is null)
            return new InstallResult
            {
                Ok = false,
                Message = $"Neither Legendary nor Epic Games Launcher has {game.Title} installed, so Exo has nothing to remove. Remove it in the app that installed it.",
            };
        return await WatchEpicLauncherJobAsync(game, appName, progress: null, uninstall: true, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Opens the new launcher's own uninstall confirmation for this game (its action=uninstall link). The launcher
    /// shows its window for the dialog; Exo's install-list watcher refreshes the library once the record is gone.
    /// </summary>
    private static InstallResult RemoveInEgl(GameEntry game, EpicLauncherV2.Installation install, string epic)
    {
        AppLog.Info($"Epic remove in Epic Games Launcher: gameId={game.Id}.");
        HiddenStoreRuntime.SuspendFor(StoreKind.Epic, TimeSpan.FromMinutes(30));
        if (!ProcessHelper.IsProcessRunning("EpicGamesLauncher"))
            Process.Start(new ProcessStartInfo(epic) { UseShellExecute = true });
        StoreWindowHider.RestoreStoreWindows(StoreWindowHider.EpicProcessNames);
        try
        {
            ProcessHelper.StartProtocol(BuildEpicActionUris(install.AppName, install.Namespace, install.ItemId, "uninstall").First());
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Epic remove link for '{install.AppName}' failed: {ex.Message}");
            return new InstallResult { Ok = false, Message = $"Exo could not open Epic Games Launcher to remove {game.Title}." };
        }
        return new InstallResult
        {
            Ok = true,
            HandoffOnly = true,
            Message = $"Confirm removing {game.Title} in Epic Games Launcher.",
        };
    }

    public InstallProgress GetDownloadProgress(string gameId) =>
        _progress.TryGetValue(gameId, out var p) ? p : new InstallProgress { GameId = gameId, Phase = InstallPhase.Idle };

    public Task CleanupAfterExitAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default)
    {
        if (options.CloseStoreUiAfterExit)
            StoreWindowHider.CollapseOrphanSurfaces(StoreWindowHider.EpicProcessNames);
        return Task.CompletedTask;
    }

    private void Report(string gameId, IProgress<InstallProgress>? progress, InstallPhase phase, double? pct, string status)
    {
        var p = new InstallProgress
        {
            GameId = gameId,
            Phase = phase,
            Percent = pct,
            Status = status,
            CanCancel = phase is not (InstallPhase.Completed or InstallPhase.Failed or InstallPhase.Cancelled or InstallPhase.Idle),
        };
        _progress[gameId] = p;
        progress?.Report(p);
    }

    private static IEnumerable<GameEntry> ReadEpicManifests(bool hasLegendary, LatestBuilds latest)
    {
        var manifestDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(manifestDir)) yield break;

        foreach (var file in Directory.EnumerateFiles(manifestDir, "*.item"))
        {
            GameEntry? entry = null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var root = doc.RootElement;
                var name = root.TryGetProperty("DisplayName", out var n) ? n.GetString() : null;
                var appName = root.TryGetProperty("AppName", out var a) ? a.GetString() : null;
                var install = root.TryGetProperty("InstallLocation", out var i) ? i.GetString() : null;
                if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(appName)) continue;
                long? size = null;
                if (root.TryGetProperty("InstallSize", out var sz) && sz.TryGetInt64(out var s)) size = s;

                var title = EpicEglMerge.NormalizeEpicTitle(name, appName);
                if (string.IsNullOrWhiteSpace(title))
                    title = appName!;

                var installed = InstallPresent(install, root.TryGetProperty("LaunchExecutable", out var exe) && exe.ValueKind == JsonValueKind.String ? exe.GetString() : null);
                var updateAvailable = installed && (IsEglUpdatePending(root) || latest.NewerThan(appName ?? "",
                    root.TryGetProperty("AppVersionString", out var version) && version.ValueKind == JsonValueKind.String ? version.GetString() : null,
                    File.GetLastWriteTimeUtc(file)));
                entry = new GameEntry
                {
                    Id = "epic:" + (appName ?? name!.ToLowerInvariant()),
                    Title = title,
                    Store = StoreKind.Epic,
                    Installed = installed,
                    // EGL manifests are machine-install evidence, not proof
                    // that the currently active Epic account owns the title.
                    Owned = false,
                    EntitlementState = EntitlementState.Unverified,
                    UpdateAvailable = updateAvailable,
                    CanInstall = false,
                    Path = install,
                    LaunchTarget = appName,
                    SizeBytes = size,
                    Status = installed ? (updateAvailable ? "Update" : "Ready") : "Not installed",
                    Deps = hasLegendary
                        ? new[] { "Legendary (preferred)" }
                        : new[] { "Epic Games Launcher" },
                    LaunchNote = hasLegendary
                        ? "Launches via Legendary when available."
                        : "Launches the installed game directly when possible.",
                };
            }
            catch { /* skip */ }

            if (entry is not null && CatalogHygiene.KeepEpicRow(entry.Title, [], installed: true))
                yield return entry;
        }
    }

    /// <summary>The new launcher's installed games (installations.jsonl), titled from its catalog cache and download history.</summary>
    private static IEnumerable<GameEntry> ReadLauncherV2(bool hasLegendary, IReadOnlyList<GameEntry> ownedForTitles, LatestBuilds latest)
    {
        var dataDir = EpicLauncherV2.DataDir();
        if (dataDir is null) yield break;
        var nonGames = EpicLauncherV2.ReadNonGameIds(dataDir);
        // A first install never finished is a folder, not a game; Unreal Engine and Twinmotion are not games either.
        var installs = EpicLauncherV2.ReadInstallations(dataDir)
            .Where(install => install.EverInstalled && !EpicLauncherV2.IsNonGame(install, nonGames))
            .ToList();
        if (installs.Count == 0) yield break;
        var catalog = EpicLauncherV2.ReadTitles(dataDir);
        var titleByApp = ownedForTitles
            .Where(g => !string.IsNullOrWhiteSpace(g.LaunchTarget))
            .GroupBy(g => g.LaunchTarget!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Title, StringComparer.OrdinalIgnoreCase);
        string? TitleOf(EpicLauncherV2.Installation install) =>
            install.ItemId is { } item && catalog.TryGetValue(item, out var catalogTitle) ? catalogTitle
                : titleByApp.GetValueOrDefault(install.AppName);
        // A game's modes (Fortnite's LEGO Fortnite Odyssey, Save the World) fold into its one card.
        var (kept, modesOf) = EpicLauncherV2.FoldModes(installs, TitleOf);
        foreach (var (main, modes) in modesOf) EpicPlaytime.RegisterModes(main, modes);
        foreach (var install in kept)
        {
            var known = TitleOf(install);
            var title = EpicEglMerge.NormalizeEpicTitle(known, install.AppName);
            if (string.IsNullOrWhiteSpace(title)) title = install.AppName;
            if (!CatalogHygiene.KeepEpicRow(title, [], installed: true)) continue;
            var installed = InstallPresent(install.Dir, install.LaunchExecutable);
            var updateAvailable = installed && (!install.UpToDate ||
                latest.NewerThan(install.AppName, install.Version, install.WrittenAt?.UtcDateTime ?? DateTime.MaxValue));
            yield return new GameEntry
            {
                Id = "epic:" + install.AppName,
                Title = title,
                Store = StoreKind.Epic,
                Installed = installed,
                // Like the old launcher's manifests: install evidence on this PC, not ownership.
                Owned = false,
                EntitlementState = EntitlementState.Unverified,
                UpdateAvailable = updateAvailable,
                CanInstall = false,
                Path = install.Dir,
                LaunchTarget = install.AppName,
                Status = installed ? (updateAvailable ? "Update" : "Ready") : "Not installed",
                Deps = hasLegendary ? new[] { "Legendary (preferred)" } : new[] { "Epic Games Launcher" },
                LaunchNote = hasLegendary ? "Launches via Legendary when available." : "Installed via Epic Games Launcher.",
            };
        }
    }

    internal static bool IsEglUpdatePending(JsonElement root) =>
        ReadEglBool(root, "bIsIncompleteInstall") || ReadEglBool(root, "bNeedsValidation");

    /// <summary>
    /// The Epic Games Launcher writes the .item manifest and creates the install
    /// folder when a download starts, so folder existence alone reported
    /// "Install complete" while the download was still at zero percent. The
    /// manifest's own incomplete / needs-validation flags are the completion
    /// evidence.
    /// </summary>
    internal static bool IsEglInstallComplete(
        bool manifestPresent,
        bool installLocationExists,
        bool updatePending) =>
        manifestPresent && installLocationExists && !updatePending;

    private static bool ReadEglBool(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return false;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => value.TryGetInt64(out var number) && number != 0,
            JsonValueKind.String => value.GetString() is "1" or "true" or "True",
            _ => false,
        };
    }

    /// <summary>Parse ProgramData LauncherInstalled.dat (EGL install registry).</summary>
    private static IEnumerable<GameEntry> ReadLauncherInstalled(IReadOnlyList<GameEntry> ownedForTitles)
    {
        var roots = new[]
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Epic", "UnrealEngineLauncher", "LauncherInstalled.dat"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Epic", "EpicGamesLauncher", "Data", "LauncherInstalled.dat"),
        };

        var titleByApp = ownedForTitles
            .Where(g => !string.IsNullOrWhiteSpace(g.LaunchTarget))
            .GroupBy(g => g.LaunchTarget!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Title, StringComparer.OrdinalIgnoreCase);

        foreach (var path in roots)
        {
            if (!File.Exists(path)) continue;
            IEnumerable<GameEntry>? batch = null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("InstallationList", out var list)
                    || list.ValueKind != JsonValueKind.Array)
                    continue;

                var acc = new List<GameEntry>();
                foreach (var el in list.EnumerateArray())
                {
                    var appName = el.TryGetProperty("AppName", out var a) ? a.GetString()
                        : el.TryGetProperty("ArtifactId", out var art) ? art.GetString()
                        : null;
                    var install = el.TryGetProperty("InstallLocation", out var i) ? i.GetString() : null;
                    if (string.IsNullOrWhiteSpace(appName)) continue;
                    if (string.IsNullOrWhiteSpace(install) || !Directory.Exists(install)) continue;

                    titleByApp.TryGetValue(appName, out var title);
                    title = EpicEglMerge.NormalizeEpicTitle(title, appName);
                    if (string.IsNullOrWhiteSpace(title)) title = appName;
                    if (!CatalogHygiene.KeepEpicRow(title, [], installed: true)) continue;

                    acc.Add(new GameEntry
                    {
                        Id = "epic:" + appName,
                        Title = title,
                        Store = StoreKind.Epic,
                        Installed = true,
                        // LauncherInstalled.dat is shared machine state. Keep
                        // the playable install without leaking another user's
                        // entitlement into the active account.
                        Owned = false,
                        EntitlementState = EntitlementState.Unverified,
                        CanInstall = false,
                        Path = install,
                        LaunchTarget = appName,
                        Status = "Ready",
                        Deps = new[] { "Epic Games Launcher" },
                        LaunchNote = "Installed via Epic Games Launcher.",
                    });
                }
                batch = acc;
            }
            catch { /* skip broken dat */ }

            if (batch is null) continue;
            foreach (var g in batch) yield return g;
        }
    }

    /// <summary>
    /// The flag that starts the launcher in its tray with no window. The old
    /// launcher (under Portal\Binaries) takes -silent; the new one (2026) is a
    /// Tauri app with no -silent, which starts hidden only with its own
    /// autostart flag, --autostart (tauri-plugin-autostart, in its binary).
    /// </summary>
    internal static string QuietStartFlag(string launcherExe) =>
        launcherExe.Contains(@"\Portal\Binaries\", StringComparison.OrdinalIgnoreCase) ? "-silent" : "--autostart";

    internal static string? ResolveLegendary()
    {
        var managedCache = Path.Combine(PathHelper.AppDataDir, "tools", "legendary.exe");
        var packagedTool = Path.Combine(PathHelper.AppDirectory, "tools", "legendary.exe");

        // PATH entries are explicitly user-installed and remain user-trusted,
        // unless PATH resolves back into an Exo-managed tools location.
        foreach (var candidate in new[]
                 {
                     CliRunner.ResolveOnPath("legendary.exe"),
                     CliRunner.ResolveOnPath("legendary"),
                 })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            if (IsSamePath(candidate, managedCache) || IsSamePath(candidate, packagedTool))
            {
                if (PinnedToolCache.IsPinnedAsset(
                        LegendaryReleaseAsset,
                        candidate,
                        IsValidAmd64Pe))
                    return candidate;
                continue;
            }
            if (IsValidAmd64Pe(candidate)) return candidate;
        }

        foreach (var managed in new[] { managedCache, packagedTool })
            if (PinnedToolCache.IsPinnedAsset(
                    LegendaryReleaseAsset,
                    managed,
                    IsValidAmd64Pe))
                return managed;

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var extra in new[]
                 {
                     Path.Combine(local, "legendary", "legendary.exe"),
                     Path.Combine(local, "heroic", "tools", "legendary", "legendary.exe"),
                     Path.Combine(local, "HeroicGamesLauncher", "tools", "legendary", "legendary.exe"),
                     Path.Combine(local, "Programs", "heroic", "resources", "app.asar.unpacked", "build", "bin", "x64", "legendary.exe"),
                 })
        {
            if (IsValidAmd64Pe(extra)) return extra;
        }
        return null;
    }

    private static bool IsSamePath(string left, string right)
    {
        try
        {
            return Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    internal static readonly OfficialClientDefinition EpicClientDefinition = new(
        ExecutableNames: ["EpicGamesLauncher.exe"],
        // The new launcher (2026) installs per user, with its exe at the top of its folder.
        DefaultPaths: new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Epic Games", "Launcher", "EpicGamesLauncher.exe") }
            .Concat(new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }
                .SelectMany(folder => new[] { "Win64", "Win32" }.Select(architecture =>
                    Path.Combine(Environment.GetFolderPath(folder), "Epic Games", "Launcher", "Portal", "Binaries", architecture, "EpicGamesLauncher.exe"))))
            .ToArray(),
        UninstallDisplayNames: ["Epic Games Launcher"],
        InstallRelativePaths: [@"Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe", @"Launcher\Portal\Binaries\Win32\EpicGamesLauncher.exe",
            @"Portal\Binaries\Win64\EpicGamesLauncher.exe", @"Portal\Binaries\Win32\EpicGamesLauncher.exe",
            @"EpicGamesLauncher.exe", @"Launcher\EpicGamesLauncher.exe"]);

    internal static string? ResolveEpicLauncher() =>
        OfficialClientLocator.Resolve(EpicClientDefinition)?.FileName;

    /// <summary>This PC has the old Epic Games Launcher (under Portal\Binaries) and not Epic's new one (2026) yet.</summary>
    internal static bool OnLegacyLauncher() =>
        ResolveEpicLauncher() is { } launcher && launcher.Contains(@"\Portal\Binaries\", StringComparison.OrdinalIgnoreCase)
        && !File.Exists(EpicClientDefinition.DefaultPaths[0]);
}
