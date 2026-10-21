using System.Diagnostics;
using System.Text.Json;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Adapters;

internal enum SteamIpcStatus
{
    Ok,
    CommandFailed,
    Unavailable,

    /// <summary>
    /// The helper is not deployed next to Exo. Retrying cannot change that, so
    /// callers must fall straight through to the protocol request.
    /// </summary>
    HostMissing,
}

/// <summary>
/// Runs ExoLauncher.SteamIpc.exe against the live Steam client. Steam stays
/// the official backend; Exo does not copy Steam files into its own tree.
/// </summary>
internal static class SteamClientIpc
{
    internal const string HostExeName = "ExoLauncher.SteamIpc.exe";

    /// <summary>
    /// The helper takes the verb and the app id only. Steam owns its library
    /// folders — IClientAppManager::InstallApp selects one by index, never by
    /// path — so an install directory is not part of this contract.
    /// </summary>
    public static async Task<SteamIpcStatus> CommandAsync(string action, string appId, CancellationToken ct = default)
    {
        var exe = ResolveHost();
        if (exe is null)
        {
            AppLog.Info("Steam IPC host is not installed next to Exo.");
            return SteamIpcStatus.HostMissing;
        }

        try
        {
            var start = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exe),
            };
            start.ArgumentList.Add(action);
            start.ArgumentList.Add(appId);

            return await RunCommandAsync(start, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            AppLog.Info($"Steam IPC host spawn failed: {ex.GetType().Name}: {ex.Message}");
            return SteamIpcStatus.Unavailable;
        }
    }

    internal static async Task<SteamIpcStatus> RunCommandAsync(ProcessStartInfo start, CancellationToken ct,
        TimeSpan? timeout = null)
    {
        ct.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(20));
        using var process = Process.Start(start);
        if (process is null) return SteamIpcStatus.Unavailable;
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            return process.ExitCode switch { 0 => SteamIpcStatus.Ok, 1 => SteamIpcStatus.CommandFailed, _ => SteamIpcStatus.Unavailable };
        }
        catch (OperationCanceledException)
        {
            try { ProcessTermination.Kill(process, entireProcessTree: true); } catch { }
            using var reap = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try { await process.WaitForExitAsync(reap.Token).ConfigureAwait(false); } catch { }
            ct.ThrowIfCancellationRequested();
            AppLog.Info("Steam IPC host timed out.");
            return SteamIpcStatus.Unavailable;
        }
    }

    /// <summary>
    /// The owned ids among the candidates, and the candidates Steam did not
    /// answer for (a later batch timed out). Unchecked ids are unknown, not
    /// unowned.
    /// </summary>
    internal sealed record OwnedAnswer(IReadOnlySet<string> Owned, IReadOnlyList<string> Unchecked);

    // The helper answers about 20,000 ids in half a second; a batch keeps
    // one slow answer from costing the whole list.
    internal const int OwnedBatchSize = 10_000;

    /// <summary>
    /// Asks the running, signed-in Steam client (via the helper's <c>owned</c>
    /// action) which candidate app ids the active account holds a license for,
    /// every candidate, in batches. Returns null when the first batch has no
    /// answer (helper missing, Steam not running or signed out, timeout,
    /// malformed output) — never an empty set in place of "unknown".
    /// </summary>
    public static async Task<OwnedAnswer?> QueryOwnedAsync(
        IEnumerable<string> candidates,
        CancellationToken ct = default,
        TimeSpan? batchTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var ids = candidates
            .Where(IsQueryableAppId)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (ids.Count == 0) return null;
        var exe = ResolveHost();
        if (exe is null) return null;
        return await QueryOwnedInBatchesAsync(
            ids, batch => QueryOwnedBatchAsync(exe, batch, batchTimeout ?? TimeSpan.FromSeconds(8), ct)).ConfigureAwait(false);
    }

    /// <summary>
    /// Candidates come most-likely-owned first (installed, played, then the
    /// rest of appinfo), so the first batch decides whether Steam answered at
    /// all. After that a failed batch stops the walk and its ids and the rest
    /// come back unchecked; the answered batches stand.
    /// </summary>
    internal static async Task<OwnedAnswer?> QueryOwnedInBatchesAsync(
        List<string> ids,
        Func<List<string>, Task<IReadOnlySet<string>?>> queryBatch)
    {
        var owned = new HashSet<string>(StringComparer.Ordinal);
        for (var start = 0; start < ids.Count; start += OwnedBatchSize)
        {
            var batch = ids.GetRange(start, Math.Min(OwnedBatchSize, ids.Count - start));
            var answer = await queryBatch(batch).ConfigureAwait(false);
            if (answer is null)
            {
                if (start == 0) return null;
                AppLog.Info($"Steam IPC owned query stopped after {start} of {ids.Count} ids; the rest stay unchecked.");
                return new OwnedAnswer(owned, ids.GetRange(start, ids.Count - start));
            }
            owned.UnionWith(answer);
        }
        return new OwnedAnswer(owned, []);
    }

    private static async Task<IReadOnlySet<string>?> QueryOwnedBatchAsync(
        string exe,
        List<string> ids,
        TimeSpan timeout,
        CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        Process? process = null;
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                WorkingDirectory = Path.GetDirectoryName(exe),
            };
            start.ArgumentList.Add("owned");
            start.ArgumentList.Add("-");
            process = Process.Start(start);
            if (process is null) return null;
            // stdin, not argv: a full appinfo candidate list exceeds the
            // 32K command-line limit.
            await process.StandardInput.WriteAsync(string.Join(',', ids).AsMemory(), deadline.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            var output = await process.StandardOutput.ReadToEndAsync(deadline.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            return process.ExitCode == 0 ? ParseOwnedOutput(output, ids) : null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            AppLog.Info("Steam IPC owned query timed out.");
            return null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLog.Debug($"Steam IPC owned query failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
        finally
        {
            if (process is not null)
            {
                try { if (!process.HasExited) ProcessTermination.Kill(process, entireProcessTree: true); } catch { }
                process.Dispose();
            }
        }
    }

    /// <summary>
    /// Parses <c>{"owned":[..],"checked":N}</c>. Only ids that were asked about
    /// are accepted, and the helper must confirm it checked every candidate.
    /// </summary>
    internal static IReadOnlySet<string>? ParseOwnedOutput(string? output, IReadOnlyCollection<string> candidates)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(text => text.StartsWith('{'));
        if (line is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (!doc.RootElement.TryGetProperty("owned", out var owned) || owned.ValueKind != JsonValueKind.Array ||
                !doc.RootElement.TryGetProperty("checked", out var checkedCount) ||
                !checkedCount.TryGetInt32(out var count) || count != candidates.Count)
                return null;
            var asked = candidates as IReadOnlySet<string> ?? candidates.ToHashSet(StringComparer.Ordinal);
            var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in owned.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Number || !item.TryGetUInt32(out var id)) continue;
                var text = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (asked.Contains(text)) result.Add(text);
            }
            return result;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Must match the helper's uint parse, or the "checked" count cannot agree
    // and the answer is discarded.
    private static bool IsQueryableAppId(string? value) =>
        Cli.SteamProtocol.IsValidAppId(value) && value[0] != '0' && uint.TryParse(value, out _);

    private static string? ResolveHost()
    {
        var dir = Path.GetDirectoryName(Environment.ProcessPath);
        if (string.IsNullOrWhiteSpace(dir))
            return null;
        foreach (var candidate in new[]
                 {
                     Path.Combine(dir, "steam-ipc", HostExeName),
                     Path.Combine(dir, HostExeName),
                 })
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    /// <summary>
    /// The helper's <c>props</c> answer for a game and the given DLC ids, or null
    /// when Steam is not running, not signed in, or did not answer in time.
    /// </summary>
    internal static async Task<SteamLiveConfig?> QueryPropertiesAsync(string appId, IEnumerable<uint> dlcIds, CancellationToken ct)
    {
        var exe = ResolveHost();
        if (exe is null || !Cli.SteamProtocol.IsValidAppId(appId)) return null;
        var (code, output) = await RunHelperAsync(exe, ["props", appId, string.Join(',', dlcIds)], null, TimeSpan.FromSeconds(10), ct)
            .ConfigureAwait(false);
        return code == 0 ? SteamGameProperties.ParseLive(output) : null;
    }

    /// <summary>
    /// One Properties change through the helper's <c>set</c>; <paramref name="secret"/>
    /// (a beta access code) goes on stdin, never on the command line.
    /// </summary>
    internal static async Task<SteamIpcStatus> SetPropertyAsync(string appId, IReadOnlyList<string> change, string? secret, CancellationToken ct)
    {
        var exe = ResolveHost();
        if (exe is null) return SteamIpcStatus.HostMissing;
        if (!Cli.SteamProtocol.IsValidAppId(appId)) return SteamIpcStatus.CommandFailed;
        var (code, _) = await RunHelperAsync(exe, ["set", appId, .. change], secret, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        return code switch { 0 => SteamIpcStatus.Ok, 1 or 2 => SteamIpcStatus.CommandFailed, _ => SteamIpcStatus.Unavailable };
    }

    private static async Task<(int Code, string Output)> RunHelperAsync(string exe, IReadOnlyList<string> args, string? stdin,
        TimeSpan timeout, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        Process? process = null;
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                WorkingDirectory = Path.GetDirectoryName(exe),
            };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            process = Process.Start(start);
            if (process is null) return (-1, "");
            if (stdin is not null) await process.StandardInput.WriteLineAsync(stdin.AsMemory(), deadline.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            var output = await process.StandardOutput.ReadToEndAsync(deadline.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            return (process.ExitCode, output);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            AppLog.Info($"Steam IPC {args[0]} timed out.");
            return (-1, "");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLog.Info($"Steam IPC {args[0]} failed: {ex.GetType().Name}");
            return (-1, "");
        }
        finally
        {
            if (process is not null)
            {
                try { if (!process.HasExited) ProcessTermination.Kill(process, entireProcessTree: true); } catch { }
                process.Dispose();
            }
        }
    }

    internal static SteamProgressWatcher StartProgressWatcher(
        string appId,
        string gameId,
        IProgress<InstallProgress>? progress,
        bool install)
    {
        return new SteamProgressWatcher(ResolveHost(), appId, gameId, progress, install);
    }
}

internal sealed class SteamProgressWatcher : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly Process? _process;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _reader;
    private readonly string _gameId;
    private readonly IProgress<InstallProgress>? _progress;
    private readonly bool _install;
    private readonly SteamProgressSource _source = new();
    private int _activeTransfer;
    internal bool HasActiveTransfer => Volatile.Read(ref _activeTransfer) != 0 && !_source.ShouldUseFallback;
    internal void ReportFallback(Action publish) => _source.ReportFallback(publish);

    public SteamProgressWatcher(
        string? helperPath,
        string appId,
        string gameId,
        IProgress<InstallProgress>? progress,
        bool install)
    {
        _gameId = gameId;
        _progress = progress;
        _install = install;
        if (string.IsNullOrWhiteSpace(helperPath) || progress is null)
        {
            _reader = Task.CompletedTask;
            return;
        }

        try
        {
            var start = new ProcessStartInfo
            {
                FileName = helperPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                WorkingDirectory = Path.GetDirectoryName(helperPath) ?? string.Empty,
            };
            start.ArgumentList.Add("watch");
            start.ArgumentList.Add(appId);
            start.ArgumentList.Add("--game-id");
            start.ArgumentList.Add(gameId);
            start.ArgumentList.Add("--mode");
            start.ArgumentList.Add(install ? "install" : "update");
            start.ArgumentList.Add("--parent-pid");
            start.ArgumentList.Add(Environment.ProcessId.ToString());
            _process = Process.Start(start);
            _reader = _process is null ? Task.CompletedTask : ReadAsync(_process, _stop.Token);
        }
        catch
        {
            _process = null;
            _reader = Task.CompletedTask;
        }
    }

    private async Task ReadAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null) break;
                WatchMessage? message;
                try { message = JsonSerializer.Deserialize<WatchMessage>(line, JsonOptions); }
                catch { continue; }
                if (message is null || message.GameId != _gameId) continue;
                var completed = message.Phase?.Equals("completed", StringComparison.OrdinalIgnoreCase) == true;
                var validPercent = message.Percent is >= 0 and <= 100;
                var valid = message.HasTelemetry && (validPercent || message.Phase is "installing" or "finalizing" or "completed");
                _source.ReportHelper(message.Sequence, valid, () =>
                {
                Volatile.Write(ref _activeTransfer,
                    message.Phase is "downloading" or "installing" && message.Percent is null or < 100 ? 1 : 0);
                _progress?.Report(new InstallProgress
                {
                    GameId = string.IsNullOrWhiteSpace(message.GameId) ? _gameId : message.GameId,
                    Phase = completed
                        ? InstallPhase.Finalizing
                        : message.Phase?.ToLowerInvariant() switch
                        {
                            "downloading" => InstallPhase.Downloading,
                            "installing" => InstallPhase.Installing,
                            "finalizing" => InstallPhase.Finalizing,
                            _ => InstallPhase.Preparing,
                        },
                    Percent = completed || !validPercent ? null : message.Percent,
                    BytesDownloaded = message.BytesDownloaded,
                    BytesToDownload = message.BytesToDownload,
                    Status = completed
                        ? "Finishing…"
                        : message.Status ?? (_install ? "Installing…" : "Updating…"),
                    CanCancel = true,
                });
                }, $"{message.Phase}|{message.Percent}|{message.BytesDownloaded}|{message.BytesToDownload}|{message.Status}");
                if (completed) break;
            }
        }
        catch (OperationCanceledException) { }
        catch { }
        finally { Volatile.Write(ref _activeTransfer, 0); _source.End(); }
    }

    public void Dispose()
    {
        _source.End();
        _stop.Cancel();
        try
        {
            if (_process is { HasExited: false })
                ProcessTermination.Kill(_process, entireProcessTree: true);
        }
        catch { }
        try { _process?.WaitForExit(500); } catch { }
        try { _reader.Wait(500); } catch { }
        _process?.Dispose();
        _stop.Dispose();
    }

    private sealed class WatchMessage
    {
        public string? GameId { get; init; }
        public string? Phase { get; init; }
        public double? Percent { get; init; }
        public long? BytesDownloaded { get; init; }
        public long? BytesToDownload { get; init; }
        public string? Status { get; init; }
        public bool IsActive { get; init; }
        public bool HasTelemetry { get; init; }
        public long Sequence { get; init; }
    }
}
