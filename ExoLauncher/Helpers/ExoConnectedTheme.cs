using ExoLauncher.Shell.Scene;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace ExoLauncher.Helpers;

internal static class ExoConnectedTheme
{
    private static ControlTemplate? _buttonTemplate;
    internal static SolidColorBrush Brush(Windows.UI.Color color) => new(color);
    internal static TextBlock Text(string text, double size = 13) => new() {
        Text = text, FontFamily = new FontFamily(Tokens.InterVariable), FontSize = size,
        Foreground = Brush(Tokens.Fg), TextWrapping = TextWrapping.Wrap,
    };
    internal static FrameworkElement Heading(string title)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        row.Children.Add((FrameworkElement)XamlReader.Load("""
            <Viewbox xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Width="24" Height="24">
              <Path Fill="#FFFFFF" Data="M7,4 L23,4 L22,8 L6,8 Z M6,11 L17,11 L16,15 L5,15 Z M4,18 L20,18 L19,22 L3,22 Z"/>
            </Viewbox>
            """));
        var label = Text(title, 16); label.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(label);
        return row;
    }
    internal static Button Button(string label, bool primary = false)
    {
        var button = new Button { Content = label, FontFamily = new FontFamily(Tokens.InterVariable), FontSize = 13,
            Background = Brush(primary ? Tokens.Fg : Tokens.Elevated), Foreground = Brush(primary ? Tokens.PrimaryInk : Tokens.Fg),
            BorderBrush = Brush(Tokens.Line), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9),
            Height = 36, Padding = new Thickness(14, 0, 14, 0), UseSystemFocusVisuals = true,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            FocusVisualPrimaryBrush = Brush(Tokens.Fg), FocusVisualSecondaryBrush = Brush(Tokens.Bg) };
        button.Template = _buttonTemplate ??= (ControlTemplate)XamlReader.Load("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="Button">
              <Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="{TemplateBinding CornerRadius}" Padding="{TemplateBinding Padding}">
                <ContentPresenter Content="{TemplateBinding Content}" FontFamily="{TemplateBinding FontFamily}" FontSize="{TemplateBinding FontSize}" Foreground="{TemplateBinding Foreground}" HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="{TemplateBinding VerticalContentAlignment}"/>
              </Border>
            </ControlTemplate>
            """);
        button.PointerEntered += (_, _) => { if (button.IsEnabled) button.Opacity = .75; };
        button.PointerExited += (_, _) => button.Opacity = button.IsEnabled ? 1 : .35;
        button.IsEnabledChanged += (_, _) => button.Opacity = button.IsEnabled ? 1 : .35;
        return button;
    }
    internal static void Resources(ResourceDictionary resources)
    {
        // Keep native keyboard/accessibility behavior, replace Fluent paint.
        foreach (var key in new[] { "MenuFlyoutPresenterBackground", "TextControlBackground", "TextControlBackgroundFocused", "TextControlBackgroundPointerOver", "ListViewItemBackground" }) resources[key] = Brush(Tokens.Surface);
        foreach (var key in new[] { "MenuFlyoutPresenterBorderBrush", "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused", "MenuFlyoutSeparatorBackground" }) resources[key] = Brush(Tokens.Line);
        foreach (var key in new[] { "MenuFlyoutItemForeground", "MenuFlyoutItemForegroundPointerOver", "MenuFlyoutItemForegroundPressed", "MenuFlyoutSubItemForeground", "TextControlForeground", "TextControlForegroundFocused", "ListViewItemForeground", "ListViewItemForegroundSelected", "ListViewItemForegroundSelectedPointerOver", "ListViewItemForegroundSelectedPressed" }) resources[key] = Brush(Tokens.Fg);
        foreach (var key in new[] { "MenuFlyoutItemBackgroundPointerOver", "MenuFlyoutItemBackgroundPressed", "MenuFlyoutSubItemBackgroundPointerOver", "MenuFlyoutSubItemBackgroundSubMenuOpened", "ListViewItemBackgroundPointerOver", "ListViewItemBackgroundSelected", "ListViewItemBackgroundSelectedPointerOver", "ListViewItemBackgroundSelectedPressed" }) resources[key] = Brush(Tokens.NavOn);
        foreach (var key in new[] { "SystemControlHighlightAccentBrush", "SystemControlHighlightListAccentLowBrush", "ListViewItemSelectionIndicatorBrush", "ListViewItemCheckBoxBrush" }) resources[key] = Brush(Tokens.Muted);
        resources["TextControlSelectionHighlightColor"] = Tokens.Muted;
        resources["MenuFlyoutItemFontFamily"] = new FontFamily(Tokens.InterVariable);
    }
    internal static Grid WindowContent(Window window, string title, FrameworkElement content, bool compact = false)
    {
        var root = new Grid { Background = Brush(Tokens.Bg), RequestedTheme = ElementTheme.Dark };
        Resources(root.Resources);
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(52) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var header = new Grid { Padding = new Thickness(18, 0, 12, 0), BorderBrush = Brush(Tokens.LineSoft), BorderThickness = new Thickness(0, 0, 0, 1) };
        header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var drag = new Grid { Background = Brush(Tokens.Bg) };
        var heading = Heading(title); heading.VerticalAlignment = VerticalAlignment.Center; drag.Children.Add(heading); header.Children.Add(drag);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        void Action(string glyph, string name, Action click)
        {
            var button = Button(glyph); button.Width = 32; button.Height = 30; button.Padding = new Thickness(0);
            AutomationProperties.SetName(button, name); ToolTipService.SetToolTip(button, name); button.Click += (_, _) => click(); actions.Children.Add(button);
        }
        if (!compact) {
            Action("−", "Minimize", () => (window.AppWindow.Presenter as OverlappedPresenter)?.Minimize());
            Action("□", "Maximize or restore", () => { if (window.AppWindow.Presenter is OverlappedPresenter p) { if (p.State == OverlappedPresenterState.Maximized) p.Restore(); else p.Maximize(); } });
        }
        Action("×", "Close", window.Close);
        Grid.SetColumn(actions, 1); header.Children.Add(actions); root.Children.Add(header);
        Grid.SetRow(content, 1); root.Children.Add(content); window.Content = root;
        window.ExtendsContentIntoTitleBar = true; window.SetTitleBar(drag);
        if (window.AppWindow.Presenter is OverlappedPresenter presenter) {
            presenter.SetBorderAndTitleBar(true, false);
            presenter.IsMinimizable = !compact; presenter.IsMaximizable = !compact;
        }
        return root;
    }
}
