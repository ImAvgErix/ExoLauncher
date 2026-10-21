using ExoLauncher.Helpers;
using ExoLauncher.Shell.Scene;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;

namespace ExoLauncher;

public sealed partial class MainWindow
{
    private void ConfigureConnectedChrome(CoreWebView2 core, WebView2 surface, string label, Func<string, bool> trusted)
    {
        // Theme browser-owned fallback surfaces as well as Exo controls.
        ExoConnectedTheme.Resources(surface.Resources);
        core.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Dark;
        core.Settings.AreDefaultContextMenusEnabled = true;
        core.ContextMenuRequested += (_, args) => ShowConnectedContextMenu(surface, args);
        core.Settings.AreDefaultScriptDialogsEnabled = false;
        core.ScriptDialogOpening += (_, args) => ShowConnectedScriptDialogAsync(surface, label, trusted, args);
    }

    private static void StyleConnectedDialog(ContentDialog dialog) => ExoConnectedTheme.Dialog(dialog);

    private void ShowConnectedContextMenu(WebView2 surface, CoreWebView2ContextMenuRequestedEventArgs args)
    {
        if (_closingWindow || surface.XamlRoot is null) { args.Handled = true; return; }
        var deferral = args.GetDeferral();
        var finished = false;
        var menu = new MenuFlyout();
        void Complete()
        {
            if (finished) return;
            finished = true;
            surface.Unloaded -= OnUnloaded;
            try { deferral.Complete(); } catch { }
        }
        void OnUnloaded(object sender, RoutedEventArgs e) { menu.Hide(); Complete(); }
        try
        {
            var presenter = new Style(typeof(MenuFlyoutPresenter));
            presenter.Setters.Add(new Setter(FrameworkElement.RequestedThemeProperty, ElementTheme.Dark));
            presenter.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Tokens.Surface)));
            presenter.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(Tokens.Line)));
            presenter.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
            presenter.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(10)));
            presenter.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(5)));
            presenter.Setters.Add(new Setter(Control.FontFamilyProperty, new FontFamily(Tokens.InterVariable)));
            menu.MenuFlyoutPresenterStyle = presenter;
            foreach (var item in args.MenuItems) menu.Items.Add(BuildConnectedMenuItem(item, command => args.SelectedCommandId = command));
            menu.Closed += (_, _) => Complete();
            surface.Unloaded += OnUnloaded;
            args.Handled = true;
            if (menu.Items.Count == 0) { Complete(); return; }
            var scale = surface.XamlRoot.RasterizationScale;
            menu.ShowAt(surface, new FlyoutShowOptions {
                Position = new Windows.Foundation.Point(args.Location.X / scale, args.Location.Y / scale),
                ShowMode = FlyoutShowMode.Standard,
            });
        }
        catch
        {
            // A menu cannot block the browser if its surface is going away.
            args.Handled = false;
            Complete();
        }
    }

    private static MenuFlyoutItemBase BuildConnectedMenuItem(CoreWebView2ContextMenuItem source, Action<int> select)
    {
        if (source.Kind == CoreWebView2ContextMenuItemKind.Separator) return new MenuFlyoutSeparator();
        var text = source.Label.Replace("&&", "\u0001").Replace("&", "").Replace("\u0001", "&");
        if (source.Kind == CoreWebView2ContextMenuItemKind.Submenu)
        {
            var submenu = new MenuFlyoutSubItem { Text = text, IsEnabled = source.IsEnabled, FontFamily = new FontFamily(Tokens.InterVariable) };
            foreach (var child in source.Children) submenu.Items.Add(BuildConnectedMenuItem(child, select));
            return submenu;
        }
        MenuFlyoutItem item = source.Kind is CoreWebView2ContextMenuItemKind.CheckBox or CoreWebView2ContextMenuItemKind.Radio
            ? new ToggleMenuFlyoutItem { IsChecked = source.IsChecked } : new MenuFlyoutItem();
        item.FontFamily = new FontFamily(Tokens.InterVariable);
        item.Text = text;
        item.IsEnabled = source.IsEnabled;
        item.KeyboardAcceleratorTextOverride = source.ShortcutKeyDescription;
        item.Click += (_, _) => select(source.CommandId);
        return item;
    }

    private async void ShowConnectedScriptDialogAsync(WebView2 surface, string label, Func<string, bool> trusted,
        CoreWebView2ScriptDialogOpeningEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            if (!trusted(args.Uri) || _closingWindow) return;
            await _connectedDialogGate.WaitAsync(_lifetimeCts.Token);
            try
            {
                if (_closingWindow || surface.XamlRoot is null) return;
                var prompt = args.Kind == CoreWebView2ScriptDialogKind.Prompt;
                var alert = args.Kind == CoreWebView2ScriptDialogKind.Alert;
                var leaving = args.Kind == CoreWebView2ScriptDialogKind.Beforeunload;
                var content = new StackPanel { Spacing = 16, MaxWidth = 460 };
                content.Children.Add(new TextBlock { Text = args.Message, TextWrapping = TextWrapping.Wrap, MaxHeight = 280 });
                TextBox? input = null;
                if (prompt) { input = new TextBox { Text = args.DefaultText }; content.Children.Add(input); }
                var dialog = new ContentDialog {
                    XamlRoot = surface.XamlRoot, Title = label + " · Exo", Content = content,
                    PrimaryButtonText = leaving ? "Leave page" : "OK",
                    CloseButtonText = alert ? "" : leaving ? "Stay" : "Cancel",
                    DefaultButton = alert ? ContentDialogButton.Primary : ContentDialogButton.Close,
                };
                StyleConnectedDialog(dialog);
                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                {
                    if (input is not null) args.ResultText = input.Text;
                    args.Accept();
                }
            }
            finally { _connectedDialogGate.Release(); }
        }
        catch { /* Dismiss if the requesting page or window closed. */ }
        finally { try { deferral.Complete(); } catch { } }
    }
}
