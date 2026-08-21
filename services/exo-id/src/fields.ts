import { MAX_FIELD_CLOCK_SKEW_MS } from "./env.ts";

export type FieldWrite = {
  value: unknown;
  updatedAt: string;
  deviceId: string;
};

export type FieldRecord = FieldWrite;

export type ApplyResult = {
  applied: string[];
  discarded: Array<{ key: string; reason: "older" | "denied" | "invalid"; message?: string }>;
};

function parseTime(iso: string): number {
  const t = Date.parse(iso);
  return Number.isFinite(t) ? t : 0;
}

export function winner(incoming: FieldWrite, existing: FieldRecord | null): "incoming" | "existing" {
  if (!existing) return "incoming";
  const a = parseTime(incoming.updatedAt);
  const b = parseTime(existing.updatedAt);
  if (a > b) return "incoming";
  if (a < b) return "existing";
  return incoming.deviceId > existing.deviceId ? "incoming" : "existing";
}

export async function readFields(
  db: D1Database,
  table: "profile_field" | "pref_field",
  userId: string,
): Promise<Record<string, FieldRecord>> {
  const rows = await db
    .prepare(`SELECT key, value, updated_at, device_id FROM ${table} WHERE user_id = ?`)
    .bind(userId)
    .all<{ key: string; value: string; updated_at: string; device_id: string }>();
  const out: Record<string, FieldRecord> = {};
  for (const row of rows.results ?? []) {
    out[row.key] = {
      value: JSON.parse(row.value) as unknown,
      updatedAt: row.updated_at,
      deviceId: row.device_id,
    };
  }
  return out;
}

export async function writeField(
  db: D1Database,
  table: "profile_field" | "pref_field",
  userId: string,
  key: string,
  field: FieldWrite,
): Promise<boolean> {
  const result = await db
    .prepare(
      `INSERT INTO ${table} (user_id, key, value, updated_at, device_id)
       VALUES (?, ?, ?, ?, ?)
       ON CONFLICT(user_id, key) DO UPDATE SET
         value = excluded.value,
         updated_at = excluded.updated_at,
         device_id = excluded.device_id
       WHERE excluded.updated_at > ${table}.updated_at
          OR (excluded.updated_at = ${table}.updated_at AND excluded.device_id > ${table}.device_id)`,
    )
    .bind(userId, key, JSON.stringify(field.value), field.updatedAt, field.deviceId)
    .run();
  return (result.meta.changes ?? 0) > 0;
}

export function parseFieldWrite(
  raw: unknown,
  deviceId: string,
): { value: unknown; updatedAt: string; deviceId: string } | null {
  if (!raw || typeof raw !== "object") return null;
  const rec = raw as Record<string, unknown>;
  if (!("value" in rec)) return null;
  const rawUpdatedAt = typeof rec.updatedAt === "string" ? rec.updatedAt : "";
  const time = Date.parse(rawUpdatedAt);
  if (!rawUpdatedAt || !Number.isFinite(time)) return null;
  if (time > Date.now() + MAX_FIELD_CLOCK_SKEW_MS) return null;
  const updatedAt = new Date(time).toISOString();
  const id = typeof rec.deviceId === "string" && rec.deviceId ? rec.deviceId : deviceId;
  if (!id || id.length > 80) return null;
  return { value: rec.value, updatedAt, deviceId: id };
}
