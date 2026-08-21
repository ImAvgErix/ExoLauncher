using System.IO.Compression;
using System.Runtime.InteropServices;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>
/// Last-resort tile art: the largest icon embedded in a game executable,
/// centred on a dark 2:3 plate. No store CDN, no third-party database.
/// </summary>
public static class GameIconArt
{
    public const int PlateWidth = 400;
    public const int PlateHeight = 600;
    public const int IconBox = 224;

    private const uint DiNormal = 0x0003;
    private const uint BiRgb = 0;
    private const uint DibRgbColors = 0;
    private static readonly byte[] PlateColor = [0x05, 0x05, 0x05, 0xFF];

    public static string CacheFileName(string gameId) =>
        "icon_" + Sanitize(gameId) + ".png";

    public static bool IsCacheFileName(string? name) =>
        !string.IsNullOrWhiteSpace(name) &&
        name.StartsWith("icon_", StringComparison.OrdinalIgnoreCase) &&
        name.EndsWith(".png", StringComparison.OrdinalIgnoreCase);

    public static bool IsCacheUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url) &&
        url.Contains("/icon_", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when this title already has a plated icon on disk.</summary>
    public static bool HasCachedPlate(string cacheRoot, string gameId)
    {
        var path = Path.Combine(cacheRoot, CacheFileName(gameId));
        return IsValidPlate(path);
    }

    public static bool IsValidPlate(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            if (new FileInfo(path).Length < CoverArtService.MinCoverBytes) return false;
            var size = CoverArtService.ReadImageSize(path);
            return size is { Width: PlateWidth, Height: PlateHeight };
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Executable Exo can read an icon from, or null.</summary>
    public static string? FindExecutable(GameEntry g)
    {
        if (LooksLikeExe(g.LaunchTarget) && File.Exists(g.LaunchTarget))
            return Path.GetFullPath(g.LaunchTarget!);
        if (LooksLikeExe(g.Path) && File.Exists(g.Path))
            return Path.GetFullPath(g.Path!);
        if (string.IsNullOrWhiteSpace(g.Path) || !Directory.Exists(g.Path))
            return null;

        var root = Path.GetFullPath(g.Path);
        foreach (var name in KnownProductExes(g))
        {
            var direct = Path.Combine(root, name);
            if (File.Exists(direct)) return direct;
            try
            {
                var nested = Directory.EnumerateFiles(root, name, SearchOption.AllDirectories)
                    .Take(4)
                    .FirstOrDefault();
                if (nested is not null) return nested;
            }
            catch { /* skip */ }
        }

        string? best = null;
        var bestScore = 0;
        var titleKey = Compact(g.Title);
        var dirKey = Compact(Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar)));
        foreach (var exe in EnumerateCandidateExes(root))
        {
            var score = ScoreExe(exe, titleKey, dirKey);
            if (score > bestScore)
            {
                bestScore = score;
                best = exe;
            }
        }
        return best;
    }

    public static bool TryExtract(GameEntry g, string destPng)
    {
        var exe = FindExecutable(g);
        return exe is not null && TryExtractFromExecutable(exe, destPng);
    }

    public static bool TryExtractFromExecutable(string exePath, string destPng)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return false;
        if (!TryReadIconPixels(exePath, out var pixels, out var size)) return false;
        return TryWritePlate(pixels, size, size, destPng);
    }

    /// <summary>Plate a PNG/JPEG/ICO already on disk (Steam cache icons, folder .ico).</summary>
    public static bool TryWritePlateFromImage(string sourcePath, string destPng)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath)) return false;
        if (sourcePath.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) ||
            sourcePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            sourcePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            return TryExtractFromExecutable(sourcePath, destPng);

        if (TryReadGdiplusPixels(sourcePath, out var pixels, out var width, out var height))
            return TryWritePlate(pixels, width, height, destPng);
        return TryExtractFromExecutable(sourcePath, destPng);
    }

    internal static bool TryWritePlate(byte[] bgra, int width, int height, string destPng)
    {
        if (bgra.Length < width * height * 4 || width < 8 || height < 8) return false;
        var plate = new byte[PlateWidth * PlateHeight * 4];
        for (var i = 0; i < plate.Length; i += 4)
        {
            plate[i] = PlateColor[0];
            plate[i + 1] = PlateColor[1];
            plate[i + 2] = PlateColor[2];
            plate[i + 3] = PlateColor[3];
        }

        var scale = Math.Min(IconBox / (double)width, IconBox / (double)height);
        var dw = Math.Max(8, (int)Math.Round(width * scale));
        var dh = Math.Max(8, (int)Math.Round(height * scale));
        var ox = (PlateWidth - dw) / 2;
        var oy = (PlateHeight - dh) / 2;
        BlitScaled(bgra, width, height, plate, PlateWidth, ox, oy, dw, dh);
        return WritePng(destPng, plate, PlateWidth, PlateHeight);
    }

    private static bool TryReadGdiplusPixels(string path, out byte[] bgra, out int width, out int height)
    {
        bgra = [];
        width = 0;
        height = 0;
        if (!EnsureGdiplus()) return false;
        nint bmp = 0;
        try
        {
            if (GdipCreateBitmapFromFile(path, out bmp) != 0 || bmp == 0) return false;
            if (GdipGetImageWidth(bmp, out var w) != 0 || GdipGetImageHeight(bmp, out var h) != 0)
                return false;
            width = (int)w;
            height = (int)h;
            if (width < 8 || height < 8 || width > 2048 || height > 2048) return false;
            var rect = new GpRect { X = 0, Y = 0, Width = width, Height = height };
            var data = new BitmapData();
            if (GdipBitmapLockBits(bmp, ref rect, ImageLockModeRead, Format32bppArgb, ref data) != 0)
                return false;
            try
            {
                bgra = new byte[width * height * 4];
                var stride = data.Stride;
                for (var y = 0; y < height; y++)
                    Marshal.Copy(data.Scan0 + y * stride, bgra, y * width * 4, width * 4);
                return HasVisiblePixels(bgra);
            }
            finally
            {
                GdipBitmapUnlockBits(bmp, ref data);
            }
        }
        catch
        {
            return false;
        }
        finally
        {
            if (bmp != 0) GdipDisposeImage(bmp);
        }
    }

    private static readonly object GdiLock = new();
    private static nint _gdiToken;
    private const int ImageLockModeRead = 1;
    private const int Format32bppArgb = 0x26200A;

    private static bool EnsureGdiplus()
    {
        lock (GdiLock)
        {
            if (_gdiToken != 0) return true;
            var input = new GdiplusStartupInput { GdiplusVersion = 1 };
            return GdiplusStartup(out _gdiToken, ref input, 0) == 0 && _gdiToken != 0;
        }
    }

    private static IEnumerable<string> EnumerateCandidateExes(string root)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> SafeList(string dir, SearchOption scope)
        {
            try { return Directory.EnumerateFiles(dir, "*.exe", scope); }
            catch { return []; }
        }

        foreach (var path in SafeList(root, SearchOption.TopDirectoryOnly))
            if (seen.Add(path)) yield return path;

        IEnumerable<string> dirs;
        try { dirs = Directory.EnumerateDirectories(root); }
        catch { yield break; }

        var n = 0;
        foreach (var dir in dirs)
        {
            var leaf = Path.GetFileName(dir);
            if (SkipDir(leaf)) continue;
            foreach (var path in SafeList(dir, SearchOption.TopDirectoryOnly))
            {
                if (seen.Add(path)) yield return path;
                if (++n > 80) yield break;
            }
        }
    }

    private static IEnumerable<string> KnownProductExes(GameEntry g)
    {
        if (g.Store != StoreKind.Riot) yield break;
        var product = (g.LaunchTarget ?? "").Trim().ToLowerInvariant();
        if (g.Id.StartsWith("riot:", StringComparison.OrdinalIgnoreCase))
            product = g.Id["riot:".Length..].Trim().ToLowerInvariant();
        switch (product)
        {
            case "valorant":
                yield return "VALORANT-Win64-Shipping.exe";
                yield return "VALORANT.exe";
                break;
            case "league_of_legends":
            case "lion":
                yield return "League of Legends.exe";
                yield return "LeagueClient.exe";
                break;
            case "bacon":
                yield return "LoR.exe";
                yield return "LegendsofRuneterra.exe";
                break;
            default:
                break;
        }
    }

    private static int ScoreExe(string path, string titleKey, string dirKey)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var n = name.ToLowerInvariant();
        if (SkipExe(n)) return -1;
        var score = 1;
        var compact = Compact(name);
        if (compact.Length >= 4 && compact == titleKey) score += 50;
        if (compact.Length >= 4 && compact == dirKey) score += 40;
        if (n.Contains("win64-shipping", StringComparison.Ordinal)) score += 45;
        else if (n.Contains("shipping", StringComparison.Ordinal) &&
                 !n.Contains("prereq", StringComparison.Ordinal))
            score += 20;
        try
        {
            var mb = new FileInfo(path).Length / (1024 * 1024);
            if (mb >= 20) score += 15;
            else if (mb >= 5) score += 8;
        }
        catch { /* */ }
        return score;
    }

    private static bool SkipExe(string nameLower) =>
        nameLower.Contains("unins", StringComparison.Ordinal) ||
        nameLower.Contains("crash", StringComparison.Ordinal) ||
        nameLower.Contains("vcredist", StringComparison.Ordinal) ||
        nameLower.Contains("vc_redist", StringComparison.Ordinal) ||
        nameLower.Contains("dxsetup", StringComparison.Ordinal) ||
        nameLower.Contains("dxwebsetup", StringComparison.Ordinal) ||
        nameLower.Contains("easyanticheat", StringComparison.Ordinal) ||
        nameLower.Contains("eac_launcher", StringComparison.Ordinal) ||
        nameLower.Contains("beservice", StringComparison.Ordinal) ||
        nameLower.Contains("battleye", StringComparison.Ordinal) ||
        nameLower.Contains("splash", StringComparison.Ordinal) ||
        nameLower.Contains("redist", StringComparison.Ordinal) ||
        nameLower.Contains("installer", StringComparison.Ordinal);

    private static bool SkipDir(string name) =>
        name.Equals("EasyAntiCheat", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("BattlEye", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Engine", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("redist", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("_Data", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeExe(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    private static string Compact(string value)
    {
        var chars = (value ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray();
        return new string(chars);
    }

    private static string Sanitize(string id)
    {
        var chars = id.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray();
        return new string(chars);
    }

    private static bool TryReadIconPixels(string path, out byte[] bgra, out int size)
    {
        bgra = [];
        size = 0;
        nint large = 0;
        nint small = 0;
        try
        {
            var hr = SHDefExtractIconW(path, 0, 0, out large, out small, IconSizeParam(256, 48));
            var handle = large != 0 ? large : small;
            if (hr != 0 || handle == 0) return false;
            if (!TryCopyIcon(handle, out bgra, out size)) return false;
            return size >= 16 && bgra.Length >= size * size * 4;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (large != 0) DestroyIcon(large);
            if (small != 0 && small != large) DestroyIcon(small);
        }
    }

    private static uint IconSizeParam(int large, int small) =>
        (uint)((small << 16) | (large & 0xFFFF));

    private static bool TryCopyIcon(nint hIcon, out byte[] bgra, out int size)
    {
        bgra = [];
        size = 0;
        var info = default(ICONINFO);
        if (!GetIconInfo(hIcon, out info)) return false;
        try
        {
            size = MeasureBitmap(info.hbmColor != 0 ? info.hbmColor : info.hbmMask);
            if (size < 16) size = 32;
            size = Math.Min(256, size);
            return TryDrawIcon(hIcon, size, out bgra);
        }
        finally
        {
            if (info.hbmColor != 0) DeleteObject(info.hbmColor);
            if (info.hbmMask != 0) DeleteObject(info.hbmMask);
        }
    }

    private static int MeasureBitmap(nint hbm)
    {
        if (hbm == 0) return 0;
        var bmp = default(BITMAP);
        if (GetObject(hbm, Marshal.SizeOf<BITMAP>(), ref bmp) == 0) return 0;
        return Math.Max(bmp.bmWidth, bmp.bmHeight);
    }

    private static bool TryDrawIcon(nint hIcon, int size, out byte[] bgra)
    {
        bgra = new byte[size * size * 4];
        var bmi = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = size,
                biHeight = -size,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = BiRgb,
            },
        };

        var screenDc = GetDC(0);
        var memDc = CreateCompatibleDC(screenDc);
        nint dib = 0;
        nint old = 0;
        try
        {
            dib = CreateDIBSection(memDc, ref bmi, DIB_RGB_COLORS: DibRgbColors, out var bits, 0, 0);
            if (dib == 0 || bits == 0) return false;
            old = SelectObject(memDc, dib);
            DrawIconEx(memDc, 0, 0, hIcon, size, size, 0, 0, DiNormal);
            Marshal.Copy(bits, bgra, 0, bgra.Length);
            return HasVisiblePixels(bgra);
        }
        finally
        {
            if (old != 0) SelectObject(memDc, old);
            if (dib != 0) DeleteObject(dib);
            if (memDc != 0) DeleteDC(memDc);
            if (screenDc != 0) ReleaseDC(0, screenDc);
        }
    }

    private static bool HasVisiblePixels(byte[] bgra)
    {
        for (var i = 3; i < bgra.Length; i += 4)
            if (bgra[i] > 8) return true;
        // Some older icons fill RGB and leave alpha 0.
        for (var i = 0; i < bgra.Length; i += 4)
            if (bgra[i] > 8 || bgra[i + 1] > 8 || bgra[i + 2] > 8) return true;
        return false;
    }

    private static void BlitScaled(
        byte[] src, int sw, int sh,
        byte[] dest, int dwStride,
        int ox, int oy, int dw, int dh)
    {
        for (var y = 0; y < dh; y++)
        {
            var sy = Math.Min(sh - 1, (int)((y + 0.5) * sh / dh));
            for (var x = 0; x < dw; x++)
            {
                var sx = Math.Min(sw - 1, (int)((x + 0.5) * sw / dw));
                var si = (sy * sw + sx) * 4;
                var di = ((oy + y) * dwStride + (ox + x)) * 4;
                var a = src[si + 3];
                if (a == 0)
                {
                    // Masked icons often store colour with zero alpha — still paint.
                    if (src[si] == 0 && src[si + 1] == 0 && src[si + 2] == 0) continue;
                    a = 255;
                }
                if (a == 255)
                {
                    dest[di] = src[si];
                    dest[di + 1] = src[si + 1];
                    dest[di + 2] = src[si + 2];
                    dest[di + 3] = 255;
                    continue;
                }
                var inv = 255 - a;
                dest[di] = (byte)((src[si] * a + dest[di] * inv) / 255);
                dest[di + 1] = (byte)((src[si + 1] * a + dest[di + 1] * inv) / 255);
                dest[di + 2] = (byte)((src[si + 2] * a + dest[di + 2] * inv) / 255);
                dest[di + 3] = 255;
            }
        }
    }

    private static bool WritePng(string path, byte[] bgra, int width, int height)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            using (var fs = File.Create(tmp))
            {
                fs.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
                WriteChunk(fs, "IHDR", Ihdr(width, height));
                WriteChunk(fs, "IDAT", DeflateScanlines(bgra, width, height));
                WriteChunk(fs, "IEND", []);
            }
            File.Move(tmp, path, overwrite: true);
            return File.Exists(path) && new FileInfo(path).Length >= CoverArtService.MinCoverBytes;
        }
        catch
        {
            return false;
        }
    }

    private static byte[] Ihdr(int width, int height)
    {
        var data = new byte[13];
        WriteBe32(data, 0, width);
        WriteBe32(data, 4, height);
        data[8] = 8;
        data[9] = 6; // RGBA
        return data;
    }

    private static byte[] DeflateScanlines(byte[] bgra, int width, int height)
    {
        var raw = new byte[height * (1 + width * 4)];
        var o = 0;
        for (var y = 0; y < height; y++)
        {
            raw[o++] = 0;
            var row = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                var i = row + x * 4;
                raw[o++] = bgra[i + 2];
                raw[o++] = bgra[i + 1];
                raw[o++] = bgra[i];
                raw[o++] = bgra[i + 3];
            }
        }

        using var ms = new MemoryStream();
        var level = CompressionLevel.Fastest;
        using (var zlib = new ZLibStream(ms, level, leaveOpen: true))
            zlib.Write(raw, 0, raw.Length);
        if (ms.Length < CoverArtService.MinCoverBytes)
        {
            ms.SetLength(0);
            using var zlib = new ZLibStream(ms, CompressionLevel.NoCompression, leaveOpen: true);
            zlib.Write(raw, 0, raw.Length);
        }
        return ms.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        var len = new byte[4];
        WriteBe32(len, 0, data.Length);
        stream.Write(len);
        stream.Write(typeBytes);
        stream.Write(data);
        var crcSrc = new byte[typeBytes.Length + data.Length];
        Buffer.BlockCopy(typeBytes, 0, crcSrc, 0, typeBytes.Length);
        if (data.Length > 0) Buffer.BlockCopy(data, 0, crcSrc, typeBytes.Length, data.Length);
        var crc = new byte[4];
        WriteBe32(crc, 0, Crc32(crcSrc));
        stream.Write(crc);
    }

    private static void WriteBe32(byte[] dest, int offset, uint value)
    {
        dest[offset] = (byte)(value >> 24);
        dest[offset + 1] = (byte)(value >> 16);
        dest[offset + 2] = (byte)(value >> 8);
        dest[offset + 3] = (byte)value;
    }

    private static void WriteBe32(byte[] dest, int offset, int value) =>
        WriteBe32(dest, offset, unchecked((uint)value));

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return crc ^ 0xFFFFFFFF;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHDefExtractIconW(
        string pszIconFile, int iIndex, uint uFlags,
        out nint phiconLarge, out nint phiconSmall, uint nIconSize);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint hIcon);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIconInfo(nint hIcon, out ICONINFO piconinfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DrawIconEx(
        nint hdc, int xLeft, int yTop, nint hIcon,
        int cxWidth, int cyHeight, uint istepIfAniCur, nint hbrFlickerFreeDraw, uint diFlags);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hWnd, nint hDC);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint hdc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(nint hdc);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint hdc, nint hgdiobj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint ho);

    [DllImport("gdi32.dll")]
    private static extern nint CreateDIBSection(
        nint hdc, ref BITMAPINFO pbmi, uint DIB_RGB_COLORS,
        out nint ppvBits, nint hSection, uint offset);

    [DllImport("gdiplus.dll", CharSet = CharSet.Unicode)]
    private static extern int GdiplusStartup(out nint token, ref GdiplusStartupInput input, nint output);

    [DllImport("gdiplus.dll", CharSet = CharSet.Unicode)]
    private static extern int GdipCreateBitmapFromFile(string filename, out nint bitmap);

    [DllImport("gdiplus.dll")]
    private static extern int GdipDisposeImage(nint image);

    [DllImport("gdiplus.dll")]
    private static extern int GdipGetImageWidth(nint image, out uint width);

    [DllImport("gdiplus.dll")]
    private static extern int GdipGetImageHeight(nint image, out uint height);

    [DllImport("gdiplus.dll")]
    private static extern int GdipBitmapLockBits(
        nint bitmap, ref GpRect rect, int flags, int format, ref BitmapData locked);

    [DllImport("gdiplus.dll")]
    private static extern int GdipBitmapUnlockBits(nint bitmap, ref BitmapData locked);

    [StructLayout(LayoutKind.Sequential)]
    private struct GdiplusStartupInput
    {
        public uint GdiplusVersion;
        public nint DebugEventCallback;
        public int SuppressBackgroundThread;
        public int SuppressExternalCodecs;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GpRect
    {
        public int X, Y, Width, Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapData
    {
        public int Width, Height, Stride, PixelFormat;
        public nint Scan0;
        public nint Reserved;
    }

    [DllImport("gdi32.dll")]
    private static extern int GetObject(nint hgdiobj, int cbBuffer, ref BITMAP lpvObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public nint hbmMask;
        public nint hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public nint bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }
}
