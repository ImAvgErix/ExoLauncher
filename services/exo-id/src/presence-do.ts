import { DurableObject } from "cloudflare:workers";
import type { Env } from "./env.ts";
import { areConnectedFriends, getProfilePrivacy, listConnectedFriendIds } from "./policy.ts";
import {
  createSocketAttachment,
  MAX_PRESENCE_CONNECTIONS_PER_USER,
  parseClientPresenceMessage,
  PresenceMessageError,
  PRESENCE_PEER_RETENTION_MS,
  PRESENCE_TTL_MS,
  publicPresence,
  readSocketAttachment,
  rosterEntry,
  unavailablePresence,
  type ActivePresenceStatus,
  type PresenceRosterEntry,
  type PresenceSnapshot,
  type PresenceStatus,
  type ServerPresenceMessage,
} from "./presence.ts";

export const PRESENCE_OWNER_HEADER = "x-exo-presence-owner";
export const PRESENCE_SESSION_HEADER = "x-exo-presence-session";

type OwnerRow = {
  owner_id: string;
  status: PresenceStatus;
  game_id: string | null;
  game_title: string | null;
  last_seen_ms: number | null;
  revision: number;
};

type ConnectionRow = {
  connection_id: string;
  session_id: string;
  status: ActivePresenceStatus;
  game_id: string | null;
  game_title: string | null;
  last_seen_ms: number;
  expires_at_ms: number;
};

function validInternalId(value: string): boolean {
  return value.length > 0 && value.length <= 512 && !/[\u0000-\u001f\u007f]/u.test(value);
}

function statusRank(status: ActivePresenceStatus): number {
  switch (status) {
    case "in_game":
      return 3;
    case "online":
      return 2;
    case "away":
      return 1;
  }
}

function toIso(value: number | null): string | null {
  return value === null ? null : new Date(value).toISOString();
}

function snapshotFromRow(row: OwnerRow): PresenceSnapshot {
  return {
    userId: row.owner_id,
    status: row.status,
    gameId: row.game_id,
    gameTitle: row.game_title,
    lastSeen: toIso(row.last_seen_ms),
    revision: row.revision,
  };
}

export class PresenceDurableObject extends DurableObject<Env> {
  private deletingOwnerId: string | null = null;
  private deleteOperation: Promise<boolean> | null = null;

  constructor(ctx: DurableObjectState, env: Env) {
    super(ctx, env);
    ctx.blockConcurrencyWhile(async () => {
      this.migrate();
    });
  }

  private migrate(): void {
    const sql = this.ctx.storage.sql;
    sql.exec(`
      CREATE TABLE IF NOT EXISTS _presence_schema_migrations (
        version INTEGER PRIMARY KEY,
        applied_at TEXT NOT NULL
      )
    `);
    const current = sql
      .exec<{ version: number }>(
        `SELECT COALESCE(MAX(version), 0) AS version FROM _presence_schema_migrations`,
      )
      .one().version;
    if (current < 1) this.ctx.storage.transactionSync(() => {
      sql.exec(`
        CREATE TABLE IF NOT EXISTS presence_owner (
          singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
          owner_id TEXT NOT NULL UNIQUE,
          status TEXT NOT NULL CHECK (status IN ('online', 'away', 'in_game', 'offline')),
          game_id TEXT,
          game_title TEXT,
          last_seen_ms INTEGER,
          revision INTEGER NOT NULL
        )
      `);
      sql.exec(`
        CREATE TABLE IF NOT EXISTS presence_connection (
          connection_id TEXT PRIMARY KEY,
          session_id TEXT NOT NULL,
          status TEXT NOT NULL CHECK (status IN ('online', 'away', 'in_game')),
          game_id TEXT,
          game_title TEXT,
          last_seen_ms INTEGER NOT NULL,
          expires_at_ms INTEGER NOT NULL
        )
      `);
      sql.exec(`CREATE INDEX IF NOT EXISTS presence_connection_expiry_idx ON presence_connection (expires_at_ms)`);
      sql.exec(`
        CREATE TABLE IF NOT EXISTS presence_peer (
          peer_user_id TEXT PRIMARY KEY,
          status TEXT NOT NULL CHECK (status IN ('online', 'away', 'in_game', 'offline', 'unknown')),
          game_id TEXT,
          game_title TEXT,
          last_seen_ms INTEGER,
          revision INTEGER NOT NULL,
          availability TEXT NOT NULL CHECK (availability IN ('available', 'unavailable'))
        )
      `);
      sql.exec(
        `INSERT INTO _presence_schema_migrations (version, applied_at) VALUES (1, datetime('now'))`,
      );
    });
    if (current < 2) this.ctx.storage.transactionSync(() => {
      sql.exec(`ALTER TABLE presence_peer ADD COLUMN received_at_ms INTEGER NOT NULL DEFAULT 0`);
      sql.exec(`UPDATE presence_peer SET received_at_ms = ? WHERE received_at_ms = 0`, Date.now());
      sql.exec(
        `INSERT INTO _presence_schema_migrations (version, applied_at) VALUES (2, datetime('now'))`,
      );
    });
  }

  private ensureOwner(ownerId: string): OwnerRow {
    if (!validInternalId(ownerId)) throw new Error("Invalid internal presence owner.");
    const sql = this.ctx.storage.sql;
    const existing = sql.exec<OwnerRow>(`SELECT * FROM presence_owner WHERE singleton = 1`).toArray()[0];
    if (existing) {
      if (existing.owner_id !== ownerId) throw new Error("Presence owner mismatch.");
      return existing;
    }
    sql.exec(
      `INSERT INTO presence_owner
         (singleton, owner_id, status, game_id, game_title, last_seen_ms, revision)
       VALUES (1, ?, 'offline', NULL, NULL, NULL, 0)`,
      ownerId,
    );
    return {
      owner_id: ownerId,
      status: "offline",
      game_id: null,
      game_title: null,
      last_seen_ms: null,
      revision: 0,
    };
  }

  private ownerRow(ownerId: string): OwnerRow {
    this.ensureOwner(ownerId);
    return this.ctx.storage.sql
      .exec<OwnerRow>(`SELECT * FROM presence_owner WHERE singleton = 1`)
      .one();
  }

  private storedOwner(): OwnerRow | null {
    const hasOwnerTable = this.ctx.storage.sql
      .exec<{ present: number }>(
        `SELECT 1 AS present FROM sqlite_master WHERE type = 'table' AND name = 'presence_owner'`,
      )
      .toArray()[0];
    if (!hasOwnerTable) return null;
    return this.ctx.storage.sql.exec<OwnerRow>(`SELECT * FROM presence_owner WHERE singleton = 1`).toArray()[0] ?? null;
  }

  private recomputeOwner(ownerId: string, offlineSeenMs: number | null): PresenceSnapshot {
    const previous = this.ownerRow(ownerId);
    const connections = this.ctx.storage.sql
      .exec<ConnectionRow>(
        `SELECT connection_id, session_id, status, game_id, game_title, last_seen_ms, expires_at_ms
         FROM presence_connection`,
      )
      .toArray();

    let status: PresenceStatus = "offline";
    let gameId: string | null = null;
    let gameTitle: string | null = null;
    let lastSeenMs = previous.last_seen_ms;
    if (connections.length > 0) {
      const selected = [...connections].sort(
        (a, b) =>
          statusRank(b.status) - statusRank(a.status) ||
          b.last_seen_ms - a.last_seen_ms ||
          a.connection_id.localeCompare(b.connection_id),
      )[0];
      status = selected.status;
      gameId = selected.status === "in_game" ? selected.game_id : null;
      gameTitle = selected.status === "in_game" ? selected.game_title : null;
      lastSeenMs = Math.max(...connections.map((connection) => connection.last_seen_ms));
    } else if (offlineSeenMs !== null) {
      lastSeenMs = Math.max(previous.last_seen_ms ?? 0, offlineSeenMs);
    }

    const revision = previous.revision + 1;
    this.ctx.storage.sql.exec(
      `UPDATE presence_owner
       SET status = ?, game_id = ?, game_title = ?, last_seen_ms = ?, revision = ?
       WHERE singleton = 1`,
      status,
      gameId,
      gameTitle,
      lastSeenMs,
      revision,
    );
    return {
      userId: ownerId,
      status,
      gameId,
      gameTitle,
      lastSeen: toIso(lastSeenMs),
      revision,
    };
  }

  private async scheduleNextAlarm(): Promise<void> {
    const row = this.ctx.storage.sql
      .exec<{ expires_at_ms: number | null }>(
        `SELECT MIN(expires_at_ms) AS expires_at_ms FROM (
           SELECT expires_at_ms FROM presence_connection
           UNION ALL
           SELECT received_at_ms + ? AS expires_at_ms FROM presence_peer
         )`,
        PRESENCE_PEER_RETENTION_MS,
      )
      .one();
    if (row.expires_at_ms === null) {
      await this.ctx.storage.deleteAlarm();
      return;
    }
    await this.ctx.storage.setAlarm(row.expires_at_ms);
  }

  private send(socket: WebSocket, message: ServerPresenceMessage): void {
    socket.send(JSON.stringify(message));
  }

  private broadcast(message: ServerPresenceMessage): void {
    for (const socket of this.ctx.getWebSockets()) {
      try {
        this.send(socket, message);
      } catch {
        try {
          socket.close(1011, "Presence delivery failed.");
        } catch {
          // The TTL alarm removes a connection whose close callback cannot run.
        }
      }
    }
  }

  private queueFanOut(snapshot: PresenceSnapshot): void {
    this.ctx.waitUntil(this.fanOut(snapshot));
  }

  private async fanOut(snapshot: PresenceSnapshot): Promise<void> {
    try {
      const privacy = await getProfilePrivacy(this.env, snapshot.userId);
      const shared: PresenceSnapshot = {
        ...snapshot,
        gameId: privacy.activityVisibility === "friends" ? snapshot.gameId : null,
        gameTitle: privacy.activityVisibility === "friends" ? snapshot.gameTitle : null,
      };
      let cursor: string | null = null;
      do {
        const page = await listConnectedFriendIds(this.env, snapshot.userId, { limit: 50, cursor });
        await Promise.allSettled(
          page.userIds.map((friendId) =>
            this.env.PRESENCE.getByName(friendId).receivePeerPresence(friendId, shared),
          ),
        );
        cursor = page.nextCursor;
      } while (cursor);
    } catch {
      // Presence is optional. A policy, D1, or peer failure must not break the socket or library.
    }
  }

  private async removeConnection(socket: WebSocket): Promise<void> {
    if (this.deletingOwnerId) return;
    const attachment = readSocketAttachment(socket.deserializeAttachment());
    if (!attachment) return;
    const existing = this.ctx.storage.sql
      .exec<{ connection_id: string }>(
        `SELECT connection_id FROM presence_connection WHERE connection_id = ? AND session_id = ?`,
        attachment.connectionId,
        attachment.sessionId,
      )
      .toArray()[0];
    if (!existing) return;
    this.ctx.storage.sql.exec(
      `DELETE FROM presence_connection WHERE connection_id = ? AND session_id = ?`,
      attachment.connectionId,
      attachment.sessionId,
    );
    const snapshot = this.recomputeOwner(attachment.ownerId, Date.now());
    await this.scheduleNextAlarm();
    this.queueFanOut(snapshot);
  }

  async fetch(request: Request): Promise<Response> {
    if (this.deletingOwnerId) return new Response("Presence account is being deleted.", { status: 410 });
    if (request.method !== "GET" || request.headers.get("upgrade")?.toLowerCase() !== "websocket") {
      return new Response("Expected Upgrade: websocket", { status: 426 });
    }
    const ownerId = request.headers.get(PRESENCE_OWNER_HEADER) ?? "";
    const sessionId = request.headers.get(PRESENCE_SESSION_HEADER) ?? "";
    if (!validInternalId(ownerId) || !validInternalId(sessionId)) {
      return new Response("Missing internal presence identity", { status: 403 });
    }
    this.ensureOwner(ownerId);
    if (this.ctx.getWebSockets().length >= MAX_PRESENCE_CONNECTIONS_PER_USER) {
      return new Response("Too many presence connections.", { status: 429 });
    }

    const connectionId = crypto.randomUUID();
    const now = Date.now();
    const pair = new WebSocketPair();
    const client = pair[0];
    const server = pair[1];
    const attachment = createSocketAttachment(ownerId, sessionId, connectionId);
    server.serializeAttachment(attachment);
    this.ctx.acceptWebSocket(server, [`connection:${connectionId}`]);

    this.ctx.storage.sql.exec(
      `INSERT INTO presence_connection
         (connection_id, session_id, status, game_id, game_title, last_seen_ms, expires_at_ms)
       VALUES (?, ?, 'online', NULL, NULL, ?, ?)`,
      connectionId,
      sessionId,
      now,
      now + PRESENCE_TTL_MS,
    );
    const snapshot = this.recomputeOwner(ownerId, null);
    await this.scheduleNextAlarm();
    this.send(server, { type: "ready", self: publicPresence(snapshot, true) });
    this.queueFanOut(snapshot);
    return new Response(null, { status: 101, webSocket: client });
  }

  async getOwnerPresence(ownerId: string): Promise<PresenceSnapshot> {
    return snapshotFromRow(this.ownerRow(ownerId));
  }

  async peekOwnerPresence(ownerId: string): Promise<PresenceSnapshot | null> {
    if (!validInternalId(ownerId)) return null;
    const owner = this.storedOwner();
    if (!owner || owner.owner_id !== ownerId) return null;
    return snapshotFromRow(owner);
  }

  async getPresenceFor(ownerId: string, viewerId: string): Promise<PresenceSnapshot | null> {
    if (this.deletingOwnerId) return null;
    try {
      this.ensureOwner(ownerId);
    } catch {
      return null;
    }
    if (viewerId !== ownerId) {
      try {
        if (!(await areConnectedFriends(this.env, ownerId, viewerId))) return null;
      } catch {
        return null;
      }
    }
    const snapshot = snapshotFromRow(this.ownerRow(ownerId));
    if (viewerId === ownerId) return snapshot;
    try {
      const privacy = await getProfilePrivacy(this.env, ownerId);
      return privacy.activityVisibility === "friends"
        ? snapshot
        : { ...snapshot, gameId: null, gameTitle: null };
    } catch {
      return { ...snapshot, gameId: null, gameTitle: null };
    }
  }

  async receivePeerPresence(ownerId: string, snapshot: PresenceSnapshot): Promise<void> {
    if (this.deletingOwnerId) return;
    this.ensureOwner(ownerId);
    if (
      !validInternalId(snapshot.userId) ||
      snapshot.userId === ownerId ||
      !["online", "away", "in_game", "offline"].includes(snapshot.status) ||
      !Number.isSafeInteger(snapshot.revision) ||
      snapshot.revision < 0
    ) {
      return;
    }
    try {
      if (!(await areConnectedFriends(this.env, ownerId, snapshot.userId))) return;
      const privacy = await getProfilePrivacy(this.env, snapshot.userId);
      const visible =
        privacy.activityVisibility === "friends"
          ? snapshot
          : { ...snapshot, gameId: null, gameTitle: null };
      const existing = this.ctx.storage.sql
        .exec<{ revision: number }>(
          `SELECT revision FROM presence_peer WHERE peer_user_id = ?`,
          visible.userId,
        )
        .toArray()[0];
      if (existing && existing.revision > visible.revision) return;
      this.ctx.storage.sql.exec(
        `INSERT INTO presence_peer
           (peer_user_id, status, game_id, game_title, last_seen_ms, revision, availability, received_at_ms)
         VALUES (?, ?, ?, ?, ?, ?, 'available', ?)
         ON CONFLICT(peer_user_id) DO UPDATE SET
           status = excluded.status,
           game_id = excluded.game_id,
           game_title = excluded.game_title,
           last_seen_ms = excluded.last_seen_ms,
           revision = excluded.revision,
           availability = 'available',
           received_at_ms = excluded.received_at_ms`,
        visible.userId,
        visible.status,
        visible.gameId,
        visible.gameTitle,
        visible.lastSeen === null ? null : Date.parse(visible.lastSeen),
        visible.revision,
        Date.now(),
      );
      await this.scheduleNextAlarm();
      this.broadcast({ type: "presence", presence: rosterEntry(publicPresence(visible, true)) });
    } catch {
      // Relation/privacy/backend failure is a deny and never interrupts the sender.
    }
  }

  async getRoster(ownerId: string, peerIds: string[]): Promise<PresenceRosterEntry[]> {
    if (this.deletingOwnerId) return peerIds.slice(0, 50).map(unavailablePresence);
    this.ensureOwner(ownerId);
    if (peerIds.length > 50) throw new RangeError("Presence roster is limited to 50 friends.");
    const unique = [...new Set(peerIds)];
    const settled = await Promise.allSettled(
      unique.map(async (peerId) => {
        if (!validInternalId(peerId) || peerId === ownerId) return unavailablePresence(peerId);
        if (!(await areConnectedFriends(this.env, ownerId, peerId))) return null;
        const snapshot = await this.env.PRESENCE.getByName(peerId).getPresenceFor(peerId, ownerId);
        return snapshot ? rosterEntry(publicPresence(snapshot, true)) : unavailablePresence(peerId);
      }),
    );

    const rows: PresenceRosterEntry[] = [];
    for (let index = 0; index < settled.length; index++) {
      const result = settled[index];
      const row = result.status === "fulfilled" ? result.value : unavailablePresence(unique[index]);
      if (!row) continue;
      rows.push(row);
      this.ctx.storage.sql.exec(
        `INSERT INTO presence_peer
           (peer_user_id, status, game_id, game_title, last_seen_ms, revision, availability, received_at_ms)
         VALUES (?, ?, ?, ?, ?, 0, ?, ?)
         ON CONFLICT(peer_user_id) DO UPDATE SET
           status = excluded.status,
           game_id = excluded.game_id,
           game_title = excluded.game_title,
           last_seen_ms = excluded.last_seen_ms,
           availability = excluded.availability,
           received_at_ms = excluded.received_at_ms`,
        row.userId,
        row.status,
        row.gameId,
        row.gameTitle,
        row.lastSeen === null ? null : Date.parse(row.lastSeen),
        row.availability,
        Date.now(),
      );
    }
    if (rows.length > 0) await this.scheduleNextAlarm();
    return rows;
  }

  async removePeerPresence(ownerId: string, peerId: string): Promise<boolean> {
    if (!validInternalId(ownerId) || !validInternalId(peerId) || ownerId === peerId) return false;
    const owner = this.storedOwner();
    if (!owner) return true;
    if (owner.owner_id !== ownerId) return false;
    this.ctx.storage.sql.exec(`DELETE FROM presence_peer WHERE peer_user_id = ?`, peerId);
    this.broadcast({ type: "presence", presence: unavailablePresence(peerId) });
    await this.scheduleNextAlarm();
    return true;
  }

  private async purgePeerCopies(ownerId: string): Promise<void> {
    let cursor = "";
    while (true) {
      const rows = await this.env.DB.prepare(
        `WITH related(peer_id) AS (
           SELECT CASE WHEN user_low = ? THEN user_high ELSE user_low END
             FROM direct_friendship WHERE user_low = ? OR user_high = ?
           UNION
           SELECT CASE WHEN user_low = ? THEN user_high ELSE user_low END
             FROM discovered_connection WHERE user_low = ? OR user_high = ?
           UNION
           SELECT CASE WHEN blocker_id = ? THEN blocked_id ELSE blocker_id END
             FROM user_block WHERE blocker_id = ? OR blocked_id = ?
           UNION
           SELECT CASE WHEN user_low = ? THEN user_high ELSE user_low END
             FROM friend_suppression WHERE user_low = ? OR user_high = ?
           UNION
           SELECT CASE WHEN sender_id = ? THEN recipient_id ELSE sender_id END
             FROM friend_request WHERE sender_id = ? OR recipient_id = ?
         )
         SELECT peer_id FROM related WHERE peer_id > ? ORDER BY peer_id LIMIT 50`,
      )
        .bind(
          ownerId, ownerId, ownerId,
          ownerId, ownerId, ownerId,
          ownerId, ownerId, ownerId,
          ownerId, ownerId, ownerId,
          ownerId, ownerId, ownerId,
          cursor,
        )
        .all<{ peer_id: string }>();
      const peers = (rows.results ?? []).map((row) => row.peer_id);
      if (peers.length === 0) return;
      const settled = await Promise.allSettled(
        peers.map((peerId) => this.env.PRESENCE.getByName(peerId).removePeerPresence(peerId, ownerId)),
      );
      if (settled.some((result) => result.status === "rejected" || result.value !== true)) {
        throw new Error("Presence peer cleanup failed.");
      }
      if (peers.length < 50) return;
      cursor = peers[peers.length - 1]!;
    }
  }

  async deleteAccount(ownerId: string): Promise<boolean> {
    if (!validInternalId(ownerId)) return false;
    if (this.deletingOwnerId) {
      if (this.deletingOwnerId !== ownerId) return false;
      return this.deleteOperation ?? true;
    }

    const owner = this.storedOwner();
    if (owner && owner.owner_id !== ownerId) return false;

    this.deletingOwnerId = ownerId;
    const operation = this.performAccountDeletion(ownerId, owner);
    this.deleteOperation = operation;
    try {
      return await operation;
    } catch (error) {
      this.deletingOwnerId = null;
      this.deleteOperation = null;
      throw error;
    }
  }

  private async performAccountDeletion(ownerId: string, owner: OwnerRow | null): Promise<boolean> {
    await this.purgePeerCopies(ownerId);

    for (const socket of this.ctx.getWebSockets()) {
      try {
        socket.close(4001, "Presence account deleted.");
      } catch {
        // Storage deletion below is authoritative even if a socket is already gone.
      }
    }
    await this.ctx.storage.deleteAlarm();
    await this.ctx.storage.deleteAll();
    return true;
  }

  async webSocketMessage(socket: WebSocket, message: string | ArrayBuffer): Promise<void> {
    if (this.deletingOwnerId) {
      socket.close(4001, "Presence account deleted.");
      return;
    }
    const attachment = readSocketAttachment(socket.deserializeAttachment());
    if (!attachment || typeof message !== "string") {
      this.send(socket, { type: "error", code: "INVALID_MESSAGE", message: "Text JSON is required." });
      socket.close(typeof message === "string" ? 1008 : 1003, "Invalid presence message.");
      await this.removeConnection(socket);
      return;
    }

    let parsed;
    try {
      parsed = parseClientPresenceMessage(message);
    } catch (error) {
      const reason = error instanceof PresenceMessageError ? error.message : "Presence message is invalid.";
      this.send(socket, { type: "error", code: "INVALID_MESSAGE", message: reason });
      socket.close(reason.includes("4096") ? 1009 : 1008, "Invalid presence message.");
      await this.removeConnection(socket);
      return;
    }

    let sessionActive = false;
    try {
      const active = await this.env.DB.prepare(
        `SELECT 1 AS active FROM session
         WHERE id = ? AND userId = ? AND expiresAt > ?
         LIMIT 1`,
      )
        .bind(attachment.sessionId, attachment.ownerId, new Date().toISOString())
        .first<{ active: number }>();
      sessionActive = active?.active === 1;
    } catch {
      sessionActive = false;
    }
    if (!sessionActive) {
      socket.close(4003, "Presence session expired.");
      await this.removeConnection(socket);
      return;
    }

    const now = Date.now();
    const result =
      parsed.type === "heartbeat"
        ? this.ctx.storage.sql.exec(
            `UPDATE presence_connection
             SET last_seen_ms = ?, expires_at_ms = ?
             WHERE connection_id = ? AND session_id = ?`,
            now,
            now + PRESENCE_TTL_MS,
            attachment.connectionId,
            attachment.sessionId,
          )
        : this.ctx.storage.sql.exec(
            `UPDATE presence_connection
             SET status = ?, game_id = ?, game_title = ?, last_seen_ms = ?, expires_at_ms = ?
             WHERE connection_id = ? AND session_id = ?`,
            parsed.status,
            parsed.gameId,
            parsed.gameTitle,
            now,
            now + PRESENCE_TTL_MS,
            attachment.connectionId,
            attachment.sessionId,
          );
    if (result.rowsWritten === 0) {
      socket.close(1008, "Presence connection expired.");
      return;
    }

    const snapshot = this.recomputeOwner(attachment.ownerId, null);
    await this.scheduleNextAlarm();
    this.send(socket, { type: "ack", self: publicPresence(snapshot, true) });
    this.queueFanOut(snapshot);
  }

  async webSocketClose(socket: WebSocket, code: number, reason: string): Promise<void> {
    if (this.deletingOwnerId) return;
    await this.removeConnection(socket);
    try {
      socket.close(code, reason);
    } catch {
      // The peer may already have completed the close handshake.
    }
  }

  async webSocketError(socket: WebSocket, _error: unknown): Promise<void> {
    try {
      socket.close(1011, "Presence connection error.");
    } catch {
      // The socket is already unusable.
    }
    if (!this.deletingOwnerId) await this.removeConnection(socket);
  }

  async alarm(): Promise<void> {
    if (this.deletingOwnerId) return;
    const now = Date.now();
    this.ctx.storage.sql.exec(
      `DELETE FROM presence_peer WHERE received_at_ms <= ?`,
      now - PRESENCE_PEER_RETENTION_MS,
    );
    const expired = this.ctx.storage.sql
      .exec<{ connection_id: string }>(
        `SELECT connection_id FROM presence_connection WHERE expires_at_ms <= ?`,
        now,
      )
      .toArray();
    if (expired.length === 0) {
      await this.scheduleNextAlarm();
      return;
    }
    const expiredIds = new Set(expired.map((row) => row.connection_id));
    this.ctx.storage.sql.exec(`DELETE FROM presence_connection WHERE expires_at_ms <= ?`, now);
    for (const socket of this.ctx.getWebSockets()) {
      const attachment = readSocketAttachment(socket.deserializeAttachment());
      if (!attachment || !expiredIds.has(attachment.connectionId)) continue;
      try {
        socket.close(4000, "Presence heartbeat timed out.");
      } catch {
        // The stale row is already gone, which is the critical cleanup.
      }
    }
    const owner = this.ctx.storage.sql.exec<OwnerRow>(`SELECT * FROM presence_owner WHERE singleton = 1`).toArray()[0];
    if (owner) {
      const snapshot = this.recomputeOwner(owner.owner_id, now);
      await this.scheduleNextAlarm();
      await this.fanOut(snapshot);
    } else {
      await this.scheduleNextAlarm();
    }
  }
}
