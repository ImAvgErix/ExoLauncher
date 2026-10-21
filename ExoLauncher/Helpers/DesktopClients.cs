using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using ExoLauncher.Services;
using Windows.Graphics;

namespace ExoLauncher.Helpers;

internal readonly record struct DesktopFrameInsets(int Left, int Top, int Right, int Bottom);

internal readonly record struct DesktopSurfaceLayout(RectInt32 Window, RectInt32 Clip);

/// <summary>
/// Pure description of one official desktop client Exo can host as an owned
/// popup: where it lives, how to recognize its window, and how to install it.
/// The host service owns every side effect beyond reading the filesystem.
/// </summary>
internal sealed class DesktopClientSpec
{
    internal required string Key { get; init; }
    internal required string Title { get; init; }

    /// Process names whose trees own the client window (rooted at the install directory).
    internal required string[] ProcessNames { get; init; }

    /// (localAppData, roamingAppData) → install, or null when absent.
    internal required Func<string, string, DesktopClientInstall?> Locate { get; init; }

    /// (className, title, width, height) → the real client window, not a splash.
    internal required Func<string?, string?, int, int, bool> IsClientWindow { get; init; }

    /// Runs before launch (Spotify's archive patch).
    internal Action<DesktopClientInstall> PrepareSettings { get; init; } = _ => { };

    /// A client that restarted itself (an update) and was adopted again, but
    /// without what Exo's own launch prepares: (install, window pid) → restart
    /// it once through the normal start. A relaunched Spotify has no DevTools pipe.
    internal Func<DesktopClientInstall, int, bool> RestartAdopted { get; init; } = (_, _) => false;

    /// Starts the client's own executable in place of a shell launch: (install,
    /// arguments) → pid. Spotify uses it to hand over its private DevTools pipe.
    internal Func<DesktopClientInstall, string, int>? Start { get; init; }

    /// Runs once on a background thread after Exo itself launched the client
    /// process — never for a session adopted already running. Spotify uses it
    /// to reach the client's DevTools pipe and hide the caption cluster.
    internal Func<DesktopClientInstall, CancellationToken, Task> LaunchedAsync { get; init; } =
        (_, _) => Task.CompletedTask;

    /// Direct installer download, verified against <see cref="InstallerSigner"/>.
    internal required string InstallerUri { get; init; }

    /// The installer's own quiet switch (Spotify's /silent, as its winget manifest installs it: no
    /// installer window). Exo adopts and hides whatever the installer starts, then starts it its own way.
    internal string InstallerArguments { get; init; } = "";

    /// Authenticode subject substring the downloaded installer must carry.
    internal required string InstallerSigner { get; init; }

    /// Runs after the installer is downloaded and verified, before it starts; a message stops
    /// the install with it. Spotify removes its Store edition here (the switch to the classic app).
    internal Func<CancellationToken, Task<string?>> BeforeInstaller { get; init; } = _ => Task.FromResult<string?>(null);
}

internal sealed record DesktopClientInstall(string Executable, string Arguments, string AppDirectory, string Version);

internal sealed class DesktopClientExitGate(Func<Task> close)
{
    private readonly object _gate = new();
    private Task? _attempt;

    internal Task WaitAsync(CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        Task attempt;
        lock (_gate)
        {
            if (_attempt is null || _attempt.IsFaulted || _attempt.IsCanceled)
                _attempt = Task.Run(close);
            attempt = _attempt;
        }
        return attempt.WaitAsync(cancellation);
    }

    internal static DesktopClientExitGate? Combine(DesktopClientExitGate? first, DesktopClientExitGate? second) =>
        first is null ? second : second is null ? first :
            new DesktopClientExitGate(() => Task.WhenAll(first.WaitAsync(), second.WaitAsync()));
}

internal static class DesktopClients
{
    internal static readonly DesktopClientSpec Spotify = new()
    {
        Key = "spotify",
        Title = "Spotify",
        ProcessNames = ["Spotify"],
        Locate = static (_, roaming) => LocateSpotify(roaming),
        // No title check: while playing, Spotify's title is "Artist - Song".
        // The pid set already limits candidates to Spotify's own tree.
        IsClientWindow = static (className, title, width, height) =>
            string.Equals(className, "Chrome_WidgetWin_1", StringComparison.Ordinal) &&
            !string.IsNullOrEmpty(title) &&
            width >= 400 && height >= 300,
        PrepareSettings = PrepareSpotifySettings,
        LaunchedAsync = (_, cancellation) => SpotifyControls.HideWindowControlsAsync(cancellation),
        Start = StartSpotify,
        RestartAdopted = static (_, _) => !SpotifyDevTools.IsConnected,
        InstallerUri = "https://download.scdn.co/SpotifySetup.exe",
        InstallerArguments = "/silent",
        InstallerSigner = "Spotify AB",
        BeforeInstaller = static async cancellation =>
            !SpotifyInstalls.StoreEditionInstalled() || await SpotifyInstalls.RemoveStoreEditionAsync(cancellation)
                ? null
                : "Spotify from the Microsoft Store could not be removed. Uninstall it in Windows Settings > Apps, then try again.",
    };

    /// The selected music app is only the Store edition, which Exo cannot host.
    internal static bool OnlyStoreEdition(DesktopClientSpec spec) =>
        spec.Key == "spotify" && !Present(spec) && SpotifyInstalls.StoreEditionInstalled();

    internal const string StoreEditionMessage =
        "Spotify from the Microsoft Store can't play inside Exo. Exo can switch it to Spotify's own app: your account, playlists and library stay, and downloaded songs download again.";

    internal static DesktopClientSpec? For(string? key) => key switch
    {
        "spotify" => Spotify,
        _ => null,
    };

    internal static readonly DesktopClientSpec[] Hosted = [Spotify];

    /// Disk presence only. Does not start the client.
    internal static bool Present(DesktopClientSpec spec)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return spec.Locate(local, roaming) is not null;
    }

    internal static DesktopSurfaceLayout? Layout(RectInt32 room, RectInt32 owner, DesktopFrameInsets frame)
    {
        if (room.Width <= 0 || room.Height <= 0 || owner.Width <= 0 || owner.Height <= 0) return null;
        var visible = ClampToOwner(room, owner);
        if (visible is not { } clip) return null;
        var window = new RectInt32(room.X - frame.Left, room.Y - frame.Top,
            room.Width + frame.Left + frame.Right, room.Height + frame.Top + frame.Bottom);
        var region = new RectInt32(clip.X - window.X, clip.Y - window.Y, clip.Width, clip.Height);
        return new DesktopSurfaceLayout(window, region);
    }

    /// Intersects the room rect with Exo's client area so the owned popup can
    /// never extend past it. Null when the two do not overlap.
    internal static RectInt32? ClampToOwner(RectInt32 room, RectInt32 ownerClient)
    {
        var left = Math.Max(room.X, ownerClient.X);
        var top = Math.Max(room.Y, ownerClient.Y);
        var right = Math.Min(room.X + room.Width, ownerClient.X + ownerClient.Width);
        var bottom = Math.Min(room.Y + room.Height, ownerClient.Y + ownerClient.Height);
        return right > left && bottom > top
            ? new RectInt32(left, top, right - left, bottom - top)
            : null;
    }

    internal static bool RoutesCloseToOwner(uint key, uint flags, nint foreground, nint hosted) =>
        key == 0x73 && (flags & 0x20) != 0 && hosted != 0 && foreground == hosted;

    /// A process belongs to the client only when its name matches the spec
    /// and its module lives under the located install's directory.
    internal static bool MatchesClientProcess(DesktopClientSpec spec, string name, string? executable,
        DesktopClientInstall install)
    {
        if (!spec.ProcessNames.Contains(name, StringComparer.OrdinalIgnoreCase)) return false;
        if (string.IsNullOrEmpty(executable) || string.IsNullOrEmpty(install.AppDirectory)) return false;
        try
        {
            var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(install.AppDirectory)) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(executable).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// Polls for the client window: activate once, reactivate once at the
    /// halfway stall, give up at the timeout. The search walks processes and
    /// windows, so it runs on the thread pool; activation and the result stay
    /// on the caller's dispatcher. Any cancellation between polls abandons the
    /// wait without another nudge.
    internal static async Task<nint> WaitForWindowAsync(Func<nint> find, Action activate, Action reactivate,
        TimeSpan timeout, TimeSpan poll, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        activate();
        var started = Environment.TickCount64;
        var nudged = false;
        while (Environment.TickCount64 - started < timeout.TotalMilliseconds)
        {
            cancellation.ThrowIfCancellationRequested();
            var window = await Task.Run(find, cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (window != 0) return window;
            if (!nudged && Environment.TickCount64 - started >= timeout.TotalMilliseconds / 2)
            {
                cancellation.ThrowIfCancellationRequested();
                nudged = true;
                reactivate();
            }
            await Task.Delay(poll, cancellation);
        }
        cancellation.ThrowIfCancellationRequested();
        return 0;
    }

    /// Spotify with Exo's private DevTools pipe, only when none runs. A second
    /// pipe launch would close the live pipe, or, with Spotify already open
    /// (start at login), hand off to it and exit, leaving no pipe at all; so
    /// then a start only raises the running one, as its shortcut would. A
    /// Spotify that runs without the pipe is restarted by the room
    /// (<see cref="DesktopClientHost.RestartForRoom"/>).
    private static int StartSpotify(DesktopClientInstall install, string arguments)
    {
        if (!SpotifyRunning() && !SpotifyDevTools.IsConnected) return SpotifyDevTools.Launch(install.Executable, arguments, install.AppDirectory);
        using var raised = Process.Start(new ProcessStartInfo(install.Executable)
        {
            Arguments = arguments.Replace(SpotifyDevTools.PipeArgument, "", StringComparison.Ordinal).Trim(),
            WorkingDirectory = install.AppDirectory,
            UseShellExecute = true,
        }) ?? throw new InvalidOperationException("Spotify did not start.");
        return raised.Id;
    }

    /// The classic app wherever it was installed (<see cref="SpotifyInstalls"/>); the Store edition is not hosted.
    internal static DesktopClientInstall? LocateSpotify(string roamingAppData)
    {
        try
        {
            if (SpotifyInstalls.FindClassic(roamingAppData) is not { } executable) return null;
            // Spotify updates in place; the file version is what changes.
            return new DesktopClientInstall(executable, SpotifyControls.EngineArguments, Path.GetDirectoryName(executable)!,
                FileVersionInfo.GetVersionInfo(executable).FileVersion ?? "");
        }
        catch { return null; }
    }

    internal static bool NeedsPlacement(bool applied, RectInt32 target, RectInt32 previousTarget,
        RectInt32 actual, RectInt32 previousActual) =>
        !applied || !target.Equals(previousTarget) ||
        actual.X != target.X || actual.Y != target.Y ||
        actual.Width < target.Width || actual.Height < target.Height ||
        (!actual.Equals(target) && !actual.Equals(previousActual));

    /// Spotify's crash handler outlives the app it watched; it is not Spotify running. Counted as running, it made
    /// every start raise a Spotify that wasn't there, without the pipe, and the room restarted it again and again.
    private static bool SpotifyRunning()
    {
        var running = false;
        foreach (var process in Process.GetProcessesByName("Spotify"))
        {
            try { running |= !process.HasExited && HostedProcessClass.QueryCommandLine(process.Id)?.Contains("--type=crashpad-handler", StringComparison.Ordinal) != true; }
            catch { running = true; }
            process.Dispose();
        }
        return running;
    }

    internal static void PrepareSpotifySettings(DesktopClientInstall install)
    {
        if (SpotifyRunning())
        {
            AppLog.Info("Spotify is already running; black surfaces apply on its next start through Exo.");
            return;
        }
        EnableSpotifyMediaControls();
        ApplySpotifyOptimization(install.AppDirectory);
    }

    /// Spotify's optimization, applied while Spotify is stopped: the
    /// xpui.spa stylesheet patch (black surfaces, clutter hidden) and Exo's
    /// preferences (two promo switches, and no Windows notification on every track change). A Spotify update replaces xpui.spa
    /// with an unpatched one, which the next start through Exo patches again.
    internal static void ApplySpotifyOptimization(string spotifyDirectory)
    {
        var apps = Path.Combine(spotifyDirectory, "Apps");
        if (!File.Exists(Path.Combine(apps, "xpui.spa")))
            throw new InvalidDataException("xpui.spa is missing; the archive could not be patched.");
        if (SpotifyAmoledTheme.Apply(apps))
            AppLog.Info("Spotify optimized archive state verified.");
        SpotifyAmoledTheme.SetPrefs(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Spotify", "Users"));
    }

    /// The mini player reads Spotify through its Windows media session, which
    /// Spotify only publishes while its "system media controls" pref is on.
    /// Written only while Spotify is stopped; it rereads prefs at start.
    private static void EnableSpotifyMediaControls()
    {
        const string off = "ui.system_media_controls_enabled=false";
        var users = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Spotify", "Users");
        if (!Directory.Exists(users)) return;
        foreach (var prefs in Directory.EnumerateFiles(users, "prefs", SearchOption.AllDirectories))
        {
            try
            {
                var text = File.ReadAllText(prefs);
                if (!text.Contains(off, StringComparison.Ordinal)) continue;
                var temporary = prefs + ".exo-tmp";
                File.WriteAllText(temporary, text.Replace(off, "ui.system_media_controls_enabled=true", StringComparison.Ordinal));
                File.Move(temporary, prefs, true);
                AppLog.Info("Spotify media controls turned on so the mini player can follow it.");
            }
            catch (Exception error) { AppLog.Warn("Spotify media controls could not be turned on: " + error.Message); }
        }
    }

    /// The downloaded installer must carry a valid Authenticode signature
    /// over its bytes (WinVerifyTrust) from the vendor.
    internal static bool IsSignedBy(string path, string subjectMatch)
    {
        if (!AuthenticodeVerifier.WinTrustValid(path)) return false;
        try
        {
#pragma warning disable SYSLIB0057 // CreateFromSignedFile has no X509CertificateLoader equivalent.
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            return certificate.Subject.Contains(subjectMatch, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
