using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExoLauncher.Helpers;

namespace ExoLauncher.Adapters.Riot;

/// <summary>
/// Riot Client's local control API.
///
/// RiotClientServices.exe --launch-product only navigates the client to a
/// product page; the user still has to press Play, which is why launches from
/// Exo appeared to do nothing. The client's own loopback REST API exposes the
/// real controls — eligibility, install state, patching with progress, launch,
/// and close — so Exo can drive Riot without touching its window, moving the
/// cursor, or going anywhere near anti-cheat.
///
/// Credentials come from the lockfile Riot writes for exactly this purpose:
///   %LOCALAPPDATA%\Riot Games\Riot Client\Config\lockfile
///   name:pid:port:password:protocol
/// </summary>
internal sealed class RiotClientApi : IDisposable
{
    private static readonly JsonDocumentOptions JsonOpts = new() { AllowTrailingCommas = true };
    internal static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
    // Riot answers the launch POST only after it has started the game, which
    // can take longer than an ordinary request on a cold client.
    private static readonly TimeSpan LaunchTimeout = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;
    private bool _disposed;

    private RiotClientApi(HttpClient http) => _http = http;

    public static string LockfilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Riot Games", "Riot Client", "Config", "lockfile");

    /// <summary>Connect using the running client's lockfile, or null when it is not up.</summary>
    public static RiotClientApi? TryConnect()
    {
        var creds = ReadLockfile();
        if (creds is null) return null;
        var (port, password, protocol) = creds.Value;

        var handler = new HttpClientHandler
        {
            // The client serves a self-signed cert on loopback only. Trust is
            // scoped to 127.0.0.1 so this cannot weaken any other connection.
            ServerCertificateCustomValidationCallback = (request, _, _, _) =>
                request.RequestUri?.IsLoopback == true,
        };
        // Every request carries its own deadline; the launch POST needs a
        // longer one than a client-wide default would allow.
        var http = ExoHttp.Create(System.Threading.Timeout.InfiniteTimeSpan, handler);
        http.BaseAddress = new Uri($"{protocol}://127.0.0.1:{port}");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes($"riot:{password}")));
        return new RiotClientApi(http);
    }

    /// <summary>
    /// Connect, starting the Riot Client hidden first if it is not running.
    /// </summary>
    public static async Task<RiotClientApi?> ConnectAsync(
        string riotClientServicesPath, TimeSpan timeout, CancellationToken ct, string? wakeArguments = null)
    {
        // A cancelled Play request must not cold-start Riot after the UI has
        // already abandoned the handoff.
        ct.ThrowIfCancellationRequested();
        RiotClientApi? api = null;
        try
        {
            var ready = await EnsureReadyAsync(async token =>
            {
                api?.Dispose();
                api = TryConnect();
                return api is not null && await api.IsReadyAsync(token).ConfigureAwait(false);
            }, () =>
            {
                // A lingering service process does not prove its API is awake.
                // Wake the requested product on the first attempt, including a
                // warm client, then continue that same launch when it is ready.
                HiddenStoreRuntime.BeginHandoff(StoreWindowHider.RiotUiProcessNames);
                using var started = ProcessHelper.StartHidden(riotClientServicesPath, wakeArguments ?? "--headless");
            }, timeout, ct).ConfigureAwait(false);
            if (ready) { var connected = api; api = null; return connected; }
            return null;
        }
        finally { api?.Dispose(); }
    }

    internal static async Task<bool> EnsureReadyAsync(
        Func<CancellationToken, Task<bool>> probe, Action wake, TimeSpan timeout, CancellationToken ct,
        Func<DateTimeOffset>? utcNow = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        utcNow ??= () => DateTimeOffset.UtcNow;
        delay ??= Task.Delay;
        ct.ThrowIfCancellationRequested();
        var deadline = utcNow() + timeout;
        if (await probe(ct).ConfigureAwait(false)) return true;
        ct.ThrowIfCancellationRequested();
        if (utcNow() >= deadline) return false;
        wake();
        while (utcNow() < deadline)
        {
            var remaining = deadline - utcNow();
            await delay(remaining < TimeSpan.FromMilliseconds(500) ? remaining : TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (await probe(ct).ConfigureAwait(false)) return true;
        }
        ct.ThrowIfCancellationRequested();
        return false;
    }

    private static (int Port, string Password, string Protocol)? ReadLockfile()
    {
        try
        {
            var path = LockfilePath;
            if (!File.Exists(path)) return null;
            // Riot holds the file open; share read/write or the open fails.
            using var fs = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            var raw = reader.ReadToEnd().Trim();
            var parts = raw.Split(':');
            // name:pid:port:password:protocol. The pid is not needed.
            if (parts.Length < 5) return null;
            if (!int.TryParse(parts[2], out var port)) return null;
            var protocol = string.IsNullOrWhiteSpace(parts[4]) ? "https" : parts[4];
            return (port, parts[3], protocol);
        }
        catch (Exception ex)
        {
            AppLog.Debug("Riot lockfile read failed: " + ex.Message);
            return null;
        }
    }

    public async Task<bool> IsReadyAsync(CancellationToken ct)
    {
        using var probe = CancellationTokenSource.CreateLinkedTokenSource(ct);
        probe.CancelAfter(TimeSpan.FromMilliseconds(750));
        try
        {
            using var resp = await _http.GetAsync("/riotclient/region-locale", probe.Token)
                .ConfigureAwait(false);
            // An authentication response proves the local API is listening.
            // Let launch report sign-in, instead of waiting 45 seconds for 200.
            return resp.IsSuccessStatusCode || resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Whether the Riot Client is signed in: its sign-in service holds an authorization (200), or has none (404, its
    /// sign-in screen). Null when it doesn't answer, or answers anything else.
    /// </summary>
    public async Task<bool?> IsSignedInAsync(CancellationToken ct)
    {
        using var probe = CancellationTokenSource.CreateLinkedTokenSource(ct);
        probe.CancelAfter(TimeSpan.FromMilliseconds(1500));
        try
        {
            using var resp = await _http.GetAsync("/rso-auth/v1/authorization", probe.Token).ConfigureAwait(false);
            return resp.IsSuccessStatusCode ? true : resp.StatusCode == HttpStatusCode.NotFound ? false : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    /// <summary>"installed", "not_installed", or null when unknown.</summary>
    public async Task<string?> GetInstallStateAsync(string product, string patchline, CancellationToken ct)
    {
        var body = await GetStringAsync(
            $"/rnet-product-registry/v1/install-states/products/{product}/patchlines/{patchline}", ct)
            .ConfigureAwait(false);
        return body?.Trim().Trim('"');
    }

    public async Task<bool?> IsEligibleAsync(string product, string patchline, CancellationToken ct)
    {
        var body = await GetStringAsync(
            $"/product-launcher/v1/products/{product}/patchlines/{patchline}/eligibility", ct)
            .ConfigureAwait(false);
        if (bool.TryParse(body?.Trim(), out var eligible)) return eligible;
        return null;
    }

    /// <summary>
    /// Launch the game. Riot returns HTTP 423/already_launched when the same
    /// product session is warm; that response is idempotent success, not a
    /// launch failure.
    /// </summary>
    public async Task<RiotLaunchApiResult> LaunchAsync(string product, string patchline, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(LaunchTimeout);
        try
        {
            using var content = new StringContent("{}", Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(
                $"/product-launcher/v1/products/{product}/patchlines/{patchline}", content, timeout.Token)
                .ConfigureAwait(false);
            var body = (await resp.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false)).Trim();
            var result = InterpretLaunchResponse(resp.StatusCode, body);
            if (!result.Accepted)
            {
                AppLog.Warn($"Riot launch API {(int)resp.StatusCode}: {body}");
            }
            else if (result.AlreadyRunning)
            {
                AppLog.Info("Riot launch request reused the product's existing session.");
            }
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // Riot often starts the game and answers late. The caller checks
            // for the game process instead of reporting a refusal.
            AppLog.Warn($"Riot launch API did not answer within {LaunchTimeout.TotalSeconds:0} s.");
            return RiotLaunchApiResult.NoAnswer();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Riot launch API failed: " + ex.Message);
            return RiotLaunchApiResult.Failed(null, null);
        }
    }

    internal static RiotLaunchApiResult InterpretLaunchResponse(HttpStatusCode statusCode, string? body)
    {
        var status = (int)statusCode;
        var success = status is >= 200 and <= 299;
        if (success)
            return RiotLaunchApiResult.Started(ExtractSessionId(body));

        var errorCode = ReadErrorCode(body);
        if (statusCode == HttpStatusCode.Locked
            && string.Equals(errorCode, "already_launched", StringComparison.OrdinalIgnoreCase))
            return RiotLaunchApiResult.Existing(ExtractSessionId(body));

        return RiotLaunchApiResult.Failed(errorCode, statusCode);
    }

    private static string? ReadErrorCode(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body, JsonOpts);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("errorCode", out var code)
                   && code.ValueKind == JsonValueKind.String
                ? NullIfBlank(code.GetString())
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ExtractSessionId(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        var value = body.Trim();

        try
        {
            using var doc = JsonDocument.Parse(value, JsonOpts);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.String)
                return NullIfBlank(root.GetString());
            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var name in new[] { "sessionId", "session_id", "id" })
                {
                    if (root.TryGetProperty(name, out var id) && id.ValueKind == JsonValueKind.String)
                        return NullIfBlank(id.GetString());
                }

                if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                    value = message.GetString() ?? value;
            }
        }
        catch (JsonException)
        {
            // Some client builds return the session as plain text.
        }

        var match = Regex.Match(
            value,
            "session\\s+ID\\s+['\\\"](?<id>[^'\\\"]+)['\\\"]",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (match.Success)
            return NullIfBlank(match.Groups["id"].Value);

        return value.IndexOfAny(['{', '}', ' ', '\t', '\r', '\n']) < 0
            ? NullIfBlank(value.Trim('"'))
            : null;
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Ask Riot to install or update the product.</summary>
    public async Task<bool> RequestPatchAsync(string product, string patchline, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            using var content = new StringContent("{}", Encoding.UTF8, "application/json");
            using var resp = await _http.PutAsync(
                $"/rnet-product-registry/v4/patch-requests/products/{product}/patchlines/{patchline}",
                content, timeout.Token).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode) return true;
            var body = await resp.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            AppLog.Warn($"Riot patch request {(int)resp.StatusCode}: {Truncate(body)}");
            return false;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Riot patch request failed: " + ex.Message);
            return false;
        }
    }

    public async Task<bool> CancelPatchAsync(string product, string patchline, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            using var resp = await _http.DeleteAsync(
                $"/patch-proxy/v2/patch-jobs/products/{product}/patchlines/{patchline}", timeout.Token)
                .ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            AppLog.Debug("Riot cancel patch failed: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// URL of the product's theme manifest on Riot's CDN. It lists the same art
    /// the Riot Client renders, which is the only official art source for these
    /// titles — Riot publishes no cover endpoint.
    /// </summary>
    public async Task<string?> GetThemeManifestUrlAsync(string product, string patchline, CancellationToken ct)
    {
        var body = await GetStringAsync(
            $"/product-metadata/v2/products/{product}/patchlines/{patchline}", ct)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body, JsonOpts);
            if (doc.RootElement.TryGetProperty("default_theme_manifest", out var el))
            {
                var url = el.GetString();
                return string.IsNullOrWhiteSpace(url) ? null : url;
            }
        }
        catch (Exception ex)
        {
            AppLog.Debug("Riot theme manifest parse failed: " + ex.Message);
        }
        return null;
    }

    public async Task<RiotPatchState?> GetPatchStateAsync(string product, string patchline, CancellationToken ct)
    {
        var body = await GetStringAsync(
            $"/patch-proxy/v2/patch-states/products/{product}/patchlines/{patchline}", ct)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body, JsonOpts);
            var root = doc.RootElement;
            var state = root.TryGetProperty("state", out var s) ? s.GetString() ?? "" : "";
            var launchable = root.TryGetProperty("launchable", out var l)
                             && l.ValueKind == JsonValueKind.True;
            double percent = 0, speed = 0;
            long remainingMs = 0, bytesToDo = 0, bytesDone = 0;
            var phase = "";
            if (root.TryGetProperty("progress", out var p) && p.ValueKind == JsonValueKind.Object)
            {
                percent = ReadDouble(p, "overallProgress") * 100.0;
                speed = ReadDouble(p, "currentSpeedMbps");
                remainingMs = (long)ReadDouble(p, "totalTimeRemainingMs");
                bytesToDo = (long)ReadDouble(p, "totalBytesToDo");
                bytesDone = (long)ReadDouble(p, "totalBytesDone");
                phase = p.TryGetProperty("phase", out var ph) ? ph.GetString() ?? "" : "";
            }
            return new RiotPatchState(state, launchable, percent, speed, remainingMs,
                bytesToDo, bytesDone, phase);
        }
        catch (Exception ex)
        {
            AppLog.Debug("Riot patch state parse failed: " + ex.Message);
            return null;
        }
    }

    private static double ReadDouble(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number
            ? el.GetDouble()
            : 0.0;

    private async Task<string?> GetStringAsync(string path, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ReadTimeout);
        try
        {
            using var resp = await _http.GetAsync(path, timeout.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Debug($"Riot API GET {path} failed: {ex.Message}");
            return null;
        }
    }

    private static string Truncate(string? s) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= 200 ? s : s[..200]);

    /// <summary>
    /// Ask a running Riot Client to exit. Never starts the client and never
    /// touches Vanguard. Best-effort — callers still soft-close process names.
    /// </summary>
    internal static void TryRequestShutdown() => _ = RequestQuitAsync(CancellationToken.None);

    /// <summary>
    /// The Riot Client's own quit, POST /process-control/v1/process/quit (in
    /// RiotClientFoundation.dll, 2026): Riot Client Services exits and takes its UI
    /// with it. The older /riotclient/kill and /riotclient/kill-ux are gone from it.
    /// False when the client is not reachable or did not accept.
    /// </summary>
    internal static async Task<bool> RequestQuitAsync(CancellationToken ct)
    {
        try
        {
            using var api = TryConnect();
            if (api is null) return false;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(RequestTimeout);
            using var response = await api._http.PostAsync("/process-control/v1/process/quit", null, timeout.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Riot may already be exiting.
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _http.Dispose(); } catch { /* */ }
    }
}

internal sealed record RiotLaunchApiResult(
    bool Accepted,
    bool AlreadyRunning,
    bool TimedOut,
    string? SessionId,
    string? ErrorCode,
    HttpStatusCode? StatusCode)
{
    public static RiotLaunchApiResult Started(string? sessionId) =>
        new(true, false, false, sessionId, null, null);

    public static RiotLaunchApiResult Existing(string? sessionId) =>
        new(true, true, false, sessionId, null, null);

    /// <summary>The launch POST got no answer in time; the game may still be starting.</summary>
    public static RiotLaunchApiResult NoAnswer() =>
        new(false, false, true, null, null, null);

    public static RiotLaunchApiResult Failed(string? errorCode, HttpStatusCode? statusCode) =>
        new(false, false, false, null, errorCode, statusCode);
}

/// <summary>Patch progress straight from Riot — no estimation on Exo's side.</summary>
internal sealed record RiotPatchState(
    string State,
    bool Launchable,
    double Percent,
    double SpeedMbps,
    long RemainingMs,
    long BytesToDo,
    long BytesDone,
    string Phase)
{
    public bool IsUpToDate => string.Equals(State, "UpToDate", StringComparison.OrdinalIgnoreCase);

    public bool IsPatching =>
        !IsUpToDate && !string.IsNullOrEmpty(State)
        && !string.Equals(State, "Error", StringComparison.OrdinalIgnoreCase);
}
