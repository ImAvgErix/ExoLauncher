import { describe, expect, it } from "vitest";
import { env } from "cloudflare:test";
import { api, authHeaders, seedUser } from "./helpers.ts";

const password = "correct horse battery staple";

async function signUpPassword(email: string, name = "Password User"): Promise<void> {
  const created = await api("/api/auth/sign-up/email", {
    method: "POST",
    body: JSON.stringify({ name, email, password }),
  });
  expect(created.status).toBe(200);
}

async function signInPassword(email: string): Promise<string> {
  const signIn = await api("/api/auth/sign-in/email", {
    method: "POST",
    body: JSON.stringify({ email, password }),
  });
  expect(signIn.status).toBe(200);
  const token = signIn.headers.get("set-auth-token");
  expect(token).toBeTruthy();
  return token!;
}

describe("magic-link capability", () => {
  it("hides magic-link routes and start when Resend is absent", async () => {
    expect(env.RESEND_API_KEY).toBe("");
    const health = await api("/v1/health");
    expect((await health.json<{ capabilities: { providers: { email: boolean } } }>()).capabilities.providers.email)
      .toBe(false);

    const start = await api("/v1/auth/start", {
      method: "POST",
      body: JSON.stringify({
        provider: "email",
        redirectUri: "http://127.0.0.1:55123/callback",
        codeChallenge: "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRS",
        codeChallengeMethod: "S256",
        email: "absent-resend@example.test",
      }),
    });
    expect(start.status).toBe(503);
    expect((await start.json<{ error: { code: string } }>()).error.code).toBe("EMAIL_NOT_CONFIGURED");

    const verify = await api("/api/auth/magic-link/verify?token=not-a-token");
    expect(verify.status).toBe(404);
    expect((await verify.json<{ error: { code: string } }>()).error.code).toBe("NOT_FOUND");
  });

  it("does not let magic-link verify inherit an unverified password identity", async () => {
    const previousKey = env.RESEND_API_KEY;
    env.RESEND_API_KEY = "test-resend-not-live";
    try {
      const email = `takeover-${crypto.randomUUID()}@example.test`;
      await signUpPassword(email, "Original Owner");
      const token = await signInPassword(email);
      const claimed = await api("/v1/handle", {
        method: "PUT",
        headers: authHeaders(token),
        body: JSON.stringify({ handle: "keepowner" }),
      });
      expect(claimed.status).toBe(200);

      const before = await env.DB.prepare(
        `SELECT u.id, u.emailVerified, a.password
         FROM user u JOIN account a ON a.userId = u.id
         WHERE u.email = ? AND a.password IS NOT NULL`,
      ).bind(email).first<{ id: string; emailVerified: number; password: string }>();
      expect(before?.emailVerified).toBe(0);
      expect(before?.password).toBeTruthy();

      const magicToken = `tok${crypto.randomUUID().replaceAll("-", "")}`;
      const now = new Date().toISOString();
      const expires = new Date(Date.now() + 5 * 60 * 1000).toISOString();
      await env.DB.prepare(
        `INSERT INTO verification (id, identifier, value, expiresAt, createdAt, updatedAt)
         VALUES (?, ?, ?, ?, ?, ?)`,
      )
        .bind(
          crypto.randomUUID(),
          magicToken,
          JSON.stringify({ email, name: "Attacker" }),
          expires,
          now,
          now,
        )
        .run();

      const verify = await api(
        `/api/auth/magic-link/verify?token=${magicToken}&callbackURL=${encodeURIComponent("http://127.0.0.1:8787/")}`,
      );
      expect(verify.status).toBe(403);
      expect((await verify.json<{ error: { code: string } }>()).error.code).toBe("INVALID_GRANT");

      const after = await env.DB.prepare(
        `SELECT u.id, u.emailVerified, a.password
         FROM user u JOIN account a ON a.userId = u.id
         WHERE u.email = ? AND a.password IS NOT NULL`,
      ).bind(email).first<{ id: string; emailVerified: number; password: string }>();
      expect(after?.id).toBe(before?.id);
      expect(after?.emailVerified).toBe(0);
      expect(after?.password).toBe(before?.password);

      const handle = await env.DB.prepare(`SELECT normalized FROM handle WHERE user_id = ?`)
        .bind(before!.id)
        .first<{ normalized: string }>();
      expect(handle?.normalized).toBe("keepowner");

      const stillPassword = await signInPassword(email);
      const me = await api("/v1/me", { headers: { authorization: `Bearer ${stillPassword}` } });
      expect(me.status).toBe(200);
      expect(await me.json()).toMatchObject({ email, name: "Original Owner" });

      const spent = await env.DB.prepare(`SELECT 1 AS found FROM verification WHERE identifier = ?`)
        .bind(magicToken)
        .first();
      expect(spent).toBeNull();
    } finally {
      env.RESEND_API_KEY = previousKey;
    }
  }, 20_000);

  it("does not send a magic-link for an existing unverified password account", async () => {
    const previousKey = env.RESEND_API_KEY;
    env.RESEND_API_KEY = "test-resend-not-live";
    try {
      const email = `nosend-${crypto.randomUUID()}@example.test`;
      await signUpPassword(email);
      const before = await env.DB.prepare(`SELECT COUNT(*) AS n FROM email_outbox`).first<{ n: number }>();
      const start = await api("/v1/auth/start", {
        method: "POST",
        body: JSON.stringify({
          provider: "email",
          redirectUri: "http://127.0.0.1:55123/callback",
          codeChallenge: "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRS",
          codeChallengeMethod: "S256",
          email,
        }),
      });
      expect(start.status).toBe(202);
      const after = await env.DB.prepare(`SELECT COUNT(*) AS n FROM email_outbox`).first<{ n: number }>();
      expect(after?.n ?? 0).toBe(before?.n ?? 0);
      const passwordRow = await env.DB.prepare(
        `SELECT a.password FROM account a JOIN user u ON u.id = a.userId WHERE u.email = ? AND a.password IS NOT NULL`,
      ).bind(email).first<{ password: string }>();
      expect(passwordRow?.password).toBeTruthy();
    } finally {
      env.RESEND_API_KEY = previousKey;
    }
  }, 20_000);
});
