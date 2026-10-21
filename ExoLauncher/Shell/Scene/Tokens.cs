using Windows.UI;

namespace ExoLauncher.Shell.Scene;

/// <summary>Product tokens. Void black. Not Fluent.</summary>
internal static class Tokens
{
    public static readonly Color Bg = Color.FromArgb(255, 0, 0, 0);
    public static readonly Color Surface = Color.FromArgb(255, 3, 3, 3);
    public static readonly Color Elevated = Color.FromArgb(255, 7, 7, 7);
    public static readonly Color Line = Color.FromArgb(255, 34, 34, 34);
    public static readonly Color LineSoft = Color.FromArgb(255, 22, 22, 22);
    public static readonly Color Fg = Color.FromArgb(255, 238, 234, 228);
    public static readonly Color Muted = Color.FromArgb(255, 140, 135, 127);
    public static readonly Color PrimaryInk = Color.FromArgb(255, 10, 10, 10);
    public static readonly Color NavOn = Color.FromArgb(255, 23, 23, 23);

    public const string InterVariable = "ms-appx:///Assets/Fonts/Inter-Variable.ttf#Inter Variable";
}
