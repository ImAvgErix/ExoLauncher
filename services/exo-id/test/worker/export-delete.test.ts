import { describe, expect, it } from "vitest";
import { env } from "cloudflare:test";
import { api, authHeaders, field, seedUser } from "./helpers.ts";

describe("export and delete", () => {
  it("exports account, handle, profile, preferences and sessions, without tokens", async () => {
    const user = await seedUser("export@example.test");
    expect(
      (
        await api("/v1/handle", {
          method: "PUT",
          headers: authHeaders(user.token),
          body: JSON.stringify({ handle: "exporter" }),
        })
      ).status,
    ).toBe(200);
    const stamp = new Date().toISOString();
    await api("/v1/profile", {
      method: "PUT",
      headers: authHeaders(user.token),
      body: JSON.stringify({
        deviceId: "pc-a",
        fields: {
          displayName: field("Pat", stamp),
          bio: field("hello", stamp),
          showcase: field(["steam:1"], stamp),
        },
      }),
    });
    await api("/v1/sync", {
      method: "PUT",
      headers: authHeaders(user.token),
      body: JSON.stringify({
        deviceId: "pc-a",
        fields: { sortMode: field("store", stamp) },
      }),
    });
    await api("/v1/profile/privacy", {
      method: "PUT",
      headers: authHeaders(user.token),
      body: JSON.stringify({
        profileVisibility: "public",
        searchable: true,
        requestPolicy: "anyone",
        activityVisibility: "friends",
      }),
    });
    const mediaVersion = "a".repeat(64);
    await env.DB.prepare(
      `INSERT INTO profile_media
        (user_id, kind, version, object_key, content_type, byte_size, width, height, sha256, created_at, updated_at)
       VALUES (?, 'avatar', ?, ?, 'image/png', 1, 64, 64, ?, ?, ?)`,
    )
      .bind(
        user.id,
        mediaVersion,
        `users/${user.id}/avatar/${mediaVersion}.png`,
        "b".repeat(64),
        stamp,
        stamp,
      )
      .run();
    const res = await api("/v1/me/export", { headers: authHeaders(user.token) });
    expect(res.status).toBe(200);
    const body = await res.json<{
      account: { id: string; email: string };
      handle: { normalized: string };
      profile: { displayName: string; bio: string };
      preferences: { sortMode: string };
      sessions: Array<{ id: string }>;
      privacy: { profileVisibility: string; searchable: boolean };
      media: { avatar: { version: string }; banner: null };
      presence: null;
    }>();
    expect(body.account.id).toBe(user.id);
    expect(body.account.email).toBe("export@example.test");
    expect(body.handle.normalized).toBe("exporter");
    expect(body.profile.displayName).toBe("Pat");
    expect(body.profile.bio).toBe("hello");
    expect(body.preferences.sortMode).toBe("store");
    expect(body.sessions.length).toBeGreaterThan(0);
    expect(body.privacy).toEqual(expect.objectContaining({ profileVisibility: "public", searchable: true }));
    expect(body.media.avatar.version).toBe(mediaVersion);
    expect(body.media.banner).toBeNull();
    expect(body.presence).toBeNull();
    expect(JSON.stringify(body)).not.toContain(user.token);
    expect(JSON.stringify(body)).not.toMatch(/"token"/i);
  });

  it("deletes the user and holds the handle in a tombstone", async () => {
    const user = await seedUser("gone@example.test");
    await api("/v1/handle", {
      method: "PUT",
      headers: authHeaders(user.token),
      body: JSON.stringify({ handle: "goner" }),
    });
    const stamp = new Date().toISOString();
    await api("/v1/profile", {
      method: "PUT",
      headers: authHeaders(user.token),
      body: JSON.stringify({ deviceId: "pc-a", fields: { bio: field("x", stamp) } }),
    });
    const mediaVersion = "c".repeat(64);
    const mediaKey = `users/${user.id}/avatar/${mediaVersion}.png`;
    await env.PROFILE_MEDIA.put(mediaKey, new Uint8Array([1]));
    await env.DB.batch([
      env.DB.prepare(
        `INSERT INTO profile_media
          (user_id, kind, version, object_key, content_type, byte_size, width, height, sha256, created_at, updated_at)
         VALUES (?, 'avatar', ?, ?, 'image/png', 1, 64, 64, ?, ?, ?)`,
      ).bind(user.id, mediaVersion, mediaKey, "d".repeat(64), stamp, stamp),
      env.DB.prepare(
        `INSERT INTO profile_privacy
          (user_id, profile_visibility, searchable, request_policy, activity_visibility, updated_at)
         VALUES (?, 'private', 0, 'none', 'private', ?)`,
      ).bind(user.id, stamp),
    ]);
    const del = await api("/v1/me", { method: "DELETE", headers: authHeaders(user.token) });
    expect(del.status).toBe(200);
    const userRow = await env.DB.prepare(`SELECT id FROM user WHERE id = ?`).bind(user.id).first();
    const profile = await env.DB.prepare(`SELECT 1 FROM profile_field WHERE user_id = ?`).bind(user.id).first();
    const prefs = await env.DB.prepare(`SELECT 1 FROM pref_field WHERE user_id = ?`).bind(user.id).first();
    const sessions = await env.DB.prepare(`SELECT 1 FROM session WHERE userId = ?`).bind(user.id).first();
    const media = await env.DB.prepare(`SELECT 1 FROM profile_media WHERE user_id = ?`).bind(user.id).first();
    const privacy = await env.DB.prepare(`SELECT 1 FROM profile_privacy WHERE user_id = ?`).bind(user.id).first();
    const live = await env.DB.prepare(`SELECT 1 FROM handle WHERE normalized = 'goner'`).first();
    const tomb = await env.DB.prepare(
      `SELECT normalized, never_release, release_at FROM handle_tombstone WHERE normalized = 'goner'`,
    ).first<{ normalized: string; never_release: number; release_at: string }>();
    expect(userRow).toBeNull();
    expect(profile).toBeNull();
    expect(prefs).toBeNull();
    expect(sessions).toBeNull();
    expect(media).toBeNull();
    expect(privacy).toBeNull();
    expect((await env.PROFILE_MEDIA.list({ prefix: `users/${user.id}/` })).objects).toHaveLength(0);
    expect(live).toBeNull();
    expect(tomb?.normalized).toBe("goner");
    expect(tomb?.never_release).toBe(0);
    expect(Boolean(tomb?.release_at && tomb.release_at > new Date().toISOString())).toBe(true);

    const other = await seedUser("squat@example.test");
    const squat = await api("/v1/handle", {
      method: "PUT",
      headers: authHeaders(other.token),
      body: JSON.stringify({ handle: "goner" }),
    });
    expect(squat.status).toBe(409);
  });

  it("requires a freshly authenticated session before destructive deletion", async () => {
    const user = await seedUser("stale-delete@example.test");
    await env.DB.prepare(`UPDATE session SET createdAt = ? WHERE id = ?`)
      .bind(new Date(Date.now() - 60 * 60 * 1000).toISOString(), user.sessionId)
      .run();

    const response = await api("/v1/me", { method: "DELETE", headers: authHeaders(user.token) });
    expect(response.status).toBe(403);
    expect((await response.json<{ error: { code: string } }>()).error.code).toBe(
      "REAUTHENTICATION_REQUIRED",
    );
    expect(await env.DB.prepare(`SELECT id FROM user WHERE id = ?`).bind(user.id).first()).not.toBeNull();
  });
});
