using System.Diagnostics;
using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// What the controller's page changes on the controller (<see cref="ControllerLab"/> Pad, <see cref="EdgeProfiles"/>),
/// over USB, never while a game runs or another controller program (DS4Windows, DSX, reWASD) is running, one change at
/// a time:
/// - controller.calibrate: the controller's own stick calibration. center (sticks left alone), range-begin and range-end
///   (both sticks rolled around their edge between them; Exo checks they were before it ends), range-cancel, keep (a
///   DualSense keeps it after unplugging), undo (the values it had before Exo first calibrated it), and reapply (an Edge,
///   whose sticks hold their own calibration, gets Exo's back each time it connects);
/// - controller.profiles, controller.profileSave, controller.profileClear, controller.profileUndo: an Edge's slots 2-4.
/// Before its first change to a controller Exo saves what the controller had, so Undo puts it back exactly.
/// </summary>
public sealed partial class WebHostBridge
{
    private readonly SemaphoreSlim _padGate = new(1, 1);
    private RangeRun? _range;
    private readonly HashSet<string> _reapplied = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A range calibration under way: when it began, and the sticks' positions since, for the check before it ends.</summary>
    private sealed class RangeRun(string id, ushort[]? before)
    {
        internal string Id { get; } = id;
        internal ushort[]? Before { get; } = before;
        internal long Started { get; } = Stopwatch.GetTimestamp();
        internal List<(byte, byte)> Left { get; } = [];
        internal List<(byte, byte)> Right { get; } = [];
        internal CancellationTokenSource Listening { get; } = new();
    }

    /// <summary>Why Exo won't change the controller now; null when it may.</summary>
    private string? CantChange(Peripherals.Peripheral card)
    {
        if (AnyGame()) return "Close the game first: Exo never changes a controller while you play.";
        if (card.Connection != "usb") return "Connect the controller with a cable first: Exo changes it over USB only.";
        var other = ControllerLab.ReadLayers().Tools.FirstOrDefault(t => t.Running && t.Name is "DS4Windows" or "DSX" or "reWASD");
        return other is null ? null : $"Close {other.Name} first: two programs changing one controller can mix up its settings.";
    }

    private async Task<object> WithPad(JsonElement p, bool hasParams, Func<Peripherals.Peripheral, ControllerLab.Pad, object> work, CancellationToken ct)
    {
        if (ControllerCard(p, hasParams) is not { } card || !ControllerLab.Supported(card.VendorId, card.ProductId))
            return new { ok = false, message = "Exo sets up a DualSense and a DualSense Edge; this controller it reads only." };
        if (CantChange(card) is { } why) return new { ok = false, message = why };
        await _padGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var pad = ControllerLab.Pad.Open(card);
            return pad is null ? new { ok = false, message = "Exo couldn't open the controller. Unplug it and plug it back in." } : work(card, pad);
        }
        finally { _padGate.Release(); }
    }

    // ------------------------------------------------------------------ calibration

    private Task<object> ControllerCalibrateAsync(JsonElement p, bool hasParams, CancellationToken ct) => WithPad(p, hasParams, (card, pad) =>
    {
        var step = ReadString(p, hasParams, "step");
        var saved = ControllerLab.Load(card.Id);
        switch (step)
        {
            case "center" or "range-begin":
            {
                // A range left open (the page went before it ended) is closed first, the values from before it put back.
                if (_range is { } open)
                {
                    StopRange(open);
                    if (open.Id == card.Id)
                    {
                        ControllerLab.EndRange(pad);
                        if (open.Before is { } was) ControllerLab.WriteFinetune(pad, was);
                    }
                }
                // Locked keeps a calibration only until it's unplugged. One left unlocked (Sony's app can leave it so) is
                // locked first, as dualshock-tools does on every connect, so nothing Exo does here is saved by accident.
                var nv = ControllerLab.ReadNv(pad);
                if (nv == "unlocked") nv = ControllerLab.Lock(pad);
                if (nv != "locked")
                    return new { ok = false, message = "Unplug the controller and plug it back in, then try again." };
                var now = ControllerLab.ReadFinetune(pad);
                if (now is null) return new { ok = false, message = "The controller didn't say how its sticks are set. Unplug it and plug it back in." };
                if (saved.Original is null) ControllerLab.Store(card.Id, saved = saved with { Original = now });
                if (step == "center")
                {
                    if (!ControllerLab.CalibrateCenters(pad)) return new { ok = false, message = "The controller didn't take it. Leave the sticks alone and try again." };
                    return Calibrated(card, pad, saved);
                }
                if (!ControllerLab.BeginRange(pad)) return new { ok = false, message = "The controller didn't start. Try again." };
                var run = _range = new RangeRun(card.Id, now);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await ControllerLab.ListenAsync(card, TimeSpan.FromMinutes(2), (_, s) =>
                        {
                            lock (run) { run.Left.Add((s.LX, s.LY)); run.Right.Add((s.RX, s.RY)); }
                        }, run.Listening.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or IOException) { }
                });
                return new { ok = true };
            }
            case "range-end":
            {
                if (_range is not { } run || run.Id != card.Id) return new { ok = false, message = "Start the range first." };
                var seconds = Stopwatch.GetElapsedTime(run.Started).TotalSeconds;
                ControllerLab.Edge? left, right;
                lock (run) { left = ControllerLab.Rolled(run.Left); right = ControllerLab.Rolled(run.Right); }
                // Ending before both sticks went all the way round would pin them to the corners: say so instead.
                if (seconds < 8 || left is not { Covered: >= 0.9 } || right is not { Covered: >= 0.9 })
                    return new { ok = false, more = true, message = "Keep rolling both sticks slowly around their edge.", left = left?.Covered ?? 0, right = right?.Covered ?? 0 };
                StopRange(run);
                if (!ControllerLab.EndRange(pad)) return new { ok = false, message = "The controller didn't take it. Unplug it and plug it back in: it goes back to how it was." };
                return Calibrated(card, pad, saved);
            }
            case "range-cancel":
            {
                if (_range is not { } run || run.Id != card.Id) return new { ok = true };
                StopRange(run);
                // The range can't be left open: end it, then put back the values from before it began.
                ControllerLab.EndRange(pad);
                if (run.Before is { } before) ControllerLab.WriteFinetune(pad, before);
                return new { ok = true };
            }
            case "keep":
            {
                if (pad.Edge) return new { ok = false, message = "An Edge's sticks keep their own calibration; Exo puts it back each time it connects instead." };
                if (saved.Calibrated is null) return new { ok = false, message = "Calibrate it first." };
                if (Charge(card) is < 30) return new { ok = false, message = "Charge the controller to 30% first: if the cable came out while it saves with a flat battery, the controller could stop working." };
                if (!ControllerLab.Keep(pad)) return new { ok = false, message = "The controller didn't keep it. It still has it until you unplug it." };
                ControllerLab.Store(card.Id, saved with { Kept = true });
                AppLog.Info("Controller: calibration kept on the DualSense.");
                return new { ok = true };
            }
            case "undo":
            {
                if (saved.Original is not { } original) return new { ok = true };
                if (!ControllerLab.WriteFinetune(pad, original)) return new { ok = false, message = "The controller didn't take it. Try again." };
                if (saved.Kept && !ControllerLab.Keep(pad)) return new { ok = false, message = "The controller has its old calibration until you unplug it, but didn't keep it. Try again." };
                ControllerLab.Store(card.Id, saved with { Calibrated = null, Reapply = false, Kept = false });
                AppLog.Info("Controller: calibration put back as it was.");
                return new { ok = true };
            }
            case "reapply-on" or "reapply-off":
                ControllerLab.Store(card.Id, saved with { Reapply = step == "reapply-on" });
                return new { ok = true };
            default:
                return new { ok = false, message = "Not a calibration step." };
        }
    }, ct);

    /// <summary>After a calibration: what it left, kept for Undo and, on an Edge, put back each time it connects.</summary>
    private static object Calibrated(Peripherals.Peripheral card, ControllerLab.Pad pad, ControllerLab.Saved saved)
    {
        var values = ControllerLab.ReadFinetune(pad);
        ControllerLab.Store(card.Id, saved with { Calibrated = values ?? saved.Calibrated, Reapply = pad.Edge || saved.Reapply });
        AppLog.Info("Controller: sticks calibrated (" + (pad.Edge ? "Edge, put back on each connect" : "until unplugged") + ").");
        return new { ok = true };
    }

    private void StopRange(RangeRun run)
    {
        run.Listening.Cancel();
        if (ReferenceEquals(_range, run)) _range = null;
    }

    /// <summary>The controller's charge from one input report, %; null when it doesn't say in time.</summary>
    private static int? Charge(Peripherals.Peripheral card)
    {
        int? charge = null;
        using var stop = new CancellationTokenSource();
        try
        {
            ControllerLab.ListenAsync(card, TimeSpan.FromSeconds(1), (_, s) =>
            {
                if (s.Battery is { } b) { charge = b; stop.Cancel(); }
            }, stop.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) { }
        return charge;
    }

    /// <summary>What the page shows about calibration: whether Exo calibrated it, kept it, and puts it back on connect.</summary>
    private static object CalibrationState(Peripherals.Peripheral card)
    {
        var saved = ControllerLab.Load(card.Id);
        return new { calibrated = saved.Calibrated is not null, kept = saved.Kept, reapply = saved.Reapply, canUndo = saved.Original is not null && saved.Calibrated is not null };
    }

    private void ReapplyCalibrationsAfter(Models.GameEntry _) => ReapplyCalibrationsSoon();

    /// <summary>
    /// An Edge Exo calibrated gets its calibration back when it connects (its stick modules keep their own, so Exo's lasts
    /// only until it's unplugged): once per connection, never while a game runs (it gets it when the game closes).
    /// </summary>
    private void ReapplyCalibrationsSoon() => _ = Task.Run(async () =>
    {
        if (AnyGame()) return;
        await _padGate.WaitAsync(_bridgeLifetime.Token).ConfigureAwait(false);
        try
        {
            var edges = Peripherals.List(AnyGame).Where(c => c is { Connection: "usb", ProductId: ControllerLab.DualSenseEdge } && ControllerLab.Supported(c.VendorId, c.ProductId) && c.Source is not null).ToList();
            lock (_reapplied) _reapplied.IntersectWith(edges.Select(c => c.Source!));
            foreach (var card in edges)
            {
                lock (_reapplied) if (_reapplied.Contains(card.Source!)) continue;
                if (ControllerLab.Load(card.Id) is not { Reapply: true, Calibrated: { } values } || CantChange(card) is not null) continue;
                using var pad = ControllerLab.Pad.Open(card);
                if (pad is null || !ControllerLab.WriteFinetune(pad, values)) continue;
                lock (_reapplied) _reapplied.Add(card.Source!);
                AppLog.Info("Controller: the Edge's calibration put back.");
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException) { AppLog.Warn("Controller: calibration not put back: " + ex.Message); }
        finally { _padGate.Release(); }
    });

    // ------------------------------------------------------------------ an Edge's profiles

    private Task<object> ControllerProfilesAsync(JsonElement p, bool hasParams, CancellationToken ct) => WithPad(p, hasParams, (card, pad) =>
        pad.Edge ? ProfilesOf(card, pad) : new { ok = false, message = "Only a DualSense Edge holds profiles." }, ct);

    private object ProfilesOf(Peripherals.Peripheral card, ControllerLab.Pad pad)
    {
        var saved = ControllerLab.Load(card.Id);
        var list = new List<object>();
        for (var slot = 1; slot <= 4; slot++)
        {
            if (ControllerLab.ReadSlot(pad, slot) is not { } c || EdgeProfiles.Parse(slot, c[0], c[1], c[2]) is not { } profile)
                return new { ok = false, message = "The controller didn't give its profiles. Unplug it and plug it back in." };
            var (stored, computed) = EdgeProfiles.Checksums(c[0], c[1], c[2]);
            list.Add(ProfileView(profile, intact: !profile.Assigned || stored == computed, canUndo: saved.Slots.ContainsKey(slot.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        }
        return new { ok = true, profiles = list };
    }

    private static object ProfileView(EdgeProfiles.Profile p, bool intact, bool canUndo) => new
    {
        slot = p.Slot, assigned = p.Assigned, intact, canUndo, name = p.Name, id = Convert.ToHexString(p.UniqueId),
        left = StickView(p.Left), right = StickView(p.Right), l2 = p.L2, r2 = p.R2, unifiedTriggers = p.UnifiedTriggers,
        vibration = p.Vibration, triggerEffects = p.TriggerEffects, buttons = p.Buttons.Select(b => (int)b).ToArray(), disabled = p.Disabled, updated = p.Updated,
    };

    private static object StickView(EdgeProfiles.Stick s) => new
    {
        preset = s.Preset, points = s.Points.Select(b => (int)b).ToArray(),
        deadzone = EdgeProfiles.DeadzoneOf(s.Points), adjustment = EdgeProfiles.AdjustmentOf(s.Preset, s.Points),
    };

    private Task<object> ControllerProfileSaveAsync(JsonElement p, bool hasParams, CancellationToken ct) => WithPad(p, hasParams, (card, pad) =>
    {
        if (!pad.Edge) return new { ok = false, message = "Only a DualSense Edge holds profiles." };
        EdgeProfiles.Profile profile;
        byte[][] chunks;
        try
        {
            profile = ProfileFrom(p.GetProperty("profile"));
            chunks = EdgeProfiles.Build(profile, DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return new { ok = false, message = ex.Message };
        }
        if (!BackUpSlot(card, pad, profile.Slot)) return new { ok = false, message = "The controller didn't give the slot's profile to keep first. Nothing changed." };
        if (!ControllerLab.WriteSlot(pad, profile.Slot, chunks)) return new { ok = false, message = "The controller didn't take it. Try again." };
        Thread.Sleep(300);
        // Read back: what the controller holds now must be what was written, its CRC intact.
        if (ControllerLab.ReadSlot(pad, profile.Slot) is not { } back || EdgeProfiles.Parse(profile.Slot, back[0], back[1], back[2]) is not { Assigned: true } kept
            || EdgeProfiles.Checksums(back[0], back[1], back[2]) is var (stored, computed) && stored != computed || kept.Name != profile.Name.Trim())
            return new { ok = false, message = "The controller didn't keep it as written. Undo puts the slot back." };
        AppLog.Info($"Controller: Edge profile saved in slot {profile.Slot}.");
        return ProfilesOf(card, pad);
    }, ct);

    private Task<object> ControllerProfileClearAsync(JsonElement p, bool hasParams, CancellationToken ct) => WithPad(p, hasParams, (card, pad) =>
    {
        var slot = (int)(ReadDouble(p, hasParams, "slot") ?? 0);
        if (!pad.Edge || slot is < 2 or > 4) return new { ok = false, message = "Only slots 2 to 4 can be cleared." };
        if (!BackUpSlot(card, pad, slot)) return new { ok = false, message = "The controller didn't give the slot's profile to keep first. Nothing changed." };
        if (!ControllerLab.ClearSlot(pad, slot)) return new { ok = false, message = "The controller didn't clear it. Try again." };
        Thread.Sleep(300);
        AppLog.Info($"Controller: Edge profile slot {slot} cleared.");
        return ProfilesOf(card, pad);
    }, ct);

    private Task<object> ControllerProfileUndoAsync(JsonElement p, bool hasParams, CancellationToken ct) => WithPad(p, hasParams, (card, pad) =>
    {
        var slot = (int)(ReadDouble(p, hasParams, "slot") ?? 0);
        var key = slot.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var saved = ControllerLab.Load(card.Id);
        if (!pad.Edge || slot is < 2 or > 4 || !saved.Slots.TryGetValue(key, out var hex)) return new { ok = false, message = "There's nothing to put back." };
        var chunks = hex.Select(Convert.FromHexString).ToArray();
        var before = EdgeProfiles.Parse(slot, chunks[0], chunks[1], chunks[2]);
        var done = before is { Assigned: true }
            // The same bytes, sent under the slot's write ID: the CRC doesn't cover the ID, so it still holds.
            ? ControllerLab.WriteSlot(pad, slot, [.. chunks.Select(c => { var copy = (byte[])c.Clone(); copy[0] = EdgeProfiles.WriteIds(slot).Write; return copy; })])
            : ControllerLab.ClearSlot(pad, slot);
        if (!done) return new { ok = false, message = "The controller didn't take it. Try again." };
        saved.Slots.Remove(key);
        ControllerLab.Store(card.Id, saved);
        Thread.Sleep(300);
        AppLog.Info($"Controller: Edge profile slot {slot} put back.");
        return ProfilesOf(card, pad);
    }, ct);

    /// <summary>A preset's curve for the editor's picture: no controller involved.</summary>
    private static object ControllerCurve(JsonElement p, bool hasParams)
    {
        var preset = (int)(ReadDouble(p, hasParams, "preset") ?? 0);
        if (preset is < 0 or > 5) return new { ok = false };
        return new { ok = true, points = EdgeProfiles.Curve(preset, ReadDouble(p, hasParams, "deadzone") ?? 0, (int)(ReadDouble(p, hasParams, "adjustment") ?? 0)).Select(b => (int)b).ToArray() };
    }

    /// <summary>Keeps the slot's bytes as the controller has them before Exo's first change to it.</summary>
    private static bool BackUpSlot(Peripherals.Peripheral card, ControllerLab.Pad pad, int slot)
    {
        var saved = ControllerLab.Load(card.Id);
        var key = slot.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (saved.Slots.ContainsKey(key)) return true;
        if (ControllerLab.ReadSlot(pad, slot) is not { } chunks) return false;
        saved.Slots[key] = [.. chunks.Select(Convert.ToHexString)];
        ControllerLab.Store(card.Id, saved);
        return true;
    }

    /// <summary>A profile as the page sends it, checked field by field (Build checks the rest).</summary>
    private static EdgeProfiles.Profile ProfileFrom(JsonElement j)
    {
        // A preset's curve is made here from its deadzone and adjustment, as the controller's own settings make it; a
        // custom curve keeps its points.
        static EdgeProfiles.Stick Stick(JsonElement s)
        {
            var preset = s.GetProperty("preset").GetInt32();
            return preset == EdgeProfiles.Custom
                ? new(preset, Bytes(s.GetProperty("points"), 8))
                : new(preset, EdgeProfiles.Curve(preset, s.GetProperty("deadzone").GetDouble(), s.GetProperty("adjustment").GetInt32()));
        }
        static byte[] Bytes(JsonElement a, int count)
        {
            var values = a.EnumerateArray().Select(v => v.GetInt32()).ToArray();
            if (values.Length != count || values.Any(v => v is < 0 or > 255)) throw new ArgumentException("A value is out of range.");
            return [.. values.Select(v => (byte)v)];
        }
        static int[] Range(JsonElement a) => [.. a.EnumerateArray().Select(v => v.GetInt32())];
        var id = j.TryGetProperty("id", out var idEl) && idEl.GetString() is { Length: 32 } hex ? Convert.FromHexString(hex) : Guid.NewGuid().ToByteArray();
        return new EdgeProfiles.Profile(
            j.GetProperty("slot").GetInt32(), true, j.GetProperty("name").GetString() ?? "", id,
            Stick(j.GetProperty("left")), Stick(j.GetProperty("right")), Range(j.GetProperty("l2")), Range(j.GetProperty("r2")),
            j.GetProperty("unifiedTriggers").GetBoolean(), j.GetProperty("vibration").GetString() ?? "", j.GetProperty("triggerEffects").GetString() ?? "",
            Bytes(j.GetProperty("buttons"), 16), j.GetProperty("disabled").GetUInt32(), 0);
    }
}
