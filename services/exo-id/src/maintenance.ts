import type { Env } from "./env.ts";

const DAY_MS = 24 * 60 * 60 * 1000;
const MATCH_CLAIM_RETENTION_MS = 30 * DAY_MS;

export async function cleanupExpiredRecords(env: Pick<Env, "DB">, now = new Date()): Promise<void> {
  const nowIso = now.toISOString();
  const staleIso = new Date(now.getTime() - DAY_MS).toISOString();
  const matchCutoff = new Date(now.getTime() - MATCH_CLAIM_RETENTION_MS).toISOString();
  const rateCutoff = now.getTime() - DAY_MS;

  await env.DB.batch([
    env.DB.prepare(
      `DELETE FROM auth_code WHERE expires_at < ? OR (consumed_at IS NOT NULL AND consumed_at < ?)`,
    ).bind(nowIso, staleIso),
    env.DB.prepare(
      `DELETE FROM pending_login WHERE expires_at < ? OR (consumed_at IS NOT NULL AND consumed_at < ?)`,
    ).bind(nowIso, staleIso),
    env.DB.prepare(
      `DELETE FROM pending_store_link WHERE expires_at < ? OR (consumed_at IS NOT NULL AND consumed_at < ?)`,
    ).bind(nowIso, staleIso),
    env.DB.prepare(`DELETE FROM match_claim WHERE created_at < ?`).bind(matchCutoff),
    env.DB.prepare(`DELETE FROM app_rate_limit WHERE window_start < ?`).bind(rateCutoff),
    env.DB.prepare(
      `DELETE FROM handle_tombstone
       WHERE never_release = 0 AND release_at IS NOT NULL AND release_at < ?`,
    ).bind(nowIso),
  ]);
}
