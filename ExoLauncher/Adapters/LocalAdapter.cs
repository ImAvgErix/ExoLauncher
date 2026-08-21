using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using ExoLauncher.Helpers;
using ExoLauncher.Models;
using ExoLauncher.Services;

namespace ExoLauncher.Adapters;

/// <summary>DRM-free / direct exe. Full install + launch with zero other client.</summary>
public sealed class LocalAdapter : IStoreAdapter
{
    public const string AddPortableId = "local:add";
    internal const string ManagedCopyStagingPrefix = ".exo-portable-partial-";

    private readonly SettingsService? _settings;
    private readonly InstalledGameCatalog _installedCatalog;
    private readonly bool? _copyPortableIntoLibrary;
    private readonly string? _managedLibraryRoot;
    private readonly ConcurrentDictionary<string, InstallProgress> _progress = new(StringComparer.OrdinalIgnoreCase);

    public LocalAdapter(SettingsService? settings = null)
        : this(settings, InstalledGameCatalog.Default, copyPortableIntoLibrary: null)
    {
    }

    internal LocalAdapter(
        SettingsService? settings,
        InstalledGameCatalog installedCatalog,
        bool? copyPortableIntoLibrary,
        string? managedLibraryRoot = null)
    {
        _settings = settings;
        _installedCatalog = installedCatalog ?? throw new ArgumentNullException(nameof(installedCatalog));
        _copyPortableIntoLibrary = copyPortableIntoLibrary;
        _managedLibraryRoot = managedLibraryRoot;
    }

    public StoreKind Store => StoreKind.Local;
    public string Id => "local";
    public string DisplayName => "Local";

    public bool IsAgentPresent() => true;

    public Task<AuthResult> AuthenticateAsync(CancellationToken ct = default) =>
        Task.FromResult(new AuthResult { Ok = true, Message = "Local store needs no account." });

    public static GameEntry CreateAddPortableEntry() => new()
    {
        Id = AddPortableId,
        Title = "Add portable game",
        Store = StoreKind.Local,
        Installed = false,
        Owned = true,
        CanInstall = true,
        Status = "Ready",
        Deps = Array.Empty<string>(),
        LaunchNote = "Pick a folder that contains the game executable. Registers in place (or copies into Exo library if enabled in Settings).",
    };

    public Task<IReadOnlyList<GameEntry>> GetLibraryAsync(CancellationToken ct = default)
    {
        var games = new List<GameEntry> { CreateAddPortableEntry() };
        var knownPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var registered in _installedCatalog.GetInstalledGames(StoreKind.Local))
        {
            ct.ThrowIfCancellationRequested();
            games.Add(registered);
            if (!string.IsNullOrWhiteSpace(registered.Path))
                knownPaths.Add(NormalizePath(registered.Path));
        }

        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Games"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Games"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Games"),
            Path.Combine(PathHelper.AppDataDir, "Games"),
            Path.Combine(PathHelper.AppDataDir, "library"),
            _managedLibraryRoot,
            _settings?.Current.DefaultInstallRoot,
        };

        foreach (var root in roots
                     .Where(r => !string.IsNullOrWhiteSpace(r))
                     .Select(r => r!)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(root))
                {
                    var folderName = Path.GetFileName(dir);
                    if (folderName.StartsWith("exo-launcher-test", StringComparison.OrdinalIgnoreCase))
                        continue;
                    // A cancelled/failed copy can survive only when Windows is
                    // temporarily holding a file open. Never surface that
                    // incomplete staging directory as an installed game.
                    if (IsManagedCopyStagingDirectory(folderName))
                        continue;
                    // GOG uses a nested managed root under the shared Games base.
                    // It has its own durable catalog and must never be mistaken
                    // for one large portable game by the one-level local scan.
                    if (folderName.Equals("GOG", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (knownPaths.Contains(NormalizePath(dir)))
                        continue;

                    var exe = FindPlayableExe(dir);
                    if (exe is null) continue;
                    var id = StableLocalId(dir, folderName);
                    if (string.Equals(id, AddPortableId, StringComparison.OrdinalIgnoreCase))
                        continue;
                    games.Add(new GameEntry
                    {
                        Id = id,
                        Title = folderName,
                        Store = StoreKind.Local,
                        Installed = true,
                        Owned = true,
                        CanInstall = false,
                        Path = dir,
                        LaunchTarget = exe,
                        Status = "Ready",
                        SizeBytes = TryDirSize(dir),
                        Deps = Array.Empty<string>(),
                        LaunchNote = "Launches the executable directly. No store client.",
                    });
                    knownPaths.Add(NormalizePath(dir));
                }
            }
            catch (Exception ex)
            {
                AppLog.Debug($"Local scan skip {root}: {ex.Message}");
            }
        }

        return Task.FromResult<IReadOnlyList<GameEntry>>(games);
    }

    public async Task<InstallResult> InstallAsync(
        GameEntry game,
        string? installPath,
        IProgress<InstallProgress>? progress,
        CancellationToken ct = default)
    {
        var path = installPath ?? game.Path;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return new InstallResult
            {
                Ok = false,
                Message = "Choose an existing folder that contains the game executable (portable / DRM-free).",
            };
        }

        // Refuse reparse points that jump outside the chosen path
        try
        {
            var full = Path.GetFullPath(path);
            if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            {
                return new InstallResult
                {
                    Ok = false,
                    Message = "Refusing to register a reparse-point folder. Pick a normal directory.",
                };
            }
            if (!IsSafePortableRegistrationRoot(full))
            {
                return new InstallResult
                {
                    Ok = false,
                    Message = "Pick a dedicated game folder. Windows, drive roots, and other broad system folders cannot be registered.",
                };
            }
            path = full;
        }
        catch (Exception ex)
        {
            return new InstallResult { Ok = false, Message = ex.Message };
        }

        Report(game.Id, progress, InstallPhase.Preparing, 10, "Scanning folder…");
        await Task.Delay(40, ct).ConfigureAwait(false);

        var exe = FindPlayableExe(path);
        if (exe is null)
        {
            Report(game.Id, progress, InstallPhase.Failed, null, "No playable exe found.");
            return new InstallResult { Ok = false, Message = "No playable .exe found in that folder (top level or one level deep)." };
        }

        var displayTitle = Path.GetFileName(path.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar));
        var copy = _copyPortableIntoLibrary ?? _settings?.Current.CopyPortableIntoLibrary == true;
        var copied = false;
        string? managedRoot = null;
        if (copy)
        {
            var libraryRoot = Path.GetFullPath(_managedLibraryRoot ?? PathHelper.GamesRoot);
            Directory.CreateDirectory(libraryRoot);
            var dest = Path.Combine(libraryRoot, ManagedPortableFolderName(path, displayTitle));
            if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
            {
                if (Path.Exists(dest))
                {
                    Report(game.Id, progress, InstallPhase.Failed, null, "Managed destination already exists.");
                    return new InstallResult
                    {
                        Ok = false,
                        Message = "A managed portable copy already exists at the destination. Remove it before retrying.",
                    };
                }

                Report(game.Id, progress, InstallPhase.Installing, 40, "Copying into Exo library…");
                var stagingPath = CreateManagedCopyStagingPath(libraryRoot, Path.GetFileName(dest));
                try
                {
                    var relativeExe = Path.GetRelativePath(path, exe);
                    CopyDirectory(path, stagingPath, ct, (pct, msg) =>
                        Report(game.Id, progress, InstallPhase.Installing, 40 + pct * 0.5, msg));

                    var stagedExe = Path.Combine(stagingPath, relativeExe);
                    if (!File.Exists(stagedExe) || FindPlayableExe(stagingPath) is null)
                        throw new IOException("The staged copy did not contain a playable executable.");
                    if (!RecursiveDeleteGuard.TryValidateManagedChild(
                            libraryRoot,
                            stagingPath,
                            out _,
                            out var stagingError))
                    {
                        throw new IOException("The staged copy could not be validated: " + stagingError);
                    }

                    ct.ThrowIfCancellationRequested();
                    if (Path.Exists(dest))
                        throw new IOException("The managed destination appeared while the copy was in progress.");

                    // Staging is a sibling under the same managed root, so this
                    // rename is an atomic promotion on the destination volume.
                    Directory.Move(stagingPath, dest);
                    path = dest;
                    exe = Path.Combine(dest, relativeExe);
                    copied = true;
                    managedRoot = libraryRoot;
                }
                catch (OperationCanceledException)
                {
                    TryDeleteManagedDirectory(libraryRoot, stagingPath, requireStagingName: true);
                    Report(game.Id, progress, InstallPhase.Cancelled, null, "Cancelled.");
                    return new InstallResult { Ok = false, Message = "Cancelled." };
                }
                catch (Exception ex)
                {
                    TryDeleteManagedDirectory(libraryRoot, stagingPath, requireStagingName: true);
                    Report(game.Id, progress, InstallPhase.Installing, 80, "Using original folder (copy skipped: " + ex.Message + ")");
                }
            }
        }

        var registeredGame = new GameEntry
        {
            Id = StableLocalId(path, displayTitle),
            Title = displayTitle,
            Store = StoreKind.Local,
            Installed = true,
            Owned = true,
            CanInstall = false,
            Path = path,
            LaunchTarget = exe,
        };
        try
        {
            _installedCatalog.Register(registeredGame, exe, copied, managedRoot);
        }
        catch (Exception ex)
        {
            if (copied && managedRoot is not null)
                TryDeleteManagedDirectory(managedRoot, path, requireStagingName: false);
            Report(game.Id, progress, InstallPhase.Failed, null, "Could not save portable registration.");
            return new InstallResult
            {
                Ok = false,
                Message = "Exo could not save the portable registration: " + ex.Message,
            };
        }

        Report(game.Id, progress, InstallPhase.Completed, 100, copied ? "Copied and registered." : "Registered in place.");
        return new InstallResult
        {
            Ok = true,
            Message = copied ? "Copied into Exo library." : "Registered local game (in place).",
            Path = path,
        };
    }

    public Task<InstallResult> UpdateAsync(GameEntry game, IProgress<InstallProgress>? progress, CancellationToken ct = default) =>
        Task.FromResult(new InstallResult
        {
            Ok = false,
            Message = "Local and DRM-free titles update by replacing files in the install folder. No store updater.",
        });

    public async Task<LaunchResult> LaunchAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var target = game.LaunchTarget ?? game.Path;
        if (string.IsNullOrWhiteSpace(target) || !File.Exists(target))
            return new LaunchResult { Ok = false, Message = "Executable not found." };

        try
        {
            ct.ThrowIfCancellationRequested();
            var installRoot = !string.IsNullOrWhiteSpace(game.Path) && Directory.Exists(game.Path)
                ? game.Path
                : Path.GetDirectoryName(target);
            var pidsBeforeLaunch = ProcessHelper.SnapshotLiveProcessIdsUnderPath(
                installRoot,
                ["crashhandler", "unins000", "setup"]);
            var working = !string.IsNullOrWhiteSpace(options.WorkingDirectory) &&
                          Directory.Exists(options.WorkingDirectory)
                ? options.WorkingDirectory
                : Path.GetDirectoryName(target) ?? string.Empty;
            var psi = new ProcessStartInfo
            {
                FileName = target,
                Arguments = options.ExtraArgs ?? string.Empty,
                WorkingDirectory = working,
                UseShellExecute = true,
            };
            if (options.RunAsAdmin)
                psi.Verb = "runas";
            using var proc = Process.Start(psi);
            var confirmedPid = await ProcessHelper.ConfirmDirectLaunchAsync(
                    proc,
                    installRoot,
                    pidsBeforeLaunch,
                    ct,
                    ["crashhandler", "unins000", "setup"])
                .ConfigureAwait(false);
            return new LaunchResult
            {
                Ok = confirmedPid is not null,
                Message = confirmedPid is not null ? "Started." : "Process did not stay running.",
                ProcessId = confirmedPid,
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

    public Task<InstallResult> UninstallAsync(GameEntry game, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_installedCatalog.UninstallRegistered(game));
    }

    public InstallProgress GetDownloadProgress(string gameId) =>
        _progress.TryGetValue(gameId, out var p) ? p : new InstallProgress { GameId = gameId, Phase = InstallPhase.Idle };

    public Task CleanupAfterExitAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default) =>
        Task.CompletedTask;

    private void Report(string gameId, IProgress<InstallProgress>? progress, InstallPhase phase, double? pct, string status)
    {
        var p = new InstallProgress
        {
            GameId = gameId,
            Phase = phase,
            Percent = pct,
            Status = status,
            CanCancel = phase is InstallPhase.Preparing or InstallPhase.Downloading or InstallPhase.Installing,
        };
        _progress[gameId] = p;
        progress?.Report(p);
    }

    internal static bool IsSafePortableRegistrationRoot(string path)
    {
        try
        {
            var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            var volumeRoot = Path.TrimEndingDirectorySeparator(Path.GetPathRoot(candidate) ?? string.Empty);
            if (string.Equals(candidate, volumeRoot, StringComparison.OrdinalIgnoreCase))
                return false;

            var windowsRoot = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
            if (string.Equals(candidate, windowsRoot, StringComparison.OrdinalIgnoreCase) ||
                ProcessHelper.IsPathUnderRoot(candidate, windowsRoot))
                return false;

            // Dedicated game folders beneath these locations are fine. The
            // broad roots themselves are not: automatic executable selection
            // there could register an unrelated application.
            var broadRoots = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            };
            return broadRoots
                .Where(root => !string.IsNullOrWhiteSpace(root))
                .Select(root => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)))
                .All(root => !string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    private static string? FindPlayableExe(string dir)
    {
        try
        {
            var top = Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly)
                .Where(f => !IsInstallerLike(f))
                .OrderByDescending(ScoreExe)
                .FirstOrDefault();
            if (top is not null) return top;

            // One level deep (common for portable packs)
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                var hit = Directory.EnumerateFiles(sub, "*.exe", SearchOption.TopDirectoryOnly)
                    .Where(f => !IsInstallerLike(f))
                    .OrderByDescending(ScoreExe)
                    .FirstOrDefault();
                if (hit is not null) return hit;
            }
        }
        catch { /* */ }
        return null;
    }

    private static int ScoreExe(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        var score = 10;
        if (name is "game" or "start" or "play" or "launcher") score += 5;
        if (name.Contains("unitycrash") || name.Contains("crashhandler") || name.Contains("redist")) score -= 20;
        try { score += (int)Math.Min(20, new FileInfo(path).Length / (512 * 1024)); } catch { /* */ }
        return score;
    }

    private static string StableLocalId(string dir, string folderName)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dir.ToLowerInvariant())))[..10].ToLowerInvariant();
        var slug = new string(folderName.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
        while (slug.Contains("--", StringComparison.Ordinal)) slug = slug.Replace("--", "-", StringComparison.Ordinal);
        slug = slug.Trim('-');
        if (string.IsNullOrWhiteSpace(slug)) slug = "game";
        return "local:" + slug + "-" + hash;
    }

    internal static string ManagedPortableFolderName(string sourcePath, string displayTitle) =>
        StableLocalId(sourcePath, displayTitle)["local:".Length..];

    internal static bool IsManagedCopyStagingDirectory(string folderName) =>
        !string.IsNullOrWhiteSpace(folderName)
        && folderName.StartsWith(ManagedCopyStagingPrefix, StringComparison.OrdinalIgnoreCase);

    private static string CreateManagedCopyStagingPath(string libraryRoot, string destinationName)
    {
        while (true)
        {
            var candidate = Path.Combine(
                libraryRoot,
                $"{ManagedCopyStagingPrefix}{destinationName}-{Guid.NewGuid():N}");
            if (!Path.Exists(candidate)) return candidate;
        }
    }

    private static void TryDeleteManagedDirectory(
        string libraryRoot,
        string candidatePath,
        bool requireStagingName)
    {
        if (!Directory.Exists(candidatePath)) return;

        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(libraryRoot));
            var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidatePath));
            if (!string.Equals(Path.GetDirectoryName(candidate), root, StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Warn("Portable-copy cleanup refused a non-sibling path.");
                return;
            }
            if (requireStagingName && !IsManagedCopyStagingDirectory(Path.GetFileName(candidate)))
            {
                AppLog.Warn("Portable-copy cleanup refused a non-staging path.");
                return;
            }
            if (!RecursiveDeleteGuard.TryValidateManagedChild(
                    root,
                    candidate,
                    out var validatedPath,
                    out var validationError))
            {
                AppLog.Warn("Portable-copy cleanup refused: " + validationError);
                return;
            }

            Directory.Delete(validatedPath, recursive: true);
        }
        catch (Exception ex)
        {
            // A locked artifact is harmless to discovery (staging names are
            // ignored) and can be cleaned on a later maintenance pass.
            AppLog.Warn("Portable-copy cleanup failed: " + ex.Message);
        }
    }

    private static string NormalizePath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool IsInstallerLike(string path)
    {
        var name = Path.GetFileName(path).ToLowerInvariant();
        return name.Contains("uninstall") || name.Contains("setup") || name.Contains("install")
            || name.Contains("redist") || name.Contains("vcredist") || name.Contains("dxsetup")
            || name.Contains("crashhandler") || name.Contains("unitycrash");
    }

    private static long? TryDirSize(string dir)
    {
        try
        {
            long total = 0;
            var n = 0;
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(f).Length; } catch { }
                if (++n > 12000) break;
            }
            return total;
        }
        catch { return null; }
    }

    private static void CopyDirectory(string source, string dest, CancellationToken ct, Action<double, string> onProgress)
    {
        var files = Directory.GetFiles(source, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
        });
        Directory.CreateDirectory(dest);
        for (var i = 0; i < files.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(source, files[i]);
            var target = Path.Combine(dest, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(files[i], target, overwrite: true);
            onProgress((i + 1) * 100.0 / Math.Max(1, files.Length), $"Copying {rel}");
        }
    }
}
