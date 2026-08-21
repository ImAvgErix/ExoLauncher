import { Hono } from "hono";
import type { Context } from "hono";
import type { Env } from "../env.ts";
import { ApiError, ErrorCode, errorBody } from "../errors.ts";
import { canViewProfile } from "../policy.ts";
import { assertRateLimit, scopedRateKey } from "../rate-limit.ts";
import { requireSession, type Authed } from "../session.ts";
import {
  PROFILE_MEDIA_CACHE_CONTROL,
  PROFILE_MEDIA_CACHE_CONTROL_LEGACY,
  PROFILE_MEDIA_LIMITS,
  ProfileMediaError,
  cleanupProfileMediaForAccount,
  currentProfileMedia,
  deleteCurrentProfileMedia,
  inspectAndSanitizeProfileMedia,
  isProfileMediaKind,
  profileMediaRecordHasOwnedKey,
  publicProfileMedia,
  readBoundedMediaBody,
  replaceProfileMedia,
  type ProfileMediaKind,
  type ProfileMediaRow,
} from "../media.ts";

export type ProfileMediaBindings = Pick<
  Env,
  | "DB"
  | "PROFILE_MEDIA"
  | "BETTER_AUTH_SECRET"
  | "BETTER_AUTH_URL"
  | "GOOGLE_CLIENT_ID"
  | "GOOGLE_CLIENT_SECRET"
  | "RESEND_API_KEY"
  | "RESEND_FROM"
  | "ENVIRONMENT"
>;
type MediaContext = Context<{ Bindings: ProfileMediaBindings }>;
type SessionResolver = (context: MediaContext) => Promise<Pick<Authed, "userId">>;
type ViewerResolver = (context: MediaContext) => Promise<string | null>;
type VisibilityHook = (env: Pick<ProfileMediaBindings, "DB">, ownerId: string, viewerId: string | null) => Promise<boolean>;
type MutationRateLimitHook = (
  env: ProfileMediaBindings,
  userId: string,
  kind: ProfileMediaKind,
) => Promise<void>;

export type ProfileMediaRouteHooks = {
  requireUser?: SessionResolver;
  resolveViewer?: ViewerResolver;
  canView?: VisibilityHook;
  rateLimitMutation?: MutationRateLimitHook;
};

function mediaErrorResponse(error: ProfileMediaError): Response {
  return Response.json({ error: { code: error.code, message: error.message } }, { status: error.status });
}

function notFound(): Response {
  const error = new ApiError(404, ErrorCode.NOT_FOUND, "Not found.");
  return Response.json(errorBody(error), { status: 404 });
}

function mediaKind(value: string): ProfileMediaKind {
  if (isProfileMediaKind(value)) return value;
  throw new ProfileMediaError(400, "MEDIA_INVALID", "Media kind must be avatar, banner, or gallery slot.");
}

function validServePath(userId: string, version: string): boolean {
  return /^[A-Za-z0-9_-]{1,128}$/.test(userId) && /^[a-f0-9]{64}$/.test(version);
}

async function defaultRequireUser(context: MediaContext): Promise<Pick<Authed, "userId">> {
  // The media router narrows its declared bindings, but requireSession only
  // reads the auth bindings included above. Reuse the shared guard so custom
  // media mutations require an explicit bearer and never accept browser cookies.
  return requireSession(context as unknown as Context<{ Bindings: Env }>);
}

async function defaultResolveViewer(context: MediaContext): Promise<string | null> {
  if (!context.req.header("authorization")) return null;
  return (await defaultRequireUser(context)).userId;
}

async function defaultMutationRateLimit(
  env: ProfileMediaBindings,
  userId: string,
  kind: ProfileMediaKind,
): Promise<void> {
  await assertRateLimit(
    env.DB,
    await scopedRateKey(env.BETTER_AUTH_SECRET, `profile-media-${kind}`, userId),
    { windowMs: 10 * 60 * 1000, max: 10 },
  );
}

type ByteRange = { offset: number; length: number };

function parseRange(value: string | undefined, size: number): ByteRange | null | "invalid" {
  if (!value) return null;
  if (!value.startsWith("bytes=") || value.includes(",")) return "invalid";
  const match = /^bytes=([0-9]*)-([0-9]*)$/.exec(value);
  if (!match || (!match[1] && !match[2])) return "invalid";
  if (!match[1]) {
    const suffix = Number(match[2]);
    if (!Number.isSafeInteger(suffix) || suffix <= 0) return "invalid";
    const length = Math.min(suffix, size);
    return { offset: size - length, length };
  }
  const start = Number(match[1]);
  if (!Number.isSafeInteger(start) || start >= size) return "invalid";
  if (!match[2]) return { offset: start, length: size - start };
  const requestedEnd = Number(match[2]);
  if (!Number.isSafeInteger(requestedEnd) || requestedEnd < start) return "invalid";
  const end = Math.min(requestedEnd, size - 1);
  return { offset: start, length: end - start + 1 };
}

function etagFor(row: ProfileMediaRow): string {
  return `"sha256-${row.sha256}"`;
}

function noneMatch(value: string | undefined, etag: string): boolean {
  if (!value) return false;
  return value.split(",").some((candidate) => {
    const normalized = candidate.trim().replace(/^W\//, "");
    return normalized === "*" || normalized === etag;
  });
}

function baseHeaders(row: ProfileMediaRow, etag: string, anonymousPublic: boolean): Headers {
  const headers = new Headers({
    "Accept-Ranges": "bytes",
    "Cache-Control": anonymousPublic ? PROFILE_MEDIA_CACHE_CONTROL : "private, no-store",
    "Content-Length": String(row.byte_size),
    "Content-Type": row.content_type,
    ETag: etag,
    Vary: "Authorization",
    "X-Content-Type-Options": "nosniff",
  });
  return headers;
}

function objectMatchesRow(object: R2Object, row: ProfileMediaRow): boolean {
  return (
    object.key === row.object_key &&
    object.size === row.byte_size &&
    object.httpMetadata?.contentType === row.content_type &&
    (object.httpMetadata.cacheControl === PROFILE_MEDIA_CACHE_CONTROL ||
      object.httpMetadata.cacheControl === PROFILE_MEDIA_CACHE_CONTROL_LEGACY) &&
    object.customMetadata?.kind === row.kind &&
    object.customMetadata.sha256 === row.sha256 &&
    object.customMetadata.width === String(row.width) &&
    object.customMetadata.height === String(row.height)
  );
}

export function createProfileMediaRoutes(hooks: ProfileMediaRouteHooks = {}): Hono<{ Bindings: ProfileMediaBindings }> {
  const routes = new Hono<{ Bindings: ProfileMediaBindings }>();
  const requireUser = hooks.requireUser ?? defaultRequireUser;
  const resolveViewer = hooks.resolveViewer ?? defaultResolveViewer;
  const canView = hooks.canView ?? canViewProfile;
  const rateLimitMutation = hooks.rateLimitMutation ?? defaultMutationRateLimit;

  routes.put("/v1/profile/media/:kind", async (context) => {
    try {
      const session = await requireUser(context);
      const kind = mediaKind(context.req.param("kind"));
      await rateLimitMutation(context.env, session.userId, kind);
      const raw = await readBoundedMediaBody(
        context.req.raw.body,
        PROFILE_MEDIA_LIMITS[kind],
        context.req.header("content-length") ?? null,
      );
      const media = await inspectAndSanitizeProfileMedia(kind, context.req.header("content-type") ?? "", raw);
      const stored = await replaceProfileMedia(context.env.DB, context.env.PROFILE_MEDIA, session.userId, kind, media);
      return context.json({ media: publicProfileMedia(stored.row) });
    } catch (error) {
      if (error instanceof ProfileMediaError) return mediaErrorResponse(error);
      throw error;
    }
  });

  routes.delete("/v1/profile/media/:kind", async (context) => {
    try {
      const session = await requireUser(context);
      const kind = mediaKind(context.req.param("kind"));
      await rateLimitMutation(context.env, session.userId, kind);
      await deleteCurrentProfileMedia(context.env.DB, context.env.PROFILE_MEDIA, session.userId, kind);
      return context.json({ ok: true });
    } catch (error) {
      if (error instanceof ProfileMediaError) return mediaErrorResponse(error);
      throw error;
    }
  });

  async function serve(context: MediaContext): Promise<Response> {
    const userId = context.req.param("userId") ?? "";
    const version = context.req.param("version") ?? "";
    let kind: ProfileMediaKind;
    try {
      kind = mediaKind(context.req.param("kind") ?? "");
    } catch {
      return notFound();
    }
    if (!validServePath(userId, version)) return notFound();
    const row = await currentProfileMedia(context.env.DB, userId, kind, version);
    if (!row || !profileMediaRecordHasOwnedKey(row)) return notFound();
    const viewerId = await resolveViewer(context);
    if (!(await canView({ DB: context.env.DB }, userId, viewerId))) return notFound();
    const object = await context.env.PROFILE_MEDIA.head(row.object_key);
    if (!object || !objectMatchesRow(object, row)) return notFound();

    const etag = etagFor(row);
    const headers = baseHeaders(row, etag, viewerId === null);
    const ifMatch = context.req.header("if-match");
    if (ifMatch && ifMatch !== "*" && !ifMatch.split(",").some((candidate) => candidate.trim() === etag)) {
      headers.delete("Content-Length");
      return new Response(null, { status: 412, headers });
    }
    if (noneMatch(context.req.header("if-none-match"), etag)) {
      headers.delete("Content-Length");
      return new Response(null, { status: 304, headers });
    }

    let range = parseRange(context.req.header("range"), row.byte_size);
    if (range !== null && range !== "invalid") {
      const ifRange = context.req.header("if-range");
      if (ifRange && ifRange !== etag) range = null;
    }
    if (range === "invalid") {
      headers.set("Content-Range", `bytes */${row.byte_size}`);
      headers.set("Content-Length", "0");
      return new Response(null, { status: 416, headers });
    }
    if (range) {
      headers.set("Content-Length", String(range.length));
      headers.set("Content-Range", `bytes ${range.offset}-${range.offset + range.length - 1}/${row.byte_size}`);
    }
    const status = range ? 206 : 200;
    if (context.req.method === "HEAD") return new Response(null, { status, headers });
    const body = await context.env.PROFILE_MEDIA.get(row.object_key, range ? { range } : undefined);
    if (!body || !("body" in body)) return notFound();
    return new Response(body.body, { status, headers });
  }

  routes.get("/v1/media/:userId/:kind/:version", serve);
  routes.on("HEAD", "/v1/media/:userId/:kind/:version", serve);
  return routes;
}

export const mediaRoutes = createProfileMediaRoutes();
export { cleanupProfileMediaForAccount };
