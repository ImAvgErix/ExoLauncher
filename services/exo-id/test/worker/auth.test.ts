import { describe, expect, it } from "vitest";
import { env } from "cloudflare:test";
import { api, seedUser } from "./helpers.ts";
import { sha256Base64Url, sha256Hex } from "../../src/crypto.ts";
import { normalizePasswordAuthResponse } from "../../src/password-auth.ts";

describe("auth start", () => {
  it("reports provider and social capabilities without exposing configuration values", async () => {
    const res = await api("/v1/health");
    expect(res.status).toBe(200);
    expect(await res.json()).toEqual({
      ok: true,
      service: "exo-id",
      capabilities: {
        providers: { google: true, email: false, password: true },
        profiles: true,
        friends: true,
        media: true,
        presence: true,
      },
    });
  });

  it("normalizes Better Auth boundary rejections without returning provider internals", async () => {
    const res = await api("/api/auth/sign-up/email", {
      method: "POST",
      headers: { origin: "https://untrusted.example" },
      body: JSON.stringify({
        name: "Origin User",
        email: `origin-${crypto.randomUUID()}@example.test`,
        password: "correct horse battery staple",
      }),
    });

    expect(res.status).toBe(400);
    expect(await res.json()).toEqual({
      error: { code: "INVALID_REQUEST", message: "Invalid account request." },
    });
  }, 20_000);

  it("rate-limits password guessing with a standard retry header", async () => {
    const headers = { "cf-connecting-ip": "203.0.113.98" };
    for (let attempt = 0; attempt < 5; attempt += 1) {
      const res = await api("/api/auth/sign-in/email", {
        method: "POST",
        headers,
        body: JSON.stringify({ email: "missing@example.test", password: "not the correct password" }),
      });
      expect(res.status).toBe(401);
    }

    const limited = await api("/api/auth/sign-in/email", {
      method: "POST",
      headers,
      body: JSON.stringify({ email: "missing@example.test", password: "not the correct password" }),
    });
    expect(limited.status).toBe(429);
    expect(Number(limited.headers.get("retry-after"))).toBeGreaterThan(0);
    expect(await limited.json()).toEqual({
      error: { code: "RATE_LIMITED", message: "Too many attempts. Try again later." },
    });
  }, 20_000);

  it("creates a durable email/password account and hands off a bearer only after sign-in", async () => {
    const email = `password-${crypto.randomUUID()}@example.test`;
    const password = "correct horse battery staple";
    const signUp = await api("/api/auth/sign-up/email", {
      method: "POST",
      body: JSON.stringify({ name: "  Password User  ", email: `  ${email.toUpperCase()}  `, password }),
    });

    expect(signUp.status).toBe(200);
    const signUpBody = await signUp.clone().json();
    expect(signUpBody).toEqual({ ok: true });
    expect(signUpBody).not.toHaveProperty("password");
    expect(JSON.stringify(signUpBody)).not.toContain(password);
    expect(signUp.headers.get("set-auth-token")).toBeNull();
    expect(signUp.headers.get("set-cookie")).toBeNull();

    const credential = await env.DB.prepare(
      `SELECT a.password
       FROM account a JOIN user u ON u.id = a.userId
       WHERE u.email = ? AND a.password IS NOT NULL`,
    ).bind(email).first<{ password: string }>();
    expect(credential?.password).toBeTruthy();
    expect(credential?.password).not.toBe(password);
    expect(credential?.password).not.toContain(password);
    expect(credential?.password.length).toBeGreaterThan(32);

    const signIn = await api("/api/auth/sign-in/email", {
      method: "POST",
      body: JSON.stringify({ email, password }),
    });
    expect(signIn.status).toBe(200);
    const token = signIn.headers.get("set-auth-token");
    expect(token).toBeTruthy();
    expect(signIn.headers.get("set-cookie")).toBeNull();
    expect(signIn.headers.get("cache-control")).toBe("no-store");
    const signInBody = await signIn.clone().json();
    expect(signInBody).not.toHaveProperty("token");
    expect(JSON.stringify(signInBody)).not.toContain(password);

    const me = await api("/v1/me", {
      headers: { authorization: `Bearer ${token}` },
    });
    expect(me.status).toBe(200);
    expect(await me.json()).toMatchObject({ name: "Password User", email });
  }, 20_000);

  it("returns the same generic sign-up response for a new and an existing account", async () => {
    const email = `duplicate-${crypto.randomUUID()}@example.test`;
    const password = "correct horse battery staple";
    const created = await api("/api/auth/sign-up/email", {
      method: "POST",
      body: JSON.stringify({ name: "Original User", email, password }),
    });
    expect(created.status).toBe(200);
    const createdBody = await created.json<{ ok: true }>();

    const duplicate = await api("/api/auth/sign-up/email", {
      method: "POST",
      body: JSON.stringify({ name: "Original User", email, password }),
    });
    expect(duplicate.status).toBe(200);
    const duplicateBody = await duplicate.json<{ ok: true }>();
    expect(created.headers.get("set-auth-token")).toBeNull();
    expect(duplicate.headers.get("set-auth-token")).toBeNull();
    expect(createdBody).toEqual({ ok: true });
    expect(duplicateBody).toEqual(createdBody);

    const original = await env.DB.prepare(
      `SELECT u.name, a.password, a.providerId, a.accountId, a.issuer
       FROM account a JOIN user u ON u.id = a.userId
       WHERE u.email = ?`,
    ).bind(email).first<{
      name: string;
      password: string;
      providerId: string;
      accountId: string;
      issuer: string;
    }>();
    expect(original?.name).toBe("Original User");
    expect(original?.password).toBeTruthy();
    expect(original?.providerId).toBe("credential");
    expect(typeof original?.issuer).toBe("string");
    expect(original?.issuer.length).toBeGreaterThan(0);

    const signIn = await api("/api/auth/sign-in/email", {
      method: "POST",
      body: JSON.stringify({ email, password }),
    });
    expect(signIn.status).toBe(200);
    expect(signIn.headers.get("set-auth-token")).toBeTruthy();

    const indexes = await env.DB.prepare(`PRAGMA index_list('account')`).all<{
      name: string;
      unique: number;
    }>();
    expect(indexes.results).toContainEqual(
      expect.objectContaining({ name: "account_providerId_accountId_uidx", unique: 1 }),
    );
    expect(indexes.results).toContainEqual(
      expect.objectContaining({ name: "account_issuer_accountId_uidx", unique: 1 }),
    );
  }, 20_000);

  it("normalizes an unexpected duplicate-signup race to the generic accepted response", async () => {
    const normalized = await normalizePasswordAuthResponse(
      "/api/auth/sign-up/email",
      new Response(JSON.stringify({ code: "USER_ALREADY_EXISTS_USE_ANOTHER_EMAIL" }), {
        status: 409,
        headers: {
          "content-type": "application/json",
          "set-cookie": "session=must-not-escape",
          "set-auth-token": "must-not-escape",
        },
      }),
    );

    expect(normalized.status).toBe(200);
    expect(normalized.headers.get("set-cookie")).toBeNull();
    expect(normalized.headers.get("set-auth-token")).toBeNull();
    expect(await normalized.json()).toEqual({ ok: true });
  });

  it("returns the same generic credential error for unknown email and wrong password", async () => {
    const email = `credentials-${crypto.randomUUID()}@example.test`;
    const password = "correct horse battery staple";
    const created = await api("/api/auth/sign-up/email", {
      method: "POST",
      body: JSON.stringify({ name: "Credential User", email, password }),
    });
    expect(created.status).toBe(200);

    const wrongPassword = await api("/api/auth/sign-in/email", {
      method: "POST",
      body: JSON.stringify({ email, password: "this is not the password" }),
    });
    const unknownEmail = await api("/api/auth/sign-in/email", {
      method: "POST",
      body: JSON.stringify({
        email: `unknown-${crypto.randomUUID()}@example.test`,
        password: "this is not the password",
      }),
    });

    expect(wrongPassword.status).toBe(401);
    expect(unknownEmail.status).toBe(401);
    expect(await wrongPassword.json()).toEqual({
      error: { code: "INVALID_CREDENTIALS", message: "Email or password is incorrect." },
    });
    expect(await unknownEmail.json()).toEqual({
      error: { code: "INVALID_CREDENTIALS", message: "Email or password is incorrect." },
    });
  }, 20_000);

  it("rejects fields outside the desktop password contract", async () => {
    const email = `strict-${crypto.randomUUID()}@example.test`;
    const invalid = await api("/api/auth/sign-up/email", {
      method: "POST",
      body: JSON.stringify({
        name: "Strict User",
        email,
        password: "correct horse battery staple",
        image: "https://untrusted.example/avatar.png",
      }),
    });

    expect(invalid.status).toBe(400);
    expect(await invalid.json()).toEqual({
      error: { code: "INVALID_REQUEST", message: "Invalid account request." },
    });
  });

  it("returns a stable password policy error at both length bounds", async () => {
    for (const password of ["a".repeat(11), "a".repeat(129)]) {
      const res = await api("/api/auth/sign-up/email", {
        method: "POST",
        body: JSON.stringify({
          name: "Policy User",
          email: `policy-${crypto.randomUUID()}@example.test`,
          password,
        }),
      });
      expect(res.status).toBe(400);
      expect(await res.json()).toEqual({
        error: {
          code: "INVALID_PASSWORD",
          message: "Password must be between 12 and 128 characters.",
        },
      });
    }
  });

  it("bounds and sanitizes the sign-up identity fields", async () => {
    const invalidNames = ["   ", `Bad${String.fromCharCode(0)}Name`, "n".repeat(81)];
    for (const name of invalidNames) {
      const res = await api("/api/auth/sign-up/email", {
        method: "POST",
        body: JSON.stringify({
          name,
          email: `name-${crypto.randomUUID()}@example.test`,
          password: "correct horse battery staple",
        }),
      });
      expect(res.status).toBe(400);
      expect(await res.json()).toEqual({
        error: { code: "INVALID_REQUEST", message: "Invalid account request." },
      });
    }

    const longEmail = `${"e".repeat(243)}@example.test`;
    const emailRes = await api("/api/auth/sign-up/email", {
      method: "POST",
      body: JSON.stringify({
        name: "Bounded User",
        email: longEmail,
        password: "correct horse battery staple",
      }),
    });
    expect(emailRes.status).toBe(400);
    expect((await emailRes.json<{ error: { code: string } }>()).error.code).toBe("INVALID_REQUEST");
  });

  it("stops oversized password request bodies at the public boundary", async () => {
    const res = await api("/api/auth/sign-up/email", {
      method: "POST",
      body: JSON.stringify({
        name: "n".repeat(3000),
        email: "oversized@example.test",
        password: "correct horse battery staple",
      }),
    });
    expect(res.status).toBe(400);
    expect(await res.json()).toEqual({
      error: { code: "INVALID_REQUEST", message: "Invalid account request." },
    });
  });

  it("rejects a non-loopback redirect", async () => {
    const res = await api("/v1/auth/start", {
      method: "POST",
      body: JSON.stringify({
        provider: "google",
        redirectUri: "https://evil.example/callback",
        codeChallenge: "a".repeat(43),
        codeChallengeMethod: "S256",
        state: "abc",
      }),
    });
    expect(res.status).toBe(400);
    expect((await res.json<{ error: { code: string } }>()).error.code).toBe("INVALID_REDIRECT_URI");
  });

  it("rejects PKCE methods other than S256", async () => {
    const res = await api("/v1/auth/start", {
      method: "POST",
      body: JSON.stringify({
        provider: "google",
        redirectUri: "http://127.0.0.1:54321/callback",
        codeChallenge: "plain-challenge",
        codeChallengeMethod: "plain",
        state: "abc",
      }),
    });
    expect(res.status).toBe(400);
    expect((await res.json<{ error: { code: string } }>()).error.code).toBe("INVALID_PKCE");
  });

  it("accepts a loopback callback with an ephemeral port", async () => {
    const res = await api("/v1/auth/start", {
      method: "POST",
      body: JSON.stringify({
        provider: "google",
        redirectUri: "http://127.0.0.1:55123/callback",
        codeChallenge: "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRS",
        codeChallengeMethod: "S256",
        state: "desktop-state",
      }),
    });
    expect(res.status).toBe(200);
    const body = await res.json<{ authorizationUrl: string; loginId: string }>();
    expect(body.loginId).toBeTruthy();
    expect(body.authorizationUrl).toContain("/v1/auth/continue/");
  });

  it("rejects an oversized auth-start body before accepting the request", async () => {
    const res = await api("/v1/auth/start", {
      method: "POST",
      headers: { "cf-connecting-ip": "203.0.113.120" },
      body: JSON.stringify({
        provider: "google",
        redirectUri: "http://127.0.0.1:55125/callback",
        codeChallenge: "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRS",
        codeChallengeMethod: "S256",
        state: "s".repeat(3000),
      }),
    });

    expect(res.status).toBe(400);
    expect(await res.json()).toEqual({
      error: { code: "INVALID_REQUEST", message: "Invalid authentication request." },
    });
  });

  it("rejects fields outside the auth-start contract", async () => {
    const res = await api("/v1/auth/start", {
      method: "POST",
      headers: { "cf-connecting-ip": "203.0.113.121" },
      body: JSON.stringify({
        provider: "google",
        redirectUri: "http://127.0.0.1:55126/callback",
        codeChallenge: "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRS",
        codeChallengeMethod: "S256",
        state: "desktop-state",
        ignored: "not allowed",
      }),
    });

    expect(res.status).toBe(400);
    expect(await res.json()).toEqual({
      error: { code: "INVALID_REQUEST", message: "Invalid authentication request." },
    });
  });

  it("stores only a keyed digest of the client address for application rate limits", async () => {
    const address = "203.0.113.47";
    const res = await api("/v1/auth/start", {
      method: "POST",
      headers: { "cf-connecting-ip": address },
      body: JSON.stringify({
        provider: "google",
        redirectUri: "http://127.0.0.1:55124/callback",
        codeChallenge: "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRS",
        codeChallengeMethod: "S256",
        state: "desktop-state",
      }),
    });
    expect(res.status).toBe(200);
    const keys = await env.DB.prepare("SELECT key FROM app_rate_limit").all<{ key: string }>();
    expect(keys.results.some((row) => row.key.includes(address))).toBe(false);
  }, 20_000);
});

describe("desktop session boundary", () => {
  it("rejects cookie-only authentication and extra session-revoke fields", async () => {
    const user = await seedUser("cookie-only@example.test");
    const res = await api("/v1/me", {
      headers: { cookie: `better-auth.session_token=${user.token}` },
    });
    expect(res.status).toBe(401);
    expect((await res.json<{ error: { code: string } }>()).error.code).toBe("UNAUTHENTICATED");

    const revoke = await api("/v1/sessions/revoke", {
      method: "POST",
      headers: { authorization: `Bearer ${user.token}` },
      body: JSON.stringify({ sessionId: user.sessionId, ignored: "not allowed" }),
    });
    expect(revoke.status).toBe(400);
    expect(await revoke.json()).toEqual({
      error: { code: "INVALID_REQUEST", message: "Invalid session request." },
    });
  });

  it("does not expose unused Better Auth client endpoints or alternate methods", async () => {
    const requests: Array<[string, RequestInit]> = [
      ["/api/auth/sign-in/magic-link", { method: "POST", body: JSON.stringify({ email: "probe@example.test" }) }],
      ["/api/auth/magic-link/verify", { method: "GET" }],
      ["/api/auth/request-password-reset", { method: "POST", body: JSON.stringify({ email: "probe@example.test" }) }],
      ["/api/auth/change-password", { method: "POST", body: JSON.stringify({}) }],
      ["/api/auth/get-session", { method: "GET" }],
      ["/api/auth/sign-in/email", { method: "GET" }],
      ["/api/auth/sign-up/email", { method: "PUT", body: JSON.stringify({}) }],
    ];
    for (const [path, init] of requests) {
      const res = await api(path, init);
      expect(res.status).toBe(404);
      expect((await res.json<{ error: { code: string } }>()).error.code).toBe("NOT_FOUND");
    }
  });

});

describe("auth token", () => {
  it("rejects fields outside the auth-token contract", async () => {
    const res = await api("/v1/auth/token", {
      method: "POST",
      headers: { "cf-connecting-ip": "203.0.113.122" },
      body: JSON.stringify({
        code: "b".repeat(64),
        codeVerifier: "a".repeat(43),
        redirectUri: "http://127.0.0.1:55123/callback",
        ignored: "not allowed",
      }),
    });

    expect(res.status).toBe(400);
    expect(await res.json()).toEqual({
      error: { code: "INVALID_REQUEST", message: "Invalid authentication request." },
    });
  });

  it("exchanges a one-time PKCE code for the session bearer token and refuses replay", async () => {
    const verifier = "a".repeat(43);
    const challenge = await sha256Base64Url(verifier);
    const user = await seedUser("pkce@example.test");
    const loginId = crypto.randomUUID().replaceAll("-", "");
    const redirect = "http://127.0.0.1:55123/callback";
    const now = new Date().toISOString();
    const loginExp = new Date(Date.now() + 10 * 60 * 1000).toISOString();
    const code = "b".repeat(64);
    const codeHash = await sha256Hex(code);
    const codeExp = new Date(Date.now() + 60 * 1000).toISOString();
    await env.DB.batch([
      env.DB.prepare(
        `INSERT INTO pending_login (id, provider, redirect_uri, code_challenge, client_state, expires_at, created_at)
         VALUES (?, 'google', ?, ?, 'st', ?, ?)`,
      ).bind(loginId, redirect, challenge, loginExp, now),
      env.DB.prepare(
        `INSERT INTO auth_code (code_hash, login_id, user_id, session_id, expires_at) VALUES (?, ?, ?, ?, ?)`,
      ).bind(codeHash, loginId, user.id, user.sessionId, codeExp),
    ]);
    const res = await api("/v1/auth/token", {
      method: "POST",
      body: JSON.stringify({ code, codeVerifier: verifier, redirectUri: redirect }),
    });
    expect(res.status).toBe(200);
    const body = await res.json<{ accessToken: string; tokenType: string }>();
    expect(body.tokenType).toBe("Bearer");
    expect(body.accessToken).toBe(user.token);

    const replay = await api("/v1/auth/token", {
      method: "POST",
      body: JSON.stringify({ code, codeVerifier: verifier, redirectUri: redirect }),
    });
    expect(replay.status).toBe(400);
    expect((await replay.json<{ error: { code: string } }>()).error.code).toBe("INVALID_GRANT");
  });
});
