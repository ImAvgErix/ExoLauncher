import type { Env } from "./env.ts";
import { emailMagicLinkEnabled } from "./env.ts";
import { ApiError, ErrorCode, errorBody } from "./errors.ts";

function isRecord(value: unknown): value is Record<string, unknown> {
  return Boolean(value) && typeof value === "object" && !Array.isArray(value);
}

export async function unverifiedPasswordUserId(
  db: D1Database,
  email: string,
): Promise<string | null> {
  const row = await db
    .prepare(
      `SELECT u.id
       FROM user u
       JOIN account a ON a.userId = u.id
       WHERE u.email = ? AND u.emailVerified = 0 AND a.password IS NOT NULL
       LIMIT 1`,
    )
    .bind(email)
    .first<{ id: string }>();
  return row?.id ?? null;
}

async function magicLinkEmailForToken(db: D1Database, token: string): Promise<string | null> {
  if (!token || token.length > 512) return null;
  const row = await db
    .prepare(`SELECT value, expiresAt FROM verification WHERE identifier = ? LIMIT 1`)
    .bind(token)
    .first<{ value: string; expiresAt: string }>();
  if (!row || row.expiresAt < new Date().toISOString()) return null;
  try {
    const parsed: unknown = JSON.parse(row.value);
    const email = isRecord(parsed) && typeof parsed.email === "string" ? parsed.email.trim().toLowerCase() : "";
    return email || null;
  } catch {
    return null;
  }
}

export async function rejectUnverifiedPasswordMagicLink(
  env: Env,
  request: Request,
): Promise<Response | null> {
  if (!emailMagicLinkEnabled(env)) {
    return new Response(JSON.stringify(errorBody(new ApiError(404, ErrorCode.NOT_FOUND, "Not found."))), {
      status: 404,
      headers: { "content-type": "application/json" },
    });
  }
  const token = new URL(request.url).searchParams.get("token") ?? "";
  const email = await magicLinkEmailForToken(env.DB, token);
  if (!email) return null;
  const userId = await unverifiedPasswordUserId(env.DB, email);
  if (!userId) return null;
  await env.DB.prepare(`DELETE FROM verification WHERE identifier = ?`).bind(token).run();
  return new Response(
    JSON.stringify(errorBody(new ApiError(403, ErrorCode.INVALID_GRANT, "This sign-in link is not valid."))),
    { status: 403, headers: { "content-type": "application/json" } },
  );
}
