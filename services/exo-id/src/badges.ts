import { isUniqueViolation, nowIso } from "./crypto.ts";
import { ApiError, ErrorCode } from "./errors.ts";

export const STAFF_ROLES = ["owner", "admin", "developer"] as const;
export type StaffRole = (typeof STAFF_ROLES)[number];

export const PROFILE_BADGE_KEYS = [
  "founder",
  "developer",
  "moderator",
  "contributor",
  "early_supporter",
] as const;
export type ProfileBadgeKey = (typeof PROFILE_BADGE_KEYS)[number];

export type ProfileBadgeTone = "founder" | "staff" | "community" | "supporter";

export type PublicProfileBadge = {
  key: ProfileBadgeKey;
  label: string;
  description: string;
  tone: ProfileBadgeTone;
};

export type ProfileBadgeRecord = PublicProfileBadge & { grantedAt: string };

type BadgeDefinition = PublicProfileBadge & {
  priority: number;
};

const BADGE_DEFINITIONS: Readonly<Record<ProfileBadgeKey, BadgeDefinition>> = {
  founder: {
    key: "founder",
    label: "Founder",
    description: "Founder of Exo",
    tone: "founder",
    priority: 0,
  },
  developer: {
    key: "developer",
    label: "Developer",
    description: "Builds Exo",
    tone: "staff",
    priority: 2,
  },
  moderator: {
    key: "moderator",
    label: "Moderator",
    description: "Helps keep Exo welcoming",
    tone: "staff",
    priority: 3,
  },
  contributor: {
    key: "contributor",
    label: "Contributor",
    description: "Contributed to Exo",
    tone: "community",
    priority: 4,
  },
  early_supporter: {
    key: "early_supporter",
    label: "Early Supporter",
    description: "Supported Exo early",
    tone: "supporter",
    priority: 5,
  },
};

type BadgeRow = { badge_key: string; granted_at: string };

function isStaffRole(value: string): value is StaffRole {
  return (STAFF_ROLES as readonly string[]).includes(value);
}

export function parseProfileBadgeKey(value: string): ProfileBadgeKey | null {
  return (PROFILE_BADGE_KEYS as readonly string[]).includes(value)
    ? value as ProfileBadgeKey
    : null;
}

function badgeProjection(key: ProfileBadgeKey): PublicProfileBadge {
  const { priority: _priority, ...safe } = BADGE_DEFINITIONS[key];
  return safe;
}

function validBadgeRows(rows: BadgeRow[]): Array<{ key: ProfileBadgeKey; grantedAt: string }> {
  const out: Array<{ key: ProfileBadgeKey; grantedAt: string }> = [];
  for (const row of rows) {
    const key = parseProfileBadgeKey(row.badge_key);
    if (key) out.push({ key, grantedAt: row.granted_at });
  }
  out.sort((a, b) => BADGE_DEFINITIONS[a.key].priority - BADGE_DEFINITIONS[b.key].priority);
  return out;
}

async function badgeRows(db: D1Database, userId: string): Promise<Array<{ key: ProfileBadgeKey; grantedAt: string }>> {
  const result = await db.prepare(
    `SELECT badge_key, granted_at FROM profile_badge WHERE user_id = ?`,
  )
    .bind(userId)
    .all<BadgeRow>();
  return validBadgeRows(result.results ?? []);
}

export async function listStaffRoles(db: D1Database, userId: string): Promise<StaffRole[]> {
  const result = await db.prepare(`SELECT role FROM staff_role WHERE user_id = ?`)
    .bind(userId)
    .all<{ role: string }>();
  const found = new Set((result.results ?? []).map((row) => row.role).filter(isStaffRole));
  return STAFF_ROLES.filter((role) => found.has(role));
}

export function canManageProfileBadges(roles: readonly StaffRole[]): boolean {
  return roles.includes("owner");
}

export async function listPublicProfileBadges(db: D1Database, userId: string): Promise<PublicProfileBadge[]> {
  return (await badgeRows(db, userId)).map((row) => badgeProjection(row.key));
}

export async function listProfileBadgeRecords(db: D1Database, userId: string): Promise<ProfileBadgeRecord[]> {
  return (await badgeRows(db, userId)).map((row) => ({ ...badgeProjection(row.key), grantedAt: row.grantedAt }));
}

export async function grantProfileBadge(
  db: D1Database,
  actorId: string,
  actorRoles: readonly StaffRole[],
  targetId: string,
  key: ProfileBadgeKey,
): Promise<void> {
  if (actorId === targetId) {
    throw new ApiError(400, ErrorCode.INVALID_REQUEST, "You cannot change your own badges.");
  }
  if (!canManageProfileBadges(actorRoles)) {
    throw new ApiError(404, ErrorCode.NOT_FOUND, "Not found.");
  }
  try {
    await db.prepare(
      `INSERT INTO profile_badge (user_id, badge_key, granted_by, granted_at)
       VALUES (?, ?, ?, ?)
       ON CONFLICT(user_id, badge_key) DO NOTHING`,
    )
      .bind(targetId, key, actorId, nowIso())
      .run();
  } catch (error) {
    if (!isUniqueViolation(error)) throw error;
    throw new ApiError(409, ErrorCode.INVALID_REQUEST, "Badge cannot be granted.");
  }
}

export async function revokeProfileBadge(
  db: D1Database,
  actorId: string,
  actorRoles: readonly StaffRole[],
  targetId: string,
  key: ProfileBadgeKey,
): Promise<void> {
  if (actorId === targetId) {
    throw new ApiError(400, ErrorCode.INVALID_REQUEST, "You cannot change your own badges.");
  }
  if (!canManageProfileBadges(actorRoles)) {
    throw new ApiError(404, ErrorCode.NOT_FOUND, "Not found.");
  }
  await db.prepare(`DELETE FROM profile_badge WHERE user_id = ? AND badge_key = ?`)
    .bind(targetId, key)
    .run();
}
