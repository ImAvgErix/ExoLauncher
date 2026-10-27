using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using ExoLauncher.Helpers;
using Microsoft.Win32;

namespace ExoLauncher.Services;

/// <summary>
/// System > Graphics > Driver and profile's driver row on a Radeon or an Intel GPU, as <see cref="NvidiaDriverService"/> is
/// on a GeForce. Built without either GPU to try it on: the pages, packages and switches below were read from AMD's and
/// Intel's own files (October 22, 2026), and each install step is checked in code and tests only.
///
/// 1. Check: neither vendor has a version feed, so Exo reads their download pages, as <see cref="PcHealth"/> reads AMD's
///    chipset page. AMD: the driver selector's product list names the card's page (an integrated GPU's is its
///    processor's), whose Windows 11 section links the newest AMD Software: Adrenalin Edition. Intel: an Arc is on Intel's
///    Arc driver page; any other GPU on the first of the Arc, 11th–14th Gen and 7th–10th Gen pages whose product list names
///    the card or the processor. Cached an hour.
/// 2. Update, after the user's consent: download from the vendor; check its signature (and on Intel the SHA-512 its page
///    lists); wait while a game runs or a Discord call is on (the driver swap crashes the game and its reload ends the
///    call); run the vendor's own silent install as administrator (Windows asks); read its result and the new version;
///    put Exo's GPU profile back. AMD's package is unpacked with Windows' tar and its Setup.exe runs (<see cref="AmdArguments"/>);
///    Intel's package runs itself (<see cref="IntelArguments"/>).
/// Never restarts the PC: a restart the driver needs is said on the row.
/// </summary>
internal static partial class GpuDriverUpdate
{
    /// <summary>
    /// Vendor is amd or intel. Kind is recommended or optional (AMD's own labels), or intel. Sha512 is what Intel's page lists,
    /// and Page is Intel's page, which shows Intel's license.
    /// </summary>
    internal sealed record Latest(string Vendor, string Version, string Url, long Size, string? Released, string Kind, string? Sha512 = null, string? Page = null);

    /// <summary>How an install ended: Ok, or not with Message saying why.</summary>
    internal sealed record Ending(bool Ok, string Message);

    internal const string AmdPublisher = "Advanced Micro Devices";
    internal const string IntelPublisher = "Intel Corporation";
    private const long MaxBytes = 4L << 30;

    private static readonly HttpClient Http = ExoHttp.Create(TimeSpan.FromMinutes(30), new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All });
    private static readonly Lock Gate = new();
    /// <summary>The newest driver per card, an hour (PRIVACY.md); everyone asking meanwhile shares one lookup.</summary>
    private static readonly Dictionary<string, (DateTime At, Latest? Value)> Cache = [];
    private static readonly Dictionary<string, Task<Latest?>> Fetching = [];
    private static NvidiaDriverService.Progress _progress = new("idle", 0, null);
    private static Task? _running;
    private static string? _vendor;

    /// <summary>Fires on every change of <see cref="Current"/>: the progress the NVIDIA row shows, on the same gpu.driver event.</summary>
    internal static event Action<NvidiaDriverService.Progress>? Changed;

    internal static NvidiaDriverService.Progress Current { get { lock (Gate) return _progress; } }

    private static void Report(string stage, int percent = 0, string? message = null)
    {
        NvidiaDriverService.Progress next;
        lock (Gate) next = _progress = new(stage, percent, message);
        Changed?.Invoke(next);
    }

    internal static string Maker(string vendor) => vendor == "amd" ? "AMD" : "Intel";

    /// <summary>The vendor's GPU the row is for, as Windows names it (<see cref="Pick"/>).</summary>
    internal static string? Card(string vendor) => Pick(vendor, GpuCapability.Adapters());

    /// <summary>
    /// With two of one vendor's GPUs (a Ryzen's Radeon Graphics beside a Radeon card, an Intel GPU beside an Arc card), the
    /// card: a Radeon RX or PRO, an Arc.
    /// </summary>
    internal static string? Pick(string vendor, IEnumerable<string> adapters) =>
        adapters.Where(name => GraphicsService.Vendors([name]).Contains(vendor))
            .OrderByDescending(name => vendor == "amd" ? DiscreteRadeon().IsMatch(name) : name.Contains("Arc", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();

    /// <summary>
    /// The installed driver as Windows records it for the card: AMD Software's version (RadeonSoftwareVersion "26.10.1",
    /// which AMD's display INF writes, so a driver from Windows Update has it too) or Intel's driver version
    /// (DriverVersion "32.0.101.9150", the version Intel's pages list). Read fresh: current the moment an install ends.
    /// </summary>
    internal static string? Installed(string vendor, string card)
    {
        var value = vendor == "amd" ? "RadeonSoftwareVersion" : "DriverVersion";
        var provider = vendor == "amd" ? "Advanced Micro Devices" : "Intel";
        string? fallback = null;
        try
        {
            using var display = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (display is null) return null;
            foreach (var name in display.GetSubKeyNames().Where(name => name.Length == 4 && name.All(char.IsAsciiDigit)))
            {
                using var adapter = display.OpenSubKey(name);
                if (adapter?.GetValue(value) is not string text || text.Trim().Length == 0) continue;
                if (adapter.GetValue("DriverDesc") is string desc && string.Equals(desc.Trim(), card, StringComparison.OrdinalIgnoreCase)) return text.Trim();
                if (adapter.GetValue("ProviderName") is string maker && maker.Contains(provider, StringComparison.OrdinalIgnoreCase)) fallback ??= text.Trim();
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        return fallback;
    }

    /// <summary>Whether <paramref name="latest"/> is newer than <paramref name="installed"/> by number ("26.10.1" after "26.9.2"); without an installed version, it is.</summary>
    internal static bool Newer(string latest, string? installed) =>
        Version.TryParse(latest, out var want) && (!Version.TryParse(installed, out var have) || want > have);

    /// <summary>The newest driver for this PC's card from its vendor's pages (cached an hour); null when there is none or it can't tell.</summary>
    /// <remarks>An answer older than the hour shows at once while the pages are read again; <paramref name="fresh"/> (an
    /// update) waits for them.</remarks>
    internal static async Task<Latest?> LatestAsync(string vendor, CancellationToken ct, bool fresh = false)
    {
        SweepUnpacked(vendor);
        if (Card(vendor) is not { } card) return null;
        var cpu = PcHealth.CpuName();
        var key = $"{vendor}|{card}|{cpu}";
        Task<Latest?>? task;
        lock (Gate)
        {
            var known = Cache.TryGetValue(key, out var cached);
            if (known && DateTime.UtcNow - cached.At < TimeSpan.FromHours(1)) return cached.Value;
            if (!Fetching.TryGetValue(key, out task))
                Fetching[key] = task = Task.Run(() => FetchAsync(vendor, card, cpu, key));
            if (known && !fresh) return cached.Value;
        }
        return await task.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Reads the pages (shared by everyone waiting on this key, so one caller leaving doesn't stop it) and keeps the answer an hour.</summary>
    private static async Task<Latest?> FetchAsync(string vendor, string card, string? cpu, string key)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        Latest? latest;
        try
        {
            Task<string> Get(string url) => GetTextAsync(vendor, url, timeout.Token);
            latest = vendor == "amd"
                ? await AmdLatestAsync(card, cpu, Get).ConfigureAwait(false)
                : await IntelLatestAsync(card, cpu, Get).ConfigureAwait(false);
            if (latest is null) AppLog.Info($"Driver: {Maker(vendor)}'s pages name no driver for {card}{(cpu is null ? "" : " with " + cpu)}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            AppLog.Info($"Driver: {Maker(vendor)}'s driver pages could not be read: {ex.Message}");
            lock (Gate) Fetching.Remove(key);
            return null;
        }
        lock (Gate)
        {
            Fetching.Remove(key);
            Cache[key] = (DateTime.UtcNow, latest);
        }
        return latest;
    }

    /// <summary>
    /// A request as each site lets one through. AMD's holds a request without a browser-style agent open until it times
    /// out (as the chipset page read found), and its download server wants www.amd.com as the referring page. Intel's
    /// turns browser-style and bare agents away and lets through one that names itself with a link, as crawlers do.
    /// </summary>
    internal static HttpRequestMessage Request(string vendor, string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (vendor == "amd")
        {
            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) ExoLauncher");
            request.Headers.Referrer = new Uri("https://www.amd.com/");
        }
        else request.Headers.TryAddWithoutValidation("User-Agent", "ExoLauncher/1.0 (+https://github.com/UhhErix/ExoLauncher)");
        return request;
    }

    internal static async Task<string> GetTextAsync(string vendor, string url, CancellationToken ct)
    {
        using var request = Request(vendor, url);
        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Why a display driver install waits, or null: a game runs (the driver swap would crash it) or a Discord call is on (the reload ends it).</summary>
    internal static string? Holding() =>
        GameRunning() ? "Waiting for your game to close"
        : DiscordClient.DiscordSession.Current?.InCall == true ? "Waiting for your Discord call to end"
        : null;

    /// <summary>Why a display driver update can't start now, or null. NVIDIA's row asks the same.</summary>
    internal static string? Busy() =>
        GameRunning() ? "Close your game first."
        : DiscordClient.DiscordSession.Current?.InCall == true ? "Leave your Discord call first."
        : Running("scskiller_warm") ? "Exo Shaders is compiling. Try again when it's done."
        : null;

    private static bool GameRunning() => App.Services.Launcher.AnyGameRunning || MainWindow.RunningLibraryGames().Count > 0;

    private static bool Running(string process)
    {
        var found = Process.GetProcessesByName(process);
        foreach (var item in found) item.Dispose();
        return found.Length > 0;
    }

    /// <summary>Returns once <paramref name="holding"/> says nothing, calling <paramref name="waiting"/> with each new reason it gives meanwhile.</summary>
    internal static async Task WaitQuietAsync(Func<string?> holding, Action<string> waiting, TimeSpan poll)
    {
        string? said = null;
        while (holding() is { } reason)
        {
            if (reason != said) waiting(said = reason);
            await Task.Delay(poll).ConfigureAwait(false);
        }
    }

    /// <summary>Why an update can't start now, or null.</summary>
    internal static string? Blocked() => _running is { IsCompleted: false } ? "A driver update is already running." : Busy();

    /// <summary>The vendor whose update is running, or null: mid-install, Windows may list no GPU of that vendor at all.</summary>
    internal static string? Updating { get { lock (Gate) return _running is { IsCompleted: false } ? _vendor : null; } }

    /// <summary>Starts the update (the user consented); progress comes through <see cref="Changed"/>.</summary>
    internal static string? Start(Latest latest)
    {
        if (Card(latest.Vendor) is not { } card) return "Exo can't find the graphics card.";
        lock (Gate)
        {
            if (Blocked() is { } why) return why;
            _vendor = latest.Vendor;
            _running = Task.Run(() => RunAsync(latest, card));
        }
        return null;
    }

    /// <summary>
    /// AMD's switches (the help in its Bin64\ATISetup.exe, and AMD's command-line installation guide): -INSTALL installs
    /// silently; -LOG writes the result as "[ResponseResult] ResultCode =N" (<see cref="AmdResult"/>); no -BOOT, so Exo
    /// never restarts the PC. The full install, not -DRIVERONLY: ADLX (amdadlx64.dll, Exo's Radeon page) comes with the
    /// display driver either way (its INF copies it to System32), but Fluid Motion Frames works only while AMD Software
    /// runs, and Exo's Radeon page turns it on per game. Whether -view:2 (AMD Software's minimal view) keeps it is unknown
    /// without a Radeon to try, so AMD's default install it is.
    /// </summary>
    internal static string AmdArguments(string log) => $"-INSTALL -LOG \"{log}\"";

    /// <summary>
    /// Intel's switches (installation_readme.txt in Intel's package): over whatever version is there (so Reinstall works),
    /// without the extras (Intel Graphics Software, Driver &amp; Support Assistant, GameAssist, oneAPI Level Zero and a data
    /// collection service), silent, -s last as Intel's examples have it. Exo's Intel page needs only ControlLib.dll, which
    /// the display driver's own INF copies to System32 (iigd_dch.inf, ControlApi_Gpu.Copy64_Ext). Not --fresh: it removes
    /// every older Intel driver, and an Intel GPU beside an Arc card runs on another of Intel's packages. No -b: Exo never
    /// restarts the PC. Intel's package is a WinRAR SFX whose setup hands its whole command line to Installer.exe (Windows'
    /// tar can't unpack it), and WinRAR's own -s keeps its window hidden too.
    /// </summary>
    internal const string IntelArguments = "--overwrite --noExtras -s";

    /// <summary>AMD's result from its install log: 0 done, 1 failed, 2 done with a restart to come; null when it wrote none.</summary>
    internal static int? AmdResult(string? log)
    {
        if (log is null) return null;
        var text = log.Replace("\0", "", StringComparison.Ordinal);
        var at = text.LastIndexOf("[ResponseResult]", StringComparison.OrdinalIgnoreCase);
        var match = AmdResultCode().Match(text, Math.Max(at, 0));
        return match.Success && int.TryParse(match.Groups[1].Value, out var code) ? code : null;
    }

    /// <summary>
    /// How an install ended, from the vendor's own answer and the version Windows now has: a driver at the new version is
    /// installed whatever the installer said; AMD's 2 and Intel's 2 and 14 (restart) or 17 and 18 (shut down) are done once
    /// the PC restarts (the new version may only show then); anything else is a stop, said as plainly as the code allows.
    /// Intel's codes are its readme's.
    /// </summary>
    internal static Ending Outcome(Latest latest, int exitCode, int? amdResult, string? installed)
    {
        var amd = latest.Vendor == "amd";
        var updated = Version.TryParse(installed, out var have) && Version.TryParse(latest.Version, out var want) && have >= want;
        var restart = amd ? amdResult == 2 : exitCode is 2 or 14;
        var shutdown = !amd && (exitCode is 17 or 18);
        var failed = amd ? amdResult == 1 || (amdResult is null && exitCode != 0) : exitCode is not (0 or 2 or 14 or 17 or 18);
        if (updated || ((restart || shutdown) && !failed))
            return new(true, $"Driver {latest.Version} installed{(shutdown ? ". Shut down your PC, then turn it on to finish" : restart ? ". Restart your PC to finish" : "")}");
        var maker = Maker(latest.Vendor);
        if (!failed) return new(false, $"{maker}'s installer finished, but the driver is still {installed ?? "the one before"}");
        return new(false, amd
            ? amdResult == 1 ? "AMD's installer stopped. Your driver is unchanged" : $"AMD's installer stopped (code {exitCode}). Your driver is unchanged"
            : exitCode switch
            {
                6 => "The install was closed. Your driver is unchanged",
                8 => "Intel's driver doesn't support this graphics card. Your driver is unchanged",
                11 => "There isn't enough free disk space. Your driver is unchanged",
                13 => "Restart your PC, then update again",
                15 => "An app Intel's installer needs closed is still open. Restart your PC, then update again",
                16 => "Another install is running. Update again when it's done",
                _ => $"Intel's installer stopped (code {exitCode}). Your driver is unchanged",
            });
    }

    /// <summary>The AMD Software version an unpacked AMD package installs (Config\InstallManifest.json's BuildInfo), or null.</summary>
    internal static string? AmdPackageVersion(string? manifest)
    {
        if (manifest is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(manifest.TrimStart('﻿'));
            return doc.RootElement.TryGetProperty("BuildInfo", out var info) && info.ValueKind == JsonValueKind.Object
                && info.TryGetProperty("RadeonSoftwareVersion", out var version) && version.ValueKind == JsonValueKind.String ? version.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private static string Root(string vendor) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ExoLauncher", "drivers-" + vendor);

    private static async Task RunAsync(Latest latest, string card)
    {
        var vendor = latest.Vendor;
        var maker = Maker(vendor);
        var folder = Path.Combine(Root(vendor), latest.Version);
        var package = Path.Combine(folder, Path.GetFileName(new Uri(latest.Url).LocalPath));
        var unpacked = Path.Combine(folder, "unpacked");
        var log = Path.Combine(folder, "install.log");
        try
        {
            // One installer per vendor stays on disk (about 1 GB): an older one goes.
            if (Directory.Exists(Root(vendor)))
                foreach (var old in Directory.EnumerateDirectories(Root(vendor)).Where(old => !string.Equals(old, folder, StringComparison.OrdinalIgnoreCase)).ToList())
                    TryDelete(old);
            Directory.CreateDirectory(folder);
            if (!Genuine(latest, package)) await DownloadAsync(latest, package).ConfigureAwait(false);
            if (!Genuine(latest, package)) throw new InvalidDataException($"The download isn't {maker}'s signed driver.");

            var installer = package;
            var arguments = IntelArguments;
            if (vendor == "amd")
            {
                Report("unpacking", 0, "Unpacking the driver");
                await UnpackAsync(package, unpacked).ConfigureAwait(false);
                installer = Path.Combine(unpacked, "Setup.exe");
                if (!AuthenticodeVerifier.HasPublisher(installer, AmdPublisher)) throw new InvalidDataException("AMD's installer isn't signed by AMD.");
                var manifest = Path.Combine(unpacked, "Config", "InstallManifest.json");
                if (AmdPackageVersion(File.Exists(manifest) ? File.ReadAllText(manifest) : null) != latest.Version)
                    throw new InvalidDataException($"AMD's package isn't AMD Software {latest.Version}.");
                File.Delete(log);
                arguments = AmdArguments(log);
            }

            // The driver swap crashes a game and its reload ends a call: one started during the download is waited out.
            await WaitQuietAsync(Holding, reason => Report("waiting", 0, reason), TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            // Windows asks for permission unless its settings let Exo go ahead without asking.
            Report("installing", 0, "Installing the driver. Your screen may flicker");
            AppLog.Info($"Driver: installing {maker} {latest.Version} for {card}: {Path.GetFileName(installer)} {arguments}");
            int exit;
            try
            {
                using var install = Process.Start(new ProcessStartInfo(installer, arguments) { UseShellExecute = true, Verb = "runas", WorkingDirectory = Path.GetDirectoryName(installer)! })
                    ?? throw new InvalidOperationException($"{maker}'s installer did not start.");
                await install.WaitForExitAsync().ConfigureAwait(false);
                exit = install.ExitCode;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                Report("failed", 0, "Windows didn't get permission, so nothing changed");
                return;
            }
            // AMD's Setup.exe hands the install to Bin64\ATISetup.exe, which starts AMD Software's installer: wait for both.
            if (vendor == "amd")
            {
                var until = DateTime.UtcNow + TimeSpan.FromHours(1);
                while (DateTime.UtcNow < until && (Running("ATISetup") || Running("AMDSoftwareInstaller"))) await Task.Delay(2000).ConfigureAwait(false);
            }
            var result = vendor == "amd" && File.Exists(log) ? AmdResult(File.ReadAllText(log)) : null;
            // Windows records the new driver as the install finishes; give it a few seconds before reading.
            var installed = Installed(vendor, card);
            for (var waited = 0; waited < 15 && !Same(installed, latest.Version); waited++)
            {
                await Task.Delay(1000).ConfigureAwait(false);
                installed = Installed(vendor, card);
            }
            var end = Outcome(latest, exit, result, installed);
            AppLog.Info($"Driver: {maker}'s installer exited {exit}{(result is { } code ? $", result {code}" : "")}; driver now {installed ?? "unknown"}: {end.Message}.");
            if (!end.Ok)
            {
                Report("failed", 0, end.Message);
                return;
            }
            lock (Gate) Cache.Clear();
            // Done before the profile is read: while the stage says installing, GpuProfileService answers "being installed".
            Report("done", 100, end.Message);
            // A new driver can reset its settings: put Exo's profile back (ADLX loads the new driver's library for each call).
            try
            {
                var profile = GpuProfileService.Read();
                if (profile.CanUndo) AppLog.Info($"Driver: Exo's profile put back: {(GpuProfileService.Apply(profile.Gsync, profile.AntiLag).Ok ? "done" : "the driver did not answer")}.");
            }
            catch (Exception ex) { AppLog.Warn("Driver: Exo's profile was not put back: " + ex.Message); }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Driver: {maker} update failed: {ex.Message}");
            Report("failed", 0, ex is InvalidDataException ? ex.Message : "The update stopped. Your driver is unchanged");
        }
        finally
        {
            // The unpacked driver (about 3 GB) goes however it ended. The installer stays for a reinstall.
            TryDelete(unpacked);
        }
    }

    private static bool Same(string? installed, string latest) =>
        Version.TryParse(installed, out var have) && Version.TryParse(latest, out var want) && have == want;

    /// <summary>The package is the vendor's: signed by them and, where the page lists one, the same SHA-512.</summary>
    private static bool Genuine(Latest latest, string path)
    {
        if (!File.Exists(path) || !AuthenticodeVerifier.HasPublisher(path, latest.Vendor == "amd" ? AmdPublisher : IntelPublisher)) return false;
        if (latest.Sha512 is null) return true;
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA512.HashData(stream)).Equals(latest.Sha512, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task DownloadAsync(Latest latest, string path)
    {
        var maker = Maker(latest.Vendor);
        Report("downloading", 0, $"Downloading from {maker}");
        var partial = path + ".part";
        try
        {
            using (var request = Request(latest.Vendor, latest.Url))
            using (var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                if (response.RequestMessage?.RequestUri?.Host != DownloadHost(latest.Vendor)) throw new InvalidDataException($"The download didn't come from {maker}.");
                var total = response.Content.Headers.ContentLength ?? latest.Size;
                if (total > MaxBytes) throw new InvalidDataException($"{maker}'s download is larger than a driver.");
                await using var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                await using var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
                var buffer = new byte[1 << 20];
                long done = 0;
                var lastPercent = -1;
                int read;
                while ((read = await StallGuard.ReadAsync(source, buffer).ConfigureAwait(false)) > 0)
                {
                    done += read;
                    if (done > MaxBytes) throw new InvalidDataException($"{maker}'s download is larger than a driver.");
                    await target.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                    var percent = total > 0 ? (int)Math.Min(100, done * 100 / total) : 0;
                    if (percent != lastPercent) { lastPercent = percent; Report("downloading", percent, $"Downloading from {maker}"); }
                }
                if (response.Content.Headers.ContentLength is { } length && done != length) throw new InvalidDataException("The download was cut short.");
            }
            File.Move(partial, path, true);
        }
        catch
        {
            try { File.Delete(partial); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    internal static string DownloadHost(string vendor) => vendor == "amd" ? "drivers.amd.com" : "downloadmirror.intel.com";

    /// <summary>AMD's package (a 7-Zip archive behind a signed stub) unpacked whole with Windows' tar: AMD's full install reads all of it.</summary>
    private static async Task UnpackAsync(string package, string unpacked)
    {
        TryDelete(unpacked);
        Directory.CreateDirectory(unpacked);
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "tar.exe")) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in (string[])["-xf", package, "-C", unpacked]) info.ArgumentList.Add(arg);
        using var unpack = Process.Start(info) ?? throw new InvalidOperationException("Windows' tar did not start.");
        await unpack.WaitForExitAsync().ConfigureAwait(false);
        if (unpack.ExitCode != 0 || !File.Exists(Path.Combine(unpacked, "Setup.exe"))) throw new InvalidDataException("The driver package didn't unpack.");
    }

    /// <summary>Unpacked drivers left by an install that ended before cleaning up (a crash), while none is running.</summary>
    private static void SweepUnpacked(string vendor)
    {
        lock (Gate) if (_running is { IsCompleted: false }) return;
        try
        {
            if (!Directory.Exists(Root(vendor))) return;
            foreach (var version in Directory.EnumerateDirectories(Root(vendor))) TryDelete(Path.Combine(version, "unpacked"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Debug("Driver: sweeping unpacked drivers: " + ex.Message); }
    }

    private static void TryDelete(string folder)
    {
        try { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        catch (Exception ex) { AppLog.Debug("Driver: could not remove " + folder + ": " + ex.Message); }
    }
}
