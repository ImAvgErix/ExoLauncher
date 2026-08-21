import { Hono } from "hono";
import type { Env } from "../env.ts";
import { AUTH_CODE_TTL_SEC, LOGIN_TTL_SEC, SESSION_TTL_SEC, emailMagicLinkEnabled } from "../env.ts";
import { unverifiedPasswordUserId } from "../magic-link-guard.ts";
import { createAuth } from "../auth.ts";
import { ApiError, ErrorCode } from "../errors.ts";
import { nowIso, randomHex, sha256Hex, sha256Base64Url } from "../crypto.ts";
import { assertPkceStart, assertPkceVerifier } from "../pkce.ts";
import { loopbackCallbackUrl, parseLoopbackRedirect } from "../loopback.ts";
import { clientIp, consumeRateLimit, scopedRateKey } from "../rate-limit.ts";
import { authErrorPage, checkEmailPage } from "../html.ts";
import { requireSession } from "../session.ts";
import { logError } from "../log.ts";
import { hasExactKeys, readBoundedJsonObject } from "../bounded-json.ts";

export const authRoutes = new Hono<{ Bindings: Env }>();

const MAX_AUTH_JSON_BYTES = 2048;
const INVALID_AUTH_REQUEST = "Invalid authentication request.";
const INVALID_SESSION_REQUEST = "Invalid session request.";

async function rateLimitOrThrow(db: D1Database, key: string, windowMs: number, max: number): Promise<void> {
  const result = await consumeRateLimit(db, key, { windowMs, max });
  if (!result.allowed) {
    throw new ApiError(429, ErrorCode.RATE_LIMITED, "Too many attempts. Try again later.", result.retryAfterSec);
  }
}

authRoutes.post("/v1/auth/start", async (c) => {
  await rateLimitOrThrow(
    c.env.DB,
    await scopedRateKey(c.env.BETTER_AUTH_SECRET, "auth-start-ip", clientIp(c.req.raw.headers)),
    10 * 60 * 1000,
    5,
  );
  const body = await readBoundedJsonObject(c.req.raw, MAX_AUTH_JSON_BYTES, INVALID_AUTH_REQUEST);
  const provider = body.provider;
  if (provider !== "google" && provider !== "email") {
    throw new ApiError(400, ErrorCode.INVALID_PROVIDER, "provider must be google or email.");
  }
  const requiredKeys = provider === "email"
    ? ["provider", "redirectUri", "codeChallenge", "codeChallengeMethod", "email"] as const
    : ["provider", "redirectUri", "codeChallenge", "codeChallengeMethod"] as const;
  if (!hasExactKeys(body, requiredKeys, ["state"])) {
    throw new ApiError(400, ErrorCode.INVALID_REQUEST, INVALID_AUTH_REQUEST);
  }
  const redirect = parseLoopbackRedirect(body.redirectUri);
  const { codeChallenge } = assertPkceStart({
    codeChallenge: body.codeChallenge,
    codeChallengeMethod: body.codeChallengeMethod,
  });
  const state = body.state === undefined
    ? randomHex(16)
    : typeof body.state === "string" && body.state.length > 0 && body.state.length <= 128
      ? body.state
      : null;
  if (state === null) {
    throw new ApiError(400, ErrorCode.INVALID_REQUEST, INVALID_AUTH_REQUEST);
  }

  if (provider === "email") {
    if (typeof body.email !== "string") {
      throw new ApiError(400, ErrorCode.INVALID_REQUEST, "email is required.");
    }
    const email = body.email.trim().toLowerCase();
    if (!/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email) || email.length > 254) {
      throw new ApiError(400, ErrorCode.INVALID_REQUEST, "email is required.");
    }
    const emailHash = await sha256Hex(`${email}|${c.env.BETTER_AUTH_SECRET}`);
    await rateLimitOrThrow(c.env.DB, `auth-email:${emailHash}`, 60 * 60 * 1000, 3);
    if (!emailMagicLinkEnabled(c.env)) {
      throw new ApiError(503, ErrorCode.EMAIL_NOT_CONFIGURED, "Email sign-in is not configured.");
    }
  } else if (!c.env.GOOGLE_CLIENT_ID || !c.env.GOOGLE_CLIENT_SECRET) {
    throw new ApiError(503, ErrorCode.GOOGLE_NOT_CONFIGURED, "Google sign-in is not configured.");
  }

  const loginId = randomHex(24);
  const created = nowIso();
  const expires = new Date(Date.now() + LOGIN_TTL_SEC * 1000).toISOString();
  await c.env.DB.prepare(
    `INSERT INTO pending_login (id, provider, redirect_uri, code_challenge, client_state, expires_at, created_at)
     VALUES (?, ?, ?, ?, ?, ?, ?)`,
  )
    .bind(loginId, provider, redirect.href, codeChallenge, state, expires, created)
    .run();

  if (provider === "email") {
    const email = String(body.email).trim().toLowerCase();
    if (!(await unverifiedPasswordUserId(c.env.DB, email))) {
      const auth = createAuth(c.env);
      const callbackURL = `${c.env.BETTER_AUTH_URL}/v1/auth/complete?login=${loginId}`;
      await auth.api.signInMagicLink({
        body: { email, callbackURL },
        headers: new Headers({ "user-agent": "exo-id" }),
      });
    }
    return c.json({ loginId, expiresIn: LOGIN_TTL_SEC, authorizationUrl: null }, 202);
  }

  const authorizationUrl = `${c.env.BETTER_AUTH_URL}/v1/auth/continue/${loginId}`;
  return c.json({ loginId, expiresIn: LOGIN_TTL_SEC, authorizationUrl });
});

authRoutes.get("/v1/auth/continue/:loginId", async (c) => {
  const loginId = c.req.param("loginId");
  const pending = await c.env.DB.prepare(
    `SELECT id, provider, expires_at, consumed_at FROM pending_login WHERE id = ?`,
  )
    .bind(loginId)
    .first<{ id: string; provider: string; expires_at: string; consumed_at: string | null }>();
  if (!pending || pending.consumed_at || pending.expires_at < nowIso()) {
    return c.html(authErrorPage("This sign-in link expired. Return to Exo and try again."), 410);
  }
  if (pending.provider === "email") {
    return c.html(checkEmailPage());
  }
  if (!c.env.GOOGLE_CLIENT_ID || !c.env.GOOGLE_CLIENT_SECRET) {
    return c.html(authErrorPage("Google sign-in is not configured."), 503);
  }
  const auth = createAuth(c.env);
  const callbackURL = `${c.env.BETTER_AUTH_URL}/v1/auth/complete?login=${loginId}`;
  const result = await auth.api.signInSocial({
    body: { provider: "google", callbackURL },
  });
  const url = result && typeof result === "object" && "url" in result ? String((result as { url?: string }).url ?? "") : "";
  if (!url) {
    logError("google sign-in did not return a redirect");
    return c.html(authErrorPage("Google sign-in could not start."), 500);
  }
  return c.redirect(url, 302);
});

authRoutes.get("/v1/auth/complete", async (c) => {
  const loginId = c.req.query("login") ?? "";
  const pending = await c.env.DB.prepare(
    `SELECT id, redirect_uri, client_state, expires_at, consumed_at FROM pending_login WHERE id = ?`,
  )
    .bind(loginId)
    .first<{
      id: string;
      redirect_uri: string;
      client_state: string;
      expires_at: string;
      consumed_at: string | null;
    }>();
  if (!pending || pending.consumed_at || pending.expires_at < nowIso()) {
    return c.html(authErrorPage("This sign-in expired. Return to Exo and try again."), 410);
  }
  const auth = createAuth(c.env);
  const session = await auth.api.getSession({ headers: c.req.raw.headers });
  if (!session?.user || !session.session) {
    return c.html(authErrorPage("Sign-in did not complete."), 401);
  }
  const claimedAt = nowIso();
  const claimed = await c.env.DB.prepare(
    `UPDATE pending_login
     SET consumed_at = ?
     WHERE id = ? AND consumed_at IS NULL AND expires_at >= ?`,
  )
    .bind(claimedAt, loginId, claimedAt)
    .run();
  if (!claimed.meta.changes) {
    return c.html(authErrorPage("This sign-in expired. Return to Exo and try again."), 410);
  }
  const ctx = await auth.$context;
  let desktop: Awaited<ReturnType<typeof ctx.internalAdapter.createSession>> | null = null;
  try {
    desktop = await ctx.internalAdapter.createSession(session.user.id, false, {
      userAgent: "exo-launcher",
    });
    await ctx.internalAdapter.deleteSession(session.session.token);
    const code = randomHex(32);
    const codeHash = await sha256Hex(code);
    const expires = new Date(Date.now() + AUTH_CODE_TTL_SEC * 1000).toISOString();
    await c.env.DB.prepare(
      `INSERT INTO auth_code (code_hash, login_id, user_id, session_id, expires_at) VALUES (?, ?, ?, ?, ?)`,
    ).bind(codeHash, loginId, session.user.id, desktop.id, expires).run();
    const target = loopbackCallbackUrl(pending.redirect_uri, { code, state: pending.client_state });
    return c.redirect(target, 302);
  } catch (error) {
    if (desktop) {
      await ctx.internalAdapter.deleteSession(desktop.token).catch(() => undefined);
    }
    throw error;
  }
});

authRoutes.post("/v1/auth/token", async (c) => {
  await rateLimitOrThrow(
    c.env.DB,
    await scopedRateKey(c.env.BETTER_AUTH_SECRET, "auth-token-ip", clientIp(c.req.raw.headers)),
    10 * 60 * 1000,
    20,
  );
  const body = await readBoundedJsonObject(c.req.raw, MAX_AUTH_JSON_BYTES, INVALID_AUTH_REQUEST);
  if (!hasExactKeys(body, ["code", "codeVerifier", "redirectUri"])) {
    throw new ApiError(400, ErrorCode.INVALID_REQUEST, INVALID_AUTH_REQUEST);
  }
  const redirect = parseLoopbackRedirect(body.redirectUri);
  const verifier = assertPkceVerifier(body.codeVerifier);
  if (typeof body.code !== "string" || !/^[0-9a-f]{64}$/u.test(body.code)) {
    throw new ApiError(400, ErrorCode.INVALID_GRANT, "code is invalid.");
  }
  const codeHash = await sha256Hex(body.code);
  const row = await c.env.DB.prepare(
    `SELECT a.code_hash, a.login_id, a.user_id, a.session_id, a.expires_at, a.consumed_at,
            p.redirect_uri, p.code_challenge
     FROM auth_code a JOIN pending_login p ON p.id = a.login_id
     WHERE a.code_hash = ?`,
  )
    .bind(codeHash)
    .first<{
      code_hash: string;
      login_id: string;
      user_id: string;
      session_id: string;
      expires_at: string;
      consumed_at: string | null;
      redirect_uri: string;
      code_challenge: string;
    }>();
  if (!row || row.consumed_at || row.expires_at < nowIso()) {
    throw new ApiError(400, ErrorCode.INVALID_GRANT, "code is invalid or expired.");
  }
  if (row.redirect_uri !== redirect.href) {
    throw new ApiError(400, ErrorCode.INVALID_GRANT, "redirectUri does not match the login.");
  }
  const expected = await sha256Base64Url(verifier);
  if (expected !== row.code_challenge) {
    throw new ApiError(400, ErrorCode.INVALID_PKCE, "PKCE verification failed.");
  }
  const consumed = await c.env.DB.prepare(
    `UPDATE auth_code SET consumed_at = ? WHERE code_hash = ? AND consumed_at IS NULL`,
  )
    .bind(nowIso(), codeHash)
    .run();
  if (!consumed.meta.changes) {
    throw new ApiError(400, ErrorCode.INVALID_GRANT, "code is invalid or expired.");
  }
  const session = await c.env.DB.prepare(`SELECT id, token, expiresAt FROM session WHERE id = ?`)
    .bind(row.session_id)
    .first<{ id: string; token: string; expiresAt: string }>();
  if (!session) {
    throw new ApiError(400, ErrorCode.INVALID_GRANT, "session is gone.");
  }
  const user = await c.env.DB.prepare(`SELECT id, name, email FROM user WHERE id = ?`)
    .bind(row.user_id)
    .first<{ id: string; name: string; email: string }>();
  const handle = await c.env.DB.prepare(`SELECT display, normalized FROM handle WHERE user_id = ?`)
    .bind(row.user_id)
    .first<{ display: string; normalized: string }>();
  return c.json({
    tokenType: "Bearer",
    accessToken: session.token,
    expiresIn: SESSION_TTL_SEC,
    expiresAt: new Date(session.expiresAt).toISOString(),
    user: {
      id: user?.id ?? row.user_id,
      name: user?.name ?? "",
      email: user?.email ?? "",
      handle: handle ? { display: handle.display, normalized: handle.normalized } : null,
    },
  });
});

authRoutes.post("/v1/auth/sign-out", async (c) => {
  const session = await requireSession(c);
  const auth = createAuth(c.env);
  const ctx = await auth.$context;
  await ctx.internalAdapter.deleteSession(session.token);
  return c.json({ ok: true });
});

authRoutes.get("/v1/sessions", async (c) => {
  const session = await requireSession(c);
  const rows = await c.env.DB.prepare(
    `SELECT id, createdAt, updatedAt, expiresAt, userAgent, ipAddress FROM session WHERE userId = ? ORDER BY createdAt DESC`,
  )
    .bind(session.userId)
    .all<{
      id: string;
      createdAt: string;
      updatedAt: string;
      expiresAt: string;
      userAgent: string | null;
      ipAddress: string | null;
    }>();
  return c.json({
    sessions: (rows.results ?? []).map((row) => ({
      id: row.id,
      current: row.id === session.sessionId,
      createdAt: row.createdAt,
      updatedAt: row.updatedAt,
      expiresAt: row.expiresAt,
      userAgent: row.userAgent,
    })),
  });
});

authRoutes.post("/v1/sessions/revoke", async (c) => {
  const session = await requireSession(c);
  const body = await readBoundedJsonObject(c.req.raw, MAX_AUTH_JSON_BYTES, INVALID_SESSION_REQUEST);
  if (!hasExactKeys(body, ["sessionId"])) {
    throw new ApiError(400, ErrorCode.INVALID_REQUEST, INVALID_SESSION_REQUEST);
  }
  const sessionId = typeof body.sessionId === "string" ? body.sessionId : "";
  if (!sessionId || sessionId.length > 128) {
    throw new ApiError(400, ErrorCode.INVALID_REQUEST, INVALID_SESSION_REQUEST);
  }
  const row = await c.env.DB.prepare(`SELECT id, token FROM session WHERE id = ? AND userId = ?`)
    .bind(sessionId, session.userId)
    .first<{ id: string; token: string }>();
  if (!row) throw new ApiError(404, ErrorCode.NOT_FOUND, "Session not found.");
  const auth = createAuth(c.env);
  const ctx = await auth.$context;
  await ctx.internalAdapter.deleteSession(row.token);
  return c.json({ ok: true });
});

authRoutes.post("/v1/sessions/revoke-all", async (c) => {
  const session = await requireSession(c);
  const rows = await c.env.DB.prepare(`SELECT token FROM session WHERE userId = ?`)
    .bind(session.userId)
    .all<{ token: string }>();
  const auth = createAuth(c.env);
  const ctx = await auth.$context;
  await ctx.internalAdapter.deleteSessions((rows.results ?? []).map((row) => row.token));
  return c.json({ ok: true });
});
