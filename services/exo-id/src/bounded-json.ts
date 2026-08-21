import { ApiError, ErrorCode } from "./errors.ts";

function invalidRequest(message: string): ApiError {
  return new ApiError(400, ErrorCode.INVALID_REQUEST, message);
}

export async function readBoundedJsonObject(
  request: Request,
  maxBytes: number,
  errorMessage: string,
): Promise<Record<string, unknown>> {
  const contentType = request.headers.get("content-type")?.split(";", 1)[0]?.trim().toLowerCase();
  if (contentType !== "application/json") throw invalidRequest(errorMessage);

  const declaredHeader = request.headers.get("content-length");
  if (declaredHeader !== null) {
    const declaredLength = Number(declaredHeader);
    if (!/^\d+$/u.test(declaredHeader.trim()) ||
      !Number.isSafeInteger(declaredLength) ||
      declaredLength > maxBytes) {
      throw invalidRequest(errorMessage);
    }
  }
  if (!request.body) throw invalidRequest(errorMessage);

  const reader = request.body.getReader();
  const chunks: Uint8Array[] = [];
  let total = 0;
  try {
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      total += value.byteLength;
      if (total > maxBytes) {
        await reader.cancel().catch(() => undefined);
        throw invalidRequest(errorMessage);
      }
      chunks.push(value);
    }
  } finally {
    reader.releaseLock();
  }

  const bytes = new Uint8Array(total);
  let offset = 0;
  for (const chunk of chunks) {
    bytes.set(chunk, offset);
    offset += chunk.byteLength;
  }

  let value: unknown;
  try {
    value = JSON.parse(new TextDecoder("utf-8", { fatal: true, ignoreBOM: false }).decode(bytes));
  } catch {
    throw invalidRequest(errorMessage);
  }
  if (!value || typeof value !== "object" || Array.isArray(value)) {
    throw invalidRequest(errorMessage);
  }
  return value as Record<string, unknown>;
}

export function hasExactKeys(
  value: Record<string, unknown>,
  required: readonly string[],
  optional: readonly string[] = [],
): boolean {
  const keys = Object.keys(value);
  const allowed = new Set([...required, ...optional]);
  return required.every((key) => Object.hasOwn(value, key)) &&
    keys.every((key) => allowed.has(key));
}

export async function readExactJsonObject(
  request: Request,
  maxBytes: number,
  required: readonly string[],
  optional: readonly string[],
  errorMessage: string,
): Promise<Record<string, unknown>> {
  const body = await readBoundedJsonObject(request, maxBytes, errorMessage);
  if (!hasExactKeys(body, required, optional)) throw invalidRequest(errorMessage);
  return body;
}
