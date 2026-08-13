using ExoLauncher.Adapters.Cli;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class SteamAppManifestScheduleTests
{
    private const string Cs2Manifest =
        """
        "AppState"
        {
        	"appid"		"730"
        	"name"		"Counter-Strike 2"
        	"StateFlags"		"6"
        	"BytesToDownload"		"2001619888"
        	"BytesDownloaded"		"0"
        	"buildid"		"24662694"
        	"TargetBuildID"		"24701871"
        	"ScheduledAutoUpdate"		"1786613863"
        	"AutoUpdateBehavior"		"0"
        }
        """;

    [Fact]
    public void MatchingTitle_ClearsOnlyTheScheduleField()
    {
        Assert.True(SteamAppManifestSchedule.TryClearScheduledAutoUpdate(
            Cs2Manifest,
            "730",
            "Counter-Strike 2",
            out var updated));

        Assert.Equal("0", SteamProtocol.MatchAcfField(updated, "ScheduledAutoUpdate"));
        Assert.Equal("730", SteamProtocol.MatchAcfField(updated, "appid"));
        Assert.Equal("Counter-Strike 2", SteamProtocol.MatchAcfField(updated, "name"));
        Assert.Equal("2001619888", SteamProtocol.MatchAcfField(updated, "BytesToDownload"));
        Assert.Equal("1786613863", SteamProtocol.MatchAcfField(Cs2Manifest, "ScheduledAutoUpdate"));
    }

    [Fact]
    public void WrongTitle_LeavesTheManifestUntouched()
    {
        Assert.False(SteamAppManifestSchedule.TryClearScheduledAutoUpdate(
            Cs2Manifest,
            "730",
            "Counter-Strike 2 Dedicated Server",
            out var updated));
        Assert.Equal(Cs2Manifest, updated);
    }

    [Fact]
    public void WrongAppId_LeavesTheManifestUntouched()
    {
        Assert.False(SteamAppManifestSchedule.TryClearScheduledAutoUpdate(
            Cs2Manifest,
            "570",
            "Counter-Strike 2",
            out var updated));
        Assert.Equal(Cs2Manifest, updated);
    }

    [Fact]
    public void AlreadyCleared_IsANoOp()
    {
        Assert.True(SteamAppManifestSchedule.TryClearScheduledAutoUpdate(
            Cs2Manifest,
            "730",
            "Counter-Strike 2",
            out var cleared));
        Assert.False(SteamAppManifestSchedule.TryClearScheduledAutoUpdate(
            cleared,
            "730",
            "Counter-Strike 2",
            out var again));
        Assert.Equal(cleared, again);
    }
}
