import { env } from "cloudflare:test";
import { describe, expect, it } from "vitest";
import { areConnectedFriends, canViewProfile } from "../../src/policy.ts";
import { api, authHeaders, field, seedUser } from "./helpers.ts";

type TestUser = Awaited<ReturnType<typeof seedUser>>;
type ErrorResponse = { error: { code: string } };

function onlineApi(path: string, init: RequestInit = {}): Promise<Response> {
  return api(path, init);
}

async function json<T>(response: Response): Promise<T> {
  return response.json() as Promise<T>;
}

let handleIpSeq = 1;

async function claim(user: TestUser, handle: string): Promise<void> {
  const ipOctet = (handleIpSeq++ % 250) + 1;
  const response = await api("/v1/handle", {
    method: "PUT",
    headers: { ...authHeaders(user.token), "cf-connecting-ip": `198.51.100.${ipOctet}` },
    body: JSON.stringify({ handle }),
  });
  expect(response.status).toBe(200);
}

async function writeProfile(user: TestUser, values: Record<string, unknown>): Promise<void> {
  const stamp = new Date().toISOString();
  const response = await api("/v1/profile", {
    method: "PUT",
    headers: authHeaders(user.token),
    body: JSON.stringify({
      deviceId: "test-pc",
      fields: Object.fromEntries(Object.entries(values).map(([key, value]) => [key, field(value, stamp)])),
    }),
  });
  expect(response.status).toBe(200);
}

async function putPrivacy(
  user: TestUser,
  overrides: Partial<{
    profileVisibility: "public" | "friends" | "private";
    searchable: boolean;
    requestPolicy: "anyone" | "none";
    activityVisibility: "friends" | "private";
  }> = {},
): Promise<Response> {
  return onlineApi("/v1/profile/privacy", {
    method: "PUT",
    headers: authHeaders(user.token),
    body: JSON.stringify({
      profileVisibility: "friends",
      searchable: false,
      requestPolicy: "anyone",
      activityVisibility: "friends",
      ...overrides,
    }),
  });
}

async function requestFriend(sender: TestUser, handle: string): Promise<Response> {
  return onlineApi("/v1/friends/requests", {
    method: "POST",
    headers: authHeaders(sender.token),
    body: JSON.stringify({ handle }),
  });
}

describe("profile privacy and public projection", () => {
  it("uses privacy-safe defaults and never returns private field vectors or account PII", async () => {
    const owner = await seedUser("social-private-owner@example.test");
    await claim(owner, "quietowner");
    await writeProfile(owner, { displayName: "Quiet", bio: "hello", accent: "sage" });

    const privacy = await onlineApi("/v1/profile/privacy", { headers: authHeaders(owner.token) });
    expect(privacy.status).toBe(200);
    expect(await json(privacy)).toEqual({
      privacy: {
        profileVisibility: "friends",
        searchable: false,
        requestPolicy: "anyone",
        activityVisibility: "friends",
        updatedAt: null,
      },
    });

    const hidden = await onlineApi("/v1/profiles/quietowner");
    expect(hidden.status).toBe(404);
    expect((await json<ErrorResponse>(hidden)).error.code).toBe("NOT_FOUND");

    const self = await onlineApi("/v1/profiles/QUIETOWNER", { headers: authHeaders(owner.token) });
    expect(self.status).toBe(200);
    const body = await json<Record<string, unknown>>(self);
    expect(body).toEqual({
      userId: owner.id,
      handle: { display: "quietowner", normalized: "quietowner" },
      profile: { accent: "sage", bio: "hello", displayName: "Quiet" },
      media: { avatar: null, banner: null },
      badges: [],
    });
    const serialized = JSON.stringify(body);
    expect(serialized).not.toContain("social-private-owner@example.test");
    expect(serialized).not.toContain(owner.token);
    expect(serialized).not.toContain("deviceId");
    expect(serialized).not.toContain("updatedAt");
  });

  it("allows public or connected-friend reads while either-direction blocks stay indistinguishable from missing", async () => {
    const owner = await seedUser("social-visible-owner@example.test");
    const friend = await seedUser("social-visible-friend@example.test");
    await claim(owner, "visibleowner");
    await claim(friend, "visiblefriend");
    await writeProfile(owner, { displayName: "Visible" });

    expect((await putPrivacy(owner, { profileVisibility: "public" })).status).toBe(200);
    expect((await onlineApi("/v1/profiles/visibleowner")).status).toBe(200);
    expect((await onlineApi("/v1/profiles/visibleowner", {
      headers: { authorization: "Bearer invalid-session" },
    })).status).toBe(401);

    expect((await putPrivacy(owner, { profileVisibility: "friends" })).status).toBe(200);
    expect((await onlineApi("/v1/profiles/visibleowner", { headers: authHeaders(friend.token) })).status).toBe(404);

    const sent = await requestFriend(friend, "visibleowner");
    const request = await json<{ request: { id: string } }>(sent);
    const accepted = await onlineApi(`/v1/friends/requests/${request.request.id}/accept`, {
      method: "POST",
      headers: authHeaders(owner.token),
    });
    expect(accepted.status).toBe(200);
    expect((await onlineApi("/v1/profiles/visibleowner", { headers: authHeaders(friend.token) })).status).toBe(200);

    const blocked = await onlineApi(`/v1/blocks/${friend.id}`, {
      method: "PUT",
      headers: authHeaders(owner.token),
    });
    expect(blocked.status).toBe(200);
    const afterBlock = await onlineApi("/v1/profiles/visibleowner", { headers: authHeaders(friend.token) });
    expect(afterBlock.status).toBe(404);
    expect((await json<ErrorResponse>(afterBlock)).error.code).toBe("NOT_FOUND");
  });

  it("serves escaped no-script share metadata only for anonymous-public profiles", async () => {
    const owner = await seedUser("social-share-owner@example.test");
    await claim(owner, "shareowner");
    await writeProfile(owner, { displayName: '<script>alert("x")</script>', statusText: "quiet & local" });

    expect((await onlineApi("/p/shareowner")).status).toBe(404);
    await putPrivacy(owner, { profileVisibility: "public" });
    const shared = await onlineApi("/p/shareowner");
    expect(shared.status).toBe(200);
    expect(shared.headers.get("content-security-policy")).toContain("default-src 'none'");
    expect(shared.headers.get("cache-control")).toBe("no-store");
    const html = await shared.text();
    expect(html).toContain("&lt;script&gt;alert(&quot;x&quot;)&lt;/script&gt;");
    expect(html).toContain("quiet &amp; local");
    expect(html).not.toContain("<script");
    expect(html).not.toContain("social-share-owner@example.test");
  });
});

describe("atomic profile writes", () => {
  it("lets only the database winner apply when concurrent writes race", async () => {
    const owner = await seedUser("social-profile-race@example.test");
    const newer = "2026-08-19T20:00:00.000Z";
    const older = "2026-08-19T19:00:00.000Z";
    const write = (value: string, updatedAt: string, deviceId: string) =>
      api("/v1/profile", {
        method: "PUT",
        headers: authHeaders(owner.token),
        body: JSON.stringify({
          deviceId,
          fields: { bio: field(value, updatedAt, deviceId) },
        }),
      });
    const responses = await Promise.all([
      write("newer", newer, "pc-new"),
      write("older", older, "pc-old"),
    ]);
    const bodies = await Promise.all(
      responses.map((response) =>
        json<{ values: { bio: string }; applied: string[]; discarded: Array<{ key: string; reason: string }> }>(
          response,
        ),
      ),
    );
    expect(bodies.every((body) => body.values.bio === "newer")).toBe(true);
    const future = await api("/v1/profile", {
      method: "PUT",
      headers: authHeaders(owner.token),
      body: JSON.stringify({
        deviceId: "pc-future",
        fields: { bio: field("from-the-future", new Date(Date.now() + 24 * 60 * 60 * 1000).toISOString()) },
      }),
    });
    const futureBody = await json<{
      values: { bio: string };
      discarded: Array<{ key: string; reason: string }>;
    }>(future);
    expect(futureBody.values.bio).toBe("newer");
    expect(futureBody.discarded).toEqual([
      expect.objectContaining({ key: "bio", reason: "invalid" }),
    ]);
    const stored = await env.DB.prepare(
      `SELECT value, updated_at, device_id FROM profile_field WHERE user_id = ? AND key = 'bio'`,
    )
      .bind(owner.id)
      .first<{ value: string; updated_at: string; device_id: string }>();
    expect(JSON.parse(stored!.value)).toBe("newer");
    expect(stored).toMatchObject({ updated_at: newer, device_id: "pc-new" });
  });
});

describe("profile search", () => {
  it("uses validated opaque keyset cursors, bounds limits, and filters non-searchable profiles", async () => {
    const viewer = await seedUser("social-search-viewer@example.test");
    const alpha = await seedUser("social-search-alpha@example.test");
    const beta = await seedUser("social-search-beta@example.test");
    const hidden = await seedUser("social-search-hidden@example.test");
    await claim(alpha, "seekalpha");
    await claim(beta, "seekbeta");
    await claim(hidden, "seekhidden");
    await writeProfile(alpha, { displayName: "Alpha" });
    await writeProfile(beta, { displayName: "Beta" });
    await putPrivacy(alpha, { profileVisibility: "public", searchable: true });
    await putPrivacy(beta, { profileVisibility: "public", searchable: true });
    await putPrivacy(hidden, { profileVisibility: "public", searchable: false });

    const first = await onlineApi("/v1/profiles/search?q=seek&limit=1", {
      headers: authHeaders(viewer.token),
    });
    expect(first.status).toBe(200);
    const pageOne = await json<{
      profiles: Array<{ handle: { normalized: string } }>;
      nextCursor: string | null;
    }>(first);
    expect(pageOne.profiles.map((row) => row.handle.normalized)).toEqual(["seekalpha"]);
    expect(pageOne.nextCursor).toMatch(/^[A-Za-z0-9_-]+$/);

    const second = await onlineApi(
      `/v1/profiles/search?q=seek&limit=1&cursor=${encodeURIComponent(pageOne.nextCursor!)}`,
      { headers: authHeaders(viewer.token) },
    );
    expect(second.status).toBe(200);
    const pageTwo = await json<{
      profiles: Array<{ handle: { normalized: string } }>;
      nextCursor: string | null;
    }>(second);
    expect(pageTwo.profiles.map((row) => row.handle.normalized)).toEqual(["seekbeta"]);
    expect(pageTwo.nextCursor).toBeNull();

    for (const path of [
      "/v1/profiles/search?q=seek&limit=0",
      "/v1/profiles/search?q=seek&limit=51",
      "/v1/profiles/search?q=seek&cursor=not-a-valid-cursor",
      `/v1/profiles/search?q=other&cursor=${encodeURIComponent(pageOne.nextCursor!)}`,
    ]) {
      const invalid = await onlineApi(path, { headers: authHeaders(viewer.token) });
      expect(invalid.status).toBe(400);
      expect((await json<ErrorResponse>(invalid)).error.code).toBe("INVALID_REQUEST");
    }

    const stranger = await seedUser("social-search-stranger@example.test");
    const stolen = await onlineApi(
      `/v1/profiles/search?q=seek&limit=1&cursor=${encodeURIComponent(pageOne.nextCursor!)}`,
      { headers: authHeaders(stranger.token) },
    );
    expect(stolen.status).toBe(400);
    expect((await json<ErrorResponse>(stolen)).error.code).toBe("INVALID_REQUEST");
  });

  it("keeps search privacy-aware across friends, blocks, and suppression", async () => {
    const viewer = await seedUser("social-search-policy-viewer@example.test");
    const friendOwner = await seedUser("social-search-policy-friend@example.test");
    const publicOwner = await seedUser("social-search-policy-public@example.test");
    const blockedOwner = await seedUser("social-search-policy-blocked@example.test");
    await claim(viewer, "policyviewer");
    await claim(friendOwner, "policyfriend");
    await claim(publicOwner, "policypublic");
    await claim(blockedOwner, "policyblocked");
    await putPrivacy(friendOwner, { profileVisibility: "friends", searchable: true });
    await putPrivacy(publicOwner, { profileVisibility: "public", searchable: true });
    await putPrivacy(blockedOwner, { profileVisibility: "public", searchable: true });

    const anonymous = await json<{ profiles: Array<{ handle: { normalized: string } }> }>(
      await onlineApi("/v1/profiles/search?q=policy"),
    );
    expect(anonymous.profiles.map((row) => row.handle.normalized).sort()).toEqual([
      "policyblocked",
      "policypublic",
    ]);

    const sent = await json<{ request: { id: string } }>(await requestFriend(viewer, "policyfriend"));
    expect((await onlineApi(`/v1/friends/requests/${sent.request.id}/accept`, {
      method: "POST",
      headers: authHeaders(friendOwner.token),
    })).status).toBe(200);
    await onlineApi(`/v1/blocks/${viewer.id}`, {
      method: "PUT",
      headers: authHeaders(blockedOwner.token),
    });

    const signedIn = await json<{ profiles: Array<{ handle: { normalized: string } }> }>(
      await onlineApi("/v1/profiles/search?q=policy", { headers: authHeaders(viewer.token) }),
    );
    expect(signedIn.profiles.map((row) => row.handle.normalized).sort()).toEqual([
      "policyfriend",
      "policypublic",
    ]);

    await onlineApi(`/v1/friends/${friendOwner.id}`, {
      method: "DELETE",
      headers: authHeaders(viewer.token),
    });
    const afterRemove = await json<{ profiles: Array<{ handle: { normalized: string } }> }>(
      await onlineApi("/v1/profiles/search?q=policy", { headers: authHeaders(viewer.token) }),
    );
    expect(afterRemove.profiles.map((row) => row.handle.normalized)).toEqual(["policypublic"]);
  });
});

describe("direct friend requests", () => {
  it("deduplicates same-direction requests and accepts a reverse request into one canonical friendship", async () => {
    const a = await seedUser("social-reverse-a@example.test");
    const b = await seedUser("social-reverse-b@example.test");
    await claim(a, "reversealice");
    await claim(b, "reversebob");

    const first = await requestFriend(a, "reversebob");
    expect(first.status).toBe(200);
    const firstBody = await json<{ request: { id: string; status: string } }>(first);
    expect(firstBody.request.status).toBe("pending");

    const duplicate = await json<{ request: { id: string; status: string } }>(
      await requestFriend(a, "reversebob"),
    );
    expect(duplicate.request).toEqual(firstBody.request);

    const asSender = await json<{ incoming: unknown[]; outgoing: Array<{ id: string }> }>(
      await onlineApi("/v1/friends/requests", { headers: authHeaders(a.token) }),
    );
    const asRecipient = await json<{ incoming: Array<{ id: string }>; outgoing: unknown[] }>(
      await onlineApi("/v1/friends/requests", { headers: authHeaders(b.token) }),
    );
    expect(asSender.incoming).toEqual([]);
    expect(asSender.outgoing.map((row) => row.id)).toEqual([firstBody.request.id]);
    expect(asRecipient.incoming.map((row) => row.id)).toEqual([firstBody.request.id]);
    expect(asRecipient.outgoing).toEqual([]);

    const reverse = await requestFriend(b, "reversealice");
    expect(reverse.status).toBe(200);
    expect((await json<{ request: { status: string } }>(reverse)).request.status).toBe("accepted");
    expect(await areConnectedFriends(env, a.id, b.id)).toBe(true);

    const count = await env.DB.prepare(
      `SELECT COUNT(*) AS n FROM direct_friendship
       WHERE user_low = ? AND user_high = ?`,
    )
      .bind(...([a.id, b.id].sort()))
      .first<{ n: number }>();
    expect(count?.n).toBe(1);
  });

  it("makes accept and decline repeat-safe and hides requests from unrelated users", async () => {
    const sender = await seedUser("social-idor-sender@example.test");
    const receiver = await seedUser("social-idor-receiver@example.test");
    const outsider = await seedUser("social-idor-outsider@example.test");
    const declinedTarget = await seedUser("social-decline-target@example.test");
    await claim(sender, "idorsender");
    await claim(receiver, "idorreceiver");
    await claim(declinedTarget, "declinetarget");

    const sent = await json<{ request: { id: string } }>(await requestFriend(sender, "idorreceiver"));
    for (const action of ["accept", "decline"] as const) {
      const idor = await onlineApi(`/v1/friends/requests/${sent.request.id}/${action}`, {
        method: "POST",
        headers: authHeaders(outsider.token),
      });
      expect(idor.status).toBe(404);
      expect((await json<ErrorResponse>(idor)).error.code).toBe("NOT_FOUND");
    }

    for (let i = 0; i < 2; i++) {
      const accepted = await onlineApi(`/v1/friends/requests/${sent.request.id}/accept`, {
        method: "POST",
        headers: authHeaders(receiver.token),
      });
      expect(accepted.status).toBe(200);
      expect((await json<{ request: { status: string } }>(accepted)).request.status).toBe("accepted");
    }

    const declineRequest = await json<{ request: { id: string } }>(await requestFriend(sender, "declinetarget"));
    for (let i = 0; i < 2; i++) {
      const declined = await onlineApi(`/v1/friends/requests/${declineRequest.request.id}/decline`, {
        method: "POST",
        headers: authHeaders(declinedTarget.token),
      });
      expect(declined.status).toBe(200);
      expect((await json<{ request: { status: string } }>(declined)).request.status).toBe("declined");
    }
  });

  it("omits private-profile media metadata from the friends roster", async () => {
    const owner = await seedUser("social-media-owner@example.test");
    const peer = await seedUser("social-media-peer@example.test");
    await claim(owner, "mediaowner");
    await claim(peer, "mediapeer");
    const sent = await json<{ request: { id: string } }>(await requestFriend(owner, "mediapeer"));
    expect((await onlineApi(`/v1/friends/requests/${sent.request.id}/accept`, {
      method: "POST",
      headers: authHeaders(peer.token),
    })).status).toBe(200);

    const version = "a".repeat(64);
    const now = new Date().toISOString();
    await env.DB.batch([
      env.DB.prepare(
        `INSERT INTO profile_media
          (user_id, kind, version, object_key, content_type, byte_size, width, height, sha256, created_at, updated_at)
         VALUES (?, 'avatar', ?, ?, 'image/png', 12, 256, 256, ?, ?, ?)`,
      ).bind(peer.id, version, `users/${peer.id}/avatar/${version}.png`, "b".repeat(64), now, now),
      env.DB.prepare(
        `INSERT INTO profile_privacy
          (user_id, profile_visibility, searchable, request_policy, activity_visibility, updated_at)
         VALUES (?, 'private', 0, 'anyone', 'friends', ?)`,
      ).bind(peer.id, now),
    ]);

    const privateRoster = await json<{ friends: Array<{ userId: string; avatar: unknown }> }>(
      await onlineApi("/v1/friends", { headers: authHeaders(owner.token) }),
    );
    expect(privateRoster.friends).toEqual([
      expect.objectContaining({ userId: peer.id, avatar: null }),
    ]);
    expect(JSON.stringify(privateRoster)).not.toContain(version);
    expect(JSON.stringify(privateRoster)).not.toContain("image/png");

    expect((await putPrivacy(peer, { profileVisibility: "friends" })).status).toBe(200);
    const visible = await json<{ friends: Array<{ userId: string; avatar: { version: string } | null }> }>(
      await onlineApi("/v1/friends", { headers: authHeaders(owner.token) }),
    );
    expect(visible.friends[0]?.avatar?.version).toBe(version);

    expect((await onlineApi(`/v1/blocks/${owner.id}`, {
      method: "PUT",
      headers: authHeaders(peer.token),
    })).status).toBe(200);
    const blocked = await json<{ friends: Array<{ userId: string }> }>(
      await onlineApi("/v1/friends", { headers: authHeaders(owner.token) }),
    );
    expect(blocked.friends).toEqual([]);
  });

  it("forbids self requests and honors a target's request policy without revealing more than not-found", async () => {
    const owner = await seedUser("social-policy-owner@example.test");
    const caller = await seedUser("social-policy-caller@example.test");
    await claim(owner, "closedrequests");
    await putPrivacy(owner, { requestPolicy: "none" });

    const self = await requestFriend(owner, "closedrequests");
    expect(self.status).toBe(400);
    expect((await json<ErrorResponse>(self)).error.code).toBe("INVALID_REQUEST");

    const closed = await requestFriend(caller, "closedrequests");
    expect(closed.status).toBe(404);
    expect((await json<ErrorResponse>(closed)).error.code).toBe("NOT_FOUND");

    const extra = await onlineApi("/v1/friends/requests", {
      method: "POST",
      headers: authHeaders(caller.token),
      body: JSON.stringify({ handle: "closedrequests", ignored: true }),
    });
    expect(extra.status).toBe(400);
    expect((await json<ErrorResponse>(extra)).error.code).toBe("INVALID_REQUEST");
  });
});

describe("friends roster and blocks", () => {
  it("merges direct and multi-store connections into one bounded roster row with deduplicated sources", async () => {
    const owner = await seedUser("social-roster-owner@example.test");
    const peer = await seedUser("social-roster-peer@example.test");
    const second = await seedUser("social-roster-second@example.test");
    await claim(peer, "rosterpeer");
    await claim(second, "rostersecond");
    const [low, high] = [owner.id, peer.id].sort();
    const [lowSecond, highSecond] = [owner.id, second.id].sort();
    const now = new Date().toISOString();
    await env.DB.batch([
      env.DB.prepare(
        `INSERT INTO direct_friendship (user_low, user_high, created_at) VALUES (?, ?, ?)`,
      ).bind(low, high, now),
      env.DB.prepare(
        `INSERT INTO discovered_connection (user_low, user_high, store, created_at) VALUES (?, ?, 'steam', ?)`,
      ).bind(low, high, now),
      env.DB.prepare(
        `INSERT INTO discovered_connection (user_low, user_high, store, created_at) VALUES (?, ?, 'epic', ?)`,
      ).bind(low, high, now),
      env.DB.prepare(
        `INSERT INTO direct_friendship (user_low, user_high, created_at) VALUES (?, ?, ?)`,
      ).bind(lowSecond, highSecond, now),
    ]);

    const first = await onlineApi("/v1/friends?limit=1", { headers: authHeaders(owner.token) });
    expect(first.status).toBe(200);
    const pageOne = await json<{
      friends: Array<{ userId: string; sources: string[] }>;
      nextCursor: string | null;
    }>(first);
    expect(pageOne.friends).toHaveLength(1);
    expect(pageOne.nextCursor).toBeTruthy();

    const secondPage = await json<{
      friends: Array<{ userId: string; sources: string[] }>;
      nextCursor: string | null;
    }>(
      await onlineApi(`/v1/friends?limit=1&cursor=${encodeURIComponent(pageOne.nextCursor!)}`, {
        headers: authHeaders(owner.token),
      }),
    );
    const rows = [...pageOne.friends, ...secondPage.friends];
    expect(rows.map((row) => row.userId).sort()).toEqual([peer.id, second.id].sort());
    expect(rows.find((row) => row.userId === peer.id)?.sources).toEqual(["direct", "epic", "steam"]);
  });

  it("block is idempotent, removes every relationship source, prevents store matching, and can be undone", async () => {
    const a = await seedUser("social-block-a@example.test");
    const b = await seedUser("social-block-b@example.test");
    await claim(a, "blockalice");
    await claim(b, "blockbob");
    const [low, high] = [a.id, b.id].sort();
    const now = new Date().toISOString();
    await env.DB.batch([
      env.DB.prepare(
        `INSERT INTO direct_friendship (user_low, user_high, created_at) VALUES (?, ?, ?)`,
      ).bind(low, high, now),
      env.DB.prepare(
        `INSERT INTO discovered_connection (user_low, user_high, store, created_at) VALUES (?, ?, 'steam', ?)`,
      ).bind(low, high, now),
      env.DB.prepare(
        `INSERT INTO match_claim (user_id, store, peer_user_id, created_at) VALUES (?, 'steam', ?, ?)`,
      ).bind(a.id, b.id, now),
    ]);

    let createdAt = "";
    for (let i = 0; i < 2; i++) {
      const response = await onlineApi(`/v1/blocks/${b.id}`, {
        method: "PUT",
        headers: authHeaders(a.token),
      });
      expect(response.status).toBe(200);
      const body = await json<{ block: { userId: string; createdAt: string } }>(response);
      expect(body.block.userId).toBe(b.id);
      if (createdAt) expect(body.block.createdAt).toBe(createdAt);
      createdAt = body.block.createdAt;
    }

    for (const table of ["direct_friendship", "discovered_connection"] as const) {
      const row = await env.DB.prepare(
        `SELECT 1 AS ok FROM ${table} WHERE (user_low = ? AND user_high = ?)`,
      )
        .bind(low, high)
        .first();
      expect(row).toBeNull();
    }
    const pendingClaim = await env.DB.prepare(
      `SELECT 1 AS ok FROM match_claim
       WHERE (user_id = ? AND peer_user_id = ?) OR (user_id = ? AND peer_user_id = ?)`,
    )
      .bind(a.id, b.id, b.id, a.id)
      .first();
    expect(pendingClaim).toBeNull();
    expect(await areConnectedFriends(env, a.id, b.id)).toBe(false);
    expect(await canViewProfile(env, b.id, a.id)).toBe(false);

    const listed = await json<{ blocks: Array<{ userId: string }> }>(
      await onlineApi("/v1/blocks", { headers: authHeaders(a.token) }),
    );
    expect(listed.blocks).toEqual([expect.objectContaining({ userId: b.id })]);

    for (let i = 0; i < 2; i++) {
      const unblocked = await onlineApi(`/v1/blocks/${b.id}`, {
        method: "DELETE",
        headers: authHeaders(a.token),
      });
      expect(unblocked.status).toBe(200);
      expect(await json(unblocked)).toEqual({ ok: true });
    }
  });

  it("suppresses a removed store connection until a later direct request is accepted", async () => {
    const a = await seedUser("social-remove-a@example.test");
    const b = await seedUser("social-remove-b@example.test");
    await claim(a, "removealice");
    await claim(b, "removebob");
    const epicA = "a1".repeat(16);
    const epicB = "b2".repeat(16);
    for (const [user, externalId] of [
      [a, epicA],
      [b, epicB],
    ] as const) {
      const linked = await api("/v1/links/epic", {
        method: "POST",
        headers: authHeaders(user.token),
        body: JSON.stringify({ accessToken: `test:${externalId}` }),
      });
      expect(linked.status).toBe(200);
    }
    const match = (user: TestUser, id: string) =>
      api("/v1/links/match", {
        method: "POST",
        headers: authHeaders(user.token),
        body: JSON.stringify({ store: "epic", relationship: "mutual", ids: [id] }),
      });
    await match(a, epicB);
    await match(b, epicA);
    expect(await areConnectedFriends(env, a.id, b.id)).toBe(true);

    const removed = await onlineApi(`/v1/friends/${b.id}`, {
      method: "DELETE",
      headers: authHeaders(a.token),
    });
    expect(removed.status).toBe(200);
    await match(a, epicB);
    await match(b, epicA);
    expect(await areConnectedFriends(env, a.id, b.id)).toBe(false);
    const roster = await json<{ friends: Array<{ userId: string }> }>(
      await onlineApi("/v1/friends", { headers: authHeaders(a.token) }),
    );
    expect(roster.friends).toEqual([]);

    const request = await json<{ request: { id: string } }>(await requestFriend(a, "removebob"));
    const accepted = await onlineApi(`/v1/friends/requests/${request.request.id}/accept`, {
      method: "POST",
      headers: authHeaders(b.token),
    });
    expect(accepted.status).toBe(200);
    expect(await areConnectedFriends(env, a.id, b.id)).toBe(true);
    const suppressed = await env.DB.prepare(
      `SELECT 1 AS found FROM friend_suppression WHERE user_low = ? AND user_high = ?`,
    )
      .bind(...([a.id, b.id].sort()))
      .first();
    expect(suppressed).toBeNull();
  });
});
