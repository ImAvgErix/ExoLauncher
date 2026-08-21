import { describe, expect, it } from "vitest";
import { env } from "cloudflare:test";
import { api, authHeaders, seedUser } from "./helpers.ts";
import { MATCH_MAX_IDS, MATCH_RATE_MAX } from "../../src/env.ts";
import { encryptStoreExternalId } from "../../src/store-link-crypto.ts";
import { hashStoreId } from "../../src/stores.ts";

function steamId(n: number): string {
  return `7656119${String(n).padStart(10, "0")}`;
}

function epicId(ch: string): string {
  return ch.repeat(32);
}

function epicNumber(n: number): string {
  return n.toString(16).padStart(32, "0");
}

type Err = { error: { code: string } };
type MatchBody = { matches: Array<{ userId: string; store: string }> };
type LinksBody = {
  discovery: { enabled: boolean };
  links: Array<{ store: string; externalId: string; verified: boolean }>;
  connections: Array<{ userId: string; store: string }>;
};

async function json<T>(res: Response): Promise<T> {
  return res.json() as Promise<T>;
}

async function steamCallback(token: string, steamId: string): Promise<Response> {
  const start = await api("/v1/links/steam/start", {
    method: "POST",
    headers: authHeaders(token),
    body: JSON.stringify({
      redirectUri: "http://127.0.0.1:55123/callback",
      state: "desktop-state",
    }),
  });
  expect(start.status).toBe(200);
  const body = await json<{ linkId: string }>(start);
  const returnTo = `http://127.0.0.1:8787/v1/links/steam/callback?link=${body.linkId}`;
  const params = new URLSearchParams({
    link: body.linkId,
    "openid.ns": "http://specs.openid.net/auth/2.0",
    "openid.mode": "id_res",
    "openid.op_endpoint": "https://steamcommunity.com/openid/login",
    "openid.claimed_id": `https://steamcommunity.com/openid/id/${steamId}`,
    "openid.identity": `https://steamcommunity.com/openid/id/${steamId}`,
    "openid.return_to": returnTo,
    "openid.response_nonce": "2026-08-18T00:00:00Znonce",
    "openid.assoc_handle": "1234567890",
    "openid.signed": "signed,op_endpoint,claimed_id,identity,return_to,response_nonce,assoc_handle",
    "openid.sig": "test-valid",
  });
  return api(`/v1/links/steam/callback?${params.toString()}`, { redirect: "manual" });
}

async function linkSteam(token: string, steamId: string): Promise<void> {
  const callback = await steamCallback(token, steamId);
  expect(callback.status).toBe(302);
  expect(callback.headers.get("location")).toContain("link=ok");
}

async function linkToken(token: string, store: "epic" | "gog", externalId: string): Promise<Response> {
  return api(`/v1/links/${store}`, {
    method: "POST",
    headers: authHeaders(token),
    body: JSON.stringify({ accessToken: `test:${externalId}` }),
  });
}

async function match(token: string, store: string, ids: string[], relationship = "mutual") {
  return api("/v1/links/match", {
    method: "POST",
    headers: authHeaders(token),
    body: JSON.stringify({ store, ids, relationship }),
  });
}

describe("store link verification", () => {
  it("migrates both per-user/provider and global provider-account uniqueness", async () => {
    const columns = await env.DB.prepare(`PRAGMA table_info('store_link')`).all<{
      name: string;
      pk: number;
    }>();
    expect(
      (columns.results ?? [])
        .filter((column) => column.pk > 0)
        .sort((a, b) => a.pk - b.pk)
        .map((column) => column.name),
    ).toEqual(["user_id", "store"]);

    const indexes = await env.DB.prepare(`PRAGMA index_list('store_link')`).all<{
      name: string;
      unique: number;
    }>();
    expect(indexes.results).toContainEqual(
      expect.objectContaining({ name: "store_link_hash_uidx", unique: 1 }),
    );
    const fingerprintColumns = await env.DB.prepare(
      `PRAGMA index_info('store_link_hash_uidx')`,
    ).all<{ name: string; seqno: number }>();
    expect(
      (fingerprintColumns.results ?? [])
        .sort((a, b) => a.seqno - b.seqno)
        .map((column) => column.name),
    ).toEqual(["store", "id_hash"]);
  });

  it("links Steam through the OpenID callback and Epic/GOG through a verified token", async () => {
    const user = await seedUser("link-steam@example.test");
    await linkSteam(user.token, steamId(1));
    const listed = await json<LinksBody>(await api("/v1/links", { headers: authHeaders(user.token) }));
    expect(listed.discovery.enabled).toBe(true);
    expect(listed.links).toEqual([
      expect.objectContaining({ store: "steam", externalId: steamId(1), verified: true }),
    ]);
    const storedSteam = await env.DB.prepare(
      `SELECT external_id, id_hash FROM store_link WHERE user_id = ? AND store = 'steam'`,
    )
      .bind(user.id)
      .first<{ external_id: string; id_hash: string }>();
    expect(storedSteam?.external_id).toMatch(/^v1\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+$/);
    expect(storedSteam?.external_id).not.toContain(steamId(1));
    expect(storedSteam?.id_hash).not.toBe(steamId(1));

    const epicUser = await seedUser("link-epic@example.test");
    const epic = await api("/v1/links/epic", {
      method: "POST",
      headers: authHeaders(epicUser.token),
      body: JSON.stringify({ accessToken: `test:${epicId("a")}` }),
    });
    expect(epic.status).toBe(200);
    expect((await json<{ link: { store: string; verified: boolean } }>(epic)).link.verified).toBe(true);

    const gogUser = await seedUser("link-gog@example.test");
    const gog = await api("/v1/links/gog", {
      method: "POST",
      headers: authHeaders(gogUser.token),
      body: JSON.stringify({ accessToken: "test:48628349957132247" }),
    });
    expect(gog.status).toBe(200);
  });

  it("allows only one Exo owner for a verified Steam, Epic, or GOG account without revealing the owner", async () => {
    const steamOwnerEmail = "steam-owner@example.test";
    const steamOwner = await seedUser(steamOwnerEmail);
    const steamOther = await seedUser("steam-other@example.test");
    const claimedSteamId = steamId(61);
    await linkSteam(steamOwner.token, claimedSteamId);
    const steamConflict = await steamCallback(steamOther.token, claimedSteamId);
    expect(steamConflict.status).toBe(302);
    const steamLocation = steamConflict.headers.get("location") ?? "";
    expect(new URL(steamLocation).searchParams.get("error")).toBe("LINK_TAKEN");
    expect(steamLocation).not.toContain(steamOwner.id);
    expect(steamLocation).not.toContain(steamOwnerEmail);
    expect(steamLocation).not.toContain(claimedSteamId);

    for (const [store, externalId] of [
      ["epic", epicId("6")],
      ["gog", "61000000000000061"],
    ] as const) {
      const ownerEmail = `${store}-owner@example.test`;
      const owner = await seedUser(ownerEmail);
      const other = await seedUser(`${store}-other@example.test`);
      expect((await linkToken(owner.token, store, externalId)).status).toBe(200);

      const conflict = await linkToken(other.token, store, externalId);
      expect(conflict.status).toBe(409);
      const conflictText = await conflict.text();
      expect(JSON.parse(conflictText)).toEqual({
        error: {
          code: "LINK_TAKEN",
          message: "That store account is already linked.",
        },
      });
      expect(conflictText).not.toContain(owner.id);
      expect(conflictText).not.toContain(ownerEmail);
      expect(conflictText).not.toContain(externalId);
    }
  });

  it("keeps same-owner verification idempotent and one account per provider", async () => {
    const owner = await seedUser("idempotent-owner@example.test");
    const releasedAccountOwner = await seedUser("idempotent-released@example.test");
    const firstId = epicId("7");
    const replacementId = epicId("8");

    expect((await linkToken(owner.token, "epic", firstId)).status).toBe(200);
    const before = await env.DB.prepare(
      `SELECT external_id, id_hash, verified_at FROM store_link WHERE user_id = ? AND store = 'epic'`,
    )
      .bind(owner.id)
      .first<{ external_id: string; id_hash: string; verified_at: string }>();

    expect((await linkToken(owner.token, "epic", firstId)).status).toBe(200);
    const afterSame = await env.DB.prepare(
      `SELECT external_id, id_hash, verified_at FROM store_link WHERE user_id = ? AND store = 'epic'`,
    )
      .bind(owner.id)
      .first<{ external_id: string; id_hash: string; verified_at: string }>();
    expect(afterSame).toEqual(before);

    expect((await linkToken(owner.token, "epic", replacementId)).status).toBe(200);
    const ownerLinks = await json<LinksBody>(
      await api("/v1/links", { headers: authHeaders(owner.token) }),
    );
    expect(ownerLinks.links).toEqual([
      expect.objectContaining({ store: "epic", externalId: replacementId, verified: true }),
    ]);
    const linkCount = await env.DB.prepare(
      `SELECT COUNT(*) AS n FROM store_link WHERE user_id = ? AND store = 'epic'`,
    )
      .bind(owner.id)
      .first<{ n: number }>();
    expect(linkCount?.n).toBe(1);
    expect((await linkToken(releasedAccountOwner.token, "epic", firstId)).status).toBe(200);
  });

  it("uses the D1 fingerprint constraint as the authority when two users link concurrently", async () => {
    const a = await seedUser("link-race-a@example.test");
    const b = await seedUser("link-race-b@example.test");
    const externalId = epicId("9");

    const [asA, asB] = await Promise.all([
      linkToken(a.token, "epic", externalId),
      linkToken(b.token, "epic", externalId),
    ]);
    expect([asA.status, asB.status].sort()).toEqual([200, 409]);

    const loser = asA.status === 409 ? a : b;
    const conflict = asA.status === 409 ? asA : asB;
    const conflictText = await conflict.text();
    expect(JSON.parse(conflictText)).toEqual({
      error: {
        code: "LINK_TAKEN",
        message: "That store account is already linked.",
      },
    });
    expect(conflictText).not.toContain(a.id);
    expect(conflictText).not.toContain(b.id);
    expect(conflictText).not.toContain(externalId);

    const idHash = await hashStoreId(env.BETTER_AUTH_SECRET, "epic", externalId);
    const owners = await env.DB.prepare(
      `SELECT user_id FROM store_link WHERE store = 'epic' AND id_hash = ?`,
    )
      .bind(idHash)
      .all<{ user_id: string }>();
    expect(owners.results).toHaveLength(1);
    const loserLinks = await json<LinksBody>(
      await api("/v1/links", { headers: authHeaders(loser.token) }),
    );
    expect(loserLinks.links).toEqual([]);
  });

  it("uses the same uniqueness authority for concurrent Steam claims", async () => {
    const a = await seedUser("steam-race-a@example.test");
    const b = await seedUser("steam-race-b@example.test");
    const claimed = steamId(91);
    const [asA, asB] = await Promise.all([
      steamCallback(a.token, claimed),
      steamCallback(b.token, claimed),
    ]);
    const statuses = [asA.status, asB.status];
    expect(statuses.every((status) => status === 302)).toBe(true);
    const locations = [asA.headers.get("location") ?? "", asB.headers.get("location") ?? ""];
    const ok = locations.filter((location) => location.includes("link=ok"));
    const taken = locations.filter((location) => location.includes("error=LINK_TAKEN"));
    expect(ok).toHaveLength(1);
    expect(taken).toHaveLength(1);
    expect(locations.join("\n")).not.toContain(a.id);
    expect(locations.join("\n")).not.toContain(b.id);
    expect(locations.join("\n")).not.toContain(claimed);

    const idHash = await hashStoreId(env.BETTER_AUTH_SECRET, "steam", claimed);
    const owners = await env.DB.prepare(
      `SELECT user_id FROM store_link WHERE store = 'steam' AND id_hash = ?`,
    )
      .bind(idHash)
      .all<{ user_id: string }>();
    expect(owners.results).toHaveLength(1);
  });

  it("rejects extra JSON fields on discovery and match", async () => {
    const user = await seedUser("link-extra@example.test");
    const extraDiscovery = await api("/v1/links/discovery", {
      method: "PATCH",
      headers: authHeaders(user.token),
      body: JSON.stringify({ enabled: false, ignored: true }),
    });
    expect(extraDiscovery.status).toBe(400);
    await linkSteam(user.token, steamId(92));
    const extraMatch = await api("/v1/links/match", {
      method: "POST",
      headers: authHeaders(user.token),
      body: JSON.stringify({ store: "steam", relationship: "mutual", ids: [], ignored: true }),
    });
    expect(extraMatch.status).toBe(400);
  });

  it("preserves the current link and discovered friends when a replacement belongs to another user", async () => {
    const a = await seedUser("safe-relink-a@example.test");
    const b = await seedUser("safe-relink-b@example.test");
    const friend = await seedUser("safe-relink-friend@example.test");
    const aId = epicNumber(0x1001);
    const bId = epicNumber(0x1002);
    const friendId = epicNumber(0x1003);
    expect((await linkToken(a.token, "epic", aId)).status).toBe(200);
    expect((await linkToken(b.token, "epic", bId)).status).toBe(200);
    expect((await linkToken(friend.token, "epic", friendId)).status).toBe(200);
    await match(a.token, "epic", [friendId]);
    await match(friend.token, "epic", [aId]);

    const conflict = await linkToken(a.token, "epic", bId);
    expect(conflict.status).toBe(409);
    expect((await json<Err>(conflict)).error.code).toBe("LINK_TAKEN");

    const listed = await json<LinksBody>(
      await api("/v1/links", { headers: authHeaders(a.token) }),
    );
    expect(listed.links).toEqual([
      expect.objectContaining({ store: "epic", externalId: aId, verified: true }),
    ]);
    expect(listed.connections).toEqual([
      expect.objectContaining({ userId: friend.id, store: "epic" }),
    ]);
  });

  it("transfers ownership only after the current owner explicitly unlinks", async () => {
    const owner = await seedUser("unlink-owner@example.test");
    const nextOwnerEmail = "unlink-next@example.test";
    const nextOwner = await seedUser(nextOwnerEmail);
    const externalId = "62000000000000062";
    expect((await linkToken(owner.token, "gog", externalId)).status).toBe(200);
    expect((await linkToken(nextOwner.token, "gog", externalId)).status).toBe(409);

    const unlinked = await api("/v1/links/gog", {
      method: "DELETE",
      headers: authHeaders(owner.token),
    });
    expect(unlinked.status).toBe(200);
    expect((await linkToken(nextOwner.token, "gog", externalId)).status).toBe(200);

    const oldOwnerConflict = await linkToken(owner.token, "gog", externalId);
    expect(oldOwnerConflict.status).toBe(409);
    const conflictText = await oldOwnerConflict.text();
    expect(JSON.parse(conflictText)).toEqual({
      error: {
        code: "LINK_TAKEN",
        message: "That store account is already linked.",
      },
    });
    expect(conflictText).not.toContain(nextOwner.id);
    expect(conflictText).not.toContain(nextOwnerEmail);
    expect(conflictText).not.toContain(externalId);
  });

  it("consumes a Steam callback once and binds it to the pending intent", async () => {
    const user = await seedUser("steam-replay@example.test");
    const start = await api("/v1/links/steam/start", {
      method: "POST",
      headers: authHeaders(user.token),
      body: JSON.stringify({
        redirectUri: "http://127.0.0.1:55123/callback",
        state: "replay-state",
      }),
    });
    const { linkId } = await json<{ linkId: string }>(start);
    const returnTo = `http://127.0.0.1:8787/v1/links/steam/callback?link=${linkId}`;
    const params = new URLSearchParams({
      link: linkId,
      "openid.ns": "http://specs.openid.net/auth/2.0",
      "openid.mode": "id_res",
      "openid.op_endpoint": "https://steamcommunity.com/openid/login",
      "openid.claimed_id": `https://steamcommunity.com/openid/id/${steamId(93)}`,
      "openid.identity": `https://steamcommunity.com/openid/id/${steamId(93)}`,
      "openid.return_to": returnTo,
      "openid.response_nonce": "2026-08-18T00:00:00Znonce",
      "openid.assoc_handle": "1234567890",
      "openid.signed": "signed,op_endpoint,claimed_id,identity,return_to,response_nonce,assoc_handle",
      "openid.sig": "test-valid",
    });
    const path = `/v1/links/steam/callback?${params.toString()}`;
    const first = await api(path, { redirect: "manual" });
    expect(first.status).toBe(302);
    expect(first.headers.get("location")).toContain("link=ok");
    expect(first.headers.get("location")).toContain("state=replay-state");
    expect(first.headers.get("location")).not.toContain(user.id);
    const replay = await api(path, { redirect: "manual" });
    expect(replay.status).toBe(410);

    const concurrentStart = await api("/v1/links/steam/start", {
      method: "POST",
      headers: authHeaders(user.token),
      body: JSON.stringify({
        redirectUri: "http://127.0.0.1:55123/callback",
        state: "concurrent-state",
      }),
    });
    const concurrent = await json<{ linkId: string }>(concurrentStart);
    const concurrentReturn = `http://127.0.0.1:8787/v1/links/steam/callback?link=${concurrent.linkId}`;
    const concurrentParams = new URLSearchParams({
      link: concurrent.linkId,
      "openid.ns": "http://specs.openid.net/auth/2.0",
      "openid.mode": "id_res",
      "openid.op_endpoint": "https://steamcommunity.com/openid/login",
      "openid.claimed_id": `https://steamcommunity.com/openid/id/${steamId(94)}`,
      "openid.identity": `https://steamcommunity.com/openid/id/${steamId(94)}`,
      "openid.return_to": concurrentReturn,
      "openid.response_nonce": "2026-08-18T00:00:01Znonce",
      "openid.assoc_handle": "1234567890",
      "openid.signed": "signed,op_endpoint,claimed_id,identity,return_to,response_nonce,assoc_handle",
      "openid.sig": "test-valid",
    });
    const concurrentPath = `/v1/links/steam/callback?${concurrentParams.toString()}`;
    const [left, right] = await Promise.all([
      api(concurrentPath, { redirect: "manual" }),
      api(concurrentPath, { redirect: "manual" }),
    ]);
    const statuses = [left.status, right.status].sort();
    expect(statuses).toEqual([302, 410]);
    const ok = [left, right].filter((response) => response.status === 302);
    expect(ok).toHaveLength(1);
    expect(ok[0]?.headers.get("location")).toContain("link=ok");
    const count = await env.DB.prepare(
      `SELECT COUNT(*) AS n FROM store_link WHERE user_id = ? AND store = 'steam'`,
    )
      .bind(user.id)
      .first<{ n: number }>();
    expect(count?.n).toBe(1);
  });

  it("does not create a Steam link from an invalid OpenID assertion", async () => {
    const user = await seedUser("bad-steam@example.test");
    const start = await api("/v1/links/steam/start", {
      method: "POST",
      headers: authHeaders(user.token),
      body: JSON.stringify({ redirectUri: "http://127.0.0.1:55123/callback", state: "st" }),
    });
    const { linkId } = await json<{ linkId: string }>(start);
    const callback = await api(
      `/v1/links/steam/callback?link=${linkId}&openid.mode=id_res&openid.claimed_id=https://steamcommunity.com/openid/id/${steamId(1)}`,
      { redirect: "manual" },
    );
    expect(callback.status).toBe(302);
    expect(callback.headers.get("location")).toContain("error=LINK_INVALID");
    const listed = await json<LinksBody>(await api("/v1/links", { headers: authHeaders(user.token) }));
    expect(listed.links).toEqual([]);
  });
});

describe("matching", () => {
  it("refuses a caller without a verified link of that kind", async () => {
    const user = await seedUser("no-link@example.test");
    const res = await match(user.token, "steam", [steamId(2)]);
    expect(res.status).toBe(403);
    expect((await json<Err>(res)).error.code).toBe("LINK_UNVERIFIED");
  });

  it("refuses matching on a different store than the one that is verified", async () => {
    const user = await seedUser("wrong-store@example.test");
    await linkSteam(user.token, steamId(3));
    const res = await match(user.token, "epic", [epicId("b")]);
    expect(res.status).toBe(403);
    expect((await json<Err>(res)).error.code).toBe("LINK_UNVERIFIED");
  });

  it("does not match an unverified store_link row", async () => {
    const a = await seedUser("unverified-a@example.test");
    const b = await seedUser("unverified-b@example.test");
    await linkSteam(a.token, steamId(4));
    const now = new Date().toISOString();
    const hash = await hashStoreId(env.BETTER_AUTH_SECRET, "steam", steamId(5));
    const encrypted = await encryptStoreExternalId(env.BETTER_AUTH_SECRET, b.id, "steam", steamId(5));
    await env.DB.prepare(
      `INSERT INTO store_link (user_id, store, external_id, id_hash, verified, verified_at)
       VALUES (?, 'steam', ?, ?, 0, ?)`,
    )
      .bind(b.id, encrypted, hash, now)
      .run();
    const first = await match(a.token, "steam", [steamId(5)]);
    expect(first.status).toBe(200);
    expect((await json<MatchBody>(first)).matches).toEqual([]);
    const asB = await match(b.token, "steam", [steamId(4)]);
    expect(asB.status).toBe(403);
    expect((await json<Err>(asB)).error.code).toBe("LINK_UNVERIFIED");
  });

  it("auto-links only after both sides present a mutual store friendship", async () => {
    const a = await seedUser("mutual-a@example.test");
    const b = await seedUser("mutual-b@example.test");
    await api("/v1/handle", {
      method: "PUT",
      headers: authHeaders(a.token),
      body: JSON.stringify({ handle: "mutuala" }),
    });
    await api("/v1/handle", {
      method: "PUT",
      headers: authHeaders(b.token),
      body: JSON.stringify({ handle: "mutualb" }),
    });
    await linkSteam(a.token, steamId(6));
    await linkSteam(b.token, steamId(7));

    const one = await match(a.token, "steam", [steamId(7)]);
    expect(one.status).toBe(200);
    expect((await json<MatchBody>(one)).matches).toEqual([]);

    const claims = await env.DB.prepare(
      `SELECT COUNT(*) AS n FROM match_claim WHERE user_id = ? AND peer_user_id = ?`,
    )
      .bind(a.id, b.id)
      .first<{ n: number }>();
    expect(claims?.n).toBe(1);

    const two = await match(b.token, "steam", [steamId(6)]);
    expect(two.status).toBe(200);
    const matched = await json<MatchBody>(two);
    expect(matched.matches).toEqual([
      expect.objectContaining({ userId: a.id, store: "steam" }),
    ]);

    const listed = await json<LinksBody>(await api("/v1/links", { headers: authHeaders(a.token) }));
    expect(listed.connections).toEqual([expect.objectContaining({ userId: b.id, store: "steam" })]);
  });

  it("does not auto-link a one-sided relationship", async () => {
    const a = await seedUser("side-a@example.test");
    const b = await seedUser("side-b@example.test");
    await linkSteam(a.token, "76561198000000011");
    await linkSteam(b.token, "76561198000000012");
    const onesided = await match(a.token, "steam", ["76561198000000012"], "onesided");
    expect(onesided.status).toBe(200);
    expect((await json<MatchBody>(onesided)).matches).toEqual([]);
    const claim = await env.DB.prepare(
      `SELECT 1 AS ok FROM match_claim WHERE user_id = ? AND peer_user_id = ?`,
    )
      .bind(a.id, b.id)
      .first();
    expect(claim).toBeNull();
    await match(b.token, "steam", ["76561198000000011"], "onesided");
    const conn = await env.DB.prepare(
      `SELECT 1 AS ok FROM discovered_connection WHERE user_low = ? OR user_high = ?`,
    )
      .bind(a.id, a.id)
      .first();
    expect(conn).toBeNull();
  });

  it("never confirms an opted-out user", async () => {
    const a = await seedUser("opt-a@example.test");
    const b = await seedUser("opt-b@example.test");
    await linkSteam(a.token, "76561198000000021");
    await linkSteam(b.token, "76561198000000022");
    const off = await api("/v1/links/discovery", {
      method: "PATCH",
      headers: authHeaders(b.token),
      body: JSON.stringify({ enabled: false }),
    });
    expect(off.status).toBe(200);
    await match(a.token, "steam", ["76561198000000022"]);
    await match(b.token, "steam", ["76561198000000021"]);
    const listedA = await json<LinksBody>(await api("/v1/links", { headers: authHeaders(a.token) }));
    const listedB = await json<LinksBody>(await api("/v1/links", { headers: authHeaders(b.token) }));
    expect(listedA.connections).toEqual([]);
    expect(listedB.connections).toEqual([]);
    expect(listedB.discovery.enabled).toBe(false);
    const claim = await env.DB.prepare(
      `SELECT 1 AS ok FROM match_claim WHERE user_id = ? OR peer_user_id = ?`,
    )
      .bind(b.id, b.id)
      .first();
    expect(claim).toBeNull();
  });

  it("does not confirm a miss, even when the id is not an Exo user", async () => {
    const user = await seedUser("miss@example.test");
    await linkSteam(user.token, "76561198000000031");
    const res = await match(user.token, "steam", [steamId(99)]);
    expect(res.status).toBe(200);
    expect((await json<MatchBody>(res)).matches).toEqual([]);
    const stored = await env.DB.prepare(`SELECT COUNT(*) AS n FROM match_claim WHERE user_id = ?`)
      .bind(user.id)
      .first<{ n: number }>();
    expect(stored?.n).toBe(0);
  });

  it("rate-limits the match endpoint", async () => {
    const user = await seedUser("rate-match@example.test");
    await linkSteam(user.token, "76561198000000041");
    let limited: Response | null = null;
    for (let i = 0; i < MATCH_RATE_MAX + 1; i++) {
      const res = await match(user.token, "steam", ["76561198000000042"]);
      if (res.status === 429) {
        limited = res;
        break;
      }
      expect(res.status).toBe(200);
    }
    expect(limited).not.toBeNull();
    expect((await json<Err>(limited!)).error.code).toBe("RATE_LIMITED");
    expect(limited!.headers.get("Retry-After")).toBeTruthy();
  });

  it("rejects an oversized id batch", async () => {
    const user = await seedUser("too-big@example.test");
    await linkSteam(user.token, "76561198000000051");
    const ids = Array.from({ length: MATCH_MAX_IDS + 1 }, () => steamId(52));
    const res = await match(user.token, "steam", ids);
    expect(res.status).toBe(400);
    expect((await json<Err>(res)).error.code).toBe("MATCH_TOO_LARGE");
  });
});

describe("export, delete, and unlink", () => {
  it("exports verified links and discovered connections, then deletes them with the account", async () => {
    const a = await seedUser("export-link-a@example.test");
    const b = await seedUser("export-link-b@example.test");
    await api("/v1/handle", {
      method: "PUT",
      headers: authHeaders(a.token),
      body: JSON.stringify({ handle: "exporta" }),
    });
    await api("/v1/handle", {
      method: "PUT",
      headers: authHeaders(b.token),
      body: JSON.stringify({ handle: "exportb" }),
    });
    const epicA = await api("/v1/links/epic", {
      method: "POST",
      headers: authHeaders(a.token),
      body: JSON.stringify({ accessToken: `test:${epicId("c")}` }),
    });
    expect(epicA.status).toBe(200);
    await api("/v1/links/epic", {
      method: "POST",
      headers: authHeaders(b.token),
      body: JSON.stringify({ accessToken: `test:${epicId("d")}` }),
    });
    await match(a.token, "epic", [epicId("d")]);
    await match(b.token, "epic", [epicId("c")]);

    const exported = await json<{
      links: Array<{ store: string; externalId: string; verified: boolean }>;
      connections: Array<{ userId: string; store: string; handle: { normalized: string } | null }>;
      discovery: { enabled: boolean };
    }>(await api("/v1/me/export", { headers: authHeaders(a.token) }));
    expect(exported.discovery.enabled).toBe(true);
    expect(exported.links).toEqual([
      expect.objectContaining({ store: "epic", externalId: epicId("c"), verified: true }),
    ]);
    expect(exported.connections).toEqual([
      expect.objectContaining({ userId: b.id, store: "epic", handle: { display: "exportb", normalized: "exportb" } }),
    ]);
    expect(JSON.stringify(exported)).not.toContain(a.token);

    const del = await api("/v1/me", { method: "DELETE", headers: authHeaders(a.token) });
    expect(del.status).toBe(200);
    const link = await env.DB.prepare(`SELECT 1 FROM store_link WHERE user_id = ?`).bind(a.id).first();
    const claim = await env.DB.prepare(`SELECT 1 FROM match_claim WHERE user_id = ? OR peer_user_id = ?`)
      .bind(a.id, a.id)
      .first();
    const conn = await env.DB.prepare(`SELECT 1 FROM discovered_connection WHERE user_low = ? OR user_high = ?`)
      .bind(a.id, a.id)
      .first();
    expect(link).toBeNull();
    expect(claim).toBeNull();
    expect(conn).toBeNull();
  });
});
