using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services.DiscordClient;

/// <summary>
/// Discord's "it's really you" step (read from its web client, module openMFAModal): an owner's or other sensitive
/// action answers 401 with code 60003 and an mfa object (a ticket and the ways to verify). The client asks for a code,
/// POSTs mfa/finish {ticket, mfa_type, data} for a short-lived token and sends the same request again once with
/// X-Discord-MFA-Authorization. Here the ticket, the waiting request and the token never leave native code: the page
/// gets an id and the ways, and sends back only what the person typed.
/// </summary>
internal sealed partial class DiscordRest
{
    internal const int MfaRequired = 60003;

    /// <summary>The ways Exo can verify without a browser, as Discord names them (a passkey or security key needs discord.com's own page).</summary>
    internal static readonly string[] MfaMethods = ["totp", "sms", "backup", "password"];

    /// <summary>A check Discord asked for: the id the page answers with and the ways Discord offers, in its order.</summary>
    internal sealed record MfaAsk(string Id, IReadOnlyList<string> Methods);

    internal sealed record MfaChallenge(string Ticket, IReadOnlyList<string> Methods);

    // How long a check waits for its code; Discord's ticket lasts about as long.
    private static readonly TimeSpan MfaWait = TimeSpan.FromMinutes(5);

    private sealed record PendingMfa(string Ticket, IReadOnlyList<string> Methods, Func<string, CancellationToken, Task<Reply>> Retry, DateTime Until);

    private readonly Dictionary<string, PendingMfa> _mfa = [];
    private readonly Lock _mfaGate = new();

    /// <summary>
    /// Discord's step-up ask in an answer, as its client recognizes it (401, code 60003, an mfa object with a ticket),
    /// with the ways Exo can use; null for any other answer.
    /// </summary>
    internal static MfaChallenge? ReadMfa(HttpStatusCode status, JsonElement? body)
    {
        if (status != HttpStatusCode.Unauthorized || body is not { ValueKind: JsonValueKind.Object } json) return null;
        if (!json.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.Number || !code.TryGetInt32(out var number) || number != MfaRequired) return null;
        if (!json.TryGetProperty("mfa", out var mfa) || mfa.ValueKind != JsonValueKind.Object) return null;
        if (!mfa.TryGetProperty("ticket", out var ticket) || ticket.GetString() is not { Length: > 0 } t) return null;
        var methods = new List<string>();
        if (mfa.TryGetProperty("methods", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var method in list.EnumerateArray())
                if (method.ValueKind == JsonValueKind.Object && method.TryGetProperty("type", out var type) && type.GetString() is { } name
                    && MfaMethods.Contains(name) && !methods.Contains(name))
                    methods.Add(name);
        return new MfaChallenge(t, methods);
    }

    // Holds the request until the person verifies; one Discord wants a passkey for is said plainly instead.
    private Reply AskMfa(MfaChallenge challenge, JsonElement? body, Func<string, CancellationToken, Task<Reply>> retry)
    {
        AppLog.Info("Discord client: Discord asks to verify (" + (challenge.Methods.Count == 0 ? "no way Exo can do" : string.Join(", ", challenge.Methods)) + ").");
        if (challenge.Methods.Count == 0)
            return new Reply(body, "Discord wants a passkey or security key for this. Do it once in the Discord app.");
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var now = DateTime.UtcNow;
        lock (_mfaGate)
        {
            foreach (var old in _mfa.Where(p => p.Value.Until <= now).Select(p => p.Key).ToList()) _mfa.Remove(old);
            _mfa[id] = new PendingMfa(challenge.Ticket, challenge.Methods, retry, now + MfaWait);
        }
        return new Reply(body, "Discord wants to check it's really you.", new MfaAsk(id, challenge.Methods));
    }

    private PendingMfa? Pending(string id)
    {
        lock (_mfaGate)
        {
            if (!_mfa.TryGetValue(id, out var pending)) return null;
            if (pending.Until > DateTime.UtcNow) return pending;
            _mfa.Remove(id);
            return null;
        }
    }

    /// <summary>
    /// Verifies with what the person typed and, when Discord takes it, sends the waiting request again with its token
    /// (its answer is this call's). A wrong code keeps the check open for another try and says so with the ask.
    /// </summary>
    internal async Task<Reply> FinishMfaAsync(string id, string method, string data, CancellationToken ct)
    {
        if (Pending(id) is not { } pending) return new Reply(null, "That check ran out of time. Try again.");
        if (!pending.Methods.Contains(method)) return new Reply(null, "Discord didn't offer that way to verify.");
        var finish = await SendAsync(HttpMethod.Post, "mfa/finish", new JsonObject { ["ticket"] = pending.Ticket, ["mfa_type"] = method, ["data"] = data }, ct).ConfigureAwait(false);
        // Still waiting (the ask comes back with the error), so the dialog stays for another try.
        if (finish.Error is not null) return finish with { Mfa = new MfaAsk(id, pending.Methods) };
        if (finish.Body is not { ValueKind: JsonValueKind.Object } body || !body.TryGetProperty("token", out var token) || token.GetString() is not { Length: > 0 } mfaToken)
            return new Reply(null, "Discord didn't take that. Try again.");
        lock (_mfaGate) _mfa.Remove(id);
        return await pending.Retry(mfaToken, ct).ConfigureAwait(false);
    }

    /// <summary>Discord texts a code to the account's phone for this check; the answer has the masked number it went to.</summary>
    internal async Task<Reply> SendMfaSmsAsync(string id, CancellationToken ct) => Pending(id) is { } pending && pending.Methods.Contains("sms")
        ? await SendAsync(HttpMethod.Post, "auth/mfa/sms/send", new JsonObject { ["ticket"] = pending.Ticket }, ct).ConfigureAwait(false)
        : new Reply(null, "That check ran out of time. Try again.");

    /// <summary>Cancel: the waiting request is dropped.</summary>
    internal void CancelMfa(string id)
    {
        lock (_mfaGate) _mfa.Remove(id);
    }
}
