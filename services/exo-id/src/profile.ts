import { ApiError, ErrorCode } from "./errors.ts";

const ACCENTS = new Set(["ash", "steel", "sand", "clay", "sage", "rose"]);
const LAYOUTS = new Set(["left", "center"]);
const BANNER_HEIGHTS = new Set(["short", "standard", "tall"]);
const SHOWCASE_STYLES = new Set(["grid", "rows"]);
const SECTIONS = ["facts", "about", "showcase", "stores"] as const;
const SECTION_SET = new Set<string>(SECTIONS);

export const PROFILE_KEYS = [
  "displayName",
  "pronouns",
  "statusText",
  "bio",
  "accent",
  "layout",
  "bannerHeight",
  "showcaseStyle",
  "sections",
  "hiddenSections",
  "showcase",
  "avatarGameId",
  "bannerGameId",
] as const;

export type ProfileKey = (typeof PROFILE_KEYS)[number];

export type ProfileVisibility = "public" | "friends" | "private";
export type FriendRequestPolicy = "anyone" | "none";
export type ActivityVisibility = "friends" | "private";

export type ProfilePrivacySettings = {
  profileVisibility: ProfileVisibility;
  searchable: boolean;
  requestPolicy: FriendRequestPolicy;
  activityVisibility: ActivityVisibility;
};

export type ProfilePrivacy = ProfilePrivacySettings & { updatedAt: string | null };

export const DEFAULT_PROFILE_PRIVACY: Readonly<ProfilePrivacy> = Object.freeze({
  profileVisibility: "friends",
  searchable: false,
  requestPolicy: "anyone",
  activityVisibility: "friends",
  updatedAt: null,
});

const PROFILE_SET = new Set<string>(PROFILE_KEYS);

export function isProfileKey(key: string): key is ProfileKey {
  return PROFILE_SET.has(key);
}

export function publicProfileValues(
  fields: Record<string, { value: unknown }>,
): Partial<Record<ProfileKey, unknown>> {
  const values: Partial<Record<ProfileKey, unknown>> = {};
  for (const key of PROFILE_KEYS) {
    const field = fields[key];
    if (field) values[key] = field.value;
  }
  return values;
}

export function parseProfilePrivacy(value: unknown): ProfilePrivacySettings {
  if (!value || typeof value !== "object" || Array.isArray(value)) {
    throw new ApiError(400, ErrorCode.INVALID_REQUEST, "JSON object required.");
  }
  const body = value as Record<string, unknown>;
  const allowed = new Set(["profileVisibility", "searchable", "requestPolicy", "activityVisibility"]);
  if (Object.keys(body).some((key) => !allowed.has(key))) {
    throw new ApiError(400, ErrorCode.INVALID_REQUEST, "Privacy settings contain an unknown field.");
  }
  if (!(["public", "friends", "private"] as unknown[]).includes(body.profileVisibility)) {
    throw new ApiError(400, ErrorCode.INVALID_REQUEST, "profileVisibility is not valid.");
  }
  if (typeof body.searchable !== "boolean") {
    throw new ApiError(400, ErrorCode.INVALID_REQUEST, "searchable must be a boolean.");
  }
  if (!(["anyone", "none"] as unknown[]).includes(body.requestPolicy)) {
    throw new ApiError(400, ErrorCode.INVALID_REQUEST, "requestPolicy is not valid.");
  }
  if (!(["friends", "private"] as unknown[]).includes(body.activityVisibility)) {
    throw new ApiError(400, ErrorCode.INVALID_REQUEST, "activityVisibility is not valid.");
  }
  return {
    profileVisibility: body.profileVisibility as ProfileVisibility,
    searchable: body.searchable,
    requestPolicy: body.requestPolicy as FriendRequestPolicy,
    activityVisibility: body.activityVisibility as ActivityVisibility,
  };
}

function capString(value: unknown, max: number, field: string): string {
  if (typeof value !== "string") throw new Error(`${field} must be a string.`);
  const trimmed = value.trim();
  if (trimmed.length > max) throw new Error(`${field} is too long.`);
  return trimmed;
}

function optionalId(value: unknown, field: string): string | null {
  if (value === null || value === "") return null;
  if (typeof value !== "string") throw new Error(`${field} must be a string.`);
  const trimmed = value.trim();
  if (trimmed.length === 0) return null;
  if (trimmed.length > 80) throw new Error(`${field} is too long.`);
  if (trimmed.includes("\\") || trimmed.includes("/") || trimmed.includes("..")) {
    throw new Error(`${field} must not be a filesystem path.`);
  }
  return trimmed;
}

function stringList(value: unknown, allowed: Set<string> | null, maxItems: number, field: string): string[] {
  if (!Array.isArray(value)) throw new Error(`${field} must be an array.`);
  if (value.length > maxItems) throw new Error(`${field} is too long.`);
  const out: string[] = [];
  const seen = new Set<string>();
  for (const item of value) {
    if (typeof item !== "string") throw new Error(`${field} items must be strings.`);
    const key = item.trim();
    if (!key || seen.has(key)) continue;
    if (allowed && !allowed.has(key)) continue;
    if (!allowed && (key.includes("\\") || key.includes("/") || key.includes("..") || key.length > 80)) {
      throw new Error(`${field} items must not be filesystem paths.`);
    }
    seen.add(key);
    out.push(key);
  }
  return out;
}

export function validateProfileValue(key: ProfileKey, value: unknown): unknown {
  switch (key) {
    case "displayName":
      return capString(value, 40, key);
    case "pronouns":
      return capString(value, 24, key);
    case "statusText":
      return capString(value, 80, key);
    case "bio":
      return capString(value, 400, key);
    case "accent": {
      const v = typeof value === "string" ? value.trim().toLowerCase() : "";
      if (!ACCENTS.has(v)) throw new Error("accent is not a known Exo accent.");
      return v;
    }
    case "layout": {
      const v = typeof value === "string" ? value.trim().toLowerCase() : "";
      if (!LAYOUTS.has(v)) throw new Error("layout must be left or center.");
      return v;
    }
    case "bannerHeight": {
      const v = typeof value === "string" ? value.trim().toLowerCase() : "";
      if (!BANNER_HEIGHTS.has(v)) throw new Error("bannerHeight must be short, standard, or tall.");
      return v;
    }
    case "showcaseStyle": {
      const v = typeof value === "string" ? value.trim().toLowerCase() : "";
      if (!SHOWCASE_STYLES.has(v)) throw new Error("showcaseStyle must be grid or rows.");
      return v;
    }
    case "sections": {
      const list = stringList(value, SECTION_SET, 8, key);
      for (const keyName of SECTIONS) if (!list.includes(keyName)) list.push(keyName);
      return list;
    }
    case "hiddenSections":
      return stringList(value, SECTION_SET, 8, key);
    case "showcase":
      return stringList(value, null, 10, key);
    case "avatarGameId":
    case "bannerGameId":
      return optionalId(value, key);
    default: {
      const _never: never = key;
      return _never;
    }
  }
}
