using System.Text.Json;

namespace ExoLauncher.Services;

/// <summary>
/// System > Graphics > Driver and profile. gpu.profile: Exo's profile for this PC (no
/// action), apply (with gsync: the G-SYNC or No G-SYNC variant) or undo.
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

    private async Task<object> GpuProfileAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        var action = ReadString(p, hasParams, "action");
        var gsync = ReadBool(p, hasParams, "gsync") == true;
        var state = await Task.Run(() => action switch
        {
            "apply" => GpuProfileService.Apply(gsync),
            "undo" => GpuProfileService.Undo(),
            _ => GpuProfileService.Read(),
        }, ct).ConfigureAwait(false);
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
        };
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
    /// driver-only clean install, its progress arriving as gpu.driver events.
    /// </summary>
    private async Task<object> GpuDriverAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        Action<NvidiaDriverService.Progress> post = progress => PostEvent("gpu.driver", DriverProgress(progress));
        if (Interlocked.CompareExchange(ref _driverEvents, post, null) is null) NvidiaDriverService.Changed += post;
        if (GraphicsService.Vendor() != "nvidia") return new { ok = true, nvidia = false };
        var card = NvidiaDriverService.Card();
        var workstation = card is not null && NvidiaDriverService.Workstation(card);
        var latest = await NvidiaDriverService.LatestAsync(ReadString(p, hasParams, "branch") == "studio" ? "studio" : "game", ct).ConfigureAwait(false);
        string? message = null;
        if (ReadString(p, hasParams, "action") == "update")
            message = latest is null ? "NVIDIA's driver list couldn't be reached." : NvidiaDriverService.Start(latest);
        var installed = NvidiaDriverService.Installed();
        return new
        {
            ok = message is null,
            message,
            nvidia = true,
            card,
            workstation,
            installed,
            latest = latest is null ? null : new { version = latest.Version, size = latest.Size, released = latest.Released, kind = latest.Kind },
            update = latest is not null && installed is not null && NvidiaDriverService.Compare(latest.Version, installed) > 0,
            blocked = NvidiaDriverService.Blocked(),
            progress = DriverProgress(NvidiaDriverService.Current),
        };
    }

    private static object DriverProgress(NvidiaDriverService.Progress progress) =>
        new { stage = progress.Stage, percent = progress.Percent, message = progress.Message };
}
