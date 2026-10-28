using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the AMD or Intel chipset update could go wrong silently:
/// - it installs while a game runs, during a Discord call, on battery, while you're at the PC, with the setting off,
///   or where Windows would pop a permission prompt with nobody there;
/// - it runs an installer that isn't the maker's signed one, or a game started during the download doesn't stop it;
/// - versions compare as text (8.10 before 8.9, 10.1.9999 after 10.1.20658), so it reinstalls or skips;
/// - a failed install (AMD's installer exits 0 even then) reads as done, or is retried every idle minute;
/// - a restart Windows needs (or Intel's MSI asks for with 3010) isn't shown, or "Restart to finish" stays after the restart;
/// - the row loses the manual Update after a failure, or keeps it while Exo installs;
/// - an Intel PC gets AMD's row, or a Ryzen Intel's;
/// - Intel's page is misread: another version, the release notes or a zip instead of SetupChipset.exe, another download's SHA-256;
/// - Intel's install command loses its silence, asks Intel for a newer setup, or restarts the PC.
/// </summary>
public sealed class ChipsetUpdateTests : IDisposable
{
    private const string Old = "8.08.12.551";
    private const string New = "8.09.28.1124";
    private const string IntelOld = "10.1.19867.8574";
    private const string IntelNew = "10.1.20658.8883";
    private const string IntelUrl = "https://downloadmirror.intel.com/925343/SetupChipset.exe";
    private static readonly ChipsetUpdate.Package IntelPackage = new(IntelNew, IntelUrl, "f20b9f97e9a81e613d4d529aad5d3948f5e449998cd118c6def4037ca6536446");
    private static readonly DateTime Booted = DateTime.UtcNow.AddDays(-1);
    private readonly string _folder = Directory.CreateTempSubdirectory("exo-chipset-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private sealed class FakeHost : ChipsetUpdate.IHost
    {
        public string? Version = Old;
        public bool Game, Call, Battery, Asks, GameDuringDownload;
        public bool Signed = true;
        public string? InstallsTo = New;
        public string Marks = "";
        public string MarksAfterInstall = "";
        public int Exit;
        public readonly List<string> Calls = [];

        public string? Installed() => Version;
        public Task<ChipsetUpdate.Package?> ReadLatestAsync(CancellationToken ct) => Task.FromResult<ChipsetUpdate.Package?>(null);
        public bool GameRunning() => Game;
        public bool InCall() => Call;
        public bool OnBattery() => Battery;
        public bool WindowsAsks() => Asks;
        public string RestartMarks() => Marks;

        public Task DownloadAsync(ChipsetUpdate.Package package, string path, CancellationToken ct)
        {
            Calls.Add("download " + package.Url);
            File.WriteAllText(path, "installer");
            if (GameDuringDownload) Game = true;
            return Task.CompletedTask;
        }

        public bool Verified(string path, ChipsetUpdate.Package package)
        {
            Calls.Add($"verify {Path.GetFileName(path)} {package.Version}");
            return Signed;
        }

        public Task<int> InstallAsync(string path, CancellationToken ct)
        {
            Calls.Add("install");
            if (InstallsTo is not null) Version = InstallsTo;
            Marks = MarksAfterInstall;
            return Task.FromResult(Exit);
        }
    }

    private ChipsetUpdate Updater(FakeHost host, string vendor = "amd") => new(vendor, host, _folder, settleSeconds: 0);

    private static async Task<bool> TickAsync(ChipsetUpdate update, bool enabled = true, bool idle = true, string latest = New)
    {
        var started = update.Tick(enabled, idle, ChipsetUpdate.AmdPackage(latest), Booted);
        await update.Running;
        return started;
    }

    // ---- Gates ----

    [Fact]
    public async Task AllGatesOpenInstallsAmdsSignedPackageSilently()
    {
        var host = new FakeHost();
        var update = Updater(host);
        Assert.True(await TickAsync(update));
        Assert.Equal([$"download https://drivers.amd.com/drivers/amd_chipset_software_{New}.exe", $"verify amd_chipset_software_{New}.exe {New}", "install"], host.Calls);
        Assert.Null(update.Current(New, Booted)); // done: nothing to show
        Assert.Empty(Directory.GetFiles(_folder, "*.exe")); // the installer goes afterwards
    }

    public static TheoryData<string> ClosedGates => ["game", "call", "battery", "asks", "busy", "off", "uptodate"];

    [Theory]
    [MemberData(nameof(ClosedGates))]
    public async Task AnyClosedGateWaits(string gate)
    {
        var host = new FakeHost
        {
            Game = gate == "game", Call = gate == "call", Battery = gate == "battery", Asks = gate == "asks",
            Version = gate == "uptodate" ? New : Old,
        };
        var update = Updater(host);
        Assert.False(await TickAsync(update, enabled: gate != "off", idle: gate != "busy"));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void PressingUpdateSaysWhyItWaits()
    {
        var update = Updater(new FakeHost { Call = true });
        Assert.Equal("You're in a Discord call, so the update waits.", update.Start(Old, ChipsetUpdate.AmdPackage(New)));
        Assert.Equal("A game is running, so the update waits.", Updater(new FakeHost { Game = true }).Start(Old, ChipsetUpdate.AmdPackage(New)));
        Assert.Equal("The AMD chipset driver is up to date.", update.Start(New, ChipsetUpdate.AmdPackage(New)));
    }

    [Fact]
    public async Task PressingUpdateRunsEvenWhereWindowsAsks()
    {
        var host = new FakeHost { Asks = true };
        var update = Updater(host);
        Assert.Null(update.Start(Old, ChipsetUpdate.AmdPackage(New)));
        await update.Running;
        Assert.Contains("install", host.Calls);
    }

    [Fact]
    public async Task AnUnsignedDownloadNeverRuns()
    {
        var host = new FakeHost { Signed = false };
        var update = Updater(host);
        await TickAsync(update);
        Assert.DoesNotContain("install", host.Calls);
        var status = update.Current(New, Booted)!;
        Assert.Equal(ChipsetUpdate.Phase.Failed, status.Phase);
        Assert.Equal("The download isn't AMD's signed installer.", status.Reason);
    }

    [Fact]
    public async Task AGameStartedDuringTheDownloadStopsTheInstallWithoutFailing()
    {
        var host = new FakeHost { GameDuringDownload = true };
        var update = Updater(host);
        await TickAsync(update);
        Assert.DoesNotContain("install", host.Calls);
        Assert.Null(update.Current(New, Booted)); // tried again in the next idle window
        host.Game = host.GameDuringDownload = false;
        host.Calls.Clear();
        Assert.True(await TickAsync(update));
        Assert.Contains("install", host.Calls);
    }

    // ---- After the installer ----

    [Fact]
    public async Task ARestartWindowsMarkedShowsUntilThePcRestarts()
    {
        var host = new FakeHost { MarksAfterInstall = "amdgpio2.sys#False#False" };
        var update = Updater(host);
        await TickAsync(update);
        var status = update.Current(New, Booted)!;
        Assert.Equal(ChipsetUpdate.Phase.Restart, status.Phase);
        var item = Assert.Single(PcHealth.Decide(Ryzen(New, status)));
        Assert.Equal("Restart to finish the AMD chipset update", item.Detail);
        Assert.True(item.Applied); // no button: Exo never restarts the PC
        Assert.Null(item.Url);
        // Windows started again after it: done, and the row says so.
        Assert.Null(update.Current(New, DateTime.UtcNow.AddMinutes(1)));
        Assert.True(Assert.Single(PcHealth.Decide(Ryzen(New, null))).Current);
    }

    [Fact]
    public async Task ARestartAlreadyPendingIsNotOurs()
    {
        var host = new FakeHost { Marks = "windows-update", MarksAfterInstall = "windows-update" };
        var update = Updater(host);
        await TickAsync(update);
        Assert.Null(update.Current(New, Booted));
    }

    [Fact]
    public async Task AnInstallThatLeftTheOldVersionFailsOnceAndKeepsAmdsPage()
    {
        var host = new FakeHost { InstallsTo = null }; // AMD's installer exits 0 even when its MSI fails
        var update = Updater(host);
        await TickAsync(update);
        var status = update.Current(New, Booted)!;
        Assert.Equal(ChipsetUpdate.Phase.Failed, status.Phase);
        Assert.Equal($"AMD's installer finished, but the driver is still {Old}.", status.Reason);

        var item = Assert.Single(PcHealth.Decide(Ryzen(Old, status)));
        Assert.Equal($"{Old} installed · {New} available. AMD's installer finished, but the driver is still {Old}.", item.Detail);
        Assert.StartsWith("https://www.amd.com/", item.Url);

        // Not again by itself for the same version; a newer one is tried.
        host.Calls.Clear();
        Assert.False(await TickAsync(update));
        Assert.Empty(host.Calls);
        Assert.True(await TickAsync(update, latest: "8.10.01.100"));
        Assert.Contains("install", host.Calls);
    }

    [Theory]
    [InlineData(0, New, "", "", null)]
    [InlineData(0, "8.10.01.100", "", "", null)] // newer than asked counts (compared as numbers)
    [InlineData(1602, New, "", "", null)] // the version is what counts
    [InlineData(0, New, "", "x", "Restart")]
    [InlineData(0, Old, "", "x", "Restart")] // Windows finishes it at the restart
    [InlineData(1603, Old, "", "x", "Failed")]
    [InlineData(2, Old, "", "", "Failed")]
    [InlineData(0, null, "", "", "Failed")]
    public void OutcomeFollowsTheVersionAndWindowsRestartMarks(int exit, string? after, string before, string marks, string? phase) =>
        Assert.Equal(phase, ChipsetUpdate.Outcome("AMD", exit, after, New, before, marks, DateTime.UtcNow)?.Phase.ToString());

    [Fact]
    public void AFailureSaysTheInstallersCode() =>
        Assert.Equal("AMD's installer stopped (code 2).", ChipsetUpdate.Outcome("AMD", 2, Old, New, "", "", DateTime.UtcNow)!.Reason);

    // ---- The row ----

    private static PcHealth.Reading Ryzen(string installed, ChipsetUpdate.Status? status, bool auto = true) => new(
        [], null, "AMD Ryzen 5 5600X 6-Core Processor", null, installed, New, false, status, auto);

    private static PcHealth.Reading IntelPc(string? installed, ChipsetUpdate.Status? status, bool auto = true) => new(
        [], null, "Intel(R) Core(TM) i7-14700K", null, installed, IntelNew, false, status, auto);

    [Fact]
    public void TheRowSaysWhatHappensNext()
    {
        Assert.Equal($"{Old} installed · {New} installs while you're away", Assert.Single(PcHealth.Decide(Ryzen(Old, null))).Detail);
        var manual = Assert.Single(PcHealth.Decide(Ryzen(Old, null, auto: false)));
        Assert.Equal($"{Old} installed · {New} available", manual.Detail);
        Assert.Null(manual.Url); // Update installs it through Exo

        var installing = Assert.Single(PcHealth.Decide(Ryzen(Old, new(ChipsetUpdate.Phase.Installing, New, null, DateTime.UtcNow))));
        Assert.True(installing.Busy);
        Assert.StartsWith($"Updating to {New}", installing.Detail);
    }

    [Theory]
    [InlineData("AMD Ryzen 5 5600X 6-Core Processor", "amd")]
    [InlineData("AMD Ryzen Threadripper PRO 7995WX 96-Cores", "amd")]
    [InlineData("12th Gen Intel(R) Core(TM) i7-12700K", "intel")]
    [InlineData("Intel(R) Core(TM) Ultra 7 265K", "intel")]
    [InlineData("Intel(R) Core(TM) i7-8700 CPU @ 3.20GHz", "intel")]
    [InlineData("Snapdragon(R) X Elite - X1E78100 - Qualcomm(R) Oryon(TM) CPU", null)]
    [InlineData(null, null)]
    public void EachProcessorGetsItsMakersChipsetDriver(string? cpu, string? vendor) =>
        Assert.Equal(vendor, PcHealth.ChipsetVendor(cpu));

    [Fact]
    public void AnIntelPcGetsIntelsRowAndNeverAmds()
    {
        var item = Assert.Single(PcHealth.Decide(IntelPc(IntelOld, null)));
        Assert.Equal("Intel chipset driver", item.Label);
        Assert.Equal($"{IntelOld} installed · {IntelNew} installs while you're away", item.Detail);
        Assert.Equal($"{IntelNew} · newest", Assert.Single(PcHealth.Decide(IntelPc(IntelNew, null))).Detail);
        Assert.Equal("Restart to finish the Intel chipset update",
            Assert.Single(PcHealth.Decide(IntelPc(IntelOld, new(ChipsetUpdate.Phase.Restart, IntelNew, null, DateTime.UtcNow)))).Detail);
        Assert.Equal($"Updating to {IntelNew}. Devices may reset for a moment",
            Assert.Single(PcHealth.Decide(IntelPc(IntelOld, new(ChipsetUpdate.Phase.Installing, IntelNew, null, DateTime.UtcNow)))).Detail);
        var failed = Assert.Single(PcHealth.Decide(IntelPc(IntelOld, new(ChipsetUpdate.Phase.Failed, IntelNew, "Another install was running, so nothing changed.", DateTime.UtcNow))));
        Assert.Equal(ChipsetUpdate.IntelPage, failed.Url);
        // Intel's package isn't installed (its INFs came from Windows Update or the PC's maker): no row, nothing installed.
        Assert.Empty(PcHealth.Decide(IntelPc(null, null)));
    }

    // ---- Intel's page ----

    [Fact]
    public void ReadsIntelsChipsetPage()
    {
        var package = GpuDriverUpdate.IntelChipset(GpuDriverUpdateTests.Page("intel-chipset-19347"));
        Assert.Equal(IntelPackage, package);
    }

    [Fact]
    public void TakesSetupChipsetAndItsOwnChecksum()
    {
        const string zipSha = "1111111111111111111111111111111111111111111111111111111111111111";
        const string exeSha = "2222222222222222222222222222222222222222222222222222222222222222";
        var page = $"""
            <meta name="DownloadVersion" content="{IntelNew}"/>
            <a href="https://downloadmirror.intel.com/925343/Release_notes_{IntelNew}-Public-Client.pdf">Release Notes</a>
            <button data-href="https://downloadmirror.intel.com/925343/Chipset_{IntelNew}_Public_MUP.zip"></button><li>SHA256: {zipSha}</li>
            <button data-href="https://downloadmirror.intel.com/925343/SetupChipset.exe"></button><li>Size: 98.1 MB</li><li>SHA256: {exeSha.ToUpperInvariant()}</li>
            """;
        Assert.Equal(new ChipsetUpdate.Package(IntelNew, IntelUrl, exeSha), GpuDriverUpdate.IntelChipset(page));
        // Without its own checksum, none: never the zip's.
        var bare = page.Replace($"<li>SHA256: {exeSha.ToUpperInvariant()}</li>", "");
        Assert.Null(GpuDriverUpdate.IntelChipset(bare)!.Sha256);
    }

    [Theory]
    [InlineData("<html>Access denied</html>")]
    [InlineData("""<meta name="DownloadVersion" content="10.1.20658.8883"/><a href="https://downloadmirror.intel.com/925343/Release_notes.pdf">""")] // no installer
    [InlineData("""<button data-href="https://downloadmirror.intel.com/925343/SetupChipset.exe">""")] // no version
    [InlineData("""<meta name="DownloadVersion" content="10.1.20658.8883"/><a href="https://example.com/925343/SetupChipset.exe">""")] // not Intel's server
    public void APageWithoutIntelsInstallerNamesNothing(string page) => Assert.Null(GpuDriverUpdate.IntelChipset(page));

    [Theory]
    [InlineData(IntelOld, IntelNew, true)]
    [InlineData(IntelNew, IntelNew, false)]
    [InlineData("10.1.9999.1", IntelNew, true)] // compared as numbers, not text
    [InlineData("10.1.20658.8883", "10.1.20440.8804", false)] // never back to an older one
    [InlineData(null, IntelNew, false)] // not installed: left so
    [InlineData(IntelOld, null, false)] // Intel's page couldn't be read
    public void ComparesIntelVersionsAsNumbers(string? installed, string? latest, bool behind) =>
        Assert.Equal(behind, PcHealth.ChipsetBehind(installed, latest));

    [Theory]
    [InlineData("SetupChipset", "Intel Corporation", true)] // the Advanced Installer setup's MSI
    [InlineData("Intel(R) Chipset Device Software", "Intel Corporation", true)] // Intel's older setup
    [InlineData("Intel® Chipset Device Software", "Intel(R) Corporation", true)]
    [InlineData("Intel(R) Management Engine Components", "Intel Corporation", false)]
    [InlineData("SetupChipset", "Someone else", false)]
    [InlineData("AMD Chipset Software", "Advanced Micro Devices, Inc.", false)]
    [InlineData(null, "Intel Corporation", false)]
    public void FindsIntelsUninstallEntry(string? name, string? publisher, bool intel) =>
        Assert.Equal(intel, ChipsetUpdate.IntelChipsetEntry(name, publisher));

    // ---- Intel's install ----

    [Fact]
    public void IntelsInstallIsSilentAndNeverRestarts()
    {
        var arguments = ChipsetUpdate.Arguments("intel").Split(' ');
        Assert.Equal(["/exenoui", "/exenoupdates", "/qn", "/norestart"], arguments); // Intel's readme: SetupChipset.exe /exenoui /qn /norestart
        Assert.DoesNotContain(arguments, a => a.Contains("restart", StringComparison.OrdinalIgnoreCase) && a != "/norestart");
        Assert.Equal("/S", ChipsetUpdate.Arguments("amd"));
    }

    [Fact]
    public async Task IntelsPackageComesFromItsPageAndSaysIntel()
    {
        var host = new FakeHost { Version = IntelOld, InstallsTo = IntelNew };
        var update = Updater(host, "intel");
        Assert.True(update.Tick(true, idle: true, IntelPackage, Booted));
        await update.Running;
        Assert.Equal([$"download {IntelUrl}", $"verify SetupChipset.exe {IntelNew}", "install"], host.Calls);
        Assert.Null(update.Current(IntelNew, Booted));
        Assert.Equal("The Intel chipset driver is up to date.", update.Start(IntelNew, IntelPackage));

        var unsigned = Updater(new FakeHost { Version = IntelOld, Signed = false }, "intel");
        Assert.Null(unsigned.Start(IntelOld, IntelPackage));
        await unsigned.Running;
        Assert.Equal("The download isn't Intel's signed installer.", unsigned.Current(IntelNew, Booted)!.Reason);
    }

    [Theory]
    [InlineData(0, IntelNew, null, null)]
    [InlineData(3010, IntelNew, "Restart", null)] // Windows Installer: installed, restart to finish
    [InlineData(3010, IntelOld, "Restart", null)] // the version may only show after it
    [InlineData(1641, IntelNew, "Restart", null)]
    [InlineData(1618, IntelOld, "Failed", "Another install was running, so nothing changed.")]
    [InlineData(1602, IntelOld, "Failed", "The install was canceled, so nothing changed.")]
    [InlineData(1603, IntelOld, "Failed", "Intel's installer stopped (code 1603).")]
    [InlineData(0, IntelOld, "Failed", $"Intel's installer finished, but the driver is still {IntelOld}.")]
    public void IntelsExitCodeMeansWhatWindowsInstallerSays(int exit, string after, string? phase, string? reason)
    {
        var status = ChipsetUpdate.Outcome("Intel", exit, after, IntelNew, "", "", DateTime.UtcNow);
        Assert.Equal(phase, status?.Phase.ToString());
        Assert.Equal(reason, status?.Reason);
    }
}
