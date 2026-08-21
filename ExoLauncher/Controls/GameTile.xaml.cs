using ExoLauncher.Ui;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace ExoLauncher.Controls;

public sealed partial class GameTile : UserControl
{
    public event EventHandler<GameItemVm>? Chosen;
    public event EventHandler<GameItemVm>? PinToggled;

    private GameItemVm? _item;
    private bool _entered;
    private bool _shadowReady;

    public GameTile()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) =>
        {
            if (!_shadowReady)
            {
                TileMotion.AttachShadow(ShadowHost, 160, 240);
                _shadowReady = true;
            }
            Bind();
            if (DataContext is GameItemVm)
                TileMotion.Entrance(Frame, Math.Abs(GetHashCode()) % 20);
        };
    }

    private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (_item is not null)
            _item.PropertyChanged -= OnItemChanged;
        Bind();
        if (_item is not null)
            _item.PropertyChanged += OnItemChanged;
    }

    private void OnItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (DispatcherQueue.HasThreadAccess) Bind();
        else DispatcherQueue.TryEnqueue(Bind);
    }

    public UIElement CoverElement => CoverHost;

    public void Bind()
    {
        if (DataContext is not GameItemVm item)
            return;
        _item = item;
        TitleText.Text = item.Title;
        StoreText.Text = item.StoreName;
        Mono.Text = item.Monogram;
        if (item.HasCover)
        {
            CoverImage.Source = item.CoverCopy(400);
            CoverImage.Visibility = Visibility.Visible;
            Mono.Visibility = Visibility.Collapsed;
        }
        else
        {
            CoverImage.Source = null;
            CoverImage.Visibility = Visibility.Collapsed;
            Mono.Visibility = Visibility.Visible;
        }
        Frame.Opacity = item.Dimmed ? 0.55 : 1;
        UpdateBadge.Visibility = item.UpdateAvailable ? Visibility.Visible : Visibility.Collapsed;
        Develop.Visibility = item.Transferring ? Visibility.Visible : Visibility.Collapsed;
        if (item.Develop is double ratio)
        {
            Develop.Width = 160 * ratio;
            Develop.Opacity = 1;
        }
        else if (item.WaitingTransfer)
        {
            Develop.Width = 160;
            Develop.Opacity = 0.35;
        }
        Pin.Visibility = item.CanPin && _entered ? Visibility.Visible : Visibility.Collapsed;
        PinIcon.Glyph = item.IsFavorite ? "\uE735" : "\uE734";
        CoverHost.BorderBrush = item.Selected
            ? (Brush)Application.Current.Resources["ExoPrimaryTextBrush"]
            : (Brush)Application.Current.Resources["ExoDividerBrush"];
        CoverHost.BorderThickness = new Thickness(item.Selected ? 1.5 : 1);
        TitleText.Foreground = item.Selected
            ? (Brush)Application.Current.Resources["ExoPrimaryTextBrush"]
            : (Brush)Application.Current.Resources["ExoSecondaryTextBrush"];
    }

    private void Hit_Click(object sender, RoutedEventArgs e)
    {
        if (_item is not null) Chosen?.Invoke(this, _item);
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        if (_item is not null) PinToggled?.Invoke(this, _item);
    }

    private void OnEntered(object sender, PointerRoutedEventArgs e)
    {
        _entered = true;
        if (_item?.CanPin == true) Pin.Visibility = Visibility.Visible;
        TitleText.Foreground = (Brush)Application.Current.Resources["ExoPrimaryTextBrush"];
        TileMotion.Hover(Frame, true);
        TileMotion.Glare(Shine, 160, true);
        TileMotion.Shadow(ShadowHost, true);
    }

    private void OnExited(object sender, PointerRoutedEventArgs e)
    {
        _entered = false;
        Pin.Visibility = Visibility.Collapsed;
        TitleText.Foreground = _item?.Selected == true
            ? (Brush)Application.Current.Resources["ExoPrimaryTextBrush"]
            : (Brush)Application.Current.Resources["ExoSecondaryTextBrush"];
        TileMotion.Hover(Frame, false);
        TileMotion.Glare(Shine, 160, false);
        TileMotion.Shine(Spot, false);
        TileMotion.Shadow(ShadowHost, false);
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        var at = e.GetCurrentPoint(CoverHost).Position;
        TileMotion.Spotlight(Spot, SpotBrush, at, true, 140);
    }

    private void OnPressed(object sender, PointerRoutedEventArgs e) =>
        TileMotion.Press(Frame, true, _entered);

    private void OnReleased(object sender, PointerRoutedEventArgs e) =>
        TileMotion.Press(Frame, false, _entered);
}
