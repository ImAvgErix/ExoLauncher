using System.Buffers.Binary;
using ExoLauncher.Services.DiscordClient;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// Go Live's own picker and pipe (docs/architecture/discord-video.md). Ways it could fail silently:
/// the picker lists Exo itself, the taskbar, a tool or overlay window, a dialog, a window cloaked on
/// another desktop or one off every screen, or hides a real app window that is owned but marked as an
/// app window; the plan upscales a small window, goes past H.264's 4096-pixel side or 4K's pixels,
/// hands the encoder an odd size, or picks a bit rate that falls as quality rises or leaves 1-10 Mbit/s;
/// the pipe reader misreads the header's byte order, loses a frame, takes a huge length as a frame,
/// or throws instead of ending when the add-on goes; capture times land on the wrong RTP clock; a raw
/// Windows error reaches the user instead of a sentence.
/// </summary>
public sealed class GoLiveTests
{
    private static GoLiveSources.WindowFacts App(string title = "Document - Notepad") =>
        new(Owned: false, AppWindow: false, Tool: false, NoActivate: false, Cloaked: false, Exo: false, OnScreen: true,
            Title: title, ClassName: "Notepad", Width: 1200, Height: 800);

    [Fact]
    public void ListsWhatAltTabShowsAndNothingElse()
    {
        Assert.True(GoLiveSources.Shareable(App()));
        Assert.False(GoLiveSources.Shareable(App() with { Exo = true }));
        Assert.False(GoLiveSources.Shareable(App() with { Cloaked = true }));
        Assert.False(GoLiveSources.Shareable(App() with { Tool = true }));
        Assert.False(GoLiveSources.Shareable(App() with { Owned = true }));
        Assert.False(GoLiveSources.Shareable(App() with { NoActivate = true }));
        Assert.False(GoLiveSources.Shareable(App() with { OnScreen = false }));
        Assert.False(GoLiveSources.Shareable(App("   ")));
        Assert.False(GoLiveSources.Shareable(App() with { Width = 100, Height = 60 }));
        Assert.False(GoLiveSources.Shareable(App() with { ClassName = "Shell_TrayWnd" }));
        Assert.False(GoLiveSources.Shareable(App() with { ClassName = "Progman" }));
        // An owned window that asks for a taskbar button is an app window (Alt+Tab shows it).
        Assert.True(GoLiveSources.Shareable(App() with { Owned = true, AppWindow = true }));
        // Exo's own and a cloaked one stay out even then.
        Assert.False(GoLiveSources.Shareable(App() with { Owned = true, AppWindow = true, Exo = true }));
    }

    [Theory]
    [InlineData(2560, 1440, 1080, 60, 1920, 1080)] // a 1440p screen at 1080p
    [InlineData(3440, 1440, 1080, 30, 2580, 1080)] // ultrawide keeps its shape
    [InlineData(1280, 720, 1080, 30, 1280, 720)]   // a small window is never made bigger
    [InlineData(1001, 701, 0, 30, 1000, 700)]      // even sides for the encoder
    [InlineData(5120, 1440, 0, 60, 4096, 1152)]    // H.264's 4096-pixel side
    [InlineData(7680, 4320, 0, 60, 3840, 2160)]    // no more than 4K's pixels
    [InlineData(1920, 1080, 1440, 60, 1920, 1080)] // 1440p of a 1080p screen is the screen
    public void PlansTheSizeFromTheSourceAndTheChoice(int sourceWidth, int sourceHeight, int height, int fps, int width, int outHeight)
    {
        var plan = GoLivePlan.For(sourceWidth, sourceHeight, height, fps);
        Assert.Equal((width, outHeight), (plan.Width, plan.Height));
        Assert.True(plan.Width % 2 == 0 && plan.Height % 2 == 0);
    }

    [Fact]
    public void BitRateIsTheUsualBestForEachSizeAndRate()
    {
        int Rate(int height, int fps) => GoLivePlan.For(3840, 2160, height, fps).Bitrate;
        Assert.Equal(5_000_000, Rate(720, 30));
        Assert.Equal(8_000_000, Rate(1080, 30));
        Assert.Equal(12_000_000, Rate(1080, 60));
        Assert.Equal(18_000_000, Rate(1080, 120));
        Assert.Equal(24_000_000, Rate(1440, 60));
        Assert.Equal(36_000_000, Rate(1440, 120));
        Assert.Equal(40_000_000, Rate(0, 30));
        Assert.Equal(60_000_000, Rate(0, 60));
        Assert.True(Rate(720, 15) < Rate(720, 30));
        Assert.Equal(1_000_000, GoLivePlan.For(160, 120, 0, 15).Bitrate);
        // Between the table's points, by pixels: an ultrawide 1440p sits between 1440p and 4K.
        Assert.InRange(GoLivePlan.For(3440, 1440, 0, 60).Bitrate, 24_000_001, 59_999_999);
    }

    [Fact]
    public void OneHundredTwentyFpsRunsUpTo1440pAndAboveThatGoesAt60()
    {
        Assert.Equal(120, GoLivePlan.For(2560, 1440, 0, 120).Fps);
        Assert.Equal(120, GoLivePlan.For(1920, 1080, 0, 144).Fps);
        Assert.Equal(60, GoLivePlan.For(3840, 2160, 0, 120).Fps);
        Assert.Equal(120, GoLivePlan.For(3840, 2160, 1440, 120).Fps);
        Assert.Equal(30, GoLivePlan.For(1920, 1080, 1080, 45).Fps);
        Assert.Equal(15, GoLivePlan.For(1920, 1080, 1080, 5).Fps);
    }

    private static byte[] Frame(byte[] data, bool key, long time)
    {
        var bytes = new byte[GoLiveCapture.HeaderSize + data.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), key ? 1u : 0u);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(8), time);
        data.CopyTo(bytes, GoLiveCapture.HeaderSize);
        return bytes;
    }

    [Fact]
    public void ReadsEveryFrameOffThePipeAndEndsWhenItCloses()
    {
        byte[] key = [0, 0, 0, 1, 0x67, 1, 2, 0, 0, 0, 1, 0x65, 9], delta = [0, 0, 0, 1, 0x41, 7];
        using var pipe = new MemoryStream([.. Frame(key, true, 10_000_000), .. Frame(delta, false, 10_166_666), .. Frame([], false, 10_333_333)]);
        var got = new List<(byte[] Data, uint Time)>();
        var why = GoLiveCapture.ReadFrames(pipe, (data, time) => got.Add((data, time)), (_, _) => { }, () => false);
        Assert.Equal("Exo Capture stopped sending the picture", why);
        Assert.Equal(3, got.Count);
        Assert.Equal(key, got[0].Data);
        Assert.Equal(delta, got[1].Data);
        Assert.Empty(got[2].Data);
        Assert.Equal(90_000u, got[0].Time);
        Assert.Equal(91_499u, got[1].Time);
    }

    [Fact]
    public void AHugeLengthIsABrokenFrameNotAnAllocation()
    {
        var header = new byte[GoLiveCapture.HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, uint.MaxValue);
        using var pipe = new MemoryStream(header);
        var frames = 0;
        Assert.Equal("Exo Capture sent a broken frame", GoLiveCapture.ReadFrames(pipe, (_, _) => frames++, (_, _) => { }, () => false));
        Assert.Equal(0, frames);
        // A frame cut short by the add-on stopping ends the read, it doesn't throw.
        using var cut = new MemoryStream(Frame([1, 2, 3, 4], true, 0)[..18]);
        Assert.Equal("Exo Capture stopped sending the picture", GoLiveCapture.ReadFrames(cut, (_, _) => frames++, (_, _) => { }, () => false));
        Assert.Equal(0, frames);
    }

    [Fact]
    public void HeaderIsLittleEndianWithTheKeyFlagInBitZero()
    {
        var (length, key, time) = GoLiveCapture.ParseHeader(Frame(new byte[300], true, 0x0102030405060708));
        Assert.Equal((300, true, 0x0102030405060708L), (length, key, time));
        Assert.False(GoLiveCapture.ParseHeader(Frame([], false, 0)).Key);
    }

    [Fact]
    public void CaptureTimesLandOnTheNinetyKilohertzClockAndWrap()
    {
        Assert.Equal(90_000u, GoLiveCapture.RtpTime(10_000_000));
        Assert.Equal(1_500u, GoLiveCapture.RtpTime(166_667));
        // Days of uptime wrap the 32-bit RTP timestamp instead of failing.
        var later = GoLiveCapture.RtpTime(10_000_000L * 86_400 * 10);
        Assert.Equal(unchecked((uint)(90_000L * 86_400 * 10)), later);
    }

    [Fact]
    public void TheUserReadsASentenceNotAWindowsError()
    {
        Assert.Equal("That window gives no picture; is it minimized?",
            GoLiveCapture.Explain(new InvalidOperationException("That window gives no picture; is it minimized?")));
        Assert.Equal("Exo Capture needs updating to share your screen.", GoLiveCapture.Explain(new InvalidOperationException("Unknown request.")));
        Assert.Equal("Windows didn't let Exo capture that window. Share the screen it's on instead.",
            GoLiveCapture.Explain(new InvalidOperationException("capture the window (0x80070005)")));
        // Only the capture is Windows' refusal; the graphics card's encoder is named as itself.
        Assert.Equal("Your graphics card's video encoder didn't start, and neither did Windows' own. Try a smaller resolution or frame rate.",
            GoLiveCapture.Explain(new InvalidOperationException("encoder output type (0xC00D36B4)")));
        Assert.Equal("Your graphics card has no video encoder Exo can use, and sharing your screen needs one.",
            GoLiveCapture.Explain(new InvalidOperationException("no hardware video encoder (0xC00D5212)")));
        Assert.Equal("Exo Capture couldn't use your graphics card to share your screen.",
            GoLiveCapture.Explain(new InvalidOperationException("D3D11 device (0x887A0004)")));
        Assert.Equal("Exo Capture didn't start the stream in time.", GoLiveCapture.Explain(new TimeoutException()));
    }
}
