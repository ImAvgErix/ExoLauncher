import type { Env } from "./env.ts";
import { MAX_PAGE_LIMIT, decodeCursor, encodeCursor } from "./pagination.ts";
import { DEFAULT_PROFILE_PRIVACY, type ProfilePrivacy } from "./profile.ts";

export type PolicyEnv = Pick<Env, "DB">;

export async function getProfilePrivacy(env: PolicyEnv, userId: string): Promise<ProfilePrivacy> {
  const row = await env.DB.prepare(
    `SELECT profile_visibility, searchable, request_policy, activity_visibility, updated_at
     FROM profile_privacy WHERE user_id = ?`,
  )
    .bind(userId)
    .first<{
      profile_visibility: "public" | "friends" | "private";
      searchable: number;
      request_policy: "anyone" | "none";
      activity_visibility: "friends" | "private";
      updated_at: string;
    }>();
  if (!row) return { ...DEFAULT_PROFILE_PRIVACY };
  return {
    profileVisibility: row.profile_visibility,
    searchable: row.searchable === 1,
    requestPolicy: row.request_policy,
    activityVisibility: row.activity_visibility,
    updatedAt: row.updated_at,
  };
}

export async function isBlockedEitherDirection(env: PolicyEnv, a: string, b: string): Promise<boolean> {
  if (a === b) return false;
  const row = await env.DB.prepare(
    `SELECT 1 AS blocked FROM user_block
     WHERE (blocker_id = ? AND blocked_id = ?) OR (blocker_id = ? AND blocked_id = ?)
     LIMIT 1`,
  )
    .bind(a, b, b, a)
    .first();
  return Boolean(row);
}

export async function areConnectedFriends(env: PolicyEnv, a: string, b: string): Promise<boolean> {
  if (!a || !b || a === b) return false;
  const row = await env.DB.prepare(
    `SELECT CASE
       WHEN EXISTS (
         SELECT 1 FROM user_block
         WHERE (blocker_id = ? AND blocked_id = ?) OR (blocker_id = ? AND blocked_id = ?)
       ) THEN 0
       WHEN EXISTS (
         SELECT 1 FROM friend_suppression WHERE user_low = ? AND user_high = ?
       ) THEN 0
       WHEN EXISTS (
         SELECT 1 FROM direct_friendship WHERE user_low = ? AND user_high = ?
       ) OR EXISTS (
         SELECT 1 FROM discovered_connection WHERE user_low = ? AND user_high = ?
       ) THEN 1
       ELSE 0
     END AS connected`,
  )
    .bind(a, b, b, a, ...([a, b].sort()), ...([a, b].sort()), ...([a, b].sort()))
    .first<{ connected: number }>();
  return row?.connected === 1;
}

export async function canViewProfile(
  env: PolicyEnv,
  ownerId: string,
  viewerId: string | null,
): Promise<boolean> {
  const owner = await env.DB.prepare(
    `SELECT COALESCE(p.profile_visibility, 'friends') AS profile_visibility
     FROM user u
     LEFT JOIN profile_privacy p ON p.user_id = u.id
     WHERE u.id = ?`,
  )
    .bind(ownerId)
    .first<{ profile_visibility: "public" | "friends" | "private" }>();
  if (!owner) return false;
  if (viewerId === ownerId) return true;
  if (viewerId && (await isBlockedEitherDirection(env, ownerId, viewerId))) return false;
  if (owner.profile_visibility === "public") return true;
  if (!viewerId || owner.profile_visibility !== "friends") return false;
  return areConnectedFriends(env, ownerId, viewerId);
}

export type ConnectedFriendPage = {
  userIds: string[];
  nextCursor: string | null;
};

export async function listConnectedFriendIds(
  env: PolicyEnv,
  userId: string,
  options: { limit?: number; cursor?: string | null } = {},
): Promise<ConnectedFriendPage> {
  const limit = options.limit ?? 20;
  if (!Number.isSafeInteger(limit) || limit < 1 || limit > MAX_PAGE_LIMIT) {
    throw new RangeError(`limit must be between 1 and ${MAX_PAGE_LIMIT}.`);
  }
  const scope = `connected-friends:${userId}`;
  const cursor = decodeCursor(options.cursor ?? undefined, scope);
  const cursorId = cursor?.key ?? "";
  const rows = await env.DB.prepare(
    `WITH connected(peer_id) AS (
       SELECT CASE WHEN user_low = ? THEN user_high ELSE user_low END
       FROM direct_friendship WHERE user_low = ? OR user_high = ?
       UNION
       SELECT CASE WHEN user_low = ? THEN user_high ELSE user_low END
       FROM discovered_connection WHERE user_low = ? OR user_high = ?
     )
     SELECT peer_id FROM connected
     WHERE (? = '' OR peer_id > ?)
       AND NOT EXISTS (
         SELECT 1 FROM user_block b
         WHERE (b.blocker_id = ? AND b.blocked_id = peer_id)
            OR (b.blocker_id = peer_id AND b.blocked_id = ?)
       )
       AND NOT EXISTS (
         SELECT 1 FROM friend_suppression s
         WHERE (s.user_low = ? AND s.user_high = peer_id)
            OR (s.user_low = peer_id AND s.user_high = ?)
       )
     ORDER BY peer_id
     LIMIT ?`,
  )
    .bind(
      userId,
      userId,
      userId,
      userId,
      userId,
      userId,
      cursorId,
      cursorId,
      userId,
      userId,
      userId,
      userId,
      limit + 1,
    )
    .all<{ peer_id: string }>();
  const all = rows.results ?? [];
  const page = all.slice(0, limit);
  return {
    userIds: page.map((row) => row.peer_id),
    nextCursor:
      all.length > limit && page.length > 0 ? encodeCursor(scope, page[page.length - 1].peer_id) : null,
  };
}
