import { describe, expect, it } from "vitest";
import { api, authHeaders, seedUser } from "./helpers.ts";

describe("authenticated JSON body bounds", () => {
  it("rejects oversized JSON on handle, profile, sync, social, and links", async () => {
    const user = await seedUser("json-bound@example.test");
    const token = user.token;
    const claimed = await api("/v1/handle", {
      method: "PUT",
      headers: authHeaders(token),
      body: JSON.stringify({ handle: "jsonbound" }),
    });
    expect(claimed.status).toBe(200);

    const twoKiB = [
      ["/v1/handle", "PUT"],
      ["/v1/friends/requests", "POST"],
    ] as const;
    for (const [path, method] of twoKiB) {
      const res = await api(path, {
        method,
        headers: authHeaders(token),
        body: `{"handle":"jsonbound","pad":"${"x".repeat(2500)}"}`,
      });
      expect(res.status).toBe(400);
      expect((await res.json<{ error: { code: string } }>()).error.code).toBe("INVALID_REQUEST");
    }

    const thirtyTwoKiB = [
      ["/v1/profile", "PUT"],
      ["/v1/sync", "PUT"],
      ["/v1/links/discovery", "PATCH"],
      ["/v1/links/match", "POST"],
    ] as const;
    const huge = "x".repeat(33 * 1024);
    for (const [path, method] of thirtyTwoKiB) {
      const res = await api(path, {
        method,
        headers: authHeaders(token),
        body: `{"pad":"${huge}"}`,
      });
      expect(res.status).toBe(400);
      expect((await res.json<{ error: { code: string } }>()).error.code).toBe("INVALID_REQUEST");
    }
  });
});
