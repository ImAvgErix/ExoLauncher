import { env } from "cloudflare:test";
import { describe, expect, it } from "vitest";
import { api, authHeaders, field, seedUser } from "./helpers.ts";

type TestUser = Awaited<ReturnType<typeof seedUser>>;
type ErrorResponse = { error: { code: string; message: string } };
type Badge = { key: string; label: string; description: string; tone: string };

async function claim(user: TestUser, handle: string): Promise<void> {
  const response = await api("/v1/handle", {
    method: "PUT",
    headers: authHeaders(user.token),
    body: JSON.stringify({ handle }),
  });
  expect(response.status).toBe(200);
}

async function makePublic(user: TestUser): Promise<void> {
  const response = await api("/v1/profile/privacy", {
    method: "PUT",
    headers: authHeaders(user.token),
    body: JSON.stringify({
      profileVisibility: "public",
      searchable: true,
      requestPolicy: "anyone",
      activityVisibility: "friends",
    }),
  });
  expect(response.status).toBe(200);
}

async function seedRole(userId: string, role: "owner" | "admin" | "developer", grantedBy?: string): Promise<void> {
  await env.DB.prepare(
    `INSERT INTO staff_role (user_id, role, granted_by, granted_at) VALUES (?, ?, ?, ?)`,
  )
    .bind(userId, role, grantedBy ?? null, new Date().toISOString())
    .run();
}

async function mutateBadge(
  actor: TestUser,
  method: "POST" | "DELETE",
  handle: string,
  badge: string,
  extra: Record<string, unknown> = {},
): Promise<Response> {
  return api("/v1/admin/badges", {
    method,
    headers: authHeaders(actor.token),
    body: JSON.stringify({ handle, badge, ...extra }),
  });
}

describe("server-authoritative roles and safe badge projections", () => {
  it("keeps roles self-only and projects only fixed visual badge metadata", async () => {
    const staff = await seedUser("badge-self-staff@example.test");
    await claim(staff, "badgestaff");
    await makePublic(staff);
    await seedRole(staff.id, "developer");
    await seedRole(staff.id, "owner");
    const stamp = new Date().toISOString();
    await env.DB.batch([
      env.DB.prepare(
        `INSERT INTO profile_badge (user_id, badge_key, granted_by, granted_at)
         VALUES (?, 'founder', NULL, ?)`,
      ).bind(staff.id, stamp),
      env.DB.prepare(
        `INSERT INTO profile_badge (user_id, badge_key, granted_by, granted_at)
         VALUES (?, 'developer', NULL, ?)`,
      ).bind(staff.id, stamp),
    ]);

    const attemptedForge = await api("/v1/profile", {
      method: "PUT",
      headers: authHeaders(staff.token),
      body: JSON.stringify({
        deviceId: "badge-test",
        fields: {
          roles: field(["owner"]),
          canManageBadges: field(true),
          badges: field([{ key: "founder" }]),
        },
      }),
    });
    expect(attemptedForge.status).toBe(200);
    const forgeBody = await attemptedForge.json<{ discarded: Array<{ key: string; reason: string }> }>();
    expect(forgeBody.discarded).toEqual([
      expect.objectContaining({ key: "roles", reason: "denied" }),
      expect.objectContaining({ key: "canManageBadges", reason: "denied" }),
      expect.objectContaining({ key: "badges", reason: "denied" }),
    ]);

    const me = await api("/v1/me", { headers: authHeaders(staff.token) });
    expect(me.status).toBe(200);
    const self = await me.json<{
      roles: string[];
      canManageBadges: boolean;
      badges: Badge[];
    }>();
    expect(self.roles).toEqual(["owner", "developer"]);
    expect(self.canManageBadges).toBe(true);
    expect(self.badges).toEqual([
      { key: "founder", label: "Founder", description: "Founder of Exo", tone: "founder" },
      { key: "developer", label: "Developer", description: "Builds Exo", tone: "staff" },
    ]);
    expect(JSON.stringify(self)).not.toContain("granted_by");
    expect(JSON.stringify(self)).not.toContain("grantedAt");

    const publicProfile = await api("/v1/profiles/badgestaff");
    expect(publicProfile.status).toBe(200);
    const publicBody = await publicProfile.json<Record<string, unknown>>();
    expect(publicBody.badges).toEqual(self.badges);
    expect(publicBody).not.toHaveProperty("roles");
    expect(publicBody).not.toHaveProperty("canManageBadges");

    const search = await api("/v1/profiles/search?q=badgestaff");
    expect(search.status).toBe(200);
    const searchBody = await search.json<{ profiles: Array<Record<string, unknown>> }>();
    expect(searchBody.profiles).toHaveLength(1);
    expect(searchBody.profiles[0]).not.toHaveProperty("badges");
    expect(searchBody.profiles[0]).not.toHaveProperty("roles");
    expect(searchBody.profiles[0]).not.toHaveProperty("canManageBadges");
  });

  it("gives ordinary users and admins the same hidden badge-administration surface", async () => {
    const ordinary = await seedUser("badge-ordinary@example.test");
    const admin = await seedUser("badge-denied-admin@example.test");
    const target = await seedUser("badge-ordinary-target@example.test");
    await claim(ordinary, "badgeordinary");
    await claim(admin, "badgedeniedadmin");
    await claim(target, "badgetarget");
    await seedRole(admin.id, "admin");

    expect(await (await api("/v1/me", { headers: authHeaders(admin.token) })).json()).toMatchObject({
      roles: ["admin"],
      canManageBadges: false,
    });

    const deniedBodies: ErrorResponse[] = [];
    for (const actor of [ordinary, admin]) {
      for (const response of [
        await api("/v1/admin/badges?handle=badgetarget", { headers: authHeaders(actor.token) }),
        await mutateBadge(actor, "POST", "badgetarget", "developer"),
        await mutateBadge(actor, "DELETE", "badgetarget", "developer"),
      ]) {
        expect(response.status).toBe(404);
        deniedBodies.push(await response.json<ErrorResponse>());
      }
    }
    expect(deniedBodies).toEqual(Array.from({ length: 6 }, () => ({
      error: { code: "NOT_FOUND", message: "Not found." },
    })));
    expect(await env.DB.prepare(`SELECT 1 FROM profile_badge WHERE user_id = ?`).bind(target.id).first()).toBeNull();
  });
});

describe("badge administration boundaries", () => {
  it("lets only owners manage allowlisted badges idempotently without exposing target roles", async () => {
    const owner = await seedUser("badge-managing-owner@example.test");
    const target = await seedUser("badge-owner-target@example.test");
    await claim(owner, "badgemanagingowner");
    await claim(target, "badgemember");
    await seedRole(owner.id, "owner");

    for (let i = 0; i < 2; i++) {
      const grant = await mutateBadge(owner, "POST", "BADGEMEMBER", "developer");
      expect(grant.status).toBe(200);
      expect(await grant.json()).toEqual({
        handle: { display: "badgemember", normalized: "badgemember" },
        badges: [{ key: "developer", label: "Developer", description: "Builds Exo", tone: "staff" }],
      });
    }
    const count = await env.DB.prepare(
      `SELECT COUNT(*) AS count FROM profile_badge WHERE user_id = ? AND badge_key = 'developer'`,
    ).bind(target.id).first<{ count: number }>();
    expect(count?.count).toBe(1);

    const listed = await api("/v1/admin/badges?handle=badgemember", { headers: authHeaders(owner.token) });
    expect(listed.status).toBe(200);
    const listedBody = await listed.json<Record<string, unknown>>();
    expect(listedBody).not.toHaveProperty("userId");
    expect(listedBody).not.toHaveProperty("roles");
    expect(JSON.stringify(listedBody)).not.toContain(target.id);

    const selfMutation = await mutateBadge(owner, "POST", "badgemanagingowner", "contributor");
    expect(selfMutation.status).toBe(400);
    expect((await selfMutation.json<ErrorResponse>()).error.code).toBe("INVALID_REQUEST");

    const unknown = await mutateBadge(owner, "POST", "badgemember", "<script>");
    expect(unknown.status).toBe(400);
    const extra = await mutateBadge(owner, "POST", "badgemember", "contributor", { color: "#fff" });
    expect(extra.status).toBe(400);

    for (let i = 0; i < 2; i++) {
      const revoke = await mutateBadge(owner, "DELETE", "badgemember", "developer");
      expect(revoke.status).toBe(200);
      expect((await revoke.json<{ badges: Badge[] }>()).badges).toEqual([]);
    }
  });

  it("keeps Founder exclusive and rejects the retired CEO badge", async () => {
    await env.DB.prepare(`DELETE FROM profile_badge WHERE badge_key = 'founder'`).run();
    const owner = await seedUser("badge-owner@example.test");
    const founder = await seedUser("badge-founder@example.test");
    const secondOwner = await seedUser("badge-second-owner@example.test");
    await claim(owner, "badgeowner");
    await claim(founder, "badgefounder");
    await claim(secondOwner, "badgeowner2");
    await seedRole(owner.id, "owner");

    const founderGrant = await mutateBadge(owner, "POST", "badgefounder", "founder");
    expect(founderGrant.status).toBe(200);
    const repeated = await mutateBadge(owner, "POST", "badgefounder", "founder");
    expect(repeated.status).toBe(200);
    expect(await (await api("/v1/me", { headers: authHeaders(founder.token) })).json()).toMatchObject({
      roles: [],
      canManageBadges: false,
      badges: [{ key: "founder" }],
    });

    const exclusive = await mutateBadge(owner, "POST", "badgeowner2", "founder");
    expect(exclusive.status).toBe(409);
    const exclusiveError = await exclusive.json<ErrorResponse>();
    expect(exclusiveError.error).toEqual({ code: "INVALID_REQUEST", message: "Badge cannot be granted." });
    expect(JSON.stringify(exclusiveError)).not.toContain("badgefounder");

    const ceoGrant = await mutateBadge(owner, "POST", "badgefounder", "ceo");
    expect(ceoGrant.status).toBe(400);

    const revoked = await mutateBadge(owner, "DELETE", "badgefounder", "founder");
    expect(revoked.status).toBe(200);
    expect((await revoked.json<{ badges: Badge[] }>()).badges).toEqual([]);
    const reassigned = await mutateBadge(owner, "POST", "badgeowner2", "founder");
    expect(reassigned.status).toBe(200);
  });

  it("uses the same not-found envelope for malformed and absent target handles", async () => {
    const owner = await seedUser("badge-target-leak-owner@example.test");
    await claim(owner, "badgeleakowner");
    await seedRole(owner.id, "owner");
    const responses = [
      await api("/v1/admin/badges?handle=x", { headers: authHeaders(owner.token) }),
      await api("/v1/admin/badges?handle=does_not_exist", { headers: authHeaders(owner.token) }),
    ];
    for (const response of responses) {
      expect(response.status).toBe(404);
      expect(await response.json()).toEqual({ error: { code: "NOT_FOUND", message: "Profile not found." } });
    }
  });

  it("rate-limits badge reads as well as mutations", async () => {
    const owner = await seedUser("badge-rate-owner@example.test");
    const target = await seedUser("badge-rate-target@example.test");
    await claim(owner, "badgerateowner");
    await claim(target, "badgeratetarget");
    await seedRole(owner.id, "owner");
    for (let i = 0; i < 40; i++) {
      expect((await api("/v1/admin/badges?handle=badgeratetarget", {
        headers: authHeaders(owner.token),
      })).status).toBe(200);
    }
    const limited = await api("/v1/admin/badges?handle=badgeratetarget", {
      headers: authHeaders(owner.token),
    });
    expect(limited.status).toBe(429);
    expect(limited.headers.get("retry-after")).toMatch(/^\d+$/);
    expect((await limited.json<ErrorResponse>()).error.code).toBe("RATE_LIMITED");
  });
});

describe("role and badge migration constraints", () => {
  it("rejects unknown stored values, enforces one Founder, and cascades safely", async () => {
    await env.DB.prepare(`DELETE FROM profile_badge WHERE badge_key = 'founder'`).run();
    const granter = await seedUser("badge-schema-granter@example.test");
    const target = await seedUser("badge-schema-target@example.test");
    const other = await seedUser("badge-schema-other@example.test");
    const stamp = new Date().toISOString();

    await expect(env.DB.prepare(
      `INSERT INTO staff_role (user_id, role, granted_by, granted_at) VALUES (?, 'superuser', ?, ?)`,
    ).bind(target.id, granter.id, stamp).run()).rejects.toThrow(/CHECK constraint failed/i);
    await expect(env.DB.prepare(
      `INSERT INTO profile_badge (user_id, badge_key, granted_by, granted_at) VALUES (?, 'custom_html', ?, ?)`,
    ).bind(target.id, granter.id, stamp).run()).rejects.toThrow(/CHECK constraint failed/i);

    await seedRole(target.id, "developer", granter.id);
    await env.DB.prepare(
      `INSERT INTO profile_badge (user_id, badge_key, granted_by, granted_at)
       VALUES (?, 'founder', ?, ?)`,
    ).bind(target.id, granter.id, stamp).run();
    await expect(env.DB.prepare(
      `INSERT INTO profile_badge (user_id, badge_key, granted_by, granted_at)
       VALUES (?, 'founder', ?, ?)`,
    ).bind(other.id, granter.id, stamp).run()).rejects.toThrow(/UNIQUE constraint failed/i);

    await env.DB.prepare(`DELETE FROM user WHERE id = ?`).bind(granter.id).run();
    const surviving = await env.DB.prepare(
      `SELECT granted_by FROM profile_badge WHERE user_id = ? AND badge_key = 'founder'`,
    ).bind(target.id).first<{ granted_by: string | null }>();
    expect(surviving?.granted_by).toBeNull();

    await env.DB.prepare(`DELETE FROM user WHERE id = ?`).bind(target.id).run();
    expect(await env.DB.prepare(`SELECT 1 FROM staff_role WHERE user_id = ?`).bind(target.id).first()).toBeNull();
    expect(await env.DB.prepare(`SELECT 1 FROM profile_badge WHERE user_id = ?`).bind(target.id).first()).toBeNull();
  });
});
