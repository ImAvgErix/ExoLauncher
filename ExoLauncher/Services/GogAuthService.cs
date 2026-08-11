using ExoLauncher.Adapters;
using ExoLauncher.Adapters.Cli;
using ExoLauncher.Helpers;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics;

namespace ExoLauncher.Services;

/// <summary>
/// Interactive GOG OAuth handoff. gogdl does not open a login page itself: the
/// host must capture GOG's authorization-code redirect and pass that code back
/// to gogdl with a persistent auth-config path.
/// </summary>
public sealed class GogAuthService : IDisposable
{
    private static readonly TimeSpan LoginTimeout = TimeSpan.FromMinutes(9);
    private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(45);

    private readonly object _stateGate = new();
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private DispatcherQueue? _queue;
    private Window? _window;
    private WebView2? _web;
    private TaskCompletionSource<string>? _codeTcs;
    private bool _disposed;

    public static string AuthConfigPath =>
        Path.Combine(PathHelper.AppDataDir, "gogdl", "credentials.json");

    public static string EffectiveAuthConfigPath => FindExistingAuthConfigPath() ?? AuthConfigPath;

    private static string WebViewUserData =>
        Path.Combine(PathHelper.AppDataDir, "gog-webview");

    public static string? FindExistingAuthConfigPath()
    {
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var candidates = new[]
        {
            AuthConfigPath,
            Path.Combine(roaming, "heroic", "gog_store", "auth.json"),
            Path.Combine(user, ".config", "heroic", "gog_store", "auth.json"),
            Path.Combine(user, ".config", "heroic", "gog_store", "credentials.json"),
            Path.Combine(user, ".config", "gogdl", "credentials.json"),
        };
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (File.Exists(candidate) &&
                    GogdlCli.HasAuthenticatedCredentials(File.ReadAllText(candidate)))
                    return candidate;
            }
            catch { /* try the next known location */ }
        }
        return null;
    }

    public void AttachDispatcher(DispatcherQueue queue) => _queue = queue;

    public async Task<AuthResult> SignInAsync(string gogdlPath, CancellationToken ct = default)
    {
        await _sessionLock.WaitAsync(ct).ConfigureAwait(true);
        try
        {
            if (_disposed) throw new ObjectDisposedException(nameof(GogAuthService));
            if (string.IsNullOrWhiteSpace(gogdlPath) || !File.Exists(gogdlPath))
                return Failure("gogdl is unavailable. Refresh and try again.");

            var queue = _queue;
            if (queue is null)
                return Failure("GOG sign-in UI is not ready. Reopen Settings and try again.");

            Directory.CreateDirectory(Path.GetDirectoryName(AuthConfigPath)!);
            Directory.CreateDirectory(WebViewUserData);

            string authorizationCode;
            try
            {
                authorizationCode = await CaptureAuthorizationCodeAsync(queue, ct).ConfigureAwait(true);
            }
            catch (TimeoutException)
            {
                return Failure("GOG sign-in timed out. Try Connect again.");
            }
            catch (OperationCanceledException)
            {
                return Failure(ct.IsCancellationRequested
                    ? "GOG sign-in was cancelled."
                    : "GOG sign-in window was closed before login completed.");
            }

            var pendingPath = AuthConfigPath + ".pending-" + Guid.NewGuid().ToString("N");
            try
            {
                using var exchangeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                exchangeCts.CancelAfter(ExchangeTimeout);
                var (exitCode, stdout, _) = await CliRunner.RunAsync(
                        gogdlPath,
                        GogdlCli.AuthCodeArgs(pendingPath, authorizationCode),
                        null,
                        null,
                        exchangeCts.Token)
                    .ConfigureAwait(true);

                var pendingJson = ReadCredentialFile(pendingPath);
                if (exitCode != 0 ||
                    !GogdlCli.HasAuthenticatedCredentials(stdout) ||
                    !GogdlCli.HasAuthenticatedCredentials(pendingJson))
                {
                    AppLog.Warn($"GOG authorization exchange failed (exit {exitCode}).");
                    return Failure("GOG rejected the sign-in. Try Connect again.");
                }

                File.Move(pendingPath, AuthConfigPath, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(pendingPath)) File.Delete(pendingPath); }
                catch { /* best effort */ }
            }

            return new AuthResult
            {
                Ok = true,
                RequiresUserAction = false,
                Message = "GOG connected.",
            };
        }
        catch (OperationCanceledException)
        {
            return Failure("GOG sign-in was cancelled.");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"GOG sign-in failed: {ex.GetType().Name}: {ex.Message}");
            return Failure("GOG sign-in failed. Try Connect again.");
        }
        finally
        {
            CloseWindow();
            _sessionLock.Release();
        }
    }

    private async Task<string> CaptureAuthorizationCodeAsync(DispatcherQueue queue, CancellationToken ct)
    {
        var readyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var codeTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Build()
        {
            try
            {
                CloseWindowCore();
                var web = new WebView2();
                var window = new Window
                {
                    Title = "GOG sign in — Exo",
                    Content = new Grid { Children = { web } },
                };
                try { window.AppWindow.Resize(new SizeInt32(980, 760)); }
                catch { /* best effort */ }

                lock (_stateGate)
                {
                    _window = window;
                    _web = web;
                    _codeTcs = codeTcs;
                }

                window.Closed += (_, _) => ResetClosedWindow(window, web, codeTcs);
                window.Activate();
                _ = InitializeWebAsync(web, readyTcs, codeTcs);
            }
            catch (Exception ex)
            {
                readyTcs.TrySetException(ex);
                codeTcs.TrySetException(ex);
            }
        }

        if (!queue.HasThreadAccess)
        {
            if (!queue.TryEnqueue(Build))
                throw new InvalidOperationException("GOG sign-in UI queue is unavailable.");
        }
        else
            Build();

        var deadline = DateTimeOffset.UtcNow + LoginTimeout;
        await readyTcs.Task.WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(true);
        var remaining = deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) throw new TimeoutException();
        return await codeTcs.Task.WaitAsync(remaining, ct).ConfigureAwait(true);
    }

    private static async Task InitializeWebAsync(
        WebView2 web,
        TaskCompletionSource<bool> readyTcs,
        TaskCompletionSource<string> codeTcs)
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync(
                null,
                WebViewUserData,
                new CoreWebView2EnvironmentOptions());
            await web.EnsureCoreWebView2Async(environment);
            var core = web.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.NavigationStarting += (_, e) =>
            {
                if (!GogdlCli.TryExtractAuthorizationCode(e.Uri, out var code)) return;
                e.Cancel = true;
                codeTcs.TrySetResult(code);
            };
            core.NewWindowRequested += (_, e) =>
            {
                // Keep OAuth popups inside the isolated sign-in view. Unknown or
                // non-web schemes must not escape into arbitrary desktop apps.
                e.Handled = true;
                if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) ||
                    uri.Scheme is not ("http" or "https"))
                    return;
                core.Navigate(uri.AbsoluteUri);
            };
            core.Navigate(GogdlCli.LoginUrl);
            readyTcs.TrySetResult(true);
        }
        catch (Exception ex)
        {
            readyTcs.TrySetException(ex);
            codeTcs.TrySetException(ex);
        }
    }

    private static AuthResult Failure(string message) => new()
    {
        Ok = false,
        RequiresUserAction = true,
        Message = message,
    };

    private static string? ReadCredentialFile(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch { return null; }
    }

    private void ResetClosedWindow(
        Window window,
        WebView2 web,
        TaskCompletionSource<string> codeTcs)
    {
        lock (_stateGate)
        {
            if (ReferenceEquals(_window, window)) _window = null;
            if (ReferenceEquals(_web, web)) _web = null;
            if (ReferenceEquals(_codeTcs, codeTcs)) _codeTcs = null;
        }
        codeTcs.TrySetCanceled();
    }

    private void CloseWindow()
    {
        var queue = _queue;
        if (queue is null) return;
        if (!queue.HasThreadAccess) queue.TryEnqueue(CloseWindowCore);
        else CloseWindowCore();
    }

    private void CloseWindowCore()
    {
        Window? window;
        lock (_stateGate) window = _window;
        try { window?.Close(); }
        catch { /* best effort */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_stateGate) _codeTcs?.TrySetCanceled();
        CloseWindow();
    }
}
