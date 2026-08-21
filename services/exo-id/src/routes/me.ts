import { Hono } from "hono";
import type { Env } from "../env.ts";
import { HANDLE_TOMBSTONE_MS } from "../env.ts";
import { createAuth } from "../auth.ts";
import { nowIso } from "../crypto.ts";
import { requireSession, currentHandle } from "../session.ts";
import { readFields } from "../fields.ts";
import { deleteUserLinkData, listConnections, listLinks } from "../links.ts";
import { getProfilePrivacy } from "../policy.ts";
import { cleanupProfileMediaForAccount, getProfileMediaProjection } from "../media.ts";
import { ApiError, ErrorCode } from "../errors.ts";
import {
  canManageProfileBadges,
  listProfileBadgeRecords,
  listPublicProfileBadges,
  listStaffRoles,
} from "../badges.ts";

export const meRoutes = new Hono<{ Bindings: Env }>();

function valuesFrom(fields: Awaited<ReturnType<typeof readFields>>): Record<string, unknown> {
  const out: Record<string, unknown> = {};
  for (const [key, rec] of Object.entries(fields)) out[key] = rec.value;
  return out;
}

meRoutes.get("/v1/me", async (c) => {
  const session = await requireSession(c);
  const handle = await currentHandle(c.env.DB, session.userId);
  const [profile, roles, badges] = await Promise.all([
    readFields(c.env.DB, "profile_field", session.userId),
    listStaffRoles(c.env.DB, session.userId),
    listPublicProfileBadges(c.env.DB, session.userId),
  ]);
  return c.json({
    id: session.userId,
    name: session.name,
    email: session.email,
    handle: handle
      ? {
          display: handle.display,
          normalized: handle.normalized,
          claimedAt: handle.claimedAt,
          changedAt: handle.changedAt,
        }
      : null,
    profile: valuesFrom(profile),
    badges,
    roles,
    canManageBadges: canManageProfileBadges(roles),
    session: { id: session.sessionId, expiresAt: session.expiresAt.toISOString() },
  });
});

meRoutes.get("/v1/me/export", async (c) => {
  const session = await requireSession(c);
  const user = await c.env.DB.prepare(`SELECT id, name, email, emailVerified, createdAt, updatedAt FROM user WHERE id = ?`)
    .bind(session.userId)
    .first<{
      id: string;
      name: string;
      email: string;
      emailVerified: number;
      createdAt: string;
      updatedAt: string;
    }>();
  const accounts = await c.env.DB.prepare(`SELECT providerId, issuer, createdAt FROM account WHERE userId = ?`)
    .bind(session.userId)
    .all<{ providerId: string; issuer: string; createdAt: string }>();
  const handle = await currentHandle(c.env.DB, session.userId);
  const profile = await readFields(c.env.DB, "profile_field", session.userId);
  const prefs = await readFields(c.env.DB, "pref_field", session.userId);
  const sessions = await c.env.DB.prepare(
    `SELECT id, createdAt, updatedAt, expiresAt, userAgent FROM session WHERE userId = ?`,
  )
    .bind(session.userId)
    .all<{ id: string; createdAt: string; updatedAt: string; expiresAt: string; userAgent: string | null }>();
  const discovery = await c.env.DB.prepare(`SELECT enabled, updated_at FROM user_discovery WHERE user_id = ?`)
    .bind(session.userId)
    .first<{ enabled: number; updated_at: string }>();
  const links = await listLinks(c.env.DB, c.env.BETTER_AUTH_SECRET, session.userId);
  const connections = await listConnections(c.env.DB, session.userId);
  const [privacy, media, roles, badges, socialRows, presence] = await Promise.all([
    getProfilePrivacy(c.env, session.userId),
    getProfileMediaProjection(c.env.DB, session.userId),
    listStaffRoles(c.env.DB, session.userId),
    listProfileBadgeRecords(c.env.DB, session.userId),
    c.env.DB.batch([
      c.env.DB.prepare(
        `SELECT CASE WHEN user_low = ? THEN user_high ELSE user_low END AS user_id, created_at
         FROM direct_friendship WHERE user_low = ? OR user_high = ? ORDER BY user_id`,
      ).bind(session.userId, session.userId, session.userId),
      c.env.DB.prepare(
        `SELECT id, sender_id, recipient_id, status, created_at, updated_at
         FROM friend_request WHERE sender_id = ? OR recipient_id = ? ORDER BY created_at, id`,
      ).bind(session.userId, session.userId),
      c.env.DB.prepare(
        `SELECT blocked_id AS user_id, created_at
         FROM user_block WHERE blocker_id = ? ORDER BY blocked_id`,
      ).bind(session.userId),
      c.env.DB.prepare(
        `SELECT CASE WHEN user_low = ? THEN user_high ELSE user_low END AS user_id,
                reason, created_at
         FROM friend_suppression
         WHERE created_by = ? ORDER BY user_id`,
      ).bind(session.userId, session.userId),
    ]),
    c.env.PRESENCE.getByName(session.userId).peekOwnerPresence(session.userId).catch(() => null),
  ]);
  const [directFriends, friendRequests, blocks, suppressions] = socialRows;
  return c.json({
    exportedAt: nowIso(),
    account: {
      id: user?.id ?? session.userId,
      name: user?.name ?? session.name,
      email: user?.email ?? session.email,
      emailVerified: Boolean(user?.emailVerified),
      createdAt: user?.createdAt,
      updatedAt: user?.updatedAt,
      providers: (accounts.results ?? []).map((row) => row.providerId),
    },
    handle: handle
      ? {
          display: handle.display,
          normalized: handle.normalized,
          claimedAt: handle.claimedAt,
          changedAt: handle.changedAt,
        }
      : null,
    profile: valuesFrom(profile),
    roles,
    badges,
    preferences: valuesFrom(prefs),
    privacy,
    media,
    sessions: (sessions.results ?? []).map((row) => ({
      id: row.id,
      current: row.id === session.sessionId,
      createdAt: row.createdAt,
      updatedAt: row.updatedAt,
      expiresAt: row.expiresAt,
      userAgent: row.userAgent,
    })),
    discovery: {
      enabled: discovery ? discovery.enabled === 1 : true,
      updatedAt: discovery?.updated_at ?? null,
    },
    links: links.map((row) => ({
      store: row.store,
      externalId: row.external_id,
      verified: row.verified === 1,
      verifiedAt: row.verified_at,
    })),
    connections: connections.map((row) => ({
      userId: row.userId,
      handle: row.handle,
      store: row.store,
      createdAt: row.createdAt,
    })),
    directFriends: directFriends.results ?? [],
    friendRequests: friendRequests.results ?? [],
    blocks: blocks.results ?? [],
    suppressions: suppressions.results ?? [],
    presence,
  });
});

meRoutes.delete("/v1/me", async (c) => {
  const session = await requireSession(c);
  if (Date.now() - session.createdAt.getTime() > 15 * 60 * 1000) {
    throw new ApiError(
      403,
      ErrorCode.REAUTHENTICATION_REQUIRED,
      "Sign in again before deleting the account.",
    );
  }
  const presenceDeleted = await c.env.PRESENCE.getByName(session.userId)
    .deleteAccount(session.userId)
    .catch(() => false);
  if (!presenceDeleted) {
    throw new ApiError(503, ErrorCode.INTERNAL, "Account cleanup is temporarily unavailable. Try again.");
  }
  await cleanupProfileMediaForAccount(c.env.DB, c.env.PROFILE_MEDIA, session.userId);
  const handle = await currentHandle(c.env.DB, session.userId);
  const stamp = nowIso();
  const releaseAt = new Date(Date.now() + HANDLE_TOMBSTONE_MS).toISOString();
  const statements = [];
  if (handle) {
    const row = await c.env.DB.prepare(`SELECT skeleton FROM handle WHERE user_id = ?`)
      .bind(session.userId)
      .first<{ skeleton: string }>();
    statements.push(
      c.env.DB.prepare(
        `INSERT INTO handle_tombstone (normalized, skeleton, user_id, deleted_at, release_at, never_release)
         VALUES (?, ?, ?, ?, ?, 0)
         ON CONFLICT(normalized) DO UPDATE SET
           skeleton = excluded.skeleton,
           user_id = excluded.user_id,
           deleted_at = excluded.deleted_at,
           release_at = CASE WHEN handle_tombstone.never_release = 1 THEN handle_tombstone.release_at ELSE excluded.release_at END,
           never_release = handle_tombstone.never_release`,
      ).bind(handle.normalized, row?.skeleton ?? handle.normalized, session.userId, stamp, releaseAt),
    );
  }
  statements.push(
    c.env.DB.prepare(`DELETE FROM auth_code WHERE user_id = ?`).bind(session.userId),
    c.env.DB.prepare(`DELETE FROM profile_field WHERE user_id = ?`).bind(session.userId),
    c.env.DB.prepare(`DELETE FROM pref_field WHERE user_id = ?`).bind(session.userId),
    c.env.DB.prepare(`DELETE FROM handle WHERE user_id = ?`).bind(session.userId),
  );
  await deleteUserLinkData(c.env.DB, session.userId);
  await c.env.DB.batch(statements);
  const auth = createAuth(c.env);
  const ctx = await auth.$context;
  await ctx.internalAdapter.deleteUser(session.userId);
  return c.json({ ok: true, handleHeldUntil: handle ? releaseAt : null });
});
