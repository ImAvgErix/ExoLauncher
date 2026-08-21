import { env } from "cloudflare:test";
import { describe, expect, it } from "vitest";
import { cleanupExpiredRecords } from "../../src/maintenance.ts";
import { seedUser } from "./helpers.ts";

describe("scheduled metadata cleanup", () => {
  it("removes expired rows while preserving current records and permanent tombstones", async () => {
    const user = await seedUser("maintenance@example.test");
    const now = new Date("2026-08-19T20:00:00.000Z");
    const expired = new Date(now.getTime() - 40 * 24 * 60 * 60 * 1000).toISOString();
    const future = new Date(now.getTime() + 60_000).toISOString();
    await env.DB.batch([
      env.DB.prepare(
        `INSERT INTO match_claim (user_id, store, peer_user_id, created_at) VALUES (?, 'steam', ?, ?)`,
      ).bind(user.id, user.id, expired),
      env.DB.prepare(
        `INSERT INTO app_rate_limit (key, count, window_start) VALUES ('expired-rate', 1, ?)`,
      ).bind(now.getTime() - 2 * 24 * 60 * 60 * 1000),
      env.DB.prepare(
        `INSERT INTO handle_tombstone
          (normalized, skeleton, user_id, deleted_at, release_at, never_release)
         VALUES ('released', 'released', ?, ?, ?, 0),
                ('permanent', 'permanent', ?, ?, NULL, 1)`,
      ).bind(user.id, expired, expired, user.id, expired),
      env.DB.prepare(
        `INSERT INTO pending_login
          (id, provider, redirect_uri, code_challenge, client_state, expires_at, created_at)
         VALUES ('still-current', 'google', 'http://127.0.0.1:1234/callback', ?, 's', ?, ?)`,
      ).bind("a".repeat(43), future, now.toISOString()),
    ]);

    await cleanupExpiredRecords(env, now);

    expect(await env.DB.prepare("SELECT 1 FROM match_claim").first()).toBeNull();
    expect(await env.DB.prepare("SELECT 1 FROM app_rate_limit WHERE key = 'expired-rate'").first()).toBeNull();
    expect(await env.DB.prepare("SELECT 1 FROM handle_tombstone WHERE normalized = 'released'").first()).toBeNull();
    expect(await env.DB.prepare("SELECT 1 FROM handle_tombstone WHERE normalized = 'permanent'").first()).not.toBeNull();
    expect(await env.DB.prepare("SELECT 1 FROM pending_login WHERE id = 'still-current'").first()).not.toBeNull();
  });
});
