import { hmacSha256Hex } from "./crypto.ts";
import { ApiError, ErrorCode } from "./errors.ts";

export const STORES = ["steam", "epic", "gog"] as const;
export type Store = (typeof STORES)[number];

export function parseStore(value: unknown): Store {
  if (value === "steam" || value === "epic" || value === "gog") return value;
  throw new ApiError(400, ErrorCode.LINK_STORE_UNSUPPORTED, "That store cannot be linked yet.");
}

/** Individual SteamID64: universe 1, type 1, 32-bit account number. */
const STEAM_ID64_RE = /^7656119\d{10}$/;
const EPIC_ID_RE = /^[0-9a-f]{32}$/;
const GOG_ID_RE = /^\d{1,20}$/;

export function canonicalizeStoreId(store: Store, raw: string): string | null {
  const value = raw.trim();
  if (store === "steam") return STEAM_ID64_RE.test(value) ? value : null;
  if (store === "epic") {
    const id = value.toLowerCase();
    return EPIC_ID_RE.test(id) ? id : null;
  }
  return GOG_ID_RE.test(value) ? value : null;
}

export async function hashStoreId(secret: string, store: Store, canonicalId: string): Promise<string> {
  return hmacSha256Hex(secret, `store-link-v1|${store}|${canonicalId}`);
}

export function parseRelationship(value: unknown): "mutual" | "onesided" {
  if (value === "mutual" || value === "onesided") return value;
  throw new ApiError(400, ErrorCode.INVALID_REQUEST, "relationship must be mutual or onesided.");
}
