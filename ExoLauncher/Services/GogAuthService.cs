using ExoLauncher.Adapters;
using ExoLauncher.Adapters.Cli;
using ExoLauncher.Helpers;
using ExoLauncher.Ui;
using Microsoft.UI.Dispatching;

namespace ExoLauncher.Services;

/// <summary>
/// Interactive GOG OAuth handoff. gogdl does not open a login page itself: the
/// host must capture GOG's authorization-code redirect and pass that code back
/// to gogdl with a persistent auth-config path.
/// </summary>
public sealed class GogAuthService : IDisposable
{
    private const string LogName = "GOG sign-in";
    private static readonly TimeSpan LoginTimeout = TimeSpan.FromMinutes(9);
    private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(45);

    private readonly object _stateGate = new();
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private DispatcherQueue? _queue;
    private StoreAuthWindow? _auth;
    private TaskCompletionSource<string>? _codeTcs;
    private bool _disposed;

    public static string AuthConfigPath =>
        Path.Combine(PathHelper.AppDataDir, "gogdl", "credentials.json");

    public static string EffectiveAuthConfigPath => FindExistingAuthConfigPath() ?? AuthConfigPath;

    private static string WebViewUserData =>
        Path.Combine(PathHelper.AppDataDir, "gog-webview");

    /// <summary>Written when Exo signs out of GOG, so Heroic's sign-in is not picked up until Exo signs in again.</summary>
    private static string SignedOutMarkerPath =>
        Path.Combine(PathHelper.AppDataDir, "gogdl", "signed-out");

    internal static void MarkSignedOut()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SignedOutMarkerPath)!);
        File.WriteAllText(SignedOutMarkerPath, "");
    }

    public static string? FindExistingAuthConfigPath()
    {
        var signedOut = File.Exists(SignedOutMarkerPath);
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
            if (signedOut && !string.Equals(candidate, AuthConfigPath, StringComparison.OrdinalIgnoreCase)) continue;
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
                return Failure("GOG sign-in is not available right now. Try again.");

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
                return Failure("GOG sign-in timed out. Try Sign in again.");
            }
            catch (OperationCanceledException)
            {
                return Failure(ct.IsCancellationRequested
                    ? "GOG sign-in was canceled."
                    : "The GOG sign-in window was closed before sign-in finished.");
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

                AppLog.Info($"{LogName}: gogdl auth --code exited {exitCode}.");
                var pendingJson = ReadCredentialFile(pendingPath);
                var verified = exitCode == 0 &&
                               GogdlCli.HasAuthenticatedCredentials(stdout) &&
                               GogdlCli.HasAuthenticatedCredentials(pendingJson);
                AppLog.Info($"{LogName}: credential verification {(verified ? "passed" : "failed")}.");
                if (!verified)
                {
                    AppLog.Warn($"GOG authorization exchange failed (exit {exitCode}).");
                    return Failure("GOG rejected the sign-in. Try Sign in again.");
                }

                File.Move(pendingPath, AuthConfigPath, overwrite: true);
                // Exo's own credentials are read first, so a stuck marker only hides Heroic's.
                try { File.Delete(SignedOutMarkerPath); }
                catch { /* best effort */ }
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
            return Failure("GOG sign-in was canceled.");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"GOG sign-in failed: {ex.GetType().Name}: {ex.Message}");
            return Failure("GOG sign-in failed. Try Sign in again.");
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
                var auth = new StoreAuthWindow(new StoreAuthWindow.Options(
                    LogName,
                    "Sign in to GOG",
                    WebViewUserData,
                    GogdlCli.LoginUrl,
                    IsAllowedNavigation,
                    InterceptNavigation: url =>
                    {
                        if (!GogdlCli.TryExtractAuthorizationCode(url, out var code)) return false;
                        AppLog.Info($"{LogName}: authorization code captured.");
                        codeTcs.TrySetResult(code);
                        return true;
                    }));
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

    /// <summary>GOG's own sign-in origins plus the identity providers GOG federates to.</summary>
    internal static bool IsAllowedNavigation(Uri uri) =>
        IsGogLoginUri(uri) || StoreAuthWindow.IsIdentityProvider(uri);
    internal static bool IsGogLoginUri(Uri uri) => ExoUrlLaunch.IsGogLogin(uri);

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
