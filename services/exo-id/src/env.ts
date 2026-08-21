// Wrangler generates storage, Durable Object, and non-secret variable bindings.
// Provider secrets are intentionally optional so an absent Google/Resend setup
// disables only that capability; BETTER_AUTH_SECRET is the sole required secret.
export type Env = Cloudflare.Env & {
  BETTER_AUTH_SECRET: string;
  GOOGLE_CLIENT_SECRET?: string;
  RESEND_API_KEY?: string;
};

export const SESSION_TTL_SEC = 60 * 60 * 24 * 7;
export const LOGIN_TTL_SEC = 60 * 10;
export const AUTH_CODE_TTL_SEC = 60;
export const HANDLE_COOLDOWN_MS = 1000 * 60 * 60 * 24 * 30;
export const HANDLE_TOMBSTONE_MS = 1000 * 60 * 60 * 24 * 365;
export const STORE_LINK_PENDING_TTL_SEC = 60 * 10;
export const MATCH_CLAIM_TTL_MS = 1000 * 60 * 60 * 24 * 30;
export const MATCH_MAX_IDS = 200;
export const MATCH_RATE_MAX = 8;
export const MATCH_RATE_WINDOW_MS = 10 * 60 * 1000;
export const LINK_VERIFY_TIMEOUT_MS = 8000;
export const MAX_FIELD_CLOCK_SKEW_MS = 5 * 60 * 1000;

export function emailMagicLinkEnabled(env: {
  RESEND_API_KEY?: string;
  RESEND_FROM?: string;
}): boolean {
  return Boolean(env.RESEND_API_KEY && env.RESEND_FROM);
}

export function isTestEnv(env: { ENVIRONMENT: string }): boolean {
  return String(env.ENVIRONMENT) === "test";
}

export function isLocalEnv(env: { ENVIRONMENT: string }): boolean {
  const environment = String(env.ENVIRONMENT);
  return environment === "test" || environment === "development";
}
