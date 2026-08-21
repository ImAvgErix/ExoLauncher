import { Hono } from "hono";
import type { Env } from "../env.ts";
import { ApiError, ErrorCode } from "../errors.ts";
import { requireHandle, requireSession } from "../session.ts";
import { classifySyncKey, isSyncAllowlisted, validateSyncValue } from "../sync.ts";
import { parseFieldWrite, readFields, winner, writeField } from "../fields.ts";
import { readExactJsonObject } from "../bounded-json.ts";

const MAX_SYNC_JSON_BYTES = 32 * 1024;
const INVALID_SYNC_REQUEST = "JSON object required.";

export const syncRoutes = new Hono<{ Bindings: Env }>();

function valuesFrom(fields: Awaited<ReturnType<typeof readFields>>): Record<string, unknown> {
  const out: Record<string, unknown> = {};
  for (const [key, rec] of Object.entries(fields)) out[key] = rec.value;
  return out;
}

syncRoutes.get("/v1/sync", async (c) => {
  const session = await requireSession(c);
  await requireHandle(c.env.DB, session.userId);
  const fields = await readFields(c.env.DB, "pref_field", session.userId);
  return c.json({ values: valuesFrom(fields), fields });
});

syncRoutes.put("/v1/sync", async (c) => {
  const session = await requireSession(c);
  await requireHandle(c.env.DB, session.userId);
  const body = await readExactJsonObject(
    c.req.raw,
    MAX_SYNC_JSON_BYTES,
    ["deviceId", "fields"],
    [],
    INVALID_SYNC_REQUEST,
  );
  const deviceId = typeof body.deviceId === "string" ? body.deviceId : "";
  if (!deviceId || deviceId.length > 80) {
    throw new ApiError(400, ErrorCode.INVALID_REQUEST, "deviceId is required.");
  }
  const incoming = body.fields;
  if (!incoming || typeof incoming !== "object" || Array.isArray(incoming)) {
    throw new ApiError(400, ErrorCode.INVALID_REQUEST, "fields is required.");
  }
  const existing = await readFields(c.env.DB, "pref_field", session.userId);
  const applied: string[] = [];
  const discarded: Array<{ key: string; reason: "older" | "denied" | "invalid"; message?: string }> = [];
  for (const [key, raw] of Object.entries(incoming as Record<string, unknown>)) {
    if (classifySyncKey(key) === "deny" || !isSyncAllowlisted(key)) {
      discarded.push({
        key,
        reason: "denied",
        message: "This key is machine-specific or not portable. The denylist wins; unknown keys are dropped.",
      });
      continue;
    }
    const parsed = parseFieldWrite(raw, deviceId);
    if (!parsed) {
      discarded.push({ key, reason: "invalid", message: "Each field needs value and updatedAt." });
      continue;
    }
    let value: unknown;
    try {
      value = validateSyncValue(key, parsed.value);
    } catch (err) {
      discarded.push({ key, reason: "invalid", message: err instanceof Error ? err.message : "invalid" });
      continue;
    }
    const write = { value, updatedAt: parsed.updatedAt, deviceId: parsed.deviceId };
    if (winner(write, existing[key] ?? null) === "existing") {
      discarded.push({ key, reason: "older" });
      continue;
    }
    const wrote = await writeField(c.env.DB, "pref_field", session.userId, key, write);
    if (!wrote) {
      discarded.push({ key, reason: "older" });
      continue;
    }
    existing[key] = write;
    applied.push(key);
  }
  const current = await readFields(c.env.DB, "pref_field", session.userId);
  return c.json({
    values: valuesFrom(current),
    fields: current,
    applied,
    discarded,
    deniedCode: ErrorCode.SYNC_DENIED_KEY,
  });
});
