using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways guessing the headset from Windows' output name can go wrong:
/// - a jack's plain "Headphones (Realtek(R) Audio)" reads as a model (words like headphones, audio, wireless match anything);
/// - a model's shorter sibling wins over the model itself ("Arctis Nova 7" for an "Arctis Nova 7 Wireless"), or one
///   with a word the name doesn't have is offered;
/// - two models fit the name as well and one is picked at random; Lucid IEMs, which no device names, is offered.
/// </summary>
public sealed class LucidGuessTests
{
    private static LucidCatalog.Headphone H(string id, string brand, string model, string category = "Gaming Headsets") =>
        new(id, $"{brand} {model}", model, brand, category, "Good", "");

    private static readonly LucidCatalog.Headphone[] Catalog =
    [
        new(LucidSettings.LucidIems, "Lucid IEMs", "Lucid IEMs", "tekkusai", "In-Ear Monitors", "Excellent", "lucid iems"),
        H("nova7", "SteelSeries", "Arctis Nova 7"),
        H("nova7w", "SteelSeries", "Arctis Nova 7 Wireless"),
        H("novapro", "SteelSeries", "Arctis Nova Pro Wireless"),
        H("cloud3", "HyperX", "Cloud III Wireless"),
        H("gprox2", "Logitech", "G PRO X 2"),
        H("a50x", "Astro", "A50 X"),
        H("hd600", "Sennheiser", "HD 600", "Headphones"),
        H("hd6xx", "Sennheiser", "HD 6XX", "Headphones"),
    ];

    [Theory]
    [InlineData("Speakers (Arctis Nova 7)", "nova7")]
    [InlineData("Headset Earphone (Arctis Nova 7 Wireless)", "nova7w")]
    [InlineData("Speakers (Arctis Nova Pro Wireless)", "novapro")]
    [InlineData("Headphones (HyperX Cloud III Wireless)", "cloud3")]
    [InlineData("PRO X 2 LIGHTSPEED (Logitech G PRO X 2)", "gprox2")]
    [InlineData("Speakers (ASTRO A50 X Game)", "a50x")]
    public void AHeadsetThatNamesItself(string output, string id) => Assert.Equal(id, LucidCatalog.Guess(output, Catalog)?.Id);

    [Theory]
    [InlineData("Headphones (Realtek(R) Audio)")]
    [InlineData("Speakers (High Definition Audio Device)")]
    [InlineData("DualSense Edge Wireless Controller")]
    [InlineData("Wireless Headset Hands-Free AG Audio")]
    [InlineData("")]
    [InlineData(null)]
    public void NothingForAJackOrAnUnknownName(string? output) => Assert.Null(LucidCatalog.Guess(output, Catalog));

    [Fact]
    public void TwoThatFitAsWellAreNoGuess() =>
        Assert.Null(LucidCatalog.Guess("Speakers (Arctis Nova 7)", [.. Catalog, H("nova7b", "SteelSeries", "Arctis Nova 7")]));
}
