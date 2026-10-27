using System.Text;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// A DualSense Edge's four on-board profiles (Fn + Triangle, Square, Cross, Circle), as the controller stores them:
/// three 64-byte feature reports a profile, read from 0x70-0x7B and written to 0x60-0x62 (the default, slot 1, is never
/// written). What a profile holds is applied inside the controller, so every game, every API and every PC gets it with
/// nothing added in between: each stick's response curve (at most four points in 8-bit space, the first one's X being
/// its deadzone), each trigger's range, the rumble and trigger-effect strength, what each button sends (the back
/// buttons included), which buttons are off, and whether the sticks swap.
///
/// The layout and the presets follow dualsense-tester (daidr, MIT) and the Edge profile web app (steffalon), checked
/// against the user's own Edge: its default profile's stored CRC-32 is over chunk 0 [2..59], chunk 1 [2..59] and chunk
/// 2 [2..55], kept little-endian at chunk 2 [56..59]. The ways this could go wrong, covered by EdgeProfilesTests:
/// - a profile written with a CRC the controller rejects (or one over the wrong bytes);
/// - a name cut mid-character or a curve's X going backwards;
/// - a preset's points rounded differently from the controller's own (the Edge's numbers use JavaScript's rounding);
/// - a remap to a target the controller doesn't take (paddles can be sources only).
/// </summary>
internal static class EdgeProfiles
{
    /// <summary>The chunk a slot (1-4) reads from: 0x70, 0x73, 0x76, 0x79, then the next two.</summary>
    internal static byte ReadId(int slot) => (byte)(0x70 + (slot - 1) * 3);

    /// <summary>The report a slot (2-4) is written to, and the one read after to finish it.</summary>
    internal static (byte Write, byte Done) WriteIds(int slot) => slot is >= 2 and <= 4
        ? ((byte)(0x60 + slot - 2), (byte)(0x63 + slot - 2))
        : throw new ArgumentOutOfRangeException(nameof(slot), "Only slots 2 to 4 take a profile; slot 1 is the controller's default.");

    /// <summary>What a slot's delete report (0x68) is sent with.</summary>
    internal static byte DeleteIndex(int slot) => slot is >= 2 and <= 4 ? (byte)slot
        : throw new ArgumentOutOfRangeException(nameof(slot), "Only slots 2 to 4 can be cleared.");

    internal static readonly string[] Presets = ["default", "quick", "precise", "steady", "digital", "dynamic"];
    internal const int Custom = 0xFF;
    internal static readonly string[] Strengths = ["strong", "medium", "weak", "off"];
    private static readonly byte[] Vibration = [0x00, 0x02, 0x03, 0xFF];
    private static readonly byte[] TriggerEffect = [0x00, 0x06, 0x09, 0xFF];

    /// <summary>The buttons the remap table lists, in its order (sources); a paddle is a source only.</summary>
    internal static readonly byte[] Sources = [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F];
    /// <summary>What a button can send: any face, shoulder, stick or d-pad button, Options or the touchpad.</summary>
    internal static readonly byte[] Targets = [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x10, 0x11];
    /// <summary>The disable bitmap the controller ships with: both back buttons off.</summary>
    internal const uint DefaultDisabled = (1u << 22) | (1u << 23);
    internal const int SwapSticksBit = 31;

    /// <summary>One stick: its preset (0-5, or 0xFF custom) and its curve, four (x, y) points flattened.</summary>
    internal sealed record Stick(int Preset, byte[] Points);

    /// <summary>A profile as Exo shows and edits it.</summary>
    /// <param name="L2">The trigger's range, % (0-100): where it starts and where it is fully pressed.</param>
    /// <param name="Vibration">strong, medium, weak or off.</param>
    /// <param name="Buttons">What each source button sends, in <see cref="Sources"/> order.</param>
    /// <param name="Disabled">The disable bitmap (bit 31: swap the sticks).</param>
    internal sealed record Profile(
        int Slot, bool Assigned, string Name, byte[] UniqueId, Stick Left, Stick Right,
        int[] L2, int[] R2, bool UnifiedTriggers, string Vibration, string TriggerEffects,
        byte[] Buttons, uint Disabled, long Updated);

    // ------------------------------------------------------------------ reading

    /// <summary>A profile from its three read chunks (each with its report ID at [0]); null when they aren't one profile.</summary>
    internal static Profile? Parse(int slot, ReadOnlySpan<byte> c0, ReadOnlySpan<byte> c1, ReadOnlySpan<byte> c2)
    {
        if (c0.Length < 64 || c1.Length < 64 || c2.Length < 64) return null;
        // An empty slot answers 0x10 in every chunk's [1]; a profile numbers its chunks there instead.
        var assigned = c0[1] != 0x10;
        if (assigned && (c1[1] != 0x01 || c2[1] != 0x02)) return null;
        if (!assigned) return New(slot, "") with { Assigned = false, UniqueId = new byte[16] };
        var buttons = c2.Slice(10, 16).ToArray();
        for (var i = 0; i < buttons.Length; i++) if (buttons[i] != Sources[i] && Array.IndexOf(Targets, buttons[i]) < 0) buttons[i] = Sources[i];
        var disabled = BitConverter.ToUInt32(c2.Slice(26, 4));
        var label = new byte[80];
        c0.Slice(6, 54).CopyTo(label);
        c1.Slice(2, 26).CopyTo(label.AsSpan(54));
        var name = Encoding.Unicode.GetString(label).Replace("\0", "", StringComparison.Ordinal).Trim();
        var left = new byte[8];
        c1.Slice(45, 8).CopyTo(left);
        var right = new byte[8];
        c1.Slice(54, 6).CopyTo(right);
        right[6] = c2[2];
        right[7] = c2[3];
        var unified = (c2[31] & 0x80) != 0;
        int[] l2 = [Percent(c2[4]), Percent(c2[5])];
        int[] r2 = unified ? l2 : [Percent(c2[6]), Percent(c2[7])];
        Span<byte> stamp = stackalloc byte[8];
        c2.Slice(34, 6).CopyTo(stamp);
        return new Profile(slot, true, name, c1.Slice(28, 16).ToArray(), new Stick(c2[30], left), new Stick(c2[32], right), l2, r2, unified,
            Strength(Vibration, c2[8]), Strength(TriggerEffect, c2[9]), buttons, disabled, BitConverter.ToInt64(stamp));
    }

    /// <summary>The CRC the controller keeps in chunk 2 [56..59], and the one its bytes give: equal for an intact profile.</summary>
    internal static (uint Stored, uint Computed) Checksums(ReadOnlySpan<byte> c0, ReadOnlySpan<byte> c1, ReadOnlySpan<byte> c2) =>
        (BitConverter.ToUInt32(c2.Slice(56, 4)), Checksum(c0, c1, c2));

    private static uint Checksum(ReadOnlySpan<byte> c0, ReadOnlySpan<byte> c1, ReadOnlySpan<byte> c2)
    {
        Span<byte> body = stackalloc byte[170];
        c0.Slice(2, 58).CopyTo(body);
        c1.Slice(2, 58).CopyTo(body[58..]);
        c2.Slice(2, 54).CopyTo(body[116..]);
        return Crc32.Of(body);
    }

    // ------------------------------------------------------------------ writing

    /// <summary>
    /// The three reports that write <paramref name="profile"/> to its slot (2-4), each 64 bytes with the slot's write ID
    /// at [0], its CRC filled in. Refuses what the controller can't hold: a name over 40 characters, a curve whose X goes
    /// backwards, a range outside 0-100 or ending before it starts, a remap to a paddle.
    /// </summary>
    internal static byte[][] Build(Profile profile, DateTimeOffset now)
    {
        var (id, _) = WriteIds(profile.Slot);
        var name = profile.Name.Trim();
        if (name.Length is 0 or > 40) throw new ArgumentException("A profile's name is 1 to 40 characters.", nameof(profile));
        CheckCurve(profile.Left.Points);
        CheckCurve(profile.Right.Points);
        CheckRange(profile.L2);
        CheckRange(profile.R2);
        // Each button sends itself (a back button too) or one of the targets.
        if (profile.Buttons.Length != 16 || profile.Buttons.Where((b, i) => b != Sources[i] && Array.IndexOf(Targets, b) < 0).Any())
            throw new ArgumentException("A button can send only what the controller allows.", nameof(profile));
        if (profile.UniqueId.Length != 16) throw new ArgumentException("A profile's id is 16 bytes.", nameof(profile));

        var c = new[] { new byte[64], new byte[64], new byte[64] };
        c[0][0] = c[1][0] = c[2][0] = id;
        c[0][2] = 0x01;
        c[1][1] = 0x01;
        c[2][1] = 0x02;
        var label = new byte[80];
        Encoding.Unicode.GetBytes(name, label);
        label.AsSpan(0, 54).CopyTo(c[0].AsSpan(6));
        label.AsSpan(54, 26).CopyTo(c[1].AsSpan(2));
        profile.UniqueId.CopyTo(c[1], 28);
        c[1][44] = PointCount(profile.Left.Preset);
        profile.Left.Points.CopyTo(c[1], 45);
        c[1][53] = PointCount(profile.Right.Preset);
        profile.Right.Points.AsSpan(0, 6).CopyTo(c[1].AsSpan(54));
        c[2][2] = profile.Right.Points[6];
        c[2][3] = profile.Right.Points[7];
        var r2 = profile.UnifiedTriggers ? profile.L2 : profile.R2;
        c[2][4] = Byte(profile.L2[0]);
        c[2][5] = Byte(profile.L2[1]);
        c[2][6] = Byte(r2[0]);
        c[2][7] = Byte(r2[1]);
        c[2][8] = Vibration[StrengthIndex(profile.Vibration)];
        c[2][9] = TriggerEffect[StrengthIndex(profile.TriggerEffects)];
        profile.Buttons.CopyTo(c[2], 10);
        BitConverter.TryWriteBytes(c[2].AsSpan(26, 4), profile.Disabled);
        c[2][30] = (byte)profile.Left.Preset;
        if (profile.UnifiedTriggers) c[2][31] = 0x80;
        c[2][32] = (byte)profile.Right.Preset;
        BitConverter.GetBytes(now.ToUnixTimeMilliseconds()).AsSpan(0, 6).CopyTo(c[2].AsSpan(34));
        BitConverter.TryWriteBytes(c[2].AsSpan(56, 4), Checksum(c[0], c[1], c[2]));
        return c;
    }

    /// <summary>A new profile for a slot: the controller's defaults, a fresh id.</summary>
    internal static Profile New(int slot, string name) =>
        new(slot, true, name, Guid.NewGuid().ToByteArray(), DefaultStick(), DefaultStick(), [0, 100], [0, 100], false, "strong", "strong",
            [.. Sources], DefaultDisabled, 0);

    private static Stick DefaultStick() => new(0, Curve(0, 0, 0));

    private static void CheckCurve(byte[] points)
    {
        if (points.Length != 8) throw new ArgumentException("A curve is four points.", nameof(points));
        for (var i = 2; i < 8; i += 2)
            if (points[i] < points[i - 2]) throw new ArgumentException("A curve's points go left to right.", nameof(points));
    }

    private static void CheckRange(int[] range)
    {
        if (range.Length != 2 || range[0] is < 0 or > 100 || range[1] is < 0 or > 100 || range[1] < range[0])
            throw new ArgumentException("A trigger's range is 0 to 100%, its end after its start.", nameof(range));
    }

    private static byte PointCount(int preset) => preset is 2 or 3 or Custom ? (byte)4 : (byte)3;
    private static int Percent(byte value) => (int)JsRound(value / 255.0 * 100);
    private static byte Byte(int percent) => (byte)JsRound(percent / 100.0 * 255);
    private static string Strength(byte[] table, byte value) => Array.IndexOf(table, value) is var i and >= 0 ? Strengths[i] : "strong";
    private static int StrengthIndex(string word) => Array.IndexOf(Strengths, word) is var i and >= 0 ? i
        : throw new ArgumentException("Strength is strong, medium, weak or off.", nameof(word));

    // ------------------------------------------------------------------ presets

    /// <summary>
    /// One preset point's coordinate: its base value, how far one step of the adjustment moves it, and whether the
    /// deadzone moves it (every X does; no Y does). As the PS5's own settings and dualsense-tester compute it.
    /// </summary>
    private readonly record struct Pos(double Value, double Delta = 0, bool Dead = true)
    {
        public int At(double deadzone, int adjustment)
        {
            if (Delta == 0) return Dead ? (int)JsRound(255 * deadzone + (1 - deadzone) * Value) : (int)Value;
            if (!Dead) return (int)JsRound(Value + Delta * adjustment);
            return (int)Math.Floor(JsRound(Value * (1 - deadzone) + 255 * deadzone) + Delta * adjustment);
        }
    }

    private static Pos X(double v, double d = 0) => new(v, d);
    private static Pos Y(double v, double d = 0) => new(v, d, Dead: false);

    /// <summary>Each preset's four points, and which flattened coordinate tells its adjustment back (-1: none).</summary>
    private static readonly (Pos[] Points, int Reverse)[] Shapes =
    [
        ([X(0), Y(0), X(128), Y(128), X(196), Y(196), X(225), Y(225)], -1),
        ([X(0), Y(0), X(38), Y(38), X(107, -3), Y(167, 5.5), X(255), Y(255)], 4),
        ([X(0), Y(0), X(85, 3), Y(40, -3.5), X(149, 3), Y(83, -6.5), X(206, 2), Y(140, -7.5)], 2),
        ([X(0), Y(0), X(57, -1), Y(57, -1), X(100, -4), Y(127, -0.5), X(210, 2.5), Y(152, -5.5)], 4),
        ([X(0), Y(0), X(38), Y(38), X(38), Y(165, 18), X(255), Y(255)], 5),
        ([X(0), Y(0), X(82, 2.5), Y(40, -3.5), X(161, -4.5), Y(213, 3), X(255), Y(255)], 5),
    ];

    /// <summary>A preset's curve with a deadzone (0-1) and an adjustment (-5..5; the presets that have one).</summary>
    internal static byte[] Curve(int preset, double deadzone, int adjustment)
    {
        if (preset is < 0 or > 5) throw new ArgumentOutOfRangeException(nameof(preset));
        deadzone = Math.Clamp(deadzone, 0, 1);
        adjustment = Math.Clamp(adjustment, -5, 5);
        return [.. Shapes[preset].Points.Select(p => (byte)Math.Clamp(p.At(deadzone, adjustment), 0, 255))];
    }

    /// <summary>The deadzone a curve starts with (0-1, to the percent).</summary>
    internal static double DeadzoneOf(byte[] points) => points[0] == 0 ? 0 : JsRound(points[0] / 2.55) / 100;

    /// <summary>The adjustment a preset's curve was made with; 0 for a preset without one.</summary>
    internal static int AdjustmentOf(int preset, byte[] points)
    {
        if (preset is < 0 or > 5 || Shapes[preset].Reverse < 0) return 0;
        var (shape, reverse) = Shapes[preset];
        var pos = shape[reverse];
        var deadzone = DeadzoneOf(points);
        var value = pos.Dead ? (points[reverse] - pos.Value * (1 - deadzone) - 255 * deadzone) / pos.Delta : (points[reverse] - pos.Value) / pos.Delta;
        return Math.Clamp((int)JsRound(value), -5, 5);
    }

    /// <summary>JavaScript's Math.round (halves go up), which the controller's numbers were made with; .NET's rounds halves to even.</summary>
    private static double JsRound(double x) => Math.Floor(x + 0.5);
}
