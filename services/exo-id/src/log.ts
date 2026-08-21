const SECRET_KEYS = new Set([
  "authorization",
  "cookie",
  "token",
  "accesstoken",
  "refreshtoken",
  "idtoken",
  "session",
  "sessionid",
  "sessiontoken",
  "code",
  "codeverifier",
  "codechallenge",
  "secret",
  "password",
  "currentpassword",
  "newpassword",
  "passwordconfirmation",
  "email",
  "set-auth-token",
  "set-cookie",
  "claimedid",
  "claimed_id",
  "steamid",
  "externalid",
  "external_id",
  "accountid",
  "account_id",
  "userid",
  "user_id",
  "ids",
].map((key) => key.toLowerCase().replaceAll("_", "").replaceAll("-", "")));

const EMAIL_RE = /[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}/gi;
const BEARER_RE = /Bearer\s+\S+/gi;

function redactString(value: string): string {
  return value.replace(EMAIL_RE, "[redacted]").replace(BEARER_RE, "Bearer [redacted]");
}

function redactUnknown(value: unknown, key?: string): unknown {
  if (key && SECRET_KEYS.has(key.toLowerCase().replaceAll("_", "").replaceAll("-", ""))) {
    return "[redacted]";
  }
  if (typeof value === "string") return redactString(value);
  if (Array.isArray(value)) return value.map((item) => redactUnknown(item));
  if (value && typeof value === "object") {
    const out: Record<string, unknown> = {};
    for (const [k, v] of Object.entries(value as Record<string, unknown>)) {
      out[k] = redactUnknown(v, k);
    }
    return out;
  }
  return value;
}

export function logInfo(message: string, extra?: Record<string, unknown>): void {
  console.log(JSON.stringify({
    level: "info",
    message: redactString(message),
    ...(extra ? { data: redactUnknown(extra) } : {}),
  }));
}

export function logError(message: string, extra?: Record<string, unknown>): void {
  console.error(JSON.stringify({
    level: "error",
    message: redactString(message),
    ...(extra ? { data: redactUnknown(extra) } : {}),
  }));
}
