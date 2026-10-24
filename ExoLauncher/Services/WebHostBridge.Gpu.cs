using System.Text.Json;

namespace ExoLauncher.Services;

/// <summary>
/// System > Graphics > Driver and profile. gpu.profile: Exo's profile for this PC (no
/// action), apply (with gsync: the G-SYNC or No G-SYNC variant; on a Radeon, antiLag; on Intel,
/// lowLatency) or undo. gpu.sensors: what the GPU reports right now. gpu.game: a game's Fluid
/// Motion Frames choice on a Radeon (set when "afmf" is passed), or its XeSS frame generation on
/// Intel (set when "frameGen" is passed).
/// gpu.import: a Profile Inspector file for the global profile (pick, apply,
/// undo, cancel, state). The file's path stays here: the page gets its
/// profile name and the settings in plain words.
/// </summary>
public sealed partial class WebHostBridge
{
    /// <summary>
    /// A gpu.* call answers within <paramref name="limit"/>: a driver call that hangs (NvAPI, ADLX) leaves
    /// its thread waiting, but the page gets a plain line instead of waiting forever.
    /// </summary>
    private static async Task<object> GpuDeadlineAsync(Task<object> work, TimeSpan limit, CancellationToken ct)
    {
        try { return await work.WaitAsync(limit, ct).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            Helpers.AppLog.Warn($"GPU: the driver did not answer within {limit.TotalSeconds:0} s.");
            throw new InvalidOperationException("Your graphics driver didn't answer. Try again.");
        }
    }

    // The System room's header and its profile tab both read the profile as it shows, and each read is a whole driver
    // session (half a second or more): the second waited out the first. One read in flight answers both.
    private readonly Lock _profileReadGate = new();
    private Task<GpuProfileService.State>? _profileRead;

    private Task<GpuProfileService.State> SharedProfileRead()
    {
        lock (_profileReadGate)
        {
            if (_profileRead is { IsCompleted: false } running) return running;
            return _profileRead = Task.Run(GpuProfileService.Read);
        }
    }

    private async Task<object> GpuProfileAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        var action = ReadString(p, hasParams, "action");
        var gsync = ReadBool(p, hasParams, "gsync") == true;
        var antiLag = ReadString(p, hasParams, "antiLag");
        var lowLatency = ReadString(p, hasParams, "lowLatency");
        var state = action is "apply" or "undo"
            ? await Task.Run(() => action == "apply" ? GpuProfileService.Apply(gsync, antiLag, lowLatency) : GpuProfileService.Undo(), ct).ConfigureAwait(false)
            : await SharedProfileRead().WaitAsync(ct).ConfigureAwait(false);
        return new
        {
            ok = state.Ok,
            message = state.Message,
            vendor = state.Vendor,
            gpu = state.Gpu,
            series = state.Series,
            screen = new { name = state.Screen.Name, hz = state.Screen.RefreshHz, vrr = state.Screen.Vrr },
            status = state.Status,
            gsync = state.Gsync,
            canUndo = state.CanUndo,
            driverUpdated = state.DriverUpdated,
            items = state.Manual.Select(item => new { key = item.Key, label = item.Label, value = item.Value, why = item.Why, current = item.Current }).ToList(),
            antiLag = state.AntiLag,
            lowLatency = state.LowLatency,
            summary = state.Summary,
        };
    }

    /// <summary>The main AMD GPU's sensors now (board power and fan included); ok false (nothing shown) on other GPUs or when ADLX can't read them.</summary>
    private static async Task<object> GpuSensorsAsync(CancellationToken ct)
    {
        var sensors = await Task.Run(() => GraphicsService.Vendor() == "amd" ? Helpers.AmdAdlx.ReadSensors() : null, ct).ConfigureAwait(false);
        if (sensors is null) return new { ok = false };
        return new
        {
            ok = true,
            temperature = sensors.TemperatureC,
            hotspot = sensors.HotspotC,
            watts = sensors.Watts,
            clock = sensors.ClockMhz,
            vramUsed = sensors.VramUsedMb,
            vramTotal = sensors.VramTotalMb,
            fan = sensors.FanRpm,
            usage = sensors.UsagePercent,
        };
    }

    /// <summary>
    /// Game sheet > driver frame generation: on a Radeon, Fluid Motion Frames (supported, on for this game, whether AMD
    /// Software runs); on Intel, XeSS frame generation's override (supported, the game's choice).
    /// </summary>
    private async Task<object> GpuGameAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        var game = await FindGameAsync(ReadString(p, hasParams, "id"), ct).ConfigureAwait(false);
        if (game is null) return new { ok = false, message = "Game not found." };
        if (await Task.Run(() => GraphicsService.Vendor(), ct).ConfigureAwait(false) == "intel")
        {
            var intel = IntelProfileService.Shared;
            if (ReadString(p, hasParams, "frameGen") is { } choice && !await Task.Run(() => intel.SetFrameGen(game.Id, choice), ct).ConfigureAwait(false))
                return new { ok = false, message = "Could not save." };
            var (canGenerate, frameGen) = await Task.Run(() => intel.FrameGen(game.Id), ct).ConfigureAwait(false);
            return new { ok = true, vendor = "intel", supported = canGenerate, frameGen };
        }
        var radeon = RadeonProfileService.Shared;
        if (ReadBool(p, hasParams, "afmf") is { } on && !await Task.Run(() => radeon.SetAfmf(game.Id, on), ct).ConfigureAwait(false))
            return new { ok = false, message = "Could not save." };
        var (supported, afmf, amdSoftware) = await Task.Run(() => radeon.Afmf(game.Id), ct).ConfigureAwait(false);
        return new { ok = true, vendor = "amd", supported, afmf, amdSoftware };
    }

    private async Task<object> GpuImportAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        static object? Undo() => GraphicsService.ImportUndo() is { } undo ? new { profile = undo.Profile, count = undo.Count } : null;
        var action = ReadString(p, hasParams, "action");
        switch (action)
        {
            case "pick":
                var path = await PickProfileFileAsync().ConfigureAwait(false);
                if (path is null) return new { ok = false, cancelled = true, undo = Undo() };
                var preview = await Task.Run(() => GraphicsService.PreviewImport(path), ct).ConfigureAwait(false);
                return new
                {
                    ok = preview.Ok,
                    cancelled = false,
                    message = preview.Message,
                    preview = preview.Ok ? new
                    {
                        profile = preview.ProfileName,
                        items = preview.Items.Select(item => new { label = item.Label, value = item.Value }).ToList(),
                        unknown = preview.Unknown,
                        leftOut = preview.LeftOut + preview.NotInDriver,
                    } : null,
                    undo = Undo(),
                };
            case "apply":
            case "undo":
                var done = await Task.Run(() => action == "apply" ? GraphicsService.ApplyImport() : GraphicsService.UndoImport(), ct).ConfigureAwait(false);
                return new { ok = done.Ok, message = done.Message, undo = Undo() };
            case "cancel":
                GraphicsService.CancelImport();
                return new { ok = true, undo = Undo() };
            case "state":
                return new { ok = true, undo = Undo() };
            default:
                return new { ok = false, message = "Unknown action." };
        }
    }

    /// <summary>A .nip file picked in Windows' file picker, or null.</summary>
    private async Task<string?> PickProfileFileAsync()
    {
        var tcs = new TaskCompletionSource<string?>();
        async Task Run()
        {
            try
            {
                if (App.MainAppWindow is not { } window) { tcs.TrySetResult(null); return; }
                var picker = new Windows.Storage.Pickers.FileOpenPicker();
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
                picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads;
                picker.ViewMode = Windows.Storage.Pickers.PickerViewMode.List;
                picker.FileTypeFilter.Add(".nip");
                var file = await picker.PickSingleFileAsync();
                tcs.TrySetResult(file?.Path);
            }
            catch (Exception ex)
            {
                Helpers.AppLog.Warn("Profile picker failed: " + ex.Message);
                tcs.TrySetResult(null);
            }
        }
        if (!_queue.HasThreadAccess) _queue.TryEnqueue(() => _ = Run());
        else _ = Run();
        return await tcs.Task.ConfigureAwait(false);
    }

    // Set once: the first two gpu.driver calls (the header and the driver row) run on the pool together.
    private Action<NvidiaDriverService.Progress>? _driverEvents;

    /// <summary>
    /// System > Graphics > Driver and profile's driver row (<see cref="NvidiaDriverService"/>): "state" checks NVIDIA's newest Game Ready
    /// driver for this card against the installed one; "update" (after the user's consent) starts the
    /// driver-only clean install, its progress arriving as gpu.driver events. A Radeon or an Intel GPU gets the
    /// same row from <see cref="GpuDriverUpdate"/>, for the vendor whose GPU Exo talks to (else the first one Windows lists).
    /// </summary>
    private async Task<object> GpuDriverAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        Action<NvidiaDriverService.Progress> post = progress => PostEvent("gpu.driver", DriverProgress(progress));
        if (Interlocked.CompareExchange(ref _driverEvents, post, null) is null)
        {
            NvidiaDriverService.Changed += post;
            GpuDriverUpdate.Changed += post;
        }
        // While an update runs the row stays on its vendor: mid-install, Windows may list no GPU of that vendor.
        var vendor = GpuDriverUpdate.Updating
            ?? (NvidiaDriverService.Current.Stage is "downloading" or "unpacking" or "waiting" or "installing" ? "nvidia" : null)
            ?? GraphicsService.Vendor() ?? GraphicsService.Vendors(GpuCapability.Adapters()).FirstOrDefault();
        if (vendor is "amd" or "intel") return await VendorDriverAsync(vendor, ReadString(p, hasParams, "action") == "update", ct).ConfigureAwait(false);
        if (vendor != "nvidia") return new { ok = true, vendor = (string?)null };
        var card = NvidiaDriverService.Card();
        var workstation = card is not null && NvidiaDriverService.Workstation(card);
        var update = ReadString(p, hasParams, "action") == "update";
        var latest = await NvidiaDriverService.LatestAsync(ReadString(p, hasParams, "branch") == "studio" ? "studio" : "game", ct, fresh: update).ConfigureAwait(false);
        string? message = null;
        if (update)
            message = latest is null ? "NVIDIA's driver list couldn't be reached." : NvidiaDriverService.Start(latest);
        var installed = NvidiaDriverService.Installed();
        return new
        {
            ok = message is null,
            message,
            vendor,
            card,
            workstation,
            installed,
            latest = latest is null ? null : new { version = latest.Version, size = latest.Size, released = latest.Released, kind = latest.Kind },
            update = latest is not null && installed is not null && NvidiaDriverService.Compare(latest.Version, installed) > 0,
            blocked = NvidiaDriverService.Blocked(),
            progress = DriverProgress(NvidiaDriverService.Current),
        };
    }

    /// <summary>The row on a Radeon or an Intel GPU: AMD's or Intel's newest driver for the card (no Game Ready or Studio choice).</summary>
    private static async Task<object> VendorDriverAsync(string vendor, bool update, CancellationToken ct)
    {
        var card = GpuDriverUpdate.Card(vendor);
        var latest = await GpuDriverUpdate.LatestAsync(vendor, ct, fresh: update).ConfigureAwait(false);
        string? message = null;
        if (update) message = latest is null ? $"{(vendor == "amd" ? "AMD" : "Intel")}'s newest driver couldn't be found." : GpuDriverUpdate.Start(latest);
        var installed = card is null ? null : GpuDriverUpdate.Installed(vendor, card);
        return new
        {
            ok = message is null,
            message,
            vendor,
            card,
            workstation = false,
            installed,
            latest = latest is null ? null : new { version = latest.Version, size = latest.Size, released = latest.Released, kind = latest.Kind, page = latest.Page },
            update = latest is not null && GpuDriverUpdate.Newer(latest.Version, installed),
            blocked = GpuDriverUpdate.Blocked(),
            progress = DriverProgress(GpuDriverUpdate.Current),
        };
    }

    private static object DriverProgress(NvidiaDriverService.Progress progress) =>
        new { stage = progress.Stage, percent = progress.Percent, message = progress.Message };
}
