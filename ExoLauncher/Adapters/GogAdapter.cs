using System.Collections.Concurrent;
using ExoLauncher.Adapters.Cli;
using ExoLauncher.Helpers;
using ExoLauncher.Models;
using ExoLauncher.Services;
using Microsoft.Win32;

namespace ExoLauncher.Adapters;

internal delegate Task<(int ExitCode, string StdOut, string StdErr)> GogdlCommandRunner(
    string fileName,
    IReadOnlyList<string> args,
    string? workingDirectory,
    Action<string>? onLine,
    CancellationToken ct);

/// <summary>
/// GOG via heroic-gogdl when present; offline registry titles launch as local exes.
/// https://github.com/Heroic-Games-Launcher/heroic-gogdl
/// </summary>
public sealed class GogAdapter : IStoreAdapter, IStoreClientPresence
{
    internal const string ManagedInstallStagingPrefix = ".exo-gog-partial-";

    private readonly ConcurrentDictionary<string, InstallProgress> _progress = new(StringComparer.OrdinalIgnoreCase);
    private readonly GogAuthService? _authService;
    private readonly GogOwnedLibraryService? _ownedLibraryService;
    private readonly InstalledGameCatalog _installedCatalog;
    private readonly string? _gogdlPathOverride;
    private readonly GogdlCommandRunner _commandRunner;
    private readonly object _ownedRefreshGate = new();
    private Task? _ownedRefreshTask;
    private DateTimeOffset _ownedRefreshRetryAfterUtc;

    public GogAdapter(
        GogAuthService? authService = null,
        GogOwnedLibraryService? ownedLibraryService = null)
        : this(authService, ownedLibraryService, InstalledGameCatalog.Default)
    {
    }

    internal GogAdapter(
        GogAuthService? authService,
        GogOwnedLibraryService? ownedLibraryService,
        InstalledGameCatalog installedCatalog,
        string? gogdlPathOverride = null,
        GogdlCommandRunner? commandRunner = null)
    {
        _authService = authService;
        _ownedLibraryService = ownedLibraryService;
        _installedCatalog = installedCatalog ?? throw new ArgumentNullException(nameof(installedCatalog));
        _gogdlPathOverride = gogdlPathOverride;
        _commandRunner = commandRunner ?? CliRunner.RunAsync;
    }

    public StoreKind Store => StoreKind.Gog;
    public string Id => "gog";
    public string DisplayName => "GOG";

    // gogdl can be bundled with Exo. It is a usable backend, but must never
    // make Settings claim that the separately installed Galaxy client exists.
    public bool IsAgentPresent() => ResolveGogdl() is not null || ResolveGalaxy() is not null;
    public bool IsClientPresent() => ResolveGalaxy() is not null;

    public async Task<AuthResult> AuthenticateAsync(CancellationToken ct = default)
    {
        try
        {
            var gogdl = ResolveGogdl() ?? await EnsureGogdlAsync(ct).ConfigureAwait(false);
            if (gogdl is not null)
            {
                if (_authService is null)
                    return new AuthResult
                    {
                        Ok = false,
                        RequiresUserAction = true,
                        Message = "GOG sign-in service is unavailable. Restart Exo and try again.",
                    };
                var auth = await _authService.SignInAsync(gogdl, ct).ConfigureAwait(false);
                if (!auth.Ok || _ownedLibraryService is null) return auth;

                var authPath = GogAuthService.FindExistingAuthConfigPath();
                if (authPath is null)
                    return new AuthResult
                    {
                        Ok = true,
                        RequiresUserAction = false,
                        Message = "GOG connected. Library sync will retry in the background.",
                    };

                GogOwnedLibrarySyncResult sync;
                try
                {
                    using var syncTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    syncTimeout.CancelAfter(TimeSpan.FromMinutes(2));
                    sync = await RefreshOwnedLibraryAsync(
                            authPath,
                            gogdl,
                            force: true,
                            syncTimeout.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    sync = new GogOwnedLibrarySyncResult(
                        false, false, 0, false, "Library sync will retry in the background.");
                }
                return new AuthResult
                {
                    Ok = true,
                    RequiresUserAction = false,
                    Message = $"GOG connected. {sync.Message}",
                };
            }

            var galaxy = ResolveGalaxy();
            if (galaxy is not null)
            {
                return new AuthResult
                {
                    Ok = false,
                    RequiresUserAction = true,
                    Message = "gogdl is required for hidden GOG actions. Install it from the official source, then Refresh.",
                };
            }

            return new AuthResult
            {
                Ok = false,
                RequiresUserAction = true,
                Message = "gogdl / GOG Galaxy not found. Install GOG Galaxy or place gogdl.exe in Exo tools.",
            };
        }
        catch (Exception ex)
        {
            return new AuthResult { Ok = false, Message = ex.Message, RequiresUserAction = true };
        }
    }

    /// <summary>AMD64 only. Reject ARM64 binaries (Windows shows "Machine Type Mismatch").</summary>
    private const ushort PeMachineAmd64 = 0x8664;
    private static readonly GitHubReleaseAsset GogdlReleaseAsset = new(
        "Heroic-Games-Launcher",
        "heroic-gogdl",
        "v1.3.0",
        "gogdl_windows_x86_64.exe",
        ExpectedSize: 12_304_645,
        ExpectedSha256: "69ea54467371803f681d6c39805992e3a4b8ddccb44ac8a1de7b1e3c80deaeec");

    /// <summary>Download gogdl_windows_x86_64.exe into Exo tools if absent / wrong arch.</summary>
    private static async Task<string?> EnsureGogdlAsync(CancellationToken ct)
    {
        try
        {
            var tools = Path.Combine(PathHelper.AppDataDir, "tools");
            Directory.CreateDirectory(tools);
            var dest = Path.Combine(tools, "gogdl.exe");
            return await VerifiedGitHubReleaseDownloader.Shared.DownloadPinnedAsync(
                    GogdlReleaseAsset,
                    dest,
                    IsValidAmd64Pe,
                    ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Warn("EnsureGogdl failed: " + ex.Message);
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
            if (pe[0] != (byte)'P' || pe[1] != (byte)'E') return false;
            var machine = BitConverter.ToUInt16(pe.Slice(4, 2));
            return machine == PeMachineAmd64;
        }
        catch
        {
            return false;
        }
    }

    public Task<IReadOnlyList<GameEntry>> GetLibraryAsync(CancellationToken ct = default)
    {
        ScheduleOwnedLibraryRefresh();

        var installed = new List<GogdlCli.OwnedGame>();
        var owned = new List<GogdlCli.OwnedGame>();
        var exeById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Installed titles from GOG registry.
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games")
                ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\GOG.com\Games");
            if (key is not null)
            {
                foreach (var subName in key.GetSubKeyNames())
                {
                    ct.ThrowIfCancellationRequested();
                    using var sub = key.OpenSubKey(subName);
                    if (sub is null) continue;
                    var name = sub.GetValue("gameName") as string ?? sub.GetValue("GAMENAME") as string;
                    var path = sub.GetValue("path") as string ?? sub.GetValue("PATH") as string;
                    var exe = sub.GetValue("exe") as string ?? sub.GetValue("EXE") as string;
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                        continue;

                    installed.Add(new GogdlCli.OwnedGame(subName, name, path, true));

                    if (!string.IsNullOrWhiteSpace(exe))
                    {
                        var launchExe = Path.Combine(path, exe);
                        if (File.Exists(launchExe)) exeById[subName] = launchExe;
                    }
                }
            }
        }
        catch { }

        // gogdl does not reliably write the Galaxy registry. Exo's own
        // successful-install catalog is therefore a second, durable installed
        // manifest. Registry installs remain discoverable but never gain Exo's
        // deletion authority merely by being found here.
        foreach (var managed in _installedCatalog.GetInstalledGames(StoreKind.Gog))
        {
            ct.ThrowIfCancellationRequested();
            var productId = ExtractGogId(managed);
            if (string.IsNullOrWhiteSpace(productId) || string.IsNullOrWhiteSpace(managed.Path))
                continue;
            installed.Add(new GogdlCli.OwnedGame(
                productId,
                managed.Title,
                managed.Path,
                true,
                managed.CoverUrl));
            if (!string.IsNullOrWhiteSpace(managed.LaunchTarget) && File.Exists(managed.LaunchTarget))
                exeById[productId] = managed.LaunchTarget;
        }

        // Owned-but-not-installed data is account-scoped whenever Exo's GOG
        // sync service is available. Never surface another local user's stale
        // Heroic/Galaxy cache after an account switch.
        var authPath = GogAuthService.FindExistingAuthConfigPath();
        var currentCredentials = authPath is null ? null : ReadCredentials(authPath);
        if (_ownedLibraryService is not null && currentCredentials is not null)
        {
            owned.AddRange(_ownedLibraryService.LoadCachedOwnedGames(currentCredentials.UserId));
        }
        else if (_ownedLibraryService is null)
        {
            // Compatibility fallback for hosts that did not register the
            // authenticated owned-library service.
            foreach (var file in LegacyOwnedLibraryCandidates().Distinct(StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (!File.Exists(file)) continue;
                    owned.AddRange(GogdlCli.ParseOwnedLibraryJson(File.ReadAllText(file)));
                }
                catch { /* skip bad cache files */ }
            }
        }

        var gogdlPresent = ResolveGogdl() is not null;
        var merged = GogdlCli.MergeOwnedAndInstalled(owned, installed);
        var playtimes = GogPlaytime.LoadAll();
        var games = merged.Select(row =>
        {
            exeById.TryGetValue(row.Id, out var launchExe);
            playtimes.TryGetValue(row.Id, out var mins);
            return new GameEntry
            {
                Id = "gog:" + row.Id,
                Title = row.Title,
                Store = StoreKind.Gog,
                Installed = row.Installed,
                Owned = true,
                CanInstall = !row.Installed && gogdlPresent,
                Path = row.InstallPath,
                LaunchTarget = launchExe ?? row.InstallPath,
                CoverUrl = row.CoverUrl,
                CoverSource = string.IsNullOrWhiteSpace(row.CoverUrl) ? null : "gog",
                PlaytimeMinutes = mins > 0 ? mins : null,
                Status = row.Installed ? "Ready" : "Not installed",
                Deps = gogdlPresent
                    ? new[] { "gogdl" }
                    : new[] { "gogdl or GOG Galaxy" },
                LaunchNote = row.Installed
                    ? "Launches the installed GOG build. Install/update via gogdl when available."
                    : "Owned on GOG. Install via gogdl when available.",
            };
        }).ToList();

        return Task.FromResult<IReadOnlyList<GameEntry>>(games);
    }

    private static IEnumerable<string> LegacyOwnedLibraryCandidates()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        yield return Path.Combine(PathHelper.AppDataDir, "gog", "library.json");
        yield return GogdlCli.HeroicLibraryCachePath(roaming);
        yield return Path.Combine(local, "heroic", "gog_store", "library.json");
        yield return Path.Combine(roaming, "heroic", "gog_store", "library.json");
        yield return Path.Combine(user, ".config", "heroic", "gog_store", "library.json");
        yield return Path.Combine(local, "GOG.com", "Galaxy", "webcache", "library.json");
    }

    private void ScheduleOwnedLibraryRefresh()
    {
        if (_ownedLibraryService is null) return;
        var authPath = GogAuthService.FindExistingAuthConfigPath();
        if (authPath is null) return;
        var credentials = ReadCredentials(authPath);
        if (credentials is null || _ownedLibraryService.IsCacheFresh(credentials.UserId)) return;

        lock (_ownedRefreshGate)
        {
            if (DateTimeOffset.UtcNow < _ownedRefreshRetryAfterUtc) return;
            if (_ownedRefreshTask is { IsCompleted: false }) return;
            var gogdl = ResolveGogdl();
            var task = Task.Run(async () =>
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                    var result = await RefreshOwnedLibraryAsync(
                            authPath,
                            gogdl,
                            force: false,
                            timeout.Token)
                        .ConfigureAwait(false);
                    lock (_ownedRefreshGate)
                    {
                        _ownedRefreshRetryAfterUtc = result.Ok
                            ? DateTimeOffset.MinValue
                            : DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5);
                    }
                    if (!result.Ok) AppLog.Debug("GOG background library refresh did not complete.");
                }
                catch (OperationCanceledException)
                {
                    lock (_ownedRefreshGate)
                        _ownedRefreshRetryAfterUtc = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5);
                    AppLog.Debug("GOG background library refresh timed out.");
                }
                catch (Exception ex)
                {
                    lock (_ownedRefreshGate)
                        _ownedRefreshRetryAfterUtc = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5);
                    AppLog.Debug($"GOG background library refresh failed ({ex.GetType().Name}).");
                }
            });
            _ownedRefreshTask = task;
            _ = task.ContinueWith(
                completed =>
                {
                    lock (_ownedRefreshGate)
                    {
                        if (ReferenceEquals(_ownedRefreshTask, completed)) _ownedRefreshTask = null;
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task<GogOwnedLibrarySyncResult> RefreshOwnedLibraryAsync(
        string authPath,
        string? gogdl,
        bool force,
        CancellationToken ct)
    {
        if (_ownedLibraryService is null)
            return new GogOwnedLibrarySyncResult(false, false, 0, false, "GOG library sync is unavailable.");

        var credentials = ReadCredentials(authPath);
        if (credentials is null)
            return new GogOwnedLibrarySyncResult(false, false, 0, false, "GOG credentials could not be read.");

        if (credentials.IsExpired(DateTimeOffset.UtcNow) && gogdl is not null)
            credentials = await RefreshCredentialsWithGogdlAsync(gogdl, authPath, ct).ConfigureAwait(false)
                          ?? credentials;

        var result = await _ownedLibraryService.RefreshAsync(credentials, force, ct).ConfigureAwait(false);
        if (!result.Unauthorized || gogdl is null) return result;

        // Let gogdl refresh its own token format, then retry exactly once.
        var refreshed = await RefreshCredentialsWithGogdlAsync(gogdl, authPath, ct).ConfigureAwait(false);
        return refreshed is null
            ? result
            : await _ownedLibraryService.RefreshAsync(refreshed, force: true, ct).ConfigureAwait(false);
    }

    private static GogdlCli.AuthCredentials? ReadCredentials(string authPath)
    {
        try
        {
            return File.Exists(authPath) &&
                   GogdlCli.TryReadCredentials(File.ReadAllText(authPath), out var credentials)
                ? credentials
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<GogdlCli.AuthCredentials?> RefreshCredentialsWithGogdlAsync(
        string gogdl,
        string authPath,
        CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var (exitCode, stdout, _) = await CliRunner.RunAsync(
                    gogdl,
                    GogdlCli.AuthStatusArgs(authPath),
                    null,
                    null,
                    timeout.Token)
                .ConfigureAwait(false);
            if (exitCode == 0 && GogdlCli.TryReadCredentials(stdout, out var stdoutCredentials))
                return stdoutCredentials;
            return ReadCredentials(authPath);
        }
        catch
        {
            return ReadCredentials(authPath);
        }
    }

    public async Task<InstallResult> InstallAsync(
        GameEntry game,
        string? installPath,
        IProgress<InstallProgress>? progress,
        CancellationToken ct = default)
    {
        var gogdl = _gogdlPathOverride ?? ResolveGogdl() ?? await EnsureGogdlAsync(ct).ConfigureAwait(false);
        if (gogdl is null)
        {
            return new InstallResult
            {
                Ok = false,
                Message = "gogdl required for GOG install. Place gogdl.exe on PATH or tools/.",
            };
        }

        var gameId = ExtractGogId(game);
        if (string.IsNullOrWhiteSpace(gameId))
            return new InstallResult { Ok = false, Message = "Missing GOG product id." };

        if (!InstalledGameCatalog.TryCreateGogInstallLocation(
                installPath ?? PathHelper.GamesRoot,
                gameId,
                out var location,
                out var locationError))
            return new InstallResult { Ok = false, Message = locationError };

        var catalogGameId = "gog:" + gameId;
        var hasRegisteredManagedInstall = Directory.Exists(location.InstallPath) &&
            _installedCatalog.IsRegisteredManagedInstall(
                StoreKind.Gog,
                catalogGameId,
                location.InstallPath,
                location.ManagedRoot);
        if (Path.Exists(location.InstallPath) && !hasRegisteredManagedInstall)
        {
            return new InstallResult
            {
                Ok = false,
                Message = "The selected GOG destination already exists and is not an Exo-managed install. Choose another library root; no files were changed.",
            };
        }

        Report(game.Id, progress, InstallPhase.Preparing, 0, "Starting gogdl download…");
        var isNewInstall = !hasRegisteredManagedInstall;
        var promotedNewInstall = false;
        string? stagingPath = null;
        var path = location.InstallPath;
        try
        {
            Directory.CreateDirectory(location.ManagedRoot);
            if ((File.GetAttributes(location.ManagedRoot) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Refusing to install through a reparse-point GOG library root.");

            if (isNewInstall)
            {
                stagingPath = CreateManagedInstallStagingPath(
                    location.ManagedRoot,
                    Path.GetFileName(location.InstallPath));
                Directory.CreateDirectory(stagingPath);
                if (!RecursiveDeleteGuard.TryValidateManagedChild(
                        location.ManagedRoot,
                        stagingPath,
                        out path,
                        out var stagingError))
                {
                    throw new IOException(stagingError);
                }
            }
            else if (!RecursiveDeleteGuard.TryValidateManagedChild(
                         location.ManagedRoot,
                         location.InstallPath,
                         out path,
                         out var validationError))
            {
                throw new IOException(validationError);
            }

            var (code, _, err) = await _commandRunner(
                gogdl,
                GogdlCli.WithAuthConfig(
                    GogAuthService.EffectiveAuthConfigPath,
                    GogdlCli.DownloadArgs(gameId, path)),
                null,
                line =>
                {
                    var p = GogdlCli.ToProgress(game.Id, line);
                    _progress[game.Id] = p;
                    progress?.Report(p);
                },
                ct).ConfigureAwait(false);

            if (code != 0)
            {
                if (stagingPath is not null)
                    TryDeleteGogInstallDirectory(
                        location.ManagedRoot,
                        stagingPath,
                        requireStagingName: true);
                Report(game.Id, progress, InstallPhase.Failed, null, err.Trim());
                return new InstallResult { Ok = false, Message = err.Trim().Length > 0 ? err.Trim() : $"gogdl exited {code}." };
            }

            if (isNewInstall)
            {
                if (!Directory.EnumerateFileSystemEntries(path).Any())
                    throw new IOException("gogdl completed without producing an install.");
                if (!RecursiveDeleteGuard.TryValidateManagedChild(
                        location.ManagedRoot,
                        path,
                        out _,
                        out var stagingError))
                {
                    throw new IOException("The staged GOG install could not be validated: " + stagingError);
                }
                if (Path.Exists(location.InstallPath))
                    throw new IOException("The final GOG destination appeared while the install was in progress.");

                // Both directories are siblings under the same managed root,
                // making this rename an atomic promotion on the target volume.
                Directory.Move(path, location.InstallPath);
                promotedNewInstall = true;
                stagingPath = null;
                path = location.InstallPath;
            }

            try
            {
                _installedCatalog.Register(
                    new GameEntry
                    {
                        Id = catalogGameId,
                        Title = game.Title,
                        Store = StoreKind.Gog,
                        Installed = true,
                        Owned = true,
                        Path = path,
                    },
                    launchTarget: null,
                    managed: true,
                    location.ManagedRoot);
            }
            catch (Exception ex)
            {
                if (promotedNewInstall)
                    TryDeleteGogInstallDirectory(
                        location.ManagedRoot,
                        location.InstallPath,
                        requireStagingName: false);
                Report(game.Id, progress, InstallPhase.Failed, null, "Could not save installed manifest.");
                return new InstallResult
                {
                    Ok = false,
                    Message = "GOG finished downloading, but Exo could not save its installed manifest: " + ex.Message,
                };
            }

            Report(game.Id, progress, InstallPhase.Completed, 100, "Install complete.");
            return new InstallResult { Ok = true, Message = "Installed via gogdl.", Path = path };
        }
        catch (OperationCanceledException)
        {
            if (stagingPath is not null)
                TryDeleteGogInstallDirectory(
                    location.ManagedRoot,
                    stagingPath,
                    requireStagingName: true);
            else if (promotedNewInstall)
                TryDeleteGogInstallDirectory(
                    location.ManagedRoot,
                    location.InstallPath,
                    requireStagingName: false);
            Report(game.Id, progress, InstallPhase.Cancelled, null, "Cancelled.");
            return new InstallResult { Ok = false, Message = "Cancelled." };
        }
        catch (Exception ex)
        {
            if (stagingPath is not null)
                TryDeleteGogInstallDirectory(
                    location.ManagedRoot,
                    stagingPath,
                    requireStagingName: true);
            else if (promotedNewInstall)
                TryDeleteGogInstallDirectory(
                    location.ManagedRoot,
                    location.InstallPath,
                    requireStagingName: false);
            Report(game.Id, progress, InstallPhase.Failed, null, ex.Message);
            return new InstallResult { Ok = false, Message = ex.Message };
        }
    }

    private static string CreateManagedInstallStagingPath(string managedRoot, string safeProductDirectoryName)
    {
        while (true)
        {
            var candidate = Path.Combine(
                managedRoot,
                $"{ManagedInstallStagingPrefix}{safeProductDirectoryName}-{Guid.NewGuid():N}");
            if (!Path.Exists(candidate)) return candidate;
        }
    }

    private static void TryDeleteGogInstallDirectory(
        string managedRoot,
        string candidatePath,
        bool requireStagingName)
    {
        if (!Directory.Exists(candidatePath)) return;

        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(managedRoot));
            var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidatePath));
            if (!string.Equals(Path.GetDirectoryName(candidate), root, StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Warn("GOG install cleanup refused a non-sibling path.");
                return;
            }
            if (requireStagingName &&
                !Path.GetFileName(candidate).StartsWith(
                    ManagedInstallStagingPrefix,
                    StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Warn("GOG install cleanup refused a non-staging path.");
                return;
            }
            if (!RecursiveDeleteGuard.TryValidateManagedChild(
                    root,
                    candidate,
                    out var validatedPath,
                    out var validationError))
            {
                AppLog.Warn("GOG install cleanup refused: " + validationError);
                return;
            }

            Directory.Delete(validatedPath, recursive: true);
        }
        catch (Exception ex)
        {
            // A staging artifact never blocks the final product id, and no
            // unverified/pre-existing destination is ever selected here.
            AppLog.Warn("GOG install cleanup failed: " + ex.Message);
        }
    }

    public async Task<InstallResult> UpdateAsync(GameEntry game, IProgress<InstallProgress>? progress, CancellationToken ct = default)
    {
        var gogdl = _gogdlPathOverride ?? ResolveGogdl() ?? await EnsureGogdlAsync(ct).ConfigureAwait(false);
        if (gogdl is null)
            return new InstallResult { Ok = false, Message = "gogdl required for update." };

        var gameId = ExtractGogId(game);
        var path = game.Path ?? Path.Combine(PathHelper.AppDataDir, "GOG", gameId ?? "unknown");
        if (string.IsNullOrWhiteSpace(gameId))
            return new InstallResult { Ok = false, Message = "Missing GOG product id." };

        Report(game.Id, progress, InstallPhase.Preparing, 0, "Repairing / updating via gogdl…");
        try
        {
            var (code, _, err) = await _commandRunner(
                gogdl,
                GogdlCli.WithAuthConfig(
                    GogAuthService.EffectiveAuthConfigPath,
                    GogdlCli.RepairArgs(gameId, path)),
                null,
                line =>
                {
                    var p = GogdlCli.ToProgress(game.Id, line);
                    _progress[game.Id] = p;
                    progress?.Report(p);
                },
                ct).ConfigureAwait(false);

            if (code != 0)
            {
                Report(game.Id, progress, InstallPhase.Failed, null, err.Trim());
                return new InstallResult { Ok = false, Message = err.Trim().Length > 0 ? err.Trim() : $"gogdl exited {code}." };
            }

            Report(game.Id, progress, InstallPhase.Completed, 100, "Up to date.");
            return new InstallResult { Ok = true, Message = "Updated via gogdl.", Path = path };
        }
        catch (OperationCanceledException)
        {
            Report(game.Id, progress, InstallPhase.Cancelled, null, "Cancelled.");
            return new InstallResult { Ok = false, Message = "Cancelled." };
        }
        catch (Exception ex)
        {
            Report(game.Id, progress, InstallPhase.Failed, null, ex.Message);
            return new InstallResult { Ok = false, Message = ex.Message };
        }
    }

    public async Task<LaunchResult> LaunchAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var gogdl = _gogdlPathOverride ?? ResolveGogdl();
        var gameId = ExtractGogId(game);
        var directWasAttempted = false;
        string? directFailure = null;
        var ignoredGogProcessNames = new[] { "GalaxyClient", "gogdl", "crashhandler" };
        var installRoot = !string.IsNullOrWhiteSpace(game.Path) && Directory.Exists(game.Path)
            ? game.Path
            : !string.IsNullOrWhiteSpace(game.LaunchTarget) && File.Exists(game.LaunchTarget)
                ? Path.GetDirectoryName(game.LaunchTarget)
                : null;

        var alreadyRunning = ProcessHelper.SnapshotLiveProcessIdsUnderPath(installRoot, ignoredGogProcessNames);
        if (ShouldReuseExistingGogProcess(alreadyRunning))
        {
            return new LaunchResult
            {
                Ok = true,
                Message = "Already running.",
                ProcessId = alreadyRunning.First(),
            };
        }

        // Prefer a local exe when we have one — avoid Galaxy GUI.
        if (!string.IsNullOrWhiteSpace(game.LaunchTarget) && File.Exists(game.LaunchTarget))
        {
            directWasAttempted = true;
            try
            {
                ct.ThrowIfCancellationRequested();
                var pidsBeforeLaunch = ProcessHelper.SnapshotLiveProcessIdsUnderPath(
                    installRoot,
                    ignoredGogProcessNames);
                using var proc = ProcessHelper.StartGame(
                    game.LaunchTarget, "", Path.GetDirectoryName(game.LaunchTarget));
                var confirmedPid = await ProcessHelper.ConfirmDirectLaunchAsync(
                        proc,
                        installRoot,
                        pidsBeforeLaunch,
                        ct,
                        ignoredGogProcessNames)
                    .ConfigureAwait(false);
                if (confirmedPid is not null)
                {
                    return new LaunchResult
                    {
                        Ok = true,
                        Message = "Started GOG title.",
                        ProcessId = confirmedPid,
                    };
                }

                directFailure = "GOG game exited immediately.";
            }
            catch (Exception ex)
            {
                if (ex is OperationCanceledException && ct.IsCancellationRequested) throw;
                directFailure = ex.Message;
            }

            if (!ShouldFallbackToGogdlAfterDirectExit(
                    directWasAttempted,
                    directIsAlive: false,
                    gogdlAvailable: gogdl is not null))
                return new LaunchResult { Ok = false, Message = directFailure };
        }

        if (gogdl is not null && gameId is not null && !string.IsNullOrWhiteSpace(game.Path))
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                // Snapshot immediately before gogdl dispatch. A pre-existing
                // game must have been handled above, not credited as this
                // launch request's handoff.
                var pidsBeforeLaunch = ProcessHelper.SnapshotLiveProcessIdsUnderPath(
                    game.Path,
                    ignoredGogProcessNames);
                using var helper = ProcessHelper.StartHidden(
                    gogdl,
                    GogdlCli.WithAuthConfig(
                        GogAuthService.EffectiveAuthConfigPath,
                        GogdlCli.LaunchArgs(gameId, game.Path)))
                    ?? throw new InvalidOperationException("gogdl did not start.");
                var gamePid = await ProcessHelper.WaitForProcessUnderPathAsync(
                        game.Path,
                        TimeSpan.FromSeconds(30),
                        ct,
                        ignoredGogProcessNames,
                        excludedProcessIds: pidsBeforeLaunch,
                        confirmationDelay: GogdlHandoffConfirmationDelay)
                    .ConfigureAwait(false);
                return new LaunchResult
                {
                    Ok = gamePid is not null,
                    Message = gamePid is not null
                        ? "GOG game started."
                        : "gogdl launch did not produce a game process.",
                    ProcessId = gamePid,
                    BackendStarted = "gogdl",
                };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new LaunchResult { Ok = false, Message = ex.Message };
            }
        }

        if (directWasAttempted)
            return new LaunchResult { Ok = false, Message = directFailure ?? "GOG game exited immediately." };

        return new LaunchResult
        {
            Ok = false,
            Message = "No launch path. Install via gogdl, or ensure the game exe is on disk.",
        };
    }

    internal static bool ShouldFallbackToGogdlAfterDirectExit(
        bool directWasAttempted,
        bool directIsAlive,
        bool gogdlAvailable) =>
        directWasAttempted && !directIsAlive && gogdlAvailable;

    internal static bool ShouldReuseExistingGogProcess(IReadOnlyCollection<int> processIds) =>
        processIds.Count > 0;

    internal static TimeSpan GogdlHandoffConfirmationDelay => TimeSpan.FromMilliseconds(700);

    public Task<InstallResult> UninstallAsync(GameEntry game, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_installedCatalog.UninstallRegistered(game));
    }

    public InstallProgress GetDownloadProgress(string gameId) =>
        _progress.TryGetValue(gameId, out var p) ? p : new InstallProgress { GameId = gameId, Phase = InstallPhase.Idle };

    public Task CleanupAfterExitAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default)
    {
        if (options.CloseStoreUiAfterExit)
            StoreWindowHider.CollapseOrphanSurfaces(StoreWindowHider.GalaxyProcessNames);
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

    private static string? ExtractGogId(GameEntry game)
    {
        if (game.Id.StartsWith("gog:", StringComparison.OrdinalIgnoreCase))
            return game.Id["gog:".Length..];
        return game.LaunchTarget;
    }

    internal static string? ResolveGogdl()
    {
        var managedCache = Path.Combine(PathHelper.AppDataDir, "tools", "gogdl.exe");
        var packagedTool = Path.Combine(PathHelper.AppDirectory, "tools", "gogdl.exe");

        foreach (var candidate in new[]
                 {
                     CliRunner.ResolveOnPath("gogdl.exe"),
                     CliRunner.ResolveOnPath("gogdl"),
                 })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            if (IsSamePath(candidate, managedCache) || IsSamePath(candidate, packagedTool))
            {
                if (VerifiedGitHubReleaseDownloader.IsPinnedAssetFile(
                        GogdlReleaseAsset,
                        candidate,
                        IsValidAmd64Pe))
                    return candidate;
                continue;
            }
            if (IsValidAmd64Pe(candidate)) return candidate;
        }

        foreach (var managed in new[] { managedCache, packagedTool })
            if (VerifiedGitHubReleaseDownloader.IsPinnedAssetFile(
                    GogdlReleaseAsset,
                    managed,
                    IsValidAmd64Pe))
                return managed;
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

    private static string? ResolveGalaxy() =>
        CliRunner.FirstExisting(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "GOG Galaxy", "GalaxyClient.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "GOG Galaxy", "GalaxyClient.exe"));
}
