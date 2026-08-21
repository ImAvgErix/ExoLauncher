import { isUniqueViolation, nowIso, randomHex } from "./crypto.ts";
import { ApiError, ErrorCode } from "./errors.ts";
import { decodeCursor, encodeCursor } from "./pagination.ts";
import { profileMediaRecordHasOwnedKey, publicProfileMedia, type ProfileMediaRow, type PublicProfileMedia } from "./media.ts";
import { getProfilePrivacy, isBlockedEitherDirection } from "./policy.ts";

export type HandleSummary = { display: string; normalized: string };

export type FriendRequestDto = {
  id: string;
  direction: "incoming" | "outgoing";
  user: { userId: string; handle: HandleSummary | null };
  status: "pending" | "accepted" | "declined";
  createdAt: string;
  updatedAt: string;
};

export type FriendDto = {
  userId: string;
  handle: HandleSummary | null;
  sources: string[];
  connectedAt: string;
  avatar: PublicProfileMedia | null;
};

export type BlockDto = {
  userId: string;
  handle: HandleSummary | null;
  createdAt: string;
};

type RequestRow = {
  id: string;
  user_low: string;
  user_high: string;
  sender_id: string;
  recipient_id: string;
  status: "pending" | "accepted" | "declined";
  created_at: string;
  updated_at: string;
};

type RequestViewRow = RequestRow & {
  peer_id: string;
  handle_display: string | null;
  handle_normalized: string | null;
};

export function pairUsers(a: string, b: string): { low: string; high: string } {
  return a < b ? { low: a, high: b } : { low: b, high: a };
}

function handleFrom(row: { handle_display: string | null; handle_normalized: string | null }): HandleSummary | null {
  if (!row.handle_display || !row.handle_normalized) return null;
  return { display: row.handle_display, normalized: row.handle_normalized };
}

function requestDto(row: RequestViewRow, viewerId: string): FriendRequestDto {
  return {
    id: row.id,
    direction: row.sender_id === viewerId ? "outgoing" : "incoming",
    user: { userId: row.peer_id, handle: handleFrom(row) },
    status: row.status,
    createdAt: row.created_at,
    updatedAt: row.updated_at,
  };
}

async function requestByPair(db: D1Database, low: string, high: string): Promise<RequestRow | null> {
  return db
    .prepare(
      `SELECT id, user_low, user_high, sender_id, recipient_id, status, created_at, updated_at
       FROM friend_request WHERE user_low = ? AND user_high = ?`,
    )
    .bind(low, high)
    .first<RequestRow>();
}

async function requestViewById(db: D1Database, requestId: string, viewerId: string): Promise<FriendRequestDto | null> {
  const row = await db
    .prepare(
      `SELECT fr.id, fr.user_low, fr.user_high, fr.sender_id, fr.recipient_id,
              fr.status, fr.created_at, fr.updated_at,
              CASE WHEN fr.sender_id = ? THEN fr.recipient_id ELSE fr.sender_id END AS peer_id,
              h.display AS handle_display, h.normalized AS handle_normalized
       FROM friend_request fr
       LEFT JOIN handle h
         ON h.user_id = CASE WHEN fr.sender_id = ? THEN fr.recipient_id ELSE fr.sender_id END
       WHERE fr.id = ?`,
    )
    .bind(viewerId, viewerId, requestId)
    .first<RequestViewRow>();
  return row ? requestDto(row, viewerId) : null;
}

async function directFriendshipExists(db: D1Database, low: string, high: string): Promise<boolean> {
  const row = await db
    .prepare(`SELECT 1 AS connected FROM direct_friendship WHERE user_low = ? AND user_high = ?`)
    .bind(low, high)
    .first();
  return Boolean(row);
}

async function acceptPair(db: D1Database, request: RequestRow): Promise<void> {
  const stamp = nowIso();
  await db.batch([
    db
      .prepare(
        `INSERT INTO direct_friendship (user_low, user_high, created_at)
         VALUES (?, ?, ?)
         ON CONFLICT(user_low, user_high) DO NOTHING`,
      )
      .bind(request.user_low, request.user_high, stamp),
    db
      .prepare(`UPDATE friend_request SET status = 'accepted', updated_at = ? WHERE id = ?`)
      .bind(stamp, request.id),
    db
      .prepare(`DELETE FROM friend_suppression WHERE user_low = ? AND user_high = ?`)
      .bind(request.user_low, request.user_high),
  ]);
}

async function ensureAcceptedRequest(
  db: D1Database,
  userId: string,
  targetId: string,
  existing: RequestRow | null,
): Promise<FriendRequestDto> {
  const pair = pairUsers(userId, targetId);
  const stamp = nowIso();
  let request = existing;
  await db
    .prepare(`DELETE FROM friend_suppression WHERE user_low = ? AND user_high = ?`)
    .bind(pair.low, pair.high)
    .run();
  if (!request) {
    const id = randomHex(24);
    await db
      .prepare(
        `INSERT INTO friend_request
          (id, user_low, user_high, sender_id, recipient_id, status, created_at, updated_at)
         VALUES (?, ?, ?, ?, ?, 'accepted', ?, ?)`,
      )
      .bind(id, pair.low, pair.high, userId, targetId, stamp, stamp)
      .run();
    request = (await requestByPair(db, pair.low, pair.high))!;
  } else if (request.status !== "accepted") {
    await db
      .prepare(`UPDATE friend_request SET status = 'accepted', updated_at = ? WHERE id = ?`)
      .bind(stamp, request.id)
      .run();
  }
  const view = await requestViewById(db, request.id, userId);
  if (!view) throw new ApiError(500, ErrorCode.INTERNAL, "Friend request could not be read.");
  return view;
}

export async function resolveHandleUser(
  db: D1Database,
  normalized: string,
): Promise<{ userId: string; handle: HandleSummary } | null> {
  const row = await db
    .prepare(`SELECT user_id, display, normalized FROM handle WHERE normalized = ?`)
    .bind(normalized)
    .first<{ user_id: string; display: string; normalized: string }>();
  return row ? { userId: row.user_id, handle: { display: row.display, normalized: row.normalized } } : null;
}

export async function createFriendRequest(
  db: D1Database,
  userId: string,
  targetId: string,
): Promise<FriendRequestDto> {
  if (userId === targetId) {
    throw new ApiError(400, ErrorCode.INVALID_REQUEST, "You cannot send a friend request to yourself.");
  }
  if (await isBlockedEitherDirection({ DB: db }, userId, targetId)) {
    throw new ApiError(404, ErrorCode.NOT_FOUND, "Profile not found.");
  }

  const pair = pairUsers(userId, targetId);
  let existing = await requestByPair(db, pair.low, pair.high);
  if (await directFriendshipExists(db, pair.low, pair.high)) {
    return ensureAcceptedRequest(db, userId, targetId, existing);
  }
  if (existing?.status === "accepted") {
    await acceptPair(db, existing);
    const view = await requestViewById(db, existing.id, userId);
    if (!view) throw new ApiError(500, ErrorCode.INTERNAL, "Friend request could not be read.");
    return view;
  }
  if (existing?.status === "pending") {
    if (existing.sender_id !== userId) await acceptPair(db, existing);
    const view = await requestViewById(db, existing.id, userId);
    if (!view) throw new ApiError(500, ErrorCode.INTERNAL, "Friend request could not be read.");
    return view;
  }

  const privacy = await getProfilePrivacy({ DB: db }, targetId);
  if (privacy.requestPolicy !== "anyone") {
    throw new ApiError(404, ErrorCode.NOT_FOUND, "Profile not found.");
  }
  const id = randomHex(24);
  const stamp = nowIso();
  try {
    if (existing) {
      await db
        .prepare(
          `UPDATE friend_request SET id = ?, sender_id = ?, recipient_id = ?, status = 'pending',
             created_at = ?, updated_at = ?
           WHERE user_low = ? AND user_high = ?`,
        )
        .bind(id, userId, targetId, stamp, stamp, pair.low, pair.high)
        .run();
    } else {
      await db
        .prepare(
          `INSERT INTO friend_request
            (id, user_low, user_high, sender_id, recipient_id, status, created_at, updated_at)
           VALUES (?, ?, ?, ?, ?, 'pending', ?, ?)`,
        )
        .bind(id, pair.low, pair.high, userId, targetId, stamp, stamp)
        .run();
    }
  } catch (error) {
    if (!isUniqueViolation(error)) throw error;
    existing = await requestByPair(db, pair.low, pair.high);
    if (!existing) throw error;
    if (existing.status === "pending" && existing.sender_id !== userId) await acceptPair(db, existing);
    const raced = await requestViewById(db, existing.id, userId);
    if (!raced) throw error;
    return raced;
  }
  const view = await requestViewById(db, id, userId);
  if (!view) throw new ApiError(500, ErrorCode.INTERNAL, "Friend request could not be read.");
  return view;
}

export async function acceptFriendRequest(
  db: D1Database,
  requestId: string,
  recipientId: string,
): Promise<FriendRequestDto> {
  const request = await db
    .prepare(
      `SELECT id, user_low, user_high, sender_id, recipient_id, status, created_at, updated_at
       FROM friend_request WHERE id = ? AND recipient_id = ?`,
    )
    .bind(requestId, recipientId)
    .first<RequestRow>();
  if (!request || request.status === "declined") {
    throw new ApiError(404, ErrorCode.NOT_FOUND, "Friend request not found.");
  }
  if (await isBlockedEitherDirection({ DB: db }, request.sender_id, request.recipient_id)) {
    throw new ApiError(404, ErrorCode.NOT_FOUND, "Friend request not found.");
  }
  if (request.status === "pending") await acceptPair(db, request);
  const view = await requestViewById(db, request.id, recipientId);
  if (!view) throw new ApiError(404, ErrorCode.NOT_FOUND, "Friend request not found.");
  return view;
}

export async function declineFriendRequest(
  db: D1Database,
  requestId: string,
  recipientId: string,
): Promise<FriendRequestDto> {
  const request = await db
    .prepare(
      `SELECT id, user_low, user_high, sender_id, recipient_id, status, created_at, updated_at
       FROM friend_request WHERE id = ? AND recipient_id = ?`,
    )
    .bind(requestId, recipientId)
    .first<RequestRow>();
  if (!request || request.status === "accepted") {
    throw new ApiError(404, ErrorCode.NOT_FOUND, "Friend request not found.");
  }
  if (request.status === "pending") {
    await db
      .prepare(`UPDATE friend_request SET status = 'declined', updated_at = ? WHERE id = ?`)
      .bind(nowIso(), request.id)
      .run();
  }
  const view = await requestViewById(db, request.id, recipientId);
  if (!view) throw new ApiError(404, ErrorCode.NOT_FOUND, "Friend request not found.");
  return view;
}

async function listRequestDirection(
  db: D1Database,
  userId: string,
  direction: "incoming" | "outgoing",
  limit: number,
  rawCursor: string | undefined,
): Promise<{ requests: FriendRequestDto[]; nextCursor: string | null }> {
  const scope = `friend-requests:${direction}:${userId}`;
  const cursor = decodeCursor(rawCursor, scope);
  const cursorTime = cursor?.key ?? "";
  const cursorId = cursor?.tie ?? "";
  const ownerColumn = direction === "incoming" ? "recipient_id" : "sender_id";
  const peerColumn = direction === "incoming" ? "sender_id" : "recipient_id";
  const rows = await db
    .prepare(
      `SELECT fr.id, fr.user_low, fr.user_high, fr.sender_id, fr.recipient_id,
              fr.status, fr.created_at, fr.updated_at, fr.${peerColumn} AS peer_id,
              h.display AS handle_display, h.normalized AS handle_normalized
       FROM friend_request fr
       LEFT JOIN handle h ON h.user_id = fr.${peerColumn}
       WHERE fr.${ownerColumn} = ? AND fr.status = 'pending'
         AND (? = '' OR fr.created_at < ? OR (fr.created_at = ? AND fr.id < ?))
       ORDER BY fr.created_at DESC, fr.id DESC
       LIMIT ?`,
    )
    .bind(userId, cursorTime, cursorTime, cursorTime, cursorId, limit + 1)
    .all<RequestViewRow>();
  const all = rows.results ?? [];
  const page = all.slice(0, limit);
  const last = page[page.length - 1];
  return {
    requests: page.map((row) => requestDto(row, userId)),
    nextCursor: all.length > limit && last ? encodeCursor(scope, last.created_at, last.id) : null,
  };
}

export async function listPendingFriendRequests(
  db: D1Database,
  userId: string,
  options: { limit: number; incomingCursor?: string; outgoingCursor?: string },
): Promise<{
  incoming: FriendRequestDto[];
  outgoing: FriendRequestDto[];
  nextIncomingCursor: string | null;
  nextOutgoingCursor: string | null;
}> {
  const [incoming, outgoing] = await Promise.all([
    listRequestDirection(db, userId, "incoming", options.limit, options.incomingCursor),
    listRequestDirection(db, userId, "outgoing", options.limit, options.outgoingCursor),
  ]);
  return {
    incoming: incoming.requests,
    outgoing: outgoing.requests,
    nextIncomingCursor: incoming.nextCursor,
    nextOutgoingCursor: outgoing.nextCursor,
  };
}

export async function listFriends(
  db: D1Database,
  userId: string,
  limit: number,
  rawCursor: string | undefined,
): Promise<{ friends: FriendDto[]; nextCursor: string | null }> {
  const scope = `friends:${userId}`;
  const cursor = decodeCursor(rawCursor, scope);
  const cursorId = cursor?.key ?? "";
  const rows = await db
    .prepare(
      `WITH edges(peer_id, source, created_at) AS (
         SELECT CASE WHEN user_low = ? THEN user_high ELSE user_low END, 'direct', created_at
         FROM direct_friendship WHERE user_low = ? OR user_high = ?
         UNION ALL
         SELECT CASE WHEN user_low = ? THEN user_high ELSE user_low END, store, created_at
         FROM discovered_connection WHERE user_low = ? OR user_high = ?
       ), grouped AS (
         SELECT peer_id, GROUP_CONCAT(DISTINCT source) AS sources, MIN(created_at) AS connected_at
         FROM edges
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
         GROUP BY peer_id
         ORDER BY peer_id
         LIMIT ?
       )
       SELECT grouped.peer_id, grouped.sources, grouped.connected_at,
              h.display AS handle_display, h.normalized AS handle_normalized,
              COALESCE(pp.profile_visibility, 'friends') AS profile_visibility,
              pm.user_id AS media_user_id, pm.kind AS media_kind, pm.version AS media_version,
              pm.object_key AS media_object_key, pm.content_type AS media_content_type,
              pm.byte_size AS media_byte_size, pm.width AS media_width, pm.height AS media_height,
              pm.sha256 AS media_sha256, pm.created_at AS media_created_at, pm.updated_at AS media_updated_at
       FROM grouped
       LEFT JOIN handle h ON h.user_id = grouped.peer_id
       LEFT JOIN profile_privacy pp ON pp.user_id = grouped.peer_id
       LEFT JOIN profile_media pm ON pm.user_id = grouped.peer_id AND pm.kind = 'avatar'
       ORDER BY grouped.peer_id`,
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
    .all<{
      peer_id: string;
      sources: string;
      connected_at: string;
      handle_display: string | null;
      handle_normalized: string | null;
      profile_visibility: "public" | "friends" | "private";
      media_user_id: string | null;
      media_kind: "avatar" | null;
      media_version: string | null;
      media_object_key: string | null;
      media_content_type: ProfileMediaRow["content_type"] | null;
      media_byte_size: number | null;
      media_width: number | null;
      media_height: number | null;
      media_sha256: string | null;
      media_created_at: string | null;
      media_updated_at: string | null;
    }>();
  const all = rows.results ?? [];
  const page = all.slice(0, limit);
  const friends = page.map((row): FriendDto => {
    const sources = row.sources.split(",").filter(Boolean);
    sources.sort((a, b) => (a === "direct" ? -1 : b === "direct" ? 1 : a.localeCompare(b)));
    const mediaVisible = row.profile_visibility !== "private";
    const media: ProfileMediaRow | null = mediaVisible && row.media_user_id && row.media_kind && row.media_version &&
      row.media_object_key && row.media_content_type && row.media_byte_size && row.media_width &&
      row.media_height && row.media_sha256 && row.media_created_at && row.media_updated_at
      ? {
          user_id: row.media_user_id,
          kind: row.media_kind,
          version: row.media_version,
          object_key: row.media_object_key,
          content_type: row.media_content_type,
          byte_size: row.media_byte_size,
          width: row.media_width,
          height: row.media_height,
          sha256: row.media_sha256,
          created_at: row.media_created_at,
          updated_at: row.media_updated_at,
        }
      : null;
    return {
      userId: row.peer_id,
      handle: handleFrom(row),
      sources,
      connectedAt: row.connected_at,
      avatar: media && profileMediaRecordHasOwnedKey(media) ? publicProfileMedia(media) : null,
    };
  });
  const last = page[page.length - 1];
  return {
    friends,
    nextCursor: all.length > limit && last ? encodeCursor(scope, last.peer_id) : null,
  };
}

async function suppressAndDeletePairRelationships(
  db: D1Database,
  a: string,
  b: string,
  reason: "removed" | "blocked",
): Promise<void> {
  const pair = pairUsers(a, b);
  await db.batch([
    db
      .prepare(
        `INSERT INTO friend_suppression (user_low, user_high, created_by, reason, created_at)
         VALUES (?, ?, ?, ?, ?)
         ON CONFLICT(user_low, user_high) DO UPDATE SET
           created_by = excluded.created_by,
           reason = CASE
             WHEN friend_suppression.reason = 'blocked' AND excluded.reason = 'removed'
               THEN friend_suppression.reason
             ELSE excluded.reason
           END,
           created_at = excluded.created_at`,
      )
      .bind(pair.low, pair.high, a, reason, nowIso()),
    db.prepare(`DELETE FROM friend_request WHERE user_low = ? AND user_high = ?`).bind(pair.low, pair.high),
    db.prepare(`DELETE FROM direct_friendship WHERE user_low = ? AND user_high = ?`).bind(pair.low, pair.high),
    db.prepare(`DELETE FROM discovered_connection WHERE user_low = ? AND user_high = ?`).bind(pair.low, pair.high),
    db
      .prepare(
        `DELETE FROM match_claim
         WHERE (user_id = ? AND peer_user_id = ?) OR (user_id = ? AND peer_user_id = ?)`,
      )
      .bind(a, b, b, a),
  ]);
}

export async function removeFriend(db: D1Database, userId: string, targetId: string): Promise<void> {
  if (userId === targetId) {
    throw new ApiError(400, ErrorCode.INVALID_REQUEST, "You cannot remove yourself.");
  }
  const target = await db.prepare(`SELECT 1 AS found FROM user WHERE id = ?`).bind(targetId).first();
  if (!target) throw new ApiError(404, ErrorCode.NOT_FOUND, "User not found.");
  await suppressAndDeletePairRelationships(db, userId, targetId, "removed");
}

export async function blockUser(db: D1Database, blockerId: string, blockedId: string): Promise<BlockDto> {
  if (blockerId === blockedId) {
    throw new ApiError(400, ErrorCode.INVALID_REQUEST, "You cannot block yourself.");
  }
  const target = await db.prepare(`SELECT 1 AS found FROM user WHERE id = ?`).bind(blockedId).first();
  if (!target) throw new ApiError(404, ErrorCode.NOT_FOUND, "User not found.");
  const stamp = nowIso();
  await db
    .prepare(
      `INSERT INTO user_block (blocker_id, blocked_id, created_at)
       VALUES (?, ?, ?)
       ON CONFLICT(blocker_id, blocked_id) DO NOTHING`,
    )
    .bind(blockerId, blockedId, stamp)
    .run();
  await suppressAndDeletePairRelationships(db, blockerId, blockedId, "blocked");
  const row = await db
    .prepare(
      `SELECT b.blocked_id, b.created_at, h.display AS handle_display, h.normalized AS handle_normalized
       FROM user_block b LEFT JOIN handle h ON h.user_id = b.blocked_id
       WHERE b.blocker_id = ? AND b.blocked_id = ?`,
    )
    .bind(blockerId, blockedId)
    .first<{
      blocked_id: string;
      created_at: string;
      handle_display: string | null;
      handle_normalized: string | null;
    }>();
  if (!row) throw new ApiError(500, ErrorCode.INTERNAL, "Block could not be read.");
  return { userId: row.blocked_id, handle: handleFrom(row), createdAt: row.created_at };
}

export async function unblockUser(db: D1Database, blockerId: string, blockedId: string): Promise<void> {
  if (blockerId === blockedId) {
    throw new ApiError(400, ErrorCode.INVALID_REQUEST, "You cannot unblock yourself.");
  }
  await db
    .prepare(`DELETE FROM user_block WHERE blocker_id = ? AND blocked_id = ?`)
    .bind(blockerId, blockedId)
    .run();
}

export async function listBlocks(
  db: D1Database,
  userId: string,
  limit: number,
  rawCursor: string | undefined,
): Promise<{ blocks: BlockDto[]; nextCursor: string | null }> {
  const scope = `blocks:${userId}`;
  const cursor = decodeCursor(rawCursor, scope);
  const cursorId = cursor?.key ?? "";
  const rows = await db
    .prepare(
      `SELECT b.blocked_id, b.created_at,
              h.display AS handle_display, h.normalized AS handle_normalized
       FROM user_block b
       LEFT JOIN handle h ON h.user_id = b.blocked_id
       WHERE b.blocker_id = ? AND (? = '' OR b.blocked_id > ?)
       ORDER BY b.blocked_id
       LIMIT ?`,
    )
    .bind(userId, cursorId, cursorId, limit + 1)
    .all<{
      blocked_id: string;
      created_at: string;
      handle_display: string | null;
      handle_normalized: string | null;
    }>();
  const all = rows.results ?? [];
  const page = all.slice(0, limit);
  const last = page[page.length - 1];
  return {
    blocks: page.map((row) => ({
      userId: row.blocked_id,
      handle: handleFrom(row),
      createdAt: row.created_at,
    })),
    nextCursor: all.length > limit && last ? encodeCursor(scope, last.blocked_id) : null,
  };
}
