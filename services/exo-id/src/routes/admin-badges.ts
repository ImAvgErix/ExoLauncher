import { Hono } from "hono";
import type { Context } from "hono";
import type { Env } from "../env.ts";
import {
  canManageProfileBadges,
  grantProfileBadge,
  listPublicProfileBadges,
  listStaffRoles,
  parseProfileBadgeKey,
  revokeProfileBadge,
  type StaffRole,
} from "../badges.ts";
import { ApiError, ErrorCode } from "../errors.ts";
import { assertRateLimit, scopedRateKey } from "../rate-limit.ts";
import { requireSession, type Authed } from "../session.ts";
import { readExactJsonObject } from "../bounded-json.ts";

export const adminBadgeRoutes = new Hono<{ Bindings: Env }>();

const MAX_BADGE_JSON_BYTES = 2048;
const INVALID_BADGE_REQUEST = "Invalid badge request.";

type BadgeAuthority = { session: Authed; roles: StaffRole[] };

async function requireBadgeAuthority(c: Context<{ Bindings: Env }>): Promise<BadgeAuthority> {
  const session = await requireSession(c);
  const roles = await listStaffRoles(c.env.DB, session.userId);
  if (!canManageProfileBadges(roles)) {
    throw new ApiError(404, ErrorCode.NOT_FOUND, "Not found.");
  }
  await assertRateLimit(
    c.env.DB,
    await scopedRateKey(c.env.BETTER_AUTH_SECRET, "admin-badges", session.userId),
    { windowMs: 10 * 60 * 1000, max: 40 },
  );
  return { session, roles };
}

function normalizeTargetHandle(value: string): string | null {
  const trimmed = value.trim();
  if (!/^[A-Za-z0-9_]{3,24}$/.test(trimmed) || !/[A-Za-z]/.test(trimmed)) return null;
  return trimmed.toLowerCase();
}

async function targetByHandle(
  db: D1Database,
  rawHandle: string,
): Promise<{ userId: string; handle: { display: string; normalized: string } }> {
  const normalized = normalizeTargetHandle(rawHandle);
  const row = normalized
    ? await db.prepare(`SELECT user_id, display, normalized FROM handle WHERE normalized = ?`)
      .bind(normalized)
      .first<{ user_id: string; display: string; normalized: string }>()
    : null;
  if (!row) throw new ApiError(404, ErrorCode.NOT_FOUND, "Profile not found.");
  return { userId: row.user_id, handle: { display: row.display, normalized: row.normalized } };
}

function requireBadgeKey(raw: unknown) {
  if (typeof raw !== "string") {
    throw new ApiError(400, ErrorCode.INVALID_REQUEST, INVALID_BADGE_REQUEST);
  }
  const key = parseProfileBadgeKey(raw);
  if (!key) throw new ApiError(400, ErrorCode.INVALID_REQUEST, INVALID_BADGE_REQUEST);
  return key;
}

async function badgeResponse(db: D1Database, target: Awaited<ReturnType<typeof targetByHandle>>) {
  return { handle: target.handle, badges: await listPublicProfileBadges(db, target.userId) };
}

async function mutationBody(request: Request): Promise<{ handle: string; badge: string }> {
  const body = await readExactJsonObject(
    request,
    MAX_BADGE_JSON_BYTES,
    ["handle", "badge"],
    [],
    INVALID_BADGE_REQUEST,
  );
  if (typeof body.handle !== "string" || typeof body.badge !== "string") {
    throw new ApiError(400, ErrorCode.INVALID_REQUEST, INVALID_BADGE_REQUEST);
  }
  return { handle: body.handle, badge: body.badge };
}

adminBadgeRoutes.get("/v1/admin/badges", async (c) => {
  await requireBadgeAuthority(c);
  const url = new URL(c.req.url);
  if (url.searchParams.size !== 1 || !url.searchParams.has("handle")) {
    throw new ApiError(400, ErrorCode.INVALID_REQUEST, INVALID_BADGE_REQUEST);
  }
  const target = await targetByHandle(c.env.DB, url.searchParams.get("handle") ?? "");
  return c.json(await badgeResponse(c.env.DB, target));
});

adminBadgeRoutes.post("/v1/admin/badges", async (c) => {
  const authority = await requireBadgeAuthority(c);
  const body = await mutationBody(c.req.raw);
  const target = await targetByHandle(c.env.DB, body.handle);
  await grantProfileBadge(
    c.env.DB,
    authority.session.userId,
    authority.roles,
    target.userId,
    requireBadgeKey(body.badge),
  );
  return c.json(await badgeResponse(c.env.DB, target));
});

adminBadgeRoutes.delete("/v1/admin/badges", async (c) => {
  const authority = await requireBadgeAuthority(c);
  const body = await mutationBody(c.req.raw);
  const target = await targetByHandle(c.env.DB, body.handle);
  await revokeProfileBadge(
    c.env.DB,
    authority.session.userId,
    authority.roles,
    target.userId,
    requireBadgeKey(body.badge),
  );
  return c.json(await badgeResponse(c.env.DB, target));
});
