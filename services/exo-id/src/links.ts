import { nowIso, isUniqueViolation } from "./crypto.ts";
import { MATCH_CLAIM_TTL_MS } from "./env.ts";
import { ApiError, ErrorCode } from "./errors.ts";
import { decryptStoreExternalId, encryptStoreExternalId } from "./store-link-crypto.ts";
import { canonicalizeStoreId, hashStoreId, type Store } from "./stores.ts";

export type StoreLinkRow = {
  user_id: string;
  store: Store;
  external_id: string;
  id_hash: string;
  verified: number;
  verified_at: string;
};

type StoredStoreLinkRow = StoreLinkRow;

export type DiscoveredConnection = {
  userId: string;
  handle: { display: string; normalized: string } | null;
  store: Store;
  createdAt: string;
};

export async function discoveryEnabled(db: D1Database, userId: string): Promise<boolean> {
  const row = await db
    .prepare(`SELECT enabled FROM user_discovery WHERE user_id = ?`)
    .bind(userId)
    .first<{ enabled: number }>();
  if (!row) return true;
  return row.enabled === 1;
}

export async function setDiscovery(db: D1Database, userId: string, enabled: boolean): Promise<void> {
  const stamp = nowIso();
  await db
    .prepare(
      `INSERT INTO user_discovery (user_id, enabled, updated_at) VALUES (?, ?, ?)
       ON CONFLICT(user_id) DO UPDATE SET enabled = excluded.enabled, updated_at = excluded.updated_at`,
    )
    .bind(userId, enabled ? 1 : 0, stamp)
    .run();
  if (!enabled) {
    await db
      .prepare(`DELETE FROM match_claim WHERE user_id = ? OR peer_user_id = ?`)
      .bind(userId, userId)
      .run();
  }
}

export async function verifiedLink(
  db: D1Database,
  secret: string,
  userId: string,
  store: Store,
): Promise<StoreLinkRow | null> {
  const row = await db
    .prepare(
      `SELECT user_id, store, external_id, id_hash, verified, verified_at
       FROM store_link WHERE user_id = ? AND store = ?`,
    )
    .bind(userId, store)
    .first<StoredStoreLinkRow>();
  if (!row || row.verified !== 1) return null;
  return {
    ...row,
    external_id: await decryptStoreExternalId(secret, row.user_id, row.store, row.external_id),
  };
}

export async function listLinks(db: D1Database, secret: string, userId: string): Promise<StoreLinkRow[]> {
  const rows = await db
    .prepare(
      `SELECT user_id, store, external_id, id_hash, verified, verified_at
       FROM store_link WHERE user_id = ? ORDER BY store`,
    )
    .bind(userId)
    .all<StoredStoreLinkRow>();
  return Promise.all(
    (rows.results ?? []).map(async (row) => ({
      ...row,
      external_id: await decryptStoreExternalId(secret, row.user_id, row.store, row.external_id),
    })),
  );
}

export async function saveVerifiedLink(
  db: D1Database,
  secret: string,
  userId: string,
  store: Store,
  canonicalId: string,
): Promise<StoreLinkRow> {
  const idHash = await hashStoreId(secret, store, canonicalId);
  const previous = await db
    .prepare(
      `SELECT user_id, store, external_id, id_hash, verified, verified_at
       FROM store_link WHERE user_id = ? AND store = ?`,
    )
    .bind(userId, store)
    .first<StoredStoreLinkRow>();

  // Re-verifying the same account is a true idempotent operation. Retaining
  // the original ciphertext/timestamp also avoids invalidating valid mutual
  // discovery state merely because the provider repeated a callback.
  if (previous?.verified === 1 && previous.id_hash === idHash) {
    return {
      ...previous,
      external_id: await decryptStoreExternalId(
        secret,
        previous.user_id,
        previous.store,
        previous.external_id,
      ),
    };
  }

  const stamp = nowIso();
  const encryptedId = await encryptStoreExternalId(secret, userId, store, canonicalId);
  const save = db
    .prepare(
      `INSERT INTO store_link (user_id, store, external_id, id_hash, verified, verified_at)
       VALUES (?, ?, ?, ?, 1, ?)
       ON CONFLICT(user_id, store) DO UPDATE SET
         external_id = excluded.external_id,
         id_hash = excluded.id_hash,
         verified = 1,
         verified_at = excluded.verified_at`,
    )
    .bind(userId, store, encryptedId, idHash, stamp);

  const statements: D1PreparedStatement[] = [];
  if (previous && previous.id_hash !== idHash) {
    // Put relationship cleanup and the replacement in one D1 batch. A
    // competing owner's unique-fingerprint conflict rolls the cleanup back,
    // leaving the caller's current link and connections intact.
    statements.push(
      db
        .prepare(`DELETE FROM match_claim WHERE (user_id = ? OR peer_user_id = ?) AND store = ?`)
        .bind(userId, userId, store),
      db
        .prepare(`DELETE FROM discovered_connection WHERE (user_low = ? OR user_high = ?) AND store = ?`)
        .bind(userId, userId, store),
    );
  }
  statements.push(save);

  try {
    await db.batch(statements);
  } catch (err) {
    if (isUniqueViolation(err)) {
      throw new ApiError(409, ErrorCode.LINK_TAKEN, "That store account is already linked.");
    }
    throw err;
  }
  return {
    user_id: userId,
    store,
    external_id: canonicalId,
    id_hash: idHash,
    verified: 1,
    verified_at: stamp,
  };
}

export async function unlinkStore(db: D1Database, userId: string, store: Store): Promise<boolean> {
  const [gone] = await db.batch([
    db.prepare(`DELETE FROM store_link WHERE user_id = ? AND store = ?`).bind(userId, store),
    db
      .prepare(`DELETE FROM match_claim WHERE (user_id = ? OR peer_user_id = ?) AND store = ?`)
      .bind(userId, userId, store),
    db
      .prepare(`DELETE FROM discovered_connection WHERE (user_low = ? OR user_high = ?) AND store = ?`)
      .bind(userId, userId, store),
  ]);
  return (gone.meta.changes ?? 0) > 0;
}

function pairUsers(a: string, b: string): { low: string; high: string } {
  return a < b ? { low: a, high: b } : { low: b, high: a };
}

export async function listConnections(db: D1Database, userId: string): Promise<DiscoveredConnection[]> {
  const rows = await db
    .prepare(
      `SELECT dc.user_low, dc.user_high, dc.store, dc.created_at,
              h.display AS handle_display, h.normalized AS handle_normalized
       FROM discovered_connection dc
       LEFT JOIN handle h
         ON h.user_id = CASE WHEN dc.user_low = ? THEN dc.user_high ELSE dc.user_low END
       WHERE (dc.user_low = ? OR dc.user_high = ?)
         AND NOT EXISTS (
           SELECT 1 FROM user_block b
           WHERE (b.blocker_id = ? AND b.blocked_id = CASE WHEN dc.user_low = ? THEN dc.user_high ELSE dc.user_low END)
              OR (b.blocker_id = CASE WHEN dc.user_low = ? THEN dc.user_high ELSE dc.user_low END AND b.blocked_id = ?)
         )
         AND NOT EXISTS (
           SELECT 1 FROM friend_suppression s
           WHERE s.user_low = dc.user_low AND s.user_high = dc.user_high
         )
       ORDER BY dc.created_at, dc.store`,
    )
    .bind(userId, userId, userId, userId, userId, userId, userId)
    .all<{
      user_low: string;
      user_high: string;
      store: Store;
      created_at: string;
      handle_display: string | null;
      handle_normalized: string | null;
    }>();
  const out: DiscoveredConnection[] = [];
  for (const row of rows.results ?? []) {
    const peer = row.user_low === userId ? row.user_high : row.user_low;
    out.push({
      userId: peer,
      handle:
        row.handle_display && row.handle_normalized
          ? { display: row.handle_display, normalized: row.handle_normalized }
          : null,
      store: row.store,
      createdAt: row.created_at,
    });
  }
  return out;
}

async function lookupDiscoverable(
  db: D1Database,
  userId: string,
  store: Store,
  hashes: string[],
): Promise<Map<string, string>> {
  const found = new Map<string, string>();
  if (hashes.length === 0) return found;
  const chunkSize = 40;
  for (let i = 0; i < hashes.length; i += chunkSize) {
    const chunk = hashes.slice(i, i + chunkSize);
    const placeholders = chunk.map(() => "?").join(",");
    const rows = await db
      .prepare(
        `SELECT sl.user_id, sl.id_hash
         FROM store_link sl
         LEFT JOIN user_discovery d ON d.user_id = sl.user_id
         WHERE sl.store = ? AND sl.verified = 1 AND COALESCE(d.enabled, 1) = 1
           AND sl.id_hash IN (${placeholders})
           AND sl.user_id <> ?
           AND NOT EXISTS (
             SELECT 1 FROM user_block b
             WHERE (b.blocker_id = ? AND b.blocked_id = sl.user_id)
                OR (b.blocker_id = sl.user_id AND b.blocked_id = ?)
           )
           AND NOT EXISTS (
             SELECT 1 FROM friend_suppression s
             WHERE (s.user_low = ? AND s.user_high = sl.user_id)
                OR (s.user_low = sl.user_id AND s.user_high = ?)
           )`,
      )
      .bind(store, ...chunk, userId, userId, userId, userId, userId)
      .all<{ user_id: string; id_hash: string }>();
    for (const row of rows.results ?? []) found.set(row.id_hash, row.user_id);
  }
  return found;
}

export async function matchMutualFriends(
  db: D1Database,
  secret: string,
  userId: string,
  store: Store,
  ids: string[],
): Promise<DiscoveredConnection[]> {
  const own = await verifiedLink(db, secret, userId, store);
  if (!own) {
    throw new ApiError(403, ErrorCode.LINK_UNVERIFIED, "Verify that store before matching friends.");
  }
  if (!(await discoveryEnabled(db, userId))) return [];

  const unique = new Set<string>();
  const hashes: string[] = [];
  for (const raw of ids) {
    if (typeof raw !== "string") continue;
    const canonical = canonicalizeStoreId(store, raw);
    if (!canonical || canonical === own.external_id) continue;
    if (unique.has(canonical)) continue;
    unique.add(canonical);
    hashes.push(await hashStoreId(secret, store, canonical));
  }

  const cutoff = new Date(Date.now() - MATCH_CLAIM_TTL_MS).toISOString();
  await db.prepare(`DELETE FROM match_claim WHERE created_at < ?`).bind(cutoff).run();

  const hits = await lookupDiscoverable(db, userId, store, hashes);
  const stamp = nowIso();
  for (const peerId of hits.values()) {
    if (peerId === userId) continue;
    const pair = pairUsers(userId, peerId);
    const claim = await db
      .prepare(
        `INSERT INTO match_claim (user_id, store, peer_user_id, created_at)
         SELECT ?, ?, ?, ?
         WHERE NOT EXISTS (
           SELECT 1 FROM user_block
           WHERE (blocker_id = ? AND blocked_id = ?) OR (blocker_id = ? AND blocked_id = ?)
         ) AND NOT EXISTS (
           SELECT 1 FROM friend_suppression WHERE user_low = ? AND user_high = ?
         )
         ON CONFLICT(user_id, store, peer_user_id) DO UPDATE SET created_at = excluded.created_at`,
      )
      .bind(userId, store, peerId, stamp, userId, peerId, peerId, userId, pair.low, pair.high)
      .run();
    if ((claim.meta.changes ?? 0) === 0) continue;
    const reverse = await db
      .prepare(
        `SELECT 1 AS ok FROM match_claim WHERE user_id = ? AND store = ? AND peer_user_id = ? AND created_at >= ?`,
      )
      .bind(peerId, store, userId, cutoff)
      .first();
    if (!reverse) continue;
    await db
      .prepare(
        `INSERT INTO discovered_connection (user_low, user_high, store, created_at)
         SELECT ?, ?, ?, ?
         WHERE NOT EXISTS (
           SELECT 1 FROM user_block
           WHERE (blocker_id = ? AND blocked_id = ?) OR (blocker_id = ? AND blocked_id = ?)
         ) AND NOT EXISTS (
           SELECT 1 FROM friend_suppression WHERE user_low = ? AND user_high = ?
         )
         ON CONFLICT(user_low, user_high, store) DO NOTHING`,
      )
      .bind(
        pair.low,
        pair.high,
        store,
        stamp,
        userId,
        peerId,
        peerId,
        userId,
        pair.low,
        pair.high,
      )
      .run();
  }

  return listConnections(db, userId).then((rows) => rows.filter((row) => row.store === store));
}

export async function deleteUserLinkData(db: D1Database, userId: string): Promise<void> {
  await db.batch([
    db.prepare(`DELETE FROM friend_request WHERE user_low = ? OR user_high = ?`).bind(userId, userId),
    db.prepare(`DELETE FROM direct_friendship WHERE user_low = ? OR user_high = ?`).bind(userId, userId),
    db.prepare(`DELETE FROM friend_suppression WHERE user_low = ? OR user_high = ?`).bind(userId, userId),
    db.prepare(`DELETE FROM user_block WHERE blocker_id = ? OR blocked_id = ?`).bind(userId, userId),
    db.prepare(`DELETE FROM profile_privacy WHERE user_id = ?`).bind(userId),
    db.prepare(`DELETE FROM match_claim WHERE user_id = ? OR peer_user_id = ?`).bind(userId, userId),
    db.prepare(`DELETE FROM discovered_connection WHERE user_low = ? OR user_high = ?`).bind(userId, userId),
    db.prepare(`DELETE FROM store_link WHERE user_id = ?`).bind(userId),
    db.prepare(`DELETE FROM user_discovery WHERE user_id = ?`).bind(userId),
    db.prepare(`DELETE FROM pending_store_link WHERE user_id = ?`).bind(userId),
  ]);
}
