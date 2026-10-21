using ExoLauncher.Services.DiscordClient;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class DiscordColorsTests
{
    [Fact]
    public void ARoleColorThatReadsOnBlackIsKeptExactly()
    {
        Assert.Equal("#ed1a1a", DiscordColors.Readable(0xed1a1a));
        Assert.Equal("#bc0be0", DiscordColors.Readable(0xbc0be0));
    }

    [Fact]
    public void ANearBlackRoleColorIsLiftedUntilItReads()
    {
        var lifted = DiscordColors.Readable(0x060606);
        Assert.NotEqual("#060606", lifted);
        var value = Convert.ToInt32(lifted[1..], 16);
        // Lifted toward white, not past what it takes: still a dark gray.
        Assert.InRange((value >> 16) & 0xff, 80, 140);
    }
}
