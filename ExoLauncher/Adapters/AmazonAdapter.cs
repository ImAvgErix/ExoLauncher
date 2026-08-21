using System.Collections.Concurrent;
using ExoLauncher.Adapters.Cli;
using ExoLauncher.Helpers;
using ExoLauncher.Models;
using ExoLauncher.Services;

namespace ExoLauncher.Adapters;

/// <summary>
/// Amazon Games via Nile when present; otherwise proven fuel.json installs.
/// https://github.com/imLinguin/nile
/// </summary>
public sealed class AmazonAdapter : IStoreAdapter, IOfficialStoreClient, IStoreRepair
{
    private static readonly OfficialClientDefinition Definition = new(
        ExecutableNames: ["Amazon Games.exe", "AmazonGames.exe"],
        DefaultPaths:
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Amazon Games", "App", "Amazon Games.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Amazon Games", "Amazon Games.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Amazon Games", "Amazon Games.exe"),
        ],
        UninstallDisplayNames: ["Amazon Games", "Amazon Games App"]);

    private const ushort PeMachineAmd64 = 0x8664;
    private static readonly GitHubReleaseAsset NileReleaseAsset = new(
        "imLinguin",
        "nile",
        "v1.2.0",
        "nile_windows_x86_64.exe",
        ExpectedSize: 12_386_046,
        ExpectedSha256: "6531790c59f78cea4a8743bf0582d5afda7fb887f5c143391d7339ad0f42ab88");

    private static readonly TimeSpan NileAuthTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan NileJobTimeout = TimeSpan.FromHours(2);

    private readonly ConcurrentDictionary<string, InstallProgress> _progress = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _nilePathOverride;

    public AmazonAdapter() : this(null)
    {
    }

    internal AmazonAdapter(string? nilePathOverride) => _nilePathOverride = nilePathOverride;

    public StoreKind Store => StoreKind.Amazon;
    public string Id => "amazon";
    public string DisplayName => "Amazon Games";
    public IReadOnlyList<string> ClientProcessNames => StoreWindowHider.AmazonClientProcessNames;

    public bool IsAgentPresent() => ResolveNile() is not null || IsClientPresent();
    public bool IsClientPresent() => GetClientLaunchCommand() is not null;

    public StoreClientLaunchCommand? GetClientLaunchCommand() =>
        OfficialClientLocator.Resolve(Definition);

    internal static bool IsNilePresent() => ResolveNile() is not null;

    public async Task<AuthResult> AuthenticateAsync(CancellationToken ct = default)
    {
        try
        {
            var nile = ResolveNile() ?? await EnsureNileAsync(ct).ConfigureAwait(false);
            if (nile is null)
            {
                return new AuthResult
                {
                    Ok = false,
                    RequiresUserAction = true,
                    Message = IsClientPresent()
                        ? "Nile is required for hidden Amazon actions. Install it from the official source, then Refresh."
                        : "Amazon Games / Nile not found. Install Amazon Games or Nile, then try Sign in again.",
                };
            }

            if (await HasValidNileSessionAsync(nile, ct).ConfigureAwait(false))
            {
                return new AuthResult
                {
                    Ok = true,
                    RequiresUserAction = false,
                    Message = "Amazon account is already connected through Nile.",
                };
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(NileAuthTimeout);
            (int ExitCode, string StdOut, string StdErr) auth;
            try
            {
                auth = await CliRunner.RunAsync(
                        nile, NileCli.AuthLoginArgs(), null, null, timeout.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new AuthResult
                {
                    Ok = false,
                    RequiresUserAction = true,
                    Message = "Amazon sign-in timed out before it completed. Try again.",
                };
            }

            if (auth.ExitCode != 0)
            {
                return new AuthResult
                {
                    Ok = false,
                    RequiresUserAction = true,
                    Message = $"Amazon sign-in did not complete (Nile exited {auth.ExitCode}).",
                };
            }

            try
            {
                await CliRunner.RunAsync(nile, NileCli.LibrarySyncArgs(), null, null, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLog.Debug("Nile library sync after auth failed: " + ex.Message);
            }

            if (!await HasValidNileSessionAsync(nile, ct).ConfigureAwait(false) &&
                !NileCli.HasLocalSession())
            {
                return new AuthResult
                {
                    Ok = false,
                    RequiresUserAction = true,
                    Message = "Nile sign-in finished, but the Amazon session could not be verified.",
                };
            }

            return new AuthResult
            {
                Ok = true,
                RequiresUserAction = false,
                Message = "Amazon account connected through Nile.",
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new AuthResult
            {
                Ok = false,
                RequiresUserAction = true,
                Message = ex.Message,
            };
        }
    }

    public Task<IReadOnlyList<GameEntry>> GetLibraryAsync(CancellationToken ct = default)
    {
        var nilePresent = ResolveNile() is not null;
        var session = NileCli.HasLocalSession();
        var fuel = OfficialInstalledLibraries.ScanAmazon();
        var nileRows = session
            ? NileCli.ReadCachedLibrary(NileCli.ConfigRoots(), File.Exists, TryReadText)
            : Array.Empty<NileCli.GameRow>();

        var games = new List<GameEntry>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in nileRows)
        {
            ct.ThrowIfCancellationRequested();
            var id = "amazon:" + SanitizeId(row.ProductId);
            if (!seenIds.Add(id)) continue;
            if (!string.IsNullOrWhiteSpace(row.InstallPath))
                seenPaths.Add(Path.GetFullPath(row.InstallPath));
            var installed = row.Installed &&
                            !string.IsNullOrWhiteSpace(row.InstallPath) &&
                            Directory.Exists(row.InstallPath);
            games.Add(new GameEntry
            {
                Id = id,
                Title = row.Title,
                Store = StoreKind.Amazon,
                Installed = installed,
                Owned = true,
                CanInstall = !installed && nilePresent && session,
                Path = installed ? row.InstallPath : null,
                LaunchTarget = row.ProductId,
                SizeBytes = row.SizeBytes,
                Status = installed ? "Ready" : "Not installed",
                Deps = nilePresent ? ["nile"] : ["Amazon Games"],
                LaunchNote = installed
                    ? "Launches the installed Amazon Games build. Install/update via Nile when available."
                    : "Owned on Amazon. Install via Nile when available.",
            });
        }

        foreach (var game in fuel)
        {
            ct.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(game.Path) &&
                seenPaths.Contains(Path.GetFullPath(game.Path)))
                continue;
            if (!seenIds.Add(game.Id)) continue;
            games.Add(game);
        }

        return Task.FromResult<IReadOnlyList<GameEntry>>(games);
    }

    public async Task<InstallResult> InstallAsync(
        GameEntry game,
        string? installPath,
        IProgress<InstallProgress>? progress,
        CancellationToken ct = default)
    {
        var productId = NileProductId(game);
        var nile = ResolveNile() ?? await EnsureNileAsync(ct).ConfigureAwait(false);
        if (nile is null || string.IsNullOrWhiteSpace(productId) || LooksLikePath(productId))
            return await OfficialInstalledLibraries.InstallAsync(game, DisplayName, ct).ConfigureAwait(false);

        return await RunNileJobAsync(
                nile,
                game,
                NileCli.InstallArgs(productId, installPath ?? PathHelper.GamesRoot),
                progress,
                "Installing via Nile.",
                ct)
            .ConfigureAwait(false);
    }

    public async Task<InstallResult> UpdateAsync(
        GameEntry game,
        IProgress<InstallProgress>? progress,
        CancellationToken ct = default)
    {
        var productId = NileProductId(game);
        var nile = ResolveNile();
        if (nile is null || string.IsNullOrWhiteSpace(productId) || LooksLikePath(productId))
            return await OfficialInstalledLibraries.UpdateAsync(game, DisplayName, ct).ConfigureAwait(false);

        return await RunNileJobAsync(
                nile,
                game,
                NileCli.UpdateArgs(productId),
                progress,
                "Updated via Nile.",
                ct)
            .ConfigureAwait(false);
    }

    public Task<LaunchResult> LaunchAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default)
    {
        var nile = ResolveNile();
        var productId = NileProductId(game);
        if (nile is not null && !string.IsNullOrWhiteSpace(productId) && !LooksLikePath(productId))
        {
            try
            {
                var start = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = nile,
                    ArgumentList = { "launch", productId },
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(nile) ?? "",
                });
                return Task.FromResult(new LaunchResult
                {
                    Ok = true,
                    Message = "Launching through Nile.",
                    ProcessId = start?.Id,
                });
            }
            catch (Exception ex)
            {
                AppLog.Debug("Nile launch failed: " + ex.Message);
            }
        }

        return Task.FromResult(OfficialInstalledLibraries.LaunchAmazon(game));
    }

    public async Task<InstallResult> UninstallAsync(GameEntry game, CancellationToken ct = default)
    {
        var productId = NileProductId(game);
        var nile = ResolveNile();
        if (nile is not null && !string.IsNullOrWhiteSpace(productId) && !LooksLikePath(productId))
        {
            try
            {
                var result = await CliRunner.RunAsync(
                        nile, NileCli.UninstallArgs(productId), null, null, ct)
                    .ConfigureAwait(false);
                if (result.ExitCode == 0)
                    return new InstallResult { Ok = true, Message = "Removed through Nile." };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLog.Debug("Nile uninstall failed: " + ex.Message);
            }
        }

        return await OfficialInstalledLibraries.UninstallAsync(game, DisplayName, ct).ConfigureAwait(false);
    }

    public bool CanRepair(GameEntry game) =>
        game.Installed &&
        ResolveNile() is not null &&
        !string.IsNullOrWhiteSpace(NileProductId(game)) &&
        !LooksLikePath(NileProductId(game)!);

    public Task<InstallResult> RepairAsync(
        GameEntry game,
        IProgress<InstallProgress>? progress,
        CancellationToken ct = default)
    {
        var nile = ResolveNile();
        var productId = NileProductId(game);
        if (nile is null || string.IsNullOrWhiteSpace(productId))
            return Task.FromResult(new InstallResult { Ok = false, Message = "Nile cannot verify this title." });

        return RunNileJobAsync(
            nile,
            game,
            NileCli.VerifyArgs(productId),
            progress,
            "Verified via Nile.",
            ct);
    }

    public InstallProgress GetDownloadProgress(string gameId) =>
        _progress.TryGetValue(gameId, out var progress)
            ? progress
            : new InstallProgress { GameId = gameId, Phase = InstallPhase.Idle };

    public Task CleanupAfterExitAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default) =>
        Task.CompletedTask;

    private async Task<InstallResult> RunNileJobAsync(
        string nile,
        GameEntry game,
        IReadOnlyList<string> args,
        IProgress<InstallProgress>? progress,
        string successMessage,
        CancellationToken ct)
    {
        Report(game.Id, progress, InstallPhase.Preparing, 5, "Starting Nile…");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(NileJobTimeout);
        try
        {
            var (code, _, err) = await CliRunner.RunAsync(
                    nile,
                    args,
                    null,
                    line =>
                    {
                        var sample = NileCli.ToProgress(game.Id, line);
                        _progress[game.Id] = sample;
                        progress?.Report(sample);
                    },
                    timeout.Token)
                .ConfigureAwait(false);
            if (code != 0)
            {
                var message = string.IsNullOrWhiteSpace(err) ? $"Nile exited {code}." : err.Trim();
                Report(game.Id, progress, InstallPhase.Failed, null, message);
                return new InstallResult { Ok = false, Message = message };
            }

            Report(game.Id, progress, InstallPhase.Completed, 100, successMessage);
            return new InstallResult { Ok = true, Message = successMessage, Path = game.Path };
        }
        catch (OperationCanceledException)
        {
            Report(game.Id, progress, InstallPhase.Cancelled, null, "Cancelled.");
            return new InstallResult { Ok = false, Message = "Cancelled." };
        }
    }

    private void Report(
        string gameId,
        IProgress<InstallProgress>? progress,
        InstallPhase phase,
        double? percent,
        string status)
    {
        var sample = new InstallProgress
        {
            GameId = gameId,
            Phase = phase,
            Percent = percent,
            Status = status,
            CanCancel = phase is InstallPhase.Installing or InstallPhase.Preparing,
        };
        _progress[gameId] = sample;
        progress?.Report(sample);
    }

    private static async Task<bool> HasValidNileSessionAsync(string nile, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            var result = await CliRunner.RunAsync(
                    nile, NileCli.AuthStatusArgs(), null, null, timeout.Token)
                .ConfigureAwait(false);
            return NileCli.IsAuthenticatedStatusResponse(result.ExitCode, result.StdOut);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return NileCli.HasLocalSession();
        }
        catch
        {
            return NileCli.HasLocalSession();
        }
    }

    internal static string? ResolveNile()
    {
        var managedCache = Path.Combine(PathHelper.AppDataDir, "tools", "nile.exe");
        var packagedTool = Path.Combine(PathHelper.AppDirectory, "tools", "nile.exe");

        foreach (var candidate in new[]
                 {
                     CliRunner.ResolveOnPath("nile.exe"),
                     CliRunner.ResolveOnPath("nile"),
                 })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            if (IsSamePath(candidate, managedCache) || IsSamePath(candidate, packagedTool))
            {
                if (PinnedToolCache.IsPinnedAsset(NileReleaseAsset, candidate, IsValidAmd64Pe))
                    return candidate;
                continue;
            }
            if (IsValidAmd64Pe(candidate)) return candidate;
        }

        foreach (var managed in new[] { managedCache, packagedTool })
        {
            if (PinnedToolCache.IsPinnedAsset(NileReleaseAsset, managed, IsValidAmd64Pe))
                return managed;
        }

        foreach (var external in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "nile", "nile.exe"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "heroic", "nile.exe"),
                 })
        {
            if (IsValidAmd64Pe(external)) return external;
        }

        return null;
    }

    private static async Task<string?> EnsureNileAsync(CancellationToken ct)
    {
        try
        {
            var tools = Path.Combine(PathHelper.AppDataDir, "tools");
            Directory.CreateDirectory(tools);
            var dest = Path.Combine(tools, "nile.exe");
            return await VerifiedGitHubReleaseDownloader.Shared.DownloadPinnedAsync(
                    NileReleaseAsset,
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
            AppLog.Warn("EnsureNile failed: " + ex.Message);
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

    private static string? NileProductId(GameEntry game)
    {
        if (!string.IsNullOrWhiteSpace(game.LaunchTarget) && !LooksLikePath(game.LaunchTarget))
            return game.LaunchTarget;
        if (game.Id.StartsWith("amazon:", StringComparison.OrdinalIgnoreCase))
        {
            var id = game.Id["amazon:".Length..];
            return LooksLikePath(id) ? null : id;
        }
        return null;
    }

    private static bool LooksLikePath(string value) =>
        value.IndexOfAny(['\\', '/', ':']) >= 0 && value.Contains(".exe", StringComparison.OrdinalIgnoreCase);

    private static string? TryReadText(string path)
    {
        try { return File.ReadAllText(path); }
        catch { return null; }
    }

    private static string SanitizeId(string value)
    {
        var chars = value.Trim().ToLowerInvariant().ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (!char.IsLetterOrDigit(chars[i]) && chars[i] is not '-' and not '_' and not '.')
                chars[i] = '-';
        }
        return new string(chars).Trim('-');
    }
}
