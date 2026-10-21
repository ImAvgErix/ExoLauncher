using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

// After an install Exo reads the driver Windows records (32.0.16.1770), because NvAPI in Exo's process still answers
// for the driver it loaded with: the install looked failed although it worked.
public sealed class NvidiaDriverVersionTests
{
    [Theory]
    [InlineData("32.0.16.1770", "617.70")]
    [InlineData("32.0.16.1742", "617.42")]
    [InlineData("32.0.15.8180", "581.80")]
    [InlineData("31.0.15.5212", "552.12")]
    public void Windows_driver_version_reads_as_NVIDIAs(string windows, string nvidia) =>
        Assert.Equal(nvidia, NvidiaDriverService.FromWindowsVersion(windows));

    [Theory]
    [InlineData("")]
    [InlineData("32.0.16")]
    [InlineData("32.0.x.1770")]
    [InlineData("32.0.16.17700")]
    public void Not_a_driver_version_reads_as_nothing(string windows) =>
        Assert.Null(NvidiaDriverService.FromWindowsVersion(windows));
}
