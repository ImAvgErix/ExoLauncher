import type { Context } from "hono";
import type { Env } from "./env.ts";
import { createAuth } from "./auth.ts";
import { ApiError, ErrorCode } from "./errors.ts";

export type Authed = {
  userId: string;
  sessionId: string;
  token: string;
  email: string;
  name: string;
  createdAt: Date;
  expiresAt: Date;
};

export async function requireSession(c: Context<{ Bindings: Env }>): Promise<Authed> {
  const authorization = c.req.raw.headers.get("authorization") ?? "";
  const bearer = authorization.slice(0, 7).toLowerCase() === "bearer "
    ? authorization.slice(7).trim()
    : "";
  if (!bearer || bearer.length > 4096 || /\s/.test(bearer)) {
    throw new ApiError(401, ErrorCode.UNAUTHENTICATED, "Sign in required.");
  }
  const auth = createAuth(c.env);
  const result = await auth.api.getSession({ headers: c.req.raw.headers });
  if (!result?.session || !result.user) {
    throw new ApiError(401, ErrorCode.UNAUTHENTICATED, "Sign in required.");
  }
  return {
    userId: result.user.id,
    sessionId: result.session.id,
    token: result.session.token,
    email: result.user.email,
    name: result.user.name,
    createdAt: result.session.createdAt,
    expiresAt: result.session.expiresAt,
  };
}

export async function currentHandle(
  db: D1Database,
  userId: string,
): Promise<{ display: string; normalized: string; claimedAt: string; changedAt: string } | null> {
  const row = await db
    .prepare(`SELECT display, normalized, claimed_at, changed_at FROM handle WHERE user_id = ?`)
    .bind(userId)
    .first<{ display: string; normalized: string; claimed_at: string; changed_at: string }>();
  if (!row) return null;
  return {
    display: row.display,
    normalized: row.normalized,
    claimedAt: row.claimed_at,
    changedAt: row.changed_at,
  };
}

export async function requireHandle(
  db: D1Database,
  userId: string,
): Promise<{ display: string; normalized: string }> {
  const row = await currentHandle(db, userId);
  if (!row) {
    throw new ApiError(403, ErrorCode.HANDLE_REQUIRED, "Claim a handle before syncing preferences.");
  }
  return { display: row.display, normalized: row.normalized };
}
