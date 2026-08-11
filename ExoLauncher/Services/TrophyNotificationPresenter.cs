using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using ExoLauncher.Models;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI;
using Windows.UI.ViewManagement;
using WinRT.Interop;

namespace ExoLauncher.Services;

/// <summary>
/// Native no-activate trophy surface. It is created only while a notification
/// is visible, stays out of Alt+Tab/the taskbar, and serializes bursts so unlocks
/// are not stacked over the game.
/// </summary>
internal sealed class TrophyNotificationPresenter : IDisposable
{
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExNoActivate = 0x08000000L;
    private const int DwmWindowCornerPreference = 33;
    private const int DwmWindowCornerPreferenceRound = 2;
    private const int NotificationWidth = 432;
    private const int NotificationHeight = 122;
    private static readonly TimeSpan NotificationDuration = TimeSpan.FromMilliseconds(3500);
    private static readonly Color TrophySurface = Color.FromArgb(255, 0, 0, 0);

    private readonly Queue<(TrophyNotificationPayload Payload, TrophyNotificationOptions Options, Action? OnPresented)> _queue = new();
    private readonly DispatcherQueue _dispatcher;
    private Window? _window;
    private Border? _card;
    private DispatcherQueueTimer? _timer;
    private Storyboard? _exitStoryboard;
    private TrophyMotion _motion;
    private bool _closing;
    private bool _disposed;

    public TrophyNotificationPresenter(DispatcherQueue dispatcher) => _dispatcher = dispatcher;

    public void Enqueue(
        TrophyNotificationPayload payload,
        AppSettings settings,
        Action? onPresented = null)
    {
        if (_disposed) return;
        _queue.Enqueue((payload, TrophyNotificationOptions.From(settings), onPresented));
        if (_window is null) ShowNext();
    }

    private void ShowNext()
    {
        if (_disposed || _window is not null || _queue.Count == 0) return;
        var (payload, options, onPresented) = _queue.Dequeue();

        try
        {
            var window = new Window { Title = "Achievement notification" };
            var card = BuildCard(payload);
            window.Content = new Border
            {
                // The backing surface, card, and DWM corner treatment share
                // one 12px silhouette. This avoids a darker square appearing
                // around the inner plate during entry/exit frames.
                Background = new SolidColorBrush(TrophySurface),
                CornerRadius = new CornerRadius(12),
                Child = card,
            };

            var hwnd = WindowNative.GetWindowHandle(window);
            var appWindow = window.AppWindow;
            appWindow.IsShownInSwitchers = false;
            if (appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
                presenter.IsAlwaysOnTop = true;
                presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
            }

            var exStyle = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
            SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(exStyle | WsExToolWindow | WsExNoActivate));
            var cornerPreference = DwmWindowCornerPreferenceRound;
            try { _ = DwmSetWindowAttribute(hwnd, DwmWindowCornerPreference, ref cornerPreference, sizeof(int)); }
            catch { /* Windows chooses its normal borderless corner behavior. */ }
            Position(appWindow, hwnd, options.PositionX, options.PositionY);

            _window = window;
            _card = card;
            _closing = false;
            appWindow.Show(activateWindow: false);
            // The outbox can be acknowledged only after WinUI has accepted the
            // native notification window. Animation/image loading are cosmetic
            // and must not block durable delivery acknowledgement.
            try { onPresented?.Invoke(); }
            catch (Exception ex) { Helpers.AppLog.Debug("Trophy presentation acknowledgement failed: " + ex.Message); }
            _motion = TrophyMotion.For(options);
            AnimateIn(card, _motion);
            TrophySoundPlayer.Play();

            _timer = _dispatcher.CreateTimer();
            _timer.Interval = NotificationDuration;
            _timer.IsRepeating = false;
            _timer.Tick += OnTimer;
            _timer.Start();
        }
        catch (Exception ex)
        {
            Helpers.AppLog.Error("Trophy notification display failed", ex);
            CloseCurrentImmediately();
            ShowNext();
        }
    }

    private static Border BuildCard(TrophyNotificationPayload payload)
    {
        var visual = TrophyVisual.For(payload);
        var secondary = string.IsNullOrWhiteSpace(payload.GameTitle)
            ? payload.Detail
            : payload.GameTitle;
        if (string.IsNullOrWhiteSpace(secondary)) secondary = "Achievement unlocked";

        var copy = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
        };
        copy.Children.Add(new TextBlock
        {
            Text = "EXO // UNLOCKED",
            FontSize = 8,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            CharacterSpacing = 140,
            Foreground = visual.MutedAccentBrush,
        });
        copy.Children.Add(new TextBlock
        {
            Text = payload.AchievementName,
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 247, 247, 248)),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var metadata = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
        };
        metadata.Children.Add(new TextBlock
        {
            Text = secondary,
            MaxWidth = 268,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 151, 151, 156)),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        copy.Children.Add(metadata);

        var iconTile = BuildIconTile(payload, visual);
        var accent = new Rectangle
        {
            Width = 4,
            Fill = visual.AccentBrush,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        var tier = new Border
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 2),
            CornerRadius = new CornerRadius(6),
            BorderBrush = visual.OutlineBrush,
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Color.FromArgb(24, visual.Accent.R, visual.Accent.G, visual.Accent.B)),
            Padding = new Thickness(7, 3, 6, 3),
            Child = new TextBlock
            {
                Text = TrophyRarityResolver.Label(visual.Rarity),
                FontSize = 8,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                CharacterSpacing = 85,
                Foreground = visual.AccentBrush,
            },
        };
        var layout = new Grid
        {
            Padding = new Thickness(0, 14, 14, 14),
            ColumnSpacing = 12,
        };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(iconTile, 1);
        Grid.SetColumn(copy, 2);
        Grid.SetColumn(tier, 3);
        layout.Children.Add(accent);
        layout.Children.Add(iconTile);
        layout.Children.Add(copy);
        layout.Children.Add(tier);

        return new Border
        {
            Background = new SolidColorBrush(TrophySurface),
            BorderBrush = visual.OutlineBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Child = layout,
            Opacity = 0,
        };
    }

    private static Border BuildIconTile(TrophyNotificationPayload payload, TrophyVisual visual)
    {
        var content = new Grid();
        var fallback = payload.IsPreview ? BuildPreviewMark(visual) : BuildTrophyMark(visual.Accent);
        content.Children.Add(fallback);

        if (TryGetSafeIconUri(payload.IconUrl, out var uri))
        {
            try
            {
                var image = new Image
                {
                    // Icons are rendered at 64 DIPs. Decode above that target
                    // so native achievement art stays crisp on high-DPI panels
                    // without claiming to invent detail from a poor source.
                    Source = new BitmapImage(uri) { DecodePixelWidth = 128 },
                    Stretch = Stretch.UniformToFill,
                    Opacity = 0,
                };
                image.ImageOpened += (_, _) =>
                {
                    fallback.Visibility = Visibility.Collapsed;
                    image.Opacity = 1;
                };
                image.ImageFailed += (_, _) =>
                {
                    image.Visibility = Visibility.Collapsed;
                    fallback.Visibility = Visibility.Visible;
                };
                content.Children.Add(image);
            }
            catch
            {
                // A malformed or unavailable icon always leaves the local mark visible.
            }
        }

        return new Border
        {
            Width = 64,
            Height = 64,
            CornerRadius = new CornerRadius(8),
            Background = visual.IconBackgroundBrush,
            Child = content,
        };
    }

    private static bool TryGetSafeIconUri(string? value, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(value)) return false;

        if (Uri.TryCreate(value.Trim(), UriKind.Absolute, out var parsed))
        {
            if (parsed.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                uri = parsed;
                return true;
            }

            if (parsed.IsFile && File.Exists(parsed.LocalPath))
            {
                uri = parsed;
                return true;
            }
        }

        try
        {
            if (System.IO.Path.IsPathFullyQualified(value) && File.Exists(value))
            {
                uri = new Uri(System.IO.Path.GetFullPath(value));
                return true;
            }
        }
        catch { }
        return false;
    }

    private static Viewbox BuildTrophyMark(Color accent)
    {
        var ink = new SolidColorBrush(Color.FromArgb(240, accent.R, accent.G, accent.B));
        var quietInk = new SolidColorBrush(Color.FromArgb(126, accent.R, accent.G, accent.B));
        var canvas = new Canvas { Width = 28, Height = 28 };

        var leftHandle = new Ellipse
        {
            Width = 10,
            Height = 12,
            Stroke = quietInk,
            StrokeThickness = 1.6,
        };
        Canvas.SetLeft(leftHandle, 1);
        Canvas.SetTop(leftHandle, 3);
        var rightHandle = new Ellipse
        {
            Width = 10,
            Height = 12,
            Stroke = quietInk,
            StrokeThickness = 1.6,
        };
        Canvas.SetLeft(rightHandle, 17);
        Canvas.SetTop(rightHandle, 3);

        var bowl = new Polygon
        {
            Points = new PointCollection
            {
                new(6, 2),
                new(22, 2),
                new(20, 11),
                new(17, 16),
                new(11, 16),
                new(8, 11),
            },
            Fill = new SolidColorBrush(Color.FromArgb(26, 255, 255, 255)),
            Stroke = ink,
            StrokeThickness = 1.6,
            StrokeLineJoin = PenLineJoin.Round,
        };
        var stem = new Border
        {
            Width = 3,
            Height = 6,
            CornerRadius = new CornerRadius(1.5),
            Background = ink,
        };
        Canvas.SetLeft(stem, 12.5);
        Canvas.SetTop(stem, 16);
        var basePlate = new Border
        {
            Width = 15,
            Height = 3,
            CornerRadius = new CornerRadius(1.5),
            Background = ink,
        };
        Canvas.SetLeft(basePlate, 6.5);
        Canvas.SetTop(basePlate, 22);

        canvas.Children.Add(leftHandle);
        canvas.Children.Add(rightHandle);
        canvas.Children.Add(bowl);
        canvas.Children.Add(stem);
        canvas.Children.Add(basePlate);
        return new Viewbox
        {
            Width = 24,
            Height = 24,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = canvas,
        };
    }

    private static Viewbox BuildPreviewMark(TrophyVisual visual)
    {
        var canvas = new Canvas { Width = 40, Height = 40 };
        var ring = new Ellipse
        {
            Width = 30,
            Height = 30,
            Stroke = visual.AccentBrush,
            StrokeThickness = 1.4,
            Fill = new SolidColorBrush(Color.FromArgb(28, visual.Accent.R, visual.Accent.G, visual.Accent.B)),
        };
        Canvas.SetLeft(ring, 5);
        Canvas.SetTop(ring, 5);
        var diamond = new Polygon
        {
            Points = new PointCollection { new(20, 10), new(28, 20), new(20, 30), new(12, 20) },
            Fill = visual.AccentBrush,
        };
        canvas.Children.Add(ring);
        canvas.Children.Add(diamond);
        return new Viewbox
        {
            Width = 36,
            Height = 36,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = canvas,
        };
    }

    private static void AnimateIn(Border target, TrophyMotion motion)
    {
        var transform = new CompositeTransform
        {
            TranslateX = motion.X,
            TranslateY = motion.Y,
            ScaleX = 0.985,
            ScaleY = 0.985,
            CenterX = NotificationWidth / 2d,
            CenterY = NotificationHeight / 2d,
        };
        target.RenderTransform = transform;
        target.Opacity = 0;
        if (!AnimationsEnabled())
        {
            transform.TranslateX = 0;
            transform.TranslateY = 0;
            transform.ScaleX = 1;
            transform.ScaleY = 1;
            target.Opacity = 1;
            return;
        }

        var storyboard = new Storyboard();
        storyboard.Children.Add(CreateAnimation(target, "Opacity", 0, 1, 220, EasingMode.EaseOut));
        storyboard.Children.Add(CreateAnimation(transform, "TranslateX", motion.X, 0, 240, EasingMode.EaseOut));
        storyboard.Children.Add(CreateAnimation(transform, "TranslateY", motion.Y, 0, 240, EasingMode.EaseOut));
        storyboard.Children.Add(CreateAnimation(transform, "ScaleX", 0.985, 1, 260, EasingMode.EaseOut));
        storyboard.Children.Add(CreateAnimation(transform, "ScaleY", 0.985, 1, 260, EasingMode.EaseOut));
        storyboard.Begin();
    }

    private void BeginCloseCurrent()
    {
        if (_closing || _window is null) return;
        _closing = true;
        StopTimer();

        if (_card is null || !AnimationsEnabled())
        {
            CompleteCloseCurrent();
            return;
        }

        var transform = _card.RenderTransform as CompositeTransform ?? new CompositeTransform();
        _card.RenderTransform = transform;
        var storyboard = new Storyboard();
        storyboard.Children.Add(CreateAnimation(_card, "Opacity", 1, 0, 160, EasingMode.EaseInOut));
        storyboard.Children.Add(CreateAnimation(transform, "TranslateX", 0, _motion.X * 0.45, 160, EasingMode.EaseInOut));
        storyboard.Children.Add(CreateAnimation(transform, "TranslateY", 0, _motion.Y * 0.45, 160, EasingMode.EaseInOut));
        storyboard.Children.Add(CreateAnimation(transform, "ScaleX", 1, 0.99, 160, EasingMode.EaseInOut));
        storyboard.Children.Add(CreateAnimation(transform, "ScaleY", 1, 0.99, 160, EasingMode.EaseInOut));
        storyboard.Completed += OnExitAnimationCompleted;
        _exitStoryboard = storyboard;
        storyboard.Begin();
    }

    private static DoubleAnimation CreateAnimation(
        DependencyObject target,
        string property,
        double from,
        double to,
        int durationMilliseconds,
        EasingMode easingMode)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(durationMilliseconds)),
            EasingFunction = new CubicEase { EasingMode = easingMode },
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        return animation;
    }

    private static bool AnimationsEnabled()
    {
        try { return new UISettings().AnimationsEnabled; }
        catch { return true; }
    }

    private static void Position(AppWindow appWindow, IntPtr notificationHwnd, double positionX, double positionY)
    {
        var display = ResolveDisplay(appWindow, notificationHwnd);
        var work = display.WorkArea;
        var bounds = TrophyNotificationLayout.Calculate(
            work.X,
            work.Y,
            work.Width,
            work.Height,
            (int)Math.Round(NotificationWidth * display.Scale),
            (int)Math.Round(NotificationHeight * display.Scale),
            positionX,
            positionY,
            (int)Math.Round(24 * display.Scale));
        appWindow.MoveAndResize(new RectInt32(bounds.Left, bounds.Top, bounds.Width, bounds.Height));
    }

    private static TrophyDisplay ResolveDisplay(AppWindow appWindow, IntPtr notificationHwnd)
    {
        try
        {
            var foreground = GetForegroundWindow();
            if (foreground != IntPtr.Zero)
            {
                var foregroundId = Win32Interop.GetWindowIdFromWindow(foreground);
                var foregroundDisplay = DisplayArea.GetFromWindowId(foregroundId, DisplayAreaFallback.Nearest);
                if (foregroundDisplay is not null)
                    return new TrophyDisplay(foregroundDisplay.WorkArea, DpiScale(foreground));
            }
        }
        catch { /* fall through to the notification display */ }

        var display = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);
        return new TrophyDisplay(
            display?.WorkArea ?? new RectInt32(0, 0, 1920, 1080),
            DpiScale(notificationHwnd));
    }

    private static double DpiScale(IntPtr hwnd)
    {
        try
        {
            var dpi = GetDpiForWindow(hwnd);
            if (dpi > 0) return Math.Clamp(dpi / 96d, 0.75d, 4d);
        }
        catch { }
        return 1d;
    }

    private void OnTimer(DispatcherQueueTimer sender, object args) => BeginCloseCurrent();

    private void OnExitAnimationCompleted(object? sender, object args)
    {
        if (sender is Storyboard storyboard)
            storyboard.Completed -= OnExitAnimationCompleted;
        _exitStoryboard = null;
        CompleteCloseCurrent();
    }

    private void CompleteCloseCurrent()
    {
        CloseCurrentImmediately();
        if (!_disposed) ShowNext();
    }

    private void StopTimer()
    {
        if (_timer is null) return;
        try { _timer.Stop(); } catch { }
        _timer.Tick -= OnTimer;
        _timer = null;
    }

    private void CloseCurrentImmediately()
    {
        StopTimer();
        if (_exitStoryboard is not null)
        {
            _exitStoryboard.Completed -= OnExitAnimationCompleted;
            try { _exitStoryboard.Stop(); } catch { }
            _exitStoryboard = null;
        }
        try { _window?.Close(); } catch { }
        _window = null;
        _card = null;
        _closing = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _queue.Clear();
        CloseCurrentImmediately();
    }

    private sealed record TrophyNotificationOptions(
        double PositionX,
        double PositionY)
    {
        public static TrophyNotificationOptions From(AppSettings settings) => new(
            settings.TrophyNotificationPositionX,
            settings.TrophyNotificationPositionY);
    }

    private readonly record struct TrophyDisplay(RectInt32 WorkArea, double Scale);

    private readonly record struct TrophyMotion(double X, double Y)
    {
        public static TrophyMotion For(TrophyNotificationOptions options) => new(
            options.PositionX <= 0d ? -18d : options.PositionX >= 1d ? 18d : 0d,
            options.PositionY <= 0d ? -18d : options.PositionY >= 1d ? 18d : 0d);
    }

    private sealed record TrophyVisual(
        TrophyRarity Rarity,
        Color Accent,
        SolidColorBrush AccentBrush,
        SolidColorBrush MutedAccentBrush,
        SolidColorBrush OutlineBrush,
        SolidColorBrush IconBackgroundBrush)
    {
        public static TrophyVisual For(TrophyNotificationPayload payload)
        {
            var rarity = payload.Rarity != TrophyRarity.Unknown
                ? payload.Rarity
                : payload.IsPerfect ? TrophyRarity.Platinum
                : payload.IsRare ? TrophyRarity.Gold
                : TrophyRarity.Unknown;
            var accent = rarity switch
            {
                TrophyRarity.Bronze => Color.FromArgb(255, 201, 130, 84),
                TrophyRarity.Silver => Color.FromArgb(255, 191, 201, 212),
                TrophyRarity.Gold => Color.FromArgb(255, 240, 199, 106),
                TrophyRarity.Platinum => Color.FromArgb(255, 147, 221, 253),
                _ => Color.FromArgb(255, 212, 215, 221),
            };
            return new TrophyVisual(
                rarity,
                accent,
                new SolidColorBrush(accent),
                new SolidColorBrush(Color.FromArgb(190, accent.R, accent.G, accent.B)),
                new SolidColorBrush(Color.FromArgb(92, accent.R, accent.G, accent.B)),
                new SolidColorBrush(Color.FromArgb(255,
                    (byte)Math.Max(10, accent.R / 7),
                    (byte)Math.Max(11, accent.G / 7),
                    (byte)Math.Max(13, accent.B / 6))));
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong32(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern int SetWindowLong32(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);

    private static IntPtr GetWindowLongPtr(IntPtr hwnd, int index) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, index) : new IntPtr(GetWindowLong32(hwnd, index));

    private static IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value) =>
        IntPtr.Size == 8 ? SetWindowLongPtr64(hwnd, index, value) : new IntPtr(SetWindowLong32(hwnd, index, value.ToInt32()));
}
