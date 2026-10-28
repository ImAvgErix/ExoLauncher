using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the hotfix lookup could fail (System > Graphics then offers an older driver than NVIDIA has, or a wrong one):
/// - it takes a forum user's topic, or a bug report that mentions a hotfix driver, as NVIDIA's announcement;
/// - it reads the search's highlighting markup into the title and misses "Hotfix";
/// - it reads an older topic before the newest one;
/// - it reads the version from the post's text instead of NVIDIA's download link, or takes a link off NVIDIA's servers;
/// - with two links in one post it keeps the older driver;
/// - when the forum's search hasn't caught up (621.12's announcement wasn't found the day it came out), the check of
///   NVIDIA's download server never asks for the name the hotfix took, or asks for an older one than the driver installed.
/// </summary>
public sealed class NvidiaHotfixTests
{
    private const string Search = """
        {"success":true,"data":{
          "discussion":[
            {"id":591809,"title":"Pulsar <span class=\"highlight\">Hotfix</span> <span class=\"highlight\">Driver</span>","createdStamp":1792000000,"user":{"aclactions":{"officialreply":true}}},
            {"id":201500,"title":"Announcing <span class=\"highlight\">Hotfix</span> driver 353.00","createdStamp":1432344119,"user":{"aclactions":{"officialreply":true}}}
          ],
          "discussionTotal":2,
          "support":[
            {"id":591295,"title":"[Bug] Hotfix <span class=\"highlight\">Driver</span> 617.70 black screen","createdStamp":1792100000,"user":{"aclactions":{}}},
            {"id":590100,"title":"Announcing GeForce Hotfix Driver 616.86 (Released 9/4/26)","createdStamp":1788500000,"user":{"aclactions":{"officialreply":true}}},
            {"id":590200,"title":"GeForce GRD 616.64 Feedback Thread","createdStamp":1788600000,"user":{"aclactions":{"officialreply":true}}}
          ]
        }}
        """;

    [Fact]
    public void TakesOnlyNvidiaStaffHotfixTopicsNewestFirst()
    {
        Assert.Equal([591809L, 590100L, 201500L], NvidiaDriverService.HotfixTopics(Search));
    }

    [Fact]
    public void ASearchWithoutResultsHasNoTopics()
    {
        Assert.Empty(NvidiaDriverService.HotfixTopics("""{"success":false,"errors":{},"data":[]}"""));
    }

    [Fact]
    public void ReadsTheVersionFromNvidiasDownloadLink()
    {
        const string topic = """
            {"data":{"topic":{"firstComment":{"body":"## hotfix driver\nWe now have a [hotfix driver](https://international.download.nvidia.com/Windows/617.70hf/617.70-desktop-notebook-win10-win11-64bit-international-dch.hf.exe) for 616.86's bugs."}}}}
            """;
        var hotfix = NvidiaDriverService.HotfixIn(topic);
        Assert.NotNull(hotfix);
        Assert.Equal("617.70", hotfix.Version);
        Assert.Equal("https://international.download.nvidia.com/Windows/617.70hf/617.70-desktop-notebook-win10-win11-64bit-international-dch.hf.exe", hotfix.Url);
        Assert.Equal("hotfix", hotfix.Kind);
    }

    [Fact]
    public void KeepsTheNewestOfSeveralLinks()
    {
        const string topic = """
            {"data":{"topic":{"firstComment":{"body":"old: https://us.download.nvidia.com/Windows/616.86hf/616.86-desktop-notebook-win10-win11-64bit-international-dch.hf.exe new: https://international.download.nvidia.com/Windows/617.70hf/617.70-desktop-notebook-win10-win11-64bit-international-dch.hf.exe"}}}}
            """;
        Assert.Equal("617.70", NvidiaDriverService.HotfixIn(topic)?.Version);
    }

    [Theory]
    [InlineData("621.01", "621.12")] // 2026-10-27, missed by the forum's search
    [InlineData("616.64", "616.86")]
    [InlineData("610.47", "610.82")]
    [InlineData("595.97", "596.02")] // a hotfix in the next hundred
    public void TheServerCheckAsksForTheNameAHotfixTook(string gameReady, string hotfix)
    {
        var candidates = NvidiaDriverService.HotfixCandidates(gameReady);
        Assert.Contains(hotfix, candidates);
        Assert.DoesNotContain(gameReady, candidates);
        // Newest first, so the first one there is the one to offer.
        Assert.Equal(candidates.OrderByDescending(v => v, Comparer<string>.Create(NvidiaDriverService.Compare)), candidates);
        Assert.All(candidates, v => Assert.True(NvidiaDriverService.Compare(v, gameReady) > 0));
    }

    [Theory]
    [InlineData("")]
    [InlineData("621")]
    [InlineData("621.x")]
    [InlineData("621.100")]
    public void NotADriverVersionAsksForNothing(string version) => Assert.Empty(NvidiaDriverService.HotfixCandidates(version));

    [Fact]
    public void TheServerChecksLinkIsOneTheForumReaderTakesAsNvidias()
    {
        Assert.Equal("https://international.download.nvidia.com/Windows/621.12hf/621.12-desktop-notebook-win10-win11-64bit-international-dch.hf.exe", NvidiaDriverService.HotfixUrl("621.12"));
        var topic = "{\"data\":{\"topic\":{\"firstComment\":{\"body\":\"" + NvidiaDriverService.HotfixUrl("621.12") + "\"}}}}";
        Assert.Equal("621.12", NvidiaDriverService.HotfixIn(topic)?.Version);
    }

    [Fact]
    public void IgnoresLinksOffNvidiasServersAndGameReadyPackages()
    {
        const string topic = """
            {"data":{"topic":{"firstComment":{"body":"https://example.com/Windows/699.99hf/699.99-desktop.hf.exe and https://us.download.nvidia.com/Windows/617.42/617.42-desktop-win10-win11-64bit-international-dch-whql.exe"}}}}
            """;
        Assert.Null(NvidiaDriverService.HotfixIn(topic));
    }
}
