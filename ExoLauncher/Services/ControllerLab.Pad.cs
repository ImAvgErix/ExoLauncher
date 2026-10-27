using System.Text.Json;
using ExoLauncher.Helpers;
using Microsoft.Win32.SafeHandles;
using static ExoLauncher.Services.Hid.HidInterop;

namespace ExoLauncher.Services;

/// <summary>
/// What Exo writes to a DualSense or DualSense Edge, over USB only and never while a game runs (the bridge checks): its
/// sticks' calibration (the controller's own center and range routines, kept until unplugged; kept for good on a
/// DualSense only when asked), its fine-tune values put back, and an Edge's profiles in slots 2-4. Every report goes
/// through <see cref="Pad"/>, which sends nothing <see cref="Allowed"/> refuses. Before Exo first changes a controller
/// it saves what the controller had (fine-tune values, each profile slot's bytes) in its own file, so Undo puts it back.
/// </summary>
internal static partial class ControllerLab
{
    /// <summary>A controller's control handle for feature reports.</summary>
    internal sealed class Pad : IDisposable
    {
        private readonly SafeFileHandle _handle;
        private readonly int _length;
        internal bool Edge { get; }

        private Pad(SafeFileHandle handle, int length, bool edge) => (_handle, _length, Edge) = (handle, length, edge);

        /// <summary>The controller's gamepad interface, opened for feature reports; null off USB or when it won't open.</summary>
        internal static Pad? Open(Peripherals.Peripheral card)
        {
            if (card.Connection != "usb" || !Supported(card.VendorId, card.ProductId)
                || card.Collections.FirstOrDefault(c => c is { Page: 0x01, Usage: 0x05 }) is not { } pad || Peripherals.InterfacePath(pad.Id) is not { } path) return null;
            var handle = CreateFile(path, GenericRead | GenericWrite, FileShareRead | FileShareWrite, 0, OpenExisting, 0, 0);
            var length = handle.IsInvalid ? 0 : FeatureLength(handle);
            if (length < 64) { handle.Dispose(); return null; }
            return new Pad(handle, length, card.ProductId == DualSenseEdge);
        }

        private static int FeatureLength(SafeFileHandle handle)
        {
            if (!HidD_GetPreparsedData(handle, out var preparsed)) return 0;
            try { return HidP_GetCaps(preparsed, out var caps) == HidpStatusSuccess ? caps.FeatureReportByteLength : 0; }
            finally { HidD_FreePreparsedData(preparsed); }
        }

        /// <summary>A feature report the controller holds (its ID at [0]); null when refused or it doesn't answer.</summary>
        internal byte[]? Get(byte id)
        {
            if (!Allowed(id, [], set: false, Edge)) throw new InvalidOperationException($"Exo doesn't ask a controller for report 0x{id:X2}.");
            var buffer = new byte[_length];
            buffer[0] = id;
            return HidD_GetFeature(_handle, buffer, buffer.Length) ? buffer : null;
        }

        /// <summary>Sends a feature report: its ID and the command after it, padded with zeros.</summary>
        internal bool Set(byte id, ReadOnlySpan<byte> payload)
        {
            if (!Allowed(id, payload, set: true, Edge)) throw new InvalidOperationException($"Exo doesn't send a controller report 0x{id:X2} with that.");
            var buffer = new byte[_length];
            buffer[0] = id;
            payload.CopyTo(buffer.AsSpan(1));
            return HidD_SetFeature(_handle, buffer, buffer.Length);
        }

        /// <summary>A test-channel command (0x80) and its answer (0x81, 81 device action 02), asked for every 50 ms for a second.</summary>
        internal byte[]? Command(ReadOnlySpan<byte> command)
        {
            if (!Set(0x80, command)) return null;
            for (var i = 0; i < 20; i++)
            {
                Thread.Sleep(50);
                if (Get(0x81) is { } reply && reply[1] == command[0] && reply[2] == command[1] && reply[3] == 0x02) return reply;
            }
            return null;
        }

        /// <summary>A calibration step (0x82) and the controller's answer (0x83); 0 when it doesn't answer.</summary>
        internal uint Step(ReadOnlySpan<byte> step) => Set(0x82, step) && Get(0x83) is { } reply ? Answer(reply) : 0;

        public void Dispose() => _handle.Dispose();
    }

    // ------------------------------------------------------------------ calibration

    internal static ushort[]? ReadFinetune(Pad pad) => pad.Command(FinetuneRead(pad.Edge)) is { } reply ? Finetune(reply) : null;

    internal static bool WriteFinetune(Pad pad, IReadOnlyList<ushort> values) => pad.Set(0x80, FinetunePayload(values));

    /// <summary>locked (a calibration lasts until unplugged), unlocked (it is saved), reconnect, or unknown.</summary>
    internal static string ReadNv(Pad pad) => pad.Set(0x80, NvStatus) && WaitFor(pad, 0x81) is { } reply ? NvState(reply) : "unknown";

    // The NVS status answers from byte 1 with its own code, not "device action 02", so it is read as it comes.
    private static byte[]? WaitFor(Pad pad, byte id)
    {
        Thread.Sleep(50);
        return pad.Get(id);
    }

    /// <summary>The controller's own center calibration: sticks left alone, five samples a tenth of a second apart.</summary>
    internal static bool CalibrateCenters(Pad pad)
    {
        if (pad.Step(CenterBegin) != 0x83010101) return false;
        for (var i = 0; i < 5; i++)
        {
            Thread.Sleep(100);
            if (pad.Step(CenterSample) != 0x83010101) return false;
        }
        return End(pad, CenterEnd, target: 1);
    }

    internal static bool BeginRange(Pad pad) => pad.Step(RangeBegin) == 0x83010201;

    internal static bool EndRange(Pad pad) => End(pad, RangeEnd, target: 2);

    private static bool End(Pad pad, ReadOnlySpan<byte> step, int target)
    {
        var first = pad.Step(step);
        if (!pad.Edge) return Ended(first, target, edge: false, second: false);
        return Ended(first, target, edge: true, second: false) && Ended(pad.Step(step), target, edge: true, second: true);
    }

    /// <summary>Locks NVS (so a calibration lasts only until unplugged) and says what it is after.</summary>
    internal static string Lock(Pad pad)
    {
        pad.Command(NvLock);
        Thread.Sleep(200);
        return ReadNv(pad);
    }

    /// <summary>Keeps a DualSense's calibration after it's unplugged: NVS unlocked, then locked again (dualshock-tools' "save").</summary>
    internal static bool Keep(Pad pad)
    {
        if (pad.Edge || pad.Command(NvUnlock) is null) return false;
        Thread.Sleep(500);
        return Lock(pad) == "locked";
    }

    // ------------------------------------------------------------------ an Edge's profiles

    /// <summary>A slot's three chunks as the controller holds them; null when one doesn't come.</summary>
    internal static byte[][]? ReadSlot(Pad pad, int slot)
    {
        var first = EdgeProfiles.ReadId(slot);
        var chunks = new byte[3][];
        for (var i = 0; i < 3; i++)
        {
            if (pad.Get((byte)(first + i)) is not { } chunk) return null;
            chunks[i] = chunk;
            Thread.Sleep(20);
        }
        return chunks;
    }

    /// <summary>Writes three chunks to a slot (2-4) and reads the slot's finishing report, as Sony's own app does.</summary>
    internal static bool WriteSlot(Pad pad, int slot, byte[][] chunks)
    {
        var (write, done) = EdgeProfiles.WriteIds(slot);
        foreach (var chunk in chunks)
        {
            if (!pad.Set(write, chunk.AsSpan(1, 63))) return false;
            Thread.Sleep(20);
        }
        Thread.Sleep(100);
        return pad.Get(done) is not null;
    }

    internal static bool ClearSlot(Pad pad, int slot) => pad.Set(0x68, [EdgeProfiles.DeleteIndex(slot)]);

    // ------------------------------------------------------------------ what Exo keeps about a controller

    /// <summary>
    /// What Exo saved before it first changed this controller, and what it set: the fine-tune values the controller had
    /// (Original), the ones Exo's calibration left (Calibrated, put back when an Edge reconnects if Reapply), whether a
    /// DualSense kept it for good (Kept), and each profile slot's bytes before Exo's last change to it (hex, by slot).
    /// </summary>
    internal sealed record Saved(ushort[]? Original, ushort[]? Calibrated, bool Reapply, bool Kept, Dictionary<string, string[]> Slots)
    {
        internal static Saved Empty => new(null, null, false, false, []);
    }

    private static string SavedPath(string id) =>
        Path.Combine(PathHelper.AppDataDir, "controllers", string.Concat(id.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_')) + ".json");

    private static readonly JsonSerializerOptions SavedJson = new() { WriteIndented = true };

    internal static Saved Load(string id)
    {
        try { return File.Exists(SavedPath(id)) ? JsonSerializer.Deserialize<Saved>(File.ReadAllText(SavedPath(id)), SavedJson) ?? Saved.Empty : Saved.Empty; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Warn($"Controller: couldn't read what Exo saved for it: {ex.GetType().Name}");
            return Saved.Empty;
        }
    }

    internal static void Store(string id, Saved saved)
    {
        var path = SavedPath(id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(saved, SavedJson));
        File.Move(tmp, path, overwrite: true);
    }
}
