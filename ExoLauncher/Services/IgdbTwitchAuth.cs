using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ExoLauncher.Services;

/// <summary>IGDB requires a Twitch application token. User OAuth tokens cannot authenticate its API.</summary>
internal static class IgdbTwitchAuth
{
    private static readonly object Gate = new();
    private static readonly SemaphoreSlim RefreshGate = new(1, 1);
    private static string? MemoryToken;
    private static string? MemoryScope;
    private static DateTimeOffset MemoryUntil;
    private static long Generation;
    private static (string? Id, string? Secret)? MemoryCredentials;

    internal static bool CanQuery() => Credentials() is (not null, not null);

    internal static string? ClientId() => Credentials().Id;

    internal static bool HasAppToken()
    {
        var (clientId, clientSecret) = Credentials();
        return HasAppToken(clientId, clientSecret);
    }

    /// <summary>
    /// The saved Twitch app pair, decrypted once and kept until <see cref="Invalidate"/>.
    /// One IGDB cover lookup used to make five DPAPI reads.
    /// </summary>
    private static (string? Id, string? Secret) Credentials()
    {
        long generation;
        lock (Gate)
        {
            if (MemoryCredentials is { } cached) return cached;
            generation = Generation;
        }
        var fresh = (CatalogApiKeyStore.TryReadTwitchClientId(), CatalogApiKeyStore.TryReadTwitchClientSecret());
        lock (Gate)
        {
            // A save that raced this read invalidated it; the next call re-reads.
            if (generation == Generation) MemoryCredentials = fresh;
        }
        return fresh;
    }

    internal static bool HasAppToken(string? clientId, string? clientSecret)
    {
        var scope = CredentialScope(clientId, clientSecret);
        lock (Gate) return scope is not null && MemoryScope == scope &&
            MemoryToken is not null && DateTimeOffset.UtcNow < MemoryUntil;
    }

    internal static void Invalidate()
    {
        lock (Gate)
        {
            Generation++;
            MemoryToken = null;
            MemoryScope = null;
            MemoryUntil = default;
            MemoryCredentials = null;
        }
    }

    // Kept only to clear files written by older versions when the user removes the app configuration.
    internal static bool ClearUserSession()
    {
        Invalidate();
        var access = StoreSecretStore.Save("igdb-twitch-access.bin", null, value => value);
        var refresh = StoreSecretStore.Save("igdb-twitch-refresh.bin", null, value => value);
        var until = StoreSecretStore.Save("igdb-twitch-until.bin", null, value => value);
        return access && refresh && until;
    }

    internal static Task<string?> GetAccessTokenAsync(HttpClient http, CancellationToken ct)
    {
        var (clientId, clientSecret) = Credentials();
        return GetAccessTokenAsync(http, clientId, clientSecret, ct);
    }

    internal static async Task<string?> GetAccessTokenAsync(
        HttpClient http, string? clientId, string? clientSecret, CancellationToken ct)
    {
        var scope = CredentialScope(clientId, clientSecret);
        if (scope is null) return null;
        await RefreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            long generation;
            lock (Gate)
            {
                if (MemoryScope == scope && MemoryToken is not null && DateTimeOffset.UtcNow < MemoryUntil)
                    return MemoryToken;
                generation = Generation;
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId!,
                ["client_secret"] = clientSecret!,
                ["grant_type"] = "client_credentials",
            });
            using var response = await http.PostAsync("https://id.twitch.tv/oauth2/token", content, timeout.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var limited = new MemoryStream();
            var buffer = new byte[4096];
            int read;
            while ((read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (limited.Length + read > 16_384) return null;
                limited.Write(buffer, 0, read);
            }
            using var doc = JsonDocument.Parse(limited.ToArray());
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("access_token", out var tokenElement) ||
                tokenElement.ValueKind != JsonValueKind.String ||
                TokenNormalize(tokenElement.GetString()) is not { } token ||
                !root.TryGetProperty("expires_in", out var expires) ||
                expires.ValueKind != JsonValueKind.Number || !expires.TryGetInt32(out var seconds) || seconds <= 60)
                return null;
            if (root.TryGetProperty("token_type", out var type) &&
                (type.ValueKind != JsonValueKind.String || !string.Equals(type.GetString(), "bearer", StringComparison.OrdinalIgnoreCase)))
                return null;
            lock (Gate)
            {
                // A settings change during the request must not restore an invalidated connection.
                if (generation != Generation) return null;
                MemoryToken = token;
                MemoryScope = scope;
                MemoryUntil = DateTimeOffset.UtcNow.AddSeconds(Math.Min(seconds - 60, 60 * 60 * 24 * 50));
            }
            return token;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
        finally { RefreshGate.Release(); }
    }

    internal static async Task<object> ConnectAsync(CancellationToken ct = default)
    {
        if (!CanQuery())
            return new { ok = false, message = "IGDB needs a Twitch app client ID and client secret. Twitch user sign-in does not grant IGDB access." };
        using var http = ExoHttp.Create(TimeSpan.FromSeconds(12));
        var token = await GetAccessTokenAsync(http, ct).ConfigureAwait(false);
        return token is null
            ? new { ok = false, message = "The Twitch app could not connect to IGDB. Check its credentials and try again." }
            : new { ok = true, message = "The Twitch app is connected for IGDB." };
    }

    private static string? CredentialScope(string? clientId, string? clientSecret) =>
        string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret) ? null :
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(clientId + "\n" + clientSecret)));

    private static string? TokenNormalize(string? raw)
    {
        var trimmed = (raw ?? string.Empty).Trim();
        return trimmed.Length is >= 8 and <= 512 &&
            trimmed.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_') ? trimmed : null;
    }
}
