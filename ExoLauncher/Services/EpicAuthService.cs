using System.Text.Json;
using ExoLauncher.Adapters;
using ExoLauncher.Adapters.Cli;
using ExoLauncher.Helpers;
using ExoLauncher.Ui;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;

namespace ExoLauncher.Services;

/// <summary>
/// Interactive Epic authorization-code handoff. Frozen Legendary builds wait
/// on stdin after opening a browser, which a hidden host process cannot
/// complete. Exo opens Epic's documented redirect in its sign-in browser
/// (<see cref="StoreAuthWindow"/>), then passes the code to
/// <c>legendary auth --code</c>.
/// </summary>
public sealed class EpicAuthService : IDisposable
{
    private const string LogName = "Epic sign-in";
    private static readonly TimeSpan LoginTimeout = TimeSpan.FromMinutes(9);
    private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(45);

    private readonly object _stateGate = new();
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private DispatcherQueue? _queue;
    private StoreAuthWindow? _auth;
    private TaskCompletionSource<string>? _codeTcs;
    private bool _disposed;

    private static string SharedWebViewUserData =>
        Path.Combine(PathHelper.AppDataDir, "epic-webview");

    /// <summary>
    /// A browser profile that remembers no Epic sign-in, for picking another
    /// account. Emptied before each use; Legendary keeps the session, not the browser.
    /// </summary>
    private static string FreshWebViewUserData =>
        Path.Combine(PathHelper.AppDataDir, "epic-webview-other");

    private string _webViewUserData = SharedWebViewUserData;
    private string WebViewUserData => _webViewUserData;

    public void AttachDispatcher(DispatcherQueue queue) => _queue = queue;

    /// <param name="configFolder">A Legendary config folder for an added account (LEGENDARY_CONFIG_PATH); null for Exo's own.</param>
    /// <param name="otherAccount">Open Epic's page without the remembered browser sign-in, so another account can sign in.</param>
    public async Task<AuthResult> SignInAsync(
        string legendaryPath, CancellationToken ct = default, string? configFolder = null, bool otherAccount = false)
    {
        await _sessionLock.WaitAsync(ct).ConfigureAwait(true);
        try
        {
            if (_disposed) throw new ObjectDisposedException(nameof(EpicAuthService));
            if (string.IsNullOrWhiteSpace(legendaryPath) || !File.Exists(legendaryPath))
                return Failure("Epic sign-in is not available right now. Try again.");

            var queue = _queue;
            if (queue is null)
                return Failure("Epic sign-in UI is not ready. Reopen Settings and try again.");

            _webViewUserData = otherAccount ? FreshWebViewUserData : SharedWebViewUserData;
            // A profile that could not be emptied may still be signed in as the last account.
            if (otherAccount && !TryDeleteProfile(FreshWebViewUserData))
                return Failure("Epic sign-in is still closing. Try again in a moment.");
            Directory.CreateDirectory(WebViewUserData);
            var environment = configFolder is null
                ? null
                : new Dictionary<string, string> { ["LEGENDARY_CONFIG_PATH"] = configFolder };

            string authorizationCode;
            try
            {
                authorizationCode = await CaptureAuthorizationCodeAsync(queue, ct).ConfigureAwait(true);
            }
            catch (TimeoutException)
            {
                return Failure("Epic sign-in timed out. Try Sign in again.");
            }
            catch (OperationCanceledException)
            {
                return Failure(ct.IsCancellationRequested
                    ? "Epic sign-in was canceled."
                    : "The Epic sign-in window was closed before sign-in finished.");
            }
            AppLog.Info($"{LogName}: authorization code captured; exchanging it through Legendary.");

            try
            {
                using var exchangeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                exchangeCts.CancelAfter(ExchangeTimeout);
                var (exitCode, _, stdErr) = await CliRunner.RunAsync(
                        legendaryPath,
                        LegendaryCli.AuthCodeArgs(authorizationCode),
                        null,
                        null,
                        exchangeCts.Token,
                        environment)
                    .ConfigureAwait(true);
                AppLog.Info($"{LogName}: legendary auth --code exited {exitCode}.");

                if (exitCode != 0)
                {
                    AppLog.Warn($"Epic authorization exchange failed (exit {exitCode}): {LastLine(stdErr)}");
                    return Failure("Epic rejected the sign-in. Try Sign in again.");
                }

                // An added account's folder holds only its session; the library probe is Exo's own account's.
                var verified = configFolder is null
                    ? await HasValidSessionAsync(legendaryPath, ct).ConfigureAwait(true)
                    : EpicAccounts.ReadSession(configFolder) is not null;
                AppLog.Info($"{LogName}: Legendary session verification {(verified ? "passed" : "failed")}.");
                if (!verified)
                    return Failure("Epic sign-in finished, but Exo could not confirm the account. Try Sign in again.");

                return new AuthResult
                {
                    Ok = true,
                    RequiresUserAction = false,
                    Message = "Epic account connected.",
                };
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                AppLog.Warn($"{LogName}: Legendary did not finish the exchange within {ExchangeTimeout.TotalSeconds:0}s.");
                return Failure("Epic sign-in timed out. Try Sign in again.");
            }
        }
        catch (OperationCanceledException)
        {
            return Failure("Epic sign-in was canceled.");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Epic sign-in failed: {ex.GetType().Name}: {ex.Message}");
            return Failure("Epic sign-in failed. Try Sign in again.");
        }
        finally
        {
            CloseWindow();
            _sessionLock.Release();
        }
    }

    /// <summary>
    /// Forgets the sign-in browser's remembered Epic account, so the next Sign in
    /// cannot bring back the previous one. False when the profile is in use.
    /// </summary>
    internal static bool ForgetBrowserSignIn() => TryDeleteProfile(SharedWebViewUserData);

    private static bool TryDeleteProfile(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"{LogName}: a browser profile was busy: {ex.GetType().Name}");
            return false;
        }
    }

    private static string LastLine(string? text)
    {
        var line = (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault() ?? "";
        return line.Length > 200 ? line[..200] : line;
    }

    private static async Task<bool> HasValidSessionAsync(string legendary, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            var probe = await CliRunner.RunJsonAsync(
                    legendary,
                    LegendaryCli.ListOwnedArgs(includeNonInstallable: false),
                    null,
                    timeout.Token)
                .ConfigureAwait(true);
            return LegendaryCli.IsAuthenticatedLibraryResponse(probe.ExitCode, probe.StdOut);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            AppLog.Debug("Legendary session verification failed: " + ex.Message);
            return false;
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
                var auth = new StoreAuthWindow(new StoreAuthWindow.Options(
                    LogName,
                    "Sign in to Epic Games",
                    WebViewUserData,
                    LegendaryCli.LoginUrl,
                    IsAllowedNavigation,
                    PageLoaded: web => TryReadAuthorizationCodeAsync(web, codeTcs)));
                lock (_stateGate)
                {
                    _auth = auth;
                    _codeTcs = codeTcs;
                }
                auth.Closed += () =>
                {
                    lock (_stateGate)
                    {
                        if (ReferenceEquals(_auth, auth)) _auth = null;
                        if (ReferenceEquals(_codeTcs, codeTcs)) _codeTcs = null;
                    }
                    codeTcs.TrySetCanceled();
                };
                _ = StartAsync(auth, readyTcs, codeTcs);
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
                throw new InvalidOperationException("Epic sign-in UI queue is unavailable.");
        }
        else
            Build();

        var deadline = DateTimeOffset.UtcNow + LoginTimeout;
        await readyTcs.Task.WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(true);
        var remaining = deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) throw new TimeoutException();
        return await codeTcs.Task.WaitAsync(remaining, ct).ConfigureAwait(true);
    }

    private static async Task StartAsync(
        StoreAuthWindow auth,
        TaskCompletionSource<bool> readyTcs,
        TaskCompletionSource<string> codeTcs)
    {
        try
        {
            await auth.StartAsync();
            readyTcs.TrySetResult(true);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"{LogName}: browser could not start: {ex.GetType().Name}: {ex.Message}");
            readyTcs.TrySetException(ex);
            codeTcs.TrySetException(ex);
        }
    }

    private static async Task TryReadAuthorizationCodeAsync(
        WebView2 web,
        TaskCompletionSource<string> codeTcs)
    {
        if (codeTcs.Task.IsCompleted) return;
        try
        {
            var uri = web.Source?.AbsoluteUri;
            if (!LegendaryCli.IsAuthorizationResultUri(uri)) return;

            var raw = await web.ExecuteScriptAsync("document.body ? document.body.innerText : ''");
            string? text = null;
            try { text = JsonSerializer.Deserialize<string>(raw); }
            catch (JsonException) { text = raw?.Trim('"'); }

            if (LegendaryCli.TryParseAuthorizationCode(text, out var code))
                codeTcs.TrySetResult(code);
            else
                AppLog.Info($"{LogName}: redirect page loaded without an authorization code (not signed in yet).");
        }
        catch (Exception ex)
        {
            // Another navigation may still yield a parseable page.
            AppLog.Debug($"{LogName}: reading the redirect page failed: {ex.GetType().Name}");
        }
    }

    /// <summary>Epic's own sign-in origins plus the identity providers Epic federates to.</summary>
    internal static bool IsAllowedNavigation(Uri uri) =>
        IsEpicLoginUri(uri) ||
        StoreAuthWindow.HostIs(uri, "epicgames.dev") ||
        StoreAuthWindow.HostIs(uri, "fortnite.com") ||
        StoreAuthWindow.IsIdentityProvider(uri);

    internal static bool IsEpicLoginUri(Uri uri) => ExoUrlLaunch.IsEpicLogin(uri);

    private static AuthResult Failure(string message) => new()
    {
        Ok = false,
        RequiresUserAction = true,
        Message = message,
    };

    private void CloseWindow()
    {
        var queue = _queue;
        if (queue is null) return;
        if (!queue.HasThreadAccess) queue.TryEnqueue(CloseWindowCore);
        else CloseWindowCore();
    }

    private void CloseWindowCore()
    {
        StoreAuthWindow? auth;
        lock (_stateGate)
        {
            auth = _auth;
            _auth = null;
        }
        try { auth?.Close(); }
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
