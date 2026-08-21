using ExoLauncher.Ui;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI.Text;

namespace ExoLauncher.Controls;

public sealed partial class SettingsView : UserControl
{
    public event EventHandler? Back;
    private ShellViewModel? _vm;
    private bool _suppressPlace;

    public SettingsView()
    {
        InitializeComponent();
    }

    public void Bind(ShellViewModel vm)
    {
        _vm = vm;
        var settings = vm.Settings;
        InstallRoot.Text = string.IsNullOrWhiteSpace(settings.DefaultInstallRoot)
            ? "Default folder"
            : settings.DefaultInstallRoot;
        TrophyToggle.IsOn = settings.TrophyNotificationsEnabled;
        VersionText.Text = "Exo Launcher " + vm.AppVersion;
        _suppressPlace = true;
        SelectPlace(settings.TrophyNotificationPosition);
        _suppressPlace = false;
        PaintStores(vm);
        Note.Text = vm.StatusLine ?? "";
        Note.Visibility = string.IsNullOrWhiteSpace(vm.StatusLine) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void PaintStores(ShellViewModel vm)
    {
        StoreList.Children.Clear();
        if (!vm.StoresReady)
        {
            StoreList.Children.Add(new TextBlock
            {
                Text = "Looking for store apps.",
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ExoMutedTextBrush"],
            });
            return;
        }
        var rows = vm.PresentStores;
        if (rows.Count == 0)
        {
            StoreList.Children.Add(new TextBlock
            {
                Text = "No store apps on this PC yet.",
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ExoMutedTextBrush"],
            });
            return;
        }
        foreach (var store in rows)
        {
            var open = new Button
            {
                Content = "Open",
                Tag = store.store,
                Style = (Style)Application.Current.Resources["ExoGhostButtonStyle"],
                VerticalAlignment = VerticalAlignment.Center,
            };
            open.Click += async (_, _) =>
            {
                if (_vm is null) return;
                await _vm.ShowStoreAsync((string)open.Tag);
            };

            var mark = new Border
            {
                Width = 36,
                Height = 36,
                CornerRadius = new CornerRadius(8),
                Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ExoRaisedFillBrush"],
                Child = new TextBlock
                {
                    Text = UiFormat.Monogram(store.displayName),
                    FontSize = 12,
                    FontWeight = new FontWeight { Weight = 600 },
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ExoPrimaryTextBrush"],
                },
            };

            var copy = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Spacing = 2,
            };
            copy.Children.Add(new TextBlock
            {
                Text = store.displayName,
                FontSize = 13,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ExoPrimaryTextBrush"],
            });
            if (!string.IsNullOrWhiteSpace(store.detail))
            {
                copy.Children.Add(new TextBlock
                {
                    Text = store.detail,
                    FontSize = 12,
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ExoMutedTextBrush"],
                    TextWrapping = TextWrapping.Wrap,
                });
            }

            var grid = new Grid { ColumnSpacing = 12 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(mark);
            Grid.SetColumn(copy, 1);
            grid.Children.Add(copy);
            Grid.SetColumn(open, 2);
            grid.Children.Add(open);

            StoreList.Children.Add(new Border
            {
                Padding = new Thickness(14, 12, 14, 12),
                CornerRadius = new CornerRadius(14),
                BorderThickness = new Thickness(1),
                BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ExoDividerBrush"],
                Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ExoCardFillBrush"],
                Child = grid,
            });
        }
    }

    private void SelectPlace(string place)
    {
        foreach (var item in TrophyPlace.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, place, StringComparison.OrdinalIgnoreCase))
            {
                TrophyPlace.SelectedItem = item;
                return;
            }
        }
        TrophyPlace.SelectedIndex = 2;
    }

    private void Back_Click(object sender, RoutedEventArgs e) => Back?.Invoke(this, EventArgs.Empty);
    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is not null) await _vm.AddFolderAsync();
    }
    private async void ChooseRoot_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is not null) await _vm.ChooseInstallRootAsync();
        if (_vm is not null) Bind(_vm);
    }
    private void Trophy_Toggled(object sender, RoutedEventArgs e)
    {
        _vm?.SetTrophyEnabled(TrophyToggle.IsOn);
    }
    private void Place_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressPlace || TrophyPlace.SelectedItem is not ComboBoxItem item || item.Tag is not string place)
            return;
        _vm?.SetTrophyPlace(place);
    }
    private void Preview_Click(object sender, RoutedEventArgs e) => _vm?.PreviewTrophy();
    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var result = await _vm.CheckUpdateAsync();
        Bind(_vm);
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(result);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var available = doc.RootElement.TryGetProperty("available", out var a) && a.ValueKind == System.Text.Json.JsonValueKind.True;
            InstallUpdateButton.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        }
        catch { }
    }
    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is not null) await _vm.InstallUpdateAsync();
    }
}
