using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using ExoLauncher.Adapters.Cli;
using ExoLauncher.Helpers;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace ExoLauncher.Adapters;

internal sealed record SteamTargetedQueuePromotionResult(
    bool Clicked,
    SteamQueuePromotionSelectionFailure Failure,
    string Message);

/// <summary>
/// Promotes exactly one scheduled Steam update without exposing Steam chrome.
/// Steam's public protocol has no app-scoped "download now" URI for an already
/// installed title, so this mirrors the client's per-row action while binding
/// the click to an OCR-verified exact title and a still-queued appmanifest.
/// Every ambiguous state fails closed.
/// </summary>
internal static class SteamTargetedQueuePromotionAutomator
{
    private const int PwRenderFullContent = 2;
    private const uint WmMouseMove = 0x0200;
    private const uint WmLeftButtonDown = 0x0201;
    private const uint WmLeftButtonUp = 0x0202;
    private const int MkLButton = 0x0001;
    private const byte BrightChannelMinimum = 155;
    private const byte MaximumBrightChannelSpread = 60;

    private static readonly EnumWindowsProc EnumTop = EnumTopCallback;
    private static readonly EnumWindowsProc EnumChild = EnumChildCallback;
    // Process-lifetime pins: native enumeration must never outlive a collected delegate.
    // ReSharper disable once NotAccessedField.Local
    private static readonly GCHandle EnumTopPin = GCHandle.Alloc(EnumTop);
    // ReSharper disable once NotAccessedField.Local
    private static readonly GCHandle EnumChildPin = GCHandle.Alloc(EnumChild);

    [ThreadStatic] private static List<IntPtr>? t_windows;
    [ThreadStatic] private static List<IntPtr>? t_children;

    public static async Task<SteamTargetedQueuePromotionResult> PromoteAsync(
        string steamExe,
        string appId,
        string exactManifestTitle,
        Func<bool> targetIsStillQueued,
        TimeSpan timeout,
        CancellationToken ct)
    {
        if (!SteamProtocol.IsValidAppId(appId) ||
            string.IsNullOrWhiteSpace(exactManifestTitle) ||
            targetIsStillQueued is null)
        {
            return Failed(
                SteamQueuePromotionSelectionFailure.InvalidInput,
                "Steam update identity was invalid.");
        }

        try
        {
            // Navigation is global, but it happens while the hider is already
            // armed. No action is taken until the exact selected title and its
            // own right-gutter icon have both been verified.
            ProcessHelper.StartHidden(
                steamExe,
                [.. SteamUpdateCommandPlan.HiddenClientStartArguments(), SteamProtocol.DownloadsUri()]);
            StoreWindowHider.HideOnce(StoreWindowHider.SteamProcessNames);

            var deadline = DateTimeOffset.UtcNow.Add(timeout);
            IntPtr steamWindow = IntPtr.Zero;
            while (steamWindow == IntPtr.Zero && DateTimeOffset.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                steamWindow = FindPrimarySteamWindow();
                if (steamWindow == IntPtr.Zero)
                    await Task.Delay(250, ct).ConfigureAwait(false);
            }

            if (steamWindow == IntPtr.Zero)
                return Failed(
                    SteamQueuePromotionSelectionFailure.TargetNotFound,
                    "Steam's hidden Downloads view did not become ready.");

            using var lease = StoreWindowHider.BeginOffscreenAutomationWindow(steamWindow);
            await Task.Delay(350, ct).ConfigureAwait(false);

            var engine = OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine is null)
                return Failed(
                    SteamQueuePromotionSelectionFailure.InvalidInput,
                    "Windows text recognition is unavailable.");

            SteamQueuePromotionSelection lastSelection = default;
            while (DateTimeOffset.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                if (!targetIsStillQueued())
                {
                    return Failed(
                        SteamQueuePromotionSelectionFailure.TargetNotFound,
                        "The selected Steam update changed state before promotion.");
                }

                using var bitmap = CaptureWindow(steamWindow);
                if (bitmap is not null)
                {
                    var lines = await RecognizeLinesAsync(engine, bitmap, ct).ConfigureAwait(false);
                    var pixels = CollectBrightPixels(bitmap);
                    lastSelection = SteamQueuePromotionSelector.Select(
                        exactManifestTitle,
                        lines,
                        bitmap.Width,
                        bitmap.Height,
                        pixels);

                    if (lastSelection.IsSuccess && lastSelection.ClickPoint is { } point)
                    {
                        // Final time-of-click guard. If Steam or another process
                        // already moved this app, do not click a now-different row action.
                        if (!targetIsStillQueued())
                        {
                            return Failed(
                                SteamQueuePromotionSelectionFailure.TargetNotFound,
                                "The selected Steam update was no longer queued.");
                        }

                        var inputWindow = FindLargestChromeInputWindow(steamWindow);
                        if (inputWindow == IntPtr.Zero ||
                            !TryMapPoint(steamWindow, inputWindow, point, out var inputPoint))
                        {
                            return Failed(
                                SteamQueuePromotionSelectionFailure.InvalidInput,
                                "Steam's hidden input surface was unavailable.");
                        }

                        if (!await PostVerifiedClickAsync(inputWindow, inputPoint, ct).ConfigureAwait(false))
                        {
                            return Failed(
                                SteamQueuePromotionSelectionFailure.InvalidInput,
                                "Steam did not accept the selected row action.");
                        }

                        AppLog.Info(
                            $"Steam targeted update promotion clicked: appId={appId}; title={exactManifestTitle}; " +
                            $"point={point.X},{point.Y}.");
                        return new SteamTargetedQueuePromotionResult(
                            true,
                            SteamQueuePromotionSelectionFailure.None,
                            "Selected Steam update was started.");
                    }

                    if (lastSelection.Failure is SteamQueuePromotionSelectionFailure.AmbiguousTarget
                        or SteamQueuePromotionSelectionFailure.AmbiguousActionIcon
                        or SteamQueuePromotionSelectionFailure.InvalidInput)
                    {
                        break;
                    }
                }

                await Task.Delay(450, ct).ConfigureAwait(false);
            }

            var failure = lastSelection.Failure == SteamQueuePromotionSelectionFailure.None
                ? SteamQueuePromotionSelectionFailure.TargetNotFound
                : lastSelection.Failure;
            AppLog.Info($"Steam targeted update promotion refused: appId={appId}; reason={failure}.");
            return Failed(
                failure,
                "Steam kept this update scheduled because its exact row could not be verified safely.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Debug($"Steam targeted update promotion failed for appId={appId}: {ex.Message}");
            return Failed(
                SteamQueuePromotionSelectionFailure.InvalidInput,
                "Steam's selected update could not be started safely.");
        }
    }

    private static SteamTargetedQueuePromotionResult Failed(
        SteamQueuePromotionSelectionFailure failure,
        string message) => new(false, failure, message);

    private static Bitmap? CaptureWindow(IntPtr hWnd)
    {
        if (!GetWindowRect(hWnd, out var rect)) return null;
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width < 640 || height < 400 || width > 4096 || height > 2160)
            return null;

        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            var hdc = graphics.GetHdc();
            try
            {
                if (!PrintWindow(hWnd, hdc, PwRenderFullContent))
                {
                    bitmap.Dispose();
                    return null;
                }
            }
            finally
            {
                graphics.ReleaseHdc(hdc);
            }
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            return null;
        }
    }

    private static async Task<IReadOnlyList<SteamQueueOcrLine>> RecognizeLinesAsync(
        OcrEngine engine,
        Bitmap bitmap,
        CancellationToken ct)
    {
        using var png = new MemoryStream();
        bitmap.Save(png, ImageFormat.Png);
        png.Position = 0;
        using var random = new InMemoryRandomAccessStream();
        await png.CopyToAsync(random.AsStreamForWrite(), ct).ConfigureAwait(false);
        random.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(random);
        using var softwareBitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied);
        ct.ThrowIfCancellationRequested();
        var result = await engine.RecognizeAsync(softwareBitmap);
        ct.ThrowIfCancellationRequested();

        var lines = new List<SteamQueueOcrLine>(result.Lines.Count);
        foreach (var line in result.Lines)
        {
            if (line.Words.Count == 0) continue;
            var left = (int)Math.Floor(line.Words.Min(w => w.BoundingRect.X));
            var top = (int)Math.Floor(line.Words.Min(w => w.BoundingRect.Y));
            var right = (int)Math.Ceiling(line.Words.Max(w => w.BoundingRect.X + w.BoundingRect.Width));
            var bottom = (int)Math.Ceiling(line.Words.Max(w => w.BoundingRect.Y + w.BoundingRect.Height));
            var text = string.Join(' ', line.Words.Select(w => w.Text));
            lines.Add(new SteamQueueOcrLine(text, left, top, right, bottom));
        }
        return lines;
    }

    private static IReadOnlyList<SteamQueuePixel> CollectBrightPixels(Bitmap bitmap)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var stride = Math.Abs(data.Stride);
            var bytes = new byte[stride * bitmap.Height];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            var pixels = new List<SteamQueuePixel>(4096);
            var startX = bitmap.Width * 2 / 3;
            for (var y = 0; y < bitmap.Height; y++)
            {
                var row = data.Stride >= 0 ? y * stride : (bitmap.Height - 1 - y) * stride;
                for (var x = startX; x < bitmap.Width; x++)
                {
                    var offset = row + x * 4;
                    var b = bytes[offset];
                    var g = bytes[offset + 1];
                    var r = bytes[offset + 2];
                    var high = Math.Max(r, Math.Max(g, b));
                    var low = Math.Min(r, Math.Min(g, b));
                    if (r >= BrightChannelMinimum &&
                        g >= BrightChannelMinimum &&
                        b >= BrightChannelMinimum &&
                        high - low <= MaximumBrightChannelSpread)
                    {
                        pixels.Add(new SteamQueuePixel(x, y));
                    }
                }
            }
            return pixels;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static IntPtr FindPrimarySteamWindow()
    {
        t_windows = [];
        try { EnumWindows(EnumTop, IntPtr.Zero); }
        catch { /* */ }
        return (t_windows ?? [])
            .Where(h => GetTitle(h).Equals("Steam", StringComparison.OrdinalIgnoreCase))
            .Where(h => GetClass(h).Equals("SDL_app", StringComparison.OrdinalIgnoreCase))
            .Where(h => GetWindowRect(h, out var r) && r.Right - r.Left >= 640 && r.Bottom - r.Top >= 400)
            .OrderByDescending(h => WindowArea(h))
            .FirstOrDefault();
    }

    private static IntPtr FindLargestChromeInputWindow(IntPtr steamWindow)
    {
        t_children = [];
        try { EnumChildWindows(steamWindow, EnumChild, IntPtr.Zero); }
        catch { /* */ }
        return (t_children ?? [])
            .Where(h => GetClass(h).Equals("Chrome_WidgetWin_1", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(WindowArea)
            .FirstOrDefault();
    }

    private static long WindowArea(IntPtr hWnd)
    {
        if (!GetWindowRect(hWnd, out var rect)) return 0;
        return Math.Max(0, rect.Right - rect.Left) * (long)Math.Max(0, rect.Bottom - rect.Top);
    }

    private static bool TryMapPoint(
        IntPtr sourceWindow,
        IntPtr inputWindow,
        SteamQueueClickPoint sourcePoint,
        out SteamQueueClickPoint inputPoint)
    {
        inputPoint = default;
        if (!GetWindowRect(sourceWindow, out var sourceRect) ||
            !GetWindowRect(inputWindow, out var inputRect) ||
            !GetClientRect(inputWindow, out var clientRect))
            return false;

        var x = sourceRect.Left + sourcePoint.X - inputRect.Left;
        var y = sourceRect.Top + sourcePoint.Y - inputRect.Top;
        if (x < clientRect.Left || x >= clientRect.Right ||
            y < clientRect.Top || y >= clientRect.Bottom)
            return false;

        inputPoint = new SteamQueueClickPoint(x, y);
        return true;
    }

    private static async Task<bool> PostVerifiedClickAsync(
        IntPtr inputWindow,
        SteamQueueClickPoint point,
        CancellationToken ct)
    {
        var packed = new IntPtr((point.Y << 16) | (point.X & 0xffff));
        if (!PostMessage(inputWindow, WmMouseMove, IntPtr.Zero, packed)) return false;
        await Task.Delay(120, ct).ConfigureAwait(false);
        if (!PostMessage(inputWindow, WmLeftButtonDown, new IntPtr(MkLButton), packed)) return false;
        await Task.Delay(70, ct).ConfigureAwait(false);
        return PostMessage(inputWindow, WmLeftButtonUp, IntPtr.Zero, packed);
    }

    private static bool EnumTopCallback(IntPtr hWnd, IntPtr lParam)
    {
        try
        {
            GetWindowThreadProcessId(hWnd, out var pid);
            if (pid != 0 && IsSteamProcess(pid))
                t_windows?.Add(hWnd);
        }
        catch { /* */ }
        return true;
    }

    private static bool EnumChildCallback(IntPtr hWnd, IntPtr lParam)
    {
        try { t_children?.Add(hWnd); }
        catch { /* */ }
        return true;
    }

    private static bool IsSteamProcess(uint pid)
    {
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return StoreWindowHider.SteamMainProcessNames.Any(name =>
                name.Equals(process.ProcessName, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    private static string GetTitle(IntPtr hWnd)
    {
        var text = new StringBuilder(512);
        _ = GetWindowText(hWnd, text, text.Capacity);
        return text.ToString().Trim();
    }

    private static string GetClass(IntPtr hWnd)
    {
        var text = new StringBuilder(256);
        _ = GetClassName(hWnd, text, text.Capacity);
        return text.ToString();
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);
}
