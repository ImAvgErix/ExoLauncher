using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;

namespace ExoLauncher.Helpers;

/// <summary>Clip only the embedded surface, without changing the provider document's shapes or layout.</summary>
internal static class EmbeddedClientFrame
{
    internal static void SetInputVisible(WebView2 view, bool visible)
    {
        var opacity = visible ? 1d : 0d;
        if (view.Opacity != opacity) view.Opacity = opacity;
        if (view.IsHitTestVisible != visible) view.IsHitTestVisible = visible;
        if (view.IsTabStop != visible) view.IsTabStop = visible;
    }

    internal static bool ValidBounds(double left, double top, double width, double height) =>
        double.IsFinite(left) && double.IsFinite(top) && double.IsFinite(width) && double.IsFinite(height) &&
        left >= 0 && top >= 0 && width >= 1 && height >= 1;

    internal static void SetBounds(WebView2 view, double left, double top, double width, double height)
    {
        var margin = new Thickness(left, top, 0, 0);
        if (view.Margin != margin) view.Margin = margin;
        if (view.Width != width) view.Width = width;
        if (view.Height != height) view.Height = height;
    }

    internal static void Apply(WebView2 view)
    {
        // WebView creates its composition placement visual during core initialization.
        // Clip that visual; XAML owns (and can replace) the control's own layout clip.
        var visual = ElementCompositionPreview.GetElementChildVisual(view)
            ?? ElementCompositionPreview.GetElementVisual(view);
        var shape = visual.Compositor.CreateRoundedRectangleGeometry();
        // Provider navigation reaches the edge (including Discord's selected
        // server indicator). Rounding this entire viewport cuts those pixels.
        shape.CornerRadius = Vector2.Zero;
        var clip = visual.Compositor.CreateGeometricClip(shape);
        void Update()
        {
            var size = new Vector2((float)view.ActualWidth, (float)view.ActualHeight);
            if (shape.Size != size) shape.Size = size;
            if (visual.Clip != clip) visual.Clip = clip;
        }
        view.SizeChanged += (_, _) => Update();
        view.Loaded += (_, _) => Update();
        Update();
    }
}
