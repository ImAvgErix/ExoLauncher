import { ApiError, ErrorCode } from "./errors.ts";
import { RESERVED_HANDLES } from "./reserved.ts";

export const HANDLE_MIN = 3;
export const HANDLE_MAX = 24;

export type HandleParse = {
  display: string;
  normalized: string;
  skeleton: string;
};

const ALLOWED = /^[A-Za-z0-9_]{3,24}$/;

export function handleSkeleton(normalized: string): string {
  let value = normalized.replaceAll("0", "o").replaceAll("1", "l");
  let prev = "";
  while (value !== prev) {
    prev = value;
    value = value.replaceAll("rn", "m");
  }
  return value;
}

export function parseHandle(raw: unknown): HandleParse {
  if (typeof raw !== "string") {
    throw new ApiError(400, ErrorCode.HANDLE_INVALID, "Handle must be a string.");
  }
  const trimmed = raw.trim();
  if (trimmed.length === 0) {
    throw new ApiError(400, ErrorCode.HANDLE_INVALID, "Handle is required.");
  }
  for (const ch of trimmed) {
    const code = ch.codePointAt(0) ?? 0;
    if (code > 127) {
      throw new ApiError(
        400,
        ErrorCode.HANDLE_CONFUSABLE,
        "Handle must be ASCII letters, digits, or underscore. Lookalike characters are refused.",
      );
    }
  }
  const nfkc = trimmed.normalize("NFKC");
  if (nfkc !== trimmed) {
    throw new ApiError(
      400,
      ErrorCode.HANDLE_CONFUSABLE,
      "Handle must be ASCII letters, digits, or underscore. Lookalike characters are refused.",
    );
  }
  if (!ALLOWED.test(trimmed)) {
    throw new ApiError(
      400,
      ErrorCode.HANDLE_INVALID,
      "Handle must be 3–24 characters: A–Z, a–z, 0–9, underscore.",
    );
  }
  if (!/[A-Za-z]/.test(trimmed)) {
    throw new ApiError(400, ErrorCode.HANDLE_INVALID, "Handle must include at least one letter.");
  }
  const normalized = trimmed.toLowerCase();
  const skeleton = handleSkeleton(normalized);
  if (RESERVED_HANDLES.includes(normalized) || RESERVED_HANDLES.includes(skeleton)) {
    throw new ApiError(400, ErrorCode.HANDLE_RESERVED, "That handle is reserved.");
  }
  return { display: trimmed, normalized, skeleton };
}

export function isReservedHandle(normalized: string, skeleton: string): boolean {
  return RESERVED_HANDLES.includes(normalized) || RESERVED_HANDLES.includes(skeleton);
}
