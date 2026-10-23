using System.ComponentModel;
using System.Diagnostics;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using ExoLauncher.Helpers;
using Microsoft.Win32;

namespace ExoLauncher.Services;

/// <summary>
/// Keeps this PC's chipset driver up to date by itself (Settings > Downloads > Keep the AMD (or Intel) chipset driver up
/// to date, on by default), as Exo updates games while you're away: AMD Chipset Software on a Ryzen, Intel Chipset
/// Device Software on an Intel processor, each only where its maker's package is installed. When the maker's page names
/// a newer package, the PC has been idle for ten minutes, no game runs, there's no Discord call and the PC isn't on
/// battery, and Windows won't ask for permission, Exo downloads the maker's installer, checks the maker's signature and
/// its version (and the SHA-256 Intel's page lists), and runs it with its own silent switches (<see cref="Arguments"/>).
/// A chipset install can reset devices for a moment, hence the gates. Update in System > This PC runs the same install
/// now (Windows may ask then). Afterwards the installed version is read again: a restart Windows needs shows as "Restart
/// to finish" (Exo never restarts the PC); a failure keeps the row with the maker's page and says why, and that version
/// isn't tried again by itself.
///
/// AMD: www.amd.com's chipset page names the newest package (<see cref="PcHealth.LatestChipset"/>); drivers.amd.com
/// serves it only with www.amd.com as the referring page. Intel: download 19347 in Intel's download center
/// (<see cref="GpuDriverUpdate.IntelChipset"/>), from downloadmirror.intel.com. Intel's package holds only INF files that
/// name chipset devices (Intel's readme says updating it isn't needed), so Exo keeps an installed one current and never
/// installs it where it isn't. Intel Management Engine is never installed: Intel says to get it from the PC's maker.
/// Built without an Intel PC: Intel's installer was read (its MSI's tables and readme), never run.
/// </summary>
/// <param name="vendor">amd or intel.</param>
/// <param name="settleSeconds">How long the version may take to show after the installer exits.</param>
internal sealed class ChipsetUpdate(string vendor, ChipsetUpdate.IHost host, string folder, int settleSeconds = 15)
{
    internal const long MaxBytes = 250L << 20;
    internal const long MinBytes = 20L << 20; // AMD's 8.09.28.1124 is 78 MB, Intel's 10.1.20658.8883 98 MB; anything this small is neither
    internal static readonly TimeSpan IdleBefore = TimeSpan.FromMinutes(10);

    /// <summary>AMD's chipset download page. One package covers every AM4, AM5 and Threadripper board, so one page names the newest for all.</summary>
    internal const string AmdPage = "https://www.amd.com/en/support/downloads/drivers.html/chipsets/am5/x870e.html";

    /// <summary>Intel Chipset Device Software in Intel's download center: one package for every Intel chipset.</summary>
    internal const string IntelPage = "https://www.intel.com/content/www/us/en/download/19347/chipset-inf-utility.html";

    /// <summary>Windows Installer's "installed, restart to finish" (3010) and "restart started" (1641, never with /norestart).</summary>
    private const int RestartNeeded = 3010, RestartStarted = 1641;

    /// <summary>What Exo is doing to this PC, without the reads: each is a fake in the tests.</summary>
    internal interface IHost
    {
        string? Installed();
        /// <summary>The maker's page read now: the newest package it names, or null when it names none.</summary>
        Task<Package?> ReadLatestAsync(CancellationToken ct);
        bool GameRunning();
        bool InCall();
        bool OnBattery();
        /// <summary>Windows would show a permission prompt for an elevated install (Exo isn't elevated and UAC asks).</summary>
        bool WindowsAsks();
        /// <summary>Windows' own marks of a restart it waits for (pending file renames, servicing), as one comparable string.</summary>
        string RestartMarks();
        Task DownloadAsync(Package package, string path, CancellationToken ct);
        /// <summary>Signed by the maker, its own version is the package's, and its SHA-256 the one the page lists (if any).</summary>
        bool Verified(string path, Package package);
        /// <summary>Runs the installer elevated and silent; its exit code.</summary>
        Task<int> InstallAsync(string path, CancellationToken ct);
    }

    /// <summary>The newest package a maker's page names: its version, its installer and the SHA-256 the page lists beside it.</summary>
    internal sealed record Package(string Version, string Url, string? Sha256 = null);

    internal enum Phase { Installing, Restart, Failed }

    internal sealed record Status(Phase Phase, string Version, string? Reason, DateTime At);

    internal static ChipsetUpdate Amd { get; } = new("amd", new NativeHost("amd"), Path.Combine(PathHelper.AppDataDir, "amd-chipset"));
    internal static ChipsetUpdate Intel { get; } = new("intel", new NativeHost("intel"), Path.Combine(PathHelper.AppDataDir, "intel-chipset"));

    /// <summary>The processor's: AMD's on a Ryzen, Intel's on an Intel processor, else null (<see cref="PcHealth.ChipsetVendor"/>).</summary>
    internal static ChipsetUpdate? For(string? cpu) => PcHealth.ChipsetVendor(cpu) switch { "amd" => Amd, "intel" => Intel, _ => null };

    /// <summary>This PC's (<see cref="For"/>).</summary>
    internal static ChipsetUpdate? ThisPc => For(PcHealth.CpuName());

    /// <summary>The package AMD's page names by its version: AMD's installer is named after it.</summary>
    internal static Package AmdPackage(string version) => new(version, $"https://drivers.amd.com/drivers/amd_chipset_software_{version}.exe");

    /// <summary>
    /// The installer's silent switches, none of which restarts the PC. AMD's /S (from the installer's help: it then runs
    /// AMD's MSI with EXPRESSINSTALLSILENT). Intel's SetupChipset.exe is an Advanced Installer bootstrapper over an MSI
    /// (Intel's readme): /exenoui without its window, /exenoupdates without asking Intel for a newer setup, then
    /// msiexec's /qn (no UI) and /norestart, Intel's own silent example.
    /// </summary>
    internal static string Arguments(string vendor) => vendor == "amd" ? "/S" : "/exenoui /exenoupdates /qn /norestart";

    internal string Vendor => vendor;
    internal string Maker => GpuDriverUpdate.Maker(vendor);

    private readonly Lock _gate = new();
    private Status? _status;
    private bool _loaded;
    private Task? _running;
    private Task<Package?>? _fetch;
    private SavedLatest? _latest;
    private bool _latestLoaded;

    private sealed record SavedLatest(DateTime At, Package? Latest);

    private string StatePath => Path.Combine(folder, "update.json");
    private string LatestPath => folder + ".json";

    internal string? Installed() => host.Installed();

    /// <summary>Windows would ask for permission, so the update isn't automatic (Update in This PC still runs it).</summary>
    internal bool WindowsAsks() => host.WindowsAsks();

    /// <summary>Why it can't install right now, or null.</summary>
    internal static string? Blocked(IHost host) =>
        host.GameRunning() ? "A game is running" : host.InCall() ? "You're in a Discord call" : host.OnBattery() ? "Your PC is on battery" : null;

    /// <summary>
    /// After the installer: the version it left, its exit code and whether Windows marked a restart it hadn't before.
    /// AMD's installer exits 0 even when AMD's MSI fails, so the version decides; Intel's exits with its MSI's code
    /// (Windows Installer's: 3010 installed with a restart to come). Null: done, nothing to show.
    /// </summary>
    internal static Status? Outcome(string maker, int exitCode, string? after, string latest, string marksBefore, string marksAfter, DateTime at)
    {
        var restart = exitCode is RestartNeeded or RestartStarted || (marksAfter.Length > 0 && marksAfter != marksBefore);
        var updated = Version.TryParse(after, out var have) && Version.TryParse(latest, out var want) && have >= want;
        if (updated || ((exitCode is 0 or RestartNeeded or RestartStarted) && restart)) return restart ? new Status(Phase.Restart, latest, null, at) : null;
        return new Status(Phase.Failed, latest, exitCode == 0 ? $"{maker}'s installer finished, but the driver is still {after ?? "not installed"}." : Stopped(maker, exitCode), at);
    }

    /// <summary>Why the installer stopped, as plainly as its exit code allows (Windows Installer's codes: both makers' installers run an MSI).</summary>
    internal static string Stopped(string maker, int exitCode) => exitCode switch
    {
        1602 => "The install was canceled, so nothing changed.",
        1618 => "Another install was running, so nothing changed.",
        _ => $"{maker}'s installer stopped (code {exitCode}).",
    };

    /// <summary>
    /// What to show now. A restart counts as done once Windows started after it was asked for; a failure only
    /// for the version that failed (a newer one is tried again).
    /// </summary>
    internal Status? Current(string? latest, DateTime bootedAt)
    {
        lock (_gate)
        {
            Load();
            if (_running is { IsCompleted: false } && _status is { Phase: Phase.Installing }) return _status;
            if (_status is { Phase: Phase.Restart } restart && restart.At < bootedAt) Save(null);
            if (_status is { Phase: Phase.Failed } failed && failed.Version != latest) return null;
            return _status is { Phase: Phase.Installing } ? null : _status;
        }
    }

    /// <summary>The idle tick: installs <paramref name="latest"/> when every gate is open. True when it started.</summary>
    internal bool Tick(bool enabled, TimeSpan idle, Package? latest, DateTime bootedAt)
    {
        if (!enabled || idle < IdleBefore || latest is null) return false;
        var installed = host.Installed();
        if (!PcHealth.ChipsetBehind(installed, latest.Version) || Current(latest.Version, bootedAt) is not null) return false;
        if (host.WindowsAsks() || Blocked(host) is not null) return false;
        return Start(installed, latest) is null;
    }

    /// <summary>Update pressed, or the idle tick: null when the install started, else the line to show.</summary>
    internal string? Start(string? installed, Package latest)
    {
        if (!PcHealth.ChipsetBehind(installed, latest.Version)) return $"The {Maker} chipset driver is up to date.";
        if (Blocked(host) is { } blocked) return blocked + ", so the update waits.";
        lock (_gate)
        {
            if (_running is { IsCompleted: false }) return null;
            Load();
            Save(new Status(Phase.Installing, latest.Version, null, DateTime.UtcNow));
            _running = Task.Run(() => RunAsync(installed, latest, CancellationToken.None));
        }
        return null;
    }

    /// <summary>The install that's running, or a finished task.</summary>
    internal Task Running
    {
        get { lock (_gate) return _running ?? Task.CompletedTask; }
    }

    /// <summary>The whole install, each step in app.log. Ends in done, Restart or Failed; never throws.</summary>
    internal async Task RunAsync(string? installed, Package latest, CancellationToken ct)
    {
        var log = Maker + " chipset: ";
        var package = Path.Combine(folder, Path.GetFileName(new Uri(latest.Url).LocalPath));
        Status? end;
        try
        {
            Directory.CreateDirectory(folder);
            AppLog.Info($"{log}{installed} installed, downloading {latest.Version} from {latest.Url}.");
            await host.DownloadAsync(latest, package, ct).ConfigureAwait(false);
            if (!host.Verified(package, latest)) throw new InvalidDataException($"The download isn't {Maker}'s signed installer.");
            AppLog.Info($"{log}{Path.GetFileName(package)} ({new FileInfo(package).Length / 1048576} MB) is signed by {Maker}.");
            // A game or call may have started during the download.
            if (Blocked(host) is { } blocked) throw new InvalidOperationException(blocked + ", so the update waits.");
            var before = host.RestartMarks();
            AppLog.Info($"{log}installing silently ({Arguments(vendor)}).");
            var exit = await host.InstallAsync(package, ct).ConfigureAwait(false);
            var after = host.Installed();
            // Windows records the new version as the MSI finishes; give it a few seconds.
            for (var waited = 0; waited < settleSeconds && !Updated(after, latest.Version); waited++)
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);
                after = host.Installed();
            }
            end = Outcome(Maker, exit, after, latest.Version, before, host.RestartMarks(), DateTime.UtcNow);
            AppLog.Info($"{log}installer exited {exit}; driver now {after ?? "unknown"}; {end?.Phase.ToString().ToLowerInvariant() ?? "done"}{(end?.Reason is { } why ? ": " + why : "")}");
        }
        catch (InvalidOperationException ex)
        {
            // A game or call came up: not a failure, the next idle window tries again.
            AppLog.Info(log + ex.Message);
            lock (_gate) Save(null);
            return;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            end = new Status(Phase.Failed, latest.Version, "Windows didn't get permission, so nothing changed.", DateTime.UtcNow);
            AppLog.Info(log + "permission refused.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException or IOException or UnauthorizedAccessException or InvalidDataException or Win32Exception)
        {
            end = new Status(Phase.Failed, latest.Version, ex is InvalidDataException ? ex.Message : "The update stopped. Your driver is unchanged.", DateTime.UtcNow);
            AppLog.Warn(log + "update failed: " + ex.Message);
        }
        finally
        {
            try { File.Delete(package); File.Delete(package + ".part"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Debug(log + "the installer wasn't removed: " + ex.Message); }
        }
        lock (_gate) Save(end);
    }

    private static bool Updated(string? installed, string latest) => !PcHealth.ChipsetBehind(installed, latest) && installed is not null;

    private void Load()
    {
        if (_loaded) return;
        _loaded = true;
        try { _status = File.Exists(StatePath) ? JsonSerializer.Deserialize<Status>(File.ReadAllText(StatePath)) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { _status = null; }
    }

    private void Save(Status? status)
    {
        _status = status;
        try
        {
            if (status is null) { File.Delete(StatePath); return; }
            Directory.CreateDirectory(folder);
            var temp = StatePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(status));
            File.Move(temp, StatePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Debug($"{Maker} chipset: the update state wasn't saved: " + ex.Message); }
    }

    // ---- The newest package: the maker's page read at most every 12 hours, kept across runs ----

    internal Task<Package?> LatestAsync()
    {
        lock (_gate)
        {
            if (!_latestLoaded)
            {
                _latestLoaded = true;
                try { _latest = File.Exists(LatestPath) ? JsonSerializer.Deserialize<SavedLatest>(File.ReadAllText(LatestPath)) : null; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { _latest = null; }
            }
            if (_latest is { } saved && DateTime.UtcNow - saved.At < TimeSpan.FromHours(12)) return Task.FromResult(saved.Latest);
            return _fetch ??= Task.Run(() => FetchLatestAsync(_latest?.Latest));
        }
    }

    /// <summary>Reads the maker's page once for everyone waiting. A failure keeps the last answer and waits the same 12 hours to try again.</summary>
    private async Task<Package?> FetchLatestAsync(Package? previous)
    {
        var latest = previous;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var found = await host.ReadLatestAsync(timeout.Token).ConfigureAwait(false);
            if (found is null) AppLog.Info($"{Maker} chipset: {Maker}'s page names no driver version.");
            latest = found ?? previous;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            AppLog.Info($"{Maker} chipset: {Maker}'s page couldn't be read: {ex.Message}");
        }
        lock (_gate)
        {
            _latest = new SavedLatest(DateTime.UtcNow, latest);
            _fetch = null;
            try
            {
                var temp = LatestPath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(_latest));
                File.Move(temp, LatestPath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Debug($"{Maker} chipset: the newest version wasn't saved: " + ex.Message); }
        }
        return latest;
    }

    /// <summary>When Windows last started (UTC).</summary>
    internal static DateTime BootedAt() => DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);

    /// <summary>
    /// An uninstall entry that is Intel Chipset Device Software: "Intel(R) Chipset Device Software" (Intel's older installer)
    /// or "SetupChipset" (its MSI's product name since the Advanced Installer setup), published by Intel.
    /// </summary>
    internal static bool IntelChipsetEntry(string? name, string? publisher) =>
        publisher?.Contains("Intel", StringComparison.OrdinalIgnoreCase) == true && name is { } n
        && (n.Contains("Chipset Device Software", StringComparison.OrdinalIgnoreCase) || n.Trim().Equals("SetupChipset", StringComparison.OrdinalIgnoreCase));

    private sealed class NativeHost(string vendor) : IHost
    {
        private static readonly HttpClient Http = ExoHttp.Create(TimeSpan.FromMinutes(10));

        public string? Installed() => vendor == "amd" ? AmdInstalled() : IntelInstalled();

        public async Task<Package?> ReadLatestAsync(CancellationToken ct)
        {
            if (vendor != "amd") return GpuDriverUpdate.IntelChipset(await GpuDriverUpdate.GetTextAsync(vendor, IntelPage, ct).ConfigureAwait(false));
            return PcHealth.LatestChipset(await GpuDriverUpdate.GetTextAsync(vendor, AmdPage, ct).ConfigureAwait(false)) is { } version ? AmdPackage(version) : null;
        }

        public bool GameRunning() => App.Services.Launcher.AnyGameRunning || MainWindow.RunningLibraryGames().Count > 0;
        public bool InCall() => DiscordClient.DiscordSession.Current?.InCall == true;
        public bool OnBattery() => new PcHealth.NativePower().Battery();

        public bool WindowsAsks()
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                // With UAC on, the Administrators role holds only in an elevated process.
                if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return false;
                using var policy = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System");
                if (policy?.GetValue("EnableLUA") is 0) return false;
                // An administrator whose elevation Windows grants without asking (its token holds Administrators deny-only until then).
                var admin = identity.Claims.Any(c => c.Type is ClaimTypes.DenyOnlySid or ClaimTypes.GroupSid && c.Value == "S-1-5-32-544");
                return !(admin && policy?.GetValue("ConsentPromptBehaviorAdmin") is 0);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return true; }
        }

        public string RestartMarks()
        {
            try
            {
                using var session = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager");
                var renames = session?.GetValue("PendingFileRenameOperations") is string[] files ? string.Join("|", files) : "";
                using var servicing = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
                using var update = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
                return renames.Length == 0 && servicing is null && update is null ? "" : $"{renames}#{servicing is not null}#{update is not null}";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return ""; }
        }

        public async Task DownloadAsync(Package package, string path, CancellationToken ct)
        {
            var maker = GpuDriverUpdate.Maker(vendor);
            var partial = path + ".part";
            // AMD's server redirects to a "download incomplete" page without www.amd.com as the referring page; Intel's turns browser-style agents away.
            using var request = GpuDriverUpdate.Request(vendor, package.Url);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri?.Host != GpuDriverUpdate.DownloadHost(vendor)) throw new InvalidDataException($"The download didn't come from {maker}.");
            if (response.Content.Headers.ContentLength is { } length && length is > MaxBytes or < MinBytes)
                throw new InvalidDataException($"{maker}'s download is {length / 1048576} MB, not the size of its chipset package.");
            await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
            {
                var buffer = new byte[1 << 20];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    done += read;
                    if (done > MaxBytes) throw new InvalidDataException($"{maker}'s download is larger than its chipset package.");
                    await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
                if (response.Content.Headers.ContentLength is { } expected && done != expected) throw new InvalidDataException("The download was cut short.");
                if (done < MinBytes) throw new InvalidDataException($"{maker}'s download is too small to be its chipset package.");
            }
            File.Move(partial, path, true);
        }

        public bool Verified(string path, Package package)
        {
            if (!AuthenticodeVerifier.HasPublisher(path, vendor == "amd" ? GpuDriverUpdate.AmdPublisher : GpuDriverUpdate.IntelPublisher)) return false;
            if (!Version.TryParse(FileVersionInfo.GetVersionInfo(path).FileVersion, out var own) || !Version.TryParse(package.Version, out var want) || own != want) return false;
            if (package.Sha256 is null) return true;
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).Equals(package.Sha256, StringComparison.OrdinalIgnoreCase);
        }

        public async Task<int> InstallAsync(string path, CancellationToken ct)
        {
            using var install = Process.Start(new ProcessStartInfo(path, Arguments(vendor)) { UseShellExecute = true, Verb = "runas", WorkingDirectory = Path.GetDirectoryName(path)! })
                ?? throw new Win32Exception($"{GpuDriverUpdate.Maker(vendor)}'s installer did not start.");
            await install.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromMinutes(30), ct).ConfigureAwait(false);
            return install.ExitCode;
        }

        /// <summary>AMD Chipset Software's version from its uninstall entry (its installer is 32-bit, so it sits in the 32-bit view).</summary>
        private static string? AmdInstalled()
        {
            foreach (var view in (RegistryView[])[RegistryView.Registry32, RegistryView.Registry64])
            {
                try
                {
                    using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                    using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\AMD_Chipset_IODrivers");
                    if (key?.GetValue("DisplayVersion") is string version && !string.IsNullOrWhiteSpace(version)) return version.Trim();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { /* the other view */ }
            }
            return null;
        }

        /// <summary>
        /// Intel Chipset Device Software's package version where Intel's installer records it: SOFTWARE\Intel\InfInst's
        /// Version, the key Intel's readme names (its MSI writes it in the 64-bit view), else its uninstall entry. Device
        /// Manager's INF versions are each chipset part's own, not the package's (the same readme), and an INF that came from
        /// Windows Update or the PC's maker leaves no record: then Intel's package isn't installed and Exo leaves it so.
        /// </summary>
        private static string? IntelInstalled()
        {
            string? found = null;
            void Keep(object? value)
            {
                if (value is string text && Version.TryParse(text.Trim(), out var version) && (found is null || version > Version.Parse(found))) found = text.Trim();
            }
            foreach (var view in (RegistryView[])[RegistryView.Registry64, RegistryView.Registry32])
            {
                try
                {
                    using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                    using var inf = root.OpenSubKey(@"SOFTWARE\Intel\InfInst");
                    Keep(inf?.GetValue("Version"));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { /* the other view */ }
            }
            if (found is not null) return found;
            foreach (var view in (RegistryView[])[RegistryView.Registry64, RegistryView.Registry32])
            {
                try
                {
                    using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                    using var uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                    if (uninstall is null) continue;
                    foreach (var name in uninstall.GetSubKeyNames())
                    {
                        using var entry = uninstall.OpenSubKey(name);
                        if (IntelChipsetEntry(entry?.GetValue("DisplayName") as string, entry?.GetValue("Publisher") as string)) Keep(entry?.GetValue("DisplayVersion"));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { /* the other view */ }
            }
            return found;
        }
    }
}
