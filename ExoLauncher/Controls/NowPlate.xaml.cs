using ExoLauncher.Services;
using ExoLauncher.Ui;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ExoLauncher.Controls;

public sealed partial class NowPlate : UserControl
{
    public event EventHandler? Primary;
    public event EventHandler? Open;

    public NowPlate()
    {
        InitializeComponent();
    }

    public void Bind(ShellViewModel vm)
    {
        var game = vm.Now;
        Visibility = game is null ? Visibility.Collapsed : Visibility.Visible;
        if (game is null) return;

        TitleText.Text = vm.NowTitle;
        Kicker.Text = string.IsNullOrWhiteSpace(vm.NowKicker) ? "" : vm.NowKicker.ToUpperInvariant();
        MetaText.Text = vm.NowMeta;
        PrimaryLabel.Text = vm.NowPrimary;
        PrimaryButton.IsEnabled = !vm.Busy;
        PosterMono.Text = game.Monogram;
        PosterMono.Visibility = game.HasCover ? Visibility.Collapsed : Visibility.Visible;
        PosterImage.Source = game.CoverCopy(240);
        PosterImage.Visibility = game.HasCover ? Visibility.Visible : Visibility.Collapsed;

        if (vm.NowHero is not null)
        {
            WashBrush.ImageSource = new BitmapImage(vm.NowHero) { DecodePixelWidth = 1600 };
            WashHost.Opacity = 0.78;
        }
        else
        {
            var uri = CoverArtService.TryImageUri(game.Game);
            WashBrush.ImageSource = uri is null ? null : new BitmapImage(uri) { DecodePixelWidth = 1600 };
            WashHost.Opacity = uri is not null ? 0.7 : 0;
        }

        PrimaryIcon.Glyph = vm.NowTransferring || game.CanStop ? "\uE71A" : game.PrimaryAction is "install" or "update" ? "\uE896" : "\uE768";

        if (vm.NowTransferring)
        {
            Meter.Visibility = Visibility.Visible;
            Meter.IsIndeterminate = vm.NowIndeterminate;
            Meter.Value = vm.NowPercent ?? 0;
        }
        else
        {
            Meter.Visibility = Visibility.Collapsed;
            Meter.IsIndeterminate = false;
        }
    }

    public UIElement CoverElement => PosterImage;

    private void Primary_Click(object sender, RoutedEventArgs e) => Primary?.Invoke(this, EventArgs.Empty);
    private void Primary_Tapped(object sender, TappedRoutedEventArgs e) => e.Handled = true;
    private void Open_Click(object sender, RoutedEventArgs e) => Open?.Invoke(this, EventArgs.Empty);
    private void Poster_Click(object sender, RoutedEventArgs e) => Open?.Invoke(this, EventArgs.Empty);
    private void Root_Tapped(object sender, TappedRoutedEventArgs e) => Open?.Invoke(this, EventArgs.Empty);

    private void Primary_Pressed(object sender, PointerRoutedEventArgs e) =>
        TileMotion.Press(PrimaryButton, true);

    private void Primary_Released(object sender, PointerRoutedEventArgs e) =>
        TileMotion.Press(PrimaryButton, false);

    private void OnEntered(object sender, PointerRoutedEventArgs e)
    {
        TileMotion.Shine(Spot, true);
        TileMotion.Glare(PosterShine, 140, true);
    }

    private void OnExited(object sender, PointerRoutedEventArgs e)
    {
        TileMotion.Shine(Spot, false);
        TileMotion.Glare(PosterShine, 140, false);
        TileMotion.Depth(PosterTilt, default, PosterTilt.RenderSize, false);
        TileMotion.Parallax(WashHost, default, Root.RenderSize, false);
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        var at = e.GetCurrentPoint(Root).Position;
        TileMotion.Spotlight(Spot, SpotBrush, at, true, 480);
        TileMotion.Parallax(WashHost, at, Root.RenderSize, true, 10);
        var poster = e.GetCurrentPoint(PosterTilt).Position;
        TileMotion.Depth(PosterTilt, poster, PosterTilt.RenderSize, true, 12);
    }
}
