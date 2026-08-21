import { env, SELF } from "cloudflare:test";

export async function api(path: string, init: RequestInit = {}): Promise<Response> {
  const headers = new Headers(init.headers);
  if (init.body && !headers.has("content-type")) headers.set("content-type", "application/json");
  return SELF.fetch(`http://127.0.0.1:8787${path}`, { ...init, headers });
}

export async function seedUser(email: string): Promise<{ id: string; token: string; sessionId: string }> {
  const id = crypto.randomUUID();
  const sessionId = crypto.randomUUID();
  const token = `tok_${crypto.randomUUID().replaceAll("-", "")}${crypto.randomUUID().replaceAll("-", "")}`;
  const now = new Date().toISOString();
  const expires = new Date(Date.now() + 7 * 24 * 60 * 60 * 1000).toISOString();
  await env.DB.batch([
    env.DB.prepare(
      `INSERT INTO user (id, name, email, emailVerified, createdAt, updatedAt) VALUES (?, ?, ?, 1, ?, ?)`,
    ).bind(id, "Test", email, now, now),
    env.DB.prepare(
      `INSERT INTO session (id, expiresAt, token, createdAt, updatedAt, userAgent, userId) VALUES (?, ?, ?, ?, ?, 'test', ?)`,
    ).bind(sessionId, expires, token, now, now, id),
  ]);
  return { id, token, sessionId };
}

export function authHeaders(token: string): HeadersInit {
  return { authorization: `Bearer ${token}`, "content-type": "application/json" };
}

export function field(value: unknown, updatedAt = new Date().toISOString(), deviceId = "pc-a") {
  return { value, updatedAt, deviceId };
}
