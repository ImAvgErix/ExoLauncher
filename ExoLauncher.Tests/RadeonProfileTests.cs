using ExoLauncher.Helpers;
using ExoLauncher.Services;
using Keys = ExoLauncher.Helpers.AmdAdlx.Keys;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the Radeon profile could go wrong silently, run against a fake ADLX driver:
/// - a setting the driver lacks (an older ADLX without IADLX3DSettingsServices3's FSR override or
///   …1's Fluid Motion Frames, or a GPU that doesn't support it) is written, so the whole apply fails;
/// - Chill is turned off after Anti-Lag (Chill turns Anti-Lag off), Image Sharpening before Super Resolution
///   (which holds it on some GPUs), V-Sync's mode before Enhanced Sync (which turns V-Sync off), or the frame rate
///   target is enabled before its fps is set;
/// - Image Sharpening is left on, or V-Sync is forced on or off for every game instead of left to each game (Fluid
///   Motion Frames needs it off in the game);
/// - the frame cap is not NVIDIA's G-SYNC cap with FreeSync, stays on without FreeSync, or falls
///   outside the driver's range; a capable screen is left without FreeSync;
/// - FSR 4 for FSR 3.1 games goes on for a GPU the driver can't run it on (RX 5000, RX 6000 before
///   26.10.1, an APU), or stays off on RX 9000/7000;
/// - Fluid Motion Frames is written by the profile (it stays the player's);
/// - Undo can't get back to the start: the backup is taken after writing, a re-apply overwrites it,
///   a write that fails part way loses it, or it holds a launch's temporary switch;
/// - "Applied" shows while the driver holds something else, or a running game's switch reads as changed;
/// - a game's Fluid Motion Frames (or Anti-Lag while you play) is not switched on before launch, not
///   put back once no game runs, put back while another game still runs, put back to the wrong value
///   when two games overlap, or never put back after Exo stops mid-game;
/// - a PC without a Radeon, or with ADLX not starting, gets a write.
/// </summary>
public sealed class RadeonProfileTests : IDisposable
{
    private readonly string _folder = Path.Combine(TestDataDirectory.Root, "radeon-" + Guid.NewGuid().ToString("N"));
    private static readonly GpuProfile.Screen Screen = new("LG ULTRAGEAR", 144, false);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    /// <summary>An AMD driver as ADLX shows it: the settings it has, what each Set did, and Sets that fail.</summary>
    private sealed class FakeDriver : IGpuDriver
    {
        internal Dictionary<string, int> Values { get; } = new(StringComparer.Ordinal);
        internal string Gpu { get; set; } = "AMD Radeon RX 7800 XT";
        internal string? Software { get; set; } = "26.10.1";
        internal bool Down { get; set; }
        internal HashSet<string> Refuses { get; } = [];
        internal List<KeyValuePair<string, int>> Written { get; } = [];

        public GpuDriverState? Read() => Down ? null : new GpuDriverState(Gpu, Software, new Dictionary<string, int>(Values), 30, 300);

        public string? Write(IReadOnlyList<KeyValuePair<string, int>> writes)
        {
            if (Down) return "AMD's driver did not answer.";
            foreach (var write in writes)
            {
                if (!Values.ContainsKey(write.Key) || Refuses.Contains(write.Key)) return $"AMD's driver did not take {write.Key}.";
                Values[write.Key] = write.Value;
                Written.Add(write);
            }
            return null;
        }

        /// <summary>A Radeon on a current driver with every setting Exo touches, set the way a player might have them.</summary>
        internal static FakeDriver Current() => new()
        {
            Values =
            {
                [Keys.AntiLag] = 1, [Keys.Chill] = 1, [Keys.Boost] = 1, [Keys.Rsr] = 1, [Keys.EnhancedSync] = 1,
                [Keys.Sharpening] = 1, [Keys.VSync] = 3,
                [Keys.Frtc] = 0, [Keys.FrtcFps] = 300, [Keys.Fsr] = 0, [Keys.Afmf] = 0,
                [Keys.FreeSync + "LG ULTRAGEAR"] = 0, [Keys.FreeSync + "DELL P2419H"] = 1,
            },
        };
    }

    private sealed class Pc(bool amd = true)
    {
        internal bool Amd { get; set; } = amd;
        internal bool Playing { get; set; }
        internal bool AmdSoftware { get; set; } = true;
    }

    private RadeonProfileService Service(FakeDriver driver, Pc? pc = null)
    {
        pc ??= new Pc();
        return new RadeonProfileService(driver, _folder, () => pc.Amd, () => pc.Playing, () => pc.AmdSoftware);
    }

    [Fact]
    public void ProfileWritesInTheDriversOrderAndOnlyWhatItHas()
    {
        var writes = GpuProfile.Radeon(FakeDriver.Current().Read()!, Screen.Name, 144, GpuProfile.AntiLagGames);
        var keys = writes.Select(write => write.Key).ToList();
        Assert.True(keys.IndexOf(Keys.Chill) < keys.IndexOf(Keys.AntiLag));
        Assert.True(keys.IndexOf(Keys.FrtcFps) < keys.IndexOf(Keys.Frtc));
        Assert.True(keys.IndexOf(Keys.Rsr) < keys.IndexOf(Keys.Sharpening));
        Assert.True(keys.IndexOf(Keys.EnhancedSync) < keys.IndexOf(Keys.VSync));
        Assert.DoesNotContain(Keys.Afmf, keys);
        var values = writes.ToDictionary();
        Assert.Equal(0, values[Keys.Chill]);
        Assert.Equal(0, values[Keys.Boost]);
        Assert.Equal(0, values[Keys.Rsr]);
        Assert.Equal(0, values[Keys.EnhancedSync]);
        Assert.Equal(0, values[Keys.Sharpening]);
        Assert.Equal(GpuProfile.VSyncGameDecides, values[Keys.VSync]);   // WFVR_OFF_UNLESS_APP_SPECIFIES
        Assert.Equal(0, values[Keys.AntiLag]);   // "games": off until a game starts
        Assert.Equal(1, values[Keys.FreeSync + "LG ULTRAGEAR"]);
        Assert.Equal(1, values[Keys.FreeSync + "DELL P2419H"]);
        Assert.Equal(GpuProfile.FrameCap(144), values[Keys.FrtcFps]);
        Assert.Equal(1, values[Keys.Frtc]);
        Assert.Equal(1, values[Keys.Fsr]);

        // An old driver with the base 3D settings only: nothing else is written.
        var old = new GpuDriverState("AMD Radeon RX 7800 XT", null, new Dictionary<string, int> { [Keys.Chill] = 1, [Keys.AntiLag] = 0 });
        Assert.Equal(new[] { Keys.Chill, Keys.AntiLag }, GpuProfile.Radeon(old, Screen.Name, 144, GpuProfile.AntiLagAlways).Select(write => write.Key));
        Assert.Equal(1, GpuProfile.Radeon(old, Screen.Name, 144, GpuProfile.AntiLagAlways).ToDictionary()[Keys.AntiLag]);
    }

    [Fact]
    public void FrameCapFollowsFreeSyncOnTheMainScreen()
    {
        var driver = FakeDriver.Current();
        // No FreeSync on the main screen (another screen has it): the target goes off, as No G-SYNC leaves the cap off.
        driver.Values.Remove(Keys.FreeSync + "LG ULTRAGEAR");
        var fixedRefresh = GpuProfile.Radeon(driver.Read()!, Screen.Name, 144, GpuProfile.AntiLagGames).ToDictionary();
        Assert.Equal(0, fixedRefresh[Keys.Frtc]);
        Assert.False(fixedRefresh.ContainsKey(Keys.FrtcFps));
        // Windows names no main screen, and one screen has FreeSync: it counts as the main screen.
        var only = GpuProfile.Radeon(driver.Read()!, null, 240, GpuProfile.AntiLagGames).ToDictionary();
        Assert.Equal(GpuProfile.FrameCap(240), only[Keys.FrtcFps]);
        // The driver's range wins: 30-300 here, so a 500 Hz cap is clamped.
        var range = new GpuDriverState("AMD Radeon RX 9070 XT", "26.10.1", new Dictionary<string, int> { [Keys.Frtc] = 0, [Keys.FrtcFps] = 60, [Keys.FreeSync + "X"] = 0 }, 30, 300);
        Assert.Equal(300, GpuProfile.Radeon(range, "X", 500, GpuProfile.AntiLagGames).ToDictionary()[Keys.FrtcFps]);
    }

    [Theory]
    [InlineData("AMD Radeon RX 9070 XT", "26.3.1", true)]
    [InlineData("AMD Radeon RX 7800 XT", null, true)]
    [InlineData("AMD Radeon PRO W7900", "26.6.2", true)]
    [InlineData("AMD Radeon RX 6800 XT", "26.9.2", false)]
    [InlineData("AMD Radeon RX 6800 XT", "26.10.1", true)]
    [InlineData("AMD Radeon RX 6600", "27.1.1", true)]
    [InlineData("AMD Radeon RX 6600", null, false)]
    [InlineData("AMD Radeon RX 5700 XT", "26.10.1", false)]
    [InlineData("AMD Radeon(TM) 780M Graphics", "26.10.1", false)]
    public void FsrUpgradeOnlyWhereTheDriverRunsIt(string gpu, string? software, bool on) =>
        Assert.Equal(on, GpuProfile.FsrUpgrade(gpu, software));

    [Fact]
    public void ApplyBacksUpFirstAndUndoPutsBackEveryValue()
    {
        var driver = FakeDriver.Current();
        var start = new Dictionary<string, int>(driver.Values);
        var radeon = Service(driver);
        Assert.Equal("ready", radeon.Read(Screen)!.Status);

        var applied = radeon.Apply(Screen, GpuProfile.AntiLagGames)!;
        Assert.True(applied.Ok);
        Assert.Equal("applied", applied.Status);
        Assert.True(applied.CanUndo);
        Assert.True(File.Exists(Path.Combine(_folder, "amd-profile.json")));

        // Re-apply with another Anti-Lag choice keeps the first backup.
        Assert.Equal("applied", radeon.Apply(Screen, GpuProfile.AntiLagAlways)!.Status);
        Assert.Equal(1, driver.Values[Keys.AntiLag]);

        var undone = radeon.Undo(Screen)!;
        Assert.True(undone.Ok);
        Assert.False(undone.CanUndo);
        Assert.Equal(start, driver.Values);
        Assert.False(File.Exists(Path.Combine(_folder, "amd-profile.json")));
        Assert.Equal("Nothing to undo.", radeon.Undo(Screen)!.Message);
    }

    [Fact]
    public void AWriteThatFailsPartWayKeepsTheBackup()
    {
        var driver = FakeDriver.Current();
        var start = new Dictionary<string, int>(driver.Values);
        driver.Refuses.Add(Keys.FrtcFps);
        var radeon = Service(driver);
        var applied = radeon.Apply(Screen, GpuProfile.AntiLagGames)!;
        Assert.False(applied.Ok);
        Assert.NotNull(applied.Message);
        Assert.True(applied.CanUndo);
        Assert.Equal(0, driver.Values[Keys.Chill]);   // written before the failure

        driver.Refuses.Clear();
        Assert.True(radeon.Undo(Screen)!.Ok);
        Assert.Equal(start, driver.Values);
    }

    [Fact]
    public void StatusComparesWithTheDriverAndNamesADriverUpdate()
    {
        var driver = FakeDriver.Current();
        var radeon = Service(driver);
        radeon.Apply(Screen, GpuProfile.AntiLagGames);
        driver.Values[Keys.Boost] = 1;
        var moved = radeon.Read(Screen)!;
        Assert.Equal("changed", moved.Status);
        Assert.False(moved.DriverUpdated);
        driver.Software = "26.11.1";
        Assert.True(radeon.Read(Screen)!.DriverUpdated);
    }

    [Fact]
    public void OlderAdlxWithoutTheNewerInterfacesStillApplies()
    {
        // IADLX3DSettingsServices3 (FSR override) and …1 (Fluid Motion Frames) missing: their keys never appear.
        var driver = FakeDriver.Current();
        driver.Values.Remove(Keys.Fsr);
        driver.Values.Remove(Keys.Afmf);
        var radeon = Service(driver);
        Assert.Equal("applied", radeon.Apply(Screen, GpuProfile.AntiLagGames)!.Status);
        Assert.DoesNotContain(driver.Written, write => write.Key is Keys.Fsr or Keys.Afmf);
        Assert.DoesNotContain("FSR", radeon.Read(Screen)!.Summary);

        Assert.True(radeon.SetAfmf("steam:1", true));
        Assert.False(radeon.Afmf("steam:1").Supported);
        driver.Written.Clear();
        radeon.GameLaunching("steam:1", "Game");
        Assert.DoesNotContain(driver.Written, write => write.Key == Keys.Afmf);
    }

    [Fact]
    public void FluidMotionFramesTurnsOnAtLaunchAndBackOnceNoGameRuns()
    {
        var driver = FakeDriver.Current();
        var pc = new Pc();
        var radeon = Service(driver, pc);
        Assert.True(radeon.SetAfmf("steam:1", true));
        Assert.True(radeon.Afmf("steam:1") is { Supported: true, On: true });
        Assert.False(radeon.Afmf("steam:2").On);

        pc.Playing = true;
        radeon.GameLaunching("steam:2", "Not opted in");
        Assert.Equal(0, driver.Values[Keys.Afmf]);
        radeon.GameLaunching("steam:1", "Opted in");
        Assert.Equal(1, driver.Values[Keys.Afmf]);

        // Another game still runs: nothing goes back yet.
        radeon.GameEnded();
        Assert.Equal(1, driver.Values[Keys.Afmf]);
        pc.Playing = false;
        radeon.GameEnded();
        Assert.Equal(0, driver.Values[Keys.Afmf]);

        // The player had it on globally: Exo neither switches nor puts back anything.
        driver.Values[Keys.Afmf] = 1;
        driver.Written.Clear();
        radeon.GameLaunching("steam:1", "Opted in");
        radeon.GameEnded();
        Assert.Empty(driver.Written);
        Assert.Equal(1, driver.Values[Keys.Afmf]);
    }

    [Fact]
    public void ALaunchSwitchIsPutBackAfterExoStopsMidGame()
    {
        var driver = FakeDriver.Current();
        var pc = new Pc { Playing = true };
        var radeon = Service(driver, pc);
        radeon.SetAfmf("steam:1", true);
        radeon.GameLaunching("steam:1", "Game");
        Assert.Equal(1, driver.Values[Keys.Afmf]);

        // Exo stopped mid-game; the next start (no game running) puts it back.
        pc.Playing = false;
        Service(driver, pc).GameEnded();
        Assert.Equal(0, driver.Values[Keys.Afmf]);
    }

    [Fact]
    public void AntiLagRunsOnlyWhileAGameRuns()
    {
        var driver = FakeDriver.Current();
        var pc = new Pc();
        var radeon = Service(driver, pc);
        radeon.Apply(Screen, GpuProfile.AntiLagGames);
        Assert.Equal(0, driver.Values[Keys.AntiLag]);

        pc.Playing = true;
        radeon.GameLaunching("steam:1", "Game");
        Assert.Equal(1, driver.Values[Keys.AntiLag]);
        // A running game's switch is not a change to the profile.
        Assert.Equal("applied", radeon.Read(Screen)!.Status);
        // Undo mid-game puts back the player's own Anti-Lag (on), not the launch's switch.
        Assert.True(radeon.Undo(Screen)!.Ok);
        pc.Playing = false;
        radeon.GameEnded();
        Assert.Equal(1, driver.Values[Keys.AntiLag]);

        // "Off": no launch switch at all.
        radeon.Apply(Screen, GpuProfile.AntiLagOff);
        driver.Written.Clear();
        radeon.GameLaunching("steam:1", "Game");
        Assert.Empty(driver.Written);
    }

    [Fact]
    public void NoRadeonOrNoAdlxMeansNoWrite()
    {
        var driver = FakeDriver.Current();
        var pc = new Pc(amd: false);
        var radeon = Service(driver, pc);
        radeon.SetAfmf("steam:1", true);
        radeon.GameLaunching("steam:1", "Game");
        Assert.Empty(driver.Written);
        Assert.False(radeon.Afmf("steam:1").Supported);

        driver.Down = true;
        pc.Amd = true;
        Assert.Null(radeon.Read(Screen));
        Assert.Null(radeon.Apply(Screen, GpuProfile.AntiLagGames));
        Assert.False(File.Exists(Path.Combine(_folder, "amd-profile.json")));
    }

    [Fact]
    public void SummarySaysWhatTheProfileDoes()
    {
        var writes = GpuProfile.Radeon(FakeDriver.Current().Read()!, Screen.Name, 144, GpuProfile.AntiLagGames);
        Assert.Equal("FreeSync on, frames capped at 138 fps, FSR 4 for FSR 3.1 games, Anti-Lag while you play, V-Sync as each game sets it; "
            + "Chill, Boost, Super Resolution, Image Sharpening and Enhanced Sync off",
            GpuProfile.RadeonSummary(writes, GpuProfile.AntiLagGames));
    }

    [Theory]
    [InlineData(0, "Always off")]
    [InlineData(1, "Off, unless application specifies")]
    [InlineData(2, "On, unless application specifies")]
    [InlineData(3, "Always on")]
    [InlineData(4, null)]
    public void VSyncModesReadAsAmdSoftwareNamesThem(int mode, string? words) => Assert.Equal(words, GpuProfile.VSyncMode(mode));
}
