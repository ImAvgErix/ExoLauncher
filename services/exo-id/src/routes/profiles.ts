import { Hono } from "hono";
import type { Context } from "hono";
import type { Env } from "../env.ts";
import { ApiError, ErrorCode } from "../errors.ts";
import { readFields } from "../fields.ts";
import { decodeCursor, encodeCursor, parsePageLimit } from "../pagination.ts";
import { canViewProfile } from "../policy.ts";
import { publicProfileValues } from "../profile.ts";
import { assertRateLimit, clientIp, scopedRateKey } from "../rate-limit.ts";
import { requireSession } from "../session.ts";
import { resolveHandleUser, type HandleSummary } from "../social.ts";
import { getProfileMediaProjection, type ProfileGalleryKind, type PublicProfileMedia } from "../media.ts";
import { listPublicProfileBadges, type PublicProfileBadge } from "../badges.ts";

type PublicProfile = {
  userId: string;
  handle: HandleSummary;
  profile: Record<string, unknown>;
  media: { avatar: PublicProfileMedia | null; banner: PublicProfileMedia | null } & Partial<Record<ProfileGalleryKind, PublicProfileMedia>>;
  badges: PublicProfileBadge[];
};

type SearchRow = {
  user_id: string;
  display: string;
  normalized: string;
  display_name: string | null;
  status_text: string | null;
  accent: string | null;
  avatar_game_id: string | null;
};

type SearchProfile = Omit<PublicProfile, "badges">;

export const profilesRoutes = new Hono<{ Bindings: Env }>();

function normalizeExactHandle(value: string): string | null {
  const handle = value.trim();
  if (!/^[A-Za-z0-9_]{3,24}$/.test(handle) || !/[A-Za-z]/.test(handle)) return null;
  return handle.toLowerCase();
}

function normalizeSearch(value: string | undefined): string {
  const query = value?.trim() ?? "";
  if (!query || query.length > 24 || !/^[A-Za-z0-9_]+$/.test(query)) {
    throw new ApiError(400, ErrorCode.INVALID_REQUEST, "q must be 1–24 ASCII letters, digits, or underscore.");
  }
  return query.toLowerCase();
}

async function optionalViewerId(c: Context<{ Bindings: Env }>): Promise<string | null> {
  const authorization = c.req.header("authorization") ?? "";
  if (!authorization) return null;
  if (!/^Bearer\s+\S+$/i.test(authorization)) {
    throw new ApiError(401, ErrorCode.UNAUTHENTICATED, "Sign in required.");
  }
  return (await requireSession(c)).userId;
}

async function exactProfile(env: Env, rawHandle: string, viewerId: string | null): Promise<PublicProfile | null> {
  const normalized = normalizeExactHandle(rawHandle);
  if (!normalized) return null;
  const owner = await resolveHandleUser(env.DB, normalized);
  if (!owner || !(await canViewProfile(env, owner.userId, viewerId))) return null;
  const [fields, media, badges] = await Promise.all([
    readFields(env.DB, "profile_field", owner.userId),
    getProfileMediaProjection(env.DB, owner.userId),
    listPublicProfileBadges(env.DB, owner.userId),
  ]);
  return {
    userId: owner.userId,
    handle: owner.handle,
    profile: publicProfileValues(fields) as Record<string, unknown>,
    media,
    badges,
  };
}

function parseStoredValue(value: string | null): unknown | undefined {
  if (value === null) return undefined;
  try {
    return JSON.parse(value) as unknown;
  } catch {
    return undefined;
  }
}

function searchProfile(row: SearchRow): SearchProfile {
  const profile: Record<string, unknown> = {};
  for (const [key, raw] of [
    ["displayName", row.display_name],
    ["statusText", row.status_text],
    ["accent", row.accent],
    ["avatarGameId", row.avatar_game_id],
  ] as const) {
    const value = parseStoredValue(raw);
    if (value !== undefined) profile[key] = value;
  }
  return {
    userId: row.user_id,
    handle: { display: row.display, normalized: row.normalized },
    profile,
    media: { avatar: null, banner: null },
  };
}

function escapeHtml(value: string): string {
  return value
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#39;");
}

function textValue(profile: Record<string, unknown>, key: string): string {
  const value = profile[key];
  return typeof value === "string" ? value.trim() : "";
}

function publicProfileHtml(env: Env, profile: PublicProfile): string {
  const displayName = textValue(profile.profile, "displayName") || `@${profile.handle.display}`;
  const description = (textValue(profile.profile, "statusText") || textValue(profile.profile, "bio") || "Exo profile")
    .slice(0, 160);
  const origin = env.BETTER_AUTH_URL.replace(/\/+$/, "");
  const canonical = `${origin}/p/${encodeURIComponent(profile.handle.normalized)}`;
  const imagePath = profile.media.avatar?.url ?? profile.media.banner?.url ?? null;
  const image = imagePath ? new URL(imagePath, origin).toString() : null;
  return `<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>${escapeHtml(displayName)} · Exo</title>
  <meta name="description" content="${escapeHtml(description)}">
  <meta property="og:type" content="profile">
  <meta property="og:title" content="${escapeHtml(displayName)}">
  <meta property="og:description" content="${escapeHtml(description)}">
  <meta property="og:url" content="${escapeHtml(canonical)}">
  ${image ? `<meta property="og:image" content="${escapeHtml(image)}">` : ""}
  <link rel="canonical" href="${escapeHtml(canonical)}">
</head>
<body>
  <main>
    <h1>${escapeHtml(displayName)}</h1>
    <p>@${escapeHtml(profile.handle.display)}</p>
    <p>${escapeHtml(description)}</p>
  </main>
</body>
</html>`;
}

profilesRoutes.get("/v1/profiles/search", async (c) => {
  const viewerId = await optionalViewerId(c);
  const query = normalizeSearch(c.req.query("q"));
  const limit = parsePageLimit(c.req.query("limit"));
  await assertRateLimit(
    c.env.DB,
    await scopedRateKey(c.env.BETTER_AUTH_SECRET, "profile-search-ip", clientIp(c.req.raw.headers)),
    { windowMs: 10 * 60 * 1000, max: 60 },
  );
  if (viewerId) {
    await assertRateLimit(
      c.env.DB,
      await scopedRateKey(c.env.BETTER_AUTH_SECRET, "profile-search-user", viewerId),
      { windowMs: 10 * 60 * 1000, max: 30 },
    );
  }
  const scope = `profile-search:${viewerId ?? "anon"}:${query}`;
  const cursor = decodeCursor(c.req.query("cursor"), scope);
  if (cursor && !cursor.tie) {
    throw new ApiError(400, ErrorCode.INVALID_REQUEST, "cursor is not valid for this request.");
  }
  const cursorHandle = cursor?.key ?? "";
  const cursorUserId = cursor?.tie ?? "";
  const viewer = viewerId ?? "";
  const rows = await c.env.DB.prepare(
    `SELECT h.user_id, h.display, h.normalized,
            (SELECT value FROM profile_field WHERE user_id = h.user_id AND key = 'displayName') AS display_name,
            (SELECT value FROM profile_field WHERE user_id = h.user_id AND key = 'statusText') AS status_text,
            (SELECT value FROM profile_field WHERE user_id = h.user_id AND key = 'accent') AS accent,
            (SELECT value FROM profile_field WHERE user_id = h.user_id AND key = 'avatarGameId') AS avatar_game_id
     FROM handle h
     LEFT JOIN profile_privacy p ON p.user_id = h.user_id
     WHERE substr(h.normalized, 1, length(?)) = ?
       AND COALESCE(p.searchable, 0) = 1
       AND (? = '' OR h.normalized > ? OR (h.normalized = ? AND h.user_id > ?))
       AND NOT EXISTS (
         SELECT 1 FROM user_block b
         WHERE ? <> '' AND (
           (b.blocker_id = ? AND b.blocked_id = h.user_id) OR
           (b.blocker_id = h.user_id AND b.blocked_id = ?)
         )
       )
       AND (
         h.user_id = ?
         OR COALESCE(p.profile_visibility, 'friends') = 'public'
         OR (
           ? <> '' AND COALESCE(p.profile_visibility, 'friends') = 'friends'
           AND NOT EXISTS (
             SELECT 1 FROM friend_suppression s
             WHERE (s.user_low = ? AND s.user_high = h.user_id)
                OR (s.user_low = h.user_id AND s.user_high = ?)
           )
           AND (
             EXISTS (
               SELECT 1 FROM direct_friendship df
               WHERE (df.user_low = ? AND df.user_high = h.user_id)
                  OR (df.user_low = h.user_id AND df.user_high = ?)
             )
             OR EXISTS (
               SELECT 1 FROM discovered_connection dc
               WHERE (dc.user_low = ? AND dc.user_high = h.user_id)
                  OR (dc.user_low = h.user_id AND dc.user_high = ?)
             )
           )
         )
       )
     ORDER BY h.normalized, h.user_id
     LIMIT ?`,
  )
    .bind(
      query,
      query,
      cursorHandle,
      cursorHandle,
      cursorHandle,
      cursorUserId,
      viewer,
      viewer,
      viewer,
      viewer,
      viewer,
      viewer,
      viewer,
      viewer,
      viewer,
      viewer,
      viewer,
      limit + 1,
    )
    .all<SearchRow>();
  const all = rows.results ?? [];
  const page = all.slice(0, limit);
  const last = page[page.length - 1];
  return c.json({
    profiles: page.map(searchProfile),
    nextCursor:
      all.length > limit && last ? encodeCursor(scope, last.normalized, last.user_id) : null,
  });
});

profilesRoutes.get("/v1/profiles/:handle", async (c) => {
  const profile = await exactProfile(c.env, c.req.param("handle"), await optionalViewerId(c));
  if (!profile) throw new ApiError(404, ErrorCode.NOT_FOUND, "Profile not found.");
  return c.json(profile);
});

profilesRoutes.get("/p/:handle", async (c) => {
  const profile = await exactProfile(c.env, c.req.param("handle"), null);
  if (!profile) throw new ApiError(404, ErrorCode.NOT_FOUND, "Profile not found.");
  c.header("Content-Security-Policy", "default-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'");
  c.header("Cache-Control", "no-store");
  c.header("Referrer-Policy", "no-referrer");
  c.header("X-Content-Type-Options", "nosniff");
  return c.html(publicProfileHtml(c.env, profile));
});
