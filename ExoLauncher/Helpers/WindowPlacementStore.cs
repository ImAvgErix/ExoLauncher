using System.Text.Json;

namespace ExoLauncher.Helpers;

internal sealed record WindowPlacement(int X, int Y, int Width, int Height, bool Maximized, double Scale);

internal static class WindowPlacementStore
{
    internal static string Path => System.IO.Path.Combine(PathHelper.AppDataDir, "window-placement.json");
    internal static bool IsValid(WindowPlacement? value) => value is { Width: >= 320 and <= 32768, Height: >= 240 and <= 32768,
        X: >= -131072 and <= 131072, Y: >= -131072 and <= 131072, Scale: >= .5 and <= 8 };

    internal static WindowPlacement? Load()
    {
        try
        {
            if (!File.Exists(Path) || new FileInfo(Path).Length > 4096) return null;
            var value = JsonSerializer.Deserialize<WindowPlacement>(File.ReadAllText(Path));
            return IsValid(value) ? value : null;
        }
        catch { return null; }
    }

    internal static WindowPlacement Fit(WindowPlacement saved, int x, int y, int width, int height, double scale)
    {
        var w = Math.Clamp((int)Math.Round(saved.Width * scale / saved.Scale), Math.Min((int)Math.Round(960 * scale), width), width);
        var h = Math.Clamp((int)Math.Round(saved.Height * scale / saved.Scale), Math.Min((int)Math.Round(640 * scale), height), height);
        return new(Math.Clamp(saved.X, x, x + width - w), Math.Clamp(saved.Y, y, y + height - h), w, h, saved.Maximized, scale);
    }

    internal static void Save(WindowPlacement placement)
    {
        if (!IsValid(placement)) return;
        var temporary = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(placement));
            File.Move(temporary, Path, overwrite: true);
        }
        catch { /* Window placement must not prevent startup or shutdown. */ }
        finally { try { File.Delete(temporary); } catch { } }
    }
}
