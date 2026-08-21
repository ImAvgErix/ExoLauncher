import { isTestEnv, LINK_VERIFY_TIMEOUT_MS, type Env } from "./env.ts";
import { ApiError, ErrorCode } from "./errors.ts";
import { canonicalizeStoreId } from "./stores.ts";

export const STEAM_OPENID_ENDPOINT = "https://steamcommunity.com/openid/login";
export const STEAM_OPENID_NS = "http://specs.openid.net/auth/2.0";
const STEAM_CLAIMED_ID_RE = /^https?:\/\/steamcommunity\.com\/openid\/id\/(7656119\d{10})$/;
const REQUIRED_SIGNED = ["claimed_id", "identity", "return_to", "response_nonce", "assoc_handle"];

export type FetchLike = (input: RequestInfo | URL, init?: RequestInit) => Promise<Response>;

export function steamClaimedId(raw: string | null): string | null {
  if (!raw) return null;
  const match = STEAM_CLAIMED_ID_RE.exec(raw.trim());
  if (!match) return null;
  return canonicalizeStoreId("steam", match[1]);
}

export function buildSteamOpenIdUrl(realm: string, returnTo: string): string {
  const params = new URLSearchParams({
    "openid.ns": STEAM_OPENID_NS,
    "openid.mode": "checkid_setup",
    "openid.return_to": returnTo,
    "openid.realm": realm,
    "openid.identity": "http://specs.openid.net/auth/2.0/identifier_select",
    "openid.claimed_id": "http://specs.openid.net/auth/2.0/identifier_select",
  });
  return `${STEAM_OPENID_ENDPOINT}?${params.toString()}`;
}

function openidParamsFrom(url: URL): URLSearchParams {
  const params = new URLSearchParams();
  for (const [key, value] of url.searchParams.entries()) {
    if (key.startsWith("openid.")) params.append(key, value);
  }
  return params;
}

function signedFieldsIncludeRequired(signed: string): boolean {
  const fields = new Set(signed.split(",").map((part) => part.trim()).filter(Boolean));
  return REQUIRED_SIGNED.every((name) => fields.has(name));
}

function assertionLooksLikeSteam(params: URLSearchParams, expectedReturnTo: string): boolean {
  if (params.get("openid.ns") !== STEAM_OPENID_NS) return false;
  if (params.get("openid.mode") !== "id_res") return false;
  if (params.get("openid.op_endpoint") !== STEAM_OPENID_ENDPOINT) return false;
  const claimed = params.get("openid.claimed_id");
  const identity = params.get("openid.identity");
  if (!claimed || claimed !== identity) return false;
  if (!steamClaimedId(claimed)) return false;
  if (params.get("openid.return_to") !== expectedReturnTo) return false;
  const signed = params.get("openid.signed") ?? "";
  if (!signedFieldsIncludeRequired(signed)) return false;
  if (!params.get("openid.sig") || !params.get("openid.response_nonce") || !params.get("openid.assoc_handle")) {
    return false;
  }
  return true;
}

function parseIsValid(body: string): boolean {
  let valid = false;
  for (const line of body.split(/\r?\n/)) {
    const trimmed = line.trim();
    if (/^is_valid\s*:\s*true$/i.test(trimmed)) valid = true;
    if (/^is_valid\s*:\s*false$/i.test(trimmed)) return false;
  }
  return valid;
}

/**
 * OpenID 2.0 §11.4.2 direct verification against Steam's OP.
 * Test env accepts `openid.sig=test-valid` after the same field checks, so
 * worker tests do not call steamcommunity.com.
 */
export async function verifySteamAssertion(
  requestUrl: URL,
  expectedReturnTo: string,
  env: { ENVIRONMENT: string },
  fetchImpl: FetchLike = fetch,
): Promise<string> {
  const params = openidParamsFrom(requestUrl);
  if (!assertionLooksLikeSteam(params, expectedReturnTo)) {
    throw new ApiError(400, ErrorCode.LINK_INVALID, "Steam did not return a valid assertion.");
  }
  const steamId = steamClaimedId(params.get("openid.claimed_id"));
  if (!steamId) {
    throw new ApiError(400, ErrorCode.LINK_INVALID, "Steam did not return a valid assertion.");
  }

  if (isTestEnv(env) && params.get("openid.sig") === "test-valid") {
    return steamId;
  }

  const verify = new URLSearchParams(params);
  verify.set("openid.mode", "check_authentication");
  let response: Response;
  try {
    response = await fetchImpl(STEAM_OPENID_ENDPOINT, {
      method: "POST",
      headers: { "content-type": "application/x-www-form-urlencoded", accept: "text/plain" },
      body: verify.toString(),
      redirect: "error",
      signal: AbortSignal.timeout(LINK_VERIFY_TIMEOUT_MS),
    });
  } catch {
    throw new ApiError(400, ErrorCode.LINK_VERIFY_FAILED, "Steam could not verify that sign-in.");
  }
  const body = await response.text();
  if (!response.ok || !parseIsValid(body)) {
    throw new ApiError(400, ErrorCode.LINK_INVALID, "Steam did not return a valid assertion.");
  }
  return steamId;
}
