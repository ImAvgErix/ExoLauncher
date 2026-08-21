import { Hono } from "hono";
import type { Env } from "../env.ts";
import { HANDLE_COOLDOWN_MS, HANDLE_TOMBSTONE_MS } from "../env.ts";
import { ApiError, ErrorCode } from "../errors.ts";
import { parseHandle } from "../handles.ts";
import { nowIso, isUniqueViolation } from "../crypto.ts";
import { clientIp, consumeRateLimit, scopedRateKey } from "../rate-limit.ts";
import { requireSession } from "../session.ts";
import { readExactJsonObject } from "../bounded-json.ts";

const MAX_HANDLE_JSON_BYTES = 2048;
const INVALID_HANDLE_REQUEST = "Invalid handle request.";

export const handleRoutes = new Hono<{ Bindings: Env }>();

async function tombstoneBlocks(db: D1Database, normalized: string, skeleton: string): Promise<boolean> {
  const now = nowIso();
  const row = await db
    .prepare(
      `SELECT 1 AS blocked FROM handle_tombstone
       WHERE (normalized = ? OR skeleton = ?)
         AND (never_release = 1 OR release_at IS NULL OR release_at > ?)
       LIMIT 1`,
    )
    .bind(normalized, skeleton, now)
    .first();
  return Boolean(row);
}

handleRoutes.get("/v1/handle", async (c) => {
  const session = await requireSession(c);
  const row = await c.env.DB.prepare(
    `SELECT display, normalized, claimed_at, changed_at FROM handle WHERE user_id = ?`,
  )
    .bind(session.userId)
    .first<{ display: string; normalized: string; claimed_at: string; changed_at: string }>();
  return c.json({
    handle: row
      ? {
          display: row.display,
          normalized: row.normalized,
          claimedAt: row.claimed_at,
          changedAt: row.changed_at,
        }
      : null,
  });
});

handleRoutes.put("/v1/handle", async (c) => {
  const session = await requireSession(c);
  const limited = await consumeRateLimit(c.env.DB, await scopedRateKey(
    c.env.BETTER_AUTH_SECRET,
    "handle-user",
    session.userId,
  ), {
    windowMs: 10 * 60 * 1000,
    max: 10,
  });
  if (!limited.allowed) {
    throw new ApiError(429, ErrorCode.RATE_LIMITED, "Too many handle attempts.", limited.retryAfterSec);
  }
  const ipLimited = await consumeRateLimit(c.env.DB, await scopedRateKey(
    c.env.BETTER_AUTH_SECRET,
    "handle-ip",
    clientIp(c.req.raw.headers),
  ), {
    windowMs: 10 * 60 * 1000,
    max: 20,
  });
  if (!ipLimited.allowed) {
    throw new ApiError(429, ErrorCode.RATE_LIMITED, "Too many handle attempts.", ipLimited.retryAfterSec);
  }
  const body = await readExactJsonObject(
    c.req.raw,
    MAX_HANDLE_JSON_BYTES,
    ["handle"],
    [],
    INVALID_HANDLE_REQUEST,
  );
  const parsed = parseHandle(body.handle);
  if (await tombstoneBlocks(c.env.DB, parsed.normalized, parsed.skeleton)) {
    throw new ApiError(409, ErrorCode.HANDLE_TAKEN, "That handle is taken.");
  }
  const existing = await c.env.DB.prepare(
    `SELECT display, normalized, skeleton, claimed_at, changed_at FROM handle WHERE user_id = ?`,
  )
    .bind(session.userId)
    .first<{
      display: string;
      normalized: string;
      skeleton: string;
      claimed_at: string;
      changed_at: string;
    }>();
  const stamp = nowIso();
  if (!existing) {
    try {
      await c.env.DB.prepare(
        `INSERT INTO handle (user_id, display, normalized, skeleton, claimed_at, changed_at)
         VALUES (?, ?, ?, ?, ?, ?)`,
      )
        .bind(session.userId, parsed.display, parsed.normalized, parsed.skeleton, stamp, stamp)
        .run();
    } catch (err) {
      if (!isUniqueViolation(err)) throw err;
      const byNorm = await c.env.DB.prepare(`SELECT 1 FROM handle WHERE normalized = ?`)
        .bind(parsed.normalized)
        .first();
      throw new ApiError(
        409,
        byNorm ? ErrorCode.HANDLE_TAKEN : ErrorCode.HANDLE_CONFUSABLE,
        byNorm ? "That handle is taken." : "That handle is too close to one that is taken.",
      );
    }
    await c.env.DB.prepare(`DELETE FROM handle_tombstone WHERE normalized = ? OR skeleton = ?`)
      .bind(parsed.normalized, parsed.skeleton)
      .run();
    return c.json({
      handle: { display: parsed.display, normalized: parsed.normalized, claimedAt: stamp, changedAt: stamp },
    });
  }
  if (existing.normalized === parsed.normalized) {
    await c.env.DB.prepare(`UPDATE handle SET display = ? WHERE user_id = ?`)
      .bind(parsed.display, session.userId)
      .run();
    return c.json({
      handle: {
        display: parsed.display,
        normalized: parsed.normalized,
        claimedAt: existing.claimed_at,
        changedAt: existing.changed_at,
      },
    });
  }
  const lastChange = Date.parse(existing.changed_at);
  if (Number.isFinite(lastChange) && Date.now() - lastChange < HANDLE_COOLDOWN_MS) {
    throw new ApiError(409, ErrorCode.HANDLE_COOLDOWN, "Handle can change once every 30 days.");
  }
  const releaseAt = new Date(Date.now() + HANDLE_TOMBSTONE_MS).toISOString();
  try {
    await c.env.DB.batch([
      c.env.DB.prepare(
        `INSERT INTO handle_tombstone (normalized, skeleton, user_id, deleted_at, release_at, never_release)
         VALUES (?, ?, ?, ?, ?, 0)
         ON CONFLICT(normalized) DO UPDATE SET
           skeleton = excluded.skeleton,
           user_id = excluded.user_id,
           deleted_at = excluded.deleted_at,
           release_at = excluded.release_at,
           never_release = 0`,
      ).bind(existing.normalized, existing.skeleton, session.userId, stamp, releaseAt),
      c.env.DB.prepare(
        `UPDATE handle SET display = ?, normalized = ?, skeleton = ?, changed_at = ? WHERE user_id = ?`,
      ).bind(parsed.display, parsed.normalized, parsed.skeleton, stamp, session.userId),
      c.env.DB.prepare(`DELETE FROM handle_tombstone WHERE normalized = ? OR skeleton = ?`).bind(
        parsed.normalized,
        parsed.skeleton,
      ),
    ]);
  } catch (err) {
    if (!isUniqueViolation(err)) throw err;
    const byNorm = await c.env.DB.prepare(`SELECT 1 FROM handle WHERE normalized = ?`)
      .bind(parsed.normalized)
      .first();
    throw new ApiError(
      409,
      byNorm ? ErrorCode.HANDLE_TAKEN : ErrorCode.HANDLE_CONFUSABLE,
      byNorm ? "That handle is taken." : "That handle is too close to one that is taken.",
    );
  }
  return c.json({
    handle: {
      display: parsed.display,
      normalized: parsed.normalized,
      claimedAt: existing.claimed_at,
      changedAt: stamp,
    },
  });
});
