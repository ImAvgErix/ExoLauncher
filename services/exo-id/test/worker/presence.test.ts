import { env, evictDurableObject, runDurableObjectAlarm, runInDurableObject } from "cloudflare:test";
import { describe, expect, it } from "vitest";
import type { PresenceDurableObject } from "../../src/presence-do.ts";
import type { PresenceSnapshot, ServerPresenceMessage } from "../../src/presence.ts";
import { api, authHeaders, seedUser } from "./helpers.ts";

type TestUser = Awaited<ReturnType<typeof seedUser>>;

function nextMessage(socket: WebSocket): Promise<ServerPresenceMessage> {
  return new Promise((resolve, reject) => {
    const onMessage = (event: MessageEvent) => {
      cleanup();
      try {
        resolve(JSON.parse(String(event.data)) as ServerPresenceMessage);
      } catch (error) {
        reject(error);
      }
    };
    const onClose = () => {
      cleanup();
      reject(new Error("Presence socket closed before a message arrived."));
    };
    const cleanup = () => {
      socket.removeEventListener("message", onMessage);
      socket.removeEventListener("close", onClose);
    };
    socket.addEventListener("message", onMessage);
    socket.addEventListener("close", onClose);
  });
}

function nextClose(socket: WebSocket): Promise<CloseEvent> {
  return new Promise((resolve) => socket.addEventListener("close", resolve, { once: true }));
}

async function openPresence(user: TestUser): Promise<{ socket: WebSocket; ready: ServerPresenceMessage }> {
  const response = await api("/v1/presence/socket", {
    headers: { ...authHeaders(user.token), Upgrade: "websocket" },
  });
  expect(response.status).toBe(101);
  expect(response.webSocket).not.toBeNull();
  const socket = response.webSocket!;
  const ready = nextMessage(socket);
  socket.accept();
  return { socket, ready: await ready };
}

async function snapshot(userId: string): Promise<PresenceSnapshot> {
  return env.PRESENCE.getByName(userId).getOwnerPresence(userId);
}

async function connectFriends(a: string, b: string): Promise<void> {
  const [low, high] = [a, b].sort();
  await env.DB.prepare(
    `INSERT INTO direct_friendship (user_low, user_high, created_at) VALUES (?, ?, ?)`,
  )
    .bind(low, high, new Date().toISOString())
    .run();
}

describe("presence WebSocket", () => {
  it("authenticates upgrades and isolates one Durable Object per user", async () => {
    expect((await api("/v1/presence/socket", { headers: { Upgrade: "websocket" } })).status).toBe(401);

    const first = await seedUser("presence-first@example.test");
    const second = await seedUser("presence-second@example.test");
    expect((await api("/v1/presence/socket", { headers: authHeaders(first.token) })).status).toBe(426);

    const firstSocket = await openPresence(first);
    const secondSocket = await openPresence(second);
    expect(firstSocket.ready).toEqual({
      type: "ready",
      self: expect.objectContaining({ userId: first.id, status: "online" }),
    });
    expect(secondSocket.ready).toEqual({
      type: "ready",
      self: expect.objectContaining({ userId: second.id, status: "online" }),
    });

    const ack = nextMessage(firstSocket.socket);
    firstSocket.socket.send(
      JSON.stringify({ type: "status", status: "in_game", gameId: "steam:10", gameTitle: "Counter-Strike" }),
    );
    expect(await ack).toEqual({
      type: "ack",
      self: expect.objectContaining({ userId: first.id, status: "in_game", gameId: "steam:10" }),
    });
    expect((await snapshot(first.id)).status).toBe("in_game");
    expect((await snapshot(second.id)).status).toBe("online");

    const guarded = env.PRESENCE.getByName("presence-owner-guard");
    await guarded.getOwnerPresence(first.id);
    expect(await guarded.getPresenceFor(second.id, first.id)).toBeNull();

    firstSocket.socket.close(1000, "done");
    secondSocket.socket.close(1000, "done");
  });

  it("keeps multi-device users online until the last connection closes or expires", async () => {
    const user = await seedUser("presence-multi@example.test");
    const first = await openPresence(user);
    const second = await openPresence(user);

    let ack = nextMessage(first.socket);
    first.socket.send(JSON.stringify({ type: "status", status: "in_game", gameTitle: "Game" }));
    await ack;
    ack = nextMessage(second.socket);
    second.socket.send(JSON.stringify({ type: "status", status: "away" }));
    await ack;
    expect((await snapshot(user.id)).status).toBe("in_game");

    first.socket.close(1000, "device closed");
    await new Promise((resolve) => setTimeout(resolve, 0));
    expect((await snapshot(user.id)).status).toBe("away");

    const stub = env.PRESENCE.getByName(user.id);
    await runInDurableObject(stub, (_instance: PresenceDurableObject, state) => {
      state.storage.sql.exec(`UPDATE presence_connection SET expires_at_ms = 0`);
      return state.storage.setAlarm(Date.now() + 60_000);
    });
    expect(await runDurableObjectAlarm(stub)).toBe(true);
    expect((await snapshot(user.id)).status).toBe("offline");
    second.socket.close(1000, "done");
  });

  it("caps concurrent presence sockets per user", async () => {
    const user = await seedUser("presence-cap@example.test");
    const sockets: WebSocket[] = [];
    try {
      for (let index = 0; index < 8; index++) {
        sockets.push((await openPresence(user)).socket);
      }
      const extra = await api("/v1/presence/socket", {
        headers: { ...authHeaders(user.token), Upgrade: "websocket" },
      });
      expect(extra.status).toBe(429);
      expect(extra.webSocket).toBeNull();
    } finally {
      for (const socket of sockets) socket.close(1000, "done");
    }
  });

  it("expires retained peer presence rows", async () => {
    const user = await seedUser("presence-peer-ttl@example.test");
    const stub = env.PRESENCE.getByName(user.id);
    await stub.getOwnerPresence(user.id);
    await runInDurableObject(stub, (_instance: PresenceDurableObject, state) => {
      state.storage.sql.exec(
        `INSERT INTO presence_peer
          (peer_user_id, status, game_id, game_title, last_seen_ms, revision, availability, received_at_ms)
         VALUES ('expired-peer', 'offline', NULL, NULL, 0, 1, 'available', 0)`,
      );
      return state.storage.setAlarm(Date.now() + 60_000);
    });
    expect(await runDurableObjectAlarm(stub)).toBe(true);
    await runInDurableObject(stub, (_instance: PresenceDurableObject, state) => {
      expect(state.storage.sql.exec<{ count: number }>(
        `SELECT COUNT(*) AS count FROM presence_peer`,
      ).one().count).toBe(0);
    });
  });

  it("survives hibernation with bounded attachments and rejects invalid client identity/messages", async () => {
    const user = await seedUser("presence-hibernate@example.test");
    const { socket } = await openPresence(user);
    const stub = env.PRESENCE.getByName(user.id);

    await runInDurableObject(stub, (_instance: PresenceDurableObject, state) => {
      const attached = state.getWebSockets();
      expect(attached).toHaveLength(1);
      const attachment = attached[0].deserializeAttachment();
      expect(attachment).toEqual(
        expect.objectContaining({ version: 1, ownerId: user.id, sessionId: user.sessionId }),
      );
      expect(new TextEncoder().encode(JSON.stringify(attachment)).byteLength).toBeLessThanOrEqual(16 * 1024);
    });

    await evictDurableObject(stub);
    const ack = nextMessage(socket);
    socket.send(JSON.stringify({ type: "heartbeat" }));
    expect(await ack).toEqual({ type: "ack", self: expect.objectContaining({ userId: user.id }) });

    const error = nextMessage(socket);
    const closed = nextClose(socket);
    socket.send(JSON.stringify({ type: "heartbeat", userId: "spoofed" }));
    expect(await error).toEqual({
      type: "error",
      code: "INVALID_MESSAGE",
      message: expect.any(String),
    });
    expect((await closed).code).toBe(1008);

    const binarySocket = await openPresence(user);
    const binaryError = nextMessage(binarySocket.socket);
    const binaryClosed = nextClose(binarySocket.socket);
    binarySocket.socket.send(new Uint8Array([1, 2, 3]).buffer);
    expect(await binaryError).toEqual({
      type: "error",
      code: "INVALID_MESSAGE",
      message: "Text JSON is required.",
    });
    expect((await binaryClosed).code).toBe(1003);

    const oversizedSocket = await openPresence(user);
    const oversizedError = nextMessage(oversizedSocket.socket);
    const oversizedClosed = nextClose(oversizedSocket.socket);
    oversizedSocket.socket.send(JSON.stringify({ type: "heartbeat", padding: "x".repeat(4096) }));
    expect(await oversizedError).toEqual({
      type: "error",
      code: "INVALID_MESSAGE",
      message: "Presence message exceeds 4096 bytes.",
    });
    expect((await oversizedClosed).code).toBe(1009);
  });

  it("closes and removes a socket when its Better Auth session is revoked", async () => {
    const user = await seedUser("presence-revoked@example.test");
    const { socket } = await openPresence(user);
    await env.DB.prepare(`DELETE FROM session WHERE id = ?`).bind(user.sessionId).run();

    const closed = nextClose(socket);
    socket.send(JSON.stringify({ type: "heartbeat" }));
    expect((await closed).code).toBe(4003);
    expect((await snapshot(user.id)).status).toBe("offline");
  });

  it("deletes account presence after publishing offline and is repeat-safe", async () => {
    const owner = await seedUser("presence-delete-owner@example.test");
    const friend = await seedUser("presence-delete-friend@example.test");
    await connectFriends(owner.id, friend.id);

    const friendSocket = await openPresence(friend);
    const initialPresence = nextMessage(friendSocket.socket);
    const ownerSocket = await openPresence(owner);
    await initialPresence;
    const secondOnlinePresence = nextMessage(friendSocket.socket);
    const secondOwnerSocket = await openPresence(owner);
    await secondOnlinePresence;

    const stub = env.PRESENCE.getByName(owner.id);
    expect(await stub.deleteAccount(friend.id)).toBe(false);
    expect((await snapshot(owner.id)).status).toBe("online");

    const removedPresence = nextMessage(friendSocket.socket);
    const ownerClosed = nextClose(ownerSocket.socket);
    const secondOwnerClosed = nextClose(secondOwnerSocket.socket);
    expect(await stub.deleteAccount(owner.id)).toBe(true);
    expect(await removedPresence).toEqual({
      type: "presence",
      presence: {
        userId: owner.id,
        status: "unknown",
        gameId: null,
        gameTitle: null,
        lastSeen: null,
        availability: "unavailable",
      },
    });
    expect((await ownerClosed).code).toBe(4001);
    expect((await secondOwnerClosed).code).toBe(4001);

    await runInDurableObject(stub, (_instance: PresenceDurableObject, state) => {
      expect(state.getWebSockets()).toHaveLength(0);
      expect(state.storage.sql.exec<{ name: string }>(
        `SELECT name FROM sqlite_master
         WHERE type = 'table' AND name IN ('presence_owner', 'presence_connection', 'presence_peer')`,
      ).toArray()).toEqual([]);
      return expect(state.storage.getAlarm()).resolves.toBeNull();
    });
    await runInDurableObject(
      env.PRESENCE.getByName(friend.id),
      (_instance: PresenceDurableObject, state) => {
        expect(state.storage.sql.exec<{ count: number }>(
          `SELECT COUNT(*) AS count FROM presence_peer WHERE peer_user_id = ?`,
          owner.id,
        ).one().count).toBe(0);
      },
    );

    expect(await stub.deleteAccount(owner.id)).toBe(true);
    await runInDurableObject(stub, (_instance: PresenceDurableObject, state) => {
      expect(state.getWebSockets()).toHaveLength(0);
      return expect(state.storage.getAlarm()).resolves.toBeNull();
    });

    friendSocket.socket.close(1000, "done");
  });
});

describe("presence friends and REST fallback", () => {
  it("fans out only to connected, unblocked friends and scrubs private game activity", async () => {
    const owner = await seedUser("presence-owner@example.test");
    const friend = await seedUser("presence-friend@example.test");
    await connectFriends(owner.id, friend.id);
    const friendSocket = await openPresence(friend);

    await env.DB.prepare(
      `INSERT INTO profile_privacy
         (user_id, profile_visibility, searchable, request_policy, activity_visibility, updated_at)
       VALUES (?, 'friends', 0, 'anyone', 'private', ?)`,
    )
      .bind(owner.id, new Date().toISOString())
      .run();

    const initialPeerUpdate = nextMessage(friendSocket.socket);
    const ownerSocket = await openPresence(owner);
    await initialPeerUpdate;

    const peerUpdate = nextMessage(friendSocket.socket);
    const ownerAck = nextMessage(ownerSocket.socket);
    ownerSocket.socket.send(
      JSON.stringify({ type: "status", status: "in_game", gameId: "steam:20", gameTitle: "Private Game" }),
    );
    await ownerAck;
    expect(await peerUpdate).toEqual({
      type: "presence",
      presence: expect.objectContaining({
        userId: owner.id,
        status: "in_game",
        gameId: null,
        gameTitle: null,
        availability: "available",
      }),
    });

    const rest = await api("/v1/presence?limit=50", { headers: authHeaders(friend.token) });
    expect(rest.status).toBe(200);
    expect(await rest.json()).toEqual({
      friends: [
        expect.objectContaining({ userId: owner.id, status: "in_game", gameId: null, availability: "available" }),
      ],
      unavailable: false,
    });

    await env.DB.prepare(`INSERT INTO user_block (blocker_id, blocked_id, created_at) VALUES (?, ?, ?)`)
      .bind(friend.id, owner.id, new Date().toISOString())
      .run();
    const blocked = await api("/v1/presence", { headers: authHeaders(friend.token) });
    expect(await blocked.json()).toEqual({ friends: [], unavailable: false });

    ownerSocket.socket.close(1000, "done");
    friendSocket.socket.close(1000, "done");
  });

  it("reconnects cleanly and represents unreachable peer state as unknown, never offline", async () => {
    const owner = await seedUser("presence-reconnect-owner@example.test");
    const friend = await seedUser("presence-reconnect-friend@example.test");
    const unavailablePeerId = "u".repeat(513);
    await connectFriends(owner.id, friend.id);
    const now = new Date().toISOString();
    await env.DB.prepare(
      `INSERT INTO user (id, name, email, emailVerified, createdAt, updatedAt)
       VALUES (?, 'Unavailable', 'presence-unavailable@example.test', 1, ?, ?)`,
    )
      .bind(unavailablePeerId, now, now)
      .run();
    await connectFriends(unavailablePeerId, friend.id);

    const first = await openPresence(owner);
    first.socket.close(1000, "restart");
    await new Promise((resolve) => setTimeout(resolve, 0));
    expect((await snapshot(owner.id)).status).toBe("offline");
    const second = await openPresence(owner);
    expect((await snapshot(owner.id)).status).toBe("online");

    const response = await api("/v1/presence", { headers: authHeaders(friend.token) });
    expect(response.status).toBe(200);
    const body = (await response.json()) as {
      friends: Array<Record<string, unknown>>;
      unavailable: boolean;
    };
    expect(body.friends).toContainEqual(expect.objectContaining({ userId: owner.id, status: "online" }));
    expect(body.friends).toContainEqual({
      userId: unavailablePeerId,
      status: "unknown",
      gameId: null,
      gameTitle: null,
      lastSeen: null,
      availability: "unavailable",
    });
    expect(body.unavailable).toBe(true);

    second.socket.close(1000, "done");
  });

  it("fans an authoritative Offline close to friends and never converts private activity into offline", async () => {
    const owner = await seedUser("presence-offline-owner@example.test");
    const friend = await seedUser("presence-offline-friend@example.test");
    await connectFriends(owner.id, friend.id);
    const friendSocket = await openPresence(friend);
    const initial = nextMessage(friendSocket.socket);
    const ownerSocket = await openPresence(owner);
    await initial;

    const inGame = nextMessage(friendSocket.socket);
    const ownerAck = nextMessage(ownerSocket.socket);
    ownerSocket.socket.send(
      JSON.stringify({ type: "status", status: "in_game", gameId: "steam:10", gameTitle: "Counter-Strike" }),
    );
    await ownerAck;
    expect(await inGame).toEqual({
      type: "presence",
      presence: expect.objectContaining({
        userId: owner.id,
        status: "in_game",
        gameId: "steam:10",
        availability: "available",
      }),
    });

    const offline = nextMessage(friendSocket.socket);
    ownerSocket.socket.close(1000, "done");
    expect(await offline).toEqual({
      type: "presence",
      presence: expect.objectContaining({
        userId: owner.id,
        status: "offline",
        gameId: null,
        gameTitle: null,
        availability: "available",
      }),
    });
    expect((await snapshot(owner.id)).status).toBe("offline");

    const rest = await api("/v1/presence", { headers: authHeaders(friend.token) });
    expect(await rest.json()).toEqual({
      friends: [
        expect.objectContaining({
          userId: owner.id,
          status: "offline",
          availability: "available",
        }),
      ],
      unavailable: false,
    });
    friendSocket.socket.close(1000, "done");
  });
});
