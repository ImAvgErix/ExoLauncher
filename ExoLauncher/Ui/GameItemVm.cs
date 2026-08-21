using ExoLauncher.Models;
using ExoLauncher.Services;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ExoLauncher.Ui;

public sealed class GameItemVm : BindableBase
{
    private GameEntry _game;
    private bool _isRunning;
    private bool _canStop;
    private bool _selected;
    private InstallProgress? _transfer;
    private ImageSource? _cover;

    public GameItemVm(GameEntry game, bool isRunning, bool canStop)
    {
        _game = game;
        _isRunning = isRunning;
        _canStop = canStop;
        _cover = LoadCover(game);
    }

    public GameEntry Game => _game;
    public string Id => _game.Id;
    public string Title => _game.Title;
    public string StoreName => UiFormat.StoreLabel(_game.Store);
    public string Monogram => UiFormat.Monogram(_game.Title);
    public bool Installed => _game.Installed;
    public bool IsFavorite => _game.IsFavorite;
    public bool CanPin => _game.Installed;
    public bool UpdateAvailable => _game.UpdateAvailable && _transfer is not { IsActive: true };
    public bool Dimmed => !_game.Installed && _transfer is not { IsActive: true };
    public bool Transferring => _transfer is { IsActive: true };
    public double? Develop => UiFormat.DevelopRatio(_transfer);
    public bool WaitingTransfer => Transferring && Develop is null;
    public string PrimaryAction => UiFormat.ResolvePrimaryAction(_game);
    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (Set(ref _isRunning, value)) OnPropertyChanged(nameof(CanStop));
        }
    }
    public bool CanStop
    {
        get => _canStop || _isRunning;
        set => Set(ref _canStop, value);
    }
    public bool Selected
    {
        get => _selected;
        set => Set(ref _selected, value);
    }
    public ImageSource? Cover
    {
        get => _cover;
        private set
        {
            if (Set(ref _cover, value)) OnPropertyChanged(nameof(HasCover));
        }
    }
    public bool HasCover => _cover is not null;
    public string? Playtime => UiFormat.Playtime(_game.PlaytimeMinutes);
    public string? LastPlayed => _game.LastPlayedUtc is null
        ? null
        : Relative(_game.LastPlayedUtc.Value);

    public void Update(GameEntry game, bool isRunning, bool canStop, InstallProgress? transfer)
    {
        var coverChanged = !string.Equals(_game.CoverUrl, game.CoverUrl, StringComparison.Ordinal);
        _game = game;
        _isRunning = isRunning;
        _canStop = canStop;
        _transfer = transfer is { IsActive: true } && NowPicker.Matches(game, transfer.GameId)
            ? transfer
            : null;
        if (coverChanged || _cover is null)
            Cover = LoadCover(game);
        OnPropertyChanged(nameof(Game));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(StoreName));
        OnPropertyChanged(nameof(Installed));
        OnPropertyChanged(nameof(IsFavorite));
        OnPropertyChanged(nameof(CanPin));
        OnPropertyChanged(nameof(UpdateAvailable));
        OnPropertyChanged(nameof(Dimmed));
        OnPropertyChanged(nameof(Transferring));
        OnPropertyChanged(nameof(Develop));
        OnPropertyChanged(nameof(WaitingTransfer));
        OnPropertyChanged(nameof(PrimaryAction));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(Playtime));
        OnPropertyChanged(nameof(LastPlayed));
        OnPropertyChanged(nameof(Monogram));
        OnPropertyChanged(nameof(HasCover));
    }

    public void SetTransfer(InstallProgress? progress)
    {
        var next = progress is { IsActive: true } && NowPicker.Matches(_game, progress.GameId)
            ? progress
            : null;
        _transfer = next;
        OnPropertyChanged(nameof(Transferring));
        OnPropertyChanged(nameof(Develop));
        OnPropertyChanged(nameof(WaitingTransfer));
        OnPropertyChanged(nameof(UpdateAvailable));
        OnPropertyChanged(nameof(Dimmed));
    }

    public BitmapImage? CoverCopy(int decodeWidth = 400)
    {
        var uri = CoverArtService.TryImageUri(_game);
        if (uri is null) return null;
        try { return new BitmapImage(uri) { DecodePixelWidth = decodeWidth }; }
        catch { return null; }
    }

    private static ImageSource? LoadCover(GameEntry game)
    {
        var uri = CoverArtService.TryImageUri(game);
        if (uri is null) return null;
        try
        {
            return new BitmapImage(uri) { DecodePixelWidth = 400 };
        }
        catch
        {
            return null;
        }
    }

    private static string Relative(DateTimeOffset when)
    {
        var span = DateTimeOffset.UtcNow - when.ToUniversalTime();
        if (span.TotalMinutes < 2) return "Just now";
        if (span.TotalHours < 1) return $"{Math.Max(1, (int)span.TotalMinutes)}m ago";
        if (span.TotalDays < 1) return $"{Math.Max(1, (int)span.TotalHours)}h ago";
        if (span.TotalDays < 14) return $"{Math.Max(1, (int)span.TotalDays)}d ago";
        return when.ToLocalTime().ToString("d");
    }
}
