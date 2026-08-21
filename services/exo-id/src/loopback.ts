import { ApiError, ErrorCode } from "./errors.ts";

const CALLBACK_PATH = "/callback";

export type LoopbackRedirect = {
  href: string;
  origin: string;
  port: number;
  path: string;
};

function decodeHost(host: string): string {
  if (host.startsWith("[") && host.endsWith("]")) return host.slice(1, -1).toLowerCase();
  return host.toLowerCase();
}

/**
 * RFC 8252 §7.3: native apps redirect to the loopback interface.
 * Port is ephemeral, so we match scheme + host + path and ignore the port.
 * `localhost` is refused: it is DNS, not the loopback interface.
 */
export function parseLoopbackRedirect(raw: unknown): LoopbackRedirect {
  if (typeof raw !== "string" || raw.length === 0 || raw.length > 200) {
    throw new ApiError(400, ErrorCode.INVALID_REDIRECT_URI, "redirectUri must be an http loopback URL.");
  }
  let url: URL;
  try {
    url = new URL(raw);
  } catch {
    throw new ApiError(400, ErrorCode.INVALID_REDIRECT_URI, "redirectUri is not a valid URL.");
  }
  if (url.protocol !== "http:") {
    throw new ApiError(400, ErrorCode.INVALID_REDIRECT_URI, "redirectUri must use http on loopback.");
  }
  if (url.username || url.password) {
    throw new ApiError(400, ErrorCode.INVALID_REDIRECT_URI, "redirectUri must not include credentials.");
  }
  if (url.hash) {
    throw new ApiError(400, ErrorCode.INVALID_REDIRECT_URI, "redirectUri must not include a fragment.");
  }
  if (url.search) {
    throw new ApiError(400, ErrorCode.INVALID_REDIRECT_URI, "redirectUri must not include a query string.");
  }
  const host = decodeHost(url.hostname);
  if (host !== "127.0.0.1" && host !== "::1") {
    throw new ApiError(
      400,
      ErrorCode.INVALID_REDIRECT_URI,
      "redirectUri host must be 127.0.0.1 or [::1], not localhost.",
    );
  }
  const path = url.pathname.replace(/\/+$/, "") || "/";
  if (path !== CALLBACK_PATH) {
    throw new ApiError(400, ErrorCode.INVALID_REDIRECT_URI, "redirectUri path must be /callback.");
  }
  const port = url.port ? Number(url.port) : 80;
  if (!Number.isInteger(port) || port < 1 || port > 65535) {
    throw new ApiError(400, ErrorCode.INVALID_REDIRECT_URI, "redirectUri port is invalid.");
  }
  const hostname = host === "::1" ? "[::1]" : "127.0.0.1";
  const href = `http://${hostname}:${port}${CALLBACK_PATH}`;
  return { href, origin: `http://${hostname}:${port}`, port, path: CALLBACK_PATH };
}

export function loopbackCallbackUrl(redirectUri: string, params: Record<string, string>): string {
  const parsed = parseLoopbackRedirect(redirectUri);
  const url = new URL(parsed.href);
  for (const [key, value] of Object.entries(params)) url.searchParams.set(key, value);
  return url.toString();
}
