using ExoLauncher.Helpers;
using ExoLauncher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace ExoLauncher;

public sealed partial class MainWindow
{
    private readonly Dictionary<WebView2, HashSet<CancellationTokenSource>> _connectedPermissionRequests = new();
    private readonly SemaphoreSlim _connectedDialogGate = new(1, 1);
    private TaskCompletionSource _connectedPermissionChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private event Action? ConnectedPermissionStateChanged;

    private void NotifyConnectedPermissionStateChanged()
    {
        if (_connectedPermissionRequests.Count == 0) return;
        var previous = _connectedPermissionChanged;
        _connectedPermissionChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
        previous.TrySetResult();
        foreach (var handler in ConnectedPermissionStateChanged?.GetInvocationList() ?? [])
            try { ((Action)handler)(); } catch { }
    }

    private void CancelConnectedPermissions(WebView2? surface)
    {
        if (surface is null || !_connectedPermissionRequests.TryGetValue(surface, out var requests)) return;
        foreach (var request in requests.ToArray()) request.Cancel();
    }

    /// Once the library is usable (and only after onboarding): Exo's Discord client connects with its
    /// saved sign-in, and the chosen music service's engine starts now, hidden (the Spotify app, or the
    /// Apple Music or YouTube Music page), so Music, the song in the bar and its controls are ready
    /// the moment they're wanted instead of a minute after the room first opens. An idle music app is
    /// frozen as before, so waiting costs memory, not processor time. Exo Shaders and Exo Capture start
    /// here too (PrewarmShaders, PrewarmCapture), not when their pages open.
    internal async Task<object> PrepareConnectedRoomsAsync(string? musicService)
    {
        if (PackagedSmoke.Enabled || _closingWindow) return new { ok = true };
        musicService = WebClientPreferences.Read().MusicService ?? musicService;
        if (!App.Services.Settings.Current.OnboardingComplete) return new { ok = true };
        _shellUiInactive = null;
        var hiddenNow = WindowIsHiddenOrMinimized();
        _ = UpdateShellActivityAsync(hiddenNow || !_windowActive, hiddenNow);
        // Every shell load prepares again; only the first is a startup milestone.
        var milestone = !_connectedPreloadLogged;
        _connectedPreloadLogged = true;
        if (milestone) LogStartupMilestone("connected-preload-start");
        var work = new List<Task> { PrewarmShaders(), PrewarmCapture(), Task.Run(Services.DiscordClient.DiscordSession.Start) };
        // The music engine starts with the Music room (its adapter's start), so a PC that never opens Music never runs
        // it (Spotify's eight processes took 570 MB at every start). A desktop app already running is adopted now: it
        // costs nothing more, and the Bar's player shows its song at once.
        if (IsDesktopMusic(musicService) && DesktopClients.For(musicService) is { } music && DesktopClients.Present(music) && Runs(music))
        {
            if (_musicWebService != musicService) SelectMusicService(musicService!);
            work.Add(MusicAppHost(musicService!).PrewarmAsync());
        }
        await Task.WhenAll(work);
        if (milestone) LogStartupMilestone("connected-preload-ready");
        return new { ok = true };
    }

    private bool _connectedPreloadLogged;

    private static bool Runs(DesktopClientSpec spec)
    {
        foreach (var name in spec.ProcessNames)
        {
            var found = System.Diagnostics.Process.GetProcessesByName(name);
            foreach (var process in found) process.Dispose();
            if (found.Length > 0) return true;
        }
        return false;
    }

    private static async Task ResetPermissionGrantsAsync(CoreWebView2Profile profile)
    {
        foreach (var grant in await profile.GetNonDefaultPermissionSettingsAsync())
            await profile.SetPermissionStateAsync(grant.PermissionKind, grant.PermissionOrigin, CoreWebView2PermissionState.Default);
    }

    internal async Task ResetWebPermissionGrantsAsync()
    {
        if (_musicWeb?.CoreWebView2 is { } music) await ResetPermissionGrantsAsync(music.Profile);
    }

    private async void OnConnectedPermissionRequested(CoreWebView2PermissionRequestedEventArgs e, WebView2 surface, bool popup, string? service)
    {
        e.SavesInProfile = false;
        e.Handled = true;
        var core = surface.CoreWebView2;
        bool Trusted(string? uri) => service is not null && MusicWebPolicy.IsPlayerDocument(service, uri);
        if (core is null || !Trusted(e.Uri) || _closingWindow)
        {
            e.State = CoreWebView2PermissionState.Deny;
            return;
        }
        var resource = e.PermissionKind switch
        {
            CoreWebView2PermissionKind.Microphone => "microphone",
            CoreWebView2PermissionKind.Camera => "camera",
            CoreWebView2PermissionKind.ClipboardRead => "clipboard",
            CoreWebView2PermissionKind.Notifications => "notifications",
            _ => null,
        };
        if (resource is null)
        {
            e.State = CoreWebView2PermissionState.Deny;
            return;
        }

        using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        var deferral = e.GetDeferral();
        ContentDialog? dialog = null;
        var finished = false;
        bool IsCurrent()
        {
            try
            {
                return !request.IsCancellationRequested && !_closingWindow && ReferenceEquals(core, surface.CoreWebView2) &&
                    Trusted(core.Source) && service is not null && !_musicWebClosed && _musicWebService == service &&
                    (popup || ReferenceEquals(surface, _musicWeb));
            }
            catch { return false; }
        }
        bool IsVisible() => surface.XamlRoot is not null && (popup ||
            (_musicWebWanted && !WindowIsHiddenOrMinimized()));
        string Choice()
        {
            var current = WebClientPreferences.Read();
            return resource switch { "microphone" => current.Microphone, "camera" => current.Camera,
                "clipboard" => current.Clipboard, _ => current.Notifications };
        }
        void HideObsoletePrompt()
        {
            if (!finished && dialog is not null && (!IsCurrent() || !IsVisible() || Choice() != "ask"))
                try { dialog.Hide(); } catch { }
        }
        void PreferencesChanged() => DispatcherQueue.TryEnqueue(() => { if (!finished) NotifyConnectedPermissionStateChanged(); });
        void Navigation(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args) { if (!args.Cancel) request.Cancel(); }
        void Failed(CoreWebView2 sender, CoreWebView2ProcessFailedEventArgs args)
        {
            if (args.ProcessFailedKind is CoreWebView2ProcessFailedKind.BrowserProcessExited or
                CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
                request.Cancel();
        }
        void Unloaded(object sender, RoutedEventArgs args) => request.Cancel();
        if (!_connectedPermissionRequests.TryGetValue(surface, out var requests))
            _connectedPermissionRequests[surface] = requests = [];
        requests.Add(request);
        WebClientPreferences.Changed += PreferencesChanged;
        ConnectedPermissionStateChanged += HideObsoletePrompt;
        core.NavigationStarting += Navigation;
        core.ProcessFailed += Failed;
        surface.Unloaded += Unloaded;
        using var cancellation = request.Token.Register(() => DispatcherQueue.TryEnqueue(HideObsoletePrompt));
        try
        {
            var allowed = await ConnectedPermissionRequest.DecideAsync(Choice, IsCurrent, IsVisible,
                () => _connectedPermissionChanged.Task, _connectedDialogGate, async token =>
                {
                    token.ThrowIfCancellationRequested();
                    var label = service is null ? "This app" : MusicWebPolicy.Label(service);
                    dialog = new ContentDialog {
                        XamlRoot = surface.XamlRoot, RequestedTheme = ElementTheme.Dark,
                        Title = "Allow " + label + " to use your " + resource + "?",
                        Content = "You can choose ongoing access in Exo Settings → Connected apps.",
                        PrimaryButtonText = "Allow this time", CloseButtonText = "Not now",
                        DefaultButton = ContentDialogButton.Close,
                    };
                    StyleConnectedDialog(dialog);
                    return await dialog.ShowAsync() == ContentDialogResult.Primary;
                }, request.Token);
            e.State = allowed ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
        }
        catch { try { e.State = CoreWebView2PermissionState.Deny; } catch { } }
        finally
        {
            finished = true;
            WebClientPreferences.Changed -= PreferencesChanged;
            ConnectedPermissionStateChanged -= HideObsoletePrompt;
            try { core.NavigationStarting -= Navigation; core.ProcessFailed -= Failed; surface.Unloaded -= Unloaded; } catch { }
            requests.Remove(request);
            if (requests.Count == 0) _connectedPermissionRequests.Remove(surface);
            try { deferral.Complete(); } catch { }
        }
    }

    private static Window CreateExoWebPopup(string title, WebView2 view)
    {
        var window = new Window { Title = title + " · Exo" };
        ExoConnectedTheme.WindowContent(window, title, view);
        return window;
    }
}
