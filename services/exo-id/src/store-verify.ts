import { LINK_VERIFY_TIMEOUT_MS } from "./env.ts";
import { ApiError, ErrorCode } from "./errors.ts";
import { canonicalizeStoreId, type Store } from "./stores.ts";
import type { FetchLike } from "./steam-openid.ts";

export const EPIC_OAUTH_VERIFY_URL =
  "https://account-public-service-prod.ol.epicgames.com/account/api/oauth/verify";
export const GOG_USERDATA_URL = "https://embed.gog.com/userData.json";

function readString(record: Record<string, unknown>, keys: string[]): string | null {
  for (const key of keys) {
    const value = record[key];
    if (typeof value === "string" && value.trim()) return value.trim();
  }
  return null;
}

async function getJson(
  url: string,
  token: string,
  scheme: "bearer" | "Bearer",
  fetchImpl: FetchLike,
): Promise<Record<string, unknown>> {
  let response: Response;
  try {
    response = await fetchImpl(url, {
      method: "GET",
      headers: {
        authorization: `${scheme} ${token}`,
        accept: "application/json",
      },
      redirect: "error",
      signal: AbortSignal.timeout(LINK_VERIFY_TIMEOUT_MS),
    });
  } catch {
    throw new ApiError(400, ErrorCode.LINK_VERIFY_FAILED, "That store could not verify the session.");
  }
  if (!response.ok) {
    throw new ApiError(400, ErrorCode.LINK_VERIFY_FAILED, "That store could not verify the session.");
  }
  const body: unknown = await response.json().catch(() => null);
  if (!body || typeof body !== "object" || Array.isArray(body)) {
    throw new ApiError(400, ErrorCode.LINK_VERIFY_FAILED, "That store could not verify the session.");
  }
  return body as Record<string, unknown>;
}

export async function verifyEpicAccessToken(token: string, fetchImpl: FetchLike = fetch): Promise<string> {
  const json = await getJson(EPIC_OAUTH_VERIFY_URL, token, "bearer", fetchImpl);
  const id = canonicalizeStoreId("epic", readString(json, ["account_id", "accountId"]) ?? "");
  if (!id) {
    throw new ApiError(400, ErrorCode.LINK_VERIFY_FAILED, "That store could not verify the session.");
  }
  return id;
}

export async function verifyGogAccessToken(token: string, fetchImpl: FetchLike = fetch): Promise<string> {
  const json = await getJson(GOG_USERDATA_URL, token, "Bearer", fetchImpl);
  if (json.isLoggedIn === false) {
    throw new ApiError(400, ErrorCode.LINK_VERIFY_FAILED, "That store could not verify the session.");
  }
  const raw = readString(json, ["userId", "user_id", "galaxyUserId"]);
  const id = raw ? canonicalizeStoreId("gog", raw) : null;
  if (!id) {
    throw new ApiError(400, ErrorCode.LINK_VERIFY_FAILED, "That store could not verify the session.");
  }
  return id;
}

export async function verifyStoreAccessToken(
  store: Exclude<Store, "steam">,
  token: string,
  fetchImpl: FetchLike = fetch,
): Promise<string> {
  if (store === "epic") return verifyEpicAccessToken(token, fetchImpl);
  return verifyGogAccessToken(token, fetchImpl);
}
