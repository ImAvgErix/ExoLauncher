using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services.DiscordClient;

/// <summary>
/// Signing in by QR code, the way Discord's own web login does: Exo never sees a password.
/// Over remote-auth-gateway.discord.gg (v2) Exo sends a fresh RSA-2048 public key, proves it
/// holds the private one (SHA-256 of the nonce Discord encrypted to it), and shows
/// discord.com/ra/&lt;fingerprint&gt; as a QR code. The phone app scans it, the user confirms on
/// the phone, and Discord returns a ticket that /users/@me/remote-auth/login trades for the
/// token, encrypted to Exo's key (RSA-OAEP, SHA-256). The key lives only in memory for that
/// one sign-in. Discord asking for a captcha ends the sign-in with a plain line.
/// </summary>
internal sealed class DiscordRemoteAuth
{
    private const string Gateway = "wss://remote-auth-gateway.discord.gg/?v=2";

    internal sealed record Step(string State, string? Url = null, string? User = null, string? Message = null);

    /// <summary>Runs one sign-in; returns the token, or null when it was cancelled, expired or failed (<paramref name="step"/> says which).</summary>
    internal static async Task<string?> RunAsync(Action<Step> step, CancellationToken ct)
    {
        using var rsa = RSA.Create(2048);
        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Origin", "https://discord.com");
        ws.Options.SetRequestHeader("User-Agent", DiscordRest.UserAgent);
        using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var sendGate = new SemaphoreSlim(1, 1);
        async Task Send(JsonObject message)
        {
            await sendGate.WaitAsync(ct).ConfigureAwait(false);
            try { await ws.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text, true, ct).ConfigureAwait(false); }
            finally { sendGate.Release(); }
        }
        byte[] Decrypt(JsonElement data, string name) =>
            rsa.Decrypt(Convert.FromBase64String(data.GetProperty(name).GetString() ?? ""), RSAEncryptionPadding.OaepSHA256);

        try
        {
            await ws.ConnectAsync(new Uri(Gateway), ct).ConfigureAwait(false);
            while (ws.State == WebSocketState.Open)
            {
                using var doc = await DiscordSession.ReceiveAsync(ws, 1 << 20, ct).ConfigureAwait(false);
                if (doc is null) break;
                var data = doc.RootElement;
                switch (data.GetProperty("op").GetString())
                {
                    case "hello":
                        var interval = data.GetProperty("heartbeat_interval").GetInt32();
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                while (!heartbeat.IsCancellationRequested)
                                {
                                    await Task.Delay(interval, heartbeat.Token).ConfigureAwait(false);
                                    await Send(new JsonObject { ["op"] = "heartbeat" }).ConfigureAwait(false);
                                }
                            }
                            catch { }
                        }, CancellationToken.None);
                        await Send(new JsonObject { ["op"] = "init", ["encoded_public_key"] = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo()) }).ConfigureAwait(false);
                        break;
                    case "nonce_proof":
                        var proof = Convert.ToBase64String(SHA256.HashData(Decrypt(data, "encrypted_nonce")))
                            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
                        await Send(new JsonObject { ["op"] = "nonce_proof", ["proof"] = proof }).ConfigureAwait(false);
                        break;
                    case "pending_remote_init":
                        step(new Step("waiting", Url: "https://discord.com/ra/" + data.GetProperty("fingerprint").GetString()));
                        break;
                    case "pending_ticket":
                        // "id:discriminator:avatar:username": shown so the user sees whose phone scanned it.
                        var parts = Encoding.UTF8.GetString(Decrypt(data, "encrypted_user_payload")).Split(':');
                        step(new Step("scanned", User: parts.Length >= 4 ? string.Join(':', parts[3..]) : null));
                        break;
                    case "pending_login":
                        using (var rest = new DiscordRest(null))
                        {
                            var reply = await rest.RemoteAuthLoginAsync(data.GetProperty("ticket").GetString() ?? "", ct).ConfigureAwait(false);
                            if (reply.Body is { ValueKind: JsonValueKind.Object } body && body.TryGetProperty("encrypted_token", out _))
                                return Encoding.UTF8.GetString(Decrypt(body, "encrypted_token"));
                            step(new Step("error", Message: reply.Error ?? "Discord didn't finish the sign-in. Try again."));
                            return null;
                        }
                    case "finish":
                        return Encoding.UTF8.GetString(Decrypt(data, "encrypted_token"));
                    case "cancel":
                        step(new Step("cancelled", Message: "Sign-in was canceled on the phone."));
                        return null;
                }
            }
            step(new Step("expired", Message: "The code expired."));
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
        catch (Exception ex)
        {
            AppLog.Info("Discord client: QR sign-in failed (" + ex.GetType().Name + ").");
            step(new Step("error", Message: "Discord's sign-in couldn't be reached. Try again."));
            return null;
        }
        finally
        {
            await heartbeat.CancelAsync().ConfigureAwait(false);
            if (ws.State == WebSocketState.Open)
                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None).ConfigureAwait(false); } catch { }
        }
    }
}
