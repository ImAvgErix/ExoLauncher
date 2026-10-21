using System.Collections.Concurrent;
using ExoLauncher.Services.Hid;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the no-access fallback for collections Windows keeps can go wrong:
/// - a Razer mouse (settings as feature reports on its mouse collection) is skipped and never shows;
/// - a keyboard collection is opened, reaching hardware Exo Mouse has no business with;
/// - a mouse collection with no feature reports (a Finalmouse receiver) is opened for nothing;
/// - a vendor collection is treated as Windows' own.
/// </summary>
public sealed class HidServiceTests
{
    private static HidService.Collection Caps(int page, int usage, int featureLength) =>
        new(page, usage, [0], [], featureLength > 0 ? [0] : [], 8, 0, featureLength);

    [Fact]
    public void RazerMouseCollectionOpensForItsFeatureReports() =>
        Assert.True(HidService.OpensForFeaturesOnly(Caps(0x01, 0x02, 91)));

    [Fact]
    public void PointerCollectionWithFeaturesOpens() =>
        Assert.True(HidService.OpensForFeaturesOnly(Caps(0x01, 0x01, 65)));

    [Theory]
    [InlineData(0x01, 0x06, 9)]      // keyboard
    [InlineData(0x01, 0x07, 9)]      // keypad
    [InlineData(0x01, 0x02, 0)]      // mouse with no feature reports
    [InlineData(0x0C, 0x01, 9)]      // consumer control
    [InlineData(0xFF00, 0x01, 65)]   // vendor collection: opened read-write, not here
    public void EverythingElseIsLeftAlone(int page, int usage, int featureLength) =>
        Assert.False(HidService.OpensForFeaturesOnly(Caps(page, usage, featureLength)));
}

/// <summary>
/// Ways a mouse that is still there can drop out of a listing:
/// - its serial is read again on every listing, and one failed read (a receiver busy relaying its
///   mouse) gives it another key, so the page sees it leave and a stranger arrive;
/// - a string that failed on the first listing is never read again, so the device stays nameless;
/// - two listings run side by side and the older answers last, closing a device the newer one found.
/// </summary>
public sealed class HidListingTests
{
    private const string Path = "hid#vid_361d&pid_0300&mi_01";

    [Fact]
    public void AFailedSerialReadKeepsTheSerialAlreadyRead()
    {
        var strings = new ConcurrentDictionary<string, (string? Product, string? Serial)>();
        Assert.Equal(("StarlightX Dongle", "937AEB5354AAB2ED"), HidService.Remember(strings, Path, () => "StarlightX Dongle", () => "937AEB5354AAB2ED"));
        var reads = 0;
        var again = HidService.Remember(strings, Path, () => { reads++; return null; }, () => { reads++; return null; });
        Assert.Equal(("StarlightX Dongle", "937AEB5354AAB2ED"), again);
        Assert.Equal(0, reads);
    }

    [Fact]
    public void AStringThatFailedIsReadOnTheNextListing()
    {
        var strings = new ConcurrentDictionary<string, (string? Product, string? Serial)>();
        Assert.Equal(("StarlightX Dongle", ""), HidService.Remember(strings, Path, () => "StarlightX Dongle", () => null));
        Assert.Equal(("StarlightX Dongle", "937AEB5354AAB2ED"), HidService.Remember(strings, Path, () => "other", () => "937AEB5354AAB2ED"));
    }

    [Fact]
    public void AnOlderListingThatAnswersLastIsNotApplied()
    {
        long applied = 0;
        Assert.True(HidService.IsNewest(2, ref applied));
        Assert.False(HidService.IsNewest(1, ref applied));
        Assert.Equal(2, applied);
        Assert.True(HidService.IsNewest(3, ref applied));
    }
}
