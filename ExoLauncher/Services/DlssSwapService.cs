using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using ExoLauncher.Adapters;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>
/// In-place upscaler replace. Exo writes only file names the game already
/// ships, only where the scan saw that exact file, and only official vendor
/// files pinned by SHA-256 (<see cref="PinnedFiles"/>). It never adds a
/// missing file, never wraps or injects, and never touches a game with
/// anti-cheat unless the user allowed it for that game (never a Riot game;
/// see DlssSwapService.AntiCheat.cs). The shipped file is captured once as <c>.dlsss</c> before the
/// first write, so Restore hands back the game's own bytes, checked by hash.
/// </summary>
public sealed partial class DlssSwapService
{
    internal const string BackupSuffix = ".exo-bak";
    internal const string SwapperBackupSuffix = ".dlsss";
    internal const string SidecarName = ".exo-upscale.json";
    internal const string WrittenSuffix = ".exo-written";
    internal const string StaleSuffix = ".exo-stale";
    internal const string NewSuffix = ".exo-new";
    internal const string StoreLockedMessage = "This install is locked by the store.";
    internal const string GameRunningMessage = "Close the game first.";
    internal const string NoBackupMessage = "Exo could not save the original, so it left this file alone.";
    internal const string NoOriginalMessage = "Exo has no copy of the original file.";
    internal const string GameUpdatedMessage = "The game updated this file after Exo changed it, so it is already the game's own file.";
    internal const string OutsideGameMessage = "This file is outside the game's install folder.";
    internal const string UnownedCompanionMessage = "Kept this file because Exo cannot verify that it still owns it.";
    private const string NothingToUpdateMessage = "This game has no upscaler files Exo can update.";
    private const string KeptNewerMessage = "Kept the game's newer file.";
    private const string AlreadyThisMessage = "Already this version.";
    private static readonly string[] OriginalBackupSuffixes = [SwapperBackupSuffix, BackupSuffix];
    private const ushort PeMachineAmd64 = 0x8664;
    private const ushort PeMachineI386 = 0x014c;

    private static readonly ConcurrentDictionary<string, MutationGate> MutationGates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, DetectionStatusCache> DetectionCaches = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] SkipScanFolders =
    [
        "_commonredist", "redist", "__installer", "node_modules", ".git",
        "paks", "movies", "videos", "cinematics", "shadercache", "saved",
        "intermediate", "logs", "crashdumps", ".egstore",
    ];

    private static readonly HashSet<string> SkipScanFolderNames = new(SkipScanFolders, StringComparer.OrdinalIgnoreCase);

    private sealed record DestSpec(string FileName, string Kind);

    private static readonly DestSpec[] DestSpecs =
    [
        new("nvngx_dlss.dll", "DLSS"),
        new("nvngx_dlssg.dll", "Frame Generation"),
        new("nvngx_dlssd.dll", "Ray Reconstruction"),
        new("amd_fidelityfx_dx12.dll", "FSR"),
        new("amd_fidelityfx_vk.dll", "FSR"),
        new("amd_fidelityfx_loader_dx12.dll", "FSR 4"),
        new("amd_fidelityfx_upscaler_dx12.dll", "FSR 4"),
        new("amd_fidelityfx_framegeneration_dx12.dll", "FSR FG"),
        new("amd_fidelityfx_denoiser_dx12.dll", "FSR RR"),
        new("amd_fidelityfx_radiancecache_dx12.dll", "FSR RC"),
        new("libxess.dll", "XeSS"),
        new("libxess_dx11.dll", "XeSS"),
        new("libxess_fg.dll", "XeSS FG"),
        new("libxell.dll", "XeLL"),
    ];

    /// <summary>
    /// One upscaler file in one folder of a game, as last read off disk.
    /// <see cref="Origin"/>: <c>shipped</c> (the game's own file), <c>exo</c>
    /// (Exo's write, hash-checked), <c>reverted</c> (Exo wrote it and the store
    /// put the original back), <c>changed</c> (Exo wrote it and something else,
    /// usually a game update, replaced it since) or <c>other</c> (another tool
    /// swapped it and left its own backup).
    /// </summary>
    public sealed record DetectedDll(
        string Path,
        string RelativePath,
        string FileName,
        string Kind,
        string? CurrentVersion,
        string? SkipReason,
        string Origin,
        string? OriginalVersion,
        string? OriginalState,
        string? MatchedRelease);

    public sealed record ScanResult(IReadOnlyList<DetectedDll> Files, string? AntiCheat);

    public sealed record StatusRow(
        DetectedDll Dll,
        PinnedFile? Latest,
        string State,
        bool CanUpdate,
        bool CanRestore);

    /// <param name="Consent">Null without anti-cheat; else <c>never</c>, <c>available</c> or <c>given</c>.</param>
    public sealed record StatusResult(
        bool Ok,
        string? Message,
        string? AntiCheat,
        string? Consent,
        IReadOnlyList<StatusRow> Rows);

    public sealed record UpdateResult(
        bool Ok,
        int Updated,
        int Skipped,
        int Failed,
        string Message,
        IReadOnlyList<FileOutcome>? Files = null);

    /// <summary>What happened at one file, with the version before and after.</summary>
    public sealed record FileOutcome(string FileName, string? Folder, string State, string? Before, string? After, string Message);

    private sealed class UpscaleSidecar
    {
        public int Version { get; set; } = 1;
        public List<string> Injected { get; set; } = [];
    }

    private sealed class MutationGate
    {
        public object Sync { get; } = new();
        public object Lifecycle { get; } = new();
        public int Users { get; set; }
        public bool Retired { get; set; }
    }

    private sealed class MutationLease(string key, MutationGate gate) : IDisposable
    {
        private MutationGate? _gate = gate;
        public object Sync { get; } = gate.Sync;

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _gate, null);
            if (current is null) return;
            lock (current.Lifecycle)
            {
                current.Users--;
                if (current.Users != 0) return;
                current.Retired = true;
                ((ICollection<KeyValuePair<string, MutationGate>>)MutationGates)
                    .Remove(new KeyValuePair<string, MutationGate>(key, current));
            }
        }
    }

    // ------------------------------------------------------------------ status

    /// <summary>
    /// Answers from the last scan while the files on disk are unchanged; the
    /// newest versions are compiled in or the last catalog read (refreshed in
    /// the background at most daily), so nothing here waits on the network or
    /// hashes a file. <paramref name="refresh"/> walks the install again.
    /// </summary>
    /// <param name="consented">The anti-cheat the user accepted the risk of for this game, from settings.</param>
    public StatusResult GetStatus(GameEntry game, bool refresh, string? consented, CancellationToken ct)
    {
        WatchUpscalerReleases();
        var scan = refresh ? ScanAndStore(game, ct) : CachedScan(game, ct);
        var antiCheat = AntiCheatForGame(game) ?? scan.AntiCheat;
        var consent = ConsentState(game, antiCheat, consented);
        var rows = scan.Files.Select(dll => BuildRow(dll, blocked: antiCheat is not null && consent != "given")).ToList();
        return new StatusResult(
            true,
            antiCheat is null ? null : AntiCheatMessage(game, antiCheat, consented),
            antiCheat,
            consent,
            rows);
    }

    /// <summary>The anti-cheat in the game right now, from a fresh walk (for the consent step).</summary>
    public string? AntiCheatNow(GameEntry game, CancellationToken ct) =>
        AntiCheatForGame(game) ?? ScanAndStore(game, ct).AntiCheat;

    private static StatusRow BuildRow(DetectedDll dll, bool blocked)
    {
        var latest = LatestFor(dll.FileName);
        var state = VersionState(dll, latest);
        var usable = !blocked && dll.SkipReason is null && latest is not null;
        return new StatusRow(
            dll,
            latest,
            state,
            CanUpdate: usable && state is "outdated" or "unknown",
            CanRestore: dll.Origin is "exo" or "other" && dll.OriginalState == "saved" && !IsWindowsAppsPath(dll.Path));
    }

    /// <summary><c>current</c>, <c>outdated</c>, <c>newer</c> (the game is ahead of Exo) or <c>unknown</c>.</summary>
    internal static string VersionState(DetectedDll dll, PinnedFile? latest)
    {
        if (latest is null) return "unknown";
        if (dll.MatchedRelease is not null)
            return dll.MatchedRelease.Equals(latest.Release, StringComparison.OrdinalIgnoreCase) ? "current" : "outdated";
        if (TryParseVersion(dll.CurrentVersion) is null) return "unknown";
        if (SkipApplyReason(dll.FileName, dll.CurrentVersion, latest.Version) is not null) return "newer";
        return CompareVersions(dll.CurrentVersion, latest.Version) == 0 ? "current" : "outdated";
    }

    private ScanResult CachedScan(GameEntry game, CancellationToken ct)
    {
        var cache = CurrentDetectionCache();
        var cached = cache.TryGet(game, out var stale);
        if (cached is null) return ScanAndStore(game, ct);
        if (stale) RescanBehind(game, cache);
        return cached;
    }

    private ScanResult ScanAndStore(GameEntry game, CancellationToken ct = default)
    {
        var scan = ScanGame(game, ct);
        CurrentDetectionCache().Store(game, scan);
        return scan;
    }

    private static readonly ConcurrentDictionary<string, byte> Rescanning = new(StringComparer.OrdinalIgnoreCase);

    /// One background rescan per game; the next status read sees its result.
    private void RescanBehind(GameEntry game, DetectionStatusCache cache)
    {
        if (!Rescanning.TryAdd(game.Id, 0)) return;
        _ = Task.Run(() =>
        {
            try { cache.Store(game, ScanGame(game)); }
            catch (Exception ex) { AppLog.Debug("Upscaler rescan failed: " + ex.GetType().Name); }
            finally { Rescanning.TryRemove(game.Id, out _); }
        });
    }

    private static DetectionStatusCache CurrentDetectionCache()
    {
        var path = Path.GetFullPath(Path.Combine(CacheRoot, "status-v2.json"));
        var appVersion = typeof(DlssSwapService).Assembly.GetName().Version?.ToString() ?? "0.0.0.0";
        return DetectionCaches.GetOrAdd(path, _ => new DetectionStatusCache(path, appVersion));
    }

    internal static void RepairDetectionCache() => CurrentDetectionCache().Clear();

    // ------------------------------------------------------------------ apply

    /// <summary>
    /// Puts official files into the game. With no <paramref name="fileKey"/>
    /// every file the game ships moves to Exo's newest release, and a file
    /// that is already newer is kept. With a file and a release, that file
    /// (and the files that must match it in the same folder) is set to exactly
    /// that release, older or newer.
    /// </summary>
    /// <param name="consent">
    /// Reads the anti-cheat the user accepted for this game from settings at
    /// the moment it is called, so consent withdrawn during a download stops
    /// the write.
    /// </param>
    public async Task<UpdateResult> ApplyAsync(GameEntry game, string? fileKey, string? release, Func<string?> consent, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var root = ResolveInstallRoot(game.Path);
        if (root is null) return Fail("The install folder is unavailable.");
        var scan = ScanAndStore(game, ct);
        if (AntiCheatRefusal(game, scan, consent(), out _) is { } refused) return refused;

        var plan = Plan(scan.Files, fileKey, release, out var planError);
        if (planError is not null) return Fail(planError);
        if (plan.Count == 0)
            return fileKey is null && scan.Files.Any(dll => dll.SkipReason is null)
                ? new UpdateResult(true, 0, 0, 0, "Already the newest.")
                : Fail(NothingToUpdateMessage);

        IReadOnlyDictionary<PinnedFile, string> sources;
        try
        {
            // Files already holding the chosen bytes, or newer ones an update
            // keeps, need no download.
            sources = await EnsurePinnedAsync(
                plan.Where(step => !step.Pinned.Release.Equals(step.Dll.MatchedRelease, StringComparison.OrdinalIgnoreCase) &&
                                   (fileKey is not null || SkipApplyReason(step.Dll.FileName, step.Dll.CurrentVersion, step.Pinned.Version) is null))
                    .Select(step => step.Pinned),
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLog.Warn("Upscaler download failed: " + ex.Message);
            return Fail("Could not download the files: " + ex.Message);
        }

        using var mutation = AcquireMutationGate(game);
        UpdateResult result;
        lock (mutation.Sync)
            result = ApplyLocked(game, root, plan, sources, explicitChoice: fileKey is not null, consent);
        ScanAndStore(game);
        return result;
    }

    /// <summary>
    /// Null when Exo may write: no anti-cheat, or the user's consent for this
    /// game names the anti-cheat detected now (and it is not Riot's).
    /// <paramref name="accepted"/> is that anti-cheat, for the write log.
    /// </summary>
    private static UpdateResult? AntiCheatRefusal(GameEntry game, ScanResult scan, string? consented, out string? accepted)
    {
        accepted = null;
        if ((AntiCheatForGame(game) ?? scan.AntiCheat) is not { } antiCheat) return null;
        if (Accepted(game, antiCheat, consented))
        {
            accepted = antiCheat;
            return null;
        }
        AppLog.Info($"Upscaler refused: gameId={game.Id}; antiCheat={antiCheat}; consent={(NeverAllowed(game, antiCheat) ? "never possible" : consented ?? "none")}.");
        return Fail(AntiCheatMessage(game, antiCheat, consented));
    }

    private sealed record PlanStep(DetectedDll Dll, PinnedFile Pinned);

    private static List<PlanStep> Plan(IReadOnlyList<DetectedDll> files, string? fileKey, string? release, out string? error)
    {
        error = null;
        var steps = new List<PlanStep>();
        if (fileKey is null)
        {
            foreach (var dll in files)
                if (dll.SkipReason is null && LatestFor(dll.FileName) is { } latest && VersionState(dll, latest) is "outdated" or "unknown")
                    steps.Add(new(dll, latest));
            return steps;
        }

        var target = files.FirstOrDefault(dll => dll.RelativePath.Equals(fileKey, StringComparison.OrdinalIgnoreCase));
        if (target is null) { error = "That file is no longer in the game's folder. Refresh and try again."; return steps; }
        if (target.SkipReason is not null) { error = target.SkipReason; return steps; }
        var chosen = release is null or "latest" ? LatestFor(target.FileName) : FindPinned(target.FileName, release);
        if (chosen is null) { error = "Exo does not have that version."; return steps; }
        steps.Add(new(target, chosen));

        var folder = Path.GetDirectoryName(target.RelativePath) ?? string.Empty;
        foreach (var name in CoupledWith(target.FileName))
        {
            var sibling = files.FirstOrDefault(dll =>
                dll.FileName.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Path.GetDirectoryName(dll.RelativePath) ?? string.Empty, folder, StringComparison.OrdinalIgnoreCase));
            if (sibling is null || sibling.SkipReason is not null) continue;
            if (FindPinned(name, chosen.Release) is { } match) steps.Add(new(sibling, match));
        }
        return steps;
    }

    private UpdateResult ApplyLocked(
        GameEntry game,
        string root,
        IReadOnlyList<PlanStep> plan,
        IReadOnlyDictionary<PinnedFile, string> sources,
        bool explicitChoice,
        Func<string?> consent)
    {
        if (IsWindowsAppsPath(root)) return Fail(StoreLockedMessage);
        if (IsGameRunning(game, root)) return Fail(GameRunningMessage);
        // The decision to write is made again under the lock from a fresh walk
        // and the consent as saved now, never from the earlier scan or a cache:
        // anti-cheat installed since, or consent withdrawn since, is still
        // caught here.
        var fresh = ScanGame(game);
        if (AntiCheatRefusal(game, fresh, consent(), out var accepted) is { } refused) return refused;
        var risk = accepted is null ? "." : $"; anti-cheat {accepted}; user accepted the risk.";
        var now = fresh.Files.ToDictionary(dll => dll.Path, StringComparer.OrdinalIgnoreCase);

        string? WhySkip(PlanStep step, DetectedDll dll) =>
            dll.SkipReason ??
            (dll.MatchedRelease is not null && dll.MatchedRelease.Equals(step.Pinned.Release, StringComparison.OrdinalIgnoreCase) ? AlreadyThisMessage
            : !explicitChoice && SkipApplyReason(dll.FileName, dll.CurrentVersion, step.Pinned.Version) is { } keep ? keep
            : !sources.ContainsKey(step.Pinned) ? "Exo has no verified copy of this version."
            : null);

        // Linked files (an FSR loader and its upscaler) change together or not
        // at all: every one is checked writable before the first is touched,
        // and a failure puts back the bytes of partners already written here.
        var toWrite = plan.Where(step => now.TryGetValue(step.Dll.Path, out var dll) && IsSafeGamePath(root, dll.Path) && WhySkip(step, dll) is null).ToList();
        var stopped = toWrite.Where(step => toWrite.Any(other => Linked(step, other)) && !CanWrite(step.Dll.Path)).ToList();
        var written = new List<(PlanStep Step, byte[] Bytes, string? Marker, int Outcome)>();

        int updated = 0, skipped = 0, failed = 0;
        var outcomes = new List<FileOutcome>();

        void UndoLinked(PlanStep failedStep)
        {
            stopped.Add(failedStep);
            foreach (var done in written.Where(item => Linked(failedStep, item.Step)).ToList())
            {
                written.Remove(done);
                var path = done.Step.Dll.Path;
                var undo = path + ".exo-undo";
                try
                {
                    File.WriteAllBytes(undo, done.Bytes);
                    ReplaceExisting(path, undo);
                    if (done.Marker is null) TryDelete(path + WrittenSuffix);
                    else WriteMarker(path + WrittenSuffix, done.Marker);
                    var outcome = outcomes[done.Outcome];
                    outcomes[done.Outcome] = outcome with { State = "failed", After = outcome.Before, Message = LinkedFailMessage };
                    updated--;
                    failed++;
                    AppLog.Info("Upscaler write undone, a linked file failed: " + path);
                }
                catch (Exception ex) { AppLog.Warn("Upscaler undo failed for " + path + ": " + FailMessage(ex)); }
                finally { TryDelete(undo); }
            }
        }

        foreach (var step in plan)
        {
            var folder = FolderOf(step.Dll.RelativePath);
            if (!now.TryGetValue(step.Dll.Path, out var dll) || !IsSafeGamePath(root, dll.Path))
            {
                failed++;
                outcomes.Add(new(step.Dll.FileName, folder, "failed", step.Dll.CurrentVersion, null, OutsideGameMessage));
                continue;
            }
            var before = DisplayVersion(dll);
            void Skip(string why)
            {
                skipped++;
                outcomes.Add(new(dll.FileName, folder, "skipped", before, before, why));
                AppLog.Info($"Upscaler skip: gameId={game.Id}; file={dll.RelativePath}; dest={dll.CurrentVersion ?? "—"}; src={step.Pinned.Version}; why={why}");
            }

            if (WhySkip(step, dll) is { } reason) { Skip(reason); continue; }
            var source = sources[step.Pinned];
            if (stopped.Any(other => Linked(step, other)))
            {
                failed++;
                outcomes.Add(new(dll.FileName, folder, "failed", before, before, LinkedFailMessage));
                continue;
            }

            try
            {
                if (IsStoreLockedDest(dll.Path))
                    throw new UnauthorizedAccessException(StoreLockedMessage);
                if (!ReconcileOriginal(dll.Path, liveIsKnownRelease: dll.MatchedRelease is not null))
                {
                    Skip(NoBackupMessage);
                    UndoLinked(step);
                    continue;
                }
                var previous = toWrite.Any(other => Linked(step, other)) ? File.ReadAllBytes(dll.Path) : null;
                var previousMarker = TryReadText(dll.Path + WrittenSuffix);
                WriteWithBackup(dll.Path, source, KnownSha(step.Pinned) ?? FileSha256(source));
                if (!SameFileBytes(dll.Path, source))
                {
                    RollbackDest(dll.Path);
                    failed++;
                    outcomes.Add(new(dll.FileName, folder, "failed", before, before, "The written file did not match the vendor's file."));
                    AppLog.Warn("Upscaler write did not match for " + dll.Path);
                    UndoLinked(step);
                    continue;
                }
                AppLog.Info($"Upscaler write: gameId={game.Id}; file={dll.RelativePath}; dest={dll.CurrentVersion ?? "—"}; src={step.Pinned.Version}; release={step.Pinned.Release}; sha={(KnownSha(step.Pinned) is { Length: >= 16 } sha ? sha[..16] : "?")}{risk}");
                updated++;
                outcomes.Add(new(dll.FileName, folder, "updated", before, step.Pinned.Display, $"Now {step.Pinned.Sdk}."));
                if (previous is not null) written.Add((step, previous, previousMarker, outcomes.Count - 1));
            }
            catch (Exception ex)
            {
                failed++;
                outcomes.Add(new(dll.FileName, folder, "failed", before, before, FailMessage(ex)));
                AppLog.Warn("Upscaler write failed for " + dll.Path + ": " + FailMessage(ex));
                UndoLinked(step);
            }
        }

        var message = failed > 0 ? "Some files could not be changed."
            : updated > 0 ? "Updated."
            : outcomes.All(item => item.Message == AlreadyThisMessage) ? "Already the newest."
            : "Nothing to update.";
        return new UpdateResult(failed == 0, updated, skipped, failed, message, outcomes);
    }

    /// <summary>
    /// Before a write, decide what the game's own file is. If the store put the
    /// shipped file back, the saved original is still right. If a game update
    /// replaced Exo's file with a different one, that file is the new original:
    /// it is captured in place of the old one, so Restore never hands back a
    /// file from before the update. A live file whose bytes are a release Exo
    /// ships is a vendor file (Exo's own write whose marker was lost), never
    /// adopted as the original. False when no verified original is saved.
    /// </summary>
    internal static bool ReconcileOriginal(string dest, bool liveIsKnownRelease)
    {
        InvalidateForeignWrite(dest);
        if (File.Exists(dest + StaleSuffix))
        {
            var factory = PreferFactory(dest);
            if (factory is not null && !liveIsKnownRelease && !SameFileBytes(dest, factory))
            {
                CopyVerified(dest, dest + SwapperBackupSuffix, overwrite: true);
                CopyVerified(dest, dest + BackupSuffix, overwrite: true);
                AppLog.Info("Upscaler original replaced after a game update: " + dest);
            }
            TryDelete(dest + StaleSuffix);
        }
        return HasOriginalBackup(dest) ? HasFirstShippedBackup(dest) : TryCaptureFirstShipped(dest);
    }

    private const string LinkedFailMessage = "A linked file could not be changed, so Exo left this one as it was.";

    /// Files that must hold the same release: coupled names in one folder.
    private static bool Linked(PlanStep step, PlanStep other) =>
        !ReferenceEquals(step, other) &&
        string.Equals(FolderOf(step.Dll.RelativePath), FolderOf(other.Dll.RelativePath), StringComparison.OrdinalIgnoreCase) &&
        CoupledWith(step.Dll.FileName).Contains(other.Dll.FileName, StringComparer.OrdinalIgnoreCase);

    private static bool CanWrite(string path)
    {
        ClearReadOnly(path);
        return !IsStoreLockedDest(path) && ProbeWritable(path);
    }

    private static UpdateResult Fail(string message) => new(false, 0, 0, 0, message);

    // ------------------------------------------------------------------ restore

    /// <summary>
    /// Puts the game's own file back, for one file or all of them, and checks
    /// the result byte for byte against the saved original.
    /// </summary>
    public UpdateResult RestoreGame(GameEntry game, string? fileKey = null)
    {
        using var mutation = AcquireMutationGate(game);
        UpdateResult result;
        lock (mutation.Sync)
            result = RestoreGameLocked(game, fileKey);
        if (ResolveInstallRoot(game.Path) is not null) ScanAndStore(game);
        return result;
    }

    private UpdateResult RestoreGameLocked(GameEntry game, string? fileKey)
    {
        var root = ResolveInstallRoot(game.Path);
        if (root is null) return Fail("The install folder is unavailable.");
        if (IsWindowsAppsPath(root)) return new UpdateResult(false, 0, 0, 1, StoreLockedMessage);
        if (IsGameRunning(game, root)) return new UpdateResult(false, 0, 0, 1, GameRunningMessage);

        string? only = null;
        if (fileKey is not null)
        {
            try { only = Path.GetFullPath(Path.Combine(root, fileKey)); }
            catch { return Fail(OutsideGameMessage); }
            if (!IsSafeGamePath(root, only) || !IsDestName(Path.GetFileName(only))) return Fail(OutsideGameMessage);
        }

        int restored = 0, removed = 0, skipped = 0, failed = 0;
        var restoredPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var outcomes = new List<FileOutcome>();
        try
        {
            var backups = ChooseOriginalBackups(root)
                .Where(pair => only is null || pair.Key.Equals(only, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var sidecars = only is not null ? [] : FindSidecars(root).Select(path => (Path: path, Sidecar: ReadSidecar(path))).ToList();
            var legacyInjected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (sidecarPath, sidecar) in sidecars)
            {
                if (sidecar is null) continue;
                var folder = Path.GetDirectoryName(sidecarPath) ?? root;
                foreach (var relative in sidecar.Injected)
                {
                    try
                    {
                        var path = Path.GetFullPath(Path.Combine(folder, relative));
                        if (IsSafeGamePath(root, path) && IsSafeDllName(Path.GetFileName(path)))
                            legacyInjected.Add(path);
                    }
                    catch { /* malformed legacy entries have no authority */ }
                }
            }
            foreach (var dll in ScanGame(game).Files)
            {
                if (only is not null && !dll.Path.Equals(only, StringComparison.OrdinalIgnoreCase)) continue;
                if (backups.Any(pair => pair.Key.Equals(dll.Path, StringComparison.OrdinalIgnoreCase)) ||
                    legacyInjected.Contains(dll.Path) || !File.Exists(dll.Path + WrittenSuffix)) continue;
                failed++;
                outcomes.Add(new(dll.FileName, FolderOf(dll.RelativePath), "failed", DisplayVersion(dll), DisplayVersion(dll), NoOriginalMessage));
                AppLog.Warn("Upscaler restore refused, no captured original for " + dll.Path);
            }

            foreach (var (dest, backup) in backups)
            {
                var name = Path.GetFileName(dest);
                var folder = FolderOf(Path.GetRelativePath(root, dest));
                try
                {
                    if (!IsSafeGamePath(root, dest) || !IsSafeGamePath(root, backup))
                        throw new IOException(OutsideGameMessage);
                    var factory = PreferFactory(dest);
                    if (factory is null || !File.Exists(dest)) continue;
                    var before = TrimVersion(FileVersion(dest));
                    if (SameFileBytes(dest, factory))
                    {
                        // Already the game's own bytes (a store check put them back).
                        TryDelete(dest + WrittenSuffix);
                        TryDelete(dest + StaleSuffix);
                        if (only is not null)
                        {
                            skipped++;
                            outcomes.Add(new(name, folder, "skipped", before, before, "Already the game's own file."));
                        }
                        continue;
                    }
                    InvalidateForeignWrite(dest);
                    if (!HasValidRestoreClaim(dest))
                    {
                        failed++;
                        outcomes.Add(new(name, folder, "failed", before, before, GameUpdatedMessage));
                        AppLog.Warn("Upscaler restore refused stale claim for " + dest);
                        continue;
                    }
                    if (IsStoreLockedDest(dest))
                    {
                        failed++;
                        outcomes.Add(new(name, folder, "failed", before, before, StoreLockedMessage));
                        continue;
                    }
                    ReplaceExisting(dest, factory);
                    if (!SameFileBytes(dest, factory))
                    {
                        failed++;
                        outcomes.Add(new(name, folder, "failed", before, TrimVersion(FileVersion(dest)), "The restored file did not match the saved original."));
                        AppLog.Warn("Upscaler restore did not match factory for " + dest);
                        continue;
                    }
                    var shipped = TrimVersion(FileVersion(factory));
                    AppLog.Info($"Upscaler restore: gameId={game.Id}; file={Path.GetRelativePath(root, dest)}; dest={before ?? "—"}; shipped={shipped ?? "—"}; sha={FileSha256(dest)?[..16]}.");
                    restoredPaths.Add(dest);
                    TryDelete(dest + WrittenSuffix);
                    restored++;
                    outcomes.Add(new(name, folder, "restored", before, shipped, "Original put back."));
                }
                catch (Exception ex)
                {
                    failed++;
                    outcomes.Add(new(name, folder, "failed", null, null, FailMessage(ex)));
                    AppLog.Warn("Upscaler restore failed for " + backup + ": " + FailMessage(ex));
                }
            }

            // A sidecar from an older Exo names a file it injected. Only a
            // matching write hash proves Exo still owns those bytes.
            if (restoredPaths.Count > 0)
            {
                foreach (var (sidecarPath, sidecar) in sidecars)
                {
                    var folder = Path.GetDirectoryName(sidecarPath) ?? root;
                    var preserveSidecar = false;
                    if (sidecar is not null)
                    {
                        foreach (var relative in sidecar.Injected)
                        {
                            try
                            {
                                var dest = Path.GetFullPath(Path.Combine(folder, relative));
                                if (!IsSafeGamePath(root, dest)) { preserveSidecar = true; continue; }
                                if (restoredPaths.Contains(dest) || HasOriginalBackup(dest)) continue;
                                var name = Path.GetFileName(dest);
                                if (!IsSafeDllName(name) || !File.Exists(dest)) continue;
                                if (!HasMatchingWrittenHash(dest))
                                {
                                    preserveSidecar = true;
                                    skipped++;
                                    outcomes.Add(new(name, FolderOf(Path.GetRelativePath(root, dest)), "skipped", null, null, UnownedCompanionMessage));
                                    continue;
                                }
                                File.Delete(dest);
                                TryDelete(dest + WrittenSuffix);
                                removed++;
                            }
                            catch (Exception ex)
                            {
                                failed++;
                                preserveSidecar = true;
                                AppLog.Warn("Upscaler inject remove failed for " + relative + ": " + ex.Message);
                            }
                        }
                    }
                    if (!preserveSidecar) TryDelete(sidecarPath);
                }
            }

            foreach (var dest in restoredPaths)
                TryDelete(dest + BackupSuffix);
        }
        catch (Exception ex)
        {
            return new UpdateResult(false, restored, skipped, failed, "Restore failed: " + ex.Message, outcomes);
        }

        var message = failed > 0
            ? outcomes.FirstOrDefault(item => item.State == "failed")?.Message ?? "Could not restore."
            : restored == 0 && removed == 0
                ? outcomes.Count > 0 ? outcomes[0].Message : "Nothing to restore."
                : "Restored.";
        return new UpdateResult(failed == 0, restored + removed, skipped, failed, message, outcomes);
    }

    // ------------------------------------------------------------------ scan

    /// <summary>
    /// One walk of the install: every upscaler file Exo knows by name, and the
    /// first anti-cheat marker found anywhere, including folders the DLL search
    /// itself skips. Hashes only what it must: files Exo wrote (to prove it),
    /// files with a saved original of the same size, and files the size of a
    /// release Exo pins.
    /// </summary>
    internal ScanResult ScanGame(GameEntry game, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var root = ResolveInstallRoot(game.Path);
        if (root is null) return new ScanResult([], null);

        var files = new List<string>();
        string? antiCheat = null;
        try
        {
            Walk(root, files, ref antiCheat, 0, ct);
            antiCheat ??= AncestorAntiCheat(game.Path, root);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return new ScanResult([], UnreadableInstall);
        }

        var found = new List<DetectedDll>();
        foreach (var file in files.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            string full;
            try { full = Path.GetFullPath(file); }
            catch { continue; }
            var name = Path.GetFileName(full);
            if (!IsSafeDllName(name) || !IsSafeGamePath(root, full)) continue;
            found.Add(Inspect(root, full, name));
        }
        return new ScanResult(
            found.OrderBy(dll => dll.RelativePath, StringComparer.OrdinalIgnoreCase).ToList(),
            antiCheat);
    }

    private DetectedDll Inspect(string root, string full, string name)
    {
        var version = FileVersion(full);
        var machine = PeMachine(full);
        string? skip = machine switch
        {
            PeMachineAmd64 => null,
            PeMachineI386 => "32-bit file. Exo has 64-bit files only.",
            _ => "Not a Windows x64 file.",
        };
        if (skip is null && name.Equals("nvngx_dlss.dll", StringComparison.OrdinalIgnoreCase) && IsDlssMajor1(version))
            skip = "DLSS 1 cannot be replaced.";
        if (skip is null && IsWindowsAppsPath(full))
            skip = StoreLockedMessage;

        string? liveHash = null;
        string LiveHash() => liveHash ??= FileSha256(full) ?? string.Empty;
        long length;
        try { length = new FileInfo(full).Length; }
        catch { length = -1; }

        string? matched = null;
        foreach (var pinned in ReleasesFor(name))
        {
            if (pinned.Size != length) continue;
            if (KnownSha(pinned) is { } known && known.Equals(LiveHash(), StringComparison.OrdinalIgnoreCase)) { matched = pinned.Release; break; }
        }

        var factory = PreferFactory(full);
        string? originalVersion = null;
        string? originalState = null;
        var origin = "shipped";
        if (factory is not null)
        {
            originalVersion = FileVersion(factory);
            originalState = PeMachine(factory) == machine && new FileInfo(factory).Length > 0 ? "saved" : "damaged";
            var sameAsOriginal = SameLengthAndHash(full, factory, LiveHash);
            var recorded = TryReadText(full + WrittenSuffix)?.Trim();
            var exoMarked = IsSha256(recorded) || File.Exists(full + StaleSuffix);
            if (sameAsOriginal) origin = exoMarked ? "reverted" : "shipped";
            else if (IsSha256(recorded) && recorded!.Equals(LiveHash(), StringComparison.OrdinalIgnoreCase) && !File.Exists(full + StaleSuffix)) origin = "exo";
            else if (exoMarked) origin = "changed";
            else origin = "other";
        }

        return new DetectedDll(
            full,
            Path.GetRelativePath(root, full),
            name,
            ClassifyKind(name),
            version,
            skip,
            origin,
            originalVersion,
            originalState,
            matched);
    }

    private static bool SameLengthAndHash(string live, string other, Func<string> liveHash)
    {
        try
        {
            if (new FileInfo(live).Length != new FileInfo(other).Length) return false;
            return liveHash().Equals(FileSha256(other), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static void Walk(string dir, List<string> files, ref string? antiCheat, int depth, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (depth > 16) return;
        IEnumerable<string> entries;
        try { entries = Directory.EnumerateFiles(dir).ToList(); }
        catch (Exception) when (depth > 0) { return; }
        foreach (var file in entries)
        {
            var name = Path.GetFileName(file);
            antiCheat ??= AntiCheatForFile(name);
            if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && IsDestName(name))
                files.Add(file);
        }

        List<string> children;
        try { children = Directory.EnumerateDirectories(dir).ToList(); }
        catch (Exception) when (depth > 0) { return; }
        foreach (var child in children)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var info = new DirectoryInfo(child);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (AntiCheatForFolder(info.Name) is { } found)
                {
                    antiCheat ??= found;
                    continue;
                }
                if (SkipScanFolderNames.Contains(info.Name))
                {
                    // No upscaler DLLs here, but an anti-cheat marker still counts.
                    antiCheat ??= ShallowAntiCheat(child);
                    continue;
                }
                Walk(child, files, ref antiCheat, depth + 1, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch { /* skip one dir */ }
        }
    }

    /// <summary>
    /// A game known by its exe can keep anti-cheat beside the folder Exo walks
    /// (Game\Binaries\Win64\game.exe next to Game\EasyAntiCheat). Look up to
    /// three folders above, one level deep, stopping at a store library.
    /// </summary>
    private static string? AncestorAntiCheat(string? gamePath, string root)
    {
        if (string.IsNullOrWhiteSpace(gamePath) || !File.Exists(gamePath)) return null;
        var dir = Directory.GetParent(root);
        for (var level = 0; level < 3 && dir is not null && dir.Parent is not null; level++, dir = dir.Parent)
        {
            if (dir.Name is "common" or "steamapps" or "Epic Games" or "GOG Games" or "XboxGames" or "Program Files" or "Program Files (x86)")
                break;
            if (ShallowAntiCheat(dir.FullName) is { } hit) return hit;
        }
        return null;
    }

    private static string? ShallowAntiCheat(string dir)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir))
                if (AntiCheatForFile(Path.GetFileName(file)) is { } hit) return hit;
            foreach (var sub in Directory.EnumerateDirectories(dir))
                if (AntiCheatForFolder(Path.GetFileName(sub)) is { } hit) return hit;
        }
        catch { /* an unreadable redist folder says nothing either way */ }
        return null;
    }

    // ------------------------------------------------------------------ names and versions

    internal static bool IsDestName(string fileName) =>
        DestSpecs.Any(spec => spec.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase));

    internal static bool IsSafeDllName(string fileName) => IsDestName(fileName);

    internal static bool IsFsr31File(string fileName) =>
        fileName.Equals("amd_fidelityfx_dx12.dll", StringComparison.OrdinalIgnoreCase) ||
        fileName.Equals("amd_fidelityfx_vk.dll", StringComparison.OrdinalIgnoreCase);

    internal static string ClassifyKind(string fileName) =>
        DestSpecs.FirstOrDefault(spec => spec.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase))?.Kind ?? "Other";

    internal static string DisplayName(string fileName) => fileName.ToLowerInvariant() switch
    {
        "nvngx_dlss.dll" => "DLSS Super Resolution",
        "nvngx_dlssg.dll" => "DLSS Frame Generation",
        "nvngx_dlssd.dll" => "DLSS Ray Reconstruction",
        "amd_fidelityfx_dx12.dll" => "FSR 3.1",
        "amd_fidelityfx_vk.dll" => "FSR 3.1",
        "amd_fidelityfx_loader_dx12.dll" => "FSR loader",
        "amd_fidelityfx_upscaler_dx12.dll" => "FSR Upscaler",
        "amd_fidelityfx_framegeneration_dx12.dll" => "FSR Frame Generation",
        "amd_fidelityfx_denoiser_dx12.dll" => "FSR Ray Regeneration",
        "amd_fidelityfx_radiancecache_dx12.dll" => "FSR Radiance Cache",
        "libxess.dll" => "XeSS Super Resolution",
        "libxess_dx11.dll" => "XeSS Super Resolution",
        "libxess_fg.dll" => "XeSS Frame Generation",
        "libxell.dll" => "XeSS Low Latency",
        _ => fileName,
    };

    internal static string? ApiName(string fileName) => fileName.ToLowerInvariant() switch
    {
        "amd_fidelityfx_dx12.dll" or "libxess.dll" => "DirectX 12",
        "amd_fidelityfx_vk.dll" => "Vulkan",
        "libxess_dx11.dll" => "DirectX 11",
        _ => null,
    };

    /// <summary>The version people know: a pinned file's own name, otherwise the file resource without a trailing .0.</summary>
    internal static string? DisplayVersion(DetectedDll dll)
    {
        if (dll.MatchedRelease is not null && FindPinned(dll.FileName, dll.MatchedRelease) is { } pinned)
            return pinned.Display;
        // FSR 3.1 is known by its release (3.1.2), not its loader's version (1.0.2.38022).
        if (IsFsr31File(dll.FileName) && TryParseVersion(dll.CurrentVersion) is { } version && KnownBuild(AllReleases, dll.FileName, version) is { } known)
            return known.Display;
        return TrimVersion(dll.CurrentVersion);
    }

    internal static string? TrimVersion(string? version)
    {
        if (TryParseVersion(version) is not { } parsed) return string.IsNullOrWhiteSpace(version) ? null : version;
        return parsed.Revision == 0 ? $"{parsed.Major}.{parsed.Minor}.{parsed.Build}" : parsed.ToString();
    }

    internal static string? FolderOf(string relativePath)
    {
        var folder = Path.GetDirectoryName(relativePath);
        return string.IsNullOrEmpty(folder) ? null : folder.Replace('\\', '/');
    }

    internal static bool IsDlssMajor1(string? version) =>
        !string.IsNullOrWhiteSpace(version) && version.Split('.')[0] == "1";

    /// <summary>
    /// Null when an automatic update may write; otherwise why it keeps the
    /// game's file. An older file never goes over a newer one. FSR 3.1 keeps a
    /// 1.0.x resource version while older games' FSR files say 2.0.x, so that
    /// exact family step counts as an upgrade.
    /// </summary>
    internal static string? SkipApplyReason(string fileName, string? destVersion, string? sourceVersion) =>
        SkipApplyReason(fileName, destVersion, sourceVersion, AllReleases);

    internal static string? SkipApplyReason(string fileName, string? destVersion, string? sourceVersion, IReadOnlyList<PinnedFile> releases)
    {
        var dest = TryParseVersion(destVersion);
        var source = TryParseVersion(sourceVersion);
        if (dest is null || source is null) return null;
        if (IsFsr31File(fileName))
        {
            if (dest.Major >= 2 && source.Major == 1) return null;
            if (dest.Major == 1 && source.Major >= 2) return KeptNewerMessage;
            // FSR 3.1's loader version does not follow its release (1.0.2.38022 is 3.1.2, older than 3.1.4 at
            // 1.0.1.41314): two builds Exo knows are compared by the release each is known by.
            if (Fsr31Release(releases, fileName, dest) is { } destRelease && Fsr31Release(releases, fileName, source) is { } sourceRelease)
                return destRelease > sourceRelease ? KeptNewerMessage : null;
        }
        return dest > source ? KeptNewerMessage : null;
    }

    /// <summary>The FSR 3.1 release (3.1.2) a known build of the file is, from the pins and the catalog's labels.</summary>
    private static Version? Fsr31Release(IReadOnlyList<PinnedFile> releases, string fileName, Version version) =>
        KnownBuild(releases, fileName, version) is { } known ? TryParseVersion(known.Display) : null;

    private static PinnedFile? KnownBuild(IReadOnlyList<PinnedFile> releases, string fileName, Version version) =>
        releases.FirstOrDefault(file => file.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase) && TryParseVersion(file.Version) == version);

    internal static int CompareVersions(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) && string.IsNullOrWhiteSpace(right)) return 0;
        if (string.IsNullOrWhiteSpace(left)) return -1;
        if (string.IsNullOrWhiteSpace(right)) return 1;
        return (TryParseVersion(left) ?? new Version(0, 0, 0, 0)).CompareTo(TryParseVersion(right) ?? new Version(0, 0, 0, 0));
    }

    /// <summary>
    /// Four numeric parts, or null when Exo cannot read the text as a version.
    /// Vendor shapes differ (DLSS 310.9.1.0, FSR 1.0.1.41314, XeSS 2.0.2.68),
    /// so every part is a number; any other part makes the whole unreadable.
    /// </summary>
    internal static Version? TryParseVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = value.Split('.', StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 4 || parts.Any(string.IsNullOrEmpty)) return null;
        var numbers = new int[4];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var part))
                return null;
            numbers[i] = part;
        }
        return new Version(numbers[0], numbers[1], numbers[2], numbers[3]);
    }

    /// <summary>Swapped in tests; production reads the file's own version resource.</summary>
    internal Func<string, string?> FileVersion { get; init; } = TryFileVersion;

    private static string? TryFileVersion(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            var info = FileVersionInfo.GetVersionInfo(path);
            if (info.FileMajorPart != 0 || info.FileMinorPart != 0 || info.FileBuildPart != 0 || info.FilePrivatePart != 0)
                return $"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}.{info.FilePrivatePart}";
            var raw = info.FileVersion?.Replace(',', '.').Replace(" ", "");
            return string.IsNullOrWhiteSpace(raw) ? null : raw;
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ provenance

    /// <summary>The PE machine field, or 0 when the file is not a readable PE image.</summary>
    internal static ushort PeMachine(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> dos = stackalloc byte[64];
            if (fs.Read(dos) < 64 || dos[0] != (byte)'M' || dos[1] != (byte)'Z') return 0;
            var peOff = BitConverter.ToInt32(dos.Slice(0x3C, 4));
            if (peOff < 0 || peOff > fs.Length - 6) return 0;
            fs.Position = peOff;
            Span<byte> pe = stackalloc byte[6];
            if (fs.Read(pe) < 6 || pe[0] != (byte)'P' || pe[1] != (byte)'E' || pe[2] != 0 || pe[3] != 0) return 0;
            return BitConverter.ToUInt16(pe.Slice(4, 2));
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Every file Exo writes is the vendor's: a valid Authenticode signature
    /// from the company that owns the file name, matching version-resource
    /// identity, the exports that API needs, and an x64 image. The pinned
    /// SHA-256 is checked on top of this by the caller.
    /// </summary>
    internal static bool IsTrustedReplacementDll(string path, string fileName) =>
        string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase) &&
        IsSafeDllName(fileName) &&
        PeMachine(path) == PeMachineAmd64 &&
        HasTrustedVendorSignature(path, fileName) &&
        HasCompatibleVendorIdentity(path, fileName) &&
        HasCompatibleExports(path, fileName) &&
        TryParseVersion(TryFileVersion(path)) is not null;

    internal static bool HasTrustedVendorSignature(string path, string fileName)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(path)) return false;
        var vendor = ExpectedVendor(fileName);
        if (vendor is null || !AuthenticodeVerifier.WinTrustValid(path)) return false;
        try
        {
#pragma warning disable SYSLIB0057 // WinTrust validates the embedded signature before reading its signer.
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            var signer = NormalizePublisher(certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false));
            return ExpectedPublisherNames(vendor).Contains(signer, StringComparer.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static string? ExpectedVendor(string fileName) => fileName.ToLowerInvariant() switch
    {
        var name when name.StartsWith("nvngx_", StringComparison.Ordinal) => "nvidia",
        var name when name.StartsWith("amd_fidelityfx_", StringComparison.Ordinal) => "amd",
        var name when name.StartsWith("libxess", StringComparison.Ordinal) || name == "libxell.dll" => "intel",
        _ => null,
    };

    private static IReadOnlyList<string> ExpectedPublisherNames(string vendor) => vendor switch
    {
        "nvidia" => ["nvidiacorporation"],
        "amd" => ["advancedmicrodevices"],
        "intel" => ["intelcorporation"],
        _ => [],
    };

    private static string NormalizePublisher(string? value) =>
        string.Concat((value ?? string.Empty).Where(char.IsLetterOrDigit)).ToLowerInvariant();

    private static bool HasCompatibleVendorIdentity(string path, string fileName)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return VendorMetadataMatches(fileName, info.OriginalFilename, info.CompanyName);
        }
        catch
        {
            return false;
        }
    }

    internal static bool VendorMetadataMatches(string fileName, string? originalFilename, string? companyName)
    {
        var vendor = ExpectedVendor(fileName);
        if (vendor is null) return false;
        if (!string.IsNullOrWhiteSpace(originalFilename))
        {
            var original = Path.GetFileName(originalFilename);
            var nvidiaBuildNumber = original.StartsWith("CL ", StringComparison.OrdinalIgnoreCase)
                ? original[3..].Trim()
                : string.Empty;
            var nvidiaBuildLabel = vendor == "nvidia" && nvidiaBuildNumber.Length > 0 && nvidiaBuildNumber.All(char.IsAsciiDigit);
            if (!original.Equals(fileName, StringComparison.OrdinalIgnoreCase) && !nvidiaBuildLabel)
                return false;
        }
        return string.IsNullOrWhiteSpace(companyName) ||
               ExpectedCompanyNames(vendor).Contains(NormalizePublisher(companyName), StringComparer.Ordinal);
    }

    private static IReadOnlyList<string> ExpectedCompanyNames(string vendor) => vendor switch
    {
        "nvidia" => ["nvidia", "nvidiacorporation"],
        "amd" => ["advancedmicrodevices", "advancedmicrodevicesinc"],
        "intel" => ["intelcorporation"],
        _ => [],
    };

    internal static bool ExportsMatchFileName(string fileName, IEnumerable<string> exportNames)
    {
        var exports = new HashSet<string>(exportNames, StringComparer.OrdinalIgnoreCase);
        if (fileName.StartsWith("nvngx_", StringComparison.OrdinalIgnoreCase))
            return exports.Contains("NVSDK_NGX_D3D12_Init") && exports.Contains("NVSDK_NGX_D3D12_CreateFeature");
        if (fileName.StartsWith("amd_fidelityfx_", StringComparison.OrdinalIgnoreCase))
            return new[] { "ffxCreateContext", "ffxDestroyContext", "ffxConfigure", "ffxQuery", "ffxDispatch" }.All(exports.Contains);
        if (fileName.Equals("libxess_fg.dll", StringComparison.OrdinalIgnoreCase))
            return exports.Contains("xefgSwapChainGetVersion") && exports.Contains("xefgSwapChainD3D12CreateContext");
        if (fileName.Equals("libxell.dll", StringComparison.OrdinalIgnoreCase))
            return exports.Contains("xellGetVersion") && exports.Contains("xellD3D12CreateContext");
        if (fileName.StartsWith("libxess", StringComparison.OrdinalIgnoreCase))
            return exports.Any(name => name.EndsWith("XessGetVersion", StringComparison.OrdinalIgnoreCase)) &&
                   exports.Any(name => name.EndsWith("XessCreateContext", StringComparison.OrdinalIgnoreCase));
        return false;
    }

    private static bool HasCompatibleExports(string path, string fileName)
    {
        var exports = ReadPeExportNames(path);
        return exports is not null && ExportsMatchFileName(fileName, exports);
    }

    private static IReadOnlyCollection<string>? ReadPeExportNames(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);
            if (stream.Length < 256) return null;

            stream.Position = 0x3c;
            var peOffset = reader.ReadInt32();
            if (peOffset < 0 || peOffset > stream.Length - 24) return null;
            stream.Position = peOffset;
            if (reader.ReadUInt32() != 0x00004550) return null;
            stream.Position = peOffset + 6;
            var sectionCount = reader.ReadUInt16();
            if (sectionCount is 0 or > 96) return null;
            stream.Position = peOffset + 20;
            var optionalSize = reader.ReadUInt16();
            var optionalStart = peOffset + 24L;
            if (optionalSize < 104 || optionalStart + optionalSize > stream.Length) return null;
            stream.Position = optionalStart;
            var magic = reader.ReadUInt16();
            var dataDirectoryOffset = optionalStart + (magic == 0x20b ? 112 : magic == 0x10b ? 96 : -1);
            if (dataDirectoryOffset < optionalStart || dataDirectoryOffset + 8 > optionalStart + optionalSize)
                return null;
            stream.Position = dataDirectoryOffset;
            var exportRva = reader.ReadUInt32();
            if (exportRva == 0) return null;

            var sections = new List<(uint VirtualAddress, uint Span, uint RawPointer)>();
            var sectionStart = optionalStart + optionalSize;
            for (var i = 0; i < sectionCount; i++)
            {
                var header = sectionStart + i * 40L;
                if (header + 40 > stream.Length) return null;
                stream.Position = header + 8;
                var virtualSize = reader.ReadUInt32();
                var virtualAddress = reader.ReadUInt32();
                var rawSize = reader.ReadUInt32();
                var rawPointer = reader.ReadUInt32();
                sections.Add((virtualAddress, Math.Max(virtualSize, rawSize), rawPointer));
            }

            long? OffsetOf(uint rva)
            {
                foreach (var section in sections)
                {
                    var end = (ulong)section.VirtualAddress + section.Span;
                    if (rva < section.VirtualAddress || (ulong)rva >= end) continue;
                    var offset = (ulong)section.RawPointer + (rva - section.VirtualAddress);
                    return offset < (ulong)stream.Length ? (long)offset : null;
                }
                return null;
            }

            var exportOffset = OffsetOf(exportRva);
            if (exportOffset is null || exportOffset.Value + 40 > stream.Length) return null;
            stream.Position = exportOffset.Value + 24;
            var nameCount = reader.ReadUInt32();
            stream.Position = exportOffset.Value + 32;
            var nameTableRva = reader.ReadUInt32();
            if (nameCount is 0 or > 4096) return null;
            var nameTableOffset = OffsetOf(nameTableRva);
            if (nameTableOffset is null || nameTableOffset.Value + nameCount * 4L > stream.Length) return null;

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0u; i < nameCount; i++)
            {
                stream.Position = nameTableOffset.Value + i * 4L;
                var nameOffset = OffsetOf(reader.ReadUInt32());
                if (nameOffset is null) return null;
                stream.Position = nameOffset.Value;
                var bytes = new List<byte>(64);
                while (bytes.Count < 512 && stream.Position < stream.Length)
                {
                    var value = reader.ReadByte();
                    if (value == 0) break;
                    if (value is < 0x20 or > 0x7e) return null;
                    bytes.Add(value);
                }
                if (bytes.Count == 0 || bytes.Count == 512) return null;
                names.Add(System.Text.Encoding.ASCII.GetString(bytes.ToArray()));
            }
            return names;
        }
        catch
        {
            return null;
        }
    }

    private static string CacheRoot => Path.Combine(PathHelper.AppDataDir, "dlss");

    // ------------------------------------------------------------------ paths and processes

    internal static string? ResolveInstallRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            if (Directory.Exists(path))
                return Path.GetFullPath(path);
            if (File.Exists(path))
            {
                var dir = Path.GetDirectoryName(path);
                if (string.IsNullOrWhiteSpace(dir)) return null;
                var executableDir = new DirectoryInfo(Path.GetFullPath(dir));
                // Imported Unreal games often point to Project/Binaries/Win64
                // instead of the store's install directory. Engine plugins are
                // siblings of Project, so scanning only beside the exe misses
                // their DLLs. Require the actual Unreal layout; never infer an
                // ancestor or another game from title words.
                if ((executableDir.Name.Equals("Win64", StringComparison.OrdinalIgnoreCase) ||
                     executableDir.Name.Equals("Win32", StringComparison.OrdinalIgnoreCase)) &&
                    executableDir.Parent is { Parent: { } project } binaries &&
                    binaries.Name.Equals("Binaries", StringComparison.OrdinalIgnoreCase) &&
                    Directory.Exists(Path.Combine(project.FullName, "Content")) &&
                    (project.Attributes & FileAttributes.ReparsePoint) == 0 &&
                    (binaries.Attributes & FileAttributes.ReparsePoint) == 0 &&
                    (executableDir.Attributes & FileAttributes.ReparsePoint) == 0)
                {
                    var install = project.Parent;
                    if (install is not null && Directory.Exists(Path.Combine(install.FullName, "Engine")))
                        return install.FullName;
                    return project.FullName;
                }
                return executableDir.FullName;
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static bool IsUnderRoot(string root, string candidate)
    {
        try
        {
            var left = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(candidate).StartsWith(left, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    internal static bool IsSafeGamePath(string root, string candidate)
    {
        if (!IsUnderRoot(root, candidate)) return false;
        try
        {
            var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var current = Path.GetFullPath(candidate);
            while (true)
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
                if (current.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)) return true;
                current = Path.GetDirectoryName(current)!;
                if (string.IsNullOrEmpty(current)) return false;
            }
        }
        catch { return false; }
    }

    internal static bool IsWindowsAppsPath(string path)
    {
        var hay = path.Replace('/', '\\');
        return hay.Contains("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase)
               || hay.Contains("\\ModifiableWindowsApps\\", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsStoreLockedDest(string dest)
    {
        if (IsWindowsAppsPath(dest)) return true;
        var xbox = dest.Replace('/', '\\').Contains("\\XboxGames\\", StringComparison.OrdinalIgnoreCase);
        return xbox && !ProbeWritable(dest);
    }

    /// <summary>
    /// Any process whose image lives under the install. The image path is read
    /// with PROCESS_QUERY_LIMITED_INFORMATION, which anti-cheat-protected game
    /// processes still grant; <see cref="Process.MainModule"/> needs VM_READ,
    /// which they deny, and would report a running protected game as closed.
    /// </summary>
    internal static bool IsGameRunning(GameEntry game, string? root = null)
    {
        root ??= ResolveInstallRoot(game.Path);
        if (string.IsNullOrWhiteSpace(root)) return false;
        try
        {
            var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    var exe = ProcessHelper.QueryFullProcessImageName(proc.Id) ?? proc.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(exe) &&
                        Path.GetFullPath(exe).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch { /* access denied / exited */ }
                finally { proc.Dispose(); }
            }
        }
        catch { /* process list unavailable */ }
        return false;
    }

    private static string MutationKeyFor(GameEntry game)
    {
        var root = ResolveInstallRoot(game.Path);
        return root is null
            ? "id:" + (game.Id ?? string.Empty).Trim()
            : "path:" + Path.TrimEndingDirectorySeparator(root);
    }

    private static MutationLease AcquireMutationGate(GameEntry game)
    {
        var key = MutationKeyFor(game);
        while (true)
        {
            var gate = MutationGates.GetOrAdd(key, static _ => new MutationGate());
            lock (gate.Lifecycle)
            {
                if (gate.Retired) continue;
                gate.Users++;
                return new MutationLease(key, gate);
            }
        }
    }

    // ------------------------------------------------------------------ backups and writes

    private static IReadOnlyDictionary<string, string> ChooseOriginalBackups(string root)
    {
        var chosen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        foreach (var suffix in OriginalBackupSuffixes)
        {
            IEnumerable<string> found;
            try { found = Directory.EnumerateFiles(root, "*" + suffix, options).ToList(); }
            catch { continue; }
            foreach (var backup in found)
            {
                var dest = backup[..^suffix.Length];
                if (!IsSafeDllName(Path.GetFileName(dest))) continue;
                if (!IsSafeGamePath(root, dest) || !IsSafeGamePath(root, backup)) continue;
                // .dlsss is the first-shipped copy; it wins over .exo-bak.
                if (!chosen.ContainsKey(dest) || suffix == SwapperBackupSuffix)
                    chosen[dest] = backup;
            }
        }
        return chosen;
    }

    private static bool HasOriginalBackup(string dest) =>
        File.Exists(dest + SwapperBackupSuffix) || File.Exists(dest + BackupSuffix);

    internal static bool HasFirstShippedBackup(string dest) =>
        !string.IsNullOrWhiteSpace(dest) && File.Exists(dest + SwapperBackupSuffix);

    internal static bool TryCaptureFirstShipped(string dest)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dest) || !File.Exists(dest)) return false;
            EnsureFactoryBackup(dest);
            return HasFirstShippedBackup(dest) && SameFileBytes(dest, dest + SwapperBackupSuffix);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Captures the shipped file before the first swap. An older Exo build kept
    /// it in .exo-bak alone; promote that, never the live file, which may
    /// already be an Exo write.
    /// </summary>
    private static void EnsureFactoryBackup(string dest)
    {
        var factory = dest + SwapperBackupSuffix;
        var backup = dest + BackupSuffix;
        if (!File.Exists(factory))
        {
            if (File.Exists(backup)) CopyVerified(backup, factory, overwrite: false);
            else if (File.Exists(dest)) CopyVerified(dest, factory, overwrite: false);
        }
        if (!File.Exists(backup) && File.Exists(factory))
            CopyVerified(factory, backup, overwrite: false);
    }

    /// <summary>
    /// A saved original lands whole or not at all: copied beside, flushed,
    /// checked byte for byte against its source, then moved into place, so a
    /// torn copy never becomes the game's "original".
    /// </summary>
    private static void CopyVerified(string from, string to, bool overwrite)
    {
        var temporary = to + ".exo-tmp";
        try
        {
            File.Copy(from, temporary, overwrite: true);
            using (var fs = new FileStream(temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                fs.Flush(flushToDisk: true);
            if (!SameFileBytes(temporary, from))
                throw new IOException(NoBackupMessage);
            File.Move(temporary, to, overwrite);
        }
        finally { TryDelete(temporary); }
    }

    private static void WriteMarker(string path, string text)
    {
        var temporary = path + ".exo-tmp";
        File.WriteAllText(temporary, text);
        File.Move(temporary, path, overwrite: true);
    }

    private static string? PreferFactory(string dest)
    {
        var factory = dest + SwapperBackupSuffix;
        if (File.Exists(factory)) return factory;
        var backup = dest + BackupSuffix;
        return File.Exists(backup) ? backup : null;
    }

    /// <summary>
    /// Restore is available for a captured original unless Exo has positively
    /// observed another writer replace the live bytes. Legacy DLSS Swapper
    /// backups have no Exo hash marker, so its absence alone is not foreign.
    /// </summary>
    internal static bool HasValidRestoreClaim(string dest)
    {
        if (!File.Exists(dest) || !HasOriginalBackup(dest) || File.Exists(dest + StaleSuffix))
            return false;
        return !File.Exists(dest + WrittenSuffix) || HasMatchingWrittenHash(dest);
    }

    private static bool HasMatchingWrittenHash(string dest)
    {
        var live = FileSha256(dest);
        var recorded = TryReadText(dest + WrittenSuffix)?.Trim();
        return !File.Exists(dest + StaleSuffix) && IsSha256(recorded) &&
               live is not null && live.Equals(recorded, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Something other than Exo wrote this file since Exo's write, so Exo drops
    /// its claim on the live bytes. The saved original is left alone.
    /// </summary>
    internal static void InvalidateForeignWrite(string dest)
    {
        var writtenPath = dest + WrittenSuffix;
        if (!File.Exists(dest) || !File.Exists(writtenPath)) return;
        var live = FileSha256(dest);
        var recorded = TryReadText(writtenPath);
        if (string.IsNullOrWhiteSpace(live) || string.IsNullOrWhiteSpace(recorded)) return;
        if (live.Equals(recorded.Trim(), StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            File.WriteAllText(dest + StaleSuffix, live);
            TryDelete(writtenPath);
        }
        catch (Exception ex) { AppLog.Debug("Upscaler stale marker failed for " + dest + ": " + ex.Message); }
    }

    /// <param name="sha">The vendor file's pinned SHA-256: the marker names it
    /// rather than a re-read of the written file, which can fail and leave
    /// the old marker naming other bytes.</param>
    internal static void WriteWithBackup(string destination, string source, string? sha)
    {
        if (!IsSha256(sha))
            throw new IOException("Exo could not verify the vendor's file.");
        var dest = Path.GetFullPath(destination);
        if (!File.Exists(dest))
            throw new IOException("The file is no longer there.");
        if (IsStoreLockedDest(dest))
            throw new UnauthorizedAccessException(StoreLockedMessage);
        ClearReadOnly(dest);
        if (!ProbeWritable(dest))
            throw new UnauthorizedAccessException(StoreLockedMessage);
        if (!HasFirstShippedBackup(dest))
            throw new InvalidOperationException(NoBackupMessage);
        if (!File.Exists(dest + BackupSuffix))
            CopyVerified(dest + SwapperBackupSuffix, dest + BackupSuffix, overwrite: false);
        ReplaceExisting(dest, source);
        WriteMarker(dest + WrittenSuffix, sha!);
        TryDelete(dest + StaleSuffix);
    }

    private static void RollbackDest(string dest)
    {
        var factory = PreferFactory(dest);
        if (factory is null) return;
        try { ReplaceExisting(dest, factory); }
        catch (Exception ex) { AppLog.Warn("Upscaler rollback failed for " + dest + ": " + ex.Message); }
    }

    private static bool SameFileBytes(string left, string right)
    {
        try
        {
            if (new FileInfo(left).Length != new FileInfo(right).Length) return false;
            var a = FileSha256(left);
            return a is not null && a.Equals(FileSha256(right), StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<string> FindSidecars(string root)
    {
        try
        {
            return Directory.EnumerateFiles(root, SidecarName, new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                })
                .Where(path => IsSafeGamePath(root, path))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static UpscaleSidecar? ReadSidecar(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<UpscaleSidecar>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return null;
        }
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static string? TryReadText(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch { return null; }
    }

    private static void ClearReadOnly(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            var attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
        }
        catch { /* best-effort */ }
    }

    internal static bool ProbeWritable(string path)
    {
        if (!File.Exists(path)) return false;
        var handle = CreateFileW(path, GenericWrite, FileShareRead, 0, OpenExisting, 0, 0);
        if (handle == nint.Zero || handle == new nint(-1)) return false;
        CloseHandle(handle);
        return true;
    }

    private static void ReplaceExisting(string dest, string source)
    {
        var staged = dest + NewSuffix;
        try
        {
            File.Copy(source, staged, overwrite: true);
            using (var fs = new FileStream(staged, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                fs.Flush(flushToDisk: true);
            ClearReadOnly(dest);
            if (!ReplaceFileW(dest, staged, null, 0, 0, 0))
                File.Replace(staged, dest, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        catch (IOException ex) when (IsSharingViolation(ex))
        {
            throw new IOException(GameRunningMessage, ex);
        }
        catch (UnauthorizedAccessException)
        {
            throw new UnauthorizedAccessException(StoreLockedMessage);
        }
        finally
        {
            TryDelete(staged);
        }
    }

    private static string FailMessage(Exception ex)
    {
        if (IsSharingViolation(ex) || ex.Message.Contains("Close the game", StringComparison.OrdinalIgnoreCase))
            return GameRunningMessage;
        if (ex is UnauthorizedAccessException || IsAccessDenied(ex))
            return StoreLockedMessage;
        return ex.Message;
    }

    private static bool IsSharingViolation(Exception ex)
    {
        for (var cur = ex; cur is not null; cur = cur.InnerException)
        {
            if (cur is IOException io && (io.HResult & 0xFFFF) is 32) return true;
            if (cur.Message.Contains("being used by another process", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static bool IsAccessDenied(Exception ex)
    {
        for (var cur = ex; cur is not null; cur = cur.InnerException)
        {
            if (cur is UnauthorizedAccessException) return true;
            if (cur is IOException io && (io.HResult & 0xFFFF) == 5) return true;
        }
        return false;
    }

    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint OpenExisting = 3;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ReplaceFileW(
        string lpReplacedFileName,
        string lpReplacementFileName,
        string? lpBackupFileName,
        uint dwReplaceFlags,
        nint lpExclude,
        nint lpReserved);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        nint lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        nint hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint hObject);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort */ }
    }
}
