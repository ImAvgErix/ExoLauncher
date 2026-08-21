import { ApiError, ErrorCode, errorBody } from "./errors.ts";

const MAX_PASSWORD_BODY_BYTES = 2048;
const DISPLAY_NAME_MAX = 80;
const EMAIL_MAX = 254;
const PASSWORD_MIN = 12;
const PASSWORD_MAX = 128;
const CONTROL_CHARACTER = /[\u0000-\u001f\u007f-\u009f]/u;
const EMAIL = /^[^\s@]+@[^\s@]+\.[^\s@]+$/u;

function invalidRequest(): ApiError {
  return new ApiError(400, ErrorCode.INVALID_REQUEST, "Invalid account request.");
}

function invalidPassword(): ApiError {
  return new ApiError(
    400,
    ErrorCode.INVALID_PASSWORD,
    `Password must be between ${PASSWORD_MIN} and ${PASSWORD_MAX} characters.`,
  );
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return Boolean(value) && typeof value === "object" && !Array.isArray(value);
}

async function readBoundedJson(request: Request): Promise<Record<string, unknown>> {
  const contentType = request.headers.get("content-type")?.split(";", 1)[0]?.trim().toLowerCase();
  if (contentType !== "application/json") throw invalidRequest();

  const declaredLength = Number(request.headers.get("content-length"));
  if (Number.isFinite(declaredLength) && declaredLength > MAX_PASSWORD_BODY_BYTES) throw invalidRequest();
  if (!request.body) throw invalidRequest();

  const reader = request.body.getReader();
  const chunks: Uint8Array[] = [];
  let total = 0;
  try {
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      total += value.byteLength;
      if (total > MAX_PASSWORD_BODY_BYTES) {
        await reader.cancel();
        throw invalidRequest();
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
    throw invalidRequest();
  }
  if (!isRecord(value)) throw invalidRequest();
  return value;
}

function hasOnlyKeys(body: Record<string, unknown>, allowed: readonly string[]): boolean {
  const keys = Object.keys(body);
  return keys.length === allowed.length && keys.every((key) => allowed.includes(key));
}

export async function preparePasswordAuthRequest(request: Request, path: string): Promise<Request> {
  const body = await readBoundedJson(request);
  const signUp = path === "/api/auth/sign-up/email";
  const allowed = signUp ? ["name", "email", "password"] as const : ["email", "password"] as const;
  if (!hasOnlyKeys(body, allowed)) throw invalidRequest();

  if (typeof body.email !== "string") throw invalidRequest();
  const email = body.email.trim().toLowerCase();
  if (!email || email.length > EMAIL_MAX || !EMAIL.test(email)) throw invalidRequest();

  if (typeof body.password !== "string") throw invalidPassword();
  if (body.password.length < PASSWORD_MIN || body.password.length > PASSWORD_MAX) throw invalidPassword();

  let name: string | undefined;
  if (signUp) {
    if (typeof body.name !== "string") throw invalidRequest();
    name = body.name.trim();
    if (!name || [...name].length > DISPLAY_NAME_MAX || CONTROL_CHARACTER.test(name)) throw invalidRequest();
  }

  const headers = new Headers(request.headers);
  headers.set("content-type", "application/json");
  headers.delete("authorization");
  headers.delete("cookie");
  headers.delete("content-length");
  return new Request(request.url, {
    method: "POST",
    headers,
    body: JSON.stringify(signUp ? { name, email, password: body.password } : { email, password: body.password }),
  });
}

function jsonError(error: ApiError): Response {
  const headers = new Headers({ "content-type": "application/json" });
  if (error.retryAfterSec) headers.set("Retry-After", String(error.retryAfterSec));
  return new Response(JSON.stringify(errorBody(error)), {
    status: error.status,
    headers,
  });
}

function genericSignUpSuccess(): Response {
  return new Response(JSON.stringify({ ok: true }), {
    status: 200,
    headers: { "content-type": "application/json" },
  });
}

export async function normalizePasswordAuthResponse(path: string, response: Response): Promise<Response> {
  if (response.ok) {
    if (path === "/api/auth/sign-up/email") return genericSignUpSuccess();

    const headers = new Headers(response.headers);
    headers.delete("set-cookie");
    let body: BodyInit | null = response.body;
    if (path === "/api/auth/sign-in/email") {
      let value: unknown;
      try {
        value = await response.json();
      } catch {
        return jsonError(new ApiError(500, ErrorCode.INTERNAL, "Internal error."));
      }
      if (!isRecord(value)) {
        return jsonError(new ApiError(500, ErrorCode.INTERNAL, "Internal error."));
      }
      const sanitized: Record<string, unknown> = {};
      for (const [key, field] of Object.entries(value)) {
        if (key !== "token") sanitized[key] = field;
      }
      headers.set("content-type", "application/json");
      headers.delete("content-length");
      body = JSON.stringify(sanitized);
    }
    return new Response(body, {
      status: response.status,
      statusText: response.statusText,
      headers,
    });
  }

  if (response.status === 429) {
    const retryAfter = Number(response.headers.get("retry-after") ?? response.headers.get("x-retry-after"));
    return jsonError(new ApiError(
      429,
      ErrorCode.RATE_LIMITED,
      "Too many attempts. Try again later.",
      Number.isFinite(retryAfter) && retryAfter > 0 ? Math.ceil(retryAfter) : 60,
    ));
  }

  let code = "";
  try {
    const body: unknown = await response.clone().json();
    code = isRecord(body) && typeof body.code === "string" ? body.code : "";
  } catch {
    // Better Auth errors should be JSON; an unrecognized body is handled generically below.
  }

  if (
    path === "/api/auth/sign-up/email" &&
    (code === "USER_ALREADY_EXISTS_USE_ANOTHER_EMAIL" || code === "USER_ALREADY_EXISTS")
  ) {
    return genericSignUpSuccess();
  }

  if (path === "/api/auth/sign-in/email" && response.status === 401) {
    return jsonError(new ApiError(401, ErrorCode.INVALID_CREDENTIALS, "Email or password is incorrect."));
  }

  if (code === "PASSWORD_TOO_SHORT" || code === "PASSWORD_TOO_LONG" || code === "INVALID_PASSWORD") {
    return jsonError(invalidPassword());
  }

  if (path === "/api/auth/sign-up/email" && (response.status === 409 || response.status === 422)) {
    return genericSignUpSuccess();
  }

  if (response.status >= 400 && response.status < 500) return jsonError(invalidRequest());

  return jsonError(new ApiError(500, ErrorCode.INTERNAL, "Internal error."));
}
