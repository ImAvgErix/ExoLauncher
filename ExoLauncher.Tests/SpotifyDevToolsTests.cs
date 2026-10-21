using ExoLauncher.Helpers;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the private DevTools pipe can fail to reach Spotify (security/attacks/local, attack 1):
/// - the C runtime block has the wrong size or count, so Chromium sees no descriptors 3 and 4;
/// - 3 and 4 are not flagged open pipes (FOPEN | FPIPE), so Chromium ignores them;
/// - the read and write ends are swapped, or 0-2 carry a real handle;
/// - the launch still asks for a debug port another program could find.
/// </summary>
public sealed class SpotifyDevToolsTests
{
    [Fact]
    public void DescriptorBlockHandsOverThreeAndFour()
    {
        var block = SpotifyDevTools.CrtDescriptorBlock(0x1234, 0x5678);
        Assert.Equal(4 + 5 + 5 * IntPtr.Size, block.Length);
        Assert.Equal(5, BitConverter.ToInt32(block, 0));
        Assert.Equal(new byte[] { 0, 0, 0, 0x09, 0x09 }, block[4..9]);
        long Handle(int fd) => BitConverter.ToInt64(block, 9 + fd * IntPtr.Size);
        Assert.Equal(-1, Handle(0));
        Assert.Equal(-1, Handle(1));
        Assert.Equal(-1, Handle(2));
        Assert.Equal(0x1234, Handle(3));
        Assert.Equal(0x5678, Handle(4));
    }

    [Fact]
    public void SpotifyIsLaunchedWithThePipeAndNoPort()
    {
        Assert.Contains("--remote-debugging-pipe", SpotifyControls.EngineArguments);
        Assert.DoesNotContain("--remote-debugging-port", SpotifyControls.EngineArguments);
    }
}
