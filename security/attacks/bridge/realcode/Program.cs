using System.Text;
using ExoLauncher.Helpers;   // WebViewTrustPolicy (real)
using ExoLauncher.Services;  // ExoBridgeProtocol, BridgeRequestRegistry (real)

// Security attack harness against the REAL bridge classes.
// Groups: ORIGIN (attack 2), PARSE + DEDUP (attack 3).
// Each line prints PASS/FAIL where PASS = "the defense behaved as a defense".

var failures = 0;
var lines = new List<string>();

void Check(string group, string what, bool ok, string detail)
{
    if (!ok) failures++;
    lines.Add($"{(ok ? "PASS" : "FAIL")} | {group,-6} | {what,-52} | {detail}");
}

// ---------------------------------------------------------------------------
// ATTACK 2 — can the wrong origin reach the privileged bridge?
// WebHostBridge.OnMessage() gates every RPC on IsTrustedBridgeSource(e.Source).
// These are the exact e.Source URIs each embedded surface would carry.
// "blocked" is the SECURE outcome for every surface except the shell itself.
// ---------------------------------------------------------------------------
void Origin(string surface, string source, bool expectTrusted)
{
    var trusted = WebViewTrustPolicy.IsTrustedBridgeSource(source);
    var ok = trusted == expectTrusted;
    Check("ORIGIN", surface, ok,
        $"IsTrustedBridgeSource(\"{source}\") = {trusted} (want {(expectTrusted ? "trusted" : "blocked")})");
}

// The shell page: the only surface that SHOULD reach the bridge.
Origin("shell index (expect trusted)",            "https://app.exo-launcher.local/index.html", true);
Origin("shell deep path (expect trusted)",        "https://app.exo-launcher.local/assets/x.js", true);

// Was the finding: Exo Mouse is bundled same-origin under /mouse/ and the path-blind
// IsTrustedAppUri accepted it. IsTrustedBridgeSource refuses /mouse/ in any spelling.
Origin("exo-mouse iframe (same-origin)",          "https://app.exo-launcher.local/mouse/index.html", false);
Origin("exo-mouse, encoded path",                 "https://app.exo-launcher.local/%6Douse/index.html", false);

// Cross-origin embedded surfaces: the secure outcome is "blocked".
Origin("shaders frame",                           "https://shaders.exo-launcher.local/index.html", false);
Origin("covers resource host",                    "https://covers.exo-launcher.local/hero_x.jpg", false);
Origin("thumbs resource host",                    "https://thumbs.exo-launcher.local/x.jpg?w=240", false);
Origin("YouTube Music web player",                "https://music.youtube.com/", false);
Origin("Apple Music web player",                  "https://music.apple.com/", false);
Origin("Steam store sign-in",                     "https://store.steampowered.com/login/", false);
Origin("GOG login",                               "https://login.gog.com/", false);
Origin("Epic login",                              "https://www.epicgames.com/id/login", false);

// Origin-spoofing attempts that must stay blocked.
Origin("look-alike host suffix",                  "https://app.exo-launcher.local.evil.com/", false);
Origin("look-alike host prefix",                  "https://evil-app.exo-launcher.local/", false);
Origin("http (non-loopback)",                     "http://app.exo-launcher.local/index.html", false);
Origin("userinfo smuggling",                      "https://app.exo-launcher.local@evil.com/", false);
Origin("explicit non-default port",               "https://app.exo-launcher.local:8443/index.html", false);
Origin("IDN homoglyph (cyrillic a)",              "https://\u0430pp.exo-launcher.local/index.html", false);
Origin("file scheme",                             "file:///C:/windows/system32/x.html", false);
Origin("plain evil host",                         "https://evil.com/", false);
Origin("empty source",                            "", false);
Origin("non-loopback http random",               "http://10.0.0.5/index.html", false);

// ---------------------------------------------------------------------------
// ATTACK 3a — malformed / hostile RPC envelopes at the parse boundary.
// ExoBridgeProtocol.TryParseRequest is the first thing HandleAsync calls.
// "rejected" (returns false) is the secure outcome for every hostile envelope.
// ---------------------------------------------------------------------------
void Parse(string what, string? raw, bool expectAccepted)
{
    var accepted = ExoBridgeProtocol.TryParseRequest(raw, out var req);
    var ok = accepted == expectAccepted;
    var tail = accepted ? $"id.len={req.Id.Length} method={req.Method}" : "rejected";
    Check("PARSE", what, ok, $"{(accepted ? "accepted" : "rejected")} ({tail})");
}

Parse("well-formed request (control, accepted)", "{\"version\":1,\"id\":\"1\",\"method\":\"library.get\"}", true);
Parse("not JSON",                                "}{not json", false);
Parse("empty string",                            "", false);
Parse("null literal",                            "null", false);
Parse("JSON array root",                         "[1,2,3]", false);
Parse("missing id",                              "{\"method\":\"library.get\"}", false);
Parse("missing method",                          "{\"id\":\"1\"}", false);
Parse("id wrong type (number)",                  "{\"id\":5,\"method\":\"library.get\"}", false);
Parse("method wrong type (object)",              "{\"id\":\"1\",\"method\":{}}", false);
Parse("version mismatch",                        "{\"version\":2,\"id\":\"1\",\"method\":\"library.get\"}", false);
Parse("version wrong type",                      "{\"version\":\"1\",\"id\":\"1\",\"method\":\"x\"}", false);
Parse("id over 128 chars",                       "{\"id\":\"" + new string('a', 129) + "\",\"method\":\"x\"}", false);
Parse("method over 128 chars",                   "{\"id\":\"1\",\"method\":\"" + new string('m', 129) + "\"}", false);
Parse("blank id",                                "{\"id\":\"   \",\"method\":\"x\"}", false);

// Oversized payload: MaxRequestCharacters = 1 MiB.
var huge = "{\"id\":\"1\",\"method\":\"app.log\",\"params\":{\"message\":\"" + new string('A', 1024 * 1024) + "\"}}";
Parse("over 1 MiB request", huge, false);

// A payload just under the cap is accepted (proves the cap is the reason, not JSON size).
var big = "{\"id\":\"1\",\"method\":\"app.log\",\"params\":{\"message\":\"" + new string('A', 900_000) + "\"}}";
Parse("~900 KB request (accepted)", big, true);

// ---------------------------------------------------------------------------
// ATTACK 3b — hostile PARAM VALUES survive parsing untouched.
// The parser is not a sanitizer: path-like ids, control chars and odd Unicode
// pass through as data. This documents that id/path defenses must live in the
// handlers (NormalizeInstallRoot, IsSafe* in SettingsService), not the parser.
// Here "accepted" is EXPECTED; the check confirms the value reached params verbatim.
// ---------------------------------------------------------------------------
void ParamSurvives(string what, string paramsJson, string property, string expectedValue)
{
    var raw = "{\"id\":\"1\",\"method\":\"game.install\",\"params\":" + paramsJson + "}";
    var accepted = ExoBridgeProtocol.TryParseRequest(raw, out var req);
    var got = accepted && req.HasParams && req.Params.TryGetProperty(property, out var el) ? el.GetString() : null;
    Check("PARSE", what, got == expectedValue, $"params.{property} = {(got is null ? "<null>" : "\"" + got + "\"")}");
}

ParamSurvives("path-traversal id reaches handler verbatim",
    "{\"id\":\"..\\\\..\\\\windows\\\\system32\"}", "id", "..\\..\\windows\\system32");
ParamSurvives("unc path id reaches handler verbatim",
    "{\"path\":\"\\\\\\\\attacker\\\\share\"}", "path", "\\\\attacker\\share");

// ---------------------------------------------------------------------------
// ATTACK 3c — 20 parallel state-changing calls: does the bridge de-duplicate?
// BridgeRequestRegistry.Begin only tracks methods that have a Deadline().
// State-changing methods (game.install, game.update, settings.set, store.wishlist)
// have NO deadline, so Begin returns null -> NO bridge-level dedup or cap.
// A replayed id for a *tracked* method (library.get) is rejected as a collision.
// ---------------------------------------------------------------------------
var reg = new BridgeRequestRegistry();
using var lifetime = new CancellationTokenSource();

// (i) no deadline => no lease => no dedup for destructive methods.
foreach (var m in new[] { "game.install", "game.update", "settings.set", "store.wishlist", "game.uninstall" })
{
    var lease = reg.Begin(id: Guid.NewGuid().ToString(), method: m, lifetime.Token);
    Check("DEDUP", $"no bridge throttle for {m}", lease is null,
        lease is null ? "Begin()=null -> runs concurrently; dedup must be in the service layer" : "unexpectedly leased");
}

// (ii) 20 concurrent installs each get a fresh id and all pass the bridge freely.
var leased20 = 0;
Parallel.For(0, 20, _ =>
{
    var lease = reg.Begin(Guid.NewGuid().ToString(), "game.install", lifetime.Token);
    if (lease is not null) { Interlocked.Increment(ref leased20); lease.Dispose(); }
});
Check("DEDUP", "20 parallel game.install not gated by bridge", leased20 == 0,
    $"{leased20}/20 produced a registry lease (0 = bridge never throttles installs)");

// (iii) a tracked method DOES reject a duplicate in-flight id (replay guard).
var first = reg.Begin("dup-id", "library.get", lifetime.Token);
var threw = false;
try { reg.Begin("dup-id", "library.get", lifetime.Token); }
catch (InvalidOperationException) { threw = true; }
first?.Dispose();
Check("DEDUP", "duplicate id rejected for tracked method", threw,
    threw ? "second Begin(\"dup-id\") threw as a replay guard" : "duplicate id was NOT rejected");

// ---------------------------------------------------------------------------
var sb = new StringBuilder();
sb.AppendLine();
sb.AppendLine("=== Exo bridge attack harness (real product source) ===");
foreach (var l in lines) sb.AppendLine(l);
sb.AppendLine($"--- {lines.Count} checks, {failures} FAIL ---");
Console.WriteLine(sb.ToString());
return failures == 0 ? 0 : 1;
