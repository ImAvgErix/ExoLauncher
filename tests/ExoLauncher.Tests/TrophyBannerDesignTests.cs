using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class TrophyBannerDesignTests
{
    [Fact]
    public void JsonIsTheOnlyBannerDesignAndBothSurfacesRenderTheSameComponent()
    {
        var jsonPath = Path.Combine(RepoRoot(), "ui", "src", "lib", "trophyBannerDesign.json");
        Assert.True(File.Exists(jsonPath));
        var json = File.ReadAllText(jsonPath);
        var spec = TrophyBannerDesign.LoadFromJson(json);
        var found = TrophyBannerDesign.FindSourceFile();
        Assert.NotNull(found);
        Assert.Equal(spec.Width, TrophyBannerDesign.LoadFromFile(found!).Width);

        Assert.Equal(400, spec.Width);
        Assert.Equal(92, spec.Height);
        Assert.Equal(14, spec.Radius);
        Assert.Equal(24, spec.OverlayPad);
        Assert.Equal("trophy.html", spec.OverlayDocument);
        Assert.Equal("#000000", spec.Colors.Bg);
        Assert.Equal("#f2f2f2", spec.Colors.Fg);
        Assert.Equal("#8a8a8a", spec.Colors.Muted);
        Assert.Equal("#808080", spec.Colors.Faint);
        Assert.Equal("#161616", spec.Colors.Hairline);
        Assert.Equal("#222222", spec.Colors.Line);
        Assert.Equal("#3dd68c", spec.Colors.Good);
        Assert.Equal("Geist", spec.FontFamily);
        Assert.Equal("Segoe UI Variable Text", spec.FontFamilyFallback);
        Assert.Contains("Geist Variable, Geist, Segoe UI Variable Text", spec.WebFontStack(), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(RepoRoot(), "ExoLauncher", "Assets", "Fonts", "Geist-Regular.ttf")));
        Assert.True(File.Exists(Path.Combine(RepoRoot(), "ExoLauncher", "Assets", "Fonts", "Geist-Medium.ttf")));
        Assert.Equal("#3dd68c", spec.Accent(TrophyRarity.Platinum).Rarity);
        Assert.True(spec.Tier(TrophyRarity.Bronze).FromY <= spec.Tier(TrophyRarity.Platinum).FromY);
        Assert.False(spec.Tier(TrophyRarity.Bronze).Sheen);
        Assert.True(spec.Tier(TrophyRarity.Platinum).Ring);
        Assert.True(spec.Tier(TrophyRarity.Platinum).Bloom);
        Assert.False(spec.Tier(TrophyRarity.Bronze).Pops);
        Assert.True(spec.Tier(TrophyRarity.Platinum).Pops);

        var presenter = File.ReadAllText(Path.Combine(RepoRoot(), "ExoLauncher", "Services", "TrophyNotificationPresenter.cs"));
        var banner = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "src", "components", "TrophyBanner.tsx"));
        var overlay = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "src", "trophy-overlay.tsx"));
        var helper = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "src", "lib", "trophyBanner.ts"));
        var settings = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "src", "components", "TrophyNotificationSettings.tsx"));
        var tokens = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "src", "tokens.css"));
        var overlayHtml = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "trophy.html"));
        var vite = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "vite.config.ts"));

        Assert.True(File.Exists(Path.Combine(RepoRoot(), "ui", "trophy.html")));
        Assert.True(File.Exists(Path.Combine(RepoRoot(), "ui", "src", "trophy-overlay.tsx")));
        Assert.True(File.Exists(Path.Combine(RepoRoot(), "ui", "src", "components", "TrophyBanner.css")));
        Assert.Contains("TrophyBannerDesign.Current", presenter, StringComparison.Ordinal);
        Assert.Contains("trophyBannerDesign.json", helper, StringComparison.Ordinal);
        Assert.Contains("from '../lib/trophyBanner'", banner, StringComparison.Ordinal);
        Assert.Contains("from './TrophyBanner'", settings, StringComparison.Ordinal);
        Assert.Contains("trophyNotificationSlot", helper, StringComparison.Ordinal);
        Assert.Contains("trophyNotificationSlot", settings, StringComparison.Ordinal);
        Assert.Contains("from './components/TrophyBanner'", overlay, StringComparison.Ordinal);
        Assert.Contains("<TrophyBanner", settings, StringComparison.Ordinal);
        Assert.Contains("<TrophyBanner", overlay, StringComparison.Ordinal);
        Assert.DoesNotContain("exo-trophy-preview-card", settings, StringComparison.Ordinal);
        Assert.DoesNotContain(".exo-trophy-banner", tokens, StringComparison.Ordinal);
        Assert.Contains("exo-trophy-banner__name", banner, StringComparison.Ordinal);
        Assert.Contains("CreateCoreWebView2ControllerAsync", presenter, StringComparison.Ordinal);
        Assert.Contains("DefaultBackgroundColor", presenter, StringComparison.Ordinal);
        Assert.Contains("trophy.html", presenter, StringComparison.Ordinal);
        Assert.Contains("Achievement notification", overlayHtml, StringComparison.Ordinal);
        Assert.Contains("/src/trophy-overlay.tsx", overlayHtml, StringComparison.Ordinal);
        Assert.Contains("trophy: resolve(root, 'trophy.html')", vite, StringComparison.Ordinal);
        Assert.Contains("DwmExtendFrameIntoClientArea", presenter, StringComparison.Ordinal);
        Assert.Contains("DwmEnableBlurBehindWindow", presenter, StringComparison.Ordinal);
        Assert.Contains("exclusive fullscreen cannot be covered", presenter, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Color.FromArgb(255, 242, 201, 88)", presenter, StringComparison.Ordinal);
        Assert.DoesNotContain("NotificationWidth = 440", presenter, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildTrophyGlyph", presenter, StringComparison.Ordinal);
        Assert.DoesNotContain("new BitmapImage", presenter, StringComparison.Ordinal);
        Assert.DoesNotContain("ClipCardToRadius", presenter, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeFontFallsBackWhenTheFileIsMissing()
    {
        var json = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "src", "lib", "trophyBannerDesign.json"));
        json = json.Replace("Assets/Fonts/Geist-Regular.ttf", "Assets/Fonts/Geist-Missing.ttf", StringComparison.Ordinal);
        json = json.Replace("Assets/Fonts/Geist-Medium.ttf", "Assets/Fonts/Geist-Missing-Medium.ttf", StringComparison.Ordinal);
        var spec = TrophyBannerDesign.LoadFromJson(json);
        Assert.False(spec.NativeFontLoaded());
        Assert.False(spec.NativeFontLoaded(medium: true));
        Assert.Equal("Segoe UI Variable Text", spec.NativeFontFamily());
        Assert.Equal("Segoe UI Variable Text", spec.NativeFontFamily(medium: true));
    }

    [Fact]
    public void WebHelperExposesTheSameTierCycleAndTokensAsTheHost()
    {
        var spec = TrophyBannerDesign.Current;
        var helper = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "src", "lib", "trophyBanner.ts"));
        var banner = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "src", "components", "TrophyBanner.tsx"));
        var overlay = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "src", "trophy-overlay.tsx"));

        foreach (var key in spec.PreviewCycle)
        {
            Assert.Contains("'" + key + "'", helper, StringComparison.Ordinal);
            Assert.Contains("--exo-trophy-", helper, StringComparison.Ordinal);
        }

        Assert.Contains("trophyBannerVars", banner, StringComparison.Ordinal);
        Assert.Contains("data-exo-trophy-source", banner, StringComparison.Ordinal);
        Assert.Contains(TrophyBannerDesign.SourceRelativePath, banner, StringComparison.Ordinal);
        Assert.Contains("TrophyBanner", overlay, StringComparison.Ordinal);
        Assert.Equal("trophy.html", spec.OverlayDocument);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ExoLauncher.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
