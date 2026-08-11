using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ExoLauncher.Helpers;

namespace ExoLauncher.Adapters;

/// <summary>
/// After Exo Install consent, auto-accept Steam's install/download confirmation dialog
/// so the user does not have to click Install in Steam. Never blind-clicks EULA / disk /
/// error dialogs — those surface once as Exo status and leave non-essential chrome hidden.
/// </summary>
internal sealed class SteamInstallDialogAutomator : IDisposable
{
    private const int BmClick = 0x00F5;
    private const uint WmGetText = 0x000D;
    private const uint WmGetTextLength = 0x000E;

    private static readonly string[] AffirmativeButtonLabels =
    [
        "install", "update", "next", "ok", "continue", "download", "accept", "yes",
        "play", // Steam sometimes labels the confirm as Play after update queued
    ];

    private static readonly string[] ManualDialogHints =
    [
        "eula", "license", "agreement", "terms of service", "disk space",
        "select drive", "choose location", "not enough", "error", "failed",
        "already installing", "cloud",
    ];

    private static readonly string[] InstallDialogHints =
    [
        "install", "update", "download", "add to library", "create shortcut",
    ];

    // Process-lifetime pins — never free.
    private static readonly EnumWindowsProc EnumTop = EnumTopCallback;
    private static readonly EnumWindowsProc EnumChild = EnumChildCallback;
    // ReSharper disable once NotAccessedField.Local
    private static readonly GCHandle EnumTopPin = GCHandle.Alloc(EnumTop);
    // ReSharper disable once NotAccessedField.Local
    private static readonly GCHandle EnumChildPin = GCHandle.Alloc(EnumChild);

    [ThreadStatic] private static List<(IntPtr Hwnd, string Title)>? t_windows;
    [ThreadStatic] private static List<(IntPtr Hwnd, string Text)>? t_buttons;

    private CancellationTokenSource? _cts;
    private Task? _task;
    private bool _disposed;

    public bool ClickedInstall { get; private set; }
    public bool NeedsManualAction { get; private set; }
    public string? ManualReason { get; private set; }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, StringBuilder lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>Poll Steam for install dialogs up to <paramref name="duration"/>.</summary>
    public void Start(TimeSpan duration)
    {
        Stop();
        _cts = new CancellationTokenSource(duration);
        var token = _cts.Token;
        _task = Task.Run(async () =>
        {
            var attempts = 0;
            while (!token.IsCancellationRequested && !ClickedInstall && !NeedsManualAction)
            {
                try
                {
                    TryAcceptOnce();
                    attempts++;
                    // Give Steam a moment after first click before we stop watching.
                    if (ClickedInstall)
                        break;
                }
                catch (Exception ex)
                {
                    AppLog.Debug("SteamInstallDialogAutomator: " + ex.Message);
                }

                try { await Task.Delay(200, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }

                if (attempts > 150) break; // ~30s hard cap
            }
        }, CancellationToken.None);
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { /* */ }
        try { _task?.Wait(2000); } catch { /* */ }
        _task = null;
        try { _cts?.Dispose(); } catch { /* */ }
        _cts = null;
    }

    private void TryAcceptOnce()
    {
        var windows = CollectSteamTopWindows();
        foreach (var (hwnd, title) in windows)
        {
            var lower = title.ToLowerInvariant();
            if (ManualDialogHints.Any(h => lower.Contains(h, StringComparison.Ordinal)))
            {
                NeedsManualAction = true;
                ManualReason = "Steam needs a choice (license, disk, or error). Complete it once — Exo will hide Steam after.";
                AppLog.Info("Steam install dialog needs manual action: " + title);
                return;
            }

            if (!LooksLikeInstallDialog(title))
                continue;

            // Only count a real Install/Update/Download button click.
            // Enter-on-any-Steam-window was a false positive that stopped re-nudging
            // while BytesDownloaded stayed 0.
            if (TryClickAffirmativeButton(hwnd))
            {
                ClickedInstall = true;
                AppLog.Info("Auto-accepted Steam install dialog: " + title);
                return;
            }

        }
    }

    private static bool LooksLikeInstallDialog(string title)
    {
        // Untitled chrome dialogs are common for Steam install/update prompts.
        if (string.IsNullOrWhiteSpace(title)) return true;
        var lower = title.ToLowerInvariant();
        // Do NOT match every window with "steam" in the title — that false-accepts
        // the main library and stalls updates at BytesDownloaded=0.
        return InstallDialogHints.Any(h => lower.Contains(h, StringComparison.Ordinal));
    }

    private static List<(IntPtr Hwnd, string Title)> CollectSteamTopWindows()
    {
        t_windows = new List<(IntPtr, string)>();
        try { EnumWindows(EnumTop, IntPtr.Zero); }
        catch { /* */ }
        return t_windows ?? new List<(IntPtr, string)>();
    }

    private static bool EnumTopCallback(IntPtr hWnd, IntPtr lParam)
    {
        try
        {
            GetWindowThreadProcessId(hWnd, out var pid);
            if (pid == 0 || !IsSteamPid(pid)) return true;
            var title = GetText(hWnd);
            var cls = GetClass(hWnd);
            // Skip main Steam big-picture / friends chrome; keep dialogs + overlays.
            if (cls.Contains("SDL_app", StringComparison.OrdinalIgnoreCase) &&
                title.Equals("Steam", StringComparison.OrdinalIgnoreCase))
                return true;
            t_windows?.Add((hWnd, title));
        }
        catch { /* */ }
        return true;
    }

    private static bool IsSteamPid(uint pid)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return StoreWindowHider.SteamProcessNames.Any(n =>
                string.Equals(n, p.ProcessName, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    private static bool TryClickAffirmativeButton(IntPtr dialog)
    {
        t_buttons = new List<(IntPtr, string)>();
        try { EnumChildWindows(dialog, EnumChild, IntPtr.Zero); }
        catch { return false; }

        foreach (var (btn, text) in t_buttons ?? Enumerable.Empty<(IntPtr, string)>())
        {
            var t = text.Trim().ToLowerInvariant().TrimEnd('.');
            if (string.IsNullOrEmpty(t)) continue;
            // Exact / prefix match for short labels ("ok", "yes"). Substring only for
            // longer action words so "Update Game" / "Download & Install" still match.
            if (!AffirmativeButtonLabels.Any(a =>
                    t == a
                    || t.StartsWith(a + " ", StringComparison.Ordinal)
                    || (a.Length >= 6 && t.Contains(a, StringComparison.Ordinal))))
                continue;
            try
            {
                SendMessage(btn, BmClick, IntPtr.Zero, IntPtr.Zero);
                return true;
            }
            catch { /* try next */ }
        }
        return false;
    }

    private static bool EnumChildCallback(IntPtr hWnd, IntPtr lParam)
    {
        try
        {
            var cls = GetClass(hWnd);
            if (!cls.Contains("Button", StringComparison.OrdinalIgnoreCase) &&
                !cls.Equals("Button", StringComparison.OrdinalIgnoreCase))
                return true;
            var text = GetText(hWnd);
            if (!string.IsNullOrWhiteSpace(text))
                t_buttons?.Add((hWnd, text));
        }
        catch { /* */ }
        return true;
    }

    private static string GetText(IntPtr hWnd)
    {
        try
        {
            var len = (int)SendMessage(hWnd, WmGetTextLength, IntPtr.Zero, IntPtr.Zero);
            if (len <= 0)
            {
                var sb0 = new StringBuilder(256);
                GetWindowText(hWnd, sb0, sb0.Capacity);
                return sb0.ToString();
            }
            var sb = new StringBuilder(len + 1);
            SendMessage(hWnd, WmGetText, new IntPtr(sb.Capacity), sb);
            return sb.ToString();
        }
        catch
        {
            return "";
        }
    }

    private static string GetClass(IntPtr hWnd)
    {
        try
        {
            var sb = new StringBuilder(256);
            GetClassName(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }
        catch { return ""; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
