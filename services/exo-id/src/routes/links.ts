import { Hono } from "hono";
import type { Context } from "hono";
import type { Env } from "../env.ts";
import {
  isTestEnv,
  MATCH_MAX_IDS,
  MATCH_RATE_MAX,
  MATCH_RATE_WINDOW_MS,
  STORE_LINK_PENDING_TTL_SEC,
} from "../env.ts";
import { ApiError, ErrorCode } from "../errors.ts";
import { nowIso, randomHex } from "../crypto.ts";
import { parseLoopbackRedirect, loopbackCallbackUrl } from "../loopback.ts";
import { assertRateLimit, clientIp, scopedRateKey } from "../rate-limit.ts";
import { requireSession } from "../session.ts";
import { storeLinkErrorPage } from "../html.ts";
import {
  canonicalizeStoreId,
  parseRelationship,
  parseStore,
  type Store,
} from "../stores.ts";
import { buildSteamOpenIdUrl, verifySteamAssertion } from "../steam-openid.ts";
import { verifyStoreAccessToken } from "../store-verify.ts";
import {
  listConnections,
  listLinks,
  matchMutualFriends,
  saveVerifiedLink,
  setDiscovery,
  unlinkStore,
  verifiedLink,
} from "../links.ts";
import { readExactJsonObject } from "../bounded-json.ts";

const MAX_LINK_JSON_BYTES = 32 * 1024;
const INVALID_LINK_REQUEST = "JSON object required.";

export const linksRoutes = new Hono<{ Bindings: Env }>();

function originOf(env: Env): string {
  return env.BETTER_AUTH_URL.replace(/\/+$/, "");
}

function publicLink(row: { store: string; external_id: string; verified: number; verified_at: string }) {
  return {
    store: row.store,
    externalId: row.external_id,
    verified: row.verified === 1,
    verifiedAt: row.verified_at,
  };
}

async function discoveryPayload(db: D1Database, userId: string) {
  const row = await db
    .prepare(`SELECT enabled, updated_at FROM user_discovery WHERE user_id = ?`)
    .bind(userId)
    .first<{ enabled: number; updated_at: string }>();
  return {
    enabled: row ? row.enabled === 1 : true,
    updatedAt: row?.updated_at ?? null,
  };
}

async function linksPayload(db: D1Database, secret: string, userId: string) {
  const [discovery, links, connections] = await Promise.all([
    discoveryPayload(db, userId),
    listLinks(db, secret, userId),
    listConnections(db, userId),
  ]);
  return {
    discovery,
    links: links.map(publicLink),
    connections,
  };
}

async function verifyTokenForStore(env: Env, store: Exclude<Store, "steam">, accessToken: string): Promise<string> {
  if (isTestEnv(env) && accessToken.startsWith("test:")) {
    const id = canonicalizeStoreId(store, accessToken.slice(5));
    if (!id) throw new ApiError(400, ErrorCode.LINK_INVALID, "That store id is not valid.");
    return id;
  }
  return verifyStoreAccessToken(store, accessToken);
}

linksRoutes.get("/v1/links", async (c) => {
  const session = await requireSession(c);
  return c.json(await linksPayload(c.env.DB, c.env.BETTER_AUTH_SECRET, session.userId));
});

linksRoutes.patch("/v1/links/discovery", async (c) => {
  const session = await requireSession(c);
  await assertRateLimit(
    c.env.DB,
    await scopedRateKey(c.env.BETTER_AUTH_SECRET, "discovery", session.userId),
    { windowMs: MATCH_RATE_WINDOW_MS, max: 10 },
  );
  const body = await readExactJsonObject(
    c.req.raw,
    MAX_LINK_JSON_BYTES,
    ["enabled"],
    [],
    INVALID_LINK_REQUEST,
  );
  if (typeof body.enabled !== "boolean") {
    throw new ApiError(400, ErrorCode.INVALID_REQUEST, "enabled must be a boolean.");
  }
  await setDiscovery(c.env.DB, session.userId, body.enabled);
  return c.json({ discovery: await discoveryPayload(c.env.DB, session.userId) });
});

linksRoutes.delete("/v1/links/:store", async (c) => {
  const session = await requireSession(c);
  const store = parseStore(c.req.param("store"));
  const removed = await unlinkStore(c.env.DB, session.userId, store);
  if (!removed) throw new ApiError(404, ErrorCode.NOT_FOUND, "That store is not linked.");
  return c.json({ ok: true });
});

linksRoutes.post("/v1/links/steam/start", async (c) => {
  const session = await requireSession(c);
  await assertRateLimit(
    c.env.DB,
    await scopedRateKey(c.env.BETTER_AUTH_SECRET, "steam-start", session.userId),
    { windowMs: MATCH_RATE_WINDOW_MS, max: 5 },
  );
  if (!isTestEnv(c.env)) {
    await assertRateLimit(
      c.env.DB,
      await scopedRateKey(c.env.BETTER_AUTH_SECRET, "steam-start-ip", clientIp(c.req.raw.headers)),
      { windowMs: MATCH_RATE_WINDOW_MS, max: 10 },
    );
  }
  const body = await readExactJsonObject(
    c.req.raw,
    MAX_LINK_JSON_BYTES,
    ["redirectUri"],
    ["state"],
    INVALID_LINK_REQUEST,
  );
  const redirect = parseLoopbackRedirect(body.redirectUri);
  const state =
    typeof body.state === "string" && body.state.length > 0 && body.state.length <= 128
      ? body.state
      : randomHex(16);
  const linkId = randomHex(24);
  const origin = originOf(c.env);
  const returnTo = `${origin}/v1/links/steam/callback?link=${linkId}`;
  const created = nowIso();
  const expires = new Date(Date.now() + STORE_LINK_PENDING_TTL_SEC * 1000).toISOString();
  await c.env.DB.prepare(
    `INSERT INTO pending_store_link
      (id, user_id, store, redirect_uri, client_state, return_to, expires_at, created_at)
     VALUES (?, ?, 'steam', ?, ?, ?, ?, ?)`,
  )
    .bind(linkId, session.userId, redirect.href, state, returnTo, expires, created)
    .run();
  return c.json({
    linkId,
    expiresIn: STORE_LINK_PENDING_TTL_SEC,
    authorizationUrl: buildSteamOpenIdUrl(origin, returnTo),
  });
});

linksRoutes.get("/v1/links/steam/callback", async (c) => {
  if (!isTestEnv(c.env)) {
    await assertRateLimit(
      c.env.DB,
      await scopedRateKey(c.env.BETTER_AUTH_SECRET, "steam-callback-ip", clientIp(c.req.raw.headers)),
      { windowMs: MATCH_RATE_WINDOW_MS, max: 20 },
    );
  }
  const linkId = c.req.query("link") ?? "";
  if (!/^[a-f0-9]{48}$/.test(linkId)) {
    return c.html(storeLinkErrorPage("This Steam link expired. Return to Exo and try again."), 410);
  }
  const claimedAt = nowIso();
  const consume = await c.env.DB.prepare(
    `UPDATE pending_store_link
     SET consumed_at = ?
     WHERE id = ? AND store = 'steam' AND consumed_at IS NULL AND expires_at >= ?`,
  )
    .bind(claimedAt, linkId, claimedAt)
    .run();
  if ((consume.meta.changes ?? 0) !== 1) {
    return c.html(storeLinkErrorPage("This Steam link expired. Return to Exo and try again."), 410);
  }
  const claimed = await c.env.DB.prepare(
    `SELECT id, user_id, redirect_uri, client_state, return_to
     FROM pending_store_link WHERE id = ? AND store = 'steam'`,
  )
    .bind(linkId)
    .first<{
      id: string;
      user_id: string;
      redirect_uri: string;
      client_state: string;
      return_to: string;
    }>();
  if (!claimed) {
    return c.html(storeLinkErrorPage("This Steam link expired. Return to Exo and try again."), 410);
  }
  try {
    const requestUrl = new URL(c.req.url);
    if (!claimed.return_to.endsWith(`link=${claimed.id}`) || !requestUrl.searchParams.get("link")) {
      throw new ApiError(400, ErrorCode.LINK_INVALID, "Steam did not return a valid assertion.");
    }
    const steamId = await verifySteamAssertion(requestUrl, claimed.return_to, c.env);
    await saveVerifiedLink(c.env.DB, c.env.BETTER_AUTH_SECRET, claimed.user_id, "steam", steamId);
    const target = loopbackCallbackUrl(claimed.redirect_uri, { state: claimed.client_state, link: "ok" });
    return c.redirect(target, 302);
  } catch (err) {
    if (err instanceof ApiError) {
      const target = loopbackCallbackUrl(claimed.redirect_uri, {
        state: claimed.client_state,
        error: err.code,
      });
      return c.redirect(target, 302);
    }
    return c.html(storeLinkErrorPage("Steam could not be linked."), 400);
  }
});

async function handleTokenLink(c: Context<{ Bindings: Env }>, store: Exclude<Store, "steam">) {
  const session = await requireSession(c);
  await assertRateLimit(
    c.env.DB,
    await scopedRateKey(c.env.BETTER_AUTH_SECRET, `link-${store}`, session.userId),
    { windowMs: MATCH_RATE_WINDOW_MS, max: 10 },
  );
  const body = await readExactJsonObject(
    c.req.raw,
    MAX_LINK_JSON_BYTES,
    ["accessToken"],
    [],
    INVALID_LINK_REQUEST,
  );
  if (typeof body.accessToken !== "string" || body.accessToken.length < 8 || body.accessToken.length > 8192) {
    throw new ApiError(400, ErrorCode.LINK_INVALID, "accessToken is required.");
  }
  const externalId = await verifyTokenForStore(c.env, store, body.accessToken);
  const row = await saveVerifiedLink(c.env.DB, c.env.BETTER_AUTH_SECRET, session.userId, store, externalId);
  return { link: publicLink(row) };
}

linksRoutes.post("/v1/links/epic", async (c) => c.json(await handleTokenLink(c, "epic")));
linksRoutes.post("/v1/links/gog", async (c) => c.json(await handleTokenLink(c, "gog")));

linksRoutes.post("/v1/links/match", async (c) => {
  const session = await requireSession(c);
  await assertRateLimit(
    c.env.DB,
    await scopedRateKey(c.env.BETTER_AUTH_SECRET, "match", session.userId),
    { windowMs: MATCH_RATE_WINDOW_MS, max: MATCH_RATE_MAX },
  );
  if (!isTestEnv(c.env)) {
    await assertRateLimit(
      c.env.DB,
      await scopedRateKey(c.env.BETTER_AUTH_SECRET, "match-ip", clientIp(c.req.raw.headers)),
      { windowMs: MATCH_RATE_WINDOW_MS, max: 20 },
    );
  }
  const body = await readExactJsonObject(
    c.req.raw,
    MAX_LINK_JSON_BYTES,
    ["store", "relationship", "ids"],
    [],
    INVALID_LINK_REQUEST,
  );
  const store = parseStore(body.store);
  const relationship = parseRelationship(body.relationship);
  if (!Array.isArray(body.ids)) {
    throw new ApiError(400, ErrorCode.INVALID_REQUEST, "ids must be an array.");
  }
  if (body.ids.length > MATCH_MAX_IDS) {
    throw new ApiError(400, ErrorCode.MATCH_TOO_LARGE, "Too many ids in one request.");
  }
  if (!(await verifiedLink(c.env.DB, c.env.BETTER_AUTH_SECRET, session.userId, store))) {
    throw new ApiError(403, ErrorCode.LINK_UNVERIFIED, "Verify that store before matching friends.");
  }
  if (relationship !== "mutual") {
    return c.json({ matches: [] });
  }
  const matches = await matchMutualFriends(
    c.env.DB,
    c.env.BETTER_AUTH_SECRET,
    session.userId,
    store,
    body.ids.map((id) => (typeof id === "string" ? id : "")),
  );
  return c.json({ matches });
});
