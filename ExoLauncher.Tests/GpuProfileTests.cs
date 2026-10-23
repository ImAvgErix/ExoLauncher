using ExoLauncher.Helpers;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways Exo's GPU profile could go wrong silently:
/// - a card is read as the wrong series (a Ti, SUPER or laptop name, a TITAN,
///   a GTX 900 or workstation card taken for a GeForce 10-50), so it gets
///   DLSS settings it cannot run or the wrong model;
/// - a forced DLSS letter is written under the "Recommended" model profile,
///   where the driver ignores it;
/// - a setting in a real global profile is not decided (neither written nor
///   deliberately left alone);
/// - the frame cap is not under the refresh rate, or not Reflex's;
/// - G-SYNC is missing a switch (full screen and windowed, driver V-Sync, the
///   cap), or No G-SYNC leaves variable refresh, a forced V-Sync or a cap on;
/// - an id the driver does not list is written (it refuses the whole apply);
/// - the shader cache is made smaller than what the user had;
/// - "Applied" shows while the driver holds something else, or a re-apply or
///   a switch of variant overwrites the first backup so Undo cannot get back
///   to the start;
/// - a Radeon or Intel GPU Exo can't reach is told to set Anti-Lag everywhere, or a frame cap without its fps.
/// </summary>
public sealed class GpuProfileTests
{
    // Every setting id in the RTX 3070 global profile (driver 617.42) the profile was built from, as Profile Inspector exported it.
    private static readonly uint[] Rtx3070Global =
    [
        390467, 983226, 983227, 983295, 1675263, 2166816, 3066610, 5912412, 6505105, 6600001, 6710836, 6710885, 6714153, 6776373,
        6776937, 8102046, 8703344, 9963649, 9963670, 10011052, 11041231, 11250721, 11250737, 11306135, 12991097, 13510289, 14019014,
        14019015, 14366808, 15151633, 15389065, 28027939, 269553971, 269573257, 270198627, 270426537, 271895433, 271965065, 272909380,
        273507943, 274197361, 274606621, 276089202, 276158834, 276495451, 276652957, 276757595, 277041150, 277041152, 277041154,
        277041157, 277041158, 277041162, 277041199, 278196567, 278196727, 278262127, 279476652, 279476686, 279476687, 279951208,
        280859683, 281106605, 281531554, 282245910, 282364549, 282555346, 282663500, 283385329, 283385331, 283385333, 283385334,
        283385335, 283385345, 283385346, 283385347, 283385348, 283958146, 283962569, 284962204, 286335539, 286335574, 294973784,
        538927519, 539527361, 540508738, 543266006, 543959236, 544392611, 545898348, 547222078, 549528094, 550932728, 553505273,
        1343646814, 2966161525,
    ];

    // A driver that lists every id (newer drivers); tests that need a shorter list pass their own.
    private static readonly HashSet<uint> Every = [.. Rtx3070Global, 0xB0D384C0, 0x10308298, 0x000BFA21];

    private const uint SrPreset = 0x10E41DF3, RrPreset = 0x10E41DF7, FgPreset = 0x10E41DF1, SrOverride = 0x10E41E01, FgOverride = 0x10E41E03;
    private const uint LowLatency = 0x0005F543, UltraLowLatency = 0x10835000, PreRendered = 0x007BA09E;

    private static readonly Dictionary<uint, uint?> Nothing = [];

    private static IReadOnlyDictionary<uint, uint?> Profile(int series, bool gsync, int hz = 165, long freeGb = 500, IReadOnlySet<uint>? known = null, Dictionary<uint, uint?>? current = null) =>
        GpuProfile.Nvidia(new GpuProfile.Variant(series, gsync, hz), freeGb, known ?? Every, current ?? Nothing);

    [Theory]
    [InlineData("NVIDIA GeForce RTX 3070", 30)]
    [InlineData("NVIDIA GeForce RTX 3070Ti", 30)]
    [InlineData("NVIDIA GeForce GTX 1080 Ti", 10)]
    [InlineData("NVIDIA GeForce GTX 1050", 10)]
    [InlineData("NVIDIA GeForce GTX 1660 SUPER", 16)]
    [InlineData("NVIDIA GeForce RTX 2060", 20)]
    [InlineData("NVIDIA GeForce RTX 4090 Laptop GPU", 40)]
    [InlineData("NVIDIA GeForce RTX 5070 Ti", 50)]
    [InlineData("NVIDIA TITAN RTX", 20)]
    [InlineData("NVIDIA TITAN Xp", 10)]
    [InlineData("NVIDIA GeForce GTX 970", null)]
    [InlineData("NVIDIA GeForce GTX TITAN X", null)]
    [InlineData("NVIDIA RTX A4000", 30)]
    [InlineData("NVIDIA Quadro RTX 5000", 20)]
    [InlineData("NVIDIA RTX 4000 Ada Generation", 40)]
    [InlineData("NVIDIA RTX PRO 6000 Blackwell Workstation Edition", 50)]
    [InlineData("NVIDIA GeForce RTX 6080", 50)]
    [InlineData("NVIDIA GeForce MX450", null)]
    [InlineData("AMD Radeon RX 7900 XTX", null)]
    public void ReadsTheSeriesFromTheName(string adapter, int? series) => Assert.Equal(series, GpuProfile.NvidiaSeries(adapter));

    [Theory]
    [InlineData(60, 57)]
    [InlineData(75, 72)]
    [InlineData(144, 138)]
    [InlineData(165, 157)]
    [InlineData(240, 225)]
    [InlineData(360, 327)]
    [InlineData(0, 0)]
    public void CapIsReflexs(int hz, int cap) => Assert.Equal(cap, GpuProfile.FrameCap(hz));

    [Fact]
    public void EverySettingOfARealGlobalProfileIsDecided()
    {
        foreach (var series in new[] { 10, 16, 20, 30, 40, 50 })
            foreach (var gsync in new[] { true, false })
            {
                var writes = Profile(series, gsync);
                // Decided: written, or a rule that keeps it the player's (Smooth Motion, frame generation on GTX), or left alone.
                Assert.All(Rtx3070Global, id => Assert.True(writes.ContainsKey(id) || GpuProfile.Decides(id) || GpuProfile.LeftAlone.Contains(id), $"0x{id:X8} undecided"));
                Assert.DoesNotContain(GpuProfile.LeftAlone, writes.ContainsKey);
            }
    }

    [Fact]
    public void GsyncTurnsOnEverySwitchWithDriverVsyncAndACap()
    {
        var writes = Profile(30, gsync: true);
        Assert.Equal(1u, writes[GpuProfile.GsyncFeature]);
        Assert.Equal(1u, writes[GpuProfile.GsyncGlobalMode]);
        Assert.Equal(1u, writes[GpuProfile.GsyncAppMode]);
        Assert.Equal(1u, writes[GpuProfile.VrrControl]);
        Assert.Null(writes[GpuProfile.GsyncAppState]);          // Allow
        Assert.Equal(GpuProfile.VSyncOn, writes[GpuProfile.VSync]);
        Assert.Equal(157u, writes[GpuProfile.FrameRateLimit]);
        Assert.Equal(157u, writes[GpuProfile.FrameRateLimitSlider]);
        // No refresh rate read: G-SYNC still on, but no cap to pick.
        Assert.Null(Profile(30, gsync: true, hz: 0)[GpuProfile.FrameRateLimit]);
    }

    [Fact]
    public void NoGsyncTurnsVariableRefreshOffAndLeavesSyncAndCapToGames()
    {
        var writes = Profile(30, gsync: false);
        Assert.Equal(0u, writes[GpuProfile.GsyncFeature]);
        Assert.Equal(0u, writes[GpuProfile.GsyncGlobalMode]);
        Assert.Equal(0u, writes[GpuProfile.GsyncAppMode]);
        Assert.Equal(0u, writes[GpuProfile.VrrControl]);
        Assert.Null(writes[GpuProfile.VSync]);
        Assert.Null(writes[GpuProfile.FrameRateLimit]);
        Assert.Null(writes[GpuProfile.FrameRateLimitSlider]);
    }

    [Fact]
    public void SyncChoiceChangesOnlySyncSettings()
    {
        var gsync = Profile(40, gsync: true);
        var plain = Profile(40, gsync: false);
        var differ = gsync.Keys.Where(id => gsync[id] != plain[id]).Order().ToList();
        Assert.Equal(new[] { GpuProfile.VSync, GpuProfile.GsyncFeature, GpuProfile.GsyncGlobalMode, GpuProfile.FrameRateLimit, GpuProfile.FrameRateLimitSlider, GpuProfile.VrrControl, GpuProfile.GsyncAppMode }.Order(), differ);
    }

    [Fact]
    public void LowLatencyIsOnNotUltra()
    {
        var writes = Profile(16, gsync: false);
        Assert.Equal(1u, writes[LowLatency]);
        Assert.Equal(0u, writes[UltraLowLatency]);
        Assert.Equal(1u, writes[PreRendered]);
        Assert.False(writes.ContainsKey(0xB0D384C0));           // Smooth Motion is the player's: never touched
    }

    [Fact]
    public void NeverWritesAnIdTheDriverDoesNotList()
    {
        var known = new HashSet<uint>(Every);
        known.Remove(GpuProfile.FrameRateLimit);
        known.Remove(FgPreset);
        known.Remove(LowLatency);
        var writes = Profile(40, gsync: true, known: known);
        Assert.All(writes.Keys, id => Assert.Contains(id, known));
    }

    [Fact]
    public void DlssFollowsTheSeries()
    {
        foreach (var rtxSeries in new[] { 20, 30, 40, 50 })
        {
            var rtx = Profile(rtxSeries, gsync: false);
            var fp8 = rtxSeries >= 40;
            Assert.Equal(fp8 ? 0x00FFFFFFu : 11u, rtx[SrPreset]); // Recommended on RTX 40/50, K before
            Assert.Equal(fp8 ? 0x00FFFFFFu : 6u, rtx[RrPreset]);  // Recommended (F today) on RTX 40/50, F before
            Assert.Equal(2u, rtx[GpuProfile.ModelPresetProfile]); // Custom: forced letters apply only under it
            Assert.Equal(3u, rtx[FgPreset]);                      // C, the performance preset
            Assert.Equal(1u, rtx[SrOverride]);                    // newest DLLs
            Assert.Equal(1u, rtx[FgOverride]);
        }
        foreach (var gtx in new[] { 10, 16, 0 })
        {
            var writes = Profile(gtx, gsync: true);
            Assert.All(new[] { SrPreset, RrPreset, SrOverride, GpuProfile.ModelPresetProfile }, id => Assert.Null(writes[id]));
            Assert.False(writes.ContainsKey(FgPreset));
            Assert.False(writes.ContainsKey(FgOverride));
        }
    }

    [Fact]
    public void ShaderCacheNeverShrinks()
    {
        Assert.Equal(GpuProfile.CacheUnlimited, Profile(30, false, freeGb: 500)[GpuProfile.ShaderCacheSize]);
        // Short of space: the driver's own 16 GB, or what the user set, never a smaller fixed size.
        Assert.Null(Profile(30, false, freeGb: 50)[GpuProfile.ShaderCacheSize]);
        Assert.Equal(0x2800u, Profile(30, false, freeGb: 50, current: new() { [GpuProfile.ShaderCacheSize] = 0x2800 })[GpuProfile.ShaderCacheSize]);
        // Off (0), 4 GB and the 100 GB earlier Exo builds set all grow to Unlimited; Unlimited stays.
        foreach (var size in new uint[] { 0, 0x1000, 0x19000, GpuProfile.CacheUnlimited })
            Assert.Equal(GpuProfile.CacheUnlimited, Profile(30, false, current: new() { [GpuProfile.ShaderCacheSize] = size })[GpuProfile.ShaderCacheSize]);
    }

    [Fact]
    public void StatusComparesEveryValue()
    {
        var writes = Profile(30, gsync: true);
        var held = new Dictionary<uint, uint?>(writes);
        Assert.Equal("applied", GpuProfile.Status(writes, held, applied: true));
        Assert.Equal("applied", GpuProfile.Status(writes, held, applied: false));

        // A driver update put the frame cap back to its default.
        var moved = new Dictionary<uint, uint?>(held) { [GpuProfile.FrameRateLimit] = null };
        Assert.Equal("changed", GpuProfile.Status(writes, moved, applied: true));
        Assert.Equal("ready", GpuProfile.Status(writes, moved, applied: false));

        // "Driver default" is the profile's value: an id the driver holds nothing for matches.
        var plain = Profile(30, gsync: false);
        Assert.Equal("applied", GpuProfile.Status(plain, plain.Where(write => write.Value is not null).ToDictionary(), applied: true));
    }

    [Fact]
    public void ReapplyAndVariantSwitchKeepTheFirstBackup()
    {
        var first = GpuProfile.MergeBackup(null, new Dictionary<uint, uint?> { [1] = 60, [2] = null }, [1u, 2u]);
        Assert.Equal(new Dictionary<uint, uint?> { [1] = 60, [2] = null }, first);
        // After apply the driver holds Exo's values; a re-apply with one new id keeps the originals.
        var again = GpuProfile.MergeBackup(first, new Dictionary<uint, uint?> { [1] = 157, [2] = 1, [3] = 7 }, [1u, 2u, 3u]);
        Assert.Equal(new Dictionary<uint, uint?> { [1] = 60, [2] = null, [3] = 7 }, again);
    }

    [Fact]
    public void AmdAndIntelOutOfReachAreShownNotWritten()
    {
        var screen = new GpuProfile.Screen("Screen", 144, false);
        var amd = GpuProfile.Amd(screen, [("Anti-Lag", "Off"), ("Radeon Chill", "On"), ("Wait for Vertical Refresh", "Always on")]);
        var intel = GpuProfile.Intel(screen);
        Assert.Equal("Off", amd.Single(item => item.Key == "antiLag").Current);
        Assert.Equal("On in each game", amd.Single(item => item.Key == "antiLag").Value);
        Assert.Equal("138 fps", amd.Single(item => item.Key == "frameCap").Value);
        Assert.Equal("Off, unless application specifies", amd.Single(item => item.Key == "vsync").Value);
        Assert.Equal("Always on", amd.Single(item => item.Key == "vsync").Current);
        Assert.Equal("138 fps", intel.Single(item => item.Key == "frameCap").Value);
        Assert.Equal("Off", intel.Single(item => item.Key == "endurance").Value);
        Assert.All(intel, item => Assert.Null(item.Current));
    }

    [Theory]
    [InlineData(10, false)]
    [InlineData(30, true)]
    [InlineData(50, false)]
    public void EveryProfileIsPerformanceFirst(int series, bool gsync)
    {
        var profile = Profile(series, gsync);
        Assert.Equal(1u, profile[0x1057EB71]);      // power: prefer maximum performance
        Assert.Equal(0x14u, profile[0x00CE2691]);   // texture filtering: High performance
        Assert.Equal(3u, profile[0x00EAD189]);      // compile shaders while idle: High
        Assert.Null(profile[0x10D2BB16]);           // anisotropic filtering stays the game's own
        Assert.Null(profile[0x000F00BA]);           // Resizable BAR: NVIDIA's per-game list, old switch
        Assert.Null(profile[0x000BFA21]);           // and the v2 switch (616.56+)
    }
}
