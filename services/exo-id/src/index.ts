import { Hono } from "hono";
import type { Env } from "./env.ts";
import { ApiError, ErrorCode, errorBody } from "./errors.ts";
import { createAuth } from "./auth.ts";
import { emailMagicLinkEnabled } from "./env.ts";
import { rejectUnverifiedPasswordMagicLink } from "./magic-link-guard.ts";
import { logError } from "./log.ts";
import { authRoutes } from "./routes/auth.ts";
import { handleRoutes } from "./routes/handle.ts";
import { profileRoutes } from "./routes/profile.ts";
import { syncRoutes } from "./routes/sync.ts";
import { meRoutes } from "./routes/me.ts";
import { linksRoutes } from "./routes/links.ts";
import { profilesRoutes } from "./routes/profiles.ts";
import { socialRoutes } from "./routes/social.ts";
import { presenceRoutes } from "./routes/presence.ts";
import { mediaRoutes } from "./routes/media.ts";
import { adminBadgeRoutes } from "./routes/admin-badges.ts";
import { cleanupExpiredRecords } from "./maintenance.ts";
import { normalizePasswordAuthResponse, preparePasswordAuthRequest } from "./password-auth.ts";

export { PresenceDurableObject } from "./presence-do.ts";

const app = new Hono<{ Bindings: Env }>();

app.use("*", async (c, next) => {
  await next();
  c.header("X-Content-Type-Options", "nosniff");
  c.header("Referrer-Policy", "no-referrer");
  c.header("X-Frame-Options", "DENY");
  if (!c.res.headers.has("cache-control")) c.header("Cache-Control", "no-store");
});

app.onError((err, c) => {
  if (err instanceof ApiError) {
    const headers = new Headers({ "content-type": "application/json" });
    if (err.retryAfterSec) headers.set("Retry-After", String(err.retryAfterSec));
    return new Response(JSON.stringify(errorBody(err)), { status: err.status, headers });
  }
  logError("unhandled", { message: err instanceof Error ? err.message : "error" });
  return new Response(JSON.stringify(errorBody(new ApiError(500, ErrorCode.INTERNAL, "Internal error."))), {
    status: 500,
    headers: { "content-type": "application/json" },
  });
});

app.get("/v1/health", (c) => c.json({
  ok: true,
  service: "exo-id",
  capabilities: {
    providers: {
      google: Boolean(c.env.GOOGLE_CLIENT_ID && c.env.GOOGLE_CLIENT_SECRET),
      email: emailMagicLinkEnabled(c.env),
      password: true,
    },
    profiles: true,
    friends: true,
    media: true,
    presence: true,
  },
}));

app.route("/", authRoutes);
app.route("/", handleRoutes);
app.route("/", profileRoutes);
app.route("/", syncRoutes);
app.route("/", meRoutes);
app.route("/", linksRoutes);
app.route("/", profilesRoutes);
app.route("/", socialRoutes);
app.route("/", presenceRoutes);
app.route("/", mediaRoutes);
app.route("/", adminBadgeRoutes);

app.all("/api/auth/*", async (c) => {
  const path = new URL(c.req.url).pathname;
  const magicLinkVerify = c.req.method === "GET" && path === "/api/auth/magic-link/verify";
  const browserCallback = c.req.method === "GET" && (
    path === "/api/auth/callback/google" ||
    magicLinkVerify
  );
  const passwordRequest = c.req.method === "POST" && (
    path === "/api/auth/sign-up/email" ||
    path === "/api/auth/sign-in/email"
  );
  if (!browserCallback && !passwordRequest) {
    return c.json(errorBody(new ApiError(404, ErrorCode.NOT_FOUND, "Not found.")), 404);
  }
  if (magicLinkVerify) {
    const blocked = await rejectUnverifiedPasswordMagicLink(c.env, c.req.raw);
    if (blocked) return blocked;
  }
  const request = passwordRequest ? await preparePasswordAuthRequest(c.req.raw, path) : c.req.raw;
  const auth = createAuth(c.env);
  const response = await auth.handler(request);
  return passwordRequest ? normalizePasswordAuthResponse(path, response) : response;
});

app.notFound((c) => c.json(errorBody(new ApiError(404, ErrorCode.NOT_FOUND, "Not found.")), 404));

export default {
  async fetch(request: Request, env: Env, ctx: ExecutionContext): Promise<Response> {
    return await app.fetch(request, env, ctx);
  },
  scheduled(_event: ScheduledController, env: Env, ctx: ExecutionContext): void {
    ctx.waitUntil(
      cleanupExpiredRecords(env).catch((error) => {
        logError("scheduled metadata cleanup failed", {
          error: error instanceof Error ? error.message : "error",
        });
      }),
    );
  },
} satisfies ExportedHandler<Env>;
