using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace ExoLauncher.Helpers;

/// <summary>
/// SDR content that looks right while Windows is in HDR. Windows draws SDR (the desktop, overlays, SDR
/// windows over a game) with the piecewise sRGB curve, but SDR monitors and content are made for gamma 2.2,
/// so in HDR shadows come out lifted and grey. The fix is a display profile with Microsoft's MHC2 tag whose
/// regamma LUT, applied to the PQ signal Windows sends, redraws everything below SDR white with gamma 2.2
/// and leaves HDR above it alone. The same idea as ledoge/win11hdr-srgb-to-gamma2.2-icm; this generator is
/// Exo's own, from the MHC2 layout KWin parses (invent.kde.org/plasma/kwin merge request 8051):
/// 'MHC2', reserved, LUT size, min and max luminance (s15Fixed16, nits), offsets of a 3x4 matrix and the
/// three curves (each 'sf32', reserved, LUT size s15Fixed16 values).
///
/// It is associated (advanced color, current user) only while the screen is in HDR, and replaced by the
/// profile that was the default before (the Windows HDR Calibration one) when HDR goes off. The calibration's
/// min and max luminance carry into Exo's profile, so games still read the screen's real brightness.
/// Cannot be seen on a PC without an HDR screen; the curve and the file are unit tested.
/// </summary>
internal static class SdrInHdr
{
    private const int LutSize = 1024;
    internal const string Prefix = "Exo SDR gamma 2.2";

    // ------------------------------------------------------------------ the curve

    private const double M1 = 2610.0 / 16384, M2 = 2523.0 / 4096 * 128, C1 = 3424.0 / 4096, C2 = 2413.0 / 4096 * 32, C3 = 2392.0 / 4096 * 32;

    /// <summary>PQ signal (0-1) to nits.</summary>
    internal static double PqToNits(double e)
    {
        var p = Math.Pow(Math.Clamp(e, 0, 1), 1 / M2);
        return 10000 * Math.Pow(Math.Max(p - C1, 0) / (C2 - C3 * p), 1 / M1);
    }

    /// <summary>Nits to PQ signal (0-1).</summary>
    internal static double NitsToPq(double nits)
    {
        var y = Math.Pow(Math.Clamp(nits / 10000, 0, 1), M1);
        return Math.Pow((C1 + C2 * y) / (1 + C3 * y), M2);
    }

    /// <summary>
    /// One LUT entry: below SDR white, the light Windows meant through the piecewise sRGB curve is
    /// re-encoded with sRGB and drawn with gamma 2.2 instead; at and above SDR white, unchanged.
    /// </summary>
    internal static double Map(double pq, double sdrWhiteNits)
    {
        var nits = PqToNits(pq);
        if (nits >= sdrWhiteNits) return pq;
        var linear = nits / sdrWhiteNits;
        var encoded = linear <= 0.0031308 ? linear * 12.92 : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055;
        return NitsToPq(Math.Pow(encoded, 2.2) * sdrWhiteNits);
    }

    // ------------------------------------------------------------------ the profile

    /// <summary>An ICC display profile with the MHC2 regamma for this SDR white level and these luminance limits.</summary>
    internal static byte[] Generate(double sdrWhiteNits, double minNits, double maxNits)
    {
        var tags = new List<(string Sig, byte[] Data)>
        {
            ("desc", Desc($"{Prefix} ({Math.Round(sdrWhiteNits)} nits SDR white)")),
            ("cprt", Text("Exo Launcher")),
            ("wtpt", Xyz(0.9642, 1.0, 0.8249)),
            ("rXYZ", Xyz(0.4361, 0.2225, 0.0139)),
            ("gXYZ", Xyz(0.3851, 0.7169, 0.0971)),
            ("bXYZ", Xyz(0.1431, 0.0606, 0.7141)),
            ("rTRC", Gamma22()), ("gTRC", Gamma22()), ("bTRC", Gamma22()),
            ("lumi", Xyz(0, maxNits, 0)),
            ("MHC2", Mhc2(sdrWhiteNits, minNits, maxNits)),
        };
        var tableSize = 4 + tags.Count * 12;
        var offset = 128 + tableSize;
        var placed = new List<(string Sig, int Offset, int Size)>();
        foreach (var (sig, data) in tags) { placed.Add((sig, offset, data.Length)); offset += Pad(data.Length); }
        var file = new byte[offset];
        var span = file.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(span, (uint)file.Length);
        BinaryPrimitives.WriteUInt32BigEndian(span[8..], 0x02100000);                 // ICC 2.1
        Ascii(span[12..], "mntr"); Ascii(span[16..], "RGB "); Ascii(span[20..], "XYZ ");
        // A fixed creation date: the file's name is a hash of its content, so a clock in the header would give every
        // look a new name and Apply would install and re-associate the profile again every few seconds.
        foreach (var (i, v) in new[] { 2026, 1, 1, 0, 0, 0 }.Select((v, i) => (i, v)))
            BinaryPrimitives.WriteUInt16BigEndian(span[(24 + i * 2)..], (ushort)v);
        Ascii(span[36..], "acsp"); Ascii(span[40..], "MSFT");
        WriteS15(span[68..], 0.9642); WriteS15(span[72..], 1.0); WriteS15(span[76..], 0.8249); // D50
        Ascii(span[80..], "EXO ");
        BinaryPrimitives.WriteUInt32BigEndian(span[128..], (uint)tags.Count);
        for (var i = 0; i < placed.Count; i++)
        {
            var entry = span[(132 + i * 12)..];
            Ascii(entry, placed[i].Sig);
            BinaryPrimitives.WriteUInt32BigEndian(entry[4..], (uint)placed[i].Offset);
            BinaryPrimitives.WriteUInt32BigEndian(entry[8..], (uint)placed[i].Size);
            tags[i].Data.CopyTo(span[placed[i].Offset..]);
        }
        return file;
    }

    private static byte[] Mhc2(double sdrWhiteNits, double minNits, double maxNits)
    {
        const int matrixOffset = 36, curveSize = 8 + LutSize * 4;
        var data = new byte[matrixOffset + 48 + curveSize * 3];
        var span = data.AsSpan();
        Ascii(span, "MHC2");
        BinaryPrimitives.WriteUInt32BigEndian(span[8..], LutSize);
        WriteS15(span[12..], minNits);
        WriteS15(span[16..], maxNits);
        BinaryPrimitives.WriteUInt32BigEndian(span[20..], matrixOffset);
        for (var c = 0; c < 3; c++) BinaryPrimitives.WriteUInt32BigEndian(span[(24 + c * 4)..], (uint)(matrixOffset + 48 + c * curveSize));
        for (var row = 0; row < 3; row++) WriteS15(span[(matrixOffset + (row * 4 + row) * 4)..], 1.0);   // identity 3x4
        for (var c = 0; c < 3; c++)
        {
            var curve = span[(matrixOffset + 48 + c * curveSize)..];
            Ascii(curve, "sf32");
            for (var i = 0; i < LutSize; i++) WriteS15(curve[(8 + i * 4)..], Map(i / (double)(LutSize - 1), sdrWhiteNits));
        }
        return data;
    }

    private static byte[] Desc(string text)
    {
        var ascii = Encoding.ASCII.GetBytes(text + "\0");
        var data = new byte[12 + ascii.Length + 4 + 4 + 2 + 1 + 67];
        Ascii(data, "desc");
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), (uint)ascii.Length);
        ascii.CopyTo(data, 12);
        return data;
    }

    private static byte[] Text(string text)
    {
        var ascii = Encoding.ASCII.GetBytes(text + "\0");
        var data = new byte[8 + ascii.Length];
        Ascii(data, "text");
        ascii.CopyTo(data, 8);
        return data;
    }

    private static byte[] Xyz(double x, double y, double z)
    {
        var data = new byte[20];
        Ascii(data, "XYZ ");
        WriteS15(data.AsSpan(8), x); WriteS15(data.AsSpan(12), y); WriteS15(data.AsSpan(16), z);
        return data;
    }

    private static byte[] Gamma22()
    {
        var data = new byte[14];
        Ascii(data, "curv");
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(12), 0x0233);              // u8Fixed8 2.2
        return data;
    }

    private static int Pad(int size) => (size + 3) & ~3;
    private static void Ascii(Span<byte> target, string text) => Encoding.ASCII.GetBytes(text, target);
    private static void WriteS15(Span<byte> target, double value) => BinaryPrimitives.WriteInt32BigEndian(target, (int)Math.Round(value * 65536));
    private static double ReadS15(ReadOnlySpan<byte> source) => BinaryPrimitives.ReadInt32BigEndian(source) / 65536.0;

    /// <summary>An ICC file's MHC2 min and max luminance (the Windows HDR Calibration profile carries them), or null.</summary>
    internal static (double Min, double Max)? Luminance(byte[] icc)
    {
        if (icc.Length < 132) return null;
        var count = BinaryPrimitives.ReadUInt32BigEndian(icc.AsSpan(128));
        for (var i = 0; i < count && 132 + i * 12 + 12 <= icc.Length; i++)
        {
            var entry = icc.AsSpan(132 + i * 12);
            if (Encoding.ASCII.GetString(entry[..4]) != "MHC2") continue;
            var at = (int)BinaryPrimitives.ReadUInt32BigEndian(entry[4..]);
            if (at + 20 > icc.Length) return null;
            return (ReadS15(icc.AsSpan(at + 12)), ReadS15(icc.AsSpan(at + 16)));
        }
        return null;
    }

    // ------------------------------------------------------------------ on the screen

    private const int CurrentUser = 1, IccProfile = 0, AdvancedColor = 8;

    /// <summary>The screen's SDR white in nits (Windows' "SDR content brightness"), or null.</summary>
    internal static double? SdrWhiteNits(WindowsHdr.Screen screen)
    {
        var request = new SdrWhiteLevel { header = new DeviceInfoHeader { type = 11, size = (uint)Marshal.SizeOf<SdrWhiteLevel>(), adapter = screen.Adapter, id = screen.Target } };
        return DisplayConfigGetDeviceInfo(ref request) == 0 && request.level > 0 ? request.level / 1000.0 * 80 : null;
    }

    /// <summary>
    /// Puts Exo's profile on an HDR screen as its advanced-color default (keeping the previous default's
    /// luminance and its name to put back). One of Exo's own already there is made again when the SDR white
    /// level moved since (its name is its content's hash), keeping the luminance it carries and the name
    /// remembered before it. Null when it's on, else why not.
    /// </summary>
    internal static string? Apply(WindowsHdr.Screen screen, Action<string, string?> rememberPrevious)
    {
        if (!screen.Enabled) return "HDR is off on this screen.";
        if (SdrWhiteNits(screen) is not { } white) return "Windows didn't report the screen's SDR brightness.";
        var previous = DefaultName(screen);
        var ours = previous?.StartsWith(Prefix, StringComparison.Ordinal) == true;
        var (min, max) = (0.0, 1000.0);
        if (previous is not null && ReadProfile(previous) is { } bytes && Luminance(bytes) is { } lum) (min, max) = lum;
        else if (WindowsHdr.PeakNits(screen) is { } peak) max = peak;
        var icc = Generate(white, min, max);
        var name = $"{Prefix} {Convert.ToHexString(SHA256.HashData(icc))[..12]}.icm";
        if (ours && string.Equals(previous, name, StringComparison.OrdinalIgnoreCase)) return null;
        var temp = Path.Combine(Path.GetTempPath(), name);
        try
        {
            File.WriteAllBytes(temp, icc);
            if (!File.Exists(Path.Combine(ColorDirectory(), name)) && !InstallColorProfileW(null, temp))
                return "Windows didn't take the profile.";
            var hr = ColorProfileAddDisplayAssociation(CurrentUser, name, screen.Adapter, screen.Source, true, true);
            if (hr != 0) return $"Windows didn't use the profile (0x{hr:X8}).";
            if (ours) ColorProfileRemoveDisplayAssociation(CurrentUser, previous!, screen.Adapter, screen.Source, true);
            else rememberPrevious(screen.DevicePath, previous);
            AppLog.Info($"HDR: SDR gamma 2.2 on {screen.GdiName} (SDR white {white:0} nits, {min:0.###}-{max:0} nits, before: {previous ?? "none"}).");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException)
        {
            AppLog.Warn("HDR: SDR gamma 2.2 failed: " + ex.Message);
            return "Windows didn't take the profile.";
        }
        finally { try { File.Delete(temp); } catch { } }
    }

    /// <summary>Takes Exo's profile off the screen and puts the previous default back.</summary>
    internal static void Remove(WindowsHdr.Screen screen, string? previous)
    {
        try
        {
            var current = DefaultName(screen);
            if (current?.StartsWith(Prefix, StringComparison.Ordinal) != true) return;
            if (previous is not null) ColorProfileSetDisplayDefaultAssociation(CurrentUser, previous, IccProfile, AdvancedColor, screen.Adapter, screen.Source);
            ColorProfileRemoveDisplayAssociation(CurrentUser, current, screen.Adapter, screen.Source, true);
            AppLog.Info($"HDR: SDR gamma 2.2 off {screen.GdiName}; {previous ?? "no profile"} back.");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
    }

    private static string? DefaultName(WindowsHdr.Screen screen)
    {
        if (ColorProfileGetDisplayDefault(CurrentUser, screen.Adapter, screen.Source, IccProfile, AdvancedColor, out var name) != 0 || name == 0) return null;
        try { return Marshal.PtrToStringUni(name) is { Length: > 0 } text ? text : null; }
        finally { LocalFree(name); }
    }

    private static byte[]? ReadProfile(string name)
    {
        try { return File.ReadAllBytes(Path.Combine(ColorDirectory(), name)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static string ColorDirectory()
    {
        var size = 520u;
        var buffer = new StringBuilder(260);
        return GetColorDirectoryW(null, buffer, ref size) ? buffer.ToString() : Path.Combine(Environment.SystemDirectory, "spool", "drivers", "color");
    }

    /// <summary>Whether Windows' color system opens the file as a valid profile (used by the tests).</summary>
    internal static bool IsValid(byte[] icc)
    {
        var handle = GCHandle.Alloc(icc, GCHandleType.Pinned);
        try
        {
            var profile = new NativeProfile { type = 2, data = handle.AddrOfPinnedObject(), size = (uint)icc.Length };
            var opened = OpenColorProfileW(ref profile, 1, 1, 3);
            if (opened == 0) return false;
            try { return IsColorProfileValid(opened, out var valid) && valid; }
            finally { CloseColorProfile(opened); }
        }
        finally { handle.Free(); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct DeviceInfoHeader { public uint type; public uint size; public WindowsHdr.Luid adapter; public uint id; }
    [StructLayout(LayoutKind.Sequential)] private struct SdrWhiteLevel { public DeviceInfoHeader header; public uint level; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeProfile { public uint type; public nint data; public uint size; }

    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref SdrWhiteLevel request);
    [DllImport("mscms.dll", CharSet = CharSet.Unicode)] private static extern bool InstallColorProfileW(string? machine, string path);
    [DllImport("mscms.dll", CharSet = CharSet.Unicode)] private static extern bool GetColorDirectoryW(string? machine, StringBuilder buffer, ref uint size);
    [DllImport("mscms.dll", CharSet = CharSet.Unicode)] private static extern int ColorProfileAddDisplayAssociation(int scope, string profile, WindowsHdr.Luid adapter, uint source, bool setAsDefault, bool advancedColor);
    [DllImport("mscms.dll", CharSet = CharSet.Unicode)] private static extern int ColorProfileRemoveDisplayAssociation(int scope, string profile, WindowsHdr.Luid adapter, uint source, bool advancedColor);
    [DllImport("mscms.dll", CharSet = CharSet.Unicode)] private static extern int ColorProfileSetDisplayDefaultAssociation(int scope, string profile, int type, int subtype, WindowsHdr.Luid adapter, uint source);
    [DllImport("mscms.dll")] private static extern int ColorProfileGetDisplayDefault(int scope, WindowsHdr.Luid adapter, uint source, int type, int subtype, out nint name);
    [DllImport("mscms.dll")] private static extern nint OpenColorProfileW(ref NativeProfile profile, uint access, uint share, uint creation);
    [DllImport("mscms.dll")] private static extern bool IsColorProfileValid(nint profile, out bool valid);
    [DllImport("mscms.dll")] private static extern bool CloseColorProfile(nint profile);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);
}
