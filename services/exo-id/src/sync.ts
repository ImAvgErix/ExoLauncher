export const SYNC_ALLOWLIST = [
  "sortMode",
  "trophyNotificationsEnabled",
  "trophyNotificationPosition",
  "trophyNotificationPreset",
  "trophyNotificationSound",
  "trophyNotificationSoundCue",
] as const;

export type SyncAllowKey = (typeof SYNC_ALLOWLIST)[number];

export const SYNC_DENYLIST = [
  "defaultInstallRoot",
  "launchOverrides",
  "appVersion",
  "copyPortableIntoLibrary",
  "allowResize",
  "trophyNotificationPositionX",
  "trophyNotificationPositionY",
  "profileAvatarImage",
  "profileBannerImage",
  "onboardingComplete",
  "closeStoreClientsAfterLaunch",
  "autoInstallRedistributables",
  "minimizeWhilePlaying",
  "antiCheatSafeMode",
  "favorites",
  "recent",
  "lastPlayed",
  "profileShowcase",
  "profileRoster",
  "profileAvatarGameId",
  "profileBannerGameId",
  "profileHandle",
] as const;

const ALLOW = new Set<string>(SYNC_ALLOWLIST);
const DENY = new Set<string>(SYNC_DENYLIST);

const PATHISH = /path|directory|cwd|workingdirectory|installroot|window/i;

export type SyncDecision = "allow" | "deny";

export function classifySyncKey(key: string): SyncDecision {
  if (ALLOW.has(key)) return "allow";
  if (DENY.has(key)) return "deny";
  if (PATHISH.test(key)) return "deny";
  return "deny";
}

export function isSyncAllowlisted(key: string): key is SyncAllowKey {
  return ALLOW.has(key);
}

const SORT_MODES = new Set(["name", "recent", "size", "store"]);
const TROPHY_POSITIONS = new Set([
  "top-left",
  "top-center",
  "top-right",
  "center-left",
  "center",
  "center-right",
  "bottom-left",
  "bottom-center",
  "bottom-right",
]);

export function validateSyncValue(key: SyncAllowKey, value: unknown): unknown {
  switch (key) {
    case "sortMode":
      if (typeof value !== "string" || !SORT_MODES.has(value)) {
        throw new Error("sortMode must be name, recent, size, or store.");
      }
      return value;
    case "trophyNotificationsEnabled":
    case "trophyNotificationSound":
      if (typeof value !== "boolean") throw new Error(`${key} must be a boolean.`);
      return value;
    case "trophyNotificationPosition":
      if (typeof value !== "string" || !TROPHY_POSITIONS.has(value)) {
        throw new Error("trophyNotificationPosition must be a named anchor.");
      }
      return value;
    case "trophyNotificationPreset":
    case "trophyNotificationSoundCue":
      if (typeof value !== "string" || value.length === 0 || value.length > 32) {
        throw new Error(`${key} must be a short string.`);
      }
      return value;
    default: {
      const _never: never = key;
      return _never;
    }
  }
}
