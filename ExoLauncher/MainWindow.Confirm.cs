using ExoLauncher.Helpers;
using ExoLauncher.Shell.Scene;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace ExoLauncher;

public sealed partial class MainWindow
{
    /// <summary>
    /// A question Exo's own window asks before something that costs money or can't be taken back, outside the page: a page
    /// that is no longer trustworthy can't answer it. False when declined, closed over, or the window can't show it.
    /// </summary>
    /// <param name="danger">The action destroys something (its words in Bad), else it is the dialog's primary.</param>
    internal async Task<bool> ConfirmNativeAsync(string title, string body, string action, bool danger = false)
    {
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(async () => answer.TrySetResult(await ShowConfirmAsync(title, body, action, danger))))
            return false;
        return await answer.Task.ConfigureAwait(false);
    }

    /// <summary>Asks with <see cref="Question"/> once the window's other questions are answered.</summary>
    private async Task<bool> ShowConfirmAsync(string title, string body, string action, bool danger)
    {
        try
        {
            if (_closingWindow || WebHost.XamlRoot is not { } root) return false;
            await _connectedDialogGate.WaitAsync(_lifetimeCts.Token);
            try
            {
                if (_closingWindow || WebHost.XamlRoot is null) return false;
                var (dialog, answered) = Question(root, title, body, action, danger);
                await dialog.ShowAsync();
                return answered();
            }
            finally { _connectedDialogGate.Release(); }
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or System.Runtime.InteropServices.COMException) { return false; }
    }

    /// <summary>
    /// Every question Exo's own window asks (this one, a connected app's alert, confirm, prompt and permission), drawn as
    /// the page draws its dialogs (DESIGN.md, Confirm; Liquid Glass large): the overlay's gray over what is behind it, the
    /// light rim, 18px corners, a 20px title and 13px words in Inter centered, a field when it takes one, then equal 32px
    /// capsules on one row, <paramref name="cancel"/> first and the action as a primary or in Bad, over the scrim, in the
    /// Appearance the page is in. Cancel has the focus: Enter alone never confirms, and Escape closes it. No action: the
    /// one capsule. Exo draws it over the page, so no page can answer it; <c>Answered</c> says whether the action was pressed.
    /// </summary>
    private static (ContentDialog Dialog, Func<bool> Answered) Question(XamlRoot root, string title, string body, string? action,
        bool danger = false, string cancel = "Cancel", FrameworkElement? field = null)
    {
        var appearance = App.Services.Settings.Current.Appearance;
        if (appearance == "system") appearance = WindowsAppsLight() ? "light" : "dark";
        var look = ConfirmLook.For(appearance);
        var confirmed = false;
        var dialog = new ContentDialog
        {
            XamlRoot = root, RequestedTheme = appearance == "light" ? ElementTheme.Light : ElementTheme.Dark,
            FontFamily = new FontFamily(Tokens.InterVariable), Foreground = new SolidColorBrush(look.Ink),
            Background = new AcrylicBrush { TintColor = look.Glass, TintOpacity = 0.78, TintLuminosityOpacity = 0.85, FallbackColor = look.Glass },
            BorderBrush = new SolidColorBrush(look.Rim), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(18),
        };
        var resources = dialog.Resources;
        resources["ContentDialogBackground"] = dialog.Background;
        resources["ContentDialogTopOverlay"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        resources["ContentDialogBorderBrush"] = dialog.BorderBrush;
        resources["ContentDialogSeparatorBorderBrush"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        resources["ContentDialogSmokeFill"] = new SolidColorBrush(look.Scrim);
        resources["ContentDialogCornerRadius"] = new CornerRadius(18);
        resources["ContentDialogPadding"] = new Thickness(28, 28, 28, 24);
        resources["ContentDialogMinWidth"] = 400.0;
        resources["ContentDialogMaxWidth"] = 420.0;

        var content = new StackPanel();
        content.Children.Add(new TextBlock
        {
            Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(look.Ink),
            TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
        });
        content.Children.Add(new TextBlock
        {
            Text = body, FontSize = 13, LineHeight = 19.5, Foreground = new SolidColorBrush(look.Secondary),
            TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
        });
        if (field is not null)
        {
            // In the dialog's colors, as the page's fields: the control fill under the rim, the ink's edge when focused.
            (string[] Keys, Color Color)[] paint =
            [
                (["TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused"], look.Fill),
                (["TextControlBorderBrush", "TextControlBorderBrushPointerOver"], look.Rim),
                (["TextControlBorderBrushFocused"], look.Ink),
                (["TextControlForeground", "TextControlForegroundPointerOver", "TextControlForegroundFocused"], look.Ink),
            ];
            foreach (var (keys, color) in paint)
                foreach (var key in keys) field.Resources[key] = new SolidColorBrush(color);
            if (field is Control control) { control.CornerRadius = new CornerRadius(16); control.FontFamily = new FontFamily(Tokens.InterVariable); control.FontSize = 13; }
            field.Margin = new Thickness(0, 12, 0, 0);
            content.Children.Add(field);
        }
        var no = Capsule(cancel, look.Fill, look.Ink, look.Hover);
        no.Click += (_, _) => dialog.Hide();
        var buttons = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 24, 0, 0) };
        buttons.ColumnDefinitions.Add(new ColumnDefinition());
        buttons.Children.Add(no);
        if (action is not null)
        {
            var go = danger ? Capsule(action, look.Fill, look.Bad, look.Hover) : Capsule(action, look.Ink, look.PrimaryInk, look.PrimaryHover);
            go.Click += (_, _) => { confirmed = true; dialog.Hide(); };
            buttons.ColumnDefinitions.Add(new ColumnDefinition());
            Grid.SetColumn(go, 1);
            buttons.Children.Add(go);
        }
        content.Children.Add(buttons);
        dialog.Content = content;
        dialog.Opened += (_, _) => no.Focus(FocusState.Programmatic);
        return (dialog, () => confirmed);
    }

    // One of the dialog's 32px capsules, Exo's .exo-set-btn: its fill, its words, a lighter fill under the pointer.
    private static Button Capsule(string label, Color fill, Color ink, Color hover)
    {
        var button = ExoConnectedTheme.Button(label);
        button.Height = 32;
        button.CornerRadius = new CornerRadius(16);
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.FontWeight = FontWeights.Medium;
        button.BorderThickness = new Thickness(0);
        button.Background = new SolidColorBrush(fill);
        button.Foreground = new SolidColorBrush(ink);
        // A lighter fill under the pointer, as the page's buttons lift; not the shared helper's fade.
        button.PointerEntered += (_, _) => { button.Background = new SolidColorBrush(hover); button.Opacity = 1; };
        button.PointerExited += (_, _) => button.Background = new SolidColorBrush(fill);
        return button;
    }

    /// <summary>The page's tokens for a dialog (ui/src/tokens.css), per Appearance.</summary>
    private sealed record ConfirmLook(Color Glass, Color Rim, Color Ink, Color Secondary, Color Fill, Color Hover, Color Bad,
        Color PrimaryInk, Color PrimaryHover, Color Scrim)
    {
        internal static ConfirmLook For(string appearance) => appearance switch
        {
            "light" => new(Rgb(245, 245, 247), Color.FromArgb(26, 0, 0, 0), Rgb(0x1e, 0x1e, 0x1e), Rgb(0x4a, 0x4a, 0x4a),
                Color.FromArgb(184, 255, 255, 255), Color.FromArgb(255, 0xec, 0xec, 0xee), Rgb(0xc4, 0x20, 0x20), Rgb(0xff, 0xff, 0xff), Rgb(0x3a, 0x3a, 0x3a),
                Color.FromArgb(77, 0, 0, 0)),
            "dark" => new(Rgb(44, 44, 46), Color.FromArgb(36, 255, 255, 255), Rgb(0xe0, 0xe0, 0xe0), Rgb(0xc8, 0xc8, 0xc8),
                Color.FromArgb(20, 245, 245, 247), Color.FromArgb(46, 255, 255, 255), Rgb(0xff, 0x79, 0x79), Rgb(0x12, 0x12, 0x12), Rgb(0xcf, 0xcf, 0xcf),
                Color.FromArgb(77, 0, 0, 0)),
            _ => new(Rgb(44, 44, 46), Color.FromArgb(36, 255, 255, 255), Rgb(0xff, 0xff, 0xff), Rgb(0xe4, 0xe4, 0xe4),
                Color.FromArgb(20, 245, 245, 247), Color.FromArgb(46, 255, 255, 255), Rgb(0xff, 0x79, 0x79), Rgb(0, 0, 0), Rgb(0xe8, 0xe8, 0xe8),
                Color.FromArgb(153, 0, 0, 0)),
        };

        private static Color Rgb(byte r, byte g, byte b) => Color.FromArgb(255, r, g, b);
    }
}
