using System.Collections.Concurrent;
using System.Diagnostics;
using ExoLauncher.Adapters.Cli;
using ExoLauncher.Helpers;
using ExoLauncher.Models;
using ExoLauncher.Services;

namespace ExoLauncher.Adapters;

/// <summary>
/// Steam library via appmanifest + minimized install/launch.
/// Steam runtime usually remains installed; user should not need to open Steam day-to-day.
/// Anonymous SteamCMD is NOT used for owned paid games.
/// </summary>
public sealed class SteamAdapter : IStoreAdapter, IInstalledSteamManifestSource, IStoreAccountScope, IAuthoritativeOwnershipSource, IOwnedPlaytimeSource, IStoreRepair
{
    private readonly ConcurrentDictionary<string, InstallProgress> _progress = new(StringComparer.OrdinalIgnoreCase);
    private static int _staleCleanupScheduled;

    public IReadOnlySet<string>? LastAuthoritativeOwnedAppIds { get; private set; }
    public IReadOnlyDictionary<string, int>? LastOwnedPlaytimeMinutes { get; private set; }

    /// <summary>Local appinfo (name, type, parent) from the last scan; types catalog-restored rows.</summary>
    internal IReadOnlyDictionary<string, SteamAppInfoNames.Entry>? LastAppInfo { get; private set; }

    public StoreKind Store => StoreKind.Steam;
    public string Id => "steam";
    public string DisplayName => "Steam";

    public string? GetActiveAccountScope()
    {
        var root = ResolveSteamRoot();
        return root is null ? null : SteamPlaytime.GetActiveAccountScope(root);
    }

    public bool IsAgentPresent() => ResolveSteamExe() is not null;

    /// <summary>
    /// Steam signs in only in its own login window; Exo never collects a Steam
    /// password. The host opens Steam (revealing its window) for a handoff.
    /// </summary>
    public Task<AuthResult> AuthenticateAsync(CancellationToken ct = default) =>
        Task.FromResult(ResolveSteamExe() is null
            ? new AuthResult { Ok = false, RequiresUserAction = true, Message = "Install Steam first." }
            : AuthResult.SignInInClient("Steam"));

    public async Task<IReadOnlyList<GameEntry>> GetLibraryAsync(CancellationToken ct = default)
    {
        var games = new List<GameEntry>();
        var steamRoot = ResolveSteamRoot();
        if (steamRoot is null)
        {
            LastAuthoritativeOwnedAppIds = null;
            LastOwnedPlaytimeMinutes = null;
            return games;
        }
        // Leftover download cleanup walks large trees. Never block the library
        // scan — a cancelled 50 GB leftover used to trip the 25s adapter timeout.
        ScheduleStaleDownloadCleanup(steamRoot);
        var activeAccount = SteamPlaytime.LoadActiveAccount(steamRoot);
        // Ownership below is the active account's; only hours may span accounts.
        var hours = SteamPlaytime.LoadHours(steamRoot, SettingsService.CountHoursAcrossAccounts);
        IReadOnlySet<string>? authoritativeOwnedAppIds = null;
        IReadOnlySet<string>? previouslyVerified = null;
        IReadOnlySet<string>? webApiOwned = null;
        IReadOnlyDictionary<string, string>? ownedResultNames = null;
        var ownershipSource = "unavailable";
        var apiKey = SteamWebApiKeyStore.TryRead();
        var steamId64 = SteamSelfIdentity.LoadSelfSteamId64(steamRoot);
        // Set once, after the Web API answers: another scan of this adapter (an action's
        // revalidation) reading it meanwhile must see the last answer, not a blank one.
        IReadOnlyDictionary<string, int>? ownedPlaytime = null;
        if (apiKey is not null && steamId64 is not null)
        {
            var ownedResult = await SteamWebApi.LoadOwnedGamesAsync(apiKey, steamId64, ct).ConfigureAwait(false);
            if (ownedResult.Authoritative)
            {
                webApiOwned = ownedResult.AppIds;
                ownedResultNames = ownedResult.Names.Count > 0 ? ownedResult.Names : null;
                ownedPlaytime = ownedResult.PlaytimeMinutes.Count > 0
                    ? ownedResult.PlaytimeMinutes
                    : null;
            }
        }
        LastOwnedPlaytimeMinutes = ownedPlaytime;
        var presentAppIds = new HashSet<string>(StringComparer.Ordinal);
        var appNames = new Dictionary<string, SteamAppInfoNames.Entry>(StringComparer.Ordinal);
        foreach (var pair in SteamAppInfoNames.Load(Path.Combine(steamRoot, "appcache", "appinfo.vdf")))
            appNames[pair.Key] = pair.Value;
        LastAppInfo = appNames;
        // The running Steam client is the complete license list. The Web API
        // leaves out free and family licences, so with a key it only adds to
        // the client's answer and never replaces it.
        var candidates = CollectOwnershipCandidates(steamRoot, activeAccount, appNames);
        if (webApiOwned is not null)
            candidates.AddRange(webApiOwned.Where(id => !candidates.Contains(id)));
        var clientOwned = await QueryClientOwnershipAsync(
            GetActiveAccountScope(), candidates, ct).ConfigureAwait(false);
        if (clientOwned is not null)
        {
            authoritativeOwnedAppIds = webApiOwned is null
                ? clientOwned
                : clientOwned.Union(webApiOwned).ToHashSet(StringComparer.Ordinal);
            ownershipSource = webApiOwned is null ? "steam-client" : "steam-client+webapi";
        }
        else
        {
            // Steam is closed or still signing in, and Exo never starts it to
            // ask. Until the client answers, games Exo verified before stay
            // owned: a Web API-only answer must not prune free or family titles.
            var known = (ExtraOwnershipCandidates?.Invoke() ?? []).ToHashSet(StringComparer.Ordinal);
            if (webApiOwned is not null)
            {
                authoritativeOwnedAppIds = webApiOwned.Union(known).ToHashSet(StringComparer.Ordinal);
                ownershipSource = "webapi+previously-verified";
            }
            else if (known.Count > 0)
            {
                // Not authoritative (nothing is written to or pruned from the
                // catalog): installed titles verified before read as owned,
                // the rest stay unverified until Steam answers.
                previouslyVerified = known;
                ownershipSource = "previously-verified";
            }
            if (SteamClientReady.WatchIfRunning(candidates))
                ownershipSource += " (waiting for Steam)";
        }
        var familyMembers = activeAccount?.FamilyMemberIds;
        var inFamily = familyMembers is { Count: > 0 };
        var familyAppIds = SteamAccountLibrary.FamilyAppIds(inFamily, clientOwned, webApiOwned);
        var accountScope = GetActiveAccountScope();
        if (clientOwned is not null && webApiOwned is not null) SteamFamilyShares.Remember(accountScope, familyAppIds);
        else if (clientOwned is null && inFamily && authoritativeOwnedAppIds is not null && SteamFamilyShares.Recall(accountScope) is { Count: > 0 } shared)
        {
            // Steam is closed (Exo closes it when idle): the family's games as it last listed them, less any bought since.
            familyAppIds = shared.Where(id => webApiOwned?.Contains(id) != true).ToHashSet(StringComparer.Ordinal);
            authoritativeOwnedAppIds = authoritativeOwnedAppIds.Union(familyAppIds).ToHashSet(StringComparer.Ordinal);
            ownershipSource += "+family-remembered";
        }
        // The ownership catalog remembers (and prunes to) the account's own
        // titles: a family share is someone else's license.
        LastAuthoritativeOwnedAppIds = familyAppIds.Count == 0 || authoritativeOwnedAppIds is null
            ? authoritativeOwnedAppIds
            : authoritativeOwnedAppIds.Where(id => !familyAppIds.Contains(id)).ToHashSet(StringComparer.Ordinal);
        AppLog.Info($"Steam ownership source={ownershipSource}; owned={authoritativeOwnedAppIds?.Count.ToString() ?? "n/a"}; family={familyAppIds.Count}.");
        if (ownedResultNames is not null)
        {
            // An owned app appinfo has never seen gets its type from the Steam
            // store; until the store answers it stays untyped and off the shelf.
            var unseen = ownedResultNames
                .Where(pair => !appNames.ContainsKey(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value) &&
                               authoritativeOwnedAppIds?.Contains(pair.Key) == true)
                .Select(pair => pair.Key)
                .ToList();
            IReadOnlyDictionary<string, string> storeTypes = unseen.Count == 0
                ? new Dictionary<string, string>()
                : await SteamStoreAppTypes.ResolveAsync(unseen, ct).ConfigureAwait(false);
            foreach (var pair in ownedResultNames)
            {
                if (appNames.ContainsKey(pair.Key) || string.IsNullOrWhiteSpace(pair.Value))
                    continue;
                appNames[pair.Key] = new SteamAppInfoNames.Entry(pair.Value.Trim(), storeTypes.GetValueOrDefault(pair.Key, ""));
            }
        }

        foreach (var lib in CollectLibraryFolders(steamRoot))
        {
            ct.ThrowIfCancellationRequested();
            var steamApps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(steamApps)) continue;

            foreach (var acf in Directory.EnumerateFiles(steamApps, "appmanifest_*.acf"))
            {
                try
                {
                    var fields = SteamProtocol.ParseAcfFields(File.ReadAllText(acf));
                    if (!SteamProtocol.TryParseAppManifest(fields, out var appId, out var name, out var installDir, out var size)
                        || appId is null || name is null)
                        continue;

                    var path = string.IsNullOrWhiteSpace(installDir)
                        ? null
                        : Path.Combine(steamApps, "common", installDir);
                    var pathExists = path is not null && Directory.Exists(path);

                    // Hide redistributables / runtimes. Demos never become
                    // tiles. Tools and soundtracks stay until LibraryService
                    // applies Show tools.
                    if (IsAlwaysHiddenSteamEntry(appId, name, installDir))
                        continue;

                    string? steamType = null;
                    string? parentAppId = null;
                    if (appNames.TryGetValue(appId, out var appInfo))
                    {
                        if (!string.IsNullOrWhiteSpace(appInfo.Type))
                            steamType = appInfo.Type;
                        if (!string.IsNullOrWhiteSpace(appInfo.ParentAppId))
                            parentAppId = appInfo.ParentAppId;
                    }

                    // StateFlags is a bitfield — do not compare to the string "4".
                    var stateFlags = fields.GetValueOrDefault("StateFlags");
                    var installed = SteamStateFlags.IsInstalledPresence(pathExists, stateFlags, fields.GetValueOrDefault("buildid"));
                    if (!CatalogHygiene.KeepSteamType(steamType, showTools: true, installed: installed))
                        continue;
                    long? bytesToDownload = null;
                    long? bytesDownloaded = null;
                    if (long.TryParse(fields.GetValueOrDefault("BytesToDownload"), out var btd) && btd > 0)
                        bytesToDownload = btd;
                    if (long.TryParse(fields.GetValueOrDefault("BytesDownloaded"), out var bd) && bd >= 0)
                        bytesDownloaded = bd;
                    var buildId = fields.GetValueOrDefault("buildid");
                    var targetBuildId = fields.GetValueOrDefault("TargetBuildID");
                    var updateAvailable = SteamStateFlags.IsUpdateAvailable(
                        stateFlags, installed, bytesToDownload, bytesDownloaded) ||
                        SteamStateFlags.HasPendingTargetBuild(buildId, targetBuildId);
                    var play = hours.TryGetValue(appId, out var accountPlay)
                        ? accountPlay
                        : (SteamPlaytime.Entry?)null;
                    // App manifests, localconfig entries, app tickets, and
                    // librarycache files can all outlive a refund. They prove
                    // installation/history, not the current account's license.
                    var ownedByActiveAccount = authoritativeOwnedAppIds?.Contains(appId) ?? previouslyVerified?.Contains(appId) == true;
                    var ownershipVerified = authoritativeOwnedAppIds is not null || ownedByActiveAccount;
                    var lastOwner = fields.GetValueOrDefault("LastOwner");
                    // Owned wins: a title on the account's own Web API list is
                    // never a family share, whoever downloaded it here.
                    var familyShared = webApiOwned?.Contains(appId) != true &&
                                       (familyAppIds.Contains(appId) ||
                                        SteamLibraryExtras.ManifestLooksFamilyShared(fields, steamId64) ||
                                        SteamAccountLibrary.LastOwnerIsFamilyMember(lastOwner, steamId64, familyMembers));
                    if (!familyShared && installed && !ownedByActiveAccount && !string.IsNullOrWhiteSpace(steamId64) &&
                        !string.IsNullOrWhiteSpace(lastOwner) &&
                        !lastOwner.Equals(steamId64, StringComparison.OrdinalIgnoreCase))
                        familyShared = true;
                    var playable = ownedByActiveAccount || familyShared;
                    var entitlementStatus = !ownershipVerified && !familyShared
                        ? "Ownership unverified"
                        : playable
                            ? (installed ? (updateAvailable ? "Update" : "Ready") : "Not installed")
                            : "Buy again";

                    presentAppIds.Add(appId);
                    games.Add(new GameEntry
                    {
                        Id = "steam:" + appId,
                        Title = name,
                        Store = StoreKind.Steam,
                        Installed = installed,
                        Owned = playable,
                        FamilyShared = familyShared,
                        EntitlementState = !ownershipVerified && !familyShared
                            ? EntitlementState.Unverified
                            : playable
                                ? EntitlementState.Owned
                                : EntitlementState.NotOwned,
                        CanInstall = !installed && playable,
                        // The manifest is this PC's own word that Steam wants an update. Only a license
                        // known to be gone hides it; Steam closed (no answer on ownership) does not.
                        UpdateAvailable = updateAvailable && (playable || !ownershipVerified),
                        Path = path,
                        LaunchTarget = appId,
                        ParentAppId = parentAppId,
                        SteamAppType = steamType,
                        // Native CoverArtService resolves official Steam art into its cache.
                        CoverUrl = null,
                        SizeBytes = size,
                        PlaytimeMinutes = play is { Minutes: > 0 } ? play.Value.Minutes : null,
                        LastPlayedUtc = play?.LastPlayedUtc,
                        Status = entitlementStatus,
                        Deps = new[] { "Steam client" },
                        LaunchNote = familyShared
                            ? "Shared through Steam Family. Launches through Steam."
                            : !ownershipVerified
                                ? "Installed files found. Ownership is unverified for the active Steam account."
                                : ownedByActiveAccount
                                    ? "Ownership verified for this Steam account. Launches through Steam."
                                    : "Installed files found, but this Steam account does not currently own the game. Buy it again through Steam.",
                    });
                }
                catch { /* skip corrupt manifests */ }
            }
        }

        // appinfo names/types are a local index for owned-not-installed rows
        // and for hygiene on installed ACF rows. GetOwnedGames names never
        // invent type=game: unknown ids stay empty-type and drop until
        // appinfo or the Steam store supplies a real game type.
        var ownedIds = authoritativeOwnedAppIds is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : authoritativeOwnedAppIds.ToHashSet(StringComparer.Ordinal);
        foreach (var extra in SteamAccountLibrary.UninstalledOwnedGames(
                     ownedIds, presentAppIds, appNames, authoritativeOwnedAppIds, familyAppIds))
        {
            if (IsAlwaysHiddenSteamEntry(extra.LaunchTarget ?? "", extra.Title, null))
                continue;
            games.Add(extra);
        }

        return games;
    }

    /// <summary>
    /// Extra app ids to re-verify through the Steam client (LibraryService
    /// supplies its last-verified ownership catalog), so a catalog title whose
    /// manifest and cache files are gone is still re-checked, not pruned.
    /// </summary>
    internal Func<IEnumerable<string>>? ExtraOwnershipCandidates { get; set; }

    private static readonly TimeSpan ClientOwnershipTtl = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ClientOwnershipRetry = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _clientOwnershipGate = new(1, 1);
    private string _clientOwnershipKey = "";
    private DateTimeOffset _clientOwnershipAt = DateTimeOffset.MinValue;
    private IReadOnlySet<string>? _clientOwnership;
    private bool _clientOwnershipPartial;

    /// <summary>
    /// A partial answer prunes nothing it did not check: titles Exo verified
    /// before stay owned until Steam answers for them.
    /// </summary>
    internal static IReadOnlySet<string> WithPreviouslyVerified(
        SteamClientIpc.OwnedAnswer answer,
        Func<IEnumerable<string>>? previouslyVerified)
    {
        if (answer.Unchecked.Count == 0) return answer.Owned;
        IEnumerable<string> known;
        try { known = previouslyVerified?.Invoke() ?? []; }
        catch { known = []; }
        var pending = answer.Unchecked.ToHashSet(StringComparer.Ordinal);
        var result = answer.Owned.ToHashSet(StringComparer.Ordinal);
        result.UnionWith(known.Where(pending.Contains));
        return result;
    }

    /// <summary>
    /// Candidate ids for the Steam-client license check: every installed
    /// manifest (any type — an installed tool must not flip to "Buy again"),
    /// plus game ids from localconfig, the account's librarycache,
    /// appinfo.vdf, and the verified catalog. Uninstalled candidates must pass
    /// <see cref="CatalogHygiene.IsOwnedUninstalledSteamGame"/> so DLC, tools,
    /// soundtracks and companion apps never become a row. packageinfo.vdf is not read: appinfo already carries
    /// every app the client has licensed metadata for.
    /// </summary>
    internal List<string> CollectOwnershipCandidates(
        string steamRoot,
        SteamPlaytime.AccountSnapshot? activeAccount,
        IReadOnlyDictionary<string, SteamAppInfoNames.Entry> appNames)
    {
        var ordered = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string? id, bool installed)
        {
            if (!Cli.SteamProtocol.IsValidAppId(id) || seen.Contains(id)) return;
            if (!installed && (!appNames.TryGetValue(id, out var info) ||
                               !CatalogHygiene.IsOwnedUninstalledSteamGame(id, info.Name, info.Type, info.ParentAppId)))
                return;
            seen.Add(id);
            ordered.Add(id);
        }

        foreach (var lib in CollectLibraryFolders(steamRoot))
        {
            try
            {
                var steamApps = Path.Combine(lib, "steamapps");
                if (!Directory.Exists(steamApps)) continue;
                foreach (var acf in Directory.EnumerateFiles(steamApps, "appmanifest_*.acf"))
                {
                    var name = Path.GetFileNameWithoutExtension(acf);
                    Add(name["appmanifest_".Length..], installed: true);
                }
            }
            catch { /* one library failed */ }
        }

        if (activeAccount is not null)
        {
            foreach (var id in activeAccount.Entries.Keys) Add(id, installed: false);
            foreach (var id in activeAccount.AppTicketIds) Add(id, installed: false);
        }

        var localConfig = SteamPlaytime.TryGetLocalConfigPath(steamRoot);
        if (localConfig is not null)
        {
            try
            {
                foreach (var id in SteamAccountLibrary.ListCacheAppIds(
                             Path.Combine(Path.GetDirectoryName(localConfig)!, "librarycache")))
                    Add(id, installed: false);
            }
            catch { /* cache is a hint only */ }
        }

        try
        {
            if (ExtraOwnershipCandidates is { } extra)
                foreach (var id in extra()) Add(id, installed: false);
        }
        catch { /* catalog is a hint only */ }

        foreach (var id in appNames.Keys) Add(id, installed: false);
        return ordered;
    }

    /// <summary>Forget the cached client answer so the next scan asks Steam again.</summary>
    internal void InvalidateClientOwnership()
    {
        _clientOwnershipKey = "";
        _clientOwnershipAt = DateTimeOffset.MinValue;
        _clientOwnership = null;
    }

    private async Task<IReadOnlySet<string>?> QueryClientOwnershipAsync(
        string? accountScope,
        IReadOnlyList<string> candidates,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(accountScope) || candidates.Count == 0) return null;
        // A closed Steam cannot answer; asking would only start the IPC helper for nothing.
        if (!ProcessHelper.IsProcessRunning("steam")) return null;
        var key = accountScope + ":" + candidates.Count + ":" +
                  string.Join(',', candidates).GetHashCode(StringComparison.Ordinal);
        await _clientOwnershipGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var age = DateTimeOffset.UtcNow - _clientOwnershipAt;
            if (string.Equals(key, _clientOwnershipKey, StringComparison.Ordinal) &&
                age < (_clientOwnership is null || _clientOwnershipPartial ? ClientOwnershipRetry : ClientOwnershipTtl))
                return _clientOwnership;
            var answer = await SteamClientIpc.QueryOwnedAsync(candidates, ct).ConfigureAwait(false);
            var result = answer is null ? null : WithPreviouslyVerified(answer, ExtraOwnershipCandidates);
            _clientOwnershipKey = key;
            _clientOwnershipAt = DateTimeOffset.UtcNow;
            _clientOwnership = result;
            _clientOwnershipPartial = answer is { Unchecked.Count: > 0 };
            return result;
        }
        finally
        {
            _clientOwnershipGate.Release();
        }
    }

    private static void ScheduleStaleDownloadCleanup(string steamRoot)
    {
        if (Interlocked.CompareExchange(ref _staleCleanupScheduled, 1, 0) != 0) return;
        _ = Task.Run(() =>
        {
            try { Services.SteamLeftoverCleanup.CleanStale(steamRoot); }
            catch { /* leftover folders are best-effort */ }
            finally { Interlocked.Exchange(ref _staleCleanupScheduled, 0); }
        });
    }

    /// <summary>
    /// Redistributables and runtimes that are never a library tile.
    /// </summary>
    internal static bool IsAlwaysHiddenSteamEntry(string appId, string name, string? installDir)
    {
        _ = installDir;
        if (appId is "228980" // Steamworks Common Redistributables
            or "1070560" // Steam Linux Runtime
            or "1391110" or "1493710" or "1628350" // Proton / Steam Linux runtimes
            or "2180100" or "2805730")
            return true;

        var n = (name ?? "").Trim();
        if (n.Length == 0) return true;
        var lower = n.ToLowerInvariant();
        string[] junk =
        [
            "steamworks common redistributables",
            "steamworks redistributable",
            "redistributable",
            "directx redistributable",
            "visual c++",
            "microsoft visual c",
            "proton ",
            "steam linux runtime",
            "steamworks sdk",
            "software development kit",
        ];
        foreach (var j in junk)
        {
            if (lower.Contains(j, StringComparison.Ordinal)) return true;
        }

        var dir = (installDir ?? "").ToLowerInvariant();
        return dir.Contains("steamworks shared", StringComparison.Ordinal) ||
               (dir.Contains("steamworks", StringComparison.Ordinal) && dir.Contains("redist", StringComparison.Ordinal));
    }

    /// <summary>
    /// Steam library includes tools and redistributables that are not playable titles.
    /// </summary>
    internal static bool IsNonGameSteamEntry(string appId, string name, string? installDir)
    {
        if (IsAlwaysHiddenSteamEntry(appId, name, installDir)) return true;
        if (CatalogHygiene.IsDemoTitle(name)) return true;
        return CatalogHygiene.IsDedicatedServerTitle(name);
    }

    public async Task<InstallResult> InstallAsync(
        GameEntry game,
        string? installPath,
        IProgress<InstallProgress>? progress,
        CancellationToken ct = default)
    {
        // installPath is ignored for Steam — library folders are managed by the Steam client.
        _ = installPath;
        var appId = game.LaunchTarget;
        if (!SteamProtocol.IsValidAppId(appId))
            return new InstallResult { Ok = false, Message = "Missing or invalid Steam app id." };

        var steamExe = ResolveSteamExe();
        if (steamExe is null)
            return new InstallResult { Ok = false, Message = "Steam is not installed." };

        var sink = Track(progress);
        Report(game.Id, sink, InstallPhase.Preparing, null, "Starting Steam…");

        // Cancel may only undo what this request created. A manifest that already
        // exists is the user's install (a paused or partially downloaded game
        // still has one), and uninstalling it on cancel deleted real files.
        var appManifestExistedBeforeRequest = FindAppManifestPath(appId) is not null;

        using var hider = StoreWindowHider.ForSteam();

        try
        {
            hider.Start(TimeSpan.FromMinutes(90));
            if (!ProcessHelper.IsProcessRunning("steam"))
            {
                ProcessHelper.StartHidden(
                    steamExe,
                    SteamUpdateCommandPlan.HiddenClientStartArguments());
                await WaitForSteamCommandListenerAsync(ct).ConfigureAwait(false);
            }

            Report(game.Id, sink, InstallPhase.Downloading, null,
                "Starting Steam install…");
            using var gameEula = SteamGameEulaPromptAutomator.Arm(
                steamExe, game.Title, TimeSpan.FromMinutes(12), ct);
            using var progressWatcher = SteamClientIpc.StartProgressWatcher(appId, game.Id, sink, install: true);
            if (await CommandSteamIpcAsync("install", appId, ct).ConfigureAwait(false) != SteamIpcStatus.Ok)
                NudgeSteamUpdate(steamExe, game, appId);

            var start = DateTimeOffset.UtcNow;
            var sawManifest = false;
            // One follow-up request per click if Steam has not written the
            // manifest 20 s in (a cold client can drop the first); never a
            // steam:// launch every 20 s for the whole two-hour wait.
            var followUpAt = start.AddSeconds(20);
            var followedUp = false;
            long lastSize = 0;
            var stableTicks = 0;
            var steamRoot = ResolveSteamRoot();
            var baseline = ReadAppManifestSnapshot(appId);
            var sampler = new SteamWatchSampler();
            // A big game can take hours. Give up only when Steam never takes
            // the request, or its download stops moving for two hours.
            var lastMovedAt = start;
            long? lastMoved = null;

            while (!ct.IsCancellationRequested)
            {
                var now = DateTimeOffset.UtcNow;
                var elapsed = (now - start).TotalSeconds;
                var hit = FindInstalled(appId);
                var snap = ReadAppManifestSnapshot(appId);
                var transfer = sampler.ReadTransfer(appId, steamRoot, snap, baseline);
                if (hit is not null || snap.StateFlags is not null || transfer.ToDownload is not null)
                    sawManifest = true;
                var moved = transfer.Downloaded ?? snap.BytesDownloaded;
                if (moved != lastMoved)
                {
                    lastMoved = moved;
                    lastMovedAt = now;
                }

                var size = hit is not null ? sampler.ReadInstalledSize(hit.Value.Path) : 0L;
                progressWatcher.ReportFallback(() =>
                {
                    var hasDownload = transfer.ToDownload is > 0 || snap.BytesToDownload is > 0;
                    var liveBytes = transfer.ToDownload is > 0 &&
                                    transfer.Downloaded is not null &&
                                    transfer.Percent is > 0;
                    var stalled = liveBytes ? null
                        : ((snap.BytesDownloaded ?? 0) == 0 ? SteamDownloadError(snap.UpdateResult) : null) ??
                          (SteamContentLogProgress.CantReachServers(steamRoot) ? CantReachServersMessage : null);
                    var status = stalled is not null
                        ? stalled
                        : liveBytes
                        ? $"Downloading {game.Title}… ({FormatBytes(transfer.Downloaded ?? 0)} / {FormatBytes(transfer.ToDownload!.Value)})"
                        : hasDownload || snap.StateFlags is not null
                            ? $"Downloading {game.Title}…"
                            : size > 0
                                ? $"Downloading {game.Title}… ({FormatBytes(size)})"
                                : sawManifest
                                    ? $"Installing {game.Title}…"
                                    : "Starting Steam install…";
                    Report(game.Id, sink, transfer.Phase == "installing" ? InstallPhase.Installing : hasDownload ? InstallPhase.Downloading : InstallPhase.Installing, transfer.Percent,
                        transfer.Phase == "installing" ? $"Installing {game.Title}…" : status,
                        transfer.Phase == "downloading" ? transfer.Downloaded : null,
                        transfer.Phase == "downloading" ? transfer.ToDownload : null);
                });

                if (hit is not null)
                {
                    var pending = HasOpenSteamTransfer(snap, baseline);
                    var liveFolder = SteamContentLogProgress.HasLiveTransferFolder(steamRoot, appId);
                    var transferOpen = transfer.Percent is > 0 and < 100 ||
                                       (transfer.Downloaded is > 0 &&
                                        transfer.ToDownload is > 0 &&
                                        transfer.Downloaded < transfer.ToDownload);
                    var ready = SteamStateFlags.IsFullyInstalled(snap.StateFlags) &&
                                !SteamStateFlags.IsBusy(snap.StateFlags, snap.BytesToDownload, snap.BytesDownloaded) &&
                                !pending &&
                                !liveFolder &&
                                !transferOpen;
                    if (ready)
                    {
                        if (size >= lastSize) stableTicks++;
                        else stableTicks = 0;
                        lastSize = size;
                        // Workshop jobs start a few seconds after the app
                        // depot commits. Two 400ms ticks used to mark Play
                        // while MECCHA was still pulling 2.6 GB of workshop.
                        if (stableTicks >= 15)
                        {
                            Report(game.Id, sink, InstallPhase.Completed, 100, "Installed.");
                            return new InstallResult
                            {
                                Ok = true,
                                Message = "Installed via Steam.",
                                Path = hit.Value.Path,
                            };
                        }
                    }
                    else
                    {
                        stableTicks = 0;
                        lastSize = size;
                    }
                }

                if (!sawManifest && !followedUp && DateTimeOffset.UtcNow >= followUpAt)
                {
                    followedUp = true;
                    if (await CommandSteamIpcAsync("install", appId, ct).ConfigureAwait(false) != SteamIpcStatus.Ok)
                        NudgeSteamUpdate(steamExe, game, appId);
                }

                if (!sawManifest && elapsed > 20 * 60 || (now - lastMovedAt).TotalMinutes > 120)
                {
                    var why = sawManifest
                        ? "Steam's download made no progress for two hours. Check it in Steam."
                        : "Steam did not start the install. Check it in Steam.";
                    Report(game.Id, sink, InstallPhase.Failed, null, why);
                    return new InstallResult { Ok = false, Message = why };
                }

                await Task.Delay(400, ct).ConfigureAwait(false);
            }

            await StopFreshSteamInstallAsync(appId, appManifestExistedBeforeRequest).ConfigureAwait(false);
            const string cancelled = "Canceled.";
            Report(game.Id, sink, InstallPhase.Cancelled, null, cancelled);
            return new InstallResult { Ok = false, Message = cancelled };
        }
        catch (OperationCanceledException)
        {
            await StopFreshSteamInstallAsync(appId, appManifestExistedBeforeRequest).ConfigureAwait(false);
            const string cancelled = "Canceled.";
            Report(game.Id, sink, InstallPhase.Cancelled, null, cancelled);
            return new InstallResult { Ok = false, Message = cancelled };
        }
        catch (Exception ex)
        {
            var failed = CliFailure.Describe("Steam", "install", game.Title, ex);
            Report(game.Id, sink, InstallPhase.Failed, null, failed);
            return new InstallResult { Ok = false, Message = failed };
        }
    }

    private static async Task StopFreshSteamInstallAsync(string appId, bool appManifestExistedBeforeRequest)
    {
        if (!SteamStateFlags.CanRollBackCancelledInstall(appManifestExistedBeforeRequest))
        {
            AppLog.Info(
                $"Steam install cancel: appId={appId}; left the pre-existing install in place.");
            return;
        }

        // The game's folder, read before Steam drops its manifest: Steam removes the files it downloaded but leaves
        // the folder, empty, in the library.
        string? folder = null;
        try
        {
            if (FindAppManifestPath(appId) is { } manifest &&
                SteamProtocol.TryParseAppManifest(SteamProtocol.ParseAcfFields(File.ReadAllText(manifest)), out _, out _, out var dir, out _) &&
                !string.IsNullOrWhiteSpace(dir) && dir.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
                folder = Path.Combine(Path.GetDirectoryName(manifest)!, "common", dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        SteamIpcStatus status;
        try { status = await SteamClientIpc.CommandAsync("uninstall", appId, cleanup.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { status = SteamIpcStatus.Unavailable; }
        AppLog.Info($"Steam install cancel: appId={appId}; ipc={status}.");
        if (folder is not null && status == SteamIpcStatus.Ok) _ = RemoveEmptyFolderAsync(folder);
    }

    // Only ever an empty folder (Steam has deleted the files): anything still inside stays.
    private static async Task RemoveEmptyFolderAsync(string folder)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            try
            {
                if (!Directory.Exists(folder)) return;
                if (Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories).Any(File.Exists)) continue;
                Directory.Delete(folder, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Re-send the exact selected app's install/update request as a fallback
    /// if Steam IPC is missing or this client interface is too new.
    /// </summary>
    private static void NudgeSteamUpdate(string steamExe, GameEntry game, string appId)
    {
        foreach (var command in SteamUpdateCommandPlan.BuildNudge(appId))
        {
            try
            {
                AppLog.Info(
                    $"Steam update request: gameId={game.Id}; appId={appId}; purpose={command.Purpose}.");
                ProcessHelper.StartHidden(steamExe, command.Arguments);
                StoreWindowHider.HideOnce(StoreWindowHider.SteamProcessNames);
            }
            catch (Exception ex)
            {
                AppLog.Debug($"Steam update request failed for gameId={game.Id}; appId={appId}: {ex.Message}");
            }
        }
    }

    private static async Task<SteamIpcStatus> CommandSteamIpcAsync(
        string action,
        string appId,
        CancellationToken ct,
        bool retryCommandFailure = true)
    {
        var last = SteamIpcStatus.Unavailable;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(25));
        try
        {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            budget.Token.ThrowIfCancellationRequested();
            last = await SteamClientIpc.CommandAsync(action, appId, budget.Token).ConfigureAwait(false);
            if (last == SteamIpcStatus.Ok)
                return last;
            // A missing helper is not a transient failure. Four more spawns and
            // 4.5s of delay only postponed the protocol fallback the caller
            // already has.
            if (last == SteamIpcStatus.HostMissing)
                return last;
            if (last == SteamIpcStatus.CommandFailed && !retryCommandFailure)
                return last;
            if (attempt < 3) await Task.Delay(1500, budget.Token).ConfigureAwait(false);
        }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return SteamIpcStatus.Unavailable; }

        return last;
    }

    private static async Task WaitForSteamCommandListenerAsync(CancellationToken ct)
    {
        // steam.exe appears before its single-instance command listener is ready.
        // Waiting for the UI helper (with a bounded fallback) prevents the first
        // exact app request from being lost during a cold client start.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline
               && (!ProcessHelper.IsProcessRunning("steam")
                   || !ProcessHelper.IsProcessRunning("steamwebhelper")))
        {
            await Task.Delay(350, ct).ConfigureAwait(false);
        }

        await Task.Delay(750, ct).ConfigureAwait(false);
    }

    public async Task<InstallResult> UpdateAsync(GameEntry game, IProgress<InstallProgress>? progress, CancellationToken ct = default)
    {
        // Real Steam game update: request install/update URI, then poll appmanifest until ready.
        var appId = game.LaunchTarget;
        if (!SteamProtocol.IsValidAppId(appId))
            return new InstallResult { Ok = false, Message = "Missing or invalid Steam app id." };

        var steamExe = ResolveSteamExe();
        if (steamExe is null)
            return new InstallResult { Ok = false, Message = "Steam is not installed." };

        using var hider = StoreWindowHider.ForSteam();
        // Above the try: the catch blocks must end the tracked progress too, or
        // a cancelled update stays "busy" in GetDownloadProgress until restart.
        var sink = Track(progress);

        try
        {
            // Arm suppression before starting or messaging Steam so protocol
            // handling cannot flash or foreground the store window.
            hider.Start(TimeSpan.FromMinutes(90));
            Report(game.Id, sink, InstallPhase.Preparing, null, "Starting Steam…");
            if (!ProcessHelper.IsProcessRunning("steam"))
            {
                ProcessHelper.StartHidden(
                    steamExe,
                    SteamUpdateCommandPlan.HiddenClientStartArguments());
                await WaitForSteamCommandListenerAsync(ct).ConfigureAwait(false);
            }

            var initial = ReadAppManifestSnapshot(appId);
            if (string.IsNullOrWhiteSpace(initial.Name) ||
                !string.Equals(initial.Name.Trim(), game.Title.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                const string identityError =
                    "Exo did not start the update because Steam lists a different name for this game. Refresh the library and try again.";
                Report(game.Id, sink, InstallPhase.Failed, null, identityError);
                return new InstallResult { Ok = false, Message = identityError };
            }

            var queuedAtStart = initial.BytesToDownload is > 0
                && (initial.BytesDownloaded is null or 0);
            var neededAtStart = game.UpdateAvailable || SnapshotNeedsUpdate(initial) || queuedAtStart;

            Report(game.Id, sink, InstallPhase.Downloading, null, "Starting Steam update…");
            using var gameEula = SteamGameEulaPromptAutomator.Arm(
                steamExe, game.Title, TimeSpan.FromMinutes(12), ct);
            using var progressWatcher = SteamClientIpc.StartProgressWatcher(appId, game.Id, sink, install: false);

            if (await CommandSteamIpcAsync("update", appId, ct).ConfigureAwait(false) != SteamIpcStatus.Ok)
                NudgeSteamUpdate(steamExe, game, appId);

            var steamRoot = ResolveSteamRoot();
            var start = DateTimeOffset.UtcNow;
            // One follow-up request per click, 20 s in, if Steam still has the
            // update queued: a client still loading its download queue can drop
            // the first one. After that the queue is Steam's and the stall
            // deadline ends the wait. Re-sending every 12 s launched steam.exe
            // with the install URI 26 times for one Update.
            var followUpAt = start.AddSeconds(20);
            var followedUp = false;
            var queuedStallDeadline = start.AddSeconds(120);
            var sawDownloadProgress = false;
            var observation = new SteamUpdateObservation(UpdateSnapshot(initial), neededAtStart);

            long lastDownloaded = -1;
            // A big update can take hours; give up only after two hours with
            // no new bytes and no phase change.
            var lastMovedAt = start;
            var lastState = "";
            var sampler = new SteamWatchSampler();
            void ReportFallback(InstallPhase phase, double? percent, string status, long? downloaded = null, long? toDownload = null)
            {
                progressWatcher.ReportFallback(() => Report(game.Id, sink, phase, percent, status, downloaded, toDownload));
            }

            while (!ct.IsCancellationRequested)
            {
                var snap = ReadAppManifestSnapshot(appId);
                var flags = snap.StateFlags ?? "";
                var busy = SteamStateFlags.IsBusy(flags, snap.BytesToDownload, snap.BytesDownloaded);
                var updateNeeded = SnapshotNeedsUpdate(snap);
                var transfer = sampler.ReadTransfer(appId, steamRoot, snap, initial);
                var transferPct = transfer.Percent;
                var openTransfer = HasOpenSteamTransfer(snap, initial);
                var state = $"{flags}|{transfer.Phase}|{transfer.Downloaded ?? snap.BytesDownloaded}|{snap.BytesStaged}";
                if (state != lastState)
                {
                    lastState = state;
                    lastMovedAt = DateTimeOffset.UtcNow;
                }

                if (transferPct is > 0 and < 100 &&
                    transfer.Downloaded is long d &&
                    d > lastDownloaded)
                {
                    sawDownloadProgress = true;
                    lastDownloaded = d;
                }

                // Steam retains the final byte totals after returning to StateFlags=4.
                // Only render byte progress while the manifest is still busy; otherwise
                // this branch would mask the ready state forever after a successful update.
                var liveFolder = SteamContentLogProgress.HasLiveTransferFolder(steamRoot, appId);

                var decision = observation.Observe(UpdateSnapshot(snap), progressWatcher.HasActiveTransfer, liveFolder);
                if (decision.Completed)
                {
                    Report(game.Id, sink, InstallPhase.Completed, 100, "Up to date.");
                    return new InstallResult { Ok = true, Message = "Game is up to date." };
                }
                if (decision.Finalizing)
                    ReportFallback(InstallPhase.Finalizing, null, "Finalizing in Steam…");
                else if (decision.Queued || (!busy && !openTransfer && updateNeeded && !sawDownloadProgress))
                {
                    ReportFallback(InstallPhase.Downloading, transferPct,
                        "Requesting this game's queued Steam update…");

                    if (!sawDownloadProgress && !progressWatcher.HasActiveTransfer && DateTimeOffset.UtcNow >= queuedStallDeadline)
                    {
                        const string stalled =
                            "Steam did not start this game's update.";
                        Report(game.Id, sink, InstallPhase.Failed, null, stalled);
                        return new InstallResult { Ok = false, Message = stalled };
                    }

                    if (!followedUp && DateTimeOffset.UtcNow >= followUpAt)
                    {
                        followedUp = true;
                        if (await CommandSteamIpcAsync("update", appId, ct).ConfigureAwait(false) != SteamIpcStatus.Ok)
                            NudgeSteamUpdate(steamExe, game, appId);
                    }
                }
                else if ((busy || openTransfer || liveFolder || transferPct is > 0 and < 100) &&
                    (snap.BytesToDownload is > 0 || snap.BytesToStage is > 0 || transferPct is > 0 and < 100 || liveFolder))
                {
                    var to = transfer.ToDownload ?? snap.BytesToDownload;
                    var done = transfer.Downloaded ?? snap.BytesDownloaded ?? 0;
                    var liveBytes = transferPct is not null && to is > 0 && done <= to.Value;
                    ReportFallback(transfer.Phase == "installing" ? InstallPhase.Installing : InstallPhase.Downloading, transferPct,
                        liveBytes
                            ? $"{(transfer.Phase == "installing" ? "Applying update" : "Downloading")}… {FormatBytes(done)} / {FormatBytes(to!.Value)}"
                            : SteamContentLogProgress.CantReachServers(steamRoot)
                                ? CantReachServersMessage
                                : "Steam is applying the update…",
                        liveBytes && transfer.Phase == "downloading" ? done : null,
                        liveBytes && transfer.Phase == "downloading" ? to : null);
                }
                else if (busy)
                {
                    ReportFallback(InstallPhase.Installing, null,
                        "Steam is applying the update…");
                }
                else
                {
                    ReportFallback(InstallPhase.Downloading, null,
                        "Waiting for Steam to start this game's update…");
                }

                if ((DateTimeOffset.UtcNow - lastMovedAt).TotalMinutes > 120)
                {
                    const string stalledLong = "Steam's update made no progress for two hours. Check it in Steam.";
                    Report(game.Id, sink, InstallPhase.Failed, null, stalledLong);
                    return new InstallResult { Ok = false, Message = stalledLong };
                }

                await Task.Delay(400, ct).ConfigureAwait(false);
            }

            const string cancelled = "Canceled.";
            Report(game.Id, sink, InstallPhase.Cancelled, null, cancelled);
            return new InstallResult { Ok = false, Message = cancelled };
        }
        catch (OperationCanceledException)
        {
            const string cancelled = "Canceled.";
            Report(game.Id, sink, InstallPhase.Cancelled, null, cancelled);
            return new InstallResult { Ok = false, Message = cancelled };
        }
        catch (Exception ex)
        {
            var failed = CliFailure.Describe("Steam", "update", game.Title, ex);
            Report(game.Id, sink, InstallPhase.Failed, null, failed);
            return new InstallResult { Ok = false, Message = failed };
        }
    }

    private static SteamUpdateObservation.Snapshot UpdateSnapshot(AppManifestSnapshot snapshot) =>
        new(snapshot.Name is not null && snapshot.BuildId is not null && SteamStateFlags.TryParse(snapshot.StateFlags, out _),
            snapshot.StateFlags, snapshot.BytesDownloaded, snapshot.BytesToDownload,
            snapshot.BytesStaged, snapshot.BytesToStage, snapshot.BuildId, snapshot.TargetBuildId);

    /// <summary>
    /// Steam's own reason a download is not moving, from the manifest's
    /// UpdateResult, worded for the progress line. Steam keeps retrying on its
    /// own; null when there is no error to report.
    /// </summary>
    private const string CantReachServersMessage =
        "Steam can't reach its download servers. Check your connection; Steam keeps trying.";

    private static string? SteamDownloadError(int? updateResult) => updateResult switch
    {
        null or 0 or 2 or 3 => null, // none, paused, canceled
        6 or 7 => CantReachServersMessage,
        12 => "Not enough disk space for this download.",
        _ => $"Steam stopped the download (error {updateResult}). It keeps trying.",
    };

    /// <summary>Whether Steam is moving this app's files right now (downloading, staging or committing).</summary>
    internal static bool TransferRunning(string appId)
    {
        var snap = ReadAppManifestSnapshot(appId);
        return SteamStateFlags.IsBusy(snap.StateFlags, snap.BytesToDownload, snap.BytesDownloaded);
    }

    /// <summary>
    /// Follows a transfer Steam started itself (its own auto-update, or one started in Steam) with the live reader
    /// Exo's own Steam jobs use, until Steam stops moving the files. Nothing is asked of Steam and no window is touched.
    /// </summary>
    internal static async Task FollowAsync(GameEntry game, IProgress<InstallProgress> progress, CancellationToken ct)
    {
        if (game.LaunchTarget is not { Length: > 0 } appId) return;
        using var watcher = SteamClientIpc.StartProgressWatcher(appId, game.Id, progress, install: !game.Installed);
        while (TransferRunning(appId)) await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
    }

    private sealed record AppManifestSnapshot(
        string? Name,
        string? StateFlags,
        long? BytesToDownload,
        long? BytesDownloaded,
        long? BytesToStage,
        long? BytesStaged,
        string? BuildId,
        string? TargetBuildId,
        int? UpdateResult = null);

    private static bool HasOpenSteamTransfer(AppManifestSnapshot snap, AppManifestSnapshot baseline)
    {
        var downloadOpen = snap.BytesToDownload is > 0 &&
                           (snap.BytesDownloaded is null || snap.BytesDownloaded < snap.BytesToDownload);
        var stageOpen = snap.BytesToStage is > 0 &&
                        (snap.BytesStaged is null || snap.BytesStaged < snap.BytesToStage);
        if (!downloadOpen && !stageOpen) return false;
        return snap.BytesToDownload != baseline.BytesToDownload ||
               snap.BytesDownloaded != baseline.BytesDownloaded ||
               snap.BytesToStage != baseline.BytesToStage ||
               snap.BytesStaged != baseline.BytesStaged;
    }

    private static bool SnapshotNeedsUpdate(AppManifestSnapshot snapshot)
    {
        var flags = snapshot.StateFlags ?? "";
        return SteamStateFlags.IsUpdateAvailable(flags, installed: true, snapshot.BytesToDownload, snapshot.BytesDownloaded) ||
               SteamStateFlags.HasPendingTargetBuild(snapshot.BuildId, snapshot.TargetBuildId);
    }

    private static AppManifestSnapshot ReadAppManifestSnapshot(string appId)
    {
        var path = FindAppManifestPath(appId);
        if (path is null || !File.Exists(path))
            return new AppManifestSnapshot(null, null, null, null, null, null, null, null);
        try
        {
            var fields = SteamProtocol.ParseAcfFields(File.ReadAllText(path));
            var name = fields.GetValueOrDefault("name");
            var flags = fields.GetValueOrDefault("StateFlags");
            long? to = null, done = null, toStage = null, staged = null;
            if (long.TryParse(fields.GetValueOrDefault("BytesToDownload"), out var btd))
                to = btd;
            if (long.TryParse(fields.GetValueOrDefault("BytesDownloaded"), out var bd))
                done = bd;
            if (long.TryParse(fields.GetValueOrDefault("BytesToStage"), out var bts))
                toStage = bts;
            if (long.TryParse(fields.GetValueOrDefault("BytesStaged"), out var bs))
                staged = bs;
            var buildId = fields.GetValueOrDefault("buildid");
            var targetBuildId = fields.GetValueOrDefault("TargetBuildID");
            int? updateResult = int.TryParse(fields.GetValueOrDefault("UpdateResult"), out var ur) ? ur : null;
            return new AppManifestSnapshot(name, flags, to, done, toStage, staged, buildId, targetBuildId, updateResult);
        }
        catch
        {
            return new AppManifestSnapshot(null, null, null, null, null, null, null, null);
        }
    }

    /// <summary>
    /// The content log tail and the downloading/install directory walks are the
    /// expensive part of an install watch. Re-reading them on every 400ms tick
    /// spent hundreds of milliseconds of disk I/O per second against the same
    /// drive Steam is writing the download to. The ACF counters stay live; only
    /// these two samples are held for a beat.
    /// </summary>
    private sealed class SteamWatchSampler
    {
        private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(2);

        private DateTimeOffset _contentAtUtc = DateTimeOffset.MinValue;
        private SteamContentLogProgress.Job? _job;
        private DateTimeOffset _sizeAtUtc = DateTimeOffset.MinValue;
        private long _installedBytes;

        public SteamTransferProgress.Sample ReadTransfer(
            string appId,
            string? steamRoot,
            AppManifestSnapshot snap,
            AppManifestSnapshot baseline)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _contentAtUtc >= SampleInterval)
            {
                _job = SteamContentLogProgress.TryReadLatest(steamRoot, appId);
                _contentAtUtc = now;
            }

            var busy = SteamStateFlags.IsBusy(
                snap.StateFlags, snap.BytesToDownload, snap.BytesDownloaded);
            return SteamTransferProgress.Resolve(
                snap.BytesDownloaded,
                snap.BytesToDownload,
                snap.BytesStaged,
                snap.BytesToStage,
                busy,
                baseline.BytesDownloaded,
                baseline.BytesToDownload,
                _job,
                baselineStaged: baseline.BytesStaged,
                baselineToStage: baseline.BytesToStage,
                stateFlags: long.TryParse(snap.StateFlags, out var state) ? state : null);
        }

        public long ReadInstalledSize(string? path)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _sizeAtUtc < SampleInterval)
                return _installedBytes;
            _installedBytes = TryDirSize(path);
            _sizeAtUtc = now;
            return _installedBytes;
        }
    }

    private static string? FindAppManifestPath(string appId)
    {
        var root = ResolveSteamRoot();
        if (root is null) return null;
        foreach (var lib in CollectLibraryFolders(root))
        {
            var p = Path.Combine(lib, "steamapps", $"appmanifest_{appId}.acf");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    public bool CanRepair(GameEntry game) =>
        game.Installed && SteamProtocol.IsValidAppId(game.LaunchTarget) && ResolveSteamExe() is not null;

    public async Task<InstallResult> RepairAsync(
        GameEntry game,
        IProgress<InstallProgress>? progress,
        CancellationToken ct = default)
    {
        var appId = game.LaunchTarget;
        if (!SteamProtocol.IsValidAppId(appId))
            return new InstallResult { Ok = false, Message = "Missing or invalid Steam app id." };
        var steamExe = ResolveSteamExe();
        if (steamExe is null)
            return new InstallResult { Ok = false, Message = "Steam is not installed." };

        Report(game.Id, progress, InstallPhase.Preparing, null, "Asking Steam to verify files…");
        try
        {
            using var hider = StoreWindowHider.ForSteam();
            hider.Start(TimeSpan.FromMinutes(35));
            if (!ProcessHelper.IsProcessRunning("steam"))
            {
                ProcessHelper.StartHidden(steamExe, SteamUpdateCommandPlan.HiddenClientStartArguments());
                await WaitForSteamCommandListenerAsync(ct).ConfigureAwait(false);
            }

            using var verificationRequest = ProcessHelper.StartHidden(
                steamExe,
                [.. SteamUpdateCommandPlan.HiddenClientStartArguments(), SteamProtocol.ValidateUri(appId)]);
            if (verificationRequest is null)
                return new InstallResult { Ok = false, Message = "Steam verification could not be started." };

            var start = DateTimeOffset.UtcNow;
            var verification = new SteamVerificationEvidence();
            while (!ct.IsCancellationRequested)
            {
                var snap = ReadAppManifestSnapshot(appId);
                var state = verification.Observe(snap.StateFlags, snap.BytesToDownload, snap.BytesDownloaded,
                    DateTimeOffset.UtcNow - start, ct.IsCancellationRequested);
                if (state == SteamVerificationStatus.Confirmed)
                {
                    Report(game.Id, progress, InstallPhase.Completed, 100, "Files verified.");
                    return new InstallResult { Ok = true, Message = "Steam verified the files.", Path = game.Path };
                }

                if (state == SteamVerificationStatus.Unconfirmed)
                {
                    const string message = "Steam received the request, but Exo could not confirm verification. Check Steam Downloads.";
                    Report(game.Id, progress, InstallPhase.Failed, null, message);
                    return new InstallResult { Ok = false, Message = message };
                }

                if (state == SteamVerificationStatus.TimedOut)
                {
                    Report(game.Id, progress, InstallPhase.Failed, null, "Steam verify timed out.");
                    return new InstallResult { Ok = false, Message = "Steam did not finish verifying files." };
                }

                Report(game.Id, progress, state == SteamVerificationStatus.Verifying ? InstallPhase.Installing : InstallPhase.Preparing,
                    null, state == SteamVerificationStatus.Verifying ? "Steam is verifying files…" : "Waiting for Steam to start verification…");
                await Task.Delay(500, ct).ConfigureAwait(false);
            }

            Report(game.Id, progress, InstallPhase.Cancelled, null, "Canceled.");
            return new InstallResult { Ok = false, Message = "Canceled." };
        }
        catch (OperationCanceledException)
        {
            Report(game.Id, progress, InstallPhase.Cancelled, null, "Canceled.");
            return new InstallResult { Ok = false, Message = "Canceled." };
        }
        catch (Exception ex)
        {
            var failed = CliFailure.Describe("Steam", "verify", game.Title, ex);
            Report(game.Id, progress, InstallPhase.Failed, null, failed);
            return new InstallResult { Ok = false, Message = failed };
        }
    }

    public async Task<LaunchResult> LaunchAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default)
    {
        var appId = game.LaunchTarget;
        if (!SteamProtocol.IsValidAppId(appId))
            return new LaunchResult { Ok = false, Message = "Missing or invalid Steam app id." };

        var steamExe = ResolveSteamExe();
        if (steamExe is null)
            return new LaunchResult { Ok = false, Message = "Steam could not be found. Open Steam once, then try Play again." };

        var installPath = FindInstalled(appId)?.Path ?? game.Path;
        ct.ThrowIfCancellationRequested();

        // Suppress Steam chrome for the whole handoff. The old
        // 5s hide + Restore on dispose was exactly what flashed Steam on Play.
        using var hider = StoreWindowHider.ForSteam();
        if (options.MinimizeStoreUi)
            hider.Start(SteamProcessPath.LaunchChromeSuppressionTimeout);

        void QuietSteam()
        {
            if (options.MinimizeStoreUi && !HiddenStoreRuntime.IsSuspended(StoreKind.Steam))
                MinimizeSteamUi();
        }
        void RevealSteam()
        {
            HiddenStoreRuntime.SuspendFor(StoreKind.Steam, TimeSpan.FromMinutes(30));
            hider.Dispose();
            StoreWindowHider.RestoreStoreWindows(StoreWindowHider.SteamProcessNames);
            try { ProcessHelper.StartHidden(steamExe, [SteamProtocol.OpenMainUri()]); } catch { }
        }

        try
        {
            var alreadyRunningPid = FindEligibleRunningPid(installPath);
            if (alreadyRunningPid is int pid)
            {
                QuietSteam();
                return new LaunchResult
                {
                    Ok = true,
                    Message = "Already running",
                    ProcessId = pid,
                    BackendStarted = "steam",
                };
            }

            if (!ProcessHelper.IsProcessRunning("steam"))
            {
                ct.ThrowIfCancellationRequested();
                ProcessHelper.StartHidden(steamExe, SteamUpdateCommandPlan.HiddenClientStartArguments());
                await WaitForSteamCommandListenerAsync(ct).ConfigureAwait(false);
            }

            // Address the resolved installation directly. The OS protocol handler
            // can still point at an old drive after Steam has been moved.
            var processIdsBeforeLaunch = SnapshotRunningPids(installPath);
            ct.ThrowIfCancellationRequested();
            using var gameEula = SteamGameEulaPromptAutomator.Arm(
                steamExe, game.Title, SteamProcessPath.LaunchChromeSuppressionTimeout, ct);
            ProcessHelper.StartHidden(steamExe,
                SteamProtocol.LaunchArguments(appId, options.ExtraArgs));

            QuietSteam();

            // Wait for a real game process — keep poking hide while we wait.
            var gamePid = await WaitForGameProcessAsync(
                    installPath,
                    appId,
                    SteamProcessPath.LaunchHandoffTimeout,
                    ct,
                    processIdsBeforeLaunch,
                    onPoll: QuietSteam)
                .ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();

            QuietSteam();

            if (gamePid is null && !SteamStateFlags.IsAppRunning(ReadAppManifestSnapshot(appId).StateFlags))
            {
                RevealSteam();
                return new LaunchResult
                {
                    Ok = false,
                    Message = "Steam did not start the game. Steam has been opened; check any license agreement, sign-in, updates, and the game drive, then try Play again.",
                    BackendStarted = "steam",
                };
            }

            QuietSteam();
            return new LaunchResult
            {
                Ok = true,
                Message = "Running",
                ProcessId = gamePid,
                BackendStarted = "steam",
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new LaunchResult { Ok = false, Message = CliFailure.Launch(game.Title, ex), BackendStarted = "steam" };
        }
        finally
        {
            QuietSteam();
        }
    }

    /// <summary>
    /// PID of a process whose executable is actually below the install folder.
    /// </summary>
    private static async Task<int?> WaitForGameProcessAsync(
        string? installPath,
        string appId,
        TimeSpan timeout,
        CancellationToken ct,
        ISet<int> processIdsBeforeLaunch,
        Action? onPoll = null)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        var busyDeadline = deadline + SteamProcessPath.LaunchBusyExtension;
        while (!ct.IsCancellationRequested)
        {
            try { onPoll?.Invoke(); } catch { /* */ }

            var snap = ReadAppManifestSnapshot(appId);
            if (SteamStateFlags.IsAppRunning(snap.StateFlags))
                return FindRunningPid(installPath, processIdsBeforeLaunch);

            var hit = FindRunningPid(installPath, processIdsBeforeLaunch);
            if (hit is int candidatePid)
            {
                // A launcher/bootstrap can briefly exist below the install folder
                // even when Steam did not start a real game. Do not credit the
                // handoff until the fresh candidate survives a short grace.
                await Task.Delay(SteamProcessPath.LaunchProcessConfirmationWindow, ct)
                    .ConfigureAwait(false);
                var confirmed = SteamProcessPath.ConfirmFreshGameProcess(
                    candidatePid,
                    IsProcessStillAlive);
                if (confirmed is not null) return confirmed;
            }

            var now = DateTimeOffset.UtcNow;
            var busy = SteamStateFlags.IsBusy(snap.StateFlags, snap.BytesToDownload, snap.BytesDownloaded);
            if (!SteamProcessPath.ShouldKeepWaiting(now, deadline, busyDeadline, busy))
                break;

            await Task.Delay(400, ct).ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();
        return FindRunningPid(installPath, processIdsBeforeLaunch);
    }

    private static bool IsProcessStillAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch { return false; }
    }

    private static HashSet<int> SnapshotRunningPids(string? installPath)
    {
        var processIds = new HashSet<int>();
        if (string.IsNullOrWhiteSpace(installPath) || !Directory.Exists(installPath))
            return processIds;

        try
        {
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    if (process.HasExited) continue;
                    var path = ProcessHelper.TryGetExecutablePath(process);
                    if (!string.IsNullOrWhiteSpace(path) && SteamProcessPath.IsWithinInstall(installPath, path))
                        processIds.Add(process.Id);
                }
                catch { /* access denied / process exit */ }
                finally { process.Dispose(); }
            }
        }
        catch { /* enumeration race */ }

        return processIds;
    }

    private static int? FindEligibleRunningPid(string? installPath) =>
        FindRunningPid(installPath, processIdsBeforeLaunch: null);

    private static int? FindRunningPid(string? installPath, ISet<int>? processIdsBeforeLaunch)
    {
        if (string.IsNullOrWhiteSpace(installPath) || !Directory.Exists(installPath))
            return null;
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    var path = ProcessHelper.TryGetExecutablePath(p);
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    var eligible = processIdsBeforeLaunch is null
                        ? SteamProcessPath.IsEligibleGameProcess(p.Id, p.ProcessName, path, installPath)
                        : SteamProcessPath.IsEligibleNewGameProcess(
                            p.Id, p.ProcessName, path, installPath, processIdsBeforeLaunch);
                    if (!p.HasExited && eligible)
                        return p.Id;
                }
                catch { /* access denied */ }
                finally { p.Dispose(); }
            }
        }
        catch { /* */ }
        return null;
    }

    public async Task<InstallResult> UninstallAsync(GameEntry game, CancellationToken ct = default)
    {
        var appId = game.LaunchTarget;
        if (!SteamProtocol.IsValidAppId(appId))
            return new InstallResult { Ok = false, Message = "Missing or invalid Steam app id." };

        var steamExe = ResolveSteamExe();
        if (steamExe is null)
            return new InstallResult { Ok = false, Message = "Steam is not installed." };

        try
        {
            if (FindAppManifestPath(appId) is null)
                return new InstallResult { Ok = true, Message = "Already removed from Steam." };

            using var hider = StoreWindowHider.ForSteam();
            hider.Start(TimeSpan.FromMinutes(60));
            if (!ProcessHelper.IsProcessRunning("steam"))
            {
                ProcessHelper.StartHidden(
                    steamExe,
                    SteamUpdateCommandPlan.HiddenClientStartArguments());
                await WaitForSteamCommandListenerAsync(ct).ConfigureAwait(false);
            }

            AppLog.Info($"Steam uninstall request: gameId={game.Id}; appId={appId}.");
            var ipc = await CommandSteamIpcAsync(
                    "uninstall",
                    appId,
                    ct,
                    retryCommandFailure: false)
                .ConfigureAwait(false);
            // IPC targets the app ID. Never click a generic client dialog:
            // reused Steam processes do not identify this removal operation.
            if (ipc != SteamIpcStatus.Ok)
            {
                // steam://uninstall asks to confirm in Steam's own dialog, which the hider kept off screen.
                HiddenStoreRuntime.SuspendFor(StoreKind.Steam, TimeSpan.FromMinutes(15));
                ProcessHelper.StartHidden(
                    steamExe,
                    [.. SteamUpdateCommandPlan.HiddenClientStartArguments(), SteamProtocol.UninstallUri(appId)]);
            }

            var uninstallingSeen = false;
            // Steam's dialog waits for the user, so its answer gets a minute; the helper's request starts at once.
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(ipc == SteamIpcStatus.Ok ? 10 : 60);
            while (DateTimeOffset.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(500, ct).ConfigureAwait(false);
                if (FindAppManifestPath(appId) is null)
                {
                    try { Services.SteamLeftoverCleanup.CleanAfterUninstall(ResolveSteamRoot(), appId); }
                    catch { /* leftover folders are best-effort */ }
                    return new InstallResult { Ok = true, Message = "Removed from Steam." };
                }

                var snap = ReadAppManifestSnapshot(appId);
                if (!uninstallingSeen &&
                    SteamStateFlags.TryParse(snap.StateFlags, out var flags) &&
                    (flags & SteamStateFlags.Uninstalling) != 0)
                {
                    uninstallingSeen = true;
                    deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(15);
                }
            }

            if (!uninstallingSeen)
            {
                hider.Dispose();
                return await StoreClientOpener.UninstallHandoffAsync(StoreKind.Steam, "Steam",
                    steamExe, StoreWindowHider.SteamMainProcessNames, ct).ConfigureAwait(false);
            }
            return new InstallResult
            {
                Ok = false,
                Message = "Steam is still removing this game. Try again in a minute.",
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new InstallResult { Ok = false, Message = CliFailure.Describe("Steam", "uninstall", game.Title, ex) };
        }
    }

    public InstallProgress GetDownloadProgress(string gameId) =>
        _progress.TryGetValue(gameId, out var p) ? p : new InstallProgress { GameId = gameId, Phase = InstallPhase.Idle };

    public Task CleanupAfterExitAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default)
    {
        // Leave Steam alone after launch — closing steamwebhelper made chrome
        // flash back and also fought the user opening Steam manually.
        _ = game;
        _ = options;
        _ = ct;
        return Task.CompletedTask;
    }

    private void Report(
        string gameId,
        IProgress<InstallProgress>? progress,
        InstallPhase phase,
        double? pct,
        string status,
        long? downloaded = null,
        long? toDownload = null)
    {
        var p = new InstallProgress
        {
            GameId = gameId,
            Phase = phase,
            Percent = pct,
            BytesDownloaded = downloaded,
            BytesToDownload = toDownload,
            Status = status,
            CanCancel = phase is InstallPhase.Preparing or InstallPhase.Downloading or InstallPhase.Installing,
        };
        if (progress is not null) progress.Report(p);
        else _progress[gameId] = p;
    }

    /// <summary>
    /// Watcher JSON and adapter Reports both have to land in
    /// <see cref="GetDownloadProgress"/> or the orchestrator's 400ms resample
    /// republishes a stale "Starting…" snapshot and wipes the bar.
    /// </summary>
    private IProgress<InstallProgress> Track(IProgress<InstallProgress>? inner) =>
        new RelayProgress(p =>
        {
            if (!string.IsNullOrWhiteSpace(p.GameId))
                _progress[p.GameId] = p;
            inner?.Report(p);
        });

    private sealed class RelayProgress(Action<InstallProgress> on) : IProgress<InstallProgress>
    {
        public void Report(InstallProgress value) => on(value);
    }

    private static void MinimizeSteamUi() =>
        StoreWindowHider.HideOnce(StoreWindowHider.SteamProcessNames);

    private static (string Path, bool Installed)? FindInstalled(string appId)
    {
        var root = ResolveSteamRoot();
        if (root is null) return null;
        foreach (var lib in CollectLibraryFolders(root))
        {
            var acf = Path.Combine(lib, "steamapps", $"appmanifest_{appId}.acf");
            if (!File.Exists(acf)) continue;
            try
            {
                var fields = SteamProtocol.ParseAcfFields(File.ReadAllText(acf));
                if (!SteamProtocol.TryParseAppManifest(fields, out _, out _, out var installDir, out _))
                    continue;
                if (string.IsNullOrWhiteSpace(installDir)) continue;
                var path = Path.Combine(lib, "steamapps", "common", installDir);
                return (path, Directory.Exists(path));
            }
            catch { }
        }
        return null;
    }

    // Install/update/uninstall watches resolve the root and its library folders
    // on every poll tick. The registry read plus libraryfolders.vdf parse cost
    // more than the manifest read they exist to find; a few seconds of staleness
    // is invisible because a new library folder only matters on the next scan.
    private static readonly TimeSpan SteamPathTtl = TimeSpan.FromSeconds(5);
    private static readonly object SteamPathGate = new();
    private static string? _steamRoot;
    private static DateTimeOffset _steamRootAtUtc = DateTimeOffset.MinValue;
    private static string? _libraryFoldersRoot;
    private static List<string>? _libraryFolders;
    private static DateTimeOffset _libraryFoldersAtUtc = DateTimeOffset.MinValue;

    private static string? ResolveSteamRoot()
    {
        lock (SteamPathGate)
        {
            if (DateTimeOffset.UtcNow - _steamRootAtUtc < SteamPathTtl)
                return _steamRoot;
        }

        var resolved = ReadSteamRoot();
        lock (SteamPathGate)
        {
            _steamRoot = resolved;
            _steamRootAtUtc = DateTimeOffset.UtcNow;
        }
        return resolved;
    }

    private static string? ReadSteamRoot() => SteamInstallation.ResolveRoot();

    /// <summary>Public for Settings → Open Steam.</summary>
    public static string? TryResolveSteamExePublic() => ResolveSteamExe();

    private static string? ResolveSteamExe()
    {
        var root = ResolveSteamRoot();
        if (root is null) return null;
        var exe = Path.Combine(root, "steam.exe");
        return File.Exists(exe) ? exe : null;
    }

    private static IReadOnlyList<string> CollectLibraryFolders(string steamRoot)
    {
        lock (SteamPathGate)
        {
            if (_libraryFolders is not null &&
                string.Equals(_libraryFoldersRoot, steamRoot, StringComparison.OrdinalIgnoreCase) &&
                DateTimeOffset.UtcNow - _libraryFoldersAtUtc < SteamPathTtl)
                return _libraryFolders;
        }

        var folders = ReadLibraryFolders(steamRoot);
        lock (SteamPathGate)
        {
            _libraryFoldersRoot = steamRoot;
            _libraryFolders = folders;
            _libraryFoldersAtUtc = DateTimeOffset.UtcNow;
        }
        return folders;
    }

    private static List<string> ReadLibraryFolders(string steamRoot)
    {
        var list = new List<string> { steamRoot };
        var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) return list;
        try
        {
            var text = File.ReadAllText(vdf);
            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(text, "\"path\"\\s+\"([^\"]+)\""))
            {
                var p = m.Groups[1].Value.Replace("\\\\", "\\");
                if (Directory.Exists(p) && !list.Contains(p, StringComparer.OrdinalIgnoreCase))
                    list.Add(p);
            }
        }
        catch { }
        return list;
    }

    private static long TryDirSize(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return 0;
        try
        {
            long total = 0;
            var n = 0;
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(f).Length; } catch { }
                if (++n > 8000) break;
            }
            return total;
        }
        catch { return 0; }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):0.#} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):0.##} GB";
    }
}
