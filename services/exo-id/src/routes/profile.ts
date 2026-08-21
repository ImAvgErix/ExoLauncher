import { Hono } from "hono";
import type { Env } from "../env.ts";
import { ApiError, ErrorCode } from "../errors.ts";
import { requireSession } from "../session.ts";
import { isProfileKey, parseProfilePrivacy, validateProfileValue } from "../profile.ts";
import { parseFieldWrite, readFields, winner, writeField } from "../fields.ts";
import { getProfilePrivacy } from "../policy.ts";
import { assertRateLimit, scopedRateKey } from "../rate-limit.ts";
import { nowIso } from "../crypto.ts";
import { getProfileMediaProjection } from "../media.ts";
import { readExactJsonObject } from "../bounded-json.ts";
import { listPublicProfileBadges } from "../badges.ts";

const MAX_PROFILE_JSON_BYTES = 32 * 1024;
const INVALID_PROFILE_REQUEST = "JSON object required.";

export const profileRoutes = new Hono<{ Bindings: Env }>();

function valuesFrom(fields: Awaited<ReturnType<typeof readFields>>): Record<string, unknown> {
  const out: Record<string, unknown> = {};
  for (const [key, rec] of Object.entries(fields)) out[key] = rec.value;
  return out;
}

profileRoutes.get("/v1/profile", async (c) => {
  const session = await requireSession(c);
  const [fields, media, badges] = await Promise.all([
    readFields(c.env.DB, "profile_field", session.userId),
    getProfileMediaProjection(c.env.DB, session.userId),
    listPublicProfileBadges(c.env.DB, session.userId),
  ]);
  return c.json({ values: valuesFrom(fields), fields, media, badges });
});

profileRoutes.put("/v1/profile", async (c) => {
  const session = await requireSession(c);
  const body = await readExactJsonObject(
    c.req.raw,
    MAX_PROFILE_JSON_BYTES,
    ["deviceId", "fields"],
    [],
    INVALID_PROFILE_REQUEST,
  );
  const deviceId = typeof body.deviceId === "string" ? body.deviceId : "";
  if (!deviceId || deviceId.length > 80) {
    throw new ApiError(400, ErrorCode.INVALID_REQUEST, "deviceId is required.");
  }
  const incoming = body.fields;
  if (!incoming || typeof incoming !== "object" || Array.isArray(incoming)) {
    throw new ApiError(400, ErrorCode.INVALID_REQUEST, "fields is required.");
  }
  const existing = await readFields(c.env.DB, "profile_field", session.userId);
  const applied: string[] = [];
  const discarded: Array<{ key: string; reason: "older" | "denied" | "invalid"; message?: string }> = [];
  for (const [key, raw] of Object.entries(incoming as Record<string, unknown>)) {
    if (!isProfileKey(key)) {
      discarded.push({ key, reason: "denied", message: "Not a portable profile field." });
      continue;
    }
    const parsed = parseFieldWrite(raw, deviceId);
    if (!parsed) {
      discarded.push({ key, reason: "invalid", message: "Each field needs value and updatedAt." });
      continue;
    }
    let value: unknown;
    try {
      value = validateProfileValue(key, parsed.value);
    } catch (err) {
      discarded.push({ key, reason: "invalid", message: err instanceof Error ? err.message : "invalid" });
      continue;
    }
    const write = { value, updatedAt: parsed.updatedAt, deviceId: parsed.deviceId };
    if (winner(write, existing[key] ?? null) === "existing") {
      discarded.push({ key, reason: "older" });
      continue;
    }
    const wrote = await writeField(c.env.DB, "profile_field", session.userId, key, write);
    if (!wrote) {
      discarded.push({ key, reason: "older" });
      continue;
    }
    existing[key] = write;
    applied.push(key);
  }
  const current = await readFields(c.env.DB, "profile_field", session.userId);
  return c.json({ values: valuesFrom(current), fields: current, applied, discarded });
});

profileRoutes.get("/v1/profile/privacy", async (c) => {
  const session = await requireSession(c);
  return c.json({ privacy: await getProfilePrivacy(c.env, session.userId) });
});

profileRoutes.put("/v1/profile/privacy", async (c) => {
  const session = await requireSession(c);
  await assertRateLimit(
    c.env.DB,
    await scopedRateKey(c.env.BETTER_AUTH_SECRET, "profile-privacy", session.userId),
    { windowMs: 10 * 60 * 1000, max: 20 },
  );
  const privacy = parseProfilePrivacy(
    await readExactJsonObject(
      c.req.raw,
      MAX_PROFILE_JSON_BYTES,
      ["profileVisibility", "searchable", "requestPolicy", "activityVisibility"],
      [],
      INVALID_PROFILE_REQUEST,
    ),
  );
  const stamp = nowIso();
  await c.env.DB.prepare(
    `INSERT INTO profile_privacy
      (user_id, profile_visibility, searchable, request_policy, activity_visibility, updated_at)
     VALUES (?, ?, ?, ?, ?, ?)
     ON CONFLICT(user_id) DO UPDATE SET
       profile_visibility = excluded.profile_visibility,
       searchable = excluded.searchable,
       request_policy = excluded.request_policy,
       activity_visibility = excluded.activity_visibility,
       updated_at = excluded.updated_at`,
  )
    .bind(
      session.userId,
      privacy.profileVisibility,
      privacy.searchable ? 1 : 0,
      privacy.requestPolicy,
      privacy.activityVisibility,
      stamp,
    )
    .run();
  return c.json({ privacy: { ...privacy, updatedAt: stamp } });
});
