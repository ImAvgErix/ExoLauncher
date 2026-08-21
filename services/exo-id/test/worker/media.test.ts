import { env } from "cloudflare:test";
import { describe, expect, it } from "vitest";
import {
  PROFILE_MEDIA_CACHE_CONTROL,
  cleanupProfileMediaForAccount,
  currentProfileMedia,
  getProfileMediaProjection,
  inspectAndSanitizeProfileMedia,
  replaceProfileMedia,
} from "../../src/media.ts";
import { createProfileMediaRoutes, type ProfileMediaBindings } from "../../src/routes/media.ts";
import { api, seedUser } from "./helpers.ts";

const encoder = new TextEncoder();

function join(...parts: Uint8Array[]): Uint8Array {
  const out = new Uint8Array(parts.reduce((length, part) => length + part.length, 0));
  let offset = 0;
  for (const part of parts) {
    out.set(part, offset);
    offset += part.length;
  }
  return out;
}

function be32(value: number): Uint8Array {
  return new Uint8Array([(value >>> 24) & 0xff, (value >>> 16) & 0xff, (value >>> 8) & 0xff, value & 0xff]);
}

function crc32(bytes: Uint8Array): number {
  let crc = 0xffffffff;
  for (const byte of bytes) {
    crc ^= byte;
    for (let bit = 0; bit < 8; bit++) crc = (crc >>> 1) ^ (crc & 1 ? 0xedb88320 : 0);
  }
  return (crc ^ 0xffffffff) >>> 0;
}

function chunk(type: string, data: Uint8Array): Uint8Array {
  const name = encoder.encode(type);
  return join(be32(data.length), name, data, be32(crc32(join(name, data))));
}

function png(width: number, height: number): Uint8Array {
  const header = new Uint8Array(13);
  header.set(be32(width), 0);
  header.set(be32(height), 4);
  header.set([8, 6, 0, 0, 0], 8);
  return join(
    new Uint8Array([137, 80, 78, 71, 13, 10, 26, 10]),
    chunk("IHDR", header),
    chunk("IDAT", new Uint8Array([0x78, 0x9c, 0x03, 0, 0, 0, 0, 1])),
    chunk("IEND", new Uint8Array()),
  );
}

type Stored = {
  key: string;
  bytes: Uint8Array;
  httpMetadata: R2HTTPMetadata;
  customMetadata: Record<string, string>;
};

class FakeR2Bucket implements R2Bucket {
  readonly objects = new Map<string, Stored>();
  readonly deleted: string[] = [];
  failPuts = false;
  readonly failDeletes = new Set<string>();

  async head(key: string): Promise<R2Object | null> {
    const stored = this.objects.get(key);
    return stored ? this.object(stored) : null;
  }

  async get(key: string, options?: R2GetOptions): Promise<R2ObjectBody | null> {
    const stored = this.objects.get(key);
    if (!stored) return null;
    const range = options?.range && !(options.range instanceof Headers) ? options.range : undefined;
    return this.object(stored, range);
  }

  async put(
    key: string,
    value: ReadableStream | ArrayBuffer | ArrayBufferView | string | null | Blob,
    options?: R2PutOptions,
  ): Promise<R2Object> {
    if (this.failPuts) throw new Error("R2 put failed");
    let bytes: Uint8Array;
    if (typeof value === "string") bytes = encoder.encode(value);
    else if (value === null) bytes = new Uint8Array();
    else if (value instanceof ArrayBuffer) bytes = new Uint8Array(value.slice(0));
    else if (ArrayBuffer.isView(value)) bytes = new Uint8Array(value.buffer, value.byteOffset, value.byteLength).slice();
    else if (value instanceof Blob) bytes = new Uint8Array(await value.arrayBuffer());
    else throw new Error("stream puts are not used by these tests");
    const httpMetadata = options?.httpMetadata instanceof Headers ? {} : (options?.httpMetadata ?? {});
    const stored = { key, bytes, httpMetadata, customMetadata: options?.customMetadata ?? {} };
    this.objects.set(key, stored);
    return this.object(stored);
  }

  async delete(keys: string | string[]): Promise<void> {
    for (const key of typeof keys === "string" ? [keys] : keys) {
      if (this.failDeletes.has(key)) throw new Error("R2 delete failed");
      this.deleted.push(key);
      this.objects.delete(key);
    }
  }

  async list(options: R2ListOptions = {}): Promise<R2Objects> {
    const objects = [...this.objects.values()]
      .filter((stored) => !options.prefix || stored.key.startsWith(options.prefix))
      .sort((a, b) => a.key.localeCompare(b.key))
      .map((stored) => this.object(stored));
    return { objects, delimitedPrefixes: [], truncated: false };
  }

  async createMultipartUpload(): Promise<R2MultipartUpload> {
    throw new Error("not implemented");
  }

  resumeMultipartUpload(): R2MultipartUpload {
    throw new Error("not implemented");
  }

  private object(stored: Stored, range?: R2Range): R2ObjectBody {
    let bodyBytes = stored.bytes;
    if (range && "offset" in range) {
      const offset = range.offset ?? 0;
      bodyBytes = stored.bytes.slice(offset, offset + (range.length ?? stored.bytes.length - offset));
    } else if (range && "suffix" in range) {
      bodyBytes = stored.bytes.slice(Math.max(0, stored.bytes.length - range.suffix));
    }
    const stream = () => new Response(bodyBytes).body!;
    return {
      key: stored.key,
      version: "fake-version",
      size: stored.bytes.length,
      etag: "fake-etag",
      httpEtag: '"fake-etag"',
      checksums: { toJSON: () => ({}) },
      uploaded: new Date("2026-08-19T00:00:00.000Z"),
      httpMetadata: stored.httpMetadata,
      customMetadata: stored.customMetadata,
      range,
      storageClass: "Standard",
      writeHttpMetadata(headers) {
        if (stored.httpMetadata.contentType) headers.set("Content-Type", stored.httpMetadata.contentType);
        if (stored.httpMetadata.cacheControl) headers.set("Cache-Control", stored.httpMetadata.cacheControl);
      },
      body: stream(),
      bodyUsed: false,
      arrayBuffer: async () => bodyBytes.slice().buffer,
      bytes: async () => bodyBytes.slice(),
      text: async () => new TextDecoder().decode(bodyBytes),
      json: async <T>() => Promise.reject<T>(new Error("not implemented")),
      blob: async () => new Blob([bodyBytes]),
    };
  }
}

function bindings(bucket: R2Bucket): ProfileMediaBindings {
  return {
    DB: env.DB,
    PROFILE_MEDIA: bucket,
    BETTER_AUTH_SECRET: env.BETTER_AUTH_SECRET,
    BETTER_AUTH_URL: env.BETTER_AUTH_URL,
    GOOGLE_CLIENT_ID: env.GOOGLE_CLIENT_ID,
    GOOGLE_CLIENT_SECRET: env.GOOGLE_CLIENT_SECRET,
    RESEND_API_KEY: env.RESEND_API_KEY,
    RESEND_FROM: env.RESEND_FROM,
    ENVIRONMENT: env.ENVIRONMENT,
  };
}

describe("profile media worker lifecycle", () => {
  it("requires an explicit bearer for upload and delete while rejecting cookie-only sessions", async () => {
    const owner = await seedUser("media-bearer-boundary@example.test");
    const image = png(256, 256);
    const cookie = `better-auth.session_token=${owner.token}`;

    const cookieUpload = await api("/v1/profile/media/avatar", {
      method: "PUT",
      headers: { cookie, "content-type": "image/png" },
      body: image,
    });
    expect(cookieUpload.status).toBe(401);
    expect((await cookieUpload.json<{ error: { code: string } }>()).error.code).toBe("UNAUTHENTICATED");
    expect(await currentProfileMedia(env.DB, owner.id, "avatar")).toBeNull();

    const bearerUpload = await api("/v1/profile/media/avatar", {
      method: "PUT",
      headers: { authorization: `Bearer ${owner.token}`, "content-type": "image/png" },
      body: image,
    });
    expect(bearerUpload.status).toBe(200);
    expect(await currentProfileMedia(env.DB, owner.id, "avatar")).not.toBeNull();

    const cookieDelete = await api("/v1/profile/media/avatar", {
      method: "DELETE",
      headers: { cookie },
    });
    expect(cookieDelete.status).toBe(401);
    expect((await cookieDelete.json<{ error: { code: string } }>()).error.code).toBe("UNAUTHENTICATED");
    expect(await currentProfileMedia(env.DB, owner.id, "avatar")).not.toBeNull();

    const bearerDelete = await api("/v1/profile/media/avatar", {
      method: "DELETE",
      headers: { authorization: `Bearer ${owner.token}` },
    });
    expect(bearerDelete.status).toBe(200);
    expect(await currentProfileMedia(env.DB, owner.id, "avatar")).toBeNull();
  });

  it("does not downgrade an invalid bearer to anonymous public media access", async () => {
    const owner = await seedUser("media-invalid-bearer@example.test");
    await env.DB.prepare(
      `INSERT INTO profile_privacy
        (user_id, profile_visibility, searchable, request_policy, activity_visibility, updated_at)
       VALUES (?, 'public', 0, 'anyone', 'friends', ?)`,
    )
      .bind(owner.id, new Date().toISOString())
      .run();
    const media = await inspectAndSanitizeProfileMedia("avatar", "image/png", png(256, 256));
    const stored = await replaceProfileMedia(env.DB, env.PROFILE_MEDIA, owner.id, "avatar", media);
    const response = await api(
      `/v1/media/${owner.id}/avatar/${stored.row.version}`,
      { headers: { authorization: "Bearer invalid-session" } },
    );
    expect(response.status).toBe(401);
    await cleanupProfileMediaForAccount(env.DB, env.PROFILE_MEDIA, owner.id);
  });

  it("replaces the current mapping before deleting the old immutable object", async () => {
    const user = await seedUser("media-replace@example.test");
    const bucket = new FakeR2Bucket();
    const firstMedia = await inspectAndSanitizeProfileMedia("avatar", "image/png", png(256, 256));
    const first = await replaceProfileMedia(env.DB, bucket, user.id, "avatar", firstMedia, {
      version: "a".repeat(64),
      now: "2026-08-19T01:00:00.000Z",
    });
    const secondMedia = await inspectAndSanitizeProfileMedia("avatar", "image/png", png(512, 512));
    const second = await replaceProfileMedia(env.DB, bucket, user.id, "avatar", secondMedia, {
      version: "b".repeat(64),
      now: "2026-08-19T02:00:00.000Z",
    });

    expect((await currentProfileMedia(env.DB, user.id, "avatar"))?.version).toBe("b".repeat(64));
    expect(bucket.objects.has(first.row.object_key)).toBe(false);
    expect(bucket.objects.has(second.row.object_key)).toBe(true);
    expect(bucket.deleted).toContain(first.row.object_key);
    const projection = await getProfileMediaProjection(env.DB, user.id);
    expect(projection.avatar).toMatchObject({
      kind: "avatar",
      version: "b".repeat(64),
      url: `/v1/media/${user.id}/avatar/${"b".repeat(64)}`,
    });
    expect(projection.banner).toBeNull();
    expect(JSON.stringify(projection)).not.toContain("object_key");
  });

  it("deletes the newly uploaded object when the D1 mapping cannot be written", async () => {
    const bucket = new FakeR2Bucket();
    const media = await inspectAndSanitizeProfileMedia("avatar", "image/png", png(256, 256));

    await expect(
      replaceProfileMedia(env.DB, bucket, "missing_user", "avatar", media, { version: "c".repeat(64) }),
    ).rejects.toThrow();
    expect(bucket.objects.size).toBe(0);
    expect(bucket.deleted).toHaveLength(1);
  });

  it("does not advance D1 when R2 put fails and reports an old-object cleanup failure without rolling back", async () => {
    const user = await seedUser("media-failures@example.test");
    const media = await inspectAndSanitizeProfileMedia("avatar", "image/png", png(256, 256));
    const failedBucket = new FakeR2Bucket();
    failedBucket.failPuts = true;
    await expect(
      replaceProfileMedia(env.DB, failedBucket, user.id, "avatar", media, { version: "3".repeat(64) }),
    ).rejects.toThrow("R2 put failed");
    expect(await currentProfileMedia(env.DB, user.id, "avatar")).toBeNull();

    const bucket = new FakeR2Bucket();
    const first = await replaceProfileMedia(env.DB, bucket, user.id, "avatar", media, { version: "4".repeat(64) });
    bucket.failDeletes.add(first.row.object_key);
    const second = await replaceProfileMedia(env.DB, bucket, user.id, "avatar", media, { version: "5".repeat(64) });
    expect(second.cleanupPending).toBe(true);
    expect((await currentProfileMedia(env.DB, user.id, "avatar"))?.version).toBe("5".repeat(64));
    expect(bucket.objects.has(first.row.object_key)).toBe(true);
  });

  it("serves only the current version through profile policy and never publicly caches an authorized response", async () => {
    const owner = await seedUser("media-owner@example.test");
    const friend = await seedUser("media-friend@example.test");
    const bucket = new FakeR2Bucket();
    const media = await inspectAndSanitizeProfileMedia("avatar", "image/png", png(256, 256));
    const stored = await replaceProfileMedia(env.DB, bucket, owner.id, "avatar", media, { version: "d".repeat(64) });
    const pair = [owner.id, friend.id].sort();
    await env.DB.prepare(`INSERT INTO direct_friendship (user_low, user_high, created_at) VALUES (?, ?, ?)`)
      .bind(pair[0], pair[1], new Date().toISOString())
      .run();
    let viewer: string | null = friend.id;
    const routes = createProfileMediaRoutes({
      resolveViewer: async () => viewer,
      requireUser: async () => ({ userId: owner.id }),
      rateLimitMutation: async () => {},
    });
    const url = `http://exo.test${stored.row ? `/v1/media/${owner.id}/avatar/${stored.row.version}` : ""}`;

    const friendResponse = await routes.request(url, {}, bindings(bucket));
    expect(friendResponse.status).toBe(200);
    expect(friendResponse.headers.get("Cache-Control")).toBe("private, no-store");
    expect(friendResponse.headers.get("Vary")).toBe("Authorization");

    await env.DB.prepare(`INSERT INTO user_block (blocker_id, blocked_id, created_at) VALUES (?, ?, ?)`)
      .bind(owner.id, friend.id, new Date().toISOString())
      .run();
    expect((await routes.request(url, {}, bindings(bucket))).status).toBe(404);
    await env.DB.prepare(`DELETE FROM user_block WHERE blocker_id = ? AND blocked_id = ?`)
      .bind(owner.id, friend.id)
      .run();

    viewer = null;
    expect((await routes.request(url, {}, bindings(bucket))).status).toBe(404);
    await env.DB.prepare(
      `INSERT INTO profile_privacy (user_id, profile_visibility, searchable, request_policy, activity_visibility, updated_at)
       VALUES (?, 'public', 0, 'anyone', 'friends', ?)`,
    )
      .bind(owner.id, new Date().toISOString())
      .run();
    const publicResponse = await routes.request(url, {}, bindings(bucket));
    expect(publicResponse.status).toBe(200);
    expect(publicResponse.headers.get("Cache-Control")).toBe(PROFILE_MEDIA_CACHE_CONTROL);
    expect(publicResponse.headers.get("Cache-Control")).not.toContain("immutable");
    expect(publicResponse.headers.get("Cache-Control")).not.toContain("31536000");
    expect(publicResponse.headers.get("Vary")).toBe("Authorization");

    await env.DB.prepare(`UPDATE profile_privacy SET profile_visibility = 'private' WHERE user_id = ?`)
      .bind(owner.id)
      .run();
    viewer = owner.id;
    const selfResponse = await routes.request(url, {}, bindings(bucket));
    expect(selfResponse.status).toBe(200);
    expect(selfResponse.headers.get("Cache-Control")).toBe("private, no-store");
    viewer = friend.id;
    expect((await routes.request(url, {}, bindings(bucket))).status).toBe(404);

    const stale = await routes.request(url.replace(stored.row.version, "e".repeat(64)), {}, bindings(bucket));
    expect(stale.status).toBe(404);
    expect((await routes.request(`${url}/../chosen-file`, {}, bindings(bucket))).status).toBe(404);
  });

  it("supports range and HEAD responses, then deletes a slot idempotently", async () => {
    const owner = await seedUser("media-delete@example.test");
    const bucket = new FakeR2Bucket();
    const routes = createProfileMediaRoutes({
      resolveViewer: async () => owner.id,
      requireUser: async () => ({ userId: owner.id }),
      canView: async () => true,
      rateLimitMutation: async () => {},
    });
    const upload = await routes.request(
      "http://exo.test/v1/profile/media/avatar",
      { method: "PUT", headers: { "Content-Type": "image/png" }, body: png(256, 256) },
      bindings(bucket),
    );
    expect(upload.status).toBe(200);
    const uploaded = await upload.json<{ media: { url: string } }>();
    const range = await routes.request(
      `http://exo.test${uploaded.media.url}`,
      { headers: { Range: "bytes=0-3" } },
      bindings(bucket),
    );
    expect(range.status).toBe(206);
    expect(range.headers.get("Content-Range")).toMatch(/^bytes 0-3\//);
    expect((await range.arrayBuffer()).byteLength).toBe(4);
    const head = await routes.request(`http://exo.test${uploaded.media.url}`, { method: "HEAD" }, bindings(bucket));
    expect(head.status).toBe(200);
    expect((await head.arrayBuffer()).byteLength).toBe(0);

    expect(
      (await routes.request("http://exo.test/v1/profile/media/avatar", { method: "DELETE" }, bindings(bucket))).status,
    ).toBe(200);
    expect(
      (await routes.request("http://exo.test/v1/profile/media/avatar", { method: "DELETE" }, bindings(bucket))).status,
    ).toBe(200);
    expect(bucket.objects.size).toBe(0);
  });

  it("stores a sanitized gallery GIF through R2 and rejects a MIME/signature mismatch", async () => {
    const owner = await seedUser("media-gallery-gif@example.test");
    const u16 = (value: number) => new Uint8Array([value & 0xff, (value >>> 8) & 0xff]);
    const gifBytes = join(
      encoder.encode("GIF89a"),
      u16(640),
      u16(360),
      new Uint8Array([0x80, 0x00, 0x00]),
      new Uint8Array([0, 0, 0, 255, 255, 255]),
      new Uint8Array([0x21, 0xfe, 3]),
      encoder.encode("gps"),
      new Uint8Array([0x00]),
      new Uint8Array([0x21, 0xf9, 0x04, 0x00, 0x05, 0x00, 0x00, 0x00]),
      new Uint8Array([0x2c, 0, 0, 0, 0, 1, 0, 1, 0, 0]),
      new Uint8Array([0x02, 0x02, 0x4c, 0x01, 0x00, 0x3b]),
    );

    const mismatch = await api("/v1/profile/media/gallery1", {
      method: "PUT",
      headers: { authorization: `Bearer ${owner.token}`, "content-type": "image/png" },
      body: gifBytes,
    });
    expect(mismatch.status).toBe(400);
    expect((await mismatch.json<{ error: { code: string } }>()).error.code).toBe("MEDIA_INVALID");

    const upload = await api("/v1/profile/media/gallery1", {
      method: "PUT",
      headers: { authorization: `Bearer ${owner.token}`, "content-type": "image/gif" },
      body: gifBytes,
    });
    expect(upload.status).toBe(200);
    const body = await upload.json<{ media: { kind: string; contentType: string; url: string; width: number } }>();
    expect(body.media).toMatchObject({
      kind: "gallery1",
      contentType: "image/gif",
      width: 640,
    });
    expect(body.media.url).toMatch(new RegExp(`^/v1/media/${owner.id}/gallery1/[a-f0-9]{64}$`));

    const stored = await currentProfileMedia(env.DB, owner.id, "gallery1");
    expect(stored?.content_type).toBe("image/gif");
    const object = await env.PROFILE_MEDIA.head(stored!.object_key);
    expect(object?.httpMetadata?.contentType).toBe("image/gif");
    expect(object?.customMetadata?.kind).toBe("gallery1");
    expect(object?.customMetadata?.sha256).toBe(stored?.sha256);

    const served = await api(body.media.url, {
      headers: { authorization: `Bearer ${owner.token}` },
    });
    expect(served.status).toBe(200);
    expect(served.headers.get("content-type")).toBe("image/gif");
    expect(new TextDecoder().decode(await served.arrayBuffer())).not.toContain("gps");

    const cleared = await api("/v1/profile/media/gallery1", {
      method: "DELETE",
      headers: { authorization: `Bearer ${owner.token}` },
    });
    expect(cleared.status).toBe(200);
    expect(await currentProfileMedia(env.DB, owner.id, "gallery1")).toBeNull();
    expect((await env.PROFILE_MEDIA.list({ prefix: `users/${owner.id}/gallery1/` })).objects).toHaveLength(0);
  });

  it("stores a bounded gallery slot and projects it by its stable slot key", async () => {
    const owner = await seedUser("media-gallery@example.test");
    const bucket = new FakeR2Bucket();
    const routes = createProfileMediaRoutes({
      resolveViewer: async () => owner.id,
      requireUser: async () => ({ userId: owner.id }),
      canView: async () => true,
      rateLimitMutation: async () => {},
    });
    const upload = await routes.request(
      "http://exo.test/v1/profile/media/gallery0",
      { method: "PUT", headers: { "Content-Type": "image/png" }, body: png(640, 360) },
      bindings(bucket),
    );
    expect(upload.status).toBe(200);
    const projection = await getProfileMediaProjection(env.DB, owner.id);
    expect(projection.gallery0).toMatchObject({ kind: "gallery0", width: 640, height: 360 });
    expect(projection.avatar).toBeNull();
    expect(projection.banner).toBeNull();
  });

  it("removes mapped and orphaned owned objects during idempotent account cleanup", async () => {
    const user = await seedUser("media-cleanup@example.test");
    const other = await seedUser("media-other@example.test");
    const bucket = new FakeR2Bucket();
    const media = await inspectAndSanitizeProfileMedia("avatar", "image/png", png(256, 256));
    await replaceProfileMedia(env.DB, bucket, user.id, "avatar", media, { version: "f".repeat(64) });
    await bucket.put(`users/${user.id}/banner/${"1".repeat(64)}.png`, media.bytes);
    await bucket.put(`users/${other.id}/avatar/${"2".repeat(64)}.png`, media.bytes);

    await cleanupProfileMediaForAccount(env.DB, bucket, user.id);
    await cleanupProfileMediaForAccount(env.DB, bucket, user.id);
    expect([...bucket.objects.keys()]).toEqual([`users/${other.id}/avatar/${"2".repeat(64)}.png`]);
    expect(await currentProfileMedia(env.DB, user.id, "avatar")).toBeNull();
  });
});
