using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the offered update's whats-new.json could fail:
/// - an asset of another repository, host, tag or name (or plain HTTP) is fetched as the notes;
/// - the notes asset is mistaken for the installer, or hides a verified installer listed after it;
/// - notes for another version (an older file left on a new release) show as the upcoming version's;
/// - a file that is not the shape (no items, too many, missing or non-string fields) reaches the UI;
/// - a title or detail past its length, empty, or with control characters reaches the UI;
/// - an icon that is not a short Latin name (a path, markup, digits) reaches the UI;
/// - a file over 64 KB, or JSON nested deep or malformed, throws instead of being no notes.
/// </summary>
public sealed class WhatsNewAssetTests
{
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static string Notes(string version = "3.15.0", string items = """[{ "icon": "Sliders", "title": "Starts lighter", "detail": "Exo opens sooner." }]""") =>
        $$"""{ "version": "{{version}}", "items": {{items}} }""";

    private static string Item(string icon = "Sliders", string title = "Title", string detail = "Detail") =>
        $$"""{ "icon": "{{icon}}", "title": "{{title}}", "detail": "{{detail}}" }""";

    [Fact]
    public void AcceptsAWellFormedFileForTheTag()
    {
        var notes = AppUpdateService.ParseWhatsNew(Notes(), "v3.15.0");
        Assert.NotNull(notes);
        Assert.Equal("3.15.0", notes.Version);
        var item = Assert.Single(notes.Items);
        Assert.Equal(("Sliders", "Starts lighter", "Exo opens sooner."), (item.Icon, item.Title, item.Detail));
    }

    [Theory]
    [InlineData("3.14.1")]
    [InlineData("3.15")]
    [InlineData("v3.15.0")]
    [InlineData("3.15.0-beta")]
    [InlineData("")]
    public void RejectsAnotherVersion(string version) =>
        Assert.Null(AppUpdateService.ParseWhatsNew(Notes(version), "v3.15.0"));

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("\"items\"")]
    [InlineData("[1]")]
    [InlineData("""[{ "icon": "Sliders", "title": "Title" }]""")]
    [InlineData("""[{ "icon": 3, "title": "Title", "detail": "Detail" }]""")]
    [InlineData("""[{ "icon": "Sliders", "title": null, "detail": "Detail" }]""")]
    public void RejectsTheWrongShape(string items) =>
        Assert.Null(AppUpdateService.ParseWhatsNew(Notes(items: items), "v3.15.0"));

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{ "items": [] }""")]
    [InlineData("""{ "version": 3, "items": [] }""")]
    [InlineData("")]
    public void RejectsNonsense(string json) =>
        Assert.Null(AppUpdateService.ParseWhatsNew(json, "v3.15.0"));

    [Fact]
    public void HoldsAtThirtyItems()
    {
        string Items(int n) => "[" + string.Join(",", Enumerable.Repeat(Item(), n)) + "]";
        Assert.Equal(30, AppUpdateService.ParseWhatsNew(Notes(items: Items(30)), "v3.15.0")?.Items.Length);
        Assert.Null(AppUpdateService.ParseWhatsNew(Notes(items: Items(31)), "v3.15.0"));
    }

    [Theory]
    [InlineData(80, 240, true)]
    [InlineData(81, 240, false)]
    [InlineData(80, 241, false)]
    public void BoundsTitleAndDetail(int title, int detail, bool accepted) =>
        Assert.Equal(accepted, AppUpdateService.ParseWhatsNew(Notes(items: $"[{Item(title: new string('a', title), detail: new string('b', detail))}]"), "v3.15.0") is not null);

    [Theory]
    [InlineData("Sliders", "Title", "Detail", true)]
    [InlineData("", "Title", "Detail", false)]
    [InlineData("Sliders", "  ", "Detail", false)]
    [InlineData("Sliders", "Title", "", false)]
    [InlineData("Sliders", "Line\\nbreak", "Detail", false)]
    [InlineData("Sliders", "Title", "Bell\\u0007", false)]
    [InlineData("../icons", "Title", "Detail", false)]
    [InlineData("<img>", "Title", "Detail", false)]
    [InlineData("Icon2", "Title", "Detail", false)]
    [InlineData("Ícone", "Title", "Detail", false)]
    [InlineData("AnIconNameThatIsFarTooLongToBeOne", "Title", "Detail", false)]
    public void ChecksEachField(string icon, string title, string detail, bool accepted) =>
        Assert.Equal(accepted, AppUpdateService.ParseWhatsNew(Notes(items: $"[{Item(icon, title, detail)}]"), "v3.15.0") is not null);

    [Fact]
    public void RejectsOversizedAndDeepFiles()
    {
        Assert.Null(AppUpdateService.ParseWhatsNew(Notes() + new string(' ', AppUpdateService.MaxWhatsNewBytes), "v3.15.0"));
        var deep = new string('[', 64) + new string(']', 64);
        Assert.Null(AppUpdateService.ParseWhatsNew(Notes(items: $"[{{ \"icon\": \"Sliders\", \"title\": \"T\", \"detail\": {deep} }}]"), "v3.15.0"));
    }

    [Theory]
    [InlineData("https://github.com/ImAvgErix/ExoLauncher/releases/download/v3.15.0/whats-new.json", true)]
    [InlineData("https://github.com/ImAvgErix/ExoLauncher/releases/download/v3.14.1/whats-new.json", false)]
    [InlineData("https://github.com/Someone/ExoLauncher/releases/download/v3.15.0/whats-new.json", false)]
    [InlineData("http://github.com/ImAvgErix/ExoLauncher/releases/download/v3.15.0/whats-new.json", false)]
    [InlineData("https://raw.githubusercontent.com/ImAvgErix/ExoLauncher/releases/download/v3.15.0/whats-new.json", false)]
    [InlineData("https://github.com/ImAvgErix/ExoLauncher/releases/download/v3.15.0/other.json", false)]
    public void TakesTheNotesOnlyFromTheSameRelease(string url, bool expected)
    {
        var json = $$"""
            {
              "tag_name": "v3.15.0",
              "assets": [
                { "name": "whats-new.json", "browser_download_url": "{{url}}", "size": 900 },
                { "name": "ExoLauncher-Setup.exe", "browser_download_url": "https://github.com/ImAvgErix/ExoLauncher/releases/download/v3.15.0/ExoLauncher-Setup.exe", "size": 1234, "digest": "sha256:{{Hash}}" }
              ]
            }
            """;
        Assert.True(AppUpdateService.TryParseReleaseJson(json, out var release));
        Assert.Equal(expected ? url : null, release.WhatsNewUrl);
        Assert.Equal("ExoLauncher-Setup.exe", release.AssetName);
        Assert.Equal(Hash, release.Sha256);
    }
}
