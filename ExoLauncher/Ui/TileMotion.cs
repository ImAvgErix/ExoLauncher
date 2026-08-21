using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using System.Numerics;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace ExoLauncher.Ui;

internal static class TileMotion
{
    public static bool Enabled
    {
        get
        {
            try { return new UISettings().AnimationsEnabled; }
            catch { return true; }
        }
    }

    public static void Hover(UIElement element, bool over)
    {
        try { Scale(element, over ? 1.03f : 1f, 160); }
        catch { }
    }

    public static void Press(UIElement element, bool down, bool hovered = false)
    {
        try { Scale(element, down ? 0.97f : hovered ? 1.03f : 1f, down ? 120 : 160); }
        catch { }
    }

    public static void Fade(UIElement element, float opacity, int ms) =>
        Opacity(element, opacity, ms);

    public static void Shine(UIElement element, bool over) =>
        Opacity(element, over ? 1f : 0f, 160);

    public static void Glare(UIElement shine, float travel, bool over)
    {
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(shine);
            if (!Enabled)
            {
                visual.Opacity = over ? 1f : 0f;
                visual.Offset = new Vector3(over ? travel : -travel, 0, 0);
                return;
            }

            var compositor = visual.Compositor;
            var ease = EaseOut(compositor);
            if (over)
            {
                visual.Offset = new Vector3(-travel, 0, 0);
                visual.Opacity = 1f;
                var move = compositor.CreateVector3KeyFrameAnimation();
                move.Duration = TimeSpan.FromMilliseconds(650);
                move.InsertKeyFrame(1f, new Vector3(travel, 0, 0), ease);
                visual.StartAnimation("Offset", move);
            }
            else
            {
                Opacity(shine, 0f, 160);
            }
        }
        catch { }
    }

    public static void Spotlight(
        UIElement layer,
        RadialGradientBrush brush,
        Point point,
        bool over,
        double radius = 180)
    {
        try
        {
            Opacity(layer, over ? 1f : 0f, 160);
            if (!over) return;
            brush.MappingMode = BrushMappingMode.Absolute;
            brush.Center = point;
            brush.GradientOrigin = point;
            brush.RadiusX = radius;
            brush.RadiusY = radius;
        }
        catch { }
    }

    public static void Shadow(FrameworkElement host, bool over)
    {
        try
        {
            if (host.Tag is not DropShadow shadow) return;
            shadow.Color = over ? Color.FromArgb(150, 0, 0, 0) : Color.FromArgb(0, 0, 0, 0);
            shadow.BlurRadius = over ? 28f : 8f;
            shadow.Offset = over ? new Vector3(0, 14, 0) : new Vector3(0, 4, 0);
        }
        catch { }
    }

    public static void AttachShadow(FrameworkElement host, float width, float height)
    {
        try
        {
            var compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;
            var shadow = compositor.CreateDropShadow();
            shadow.BlurRadius = 8f;
            shadow.Offset = new Vector3(0, 4, 0);
            shadow.Color = Color.FromArgb(0, 0, 0, 0);
            var sprite = compositor.CreateSpriteVisual();
            sprite.Size = new Vector2(width, height);
            sprite.Shadow = shadow;
            ElementCompositionPreview.SetElementChildVisual(host, sprite);
            host.Tag = shadow;
        }
        catch { }
    }

    public static void Depth(UIElement element, Point point, Size size, bool over, float maxDegrees = 12f)
    {
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(element);
            Quaternion target;
            if (!over || size.Width <= 0 || size.Height <= 0)
            {
                target = Quaternion.Identity;
            }
            else
            {
                var nx = (float)((point.X / size.Width) * 2 - 1);
                var ny = (float)((point.Y / size.Height) * 2 - 1);
                var maxRad = maxDegrees * (MathF.PI / 180f);
                target = Quaternion.CreateFromYawPitchRoll(nx * maxRad, -ny * maxRad, 0);
            }

            visual.CenterPoint = new Vector3((float)(size.Width / 2), (float)(size.Height / 2), 0);
            if (!Enabled)
            {
                visual.Orientation = target;
                return;
            }

            var anim = visual.Compositor.CreateQuaternionKeyFrameAnimation();
            anim.Duration = TimeSpan.FromMilliseconds(over ? 80 : 200);
            anim.InsertKeyFrame(1f, target, EaseOut(visual.Compositor));
            visual.StartAnimation("Orientation", anim);
        }
        catch { }
    }

    public static void Parallax(UIElement element, Point point, Size size, bool over, float maxPx = 12f)
    {
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(element);
            Vector3 offset;
            if (!over || size.Width <= 0 || size.Height <= 0)
            {
                offset = Vector3.Zero;
            }
            else
            {
                var nx = (float)((point.X / size.Width) * 2 - 1);
                var ny = (float)((point.Y / size.Height) * 2 - 1);
                offset = new Vector3(-nx * maxPx, -ny * maxPx, 0);
            }

            if (!Enabled)
            {
                visual.Offset = offset;
                return;
            }

            var anim = visual.Compositor.CreateVector3KeyFrameAnimation();
            anim.Duration = TimeSpan.FromMilliseconds(over ? 80 : 200);
            anim.InsertKeyFrame(1f, offset, EaseOut(visual.Compositor));
            visual.StartAnimation("Offset", anim);
        }
        catch { }
    }

    public static void Entrance(UIElement element, int index)
    {
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(element);
            if (!Enabled)
            {
                visual.Opacity = 1;
                visual.Offset = Vector3.Zero;
                return;
            }

            visual.Opacity = 0;
            visual.Offset = new Vector3(0, 12, 0);
            var compositor = visual.Compositor;
            var delay = TimeSpan.FromMilliseconds(Math.Min(index, 12) * 40);
            var ease = EaseOut(compositor);

            var fade = compositor.CreateScalarKeyFrameAnimation();
            fade.DelayTime = delay;
            fade.Duration = TimeSpan.FromMilliseconds(400);
            fade.InsertKeyFrame(1f, 1f, ease);

            var slide = compositor.CreateVector3KeyFrameAnimation();
            slide.DelayTime = delay;
            slide.Duration = fade.Duration;
            slide.InsertKeyFrame(1f, Vector3.Zero, ease);

            visual.StartAnimation("Opacity", fade);
            visual.StartAnimation("Offset", slide);
        }
        catch { }
    }

    private static void Scale(UIElement element, float scale, int ms)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var size = element.RenderSize;
        if (size.Width > 0 && size.Height > 0)
            visual.CenterPoint = new Vector3((float)(size.Width / 2), (float)(size.Height / 2), 0);
        if (!Enabled)
        {
            visual.Scale = new Vector3(scale, scale, 1f);
            return;
        }

        var compositor = visual.Compositor;
        var anim = compositor.CreateVector3KeyFrameAnimation();
        anim.Duration = TimeSpan.FromMilliseconds(ms);
        anim.InsertKeyFrame(1f, new Vector3(scale, scale, 1f), EaseOut(compositor));
        visual.StartAnimation("Scale", anim);
    }

    private static void Opacity(UIElement element, float opacity, int ms)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        if (!Enabled)
        {
            visual.Opacity = opacity;
            return;
        }

        var compositor = visual.Compositor;
        var anim = compositor.CreateScalarKeyFrameAnimation();
        anim.Duration = TimeSpan.FromMilliseconds(ms);
        anim.InsertKeyFrame(1f, opacity, EaseOut(compositor));
        visual.StartAnimation("Opacity", anim);
    }

    private static CubicBezierEasingFunction EaseOut(Compositor compositor) =>
        compositor.CreateCubicBezierEasingFunction(new Vector2(0.23f, 1f), new Vector2(0.32f, 1f));
}
