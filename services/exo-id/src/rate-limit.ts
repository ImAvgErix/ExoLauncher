import { ApiError, ErrorCode } from "./errors.ts";
import { sha256Hex } from "./crypto.ts";

export type RateRule = {
  windowMs: number;
  max: number;
};

export async function scopedRateKey(secret: string, scope: string, id: string): Promise<string> {
  return `${scope}:${await sha256Hex(`${scope}|${id}|${secret}`)}`;
}

export async function assertRateLimit(db: D1Database, key: string, rule: RateRule): Promise<void> {
  const result = await consumeRateLimit(db, key, rule);
  if (!result.allowed) {
    throw new ApiError(429, ErrorCode.RATE_LIMITED, "Too many attempts. Try again later.", result.retryAfterSec);
  }
}

export async function consumeRateLimit(
  db: D1Database,
  key: string,
  rule: RateRule,
): Promise<{ allowed: boolean; retryAfterSec: number }> {
  const now = Date.now();
  const windowStartMin = now - rule.windowMs;
  await db
    .prepare(
      `INSERT INTO app_rate_limit (key, count, window_start)
       VALUES (?, 1, ?)
       ON CONFLICT(key) DO UPDATE SET
         count = CASE WHEN app_rate_limit.window_start < ? THEN 1 ELSE app_rate_limit.count + 1 END,
         window_start = CASE WHEN app_rate_limit.window_start < ? THEN ? ELSE app_rate_limit.window_start END`,
    )
    .bind(key, now, windowStartMin, windowStartMin, now)
    .run();
  const row = await db
    .prepare(`SELECT count, window_start FROM app_rate_limit WHERE key = ?`)
    .bind(key)
    .first<{ count: number; window_start: number }>();
  if (!row) return { allowed: true, retryAfterSec: 0 };
  if (row.count <= rule.max) return { allowed: true, retryAfterSec: 0 };
  const retryAfterSec = Math.max(1, Math.ceil((row.window_start + rule.windowMs - now) / 1000));
  return { allowed: false, retryAfterSec };
}

export function clientIp(headers: Headers): string {
  return headers.get("cf-connecting-ip")?.trim() || "local";
}
