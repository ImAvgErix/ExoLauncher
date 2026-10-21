using System.Globalization;
using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// System > Graphics > Driver and profile: Exo's global profile (<see cref="GpuProfile"/>)
/// for this PC's GeForce series, in the variant the user picks: G-SYNC or No
/// G-SYNC (preselected from what the driver says the main screen can do).
/// Use writes every setting of that variant to the driver's global profile in
/// one session after saving every value it replaces to gpu-profile.json, so
/// Undo puts back exactly what was there (a value, or the driver's default),
/// even after Exo restarts. A re-apply or a switch of variant keeps the first
/// backup. Each read compares the driver with the applied variant, so a
/// driver update or another tool that moves a setting shows as changed with
/// Re-apply. AMD and Intel get what to set by hand; Exo writes neither.
/// </summary>
internal static class GpuProfileService
{
    internal sealed record State(bool Ok, string? Message, string? Vendor, string? Gpu, int Series, GpuProfile.Screen Screen,
        string Status, bool Gsync, bool CanUndo, bool DriverUpdated, IReadOnlyList<GpuProfile.Item> Manual);

    private sealed record Backup(uint Driver, bool Gsync, Dictionary<uint, uint?> Before);

    private static readonly Lock Gate = new();
    private static string BackupPath => Path.Combine(PathHelper.AppDataDir, "gpu-profile.json");

    /// <summary>The profile for this PC and how the driver compares.</summary>
    internal static State Read()
    {
        // While NVIDIA's installer swaps the driver, its settings can't be read: say so instead of an error.
        if (NvidiaDriverService.Current.Stage == "installing")
            return new State(false, "Your NVIDIA driver is being installed.", "nvidia", null, 0, MainScreen("nvidia"), "manual", false, false, false, []);
        lock (Gate)
        {
            FollowRefreshLocked();
            return ReadLocked();
        }
    }

    /// <summary>
    /// The G-SYNC frame cap follows the main screen: when the only difference from the applied
    /// profile is the cap (a new refresh rate or a new main screen), it is written again.
    /// Called when Windows reports a display change and whenever the profile is read.
    /// </summary>
    internal static void FollowRefresh()
    {
        lock (Gate) FollowRefreshLocked();
    }

    private static void FollowRefreshLocked()
    {
        try
        {
            if (LoadBackup() is not { Gsync: true } || GraphicsService.Vendor() != "nvidia") return;
            var screen = MainScreen("nvidia");
            var (ok, _, writes, values) = Nvidia(new GpuProfile.Variant(Series(), true, screen.RefreshHz));
            if (!ok) return;
            var stale = writes.Where(write => values.GetValueOrDefault(write.Key) != write.Value).Select(write => write.Key).ToList();
            if (stale.Count == 0 || stale.Any(id => id is not (GpuProfile.FrameRateLimit or GpuProfile.FrameRateLimitSlider))) return;
            var written = NvidiaProfiles.Write(writes.Where(write => stale.Contains(write.Key)).OrderBy(write => write.Key).ToList(), stale);
            AppLog.Info($"GPU profile frame cap follows the screen: refresh={screen.RefreshHz}; cap={writes[GpuProfile.FrameRateLimit]}; ok={written.Ok}.");
        }
        catch (Exception ex) { AppLog.Debug("GPU profile frame cap: " + ex.GetType().Name); }
    }

    /// <summary>Applies the G-SYNC or No G-SYNC variant to the global profile, backing up what it replaces first.</summary>
    internal static State Apply(bool gsync)
    {
        lock (Gate)
        {
            if (GraphicsService.Vendor() != "nvidia") return ReadLocked() with { Ok = false, Message = "Exo changes only NVIDIA driver settings." };
            var screen = MainScreen("nvidia");
            var (ok, message, writes, values) = Nvidia(new GpuProfile.Variant(Series(), gsync, screen.RefreshHz));
            if (!ok) return ReadLocked() with { Ok = false, Message = message };
            var ids = writes.Keys.Order().ToList();
            var earlier = LoadBackup();
            if (!SaveBackup(new Backup(NvidiaProfiles.DriverVersion() ?? 0, gsync, GpuProfile.MergeBackup(earlier?.Before, values, ids))))
                return ReadLocked() with { Ok = false, Message = "Exo could not save a backup, so it changed nothing." };
            var written = NvidiaProfiles.Write(writes.OrderBy(write => write.Key).ToList(), ids);
            if (!written.Ok)
            {
                // One session, one save: nothing was written, so the backup goes back to what it was.
                if (earlier is null) DeleteBackup(); else SaveBackup(earlier);
                return ReadLocked() with { Ok = false, Message = written.Message };
            }
            AppLog.Info($"GPU profile applied: series={Series()}; gsync={gsync}; settings={ids.Count}; refresh={screen.RefreshHz}.");
            return ReadLocked();
        }
    }

    /// <summary>Puts back every value the profile replaced and forgets the backup.</summary>
    internal static State Undo()
    {
        lock (Gate)
        {
            if (LoadBackup() is not { } backup) return ReadLocked() with { Ok = false, Message = "Nothing to undo." };
            if (GraphicsService.Vendor() != "nvidia") return ReadLocked() with { Ok = false, Message = "Not undone." };
            var ids = backup.Before.Keys.Order().ToList();
            var restored = NvidiaProfiles.Write(NvidiaProfileImport.UndoWrites(backup.Before, ids), ids);
            if (!restored.Ok) return ReadLocked() with { Ok = false, Message = restored.Message };
            DeleteBackup();
            AppLog.Info($"GPU profile undone: settings={ids.Count}.");
            return ReadLocked();
        }
    }

    private static State ReadLocked()
    {
        var vendor = GraphicsService.Vendor();
        var screen = MainScreen(vendor);
        if (vendor is null) return new State(true, null, null, null, 0, screen, "none", false, false, false, []);
        if (vendor != "nvidia")
        {
            var amd = vendor == "amd";
            return new State(true,
                amd ? "Set these in AMD Software. Exo reads AMD's settings but does not change them yet: it has not been tried on an AMD card."
                    : "Set these in Intel Graphics Software. Exo reads Intel's settings but does not change them yet: it has not been tried on an Intel card.",
                vendor, AdapterName(vendor), 0, screen, "manual", false, false, false,
                amd ? GpuProfile.Amd(screen, AmdAdlx.Read()) : GpuProfile.Intel(screen, IntelIgcl.Read()));
        }
        var backup = LoadBackup();
        // The applied variant is the one to compare; before any, what the screen reports.
        var gsync = backup?.Gsync ?? screen.Vrr;
        var (ok, message, writes, values) = Nvidia(new GpuProfile.Variant(Series(), gsync, screen.RefreshHz));
        if (!ok) return new State(false, message, vendor, AdapterName(vendor), Series(), screen, "none", gsync, backup is not null, false, []);
        var status = GpuProfile.Status(writes, values, backup is not null);
        var driverUpdated = status == "changed" && backup!.Driver != (NvidiaProfiles.DriverVersion() ?? 0);
        return new State(true, null, vendor, AdapterName(vendor), Series(), screen, status, gsync, backup is not null, driverUpdated, []);
    }

    /// <summary>The variant's writes and what the global profile holds for them, in one driver session.</summary>
    private static (bool Ok, string? Message, IReadOnlyDictionary<uint, uint?> Writes, IReadOnlyDictionary<uint, uint?> Values) Nvidia(GpuProfile.Variant variant)
    {
        var known = GraphicsService.KnownIds();
        if (known.Count == 0) return (false, "The NVIDIA driver did not list its settings.", new Dictionary<uint, uint?>(), new Dictionary<uint, uint?>());
        var free = FreeGb();
        // The ids come from the profile built on nothing set; the values then pick the shader cache.
        var ids = GpuProfile.Nvidia(variant, free, known, new Dictionary<uint, uint?>()).Keys.ToList();
        var read = NvidiaProfiles.Read(ids);
        if (!read.Ok) return (false, read.Message, new Dictionary<uint, uint?>(), read.Values);
        return (true, null, GpuProfile.Nvidia(variant, free, known, read.Values), read.Values);
    }

    private static int Series() =>
        GpuCapability.Adapters().Select(GpuProfile.NvidiaSeries).FirstOrDefault(series => series is not null) ?? 0;

    private static string? AdapterName(string vendor)
    {
        var name = GpuCapability.Adapters().FirstOrDefault(adapter => GraphicsService.Vendors([adapter]).Contains(vendor));
        return name?.Replace("NVIDIA ", "", StringComparison.OrdinalIgnoreCase).Replace("GeForce ", "", StringComparison.OrdinalIgnoreCase).Trim();
    }

    /// <summary>The main screen; its variable refresh only as NVIDIA's driver reports it (others: false).</summary>
    private static GpuProfile.Screen MainScreen(string? vendor)
    {
        if (WindowsHdr.Primary() is not { } primary) return new GpuProfile.Screen(null, 0, false);
        var refresh = DisplayOutput.RefreshRates(primary.GdiName).Current;
        var vrr = vendor == "nvidia" && NvidiaProfiles.ScreenVrr(primary.GdiName) == true;
        return new GpuProfile.Screen(string.IsNullOrWhiteSpace(primary.Name) ? null : primary.Name, refresh, vrr);
    }

    /// <summary>Free space on the drive the driver keeps its shader cache on (%LOCALAPPDATA%), in GB.</summary>
    private static long FreeGb()
    {
        try
        {
            var root = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            return root is null ? 0 : new DriveInfo(root).AvailableFreeSpace / (1024L * 1024 * 1024);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return 0; }
    }

    // gpu-profile.json: {"driver":61742,"gsync":false,"before":{"0x10835002":141,"0x00A879CF":null}}
    // A backup from before the G-SYNC choice has no "gsync": it reads as No G-SYNC, which only picks what Re-apply offers.
    private static Backup? LoadBackup()
    {
        try
        {
            if (!File.Exists(BackupPath)) return null;
            using var json = JsonDocument.Parse(File.ReadAllText(BackupPath));
            var driver = json.RootElement.GetProperty("driver").GetUInt32();
            var gsync = json.RootElement.TryGetProperty("gsync", out var flag) && flag.ValueKind == JsonValueKind.True;
            var before = new Dictionary<uint, uint?>();
            foreach (var item in json.RootElement.GetProperty("before").EnumerateObject())
            {
                if (!uint.TryParse(item.Name.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id)) continue;
                before[id] = item.Value.ValueKind == JsonValueKind.Null ? null : item.Value.GetUInt32();
            }
            return new Backup(driver, gsync, before);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            AppLog.Warn("GPU profile backup unreadable: " + ex.GetType().Name);
            return null;
        }
    }

    private static bool SaveBackup(Backup backup)
    {
        try
        {
            var body = new Dictionary<string, object?>
            {
                ["driver"] = backup.Driver,
                ["gsync"] = backup.Gsync,
                ["before"] = backup.Before.OrderBy(item => item.Key).ToDictionary(item => $"0x{item.Key:X8}", item => item.Value),
            };
            var temp = BackupPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(body));
            File.Move(temp, BackupPath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("GPU profile backup not saved: " + ex.GetType().Name);
            return false;
        }
    }

    private static void DeleteBackup()
    {
        try { File.Delete(BackupPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Warn("GPU profile backup not removed: " + ex.GetType().Name); }
    }
}
