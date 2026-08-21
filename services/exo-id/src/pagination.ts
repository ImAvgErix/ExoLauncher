import { ApiError, ErrorCode } from "./errors.ts";

export const DEFAULT_PAGE_LIMIT = 20;
export const MAX_PAGE_LIMIT = 50;

type CursorTuple = [version: 1, scope: string, key: string, tie: string];

function invalidCursor(): never {
  throw new ApiError(400, ErrorCode.INVALID_REQUEST, "cursor is not valid for this request.");
}

function encodeBase64Url(value: string): string {
  const bytes = new TextEncoder().encode(value);
  let binary = "";
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary).replaceAll("+", "-").replaceAll("/", "_").replaceAll("=", "");
}

function decodeBase64Url(value: string): string {
  if (!/^[A-Za-z0-9_-]+$/.test(value) || value.length > 1024) invalidCursor();
  const padding = "=".repeat((4 - (value.length % 4)) % 4);
  let binary: string;
  try {
    binary = atob(value.replaceAll("-", "+").replaceAll("_", "/") + padding);
  } catch {
    invalidCursor();
  }
  const bytes = Uint8Array.from(binary, (character) => character.charCodeAt(0));
  try {
    return new TextDecoder("utf-8", { fatal: true, ignoreBOM: false }).decode(bytes);
  } catch {
    invalidCursor();
  }
}

export function parsePageLimit(raw: string | undefined, fallback = DEFAULT_PAGE_LIMIT): number {
  if (raw === undefined || raw === "") return fallback;
  if (!/^[1-9][0-9]*$/.test(raw)) {
    throw new ApiError(400, ErrorCode.INVALID_REQUEST, `limit must be between 1 and ${MAX_PAGE_LIMIT}.`);
  }
  const limit = Number(raw);
  if (!Number.isSafeInteger(limit) || limit < 1 || limit > MAX_PAGE_LIMIT) {
    throw new ApiError(400, ErrorCode.INVALID_REQUEST, `limit must be between 1 and ${MAX_PAGE_LIMIT}.`);
  }
  return limit;
}

export function encodeCursor(scope: string, key: string, tie = ""): string {
  return encodeBase64Url(JSON.stringify([1, scope, key, tie] satisfies CursorTuple));
}

export function decodeCursor(
  raw: string | undefined,
  scope: string,
): { key: string; tie: string } | null {
  if (raw === undefined || raw === "") return null;
  let parsed: unknown;
  try {
    parsed = JSON.parse(decodeBase64Url(raw));
  } catch (error) {
    if (error instanceof ApiError) throw error;
    invalidCursor();
  }
  if (
    !Array.isArray(parsed) ||
    parsed.length !== 4 ||
    parsed[0] !== 1 ||
    parsed[1] !== scope ||
    typeof parsed[2] !== "string" ||
    typeof parsed[3] !== "string" ||
    parsed[2].length === 0 ||
    parsed[2].length > 256 ||
    parsed[3].length > 256
  ) {
    invalidCursor();
  }
  const canonical = encodeBase64Url(JSON.stringify(parsed));
  if (canonical !== raw) invalidCursor();
  return { key: parsed[2], tie: parsed[3] };
}
