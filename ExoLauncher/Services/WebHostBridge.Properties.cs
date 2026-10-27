using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ExoLauncher.Adapters;
using ExoLauncher.Adapters.Cli;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>
/// A game's Properties: what its store's own Properties page offers, done by Exo
/// where the store lets a program do it. Steam changes go through the Steam
/// client's own calls (the helper's <c>set</c>), so Steam writes its manifests
/// itself; Legendary's per-game config is written only while Legendary is not
/// running. A change that downloads or deletes files hands the work to the
/// store's own download, which shows in Exo's downloads.
/// </summary>
public sealed partial class WebHostBridge
{
    private static readonly ConcurrentDictionary<string, IReadOnlyList<EpicComponent>> EpicSdlCache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, long>> EpicTagSizeCache = new(StringComparer.Ordinal);

    private static readonly (string Value, string Label)[] SteamAutoUpdateChoices =
    [
        ("0", "Use Steam's setting"),
        ("3", "Keep it up to date"),
        ("1", "Only when I launch it"),
        ("2", "Before other games"),
    ];

    private static readonly (string Value, string Label)[] SteamBackgroundChoices =
    [
        ("0", "Use Steam's setting"),
        ("1", "Always allow"),
        ("2", "Never"),
    ];

    private async Task<object> GamePropertiesAsync(JsonElement p, bool hasParams)
    {
        var game = await PropertiesGameAsync(ReadString(p, hasParams, "id")).ConfigureAwait(true);
        if (game is null) return new { ok = false, message = "Game not found. Refresh the library." };
        try
        {
            return await BuildPropertiesAsync(game, _bridgeLifetime.Token).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Warn($"game.properties failed: {ex.GetType().Name}: {ex.Message}");
            return new { ok = false, message = "Exo could not read this game's properties." };
        }
    }

    private async Task<object> GamePropertiesSetAsync(JsonElement p, bool hasParams)
    {
        var game = await PropertiesGameAsync(ReadString(p, hasParams, "id")).ConfigureAwait(true);
        if (game is null) return new { ok = false, message = "Game not found. Refresh the library." };
        var key = ReadString(p, hasParams, "key") ?? "";
        var value = ReadString(p, hasParams, "value") ?? "";
        var secret = ReadString(p, hasParams, "secret");
        string? message;
        try
        {
            message = await ApplyPropertyAsync(game, key, value, secret, _bridgeLifetime.Token).ConfigureAwait(true);
        }
        catch (InvalidDataException ex)
        {
            return new { ok = false, message = ex.Message };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Warn($"game.properties.set {key} failed: {ex.GetType().Name}: {ex.Message}");
            return new { ok = false, message = "That change did not go through. Try again." };
        }
        var properties = await BuildPropertiesAsync(game, _bridgeLifetime.Token).ConfigureAwait(true);
        return new { ok = true, message, properties };
    }

    private async Task<GameEntry?> PropertiesGameAsync(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        await _services.Library.GetLibraryAsync().ConfigureAwait(true);
        return _services.Library.Find(id) is { Installed: true } game ? game : null;
    }

    private static bool TakesLaunchOptions(GameEntry game) =>
        game.Store is StoreKind.Steam or StoreKind.Epic or StoreKind.Gog or StoreKind.Amazon or StoreKind.Local &&
        !LaunchOrchestrator.IsAntiCheatTitle(game);

    private async Task<object> BuildPropertiesAsync(GameEntry game, CancellationToken ct)
    {
        var running = _services.Launcher.GetGameRunState(game).IsRunning;
        var adapter = _services.FindAdapter(game.Store);
        var canVerify = adapter is IStoreRepair repair && repair.CanRepair(game);
        var common = new PropertiesView
        {
            Store = game.Store.ToString().ToLowerInvariant(),
            LaunchOptions = TakesLaunchOptions(game) ? _services.Settings.LaunchArgs(game.Id) : null,
            SizeBytes = game.SizeBytes is > 0 ? game.SizeBytes : null,
            VerifyLabel = canVerify ? game.Store == StoreKind.Steam ? "Verify files" : "Repair" : null,
            CanBrowse = !string.IsNullOrWhiteSpace(game.Path) && Directory.Exists(game.Path),
            Running = running,
        };
        if (game.Store == StoreKind.Steam && SteamProtocol.IsValidAppId(game.LaunchTarget))
        {
            await Task.Run(() => FillSteamAsync(common, game.LaunchTarget!, ct), ct).ConfigureAwait(true);
            common.Overlay = SteamOverlayFor(game.LaunchTarget!);
            if (SteamInputFor(game.LaunchTarget!) is { } input) (common.Controller, common.ReadsPlayStation) = (input.Mode, input.ReadsPlayStation);
        }
        else if (game.Store == StoreKind.Epic && LegendaryProperties.IsAppName(game.LaunchTarget))
            await Task.Run(() => FillEpicAsync(common, game.LaunchTarget!, ct), ct).ConfigureAwait(true);
        return common.ToDto();
    }

    private static async Task FillSteamAsync(PropertiesView view, string appId, CancellationToken ct)
    {
        var root = SteamInstallation.ResolveRoot();
        if (root is null) return;
        var manifestPath = SteamGameProperties.FindManifest(root, appId,
            SteamPlaytime.ReadSharedText(Path.Combine(root, "steamapps", "libraryfolders.vdf")), File.Exists);
        if (manifestPath is null || SteamPlaytime.ReadSharedText(manifestPath) is not { } acf) return;
        var manifest = SteamGameProperties.ReadManifest(acf);
        view.SizeBytes = manifest.SizeOnDisk is > 0 ? manifest.SizeOnDisk : view.SizeBytes;

        var id = uint.Parse(appId, CultureInfo.InvariantCulture);
        var appInfo = ReadSharedBytes(Path.Combine(root, "appcache", "appinfo.vdf"));
        var app = appInfo is null ? SteamKeyValues.Empty
            : SteamAppInfoNames.ReadApps(appInfo, new HashSet<uint> { id }).GetValueOrDefault(id) ?? SteamKeyValues.Empty;
        var dlcIds = SteamGameProperties.DlcIds(app).ToHashSet();
        var dlcRecords = appInfo is null || dlcIds.Count == 0 ? new Dictionary<uint, SteamKeyValues>() : SteamAppInfoNames.ReadApps(appInfo, dlcIds);
        // Every listed DLC is asked about: one without files is held by a license only Steam knows.
        var live = ProcessHelper.IsProcessRunning("steam") && SteamClientReady.ReadActiveUser() != 0
            ? await SteamClientIpc.QueryPropertiesAsync(appId, dlcIds.Order().Take(1000), ct).ConfigureAwait(false)
            : null;

        var autoUpdate = live?.AutoUpdate ?? manifest.AutoUpdate;
        view.AutoUpdate = autoUpdate.ToString(CultureInfo.InvariantCulture);
        view.AutoUpdateChoices = SteamAutoUpdateChoices.Select(choice => new PropertyChoice(choice.Value, choice.Label)).ToList();
        var background = live?.Background ?? manifest.Background;
        view.Background = background.ToString(CultureInfo.InvariantCulture);
        view.BackgroundChoices = SteamBackgroundChoices.Select(choice => new PropertyChoice(choice.Value, choice.Label)).ToList();

        // Off for the whole account means Steam keeps no saves in the cloud for any game.
        if (live?.Cloud is { } cloud && live.CloudAccount != false) view.Cloud = cloud;

        var languages = SteamGameProperties.Languages(app);
        var language = live?.Language is int code && code >= 0 && code < SteamGameProperties.LanguageCodes.Length
            ? SteamGameProperties.LanguageCodes[code]
            : manifest.Language is { Length: > 0 } named ? named : "english";
        if (languages.Count > 1)
        {
            var current = languages.FirstOrDefault(entry => entry.Name.Equals(language, StringComparison.OrdinalIgnoreCase));
            view.Language = current?.Name ?? language;
            view.LanguageChoices = languages
                .Select(entry => new PropertyChoice(entry.Name, SteamGameProperties.LanguageName(entry.Name),
                    SizeBytes: entry.DepotBytes))
                .OrderBy(choice => choice.Label, StringComparer.CurrentCulture)
                .ToList();
        }

        var beta = live?.Beta ?? manifest.Beta;
        var branches = SteamGameProperties.Branches(app, beta);
        view.Beta = beta;
        view.BetaCode = true;
        view.BetaChoices = branches
            .Select(branch => new PropertyChoice(branch.Name, branch.Name.Length == 0 ? "None" : branch.Name, branch.Description.Length > 0 ? branch.Description : null))
            .ToList();

        view.ContentLabel = "DLC";
        view.Content = SteamGameProperties.DlcContent(app, dlcRecords, manifest, live)
            .Select(dlc => new PropertyContent(dlc.Id.ToString(CultureInfo.InvariantCulture), dlc.Name, dlc.Enabled, dlc.SizeBytes,
                dlc.Toggle, dlc.Toggle ? null : dlc.Owned switch { true => "Owned", false => "Not owned", null => null }))
            .ToList();

        if (live is { Folders.Count: > 1 } && live.Folder is int folder && folder >= 0 && folder < live.Folders.Count)
        {
            view.Folder = folder.ToString(CultureInfo.InvariantCulture);
            view.FolderChoices = live.Folders.Select((path, index) => new PropertyChoice(
                index.ToString(CultureInfo.InvariantCulture), DriveLabel(path, live.Folders, index), FreeSpace(path))).ToList();
        }
        if (live is { MoveTotal: > 0 }) view.Moving = (live.MoveDone ?? 0, live.MoveTotal.Value);
        view.StoreProperties = "Steam";
    }

    private static async Task FillEpicAsync(PropertiesView view, string appName, CancellationToken ct)
    {
        var folder = EpicAccounts.MainConfigFolder();
        var installed = LegendaryProperties.InstalledTags(SteamPlaytime.ReadSharedText(Path.Combine(folder, "installed.json")), appName);
        if (installed is null) return;
        var components = await EpicComponentsAsync(folder, appName, ct).ConfigureAwait(false);
        if (components.Count > 1)
        {
            var sizes = await EpicTagSizesAsync(appName, ct).ConfigureAwait(false);
            view.ContentLabel = "Optional content";
            view.Content = components.Where(component => !component.Required)
                .Select(component => new PropertyContent(component.Id, component.Name,
                    LegendaryProperties.IsSelected(component, installed), LegendaryProperties.ComponentBytes(component, sizes), Toggle: true))
                .ToList();
        }
        view.CloudSync = EpicHasCloudSaves(SteamPlaytime.ReadSharedText(Path.Combine(folder, "metadata", appName + ".json")));
    }

    /// <summary>
    /// Legendary's selective-download list for the game: only for apps Legendary's
    /// own update data names (<c>version.json</c> › <c>game_overrides.sdl_config</c>),
    /// fetched once per run from the same API Legendary uses.
    /// </summary>
    private static async Task<IReadOnlyList<EpicComponent>> EpicComponentsAsync(string folder, string appName, CancellationToken ct)
    {
        if (EpicSdlCache.TryGetValue(appName, out var cached)) return cached;
        if (!EpicListsSdl(SteamPlaytime.ReadSharedText(Path.Combine(folder, "version.json")), appName)) return [];
        try
        {
            using var http = ExoHttp.Create(TimeSpan.FromSeconds(6), new HttpClientHandler { AllowAutoRedirect = false });
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ExoLauncher/1.0");
            var json = await http.GetStringAsync($"https://api.legendary.gl/v1/sdl/{appName}.json", ct).ConfigureAwait(false);
            var components = LegendaryProperties.ParseSdl(json);
            EpicSdlCache[appName] = components;
            return components;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            AppLog.Info($"Epic optional content list unavailable: {ex.GetType().Name}");
            return [];
        }
    }

    internal static bool EpicListsSdl(string? versionJson, string appName)
    {
        if (string.IsNullOrWhiteSpace(versionJson)) return false;
        try
        {
            using var doc = JsonDocument.Parse(versionJson);
            return doc.RootElement.TryGetProperty("data", out var data) &&
                   data.TryGetProperty("game_overrides", out var overrides) &&
                   overrides.TryGetProperty("sdl_config", out var sdl) && sdl.ValueKind == JsonValueKind.Object &&
                   sdl.TryGetProperty(appName, out _);
        }
        catch (JsonException) { return false; }
    }

    internal static bool EpicHasCloudSaves(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) return false;
        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            return doc.RootElement.TryGetProperty("metadata", out var metadata) &&
                   metadata.TryGetProperty("customAttributes", out var attributes) && attributes.ValueKind == JsonValueKind.Object &&
                   attributes.TryGetProperty("CloudSaveFolder", out var folder) && folder.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException) { return false; }
    }

    private static async Task<IReadOnlyDictionary<string, long>> EpicTagSizesAsync(string appName, CancellationToken ct)
    {
        if (EpicTagSizeCache.TryGetValue(appName, out var cached)) return cached;
        if (EpicAdapter.ResolveLegendary() is not { } legendary) return new Dictionary<string, long>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            var (code, output, _) = await CliRunner.RunJsonAsync(legendary, ["info", appName, "--json"], null, timeout.Token).ConfigureAwait(false);
            var sizes = code == 0 ? LegendaryProperties.TagSizes(output) : new Dictionary<string, long>();
            if (sizes.Count > 0) EpicTagSizeCache[appName] = sizes;
            return sizes;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new Dictionary<string, long>();
        }
    }

    private async Task<string?> ApplyPropertyAsync(GameEntry game, string key, string value, string? secret, CancellationToken ct)
    {
        if (key == "launchOptions")
        {
            if (!TakesLaunchOptions(game)) throw new InvalidDataException("This game takes no launch options from Exo.");
            _services.Settings.SetLaunchArgs(game.Id, value);
            return null;
        }
        if (key == "storeProperties") return await OpenStorePropertiesAsync(game).ConfigureAwait(true);
        if (key == "overlay")
        {
            if (game.Store != StoreKind.Steam || !SteamProtocol.IsValidAppId(game.LaunchTarget) || value is not ("on" or "off"))
                throw new InvalidDataException("Steam does not take that change.");
            return await ApplySteamOverlayAsync(new Dictionary<string, bool> { [game.LaunchTarget!] = value == "on" }, userAsked: true, ct)
                .ConfigureAwait(true);
        }
        if (key == "controller")
        {
            if (game.Store != StoreKind.Steam || !SteamProtocol.IsValidAppId(game.LaunchTarget) || value is not ("native" or "steam" or "default"))
                throw new InvalidDataException("Steam does not take that change.");
            var appId = game.LaunchTarget!;
            _services.Settings.UpdateProfile(s => { if (value == "default") s.SteamInputChoices.Remove(appId); else s.SteamInputChoices[appId] = value; });
            return await ApplySteamOverlayAsync(null, userAsked: true, ct, new HashSet<string> { appId }).ConfigureAwait(true);
        }
        var touchesFiles = key is "content" or "beta" or "betaCode" or "language" or "folder";
        if (touchesFiles && _services.Launcher.GetGameRunState(game).IsRunning)
            throw new InvalidDataException($"Close {game.Title} first.");
        return game.Store switch
        {
            StoreKind.Steam when SteamProtocol.IsValidAppId(game.LaunchTarget) =>
                await ApplySteamPropertyAsync(game, game.LaunchTarget!, key, value, secret, ct).ConfigureAwait(true),
            StoreKind.Epic when LegendaryProperties.IsAppName(game.LaunchTarget) =>
                await ApplyEpicPropertyAsync(game, game.LaunchTarget!, key, value, ct).ConfigureAwait(true),
            _ => throw new InvalidDataException("Exo cannot change that for this game."),
        };
    }

    private async Task<string?> ApplySteamPropertyAsync(GameEntry game, string appId, string key, string value, string? secret, CancellationToken ct)
    {
        string[] change = key switch
        {
            "autoUpdate" when SteamAutoUpdateChoices.Any(choice => choice.Value == value) => ["autoupdate", value],
            "background" when SteamBackgroundChoices.Any(choice => choice.Value == value) => ["background", value],
            "cloud" when value is "on" or "off" => ["cloud", value == "on" ? "1" : "0"],
            "language" when SteamGameProperties.LanguageCode(value) is int code and >= 0 =>
                ["language", code.ToString(CultureInfo.InvariantCulture)],
            "beta" when value.Length == 0 || IsSteamBranch(value) => ["beta", value.Length == 0 ? "-" : value],
            "betaCode" when secret is { Length: > 0 and <= 256 } => ["betacode"],
            "content" when ParseContentChange(value) is (string dlc, bool on) && uint.TryParse(dlc, out _) => ["dlc", dlc, on ? "1" : "0"],
            "folder" when int.TryParse(value, out var index) && index is >= 0 and < 64 => ["move", value],
            _ => throw new InvalidDataException("Steam does not take that change."),
        };
        if (!await EnsureSteamSignedInAsync(ct).ConfigureAwait(true))
            throw new InvalidDataException("Steam did not start. Open Steam, then try again.");
        var status = await SteamClientIpc.SetPropertyAsync(appId, change, key == "betaCode" ? secret : null, ct).ConfigureAwait(true);
        if (status != SteamIpcStatus.Ok)
        {
            AppLog.Info($"Steam properties: {change[0]} for {appId} returned {status}.");
            throw new InvalidDataException(key switch
            {
                "betaCode" => "Steam did not accept that code.",
                _ when status == SteamIpcStatus.HostMissing => "Exo's Steam helper is missing. Reinstall Exo.",
                _ => "Steam did not take that change. Try again.",
            });
        }
        AppLog.Info($"Steam properties: {change[0]} set for {appId}.");
        switch (key)
        {
            case "content":
            case "beta":
            case "language":
                // Steam queues the files the change adds or removes; Exo runs that
                // job so its progress shows in Exo's downloads.
                StartStoreDownload(game, update: true);
                return key != "content" ? "Steam is updating the game."
                    : change[2] == "1" ? "Steam is downloading it." : "Steam is removing its files.";
            case "betaCode":
                // Steam checks the code with its servers and adds the branch it opens.
                await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(true);
                return "Code sent. Its beta shows in the list once Steam accepts it.";
            case "folder":
                return "Steam is moving the game.";
            default:
                return null;
        }
    }

    private async Task<string?> ApplyEpicPropertyAsync(GameEntry game, string appName, string key, string value, CancellationToken ct)
    {
        var folder = EpicAccounts.MainConfigFolder();
        switch (key)
        {
            case "content":
            {
                if (ParseContentChange(value) is not (string id, bool on)) throw new InvalidDataException("Unknown content.");
                var components = await EpicComponentsAsync(folder, appName, ct).ConfigureAwait(true);
                var installed = LegendaryProperties.InstalledTags(SteamPlaytime.ReadSharedText(Path.Combine(folder, "installed.json")), appName) ?? [];
                if (components.All(component => component.Id != id || component.Required)) throw new InvalidDataException("Unknown content.");
                var chosen = components.Where(component => !component.Required && LegendaryProperties.IsSelected(component, installed))
                    .Select(component => component.Id).ToHashSet(StringComparer.Ordinal);
                if (on) chosen.Add(id); else chosen.Remove(id);
                if (ProcessHelper.IsProcessRunning("legendary"))
                    throw new InvalidDataException("Epic is busy with another download. Try again when it finishes.");
                var config = Path.Combine(folder, "config.ini");
                var text = SteamPlaytime.ReadSharedText(config);
                var updated = LegendaryProperties.SetIniValue(text, appName, "install_tags",
                    string.Join(',', LegendaryProperties.TagsFor(components, chosen)));
                // Written beside it and moved over, so a crash or a full disk never leaves Epic's config cut off.
                var staged = config + ".exo-" + Guid.NewGuid().ToString("N");
                try
                {
                    await File.WriteAllTextAsync(staged, updated, ct).ConfigureAwait(true);
                    File.Move(staged, config, overwrite: true);
                }
                catch
                {
                    try { File.Delete(staged); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                    throw;
                }
                AppLog.Info($"Epic properties: install tags changed for {appName}.");
                // Legendary's repair downloads what the tags add and deletes what they drop.
                StartStoreDownload(game, update: false);
                return on ? "Epic is downloading it." : "Epic is removing its files.";
            }
            case "cloudSync":
            {
                if (EpicAdapter.ResolveLegendary() is not { } legendary) throw new InvalidDataException("Epic is not ready yet.");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromMinutes(2));
                var (code, _, _) = await CliRunner.RunAsync(legendary, ["sync-saves", appName, "-y"], null, null, timeout.Token).ConfigureAwait(true);
                if (code != 0) throw new InvalidDataException("Epic could not sync the saves. Try again.");
                return "Saves are in sync.";
            }
            default:
                throw new InvalidDataException("Epic does not take that change.");
        }
    }

    private void StartStoreDownload(GameEntry game, bool update)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var result = update
                    ? await _services.Launcher.UpdateAsync(game).ConfigureAwait(false)
                    : await _services.Launcher.RepairAsync(game).ConfigureAwait(false);
                if (!result.Ok) AppLog.Info($"Properties download for {game.Id}: {result.Message}");
                _services.Library.Invalidate();
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Properties download for {game.Id} failed: {ex.GetType().Name}: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Steam's own page for what Exo leaves to Steam (the overlay, controller
    /// layouts): Steam shows its window on the game's library page.
    /// </summary>
    private async Task<string?> OpenStorePropertiesAsync(GameEntry game)
    {
        if (game.Store != StoreKind.Steam || !SteamProtocol.IsValidAppId(game.LaunchTarget) ||
            SteamInstallation.ResolveRoot() is not { } root)
            throw new InvalidDataException("This store has no properties page of its own.");
        var opened = await OpenStoreClientAsync("steam").ConfigureAwait(true);
        if (!opened.Ok) throw new InvalidDataException(opened.Message);
        using (Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(root, "steam.exe"),
            Arguments = $"steam://nav/games/details/{game.LaunchTarget}",
            UseShellExecute = false,
        })) { }
        return "Opened in Steam. Its Properties are under the gear on the game's page.";
    }

    /// <summary>Starts Steam hidden when it is closed and waits for it to sign in.</summary>
    private static async Task<bool> EnsureSteamSignedInAsync(CancellationToken ct)
    {
        if (!ProcessHelper.IsProcessRunning("steam"))
        {
            if (SteamInstallation.ResolveRoot() is not { } root) return false;
            StartSteamHidden(Path.Combine(root, "steam.exe"));
        }
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(90);
        while (SteamClientReady.ReadActiveUser() == 0)
        {
            if (DateTimeOffset.UtcNow > deadline) return false;
            await Task.Delay(1000, ct).ConfigureAwait(false);
        }
        return true;
    }

    internal static (string Id, bool On)? ParseContentChange(string value)
    {
        var at = value.LastIndexOf(':');
        if (at <= 0 || value[(at + 1)..] is not ("on" or "off")) return null;
        var id = value[..at];
        return id.Length <= 128 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.') ? (id, value[(at + 1)..] == "on") : null;
    }

    private static bool IsSteamBranch(string value) =>
        value.Length is > 0 and <= 64 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');

    private static byte[]? ReadSharedBytes(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > 256L * 1024 * 1024) return null;
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            return bytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>A library folder as its drive ("D:"), numbered when one drive holds two.</summary>
    private static string DriveLabel(string path, IReadOnlyList<string> all, int index)
    {
        static string Drive(string value) => (Path.GetPathRoot(value) ?? value).TrimEnd('\\', '/').ToUpperInvariant();
        var drive = Drive(path);
        var same = all.Select((other, at) => (other, at)).Where(entry => Drive(entry.other) == drive).Select(entry => entry.at).ToList();
        return same.Count > 1 ? $"{drive} ({same.IndexOf(index) + 1})" : drive;
    }

    private static string? FreeSpace(string path)
    {
        try
        {
            var free = new DriveInfo(Path.GetPathRoot(path) ?? path).AvailableFreeSpace;
            return $"{free / (1024.0 * 1024 * 1024):0} GB free";
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private sealed record PropertyChoice(string Value, string Label, string? Detail = null, long? SizeBytes = null);

    /// <summary>One optional item: <see cref="Toggle"/> when the store lets it be switched, else <see cref="State"/> says where it stands.</summary>
    private sealed record PropertyContent(string Id, string Name, bool Enabled, long? SizeBytes, bool Toggle, string? State = null);

    /// <summary>The page's view of a game's Properties: a null field is a row this game does not have.</summary>
    private sealed class PropertiesView
    {
        public string Store { get; init; } = "";
        public string? LaunchOptions { get; init; }
        public long? SizeBytes { get; set; }
        public string? VerifyLabel { get; init; }
        public bool CanBrowse { get; init; }
        public bool Running { get; init; }
        public string? AutoUpdate { get; set; }
        public List<PropertyChoice> AutoUpdateChoices { get; set; } = [];
        public string? Background { get; set; }
        public List<PropertyChoice> BackgroundChoices { get; set; } = [];
        public bool? Cloud { get; set; }
        public bool CloudSync { get; set; }
        public string? Language { get; set; }
        public List<PropertyChoice> LanguageChoices { get; set; } = [];
        public string? Beta { get; set; }
        public List<PropertyChoice> BetaChoices { get; set; } = [];
        public bool BetaCode { get; set; }
        public string ContentLabel { get; set; } = "DLC";
        public List<PropertyContent>? Content { get; set; }
        public string? Folder { get; set; }
        public List<PropertyChoice> FolderChoices { get; set; } = [];
        public (long Done, long Total)? Moving { get; set; }
        public string? StoreProperties { get; set; }
        /// <summary>The Steam overlay for this game; null for other stores.</summary>
        public bool? Overlay { get; set; }
        /// <summary>Steam Input for this game: native, steam or default; null for other stores.</summary>
        public string? Controller { get; set; }
        /// <summary>Whether the game reads a PlayStation controller itself (its store page says so).</summary>
        public bool ReadsPlayStation { get; set; }

        public object ToDto() => new
        {
            ok = true,
            store = Store,
            launchOptions = LaunchOptions,
            sizeBytes = SizeBytes,
            verifyLabel = VerifyLabel,
            canBrowse = CanBrowse,
            running = Running,
            autoUpdate = AutoUpdate,
            autoUpdateChoices = AutoUpdateChoices.Select(Choice),
            background = Background,
            backgroundChoices = BackgroundChoices.Select(Choice),
            cloud = Cloud,
            cloudSync = CloudSync,
            language = Language,
            languageChoices = LanguageChoices.Select(Choice),
            beta = Beta,
            betaChoices = BetaChoices.Select(Choice),
            betaCode = BetaCode,
            contentLabel = ContentLabel,
            content = Content?.Select(item => new { id = item.Id, name = item.Name, enabled = item.Enabled, sizeBytes = item.SizeBytes, toggle = item.Toggle, state = item.State }),
            folder = Folder,
            folderChoices = FolderChoices.Select(Choice),
            moving = Moving is { } moving ? new { done = moving.Done, total = moving.Total } : null,
            storeProperties = StoreProperties,
            overlay = Overlay,
            controller = Controller,
            readsPlayStation = ReadsPlayStation,
        };

        private static object Choice(PropertyChoice choice) =>
            new { value = choice.Value, label = choice.Label, detail = choice.Detail, sizeBytes = choice.SizeBytes };
    }
}
