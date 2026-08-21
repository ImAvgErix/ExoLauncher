using System.Text.Json;
using ExoLauncher.Services;
using ExoLauncher.Ui;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ExoLauncher.Controls;

public sealed partial class GamePlate : UserControl
{
    public event EventHandler? CloseRequested;
    public event EventHandler? PrimaryRequested;
    public event EventHandler? RemoveRequested;
    public event EventHandler? FolderRequested;
    public event EventHandler? RepairRequested;
    public event EventHandler? ApplyRequested;
    public event EventHandler<string>? BuyRequested;

    private GameItemVm? _item;
    private bool _removeArmed;
    private string? _buyUrl;

    public GamePlate()
    {
        InitializeComponent();
    }

    public const int MenuWidth = 400;

    public void Slide(bool open, Action? done = null)
    {
        try
        {
            if (!TileMotion.Enabled)
            {
                Drawer.X = open ? 0 : MenuWidth;
                done?.Invoke();
                return;
            }

            var anim = new DoubleAnimation
            {
                To = open ? 0 : MenuWidth,
                Duration = TimeSpan.FromMilliseconds(open ? 280 : 200),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(anim, Drawer);
            Storyboard.SetTargetProperty(anim, "X");
            var sb = new Storyboard();
            sb.Children.Add(anim);
            if (done is not null)
                sb.Completed += (_, _) => done();
            sb.Begin();
        }
        catch
        {
            Drawer.X = open ? 0 : MenuWidth;
            done?.Invoke();
        }
    }

    public void Bind(GameItemVm? item, ShellViewModel vm)
    {
        try
        {
            BindCore(item, vm);
        }
        catch
        {
            _item = item;
        }
    }

    private void BindCore(GameItemVm? item, ShellViewModel vm)
    {
        _item = item;
        if (item is null) return;

        TitleText.Text = item.Title;
        StoreText.Text = string.IsNullOrWhiteSpace(item.StoreName) ? "" : item.StoreName.ToUpperInvariant();
        Cover.Source = CoverCopy(item);
        var facts = new List<string>();
        if (item.Playtime is not null) facts.Add(item.Playtime);
        if (item.LastPlayed is not null) facts.Add(item.LastPlayed);
        var size = UiFormat.Size(item.Game.SizeBytes);
        if (size is not null) facts.Add(size);
        Facts.Text = string.Join(" · ", facts);
        Note.Text = item.Game.LaunchNote;
        PrimaryButton.Content = UiFormat.PrimaryLabel(item.Game, vm.NowTransferring && NowPicker.Matches(item.Game, vm.Progress?.GameId ?? ""), item.CanStop);
        PrimaryButton.IsEnabled = !vm.Busy;
        RemoveButton.Visibility = item.Installed ? Visibility.Visible : Visibility.Collapsed;
        RemoveButton.Content = _removeArmed ? "Confirm remove" : "Remove";
        _buyUrl = UiFormat.BuyUrl(item.Game);
        BuyLink.Visibility = string.IsNullOrEmpty(_buyUrl) ? Visibility.Collapsed : Visibility.Visible;

        if (vm.Progress is { IsActive: true } p && NowPicker.Matches(item.Game, p.GameId))
        {
            Meter.Visibility = Visibility.Visible;
            Meter.IsIndeterminate = UiFormat.VisiblePercent(p.Percent) is null;
            Meter.Value = UiFormat.VisiblePercent(p.Percent) ?? 0;
        }
        else
        {
            Meter.Visibility = Visibility.Collapsed;
        }

        _ = LoadExtrasAsync(item, vm);
    }

    private static BitmapImage? CoverCopy(GameItemVm item)
    {
        var uri = CoverArtService.TryImageUri(item.Game);
        if (uri is null) return null;
        try { return new BitmapImage(uri) { DecodePixelWidth = 240 }; }
        catch { return null; }
    }

    private async Task LoadExtrasAsync(GameItemVm item, ShellViewModel vm)
    {
        try
        {
            var extras = vm.Extras(item);
            var json = JsonSerializer.Serialize(extras);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("repairLabel", out var label) && label.ValueKind == JsonValueKind.String)
                RepairButton.Content = label.GetString();
            RepairButton.Visibility = doc.RootElement.TryGetProperty("canRepair", out var can) && can.ValueKind == JsonValueKind.True
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        catch { }

        try
        {
            var status = await vm.DlssStatusAsync(item);
            var json = JsonSerializer.Serialize(status);
            using var doc = JsonDocument.Parse(json);
            var message = doc.RootElement.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String
                ? msg.GetString()
                : null;
            var items = doc.RootElement.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array
                ? arr.GetArrayLength()
                : 0;
            Upscaler.Text = items > 0
                ? (message ?? "Upscaler files found.")
                : (message ?? "");
            ApplyButton.Visibility = items > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch
        {
            Upscaler.Text = "";
            ApplyButton.Visibility = Visibility.Collapsed;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        _removeArmed = false;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Primary_Click(object sender, RoutedEventArgs e) => PrimaryRequested?.Invoke(this, EventArgs.Empty);
    private void Primary_Pressed(object sender, PointerRoutedEventArgs e) => TileMotion.Press(PrimaryButton, true);
    private void Primary_Released(object sender, PointerRoutedEventArgs e) => TileMotion.Press(PrimaryButton, false);
    private void Folder_Click(object sender, RoutedEventArgs e) => FolderRequested?.Invoke(this, EventArgs.Empty);
    private void Repair_Click(object sender, RoutedEventArgs e) => RepairRequested?.Invoke(this, EventArgs.Empty);
    private void Apply_Click(object sender, RoutedEventArgs e) => ApplyRequested?.Invoke(this, EventArgs.Empty);
    private void Buy_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_buyUrl)) BuyRequested?.Invoke(this, _buyUrl);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (!_removeArmed)
        {
            _removeArmed = true;
            RemoveButton.Content = "Confirm remove";
            return;
        }
        _removeArmed = false;
        RemoveRequested?.Invoke(this, EventArgs.Empty);
    }
}
