using System.Diagnostics;
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

    private readonly HttpClient _http;
    private bool _disposed;

    public int Port { get; }
    public int ClientPid { get; }

    private RiotClientApi(HttpClient http, int port, int clientPid)
    {
        _http = http;
        Port = port;
        ClientPid = clientPid;
    }

    public static string LockfilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Riot Games", "Riot Client", "Config", "lockfile");

    /// <summary>Connect using the running client's lockfile, or null when it is not up.</summary>
    public static RiotClientApi? TryConnect()
    {
        var creds = ReadLockfile();
        if (creds is null) return null;
        var (pid, port, password, protocol) = creds.Value;

        var handler = new HttpClientHandler
        {
            // The client serves a self-signed cert on loopback only. Trust is
            // scoped to 127.0.0.1 so this cannot weaken any other connection.
            ServerCertificateCustomValidationCallback = (request, _, _, _) =>
                request.RequestUri?.IsLoopback == true,
        };
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri($"{protocol}://127.0.0.1:{port}"),
            Timeout = TimeSpan.FromSeconds(20),
        };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes($"riot:{password}")));
        return new RiotClientApi(http, port, pid);
    }

    /// <summary>
    /// Connect, starting the Riot Client hidden first if it is not running.
    /// </summary>
    public static async Task<RiotClientApi?> ConnectAsync(
        string riotClientServicesPath, TimeSpan timeout, CancellationToken ct)
    {
        // A cancelled Play request must not cold-start Riot after the UI has
        // already abandoned the handoff.
        ct.ThrowIfCancellationRequested();
        var api = TryConnect();
        if (api is not null && await api.IsReadyAsync(ct).ConfigureAwait(false))
            return api;
        api?.Dispose();

        ct.ThrowIfCancellationRequested();
        if (!ProcessHelper.IsProcessRunning("RiotClientServices"))
        {
            // No --launch-product here: this only needs the client's API up.
            ProcessHelper.StartHidden(riotClientServicesPath, "--headless");
        }

        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            await Task.Delay(500, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            api = TryConnect();
            if (api is not null && await api.IsReadyAsync(ct).ConfigureAwait(false))
                return api;
            api?.Dispose();
        }
        ct.ThrowIfCancellationRequested();
        return null;
    }

    private static (int Pid, int Port, string Password, string Protocol)? ReadLockfile()
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
            if (parts.Length < 5) return null;
            if (!int.TryParse(parts[1], out var pid)) return null;
            if (!int.TryParse(parts[2], out var port)) return null;
            var protocol = string.IsNullOrWhiteSpace(parts[4]) ? "https" : parts[4];
            return (pid, port, parts[3], protocol);
        }
        catch (Exception ex)
        {
            AppLog.Debug("Riot lockfile read failed: " + ex.Message);
            return null;
        }
    }

    public async Task<bool> IsReadyAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync("/riotclient/region-locale", ct)
                .ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
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
        try
        {
            using var content = new StringContent("{}", Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(
                $"/product-launcher/v1/products/{product}/patchlines/{patchline}", content, ct)
                .ConfigureAwait(false);
            var body = (await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim();
            var result = InterpretLaunchResponse(resp.StatusCode, body);
            if (!result.Accepted)
            {
                AppLog.Warn($"Riot launch API {(int)resp.StatusCode}: {Truncate(body)}");
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
        catch (Exception ex)
        {
            AppLog.Warn("Riot launch API failed: " + ex.Message);
            return RiotLaunchApiResult.Failed(ex.Message);
        }
    }

    internal static RiotLaunchApiResult InterpretLaunchResponse(HttpStatusCode statusCode, string? body)
    {
        var status = (int)statusCode;
        var success = status is >= 200 and <= 299;
        if (success)
            return RiotLaunchApiResult.Started(ExtractSessionId(body));

        if (statusCode == HttpStatusCode.Locked && IsAlreadyLaunched(body))
            return RiotLaunchApiResult.Existing(ExtractSessionId(body));

        return RiotLaunchApiResult.Failed(Truncate(body));
    }

    private static bool IsAlreadyLaunched(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        try
        {
            using var doc = JsonDocument.Parse(body, JsonOpts);
            return doc.RootElement.TryGetProperty("errorCode", out var code)
                   && string.Equals(code.GetString(), "already_launched", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
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
        try
        {
            using var content = new StringContent("{}", Encoding.UTF8, "application/json");
            using var resp = await _http.PutAsync(
                $"/rnet-product-registry/v4/patch-requests/products/{product}/patchlines/{patchline}",
                content, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode) return true;
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
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
        try
        {
            using var resp = await _http.DeleteAsync(
                $"/patch-proxy/v2/patch-jobs/products/{product}/patchlines/{patchline}", ct)
                .ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            AppLog.Debug("Riot cancel patch failed: " + ex.Message);
            return false;
        }
    }

    /// <summary>Close a product Riot launched for us.</summary>
    public async Task<bool> CloseProductAsync(string product, string patchline, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.DeleteAsync(
                $"/product-launcher/v1/products/{product}/patchlines/{patchline}", ct)
                .ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
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
        try
        {
            using var resp = await _http.GetAsync(path, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Debug($"Riot API GET {path} failed: {ex.Message}");
            return null;
        }
    }

    private static string Truncate(string? s) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= 200 ? s : s[..200]);

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
    string? SessionId,
    string? Error)
{
    public static RiotLaunchApiResult Started(string? sessionId) =>
        new(true, false, sessionId, null);

    public static RiotLaunchApiResult Existing(string? sessionId) =>
        new(true, true, sessionId, null);

    public static RiotLaunchApiResult Failed(string? error) =>
        new(false, false, null, error);
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
