using System.Globalization;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
using ExoLauncher.Helpers;
using Microsoft.Win32;

namespace ExoLauncher.Services;

/// <summary>
/// System > Graphics > Driver and profile's driver row: the newest NVIDIA Game Ready driver for this PC's card, and a
/// one-click update that installs only the driver. docs/architecture/nvidia-driver-install.md has the
/// research (NVIDIA's lookup endpoints, the package format, what NVCleanstall and Nexus do).
///
/// 1. Check: NVIDIA's product list maps the card's name to its series and product ids (a GeForce card to a
///    GeForce series, a laptop to a notebook series, a workstation card to an NVIDIA RTX / Quadro series,
///    never a data center one), then NVIDIA's driver list gives the newest DCH driver for the chosen branch:
///    Game Ready or Studio on GeForce, NVIDIA's recommended RTX Enterprise driver on a workstation card. On
///    Game Ready, a GeForce hotfix driver (announced on NVIDIA's GeForce forums) wins when it is newer and the card
///    runs NVIDIA's current branches (<see cref="CurrentBranches"/>); Maxwell, Pascal and Volta stopped at release 580,
///    and NVIDIA's list gives them its last driver for them. Cached an hour, so a driver NVIDIA puts out today shows the same day.
/// 2. Update, after the user's consent: download from NVIDIA; verify NVIDIA's Authenticode signature and the
///    size; unpack only the driver and NVIDIA's installer (on a laptop also the platform controllers) with
///    Windows' own tar, so nothing else (NVIDIA app, telemetry, containers, ShadowPlay, PhysX, HDMI/DP audio)
///    is ever installed; check setup.exe's signature; run NVIDIA's setup as a silent clean install as
///    administrator (Windows asks); confirm the new version; put Exo's GPU profile back.
/// Never touches a file NVIDIA signed, never re-signs, never writes driver tweaks. Waits while a game runs or a Discord call is on.
/// A Radeon or an Intel GPU gets the same row from <see cref="GpuDriverUpdate"/>.
/// </summary>
internal static partial class NvidiaDriverService
{
    /// <summary>Kind is game, studio, hotfix, security (NVIDIA's security update for a card Game Ready drivers left) or workstation.</summary>
    internal sealed record Latest(string Version, string Url, long Size, string? Released, string Kind);

    /// <summary>Where an update stands; Stage is idle, downloading, unpacking, waiting (for a game to close), installing, done or failed.</summary>
    internal sealed record Progress(string Stage, int Percent, string? Message);

    private static readonly HttpClient Http = ExoHttp.Create(TimeSpan.FromMinutes(30));
    private static readonly Lock Gate = new();
    /// <summary>The newest driver per card and branch, so the header's Game Ready read and the row's Studio read don't evict each other.
    /// Kept on disk too, so a restart doesn't ask NVIDIA again (PRIVACY.md: at most once an hour).</summary>
    private static readonly Dictionary<string, (DateTime At, Latest? Value)> Cache = [];
    /// <summary>One lookup at a time per card and branch; everyone asking meanwhile shares it.</summary>
    private static readonly Dictionary<string, Task<Latest?>> Fetching = [];
    private static bool _cacheLoaded;
    /// <summary>Until when NVIDIA asked Exo to wait (a 429 or 503, its Retry-After).</summary>
    private static DateTime _backoffUntil;
    private static string CachePath => Path.Combine(PathHelper.AppDataDir, "nvidia-drivers.json");
    private sealed record Saved(DateTime At, Latest? Value);
    private static Progress _progress = new("idle", 0, null);
    private static Task? _running;

    /// <summary>Fires on every change of <see cref="Current"/>.</summary>
    internal static event Action<Progress>? Changed;

    internal static Progress Current { get { lock (Gate) return _progress; } }

    private static void Report(string stage, int percent = 0, string? message = null)
    {
        Progress next;
        lock (Gate) next = _progress = new(stage, percent, message);
        Changed?.Invoke(next);
    }

    /// <summary>"617.42" from NvAPI's 61742, or null without an NVIDIA driver.</summary>
    internal static string? Installed() => WindowsDriverVersion()
        ?? (NvidiaProfiles.DriverVersion() is { } v and > 0 ? $"{v / 100}.{v % 100:00}" : null);

    /// <summary>
    /// The NVIDIA display driver as Windows records it, current the moment an install finishes (NvAPI in this process
    /// keeps answering for the driver it loaded with until Exo restarts). Null when Windows has no NVIDIA display driver.
    /// </summary>
    internal static string? WindowsDriverVersion() => NvidiaAdapter("DriverVersion") is { } version ? FromWindowsVersion(version) : null;

    /// <summary>A value Windows records for the NVIDIA display driver (its DriverVersion, MatchingDeviceId), or null without one.</summary>
    private static string? NvidiaAdapter(string value)
    {
        try
        {
            using var display = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (display is null) return null;
            foreach (var name in display.GetSubKeyNames().Where(name => name.All(char.IsDigit)))
            {
                using var adapter = display.OpenSubKey(name);
                if (adapter?.GetValue("ProviderName") is string provider && provider.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) &&
                    adapter.GetValue(value) is string text)
                    return text;
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        return null;
    }

    /// <summary>
    /// Whether NVIDIA's current driver branches support the card, by the hardware id Windows matched its driver to
    /// ("pci\ven_10de&amp;dev_2484", an RTX 3070): Turing (device 1E00) and newer. Maxwell, Pascal and Volta (and older)
    /// stopped at release 580, so a hotfix from a newer branch won't install on them. An unknown card gets no hotfix.
    /// </summary>
    internal static bool CurrentBranches(string? hardwareId) =>
        hardwareId is not null && DeviceId().Match(hardwareId) is { Success: true } match
        && int.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture) >= 0x1E00;

    [System.Text.RegularExpressions.GeneratedRegex(@"ven_10de&dev_([0-9a-f]{4})", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex DeviceId();

    /// <summary>Windows' 32.0.16.1770 is NVIDIA's 617.70: the last five digits of its last two parts.</summary>
    internal static string? FromWindowsVersion(string version)
    {
        var parts = version.Split('.');
        if (parts.Length != 4 || !parts.All(part => part.Length > 0 && part.All(char.IsDigit)) || parts[3].Length > 4) return null;
        var digits = parts[2] + parts[3].PadLeft(4, '0');
        if (digits.Length < 5) return null;
        var tail = digits[^5..];
        return $"{int.Parse(tail[..3], CultureInfo.InvariantCulture)}.{tail[3..]}";
    }

    /// <summary>The NVIDIA card's name as Windows has it ("NVIDIA GeForce RTX 3070"), or null.</summary>
    internal static string? Card() => GpuCapability.Adapters().FirstOrDefault(adapter => GraphicsService.Vendors([adapter]).Contains("nvidia"));

    /// <summary>Whether the card is a workstation one (NVIDIA RTX / Quadro): it gets NVIDIA's RTX Enterprise driver, no branch choice.</summary>
    internal static bool Workstation(string card)
    {
        if (card.Contains("GeForce", StringComparison.OrdinalIgnoreCase)) return false;
        var name = Plain(card);
        return name.StartsWith("Quadro", StringComparison.OrdinalIgnoreCase) || name.StartsWith("RTX ", StringComparison.OrdinalIgnoreCase)
            || (name.Length is > 1 and <= 6 && name[0] == 'T' && name.Skip(1).All(char.IsDigit));
    }

    private static string Plain(string name) => name.Replace("NVIDIA ", "", StringComparison.OrdinalIgnoreCase).Replace(" GPU", "", StringComparison.OrdinalIgnoreCase).Trim();

    /// <summary>The newest driver for this card on the branch (game or studio; a workstation card has one), from NVIDIA (cached an hour); null when it can't tell.</summary>
    /// <remarks>An answer older than the hour shows at once while NVIDIA is asked again (System's first showing never
    /// waits on the network; the row reads again each time it shows); <paramref name="fresh"/> (an update) waits for it.</remarks>
    internal static async Task<Latest?> LatestAsync(string branch, CancellationToken ct, bool fresh = false)
    {
        SweepUnpacked();
        if (Card() is not { } card) return null;
        var workstation = Workstation(card);
        var studio = !workstation && branch == "studio";
        var hardwareId = NvidiaAdapter("MatchingDeviceId");
        var hotfix = !workstation && !studio && CurrentBranches(hardwareId);
        var key = card + "|" + hardwareId + "|" + (workstation ? "workstation" : studio ? "studio" : "game");
        Task<Latest?>? task;
        lock (Gate)
        {
            LoadCacheLocked();
            var waiting = DateTime.UtcNow < _backoffUntil;
            var known = Cache.TryGetValue(key, out var c);
            if (known && (waiting || DateTime.UtcNow - c.At < TimeSpan.FromHours(1))) return c.Value;
            if (waiting) return null;
            if (!Fetching.TryGetValue(key, out task))
                Fetching[key] = task = Task.Run(() => FetchAsync(card, key, workstation, studio, hotfix));
            if (known && !fresh) return c.Value;
        }
        return await task.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Asks NVIDIA (shared by everyone waiting on this key, so one caller leaving doesn't stop it) and keeps the answer an hour.</summary>
    private static async Task<Latest?> FetchAsync(string card, string key, bool workstation, bool studio, bool hotfix)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var ct = timeout.Token;
        Latest? latest = null;
        try
        {
            var products = await ProductsAsync(card, ct).ConfigureAwait(false);
            if (products.Count == 0) AppLog.Info($"Driver: NVIDIA lists no product named \"{Plain(card)}\".");
            foreach (var (series, product) in products)
            {
                // Studio drivers are listed without NVIDIA's WHQL flag; betas are skipped either way.
                var url = "https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php?func=DriverManualLookup" +
                    $"&psid={series}&pfid={product}&osID=57&languageCode=1033&isWHQL={(studio ? 0 : 1)}&dch=1&upCRD={(studio ? 1 : 0)}&sort1=0&numberOfResults=3";
                using var doc = JsonDocument.Parse(await GetTextAsync(url, ct).ConfigureAwait(false));
                if (!doc.RootElement.TryGetProperty("IDS", out var ids) || ids.ValueKind != JsonValueKind.Array) continue;
                foreach (var entry in ids.EnumerateArray())
                {
                    var info = entry.GetProperty("downloadInfo");
                    if (info.TryGetProperty("IsBeta", out var beta) && beta.GetString() == "1") continue;
                    var version = info.GetProperty("Version").GetString();
                    var download = info.GetProperty("DownloadURL").GetString();
                    if (version is null || download is null || !download.StartsWith("https://us.download.nvidia.com/", StringComparison.OrdinalIgnoreCase)) continue;
                    var released = info.TryGetProperty("ReleaseDateTime", out var r) ? r.GetString() : null;
                    var size = info.TryGetProperty("DownloadURLFileSize", out var sz) && double.TryParse(sz.GetString()?.Split(' ')[0],
                        System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var mb) ? (long)(mb * 1_000_000) : 0;
                    // A card NVIDIA's Game Ready drivers left (GTX 10 series and older) gets its security updates instead.
                    var security = info.TryGetProperty("Name", out var n) && n.GetString()?.Contains("Security", StringComparison.OrdinalIgnoreCase) == true;
                    if (latest is null || Compare(version, latest.Version) > 0)
                        latest = new Latest(version, download, size, released, workstation ? "workstation" : studio ? "studio" : security ? "security" : "game");
                    break;
                }
            }
            if (products.Count > 0 && hotfix && await HotfixAsync(card, ct).ConfigureAwait(false) is { } newer && (latest is null || Compare(newer.Version, latest.Version) > 0))
                latest = newer;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or System.Xml.XmlException or KeyNotFoundException or InvalidOperationException)
        {
            AppLog.Info("Driver: NVIDIA's driver list could not be read: " + ex.Message);
            lock (Gate) Fetching.Remove(key);
            return null;
        }
        lock (Gate)
        {
            Fetching.Remove(key);
            Cache[key] = (DateTime.UtcNow, latest);
            SaveCacheLocked();
        }
        return latest;
    }

    /// <summary>A page from NVIDIA. A 429 or 503 makes Exo leave NVIDIA alone for its Retry-After (else an hour, at most a day).</summary>
    private static async Task<string> GetTextAsync(string url, CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, ct).ConfigureAwait(false);
        if (response.StatusCode is System.Net.HttpStatusCode.TooManyRequests or System.Net.HttpStatusCode.ServiceUnavailable)
        {
            var after = response.Headers.RetryAfter is { } retry ? retry.Delta ?? retry.Date - DateTimeOffset.UtcNow : null;
            var wait = after is { } a && a > TimeSpan.Zero ? (a < TimeSpan.FromDays(1) ? a : TimeSpan.FromDays(1)) : TimeSpan.FromHours(1);
            lock (Gate) if (DateTime.UtcNow + wait > _backoffUntil) _backoffUntil = DateTime.UtcNow + wait;
            AppLog.Info($"Driver: NVIDIA asked Exo to wait ({(int)response.StatusCode}); next look in {wait.TotalMinutes:0} min.");
        }
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    private static void LoadCacheLocked()
    {
        if (_cacheLoaded) return;
        _cacheLoaded = true;
        try
        {
            if (File.Exists(CachePath) && JsonSerializer.Deserialize<Dictionary<string, Saved>>(File.ReadAllText(CachePath)) is { } saved)
                foreach (var (key, entry) in saved) Cache[key] = (entry.At, entry.Value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            AppLog.Debug("Driver: the saved driver list is unreadable: " + ex.Message);
        }
    }

    private static void SaveCacheLocked()
    {
        var temp = CachePath + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(Cache.ToDictionary(item => item.Key, item => new Saved(item.Value.At, item.Value.Value))));
            File.Move(temp, CachePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Debug("Driver: the driver list was not saved: " + ex.Message); }
    }

    /// <summary>
    /// The card's (series, product) ids. NVIDIA lists one card under several series (desktop, notebook,
    /// data center): keep the series of the card's own kind, by the series' product type (1 GeForce, 3 NVIDIA
    /// RTX / Quadro) and whether it is a notebook series.
    /// </summary>
    private static async Task<List<(string Series, string Product)>> ProductsAsync(string card, CancellationToken ct)
    {
        var name = Plain(card);
        var laptop = card.Contains("Laptop", StringComparison.OrdinalIgnoreCase) || card.Contains("Max-Q", StringComparison.OrdinalIgnoreCase);
        var type = Workstation(card) ? "3" : "1";
        var seriesDoc = XDocument.Parse(await GetTextAsync("https://www.nvidia.com/Download/API/lookupValueSearch.aspx?TypeID=2", ct).ConfigureAwait(false));
        var series = new Dictionary<string, bool>();
        foreach (var v in seriesDoc.Descendants("LookupValue"))
        {
            var id = (string?)v.Element("Value");
            var seriesName = (string?)v.Element("Name") ?? "";
            if (id is not null && (string?)v.Attribute("ParentID") == type)
                series[id] = seriesName.Contains("Notebook", StringComparison.OrdinalIgnoreCase) || seriesName.Contains("Laptop", StringComparison.OrdinalIgnoreCase);
        }
        var products = XDocument.Parse(await GetTextAsync("https://www.nvidia.com/Download/API/lookupValueSearch.aspx?TypeID=3", ct).ConfigureAwait(false));
        var found = products.Descendants("LookupValue")
            .Where(v => string.Equals(Plain((string?)v.Element("Name") ?? ""), name, StringComparison.OrdinalIgnoreCase))
            .Select(v => (Series: (string?)v.Attribute("ParentID") ?? "", Product: (string?)v.Element("Value") ?? ""))
            .Where(v => v.Product.Length > 0 && series.ContainsKey(v.Series))
            .ToList();
        // A laptop's own notebook series when NVIDIA has one, else whatever matched; a desktop never takes a notebook series.
        var own = found.Where(v => series[v.Series] == laptop).ToList();
        return own.Count > 0 || !laptop ? own : found;
    }

    private const string ForumsApi = "https://api-forums.nvidia.com/forums-api/1.0";

    /// <summary>
    /// The newest GeForce hotfix driver, from NVIDIA's GeForce forums, where NVIDIA announces every hotfix
    /// (its support site's list misses some, such as 617.70); null when there is none or it can't be read.
    /// The newest few hotfix topics NVIDIA's own staff opened are read for NVIDIA's download link, which
    /// names the version. Hotfixes are GeForce-only and one package covers desktops and laptops.
    /// </summary>
    private static async Task<Latest?> HotfixAsync(string card, CancellationToken ct)
    {
        if (!card.Contains("GeForce", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            var search = await GetTextAsync(ForumsApi + "/search?query=hotfix%20driver&order=created&_pageSize=20&_page=1", ct).ConfigureAwait(false);
            Latest? newest = null;
            foreach (var id in HotfixTopics(search).Take(3))
                if (HotfixIn(await GetTextAsync(ForumsApi + "/topics?id=" + id, ct).ConfigureAwait(false)) is { } found
                    && (newest is null || Compare(found.Version, newest.Version) > 0))
                    newest = found;
            return newest;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            AppLog.Info("Driver: NVIDIA's hotfix announcements could not be read: " + ex.Message);
            return null;
        }
    }

    /// <summary>The forum search's topics that NVIDIA staff opened with "hotfix" in the title, newest first.</summary>
    internal static List<long> HotfixTopics(string searchJson)
    {
        using var doc = JsonDocument.Parse(searchJson);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return [];
        var topics = new List<(long Id, long Created)>();
        foreach (var group in data.EnumerateObject())
        {
            if (group.Value.ValueKind != JsonValueKind.Array) continue;
            foreach (var topic in group.Value.EnumerateArray())
            {
                if (topic.ValueKind != JsonValueKind.Object || !topic.TryGetProperty("id", out var id) || !id.TryGetInt64(out var topicId)) continue;
                var title = topic.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? HtmlTag().Replace(t.GetString()!, "") : "";
                var official = topic.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object
                    && user.TryGetProperty("aclactions", out var acl) && acl.ValueKind == JsonValueKind.Object
                    && acl.TryGetProperty("officialreply", out var reply) && reply.ValueKind == JsonValueKind.True;
                if (!official || !title.Contains("hotfix", StringComparison.OrdinalIgnoreCase)) continue;
                topics.Add((topicId, topic.TryGetProperty("createdStamp", out var c) && c.TryGetInt64(out var at) ? at : 0));
            }
        }
        return topics.OrderByDescending(t => t.Created).Select(t => t.Id).Distinct().ToList();
    }

    /// <summary>The newest hotfix download a forum topic's first post links to, or null.</summary>
    internal static Latest? HotfixIn(string topicJson)
    {
        using var doc = JsonDocument.Parse(topicJson);
        var body = doc.RootElement.TryGetProperty("data", out var data) && data.TryGetProperty("topic", out var topic)
            && topic.TryGetProperty("firstComment", out var first) && first.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String
            ? b.GetString()! : "";
        return HotfixLink().Matches(body)
            .Select(m => new Latest(m.Groups[1].Value, m.Value, 0, null, "hotfix"))
            .OrderByDescending(l => l.Version, Comparer<string>.Create(Compare))
            .FirstOrDefault();
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"<[^>]+>")]
    private static partial System.Text.RegularExpressions.Regex HtmlTag();

    [System.Text.RegularExpressions.GeneratedRegex(@"https://(?:international|us)\.download\.nvidia\.com/Windows/(\d{3}\.\d{2})hf/[\w.-]+\.hf\.exe")]
    private static partial System.Text.RegularExpressions.Regex HotfixLink();

    /// <summary>Compares "617.42"-style versions.</summary>
    internal static int Compare(string a, string b)
    {
        static (int, int) Parse(string v) { var p = v.Split('.'); return (int.TryParse(p[0], out var x) ? x : 0, p.Length > 1 && int.TryParse(p[1], out var y) ? y : 0); }
        return Parse(a).CompareTo(Parse(b));
    }

    /// <summary>Why an update can't start now (a game, a Discord call, an Exo Shaders compile: <see cref="GpuDriverUpdate.Busy"/>), or null.</summary>
    internal static string? Blocked() => _running is { IsCompleted: false } ? "A driver update is already running." : GpuDriverUpdate.Busy();

    /// <summary>Starts the update (the user consented); progress comes through <see cref="Changed"/>.</summary>
    internal static string? Start(Latest latest)
    {
        // Laptops keep NVIDIA's platform controllers (Dynamic Boost, power sharing with the CPU).
        var laptop = Card()?.Contains("Laptop", StringComparison.OrdinalIgnoreCase) == true;
        lock (Gate)
        {
            if (Blocked() is { } why) return why;
            _running = Task.Run(() => RunAsync(latest, laptop));
        }
        return null;
    }

    private static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ExoLauncher", "drivers");

    private static async Task RunAsync(Latest latest, bool laptop)
    {
        var folder = Path.Combine(Root, latest.Version);
        var package = Path.Combine(folder, Path.GetFileName(new Uri(latest.Url).LocalPath));
        var unpacked = Path.Combine(folder, "unpacked");
        try
        {
            Directory.CreateDirectory(folder);
            if (!File.Exists(package) || !AuthenticodeVerifier.HasPublisher(package, "NVIDIA Corporation"))
                await DownloadAsync(latest, package).ConfigureAwait(false);
            if (!AuthenticodeVerifier.HasPublisher(package, "NVIDIA Corporation"))
                throw new InvalidDataException("The download isn't signed by NVIDIA.");

            // Only the driver is ever unpacked: everything else in NVIDIA's package stays in the archive.
            Report("unpacking", 0, "Unpacking the driver");
            if (Directory.Exists(unpacked)) Directory.Delete(unpacked, true);
            Directory.CreateDirectory(unpacked);
            // setup.cfg's manifest checks for the NVIDIA app's license, privacy and consent texts even when the app
            // isn't installed (missing, setup quits with 0x80070003): unpack those texts, never NvApp.nvi, so the app
            // itself is never offered to setup.
            string[] keep = ["setup.exe", "setup.cfg", "EULA.txt", "ListDevices.txt", "Display.Driver", "NVI2", .. laptop ? new[] { "NVPCF" } : [],
                "NvApp/Unified_EULA", "NvApp/PrivacyPolicy", "NvApp/FunctionalConsent_*"];
            var unpackInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "tar.exe")) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in (string[])["-xf", package, "-C", unpacked, .. keep]) unpackInfo.ArgumentList.Add(arg);
            using (var unpack = Process.Start(unpackInfo) ?? throw new InvalidOperationException("Windows' tar did not start."))
            {
                await unpack.WaitForExitAsync().ConfigureAwait(false);
                if (unpack.ExitCode != 0) throw new InvalidDataException("The driver package didn't unpack.");
            }
            var setup = Path.Combine(unpacked, "setup.exe");
            if (!File.Exists(setup) || !Directory.Exists(Path.Combine(unpacked, "Display.Driver"))) throw new InvalidDataException("The driver package didn't unpack.");
            if (!AuthenticodeVerifier.HasPublisher(setup, "NVIDIA Corporation")) throw new InvalidDataException("NVIDIA's installer isn't signed by NVIDIA.");

            // A clean install blanks the screen for a moment and the driver's reload ends a Discord call: a game or a call
            // started during the download is waited out first.
            await GpuDriverUpdate.WaitQuietAsync(GpuDriverUpdate.Holding, reason => Report("waiting", 0, reason), TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            // Windows asks for permission unless its settings let Exo go ahead without asking.
            Report("installing", 0, "Installing the driver. Your screen may flicker");
            // Always clean: nothing of an earlier install (its settings, its leftovers) carries over.
            const string args = "-s -clean -noreboot -noeula -nofinish -nosplash";
            AppLog.Info($"Driver: clean install of {latest.Version} ({string.Join(", ", keep.Skip(4).Where(item => !item.StartsWith("NvApp/", StringComparison.Ordinal)))}).");
            Process install;
            try
            {
                install = Process.Start(new ProcessStartInfo(setup, args) { UseShellExecute = true, Verb = "runas", WorkingDirectory = unpacked })
                    ?? throw new InvalidOperationException("NVIDIA's installer did not start.");
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                Report("failed", 0, "Windows didn't get permission, so nothing changed");
                return;
            }
            await install.WaitForExitAsync().ConfigureAwait(false);
            // Windows records the new driver as setup finishes; give it a few seconds before reading.
            var installed = Installed();
            for (var waited = 0; waited < 15 && install.ExitCode == 0 && (installed is null || Compare(installed, latest.Version) != 0); waited++)
            {
                await Task.Delay(1000).ConfigureAwait(false);
                installed = Installed();
            }
            AppLog.Info($"Driver: NVIDIA setup exited {install.ExitCode}; driver now {installed ?? "unknown"}.");
            if (install.ExitCode != 0 || installed is null || Compare(installed, latest.Version) != 0)
            {
                Report("failed", 0, $"NVIDIA's installer stopped (code {install.ExitCode}). Your driver is unchanged");
                return;
            }

            // NvAPI in this process still points at the driver that was removed: load it again for the new one.
            NvidiaProfiles.Reinitialize();
            lock (Gate) { Cache.Clear(); SaveCacheLocked(); }
            // Done before the profile is read: while the stage says installing, GpuProfileService answers "being installed".
            Report("done", 100, $"Driver {latest.Version} installed");
            try
            {
                // A new driver can reset driver profiles: put Exo's back.
                var profile = GpuProfileService.Read();
                if (profile.CanUndo) GpuProfileService.Apply(profile.Gsync);
                // And the desktop color (NVIDIA's brightness, contrast, gamma, vibrance and hue), which a clean install resets.
                NvidiaColor.ApplySaved(App.Services.Settings);
            }
            catch (Exception ex) { AppLog.Warn("Driver: Exo's profile was not put back: " + ex.Message); }
            Prune(keepVersion: latest.Version);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Driver update failed: " + ex.Message);
            Report("failed", 0, ex is InvalidDataException ? ex.Message : "The update stopped. Your driver is unchanged");
        }
        finally
        {
            // The unpacked driver (about 3 GB) goes however it ended: done, refused, stopped or failed. The installer stays for next time.
            TryDelete(unpacked);
        }
    }

    private static async Task DownloadAsync(Latest latest, string path)
    {
        Report("downloading", 0, "Downloading from NVIDIA");
        var partial = path + ".part";
        try
        {
            using (var response = await Http.GetAsync(latest.Url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                if (response.RequestMessage?.RequestUri?.Host is not ("us.download.nvidia.com" or "international.download.nvidia.com")) throw new InvalidDataException("The download didn't come from NVIDIA.");
                var total = response.Content.Headers.ContentLength ?? latest.Size;
                await using var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                await using var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
                var buffer = new byte[1 << 20];
                long done = 0;
                var lastPercent = -1;
                int read;
                while ((read = await StallGuard.ReadAsync(source, buffer).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                    done += read;
                    var percent = total > 0 ? (int)(done * 100 / total) : 0;
                    if (percent != lastPercent) { lastPercent = percent; Report("downloading", percent, "Downloading from NVIDIA"); }
                }
                if (response.Content.Headers.ContentLength is { } length && done != length) throw new InvalidDataException("The download was cut short.");
            }
            File.Move(partial, path, true);
        }
        catch
        {
            // A half file is not kept: nothing would check it, and it holds the disk.
            try { File.Delete(partial); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>Keeps this version's and the one before's installers (the way back), removes older ones.</summary>
    private static void Prune(string keepVersion)
    {
        try
        {
            var folders = Directory.EnumerateDirectories(Root).OrderByDescending(d => Path.GetFileName(d), Comparer<string>.Create(Compare)).ToList();
            foreach (var old in folders.Where(f => Path.GetFileName(f) != keepVersion).Skip(1)) TryDelete(old);
        }
        catch (Exception ex) { AppLog.Debug("Driver: pruning old installers: " + ex.Message); }
    }

    /// <summary>Unpacked drivers left by an install that ended before cleaning up (an older Exo, a crash), while none is running.</summary>
    private static void SweepUnpacked()
    {
        lock (Gate) if (_running is { IsCompleted: false }) return;
        try
        {
            if (!Directory.Exists(Root)) return;
            foreach (var version in Directory.EnumerateDirectories(Root)) TryDelete(Path.Combine(version, "unpacked"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Debug("Driver: sweeping unpacked drivers: " + ex.Message); }
    }

    private static void TryDelete(string folder)
    {
        try { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        catch (Exception ex) { AppLog.Debug("Driver: could not remove " + folder + ": " + ex.Message); }
    }
}
