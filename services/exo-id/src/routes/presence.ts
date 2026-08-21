import { Hono } from "hono";
import type { Env } from "../env.ts";
import { ApiError, ErrorCode } from "../errors.ts";
import {
  PRESENCE_OWNER_HEADER,
  PRESENCE_SESSION_HEADER,
} from "../presence-do.ts";
import { unavailablePresence } from "../presence.ts";
import { listConnectedFriendIds } from "../policy.ts";
import { requireSession } from "../session.ts";
import { assertRateLimit, clientIp, scopedRateKey } from "../rate-limit.ts";

export const presenceRoutes = new Hono<{ Bindings: Env }>();

function presenceLimit(raw: string | undefined): number {
  if (raw === undefined) return 50;
  if (!/^[1-9][0-9]*$/u.test(raw)) {
    throw new ApiError(400, ErrorCode.INVALID_REQUEST, "limit must be between 1 and 50.");
  }
  const limit = Number(raw);
  if (!Number.isSafeInteger(limit) || limit > 50) {
    throw new ApiError(400, ErrorCode.INVALID_REQUEST, "limit must be between 1 and 50.");
  }
  return limit;
}

presenceRoutes.get("/v1/presence/socket", async (c) => {
  const session = await requireSession(c);
  if (c.req.raw.headers.get("upgrade")?.toLowerCase() !== "websocket") {
    throw new ApiError(426, ErrorCode.INVALID_REQUEST, "Upgrade: websocket is required.");
  }
  await assertRateLimit(
    c.env.DB,
    await scopedRateKey(c.env.BETTER_AUTH_SECRET, "presence-socket-user", session.userId),
    { windowMs: 10 * 60 * 1000, max: 30 },
  );
  await assertRateLimit(
    c.env.DB,
    await scopedRateKey(c.env.BETTER_AUTH_SECRET, "presence-socket-ip", clientIp(c.req.raw.headers)),
    { windowMs: 10 * 60 * 1000, max: 60 },
  );

  const headers = new Headers({
    Upgrade: "websocket",
    [PRESENCE_OWNER_HEADER]: session.userId,
    [PRESENCE_SESSION_HEADER]: session.sessionId,
  });
  const internalRequest = new Request("https://presence.internal/socket", { method: "GET", headers });
  return c.env.PRESENCE.getByName(session.userId).fetch(internalRequest);
});

presenceRoutes.get("/v1/presence", async (c) => {
  const session = await requireSession(c);
  const limit = presenceLimit(c.req.query("limit"));
  let userIds: string[];
  try {
    const page = await listConnectedFriendIds(c.env, session.userId, { limit });
    userIds = page.userIds;
  } catch {
    return c.json({ friends: [], unavailable: true });
  }

  try {
    const friends = await c.env.PRESENCE.getByName(session.userId).getRoster(session.userId, userIds);
    return c.json({
      friends,
      unavailable: friends.some((friend) => friend.availability === "unavailable"),
    });
  } catch {
    return c.json({
      friends: userIds.map(unavailablePresence),
      unavailable: true,
    });
  }
});
