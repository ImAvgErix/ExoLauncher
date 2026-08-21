import { Hono } from "hono";
import type { Env } from "../env.ts";
import { ApiError, ErrorCode } from "../errors.ts";
import { parsePageLimit } from "../pagination.ts";
import { assertRateLimit, scopedRateKey } from "../rate-limit.ts";
import { requireSession } from "../session.ts";
import {
  acceptFriendRequest,
  blockUser,
  createFriendRequest,
  declineFriendRequest,
  listBlocks,
  listFriends,
  listPendingFriendRequests,
  removeFriend,
  resolveHandleUser,
  unblockUser,
} from "../social.ts";
import { readExactJsonObject } from "../bounded-json.ts";

const MUTATION_WINDOW_MS = 10 * 60 * 1000;
const MUTATION_MAX = 40;

export const socialRoutes = new Hono<{ Bindings: Env }>();

async function limitMutation(env: Env, userId: string): Promise<void> {
  await assertRateLimit(
    env.DB,
    await scopedRateKey(env.BETTER_AUTH_SECRET, "social-mutation", userId),
    { windowMs: MUTATION_WINDOW_MS, max: MUTATION_MAX },
  );
}

const MAX_SOCIAL_JSON_BYTES = 2048;
const INVALID_SOCIAL_REQUEST = "Invalid friend request.";

function normalizedRequestHandle(value: unknown): string {
  if (typeof value !== "string") {
    throw new ApiError(400, ErrorCode.INVALID_REQUEST, "handle is required.");
  }
  const handle = value.trim();
  if (!/^[A-Za-z0-9_]{3,24}$/.test(handle) || !/[A-Za-z]/.test(handle)) {
    throw new ApiError(400, ErrorCode.INVALID_REQUEST, "handle is not valid.");
  }
  return handle.toLowerCase();
}

function requestId(value: string): string {
  if (!/^[a-f0-9]{48}$/.test(value)) {
    throw new ApiError(404, ErrorCode.NOT_FOUND, "Friend request not found.");
  }
  return value;
}

function targetUserId(value: string): string {
  if (!value || value.length > 128) throw new ApiError(404, ErrorCode.NOT_FOUND, "User not found.");
  return value;
}

socialRoutes.get("/v1/friends", async (c) => {
  const session = await requireSession(c);
  const limit = parsePageLimit(c.req.query("limit"));
  return c.json(await listFriends(c.env.DB, session.userId, limit, c.req.query("cursor")));
});

socialRoutes.get("/v1/friends/requests", async (c) => {
  const session = await requireSession(c);
  const limit = parsePageLimit(c.req.query("limit"));
  return c.json(
    await listPendingFriendRequests(c.env.DB, session.userId, {
      limit,
      incomingCursor: c.req.query("incomingCursor"),
      outgoingCursor: c.req.query("outgoingCursor"),
    }),
  );
});

socialRoutes.post("/v1/friends/requests", async (c) => {
  const session = await requireSession(c);
  await limitMutation(c.env, session.userId);
  const body = await readExactJsonObject(
    c.req.raw,
    MAX_SOCIAL_JSON_BYTES,
    ["handle"],
    [],
    INVALID_SOCIAL_REQUEST,
  );
  const target = await resolveHandleUser(c.env.DB, normalizedRequestHandle(body.handle));
  if (!target) throw new ApiError(404, ErrorCode.NOT_FOUND, "Profile not found.");
  const request = await createFriendRequest(c.env.DB, session.userId, target.userId);
  return c.json({ request });
});

socialRoutes.post("/v1/friends/requests/:id/accept", async (c) => {
  const session = await requireSession(c);
  await limitMutation(c.env, session.userId);
  const request = await acceptFriendRequest(c.env.DB, requestId(c.req.param("id")), session.userId);
  return c.json({ request });
});

socialRoutes.post("/v1/friends/requests/:id/decline", async (c) => {
  const session = await requireSession(c);
  await limitMutation(c.env, session.userId);
  const request = await declineFriendRequest(c.env.DB, requestId(c.req.param("id")), session.userId);
  return c.json({ request });
});

socialRoutes.delete("/v1/friends/:userId", async (c) => {
  const session = await requireSession(c);
  await limitMutation(c.env, session.userId);
  await removeFriend(c.env.DB, session.userId, targetUserId(c.req.param("userId")));
  return c.json({ ok: true });
});

socialRoutes.get("/v1/blocks", async (c) => {
  const session = await requireSession(c);
  const limit = parsePageLimit(c.req.query("limit"));
  return c.json(await listBlocks(c.env.DB, session.userId, limit, c.req.query("cursor")));
});

socialRoutes.put("/v1/blocks/:userId", async (c) => {
  const session = await requireSession(c);
  await limitMutation(c.env, session.userId);
  const block = await blockUser(c.env.DB, session.userId, targetUserId(c.req.param("userId")));
  return c.json({ block });
});

socialRoutes.delete("/v1/blocks/:userId", async (c) => {
  const session = await requireSession(c);
  await limitMutation(c.env, session.userId);
  await unblockUser(c.env.DB, session.userId, targetUserId(c.req.param("userId")));
  return c.json({ ok: true });
});
