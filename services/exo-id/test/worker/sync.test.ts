import { describe, expect, it } from "vitest";
import { env } from "cloudflare:test";
import { api, authHeaders, field, seedUser } from "./helpers.ts";

async function userWithHandle(email: string, handle: string) {
  const user = await seedUser(email);
  const claimed = await api("/v1/handle", {
    method: "PUT",
    headers: authHeaders(user.token),
    body: JSON.stringify({ handle }),
  });
  expect(claimed.status).toBe(200);
  return user;
}

describe("sync denylist", () => {
  it("drops machine-specific keys and never stores them", async () => {
    const user = await userWithHandle("sync@example.test", "syncer");
    const updatedAt = new Date().toISOString();
    const res = await api("/v1/sync", {
      method: "PUT",
      headers: authHeaders(user.token),
      body: JSON.stringify({
        deviceId: "pc-a",
        fields: {
          sortMode: field("recent", updatedAt),
          defaultInstallRoot: field("D:\\Games", updatedAt),
          launchOverrides: field({ cwd: "C:\\Nope" }, updatedAt),
          trophyNotificationPositionX: field(1, updatedAt),
          windowBounds: field({ x: 0 }, updatedAt),
        },
      }),
    });
    expect(res.status).toBe(200);
    const body = await res.json<{
      values: Record<string, unknown>;
      discarded: Array<{ key: string; reason: string }>;
    }>();
    expect(body.values.sortMode).toBe("recent");
    expect(body.values.defaultInstallRoot).toBeUndefined();
    expect(body.discarded.map((row) => row.key).sort()).toEqual(
      ["defaultInstallRoot", "launchOverrides", "trophyNotificationPositionX", "windowBounds"].sort(),
    );
    expect(body.discarded.every((row) => row.reason === "denied")).toBe(true);
    const stored = await env.DB.prepare(`SELECT key FROM pref_field WHERE user_id = ?`)
      .bind(user.id)
      .all<{ key: string }>();
    expect((stored.results ?? []).map((row) => row.key)).toEqual(["sortMode"]);
  });

  it("keeps the newer field and reports the losing write", async () => {
    const user = await userWithHandle("lww@example.test", "lwwhandle");
    const older = "2026-01-01T00:00:00.000Z";
    const newer = "2026-08-18T00:00:00.000Z";
    await api("/v1/sync", {
      method: "PUT",
      headers: authHeaders(user.token),
      body: JSON.stringify({
        deviceId: "pc-a",
        fields: { sortMode: field("name", newer, "pc-a") },
      }),
    });
    const res = await api("/v1/sync", {
      method: "PUT",
      headers: authHeaders(user.token),
      body: JSON.stringify({
        deviceId: "pc-b",
        fields: { sortMode: field("recent", older, "pc-b") },
      }),
    });
    const body = await res.json<{
      values: { sortMode: string };
      discarded: Array<{ key: string; reason: string }>;
    }>();
    expect(body.values.sortMode).toBe("name");
    expect(body.discarded).toEqual([{ key: "sortMode", reason: "older" }]);
  });

  it("discards unreasonably future updatedAt values", async () => {
    const user = await userWithHandle("future-sync@example.test", "futuresync");
    const future = new Date(Date.now() + 24 * 60 * 60 * 1000).toISOString();
    const res = await api("/v1/sync", {
      method: "PUT",
      headers: authHeaders(user.token),
      body: JSON.stringify({
        deviceId: "pc-a",
        fields: { sortMode: field("store", future) },
      }),
    });
    expect(res.status).toBe(200);
    const body = await res.json<{
      values: Record<string, unknown>;
      discarded: Array<{ key: string; reason: string }>;
    }>();
    expect(body.values.sortMode).toBeUndefined();
    expect(body.discarded).toEqual([
      expect.objectContaining({ key: "sortMode", reason: "invalid" }),
    ]);
  });
});
