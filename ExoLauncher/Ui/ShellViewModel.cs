using System.Collections.ObjectModel;
using ExoLauncher.Helpers;
using ExoLauncher.Models;
using ExoLauncher.Services;
using Microsoft.UI.Dispatching;

namespace ExoLauncher.Ui;

public sealed class ShellViewModel : BindableBase
{
    private readonly AppServices _services;
    private readonly ShellController _shell;
    private readonly DispatcherQueue _queue;
    private readonly Dictionary<string, GameItemVm> _byId = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<GameEntry> _games = Array.Empty<GameEntry>();
    private IReadOnlyList<string> _recent = Array.Empty<string>();
    private string _sortMode = "name";
    private string _search = "";
    private int _searchGen;
    private string? _holdNowId;
    private GameItemVm? _now;
    private NowKind _nowKind = NowKind.Recent;
    private GameItemVm? _selected;
    private InstallProgress? _progress;
    private string? _status;
    private bool _busy;
    private bool _settingsOpen;
    private bool _onboarding;
    private bool _storesReady;
    private bool _libraryReady;
    private IReadOnlyList<LibraryService.StoreBackendStatus> _stores =
        Array.Empty<LibraryService.StoreBackendStatus>();

    public ShellViewModel(AppServices services, ShellController shell, DispatcherQueue queue)
    {
        _services = services;
        _shell = shell;
        _queue = queue;
        _sortMode = services.Settings.Current.SortMode;
        _recent = services.Settings.Current.Recent.ToArray();
        _onboarding = !services.Settings.Current.OnboardingComplete;
        _shell.LibraryChanged += (_, e) => ApplyGames(e.Games);
        _shell.ProgressChanged += (_, progress) => ApplyProgress(progress);
        _shell.LaunchStatusChanged += (_, status) =>
        {
            _status = status.Message;
            OnPropertyChanged(nameof(StatusLine));
            RefreshRunState();
            RecomputeNow();
        };
        _shell.SearchPartial += (_, e) =>
        {
            if (!string.Equals(e.Query, _search.Trim(), StringComparison.OrdinalIgnoreCase)) return;
            ApplyCatalog(e.Hits);
        };
    }

    public ObservableCollection<GameItemVm> Pinned { get; } = [];
    public ObservableCollection<GameItemVm> Library { get; } = [];
    public ObservableCollection<GameItemVm> Catalog { get; } = [];

    public GameItemVm? Now
    {
        get => _now;
        private set
        {
            if (Set(ref _now, value))
            {
                OnPropertyChanged(nameof(HasNow));
                OnPropertyChanged(nameof(NowTitle));
                OnPropertyChanged(nameof(NowKicker));
                OnPropertyChanged(nameof(NowMeta));
                OnPropertyChanged(nameof(NowPrimary));
                OnPropertyChanged(nameof(NowHero));
            }
        }
    }

    public NowKind NowKind
    {
        get => _nowKind;
        private set
        {
            if (Set(ref _nowKind, value))
            {
                OnPropertyChanged(nameof(NowKicker));
                OnPropertyChanged(nameof(NowMeta));
                OnPropertyChanged(nameof(NowPrimary));
                OnPropertyChanged(nameof(NowTransferring));
            }
        }
    }

    public bool HasNow => _now is not null;
    public string NowTitle => _now?.Title ?? "";
    public string NowKicker => UiFormat.NowKicker(_nowKind);
    public string NowPrimary => _now is null
        ? "Play"
        : UiFormat.PrimaryLabel(_now.Game, NowTransferring, _now.CanStop);
    public bool NowTransferring => _nowKind == NowKind.Download && _progress is { IsActive: true };
    public Uri? NowHero
    {
        get
        {
            if (_now is null) return null;
            foreach (var url in CoverArtService.SteamHeroUrls(_now.Game))
            {
                if (Uri.TryCreate(url, UriKind.Absolute, out var uri)) return uri;
            }
            return null;
        }
    }
    public string NowMeta
    {
        get
        {
            if (_now is null) return "";
            if (!string.IsNullOrWhiteSpace(_status) && NowTransferring) return _status!;
            if (_now.CanStop && !NowTransferring) return "Running";
            var parts = new List<string> { _now.StoreName };
            if (NowTransferring)
            {
                if (!string.IsNullOrWhiteSpace(_progress?.Status)) parts.Add(_progress!.Status);
                var speed = UiFormat.Speed(_progress?.BytesPerSecond);
                if (speed is not null) parts.Add(speed);
            }
            else
            {
                if (_now.Playtime is not null) parts.Add(_now.Playtime);
                if (_now.LastPlayed is not null) parts.Add(_now.LastPlayed);
            }
            return string.Join(" · ", parts);
        }
    }

    public double? NowPercent => UiFormat.VisiblePercent(_progress?.Percent);
    public bool NowIndeterminate => NowTransferring && NowPercent is null;
    public GameItemVm? Selected
    {
        get => _selected;
        private set
        {
            if (_selected == value) return;
            if (_selected is not null) _selected.Selected = false;
            _selected = value;
            if (_selected is not null) _selected.Selected = true;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PlateOpen));
        }
    }

    public bool PlateOpen => _selected is not null;
    public bool SettingsOpen
    {
        get => _settingsOpen;
        set => Set(ref _settingsOpen, value);
    }
    public bool Onboarding
    {
        get => _onboarding;
        private set => Set(ref _onboarding, value);
    }
    public bool Busy
    {
        get => _busy;
        private set => Set(ref _busy, value);
    }
    public string Search
    {
        get => _search;
        set
        {
            if (!Set(ref _search, value)) return;
            _ = SearchAsync(value);
        }
    }
    public bool Searching => _search.Trim().Length >= 2;
    public bool HasCatalog => Catalog.Count > 0;
    public bool EmptyLibrary =>
        _libraryReady && !_games.Any(UiFormat.IsLibraryRow) && !Searching;
    public bool StoresReady
    {
        get => _storesReady;
        private set => Set(ref _storesReady, value);
    }
    public IReadOnlyList<LibraryService.StoreBackendStatus> Stores => _stores;
    public IReadOnlyList<LibraryService.StoreBackendStatus> PresentStores =>
        _stores.Where(s => s.store != "local" && s.clientPresent).ToList();
    public string OnboardingSentence
    {
        get
        {
            if (!_storesReady) return "Looking for store apps.";
            var present = PresentStores;
            if (present.Count == 0) return "No store apps on this PC yet.";
            if (present.Count == 1) return $"{present[0].displayName} is on this PC.";
            if (present.Count == 2) return $"{present[0].displayName} and {present[1].displayName} are on this PC.";
            return $"{string.Join(", ", present.Take(present.Count - 1).Select(s => s.displayName))}, and {present[^1].displayName} are on this PC.";
        }
    }
    public string? StatusLine => _status;
    public string AppVersion => _services.AppVersion;
    public AppSettings Settings => _services.Settings.Current;
    public string SortMode
    {
        get => _sortMode;
        set
        {
            if (!Set(ref _sortMode, value)) return;
            _services.Settings.ApplyPatch(sortMode: value);
            RebuildLists();
        }
    }
    public InstallProgress? Progress => _progress;

    public async Task StartAsync()
    {
        LogMilestone("shell-ready");
        _ = LoadStoresAsync();
        try
        {
            var games = await _services.Library.GetLibraryAsync().ConfigureAwait(true);
            ApplyGames(games);
            LogMilestone("library-loaded");
        }
        catch (Exception ex)
        {
            _status = ex.Message;
            OnPropertyChanged(nameof(StatusLine));
            ApplyGames(_services.Library.PeekCachedLibrary());
        }
    }

    public void Select(GameItemVm item)
    {
        Selected = item;
        _holdNowId = Now?.Id;
        RecomputeNow();
    }

    public void ClosePlate() => Selected = null;

    public void ToggleSettings()
    {
        SettingsOpen = !SettingsOpen;
        if (SettingsOpen) ClosePlate();
    }

    public void FinishOnboarding()
    {
        _services.Settings.ApplyPatch(onboardingComplete: true);
        Onboarding = false;
    }

    public async Task PrimaryAsync(GameItemVm? item = null)
    {
        var target = item ?? Now ?? Selected;
        if (target is null || Busy) return;
        if (NowTransferring && NowPicker.Matches(target.Game, _progress?.GameId ?? ""))
        {
            _shell.CancelInstall();
            return;
        }
        if (target.CanStop)
        {
            await RunAsync(() => _shell.StopAsync(target.Id));
            return;
        }

        switch (target.PrimaryAction)
        {
            case "install":
                await RunAsync(() => _shell.InstallAsync(target.Id));
                break;
            case "update":
                await RunAsync(() => _shell.UpdateAsync(target.Id));
                break;
            default:
                await RunAsync(() => _shell.LaunchAsync(target.Id));
                break;
        }
    }

    public Task StopAsync(GameItemVm item) => RunAsync(() => _shell.StopAsync(item.Id));

    public void ToggleFavorite(GameItemVm item)
    {
        _shell.ToggleFavorite(item.Id);
        ApplyGames(_services.Library.PeekCachedLibrary());
    }

    public async Task UninstallAsync(GameItemVm item)
    {
        await RunAsync(() => _shell.UninstallAsync(item.Id));
        ClosePlate();
    }

    public object OpenFolder(GameItemVm item) => _shell.OpenFolder(item.Id);

    public Task RepairAsync(GameItemVm item) => RunAsync(() => _shell.RepairAsync(item.Id));

    public Task<object> DlssStatusAsync(GameItemVm item) => _shell.DlssStatusAsync(item.Id);

    public Task<object> DlssApplyAsync(GameItemVm item) => _shell.DlssUpdateAllAsync(item.Id);

    public Task<object> DlssRestoreAsync(GameItemVm item) => _shell.DlssRestoreAsync(item.Id);

    public object Extras(GameItemVm item) => _shell.Extras(item.Id);

    public async Task AddFolderAsync()
    {
        var pick = await _shell.PickFolderAsync("Choose game folder");
        var path = ReadString(pick, "path");
        var cancelled = ReadBool(pick, "cancelled");
        if (cancelled || string.IsNullOrWhiteSpace(path)) return;
        await RunAsync(() => _shell.InstallAsync("local:add", path));
    }

    public async Task ChooseInstallRootAsync()
    {
        var pick = await _shell.PickFolderAsync("Choose default install folder");
        var path = ReadString(pick, "path");
        var cancelled = ReadBool(pick, "cancelled");
        if (cancelled || string.IsNullOrWhiteSpace(path)) return;
        _shell.SetSettings(new { defaultInstallRoot = path });
        OnPropertyChanged(nameof(Settings));
    }

    public Task ShowStoreAsync(string store) => _shell.ShowStoreAsync(store);

    public Task AuthAsync(string store) => _shell.StoresAuthAsync(store);

    public void SetTrophyEnabled(bool enabled)
    {
        _shell.SetSettings(new { trophyNotificationsEnabled = enabled });
        OnPropertyChanged(nameof(Settings));
    }

    public void SetTrophyPlace(string place)
    {
        _shell.SetSettings(new { trophyNotificationPosition = place });
        OnPropertyChanged(nameof(Settings));
    }

    public object PreviewTrophy() => _shell.PreviewTrophy();

    public Task<object> CheckUpdateAsync() => _shell.CheckUpdateAsync();

    public Task<object> InstallUpdateAsync() => _shell.InstallUpdateAsync();

    public object Minimize() => _shell.Minimize();
    public object ToggleMaximize() => _shell.ToggleMaximize();
    public object CloseWindow() => _shell.Close();
    public object OpenUrl(string url) => _shell.OpenUrl(url);

    private async Task SearchAsync(string raw)
    {
        var gen = ++_searchGen;
        Catalog.Clear();
        OnPropertyChanged(nameof(HasCatalog));
        OnPropertyChanged(nameof(Searching));
        RebuildLists();
        var query = raw.Trim();
        if (query.Length < 2) return;
        await Task.Delay(180).ConfigureAwait(true);
        if (gen != _searchGen) return;
        try { await _shell.SearchAsync(query).ConfigureAwait(true); }
        catch (Exception ex)
        {
            _status = ex.Message;
            OnPropertyChanged(nameof(StatusLine));
        }
    }

    private async Task LoadStoresAsync()
    {
        try
        {
            if (_stores.Count == 0)
                _stores = _services.Library.StoreMatrix();
            StoresReady = true;
            OnPropertyChanged(nameof(Stores));
            OnPropertyChanged(nameof(PresentStores));
            OnPropertyChanged(nameof(OnboardingSentence));
        }
        catch
        {
            StoresReady = true;
        }
        await Task.CompletedTask;
    }

    private void ApplyGames(IReadOnlyList<GameEntry> games)
    {
        _games = games;
        _recent = _services.Settings.Current.Recent.ToArray();
        _stores = _services.Library.StoreMatrix();
        _libraryReady = true;
        RebuildLists();
        OnPropertyChanged(nameof(Stores));
        OnPropertyChanged(nameof(PresentStores));
        OnPropertyChanged(nameof(OnboardingSentence));
        OnPropertyChanged(nameof(Settings));
    }

    private void ApplyProgress(InstallProgress progress)
    {
        _progress = progress;
        _status = progress.Status;
        foreach (var item in _byId.Values) item.SetTransfer(progress);
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(NowPercent));
        OnPropertyChanged(nameof(NowIndeterminate));
        OnPropertyChanged(nameof(NowTransferring));
        OnPropertyChanged(nameof(NowMeta));
        OnPropertyChanged(nameof(NowPrimary));
        OnPropertyChanged(nameof(StatusLine));
        RecomputeNow();
    }

    private void ApplyCatalog(IReadOnlyList<StoreSearchHit> hits)
    {
        Catalog.Clear();
        var present = LibraryPresence();
        foreach (var hit in hits)
        {
            if (hit.Installed) continue;
            if (present.Contains(hit.Id)) continue;
            if (!hit.Owned && !hit.CanInstall) continue;
            var entry = new GameEntry
            {
                Id = hit.Id,
                Title = hit.Title,
                Store = hit.Store,
                Installed = hit.Installed,
                Owned = hit.Owned,
                CanInstall = hit.CanInstall,
                CoverUrl = hit.CoverUrl,
                CoverSource = hit.CoverSource,
                LaunchTarget = hit.LaunchTarget,
                Status = hit.Installed ? "Ready" : hit.Owned ? "Owned" : "Catalog",
            };
            Catalog.Add(Wrap(entry, discover: false));
        }
        OnPropertyChanged(nameof(HasCatalog));
    }

    private void RebuildLists()
    {
        var query = _search.Trim();
        IEnumerable<GameEntry> source = _games;
        if (query.Length >= 2)
        {
            source = _games
                .Select(game => (game, score: LibrarySearch.Score(game.Title, query)))
                .Where(row => row.score >= 0)
                .OrderByDescending(row => row.score)
                .Select(row => row.game);
        }
        else
        {
            source = UiFormat.Sort(_games, _sortMode, _recent);
        }

        bool Playing(GameEntry game)
        {
            var state = _shell.RunState(game);
            return state.IsRunning || state.CanStop;
        }
        var picked = NowPicker.Pick(_games, _progress, _recent, Playing);
        picked = NowPicker.Retain(_games, picked, _holdNowId, Playing);
        var nowId = picked?.Game.Id;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Library.Clear();
        Pinned.Clear();
        foreach (var game in source)
        {
            if (!UiFormat.IsLibraryRow(game)) continue;
            var vm = Wrap(game, discover: false);
            seen.Add(game.Id);
            var pin = game.IsFavorite && game.Installed && query.Length < 2;
            if (pin) Pinned.Add(vm);
            var hideFromGrid = query.Length < 2 &&
                (pin || (nowId is not null && NowPicker.Matches(game, nowId)));
            if (!hideFromGrid) Library.Add(vm);
        }

        foreach (var leftover in _byId.Keys.Where(id => !seen.Contains(id)).ToList())
            _byId.Remove(leftover);

        if (_selected is not null && !_byId.ContainsKey(_selected.Id))
            Selected = null;

        RecomputeNow();
        OnPropertyChanged(nameof(EmptyLibrary));
        OnPropertyChanged(nameof(Searching));
    }

    private void RecomputeNow()
    {
        bool Playing(GameEntry game)
        {
            var state = _shell.RunState(game);
            return state.IsRunning || state.CanStop;
        }

        var picked = NowPicker.Pick(_games, _progress, _recent, Playing);
        picked = NowPicker.Retain(_games, picked, _holdNowId, Playing);
        if (picked is null)
        {
            Now = null;
            return;
        }

        NowKind = picked.Value.Kind;
        Now = Wrap(picked.Value.Game, discover: picked.Value.Kind == NowKind.Playing);
        OnPropertyChanged(nameof(NowMeta));
        OnPropertyChanged(nameof(NowPrimary));
        OnPropertyChanged(nameof(NowTransferring));
        OnPropertyChanged(nameof(NowPercent));
        OnPropertyChanged(nameof(NowIndeterminate));
        OnPropertyChanged(nameof(NowHero));
    }

    private void RefreshRunState()
    {
        foreach (var item in _byId.Values)
        {
            var state = _shell.RunState(item.Game, discoverExternal: false);
            item.IsRunning = state.IsRunning;
            item.CanStop = state.CanStop;
        }
    }

    private GameItemVm Wrap(GameEntry game, bool discover)
    {
        var state = _shell.RunState(game, discover);
        if (_byId.TryGetValue(game.Id, out var existing))
        {
            existing.Update(game, state.IsRunning, state.CanStop, _progress);
            return existing;
        }

        var vm = new GameItemVm(game, state.IsRunning, state.CanStop);
        vm.SetTransfer(_progress);
        _byId[game.Id] = vm;
        return vm;
    }

    private HashSet<string> LibraryPresence()
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var game in _games)
        {
            if (!UiFormat.IsLibraryRow(game)) continue;
            keys.Add(game.Id);
            foreach (var variant in game.Variants) keys.Add(variant.Id);
        }
        return keys;
    }

    private async Task RunAsync(Func<Task<object>> work)
    {
        Busy = true;
        try
        {
            var result = await work().ConfigureAwait(true);
            _status = ReadString(result, "message") ?? _status;
            OnPropertyChanged(nameof(StatusLine));
        }
        catch (Exception ex)
        {
            _status = ex.Message;
            OnPropertyChanged(nameof(StatusLine));
        }
        finally
        {
            Busy = false;
            ApplyGames(_services.Library.PeekCachedLibrary());
        }
    }

    private static string? ReadString(object result, string name)
    {
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(result);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(name, out var el) && el.ValueKind == System.Text.Json.JsonValueKind.String)
                return el.GetString();
        }
        catch { }
        return null;
    }

    private static bool ReadBool(object result, string name)
    {
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(result);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(name, out var el))
                return el.ValueKind == System.Text.Json.JsonValueKind.True;
        }
        catch { }
        return false;
    }

    private static void LogMilestone(string name) =>
        AppLog.Info($"PERF startup milestone={name} ms={Environment.TickCount64}");
}
