using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExoLauncher.Services;

/// <summary>
/// Geometry, color, type, and motion for the trophy banner. Settings preview
/// and the live overlay both render <c>TrophyBanner</c> from this JSON.
/// </summary>
public static class TrophyBannerDesign
{
    public const string SourceRelativePath = "ui/src/lib/trophyBannerDesign.json";
    public const string FileName = "trophyBannerDesign.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly Lazy<TrophyBannerSpec> Cached = new(LoadCurrent, true);

    public static TrophyBannerSpec Current => Cached.Value;

    public static TrophyBannerSpec LoadCurrent()
    {
        var path = FindSourceFile()
            ?? throw new InvalidOperationException(
                "Trophy banner design is missing. Expected " + SourceRelativePath + ".");
        return LoadFromFile(path);
    }

    public static TrophyBannerSpec LoadFromFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return LoadFromJson(File.ReadAllText(path));
    }

    public static TrophyBannerSpec LoadFromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var spec = JsonSerializer.Deserialize<TrophyBannerSpec>(json, JsonOptions)
            ?? throw new InvalidOperationException("Trophy banner design deserialized to nothing.");
        spec.Validate();
        return spec;
    }

    public static string? FindSourceFile()
    {
        foreach (var candidate in SourceCandidates())
        {
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        return null;
    }

    public static IEnumerable<string> SourceCandidates()
    {
        yield return Path.Combine(AppContext.BaseDirectory, FileName);
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            yield return Path.Combine(dir.FullName, "ui", "src", "lib", FileName);
            yield return Path.Combine(dir.FullName, SourceRelativePath.Replace('/', Path.DirectorySeparatorChar));
            dir = dir.Parent;
        }
    }

    public static string Key(TrophyRarity rarity) => rarity.ToString().ToLowerInvariant();

    public static TrophyRarity ParseRarity(string? value)
    {
        if (string.Equals(value, "bronze", StringComparison.OrdinalIgnoreCase)) return TrophyRarity.Bronze;
        if (string.Equals(value, "silver", StringComparison.OrdinalIgnoreCase)) return TrophyRarity.Silver;
        if (string.Equals(value, "gold", StringComparison.OrdinalIgnoreCase)) return TrophyRarity.Gold;
        if (string.Equals(value, "platinum", StringComparison.OrdinalIgnoreCase)) return TrophyRarity.Platinum;
        return TrophyRarity.Unknown;
    }
}

public sealed class TrophyBannerSpec
{
    public int Width { get; init; }
    public int Height { get; init; }
    public int Radius { get; init; }
    public int Icon { get; init; }
    public int IconRadius { get; init; }
    public int PadX { get; init; }
    public int PadY { get; init; }
    public int Gap { get; init; }
    public int OverlayPad { get; init; }
    public string OverlayDocument { get; init; } = "trophy.html";
    public string FontFamily { get; init; } = "";
    public string FontFamilyFallback { get; init; } = "";
    public string FontFile { get; init; } = "";
    public string FontFileMedium { get; init; } = "";
    public string FontNativeFace { get; init; } = "";
    public string FontNativeFaceMedium { get; init; } = "";
    public TrophyBannerColors Colors { get; init; } = new();
    public TrophyBannerType Type { get; init; } = new();
    public TrophyBannerPreview Preview { get; init; } = new();
    public string[] PreviewCycle { get; init; } = [];
    public Dictionary<string, TrophyBannerAccent> Accents { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public TrophyBannerMotion Motion { get; init; } = new();

    public TrophyBannerAccent Accent(TrophyRarity rarity) =>
        Accents.TryGetValue(TrophyBannerDesign.Key(rarity), out var accent)
            ? accent
            : Accents["unknown"];

    public TrophyBannerTierMotion Tier(TrophyRarity rarity) =>
        Motion.Tiers.TryGetValue(TrophyBannerDesign.Key(rarity), out var motion)
            ? motion
            : Motion.Tiers["unknown"];

    public TrophyRarity[] Cycle() =>
        PreviewCycle.Length == 0
            ? [TrophyRarity.Bronze, TrophyRarity.Silver, TrophyRarity.Gold, TrophyRarity.Platinum]
            : PreviewCycle.Select(TrophyBannerDesign.ParseRarity).ToArray();

    public string WebFontStack() =>
        FontFamily + " Variable, " + FontFamily + ", " + FontFamilyFallback + ", sans-serif";

    /// <summary>
    /// WinUI unpackaged apps resolve <c>ms-appx:///</c> to the exe directory.
    /// WOFF2 from the WebView bundle will not load here; the TTF in
    /// <see cref="FontFile"/> is the native face. Missing files fall back.
    /// </summary>
    public string NativeFontFamily(bool medium = false)
    {
        var relative = (medium ? FontFileMedium : FontFile).Replace('\\', '/').Trim().TrimStart('/');
        var face = medium
            ? (string.IsNullOrWhiteSpace(FontNativeFaceMedium) ? FontFamily + " Medium" : FontNativeFaceMedium)
            : (string.IsNullOrWhiteSpace(FontNativeFace) ? FontFamily : FontNativeFace);
        if (string.IsNullOrWhiteSpace(relative) || !NativeFontExists(relative))
            return FontFamilyFallback;
        return "ms-appx:///" + relative + "#" + face;
    }

    public bool NativeFontLoaded(bool medium = false)
    {
        var relative = (medium ? FontFileMedium : FontFile).Replace('\\', '/').Trim().TrimStart('/');
        return NativeFontExists(relative);
    }

    private static bool NativeFontExists(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return false;
        var name = relative.Replace('/', Path.DirectorySeparatorChar);
        foreach (var root in NativeFontRoots())
        {
            var candidate = Path.Combine(root, name);
            if (File.Exists(candidate)) return true;
        }
        return false;
    }

    private static IEnumerable<string> NativeFontRoots()
    {
        yield return AppContext.BaseDirectory;
        var design = TrophyBannerDesign.FindSourceFile();
        if (design is null) yield break;
        var ui = new DirectoryInfo(Path.GetDirectoryName(design)!);
        while (ui is not null)
        {
            var exo = Path.Combine(ui.FullName, "ExoLauncher");
            if (Directory.Exists(exo)) yield return exo;
            ui = ui.Parent;
        }
    }

    public void Validate()
    {
        if (Width < 200 || Height < 64 || Radius != 14)
            throw new InvalidOperationException("Trophy banner geometry must stay on the Exo 14px shell.");
        if (Icon < 32 || IconRadius < 0 || PadX < 0 || PadY < 0 || Gap < 0)
            throw new InvalidOperationException("Trophy banner spacing is invalid.");
        if (OverlayPad < 16 || OverlayPad > 40)
            throw new InvalidOperationException("Trophy overlay pad must leave room for compositor overshoot.");
        if (!string.Equals(OverlayDocument, "trophy.html", StringComparison.Ordinal))
            throw new InvalidOperationException("Trophy overlay document must stay trophy.html.");
        Colors.Validate();
        if (!string.Equals(FontFamily, "Geist", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(FontFamilyFallback) ||
            string.IsNullOrWhiteSpace(FontFile) ||
            string.IsNullOrWhiteSpace(FontNativeFace))
            throw new InvalidOperationException("Trophy banner type must be Geist with a real fallback.");
        if (string.IsNullOrWhiteSpace(Preview.AchievementName) ||
            Preview.AchievementName.Contains("Exo Launcher", StringComparison.OrdinalIgnoreCase) ||
            Preview.GameTitle.Contains("Exo Launcher", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Trophy preview copy must not use the product name as a fake unlock.");
        foreach (var key in new[] { "unknown", "bronze", "silver", "gold", "platinum" })
        {
            if (!Accents.ContainsKey(key) || !Motion.Tiers.ContainsKey(key))
                throw new InvalidOperationException("Trophy banner is missing tier '" + key + "'.");
        }
        Motion.Validate();
    }
}

public sealed class TrophyBannerColors
{
    public string Bg { get; init; } = "";
    public string Fg { get; init; } = "";
    public string Muted { get; init; } = "";
    public string Faint { get; init; } = "";
    public string Hairline { get; init; } = "";
    public string Line { get; init; } = "";
    public string Good { get; init; } = "";

    public void Validate()
    {
        if (Bg != "#000000" || Fg != "#f2f2f2" || Muted != "#8a8a8a" || Faint != "#808080" ||
            Hairline != "#161616" || Line != "#222222" || Good != "#3dd68c")
            throw new InvalidOperationException("Trophy banner colors must match the Exo shell tokens.");
    }
}

public sealed class TrophyBannerType
{
    public double NameSize { get; init; }
    public int NameWeight { get; init; }
    public double DetailSize { get; init; }
    public double MetaSize { get; init; }
    public double RaritySize { get; init; }
    public double RarityTrackingEm { get; init; }
}

public sealed class TrophyBannerPreview
{
    public string GameTitle { get; init; } = "";
    public string AchievementName { get; init; } = "";
    public string Detail { get; init; } = "";
}

public sealed class TrophyBannerAccent
{
    public string Rarity { get; init; } = "";
    public string Hairline { get; init; } = "";
}

public sealed class TrophyBannerMotion
{
    public int ReducedFadeMs { get; init; }
    public int ExitMs { get; init; }
    public Dictionary<string, TrophyBannerTierMotion> Tiers { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public void Validate()
    {
        if (ReducedFadeMs < 80 || ReducedFadeMs > 240 || ExitMs < 120 || ExitMs > 280)
            throw new InvalidOperationException("Trophy banner reduced-motion and exit timing is out of range.");
        foreach (var motion in Tiers.Values) motion.Validate();
    }
}

public sealed class TrophyBannerTierMotion
{
    public int EnterMs { get; init; }
    public int SettleMs { get; init; }
    public double FromY { get; init; }
    public double FromScale { get; init; }
    public double Overshoot { get; init; }
    public bool Sheen { get; init; }
    public bool Ring { get; init; }
    public bool Bloom { get; init; }
    public bool PulseIcon { get; init; }

    [JsonIgnore]
    public bool Pops => Overshoot > 1.001;

    public void Validate()
    {
        if (EnterMs < 120 || EnterMs > 400 || SettleMs < 0 || SettleMs > 280)
            throw new InvalidOperationException("Trophy banner enter timing is out of range.");
        if (FromY < 0 || FromY > 20 || FromScale < 0.9 || FromScale > 1 || Overshoot < 1 || Overshoot > 1.08)
            throw new InvalidOperationException("Trophy banner motion must stay compositor-cheap.");
    }
}

public readonly record struct TrophyColor(byte A, byte R, byte G, byte B)
{
    public static TrophyColor Parse(string hex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hex);
        var value = hex.Trim();
        if (value.StartsWith('#')) value = value[1..];
        if (value.Length == 6)
        {
            var n = int.Parse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return new TrophyColor(255, (byte)((n >> 16) & 0xFF), (byte)((n >> 8) & 0xFF), (byte)(n & 0xFF));
        }
        if (value.Length == 8)
        {
            var n = uint.Parse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return new TrophyColor((byte)((n >> 24) & 0xFF), (byte)((n >> 16) & 0xFF), (byte)((n >> 8) & 0xFF), (byte)(n & 0xFF));
        }
        throw new FormatException("Trophy color '" + hex + "' is not #RRGGBB.");
    }
}
