import { describe, expect, it } from "vitest";
import { env } from "cloudflare:test";
import { api, authHeaders, seedUser } from "./helpers.ts";

describe("handle uniqueness", () => {
  it("only one of two concurrent claims for the same normalized handle succeeds", async () => {
    const a = await seedUser("a@example.test");
    const b = await seedUser("b@example.test");
    const body = JSON.stringify({ handle: "erik" });
    const [ra, rb] = await Promise.all([
      api("/v1/handle", { method: "PUT", headers: authHeaders(a.token), body }),
      api("/v1/handle", { method: "PUT", headers: authHeaders(b.token), body }),
    ]);
    const statuses = [ra.status, rb.status].sort();
    expect(statuses).toEqual([200, 409]);
    const loser = ra.status === 409
      ? await ra.json<{ error: { code: string } }>()
      : await rb.json<{ error: { code: string } }>();
    expect(loser.error.code).toBe("HANDLE_TAKEN");
    const rows = await env.DB.prepare(`SELECT COUNT(*) AS n FROM handle WHERE normalized = 'erik'`).first<{ n: number }>();
    expect(rows?.n).toBe(1);
  });

  it("enforces uniqueness at the database, not only in the API", async () => {
    const a = await seedUser("c@example.test");
    const b = await seedUser("d@example.test");
    const now = new Date().toISOString();
    await env.DB.prepare(
      `INSERT INTO handle (user_id, display, normalized, skeleton, claimed_at, changed_at) VALUES (?, 'x', 'uniqueidx', 'uniqueidx', ?, ?)`,
    )
      .bind(a.id, now, now)
      .run();
    let failed = false;
    try {
      await env.DB.prepare(
        `INSERT INTO handle (user_id, display, normalized, skeleton, claimed_at, changed_at) VALUES (?, 'x', 'uniqueidx', 'uniqueidx', ?, ?)`,
      )
        .bind(b.id, now, now)
        .run();
    } catch {
      failed = true;
    }
    expect(failed).toBe(true);
  });
});

describe("confusable rejection", () => {
  it("rejects a later claim whose skeleton collides with an existing handle", async () => {
    const a = await seedUser("skel-a@example.test");
    const b = await seedUser("skel-b@example.test");
    const first = await api("/v1/handle", {
      method: "PUT",
      headers: authHeaders(a.token),
      body: JSON.stringify({ handle: "m" }),
    });
    expect(first.status).toBe(400);
    const m = await api("/v1/handle", {
      method: "PUT",
      headers: authHeaders(a.token),
      body: JSON.stringify({ handle: "max" }),
    });
    expect(m.status).toBe(200);
    const rn = await api("/v1/handle", {
      method: "PUT",
      headers: authHeaders(b.token),
      body: JSON.stringify({ handle: "rnax" }),
    });
    expect(rn.status).toBe(409);
    const body = await rn.json<{ error: { code: string } }>();
    expect(body.error.code).toBe("HANDLE_CONFUSABLE");
  });

  it("rejects Cyrillic lookalikes at the API", async () => {
    const user = await seedUser("cyr@example.test");
    const res = await api("/v1/handle", {
      method: "PUT",
      headers: authHeaders(user.token),
      body: JSON.stringify({ handle: "еrix" }),
    });
    expect(res.status).toBe(400);
    expect((await res.json<{ error: { code: string } }>()).error.code).toBe("HANDLE_CONFUSABLE");
  });
});

describe("handle cooldown and exact body", () => {
  it("lets a casing-only change through and rate-limits a normalized change", async () => {
    const user = await seedUser("cooldown@example.test");
    const headers = { ...authHeaders(user.token), "cf-connecting-ip": "198.51.100.20" };
    const claimed = await api("/v1/handle", {
      method: "PUT",
      headers,
      body: JSON.stringify({ handle: "CoolUser" }),
    });
    expect(claimed.status).toBe(200);

    const casing = await api("/v1/handle", {
      method: "PUT",
      headers,
      body: JSON.stringify({ handle: "cooluser" }),
    });
    expect(casing.status).toBe(200);
    expect((await casing.json<{ handle: { display: string; changedAt: string } }>()).handle.display).toBe("cooluser");

    const extra = await api("/v1/handle", {
      method: "PUT",
      headers,
      body: JSON.stringify({ handle: "cooluser", ignored: true }),
    });
    expect(extra.status).toBe(400);

    const renamed = await api("/v1/handle", {
      method: "PUT",
      headers,
      body: JSON.stringify({ handle: "coolnext" }),
    });
    expect(renamed.status).toBe(409);
    expect((await renamed.json<{ error: { code: string } }>()).error.code).toBe("HANDLE_COOLDOWN");
  });

  it("holds a tombstoned skeleton against a later confusable claim", async () => {
    const a = await seedUser("tomb-a@example.test");
    const b = await seedUser("tomb-b@example.test");
    const headersA = { ...authHeaders(a.token), "cf-connecting-ip": "198.51.100.21" };
    const headersB = { ...authHeaders(b.token), "cf-connecting-ip": "198.51.100.22" };
    expect((await api("/v1/handle", {
      method: "PUT",
      headers: headersA,
      body: JSON.stringify({ handle: "tombmax" }),
    })).status).toBe(200);
    await env.DB.prepare(`UPDATE handle SET changed_at = ? WHERE user_id = ?`)
      .bind(new Date(Date.now() - 31 * 24 * 60 * 60 * 1000).toISOString(), a.id)
      .run();
    expect((await api("/v1/handle", {
      method: "PUT",
      headers: headersA,
      body: JSON.stringify({ handle: "latermax" }),
    })).status).toBe(200);

    const blocked = await api("/v1/handle", {
      method: "PUT",
      headers: headersB,
      body: JSON.stringify({ handle: "tombrnax" }),
    });
    expect(blocked.status).toBe(409);
    expect((await blocked.json<{ error: { code: string } }>()).error.code).toBe("HANDLE_TAKEN");
  });
});

describe("reserved words", () => {
  it("refuses admin, exo, support, system and skeleton lookalikes", async () => {
    const user = await seedUser("res@example.test");
    for (const handle of ["admin", "exo", "support", "system", "ex0"]) {
      const res = await api("/v1/handle", {
        method: "PUT",
        headers: authHeaders(user.token),
        body: JSON.stringify({ handle }),
      });
      expect(res.status).toBe(400);
      expect((await res.json<{ error: { code: string } }>()).error.code).toBe("HANDLE_RESERVED");
    }
  });
});
