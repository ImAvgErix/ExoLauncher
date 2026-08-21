import { describe, expect, it } from "vitest";
import { canonicalizeStoreId, hashStoreId, parseRelationship, parseStore } from "../../src/stores.ts";
import { ApiError } from "../../src/errors.ts";
import { buildSteamOpenIdUrl, steamClaimedId, verifySteamAssertion, STEAM_OPENID_ENDPOINT, STEAM_OPENID_NS } from "../../src/steam-openid.ts";
import type { Env } from "../../src/env.ts";
import { verifyEpicAccessToken, verifyGogAccessToken, EPIC_OAUTH_VERIFY_URL, GOG_USERDATA_URL } from "../../src/store-verify.ts";

describe("store ids", () => {
  it("accepts a public SteamID64 and rejects junk", () => {
    expect(canonicalizeStoreId("steam", "76561198000000001")).toBe("76561198000000001");
    expect(canonicalizeStoreId("steam", " 76561198000000001 ")).toBe("76561198000000001");
    expect(canonicalizeStoreId("steam", "12345")).toBeNull();
    expect(canonicalizeStoreId("steam", "7656119800000000")).toBeNull();
  });

  it("lowercases Epic account ids and keeps GOG numeric ids", () => {
    const epic = "A".repeat(32);
    expect(canonicalizeStoreId("epic", epic)).toBe("a".repeat(32));
    expect(canonicalizeStoreId("epic", "not-an-id")).toBeNull();
    expect(canonicalizeStoreId("gog", "48628349957132247")).toBe("48628349957132247");
    expect(canonicalizeStoreId("gog", "abc")).toBeNull();
  });

  it("HMACs the same id to the same hash and does not collide across stores", async () => {
    const secret = "test-secret-at-least-32-characters-long!";
    const a = await hashStoreId(secret, "steam", "76561198000000001");
    const b = await hashStoreId(secret, "steam", "76561198000000001");
    const c = await hashStoreId(secret, "gog", "76561198000000001");
    expect(a).toBe(b);
    expect(a).toHaveLength(64);
    expect(a).not.toBe(c);
  });

  it("refuses stores that cannot be linked yet", () => {
    expect(parseStore("steam")).toBe("steam");
    try {
      parseStore("riot");
      throw new Error("expected throw");
    } catch (err) {
      expect(err).toBeInstanceOf(ApiError);
      expect((err as ApiError).code).toBe("LINK_STORE_UNSUPPORTED");
    }
  });

  it("parses relationship kinds", () => {
    expect(parseRelationship("mutual")).toBe("mutual");
    expect(parseRelationship("onesided")).toBe("onesided");
    try {
      parseRelationship("plugin");
      throw new Error("expected throw");
    } catch (err) {
      expect(err).toBeInstanceOf(ApiError);
      expect((err as ApiError).code).toBe("INVALID_REQUEST");
    }
  });
});

describe("Steam OpenID", () => {
  it("builds a checkid_setup URL against steamcommunity.com", () => {
    const url = new URL(
      buildSteamOpenIdUrl("http://127.0.0.1:8787", "http://127.0.0.1:8787/v1/links/steam/callback?link=abc"),
    );
    expect(url.origin + url.pathname).toBe(STEAM_OPENID_ENDPOINT);
    expect(url.searchParams.get("openid.mode")).toBe("checkid_setup");
    expect(url.searchParams.get("openid.realm")).toBe("http://127.0.0.1:8787");
  });

  it("reads SteamID64 from http or https claimed_id", () => {
    expect(steamClaimedId("https://steamcommunity.com/openid/id/76561198000000001")).toBe("76561198000000001");
    expect(steamClaimedId("http://steamcommunity.com/openid/id/76561198000000001")).toBe("76561198000000001");
    expect(steamClaimedId("https://evil.example/openid/id/76561198000000001")).toBeNull();
  });

  it("direct-verifies an assertion with Steam check_authentication", async () => {
    const returnTo = "http://127.0.0.1:8787/v1/links/steam/callback?link=abc";
    const url = new URL(returnTo);
    url.searchParams.set("openid.ns", STEAM_OPENID_NS);
    url.searchParams.set("openid.mode", "id_res");
    url.searchParams.set("openid.op_endpoint", STEAM_OPENID_ENDPOINT);
    url.searchParams.set("openid.claimed_id", "https://steamcommunity.com/openid/id/76561198000000001");
    url.searchParams.set("openid.identity", "https://steamcommunity.com/openid/id/76561198000000001");
    url.searchParams.set("openid.return_to", returnTo);
    url.searchParams.set("openid.response_nonce", "2026-08-18T00:00:00Znonce");
    url.searchParams.set("openid.assoc_handle", "1234567890");
    url.searchParams.set("openid.signed", "signed,op_endpoint,claimed_id,identity,return_to,response_nonce,assoc_handle");
    url.searchParams.set("openid.sig", "not-a-test-backdoor");
    const env = { ENVIRONMENT: "development" };
    const fetchImpl: typeof fetch = async (input, init) => {
      expect(String(input)).toBe(STEAM_OPENID_ENDPOINT);
      expect(init?.method).toBe("POST");
      expect(String(init?.body)).toContain("openid.mode=check_authentication");
      return new Response("ns:http://specs.openid.net/auth/2.0\nis_valid:true\n", { status: 200 });
    };
    await expect(verifySteamAssertion(url, returnTo, env, fetchImpl)).resolves.toBe("76561198000000001");
  });

  it("rejects is_valid:false", async () => {
    const returnTo = "http://127.0.0.1:8787/v1/links/steam/callback?link=abc";
    const url = new URL(returnTo);
    url.searchParams.set("openid.ns", STEAM_OPENID_NS);
    url.searchParams.set("openid.mode", "id_res");
    url.searchParams.set("openid.op_endpoint", STEAM_OPENID_ENDPOINT);
    url.searchParams.set("openid.claimed_id", "https://steamcommunity.com/openid/id/76561198000000001");
    url.searchParams.set("openid.identity", "https://steamcommunity.com/openid/id/76561198000000001");
    url.searchParams.set("openid.return_to", returnTo);
    url.searchParams.set("openid.response_nonce", "2026-08-18T00:00:00Znonce");
    url.searchParams.set("openid.assoc_handle", "1234567890");
    url.searchParams.set("openid.signed", "signed,op_endpoint,claimed_id,identity,return_to,response_nonce,assoc_handle");
    url.searchParams.set("openid.sig", "nope");
    const env = { ENVIRONMENT: "development" };
    const fetchImpl: typeof fetch = async () =>
      new Response("ns:http://specs.openid.net/auth/2.0\nis_valid:false\n", { status: 200 });
    await expect(verifySteamAssertion(url, returnTo, env, fetchImpl)).rejects.toMatchObject({ code: "LINK_INVALID" });
  });
});

describe("Epic and GOG token verify", () => {
  it("reads account_id from Epic oauth/verify", async () => {
    const id = "a".repeat(32);
    const fetchImpl: typeof fetch = async (input) => {
      expect(String(input)).toBe(EPIC_OAUTH_VERIFY_URL);
      return new Response(JSON.stringify({ account_id: id }), { status: 200 });
    };
    await expect(verifyEpicAccessToken("legendary-access-token", fetchImpl)).resolves.toBe(id);
  });

  it("rejects a dead Epic token", async () => {
    const fetchImpl: typeof fetch = async () => new Response("no", { status: 401 });
    await expect(verifyEpicAccessToken("dead", fetchImpl)).rejects.toMatchObject({ code: "LINK_VERIFY_FAILED" });
  });

  it("reads userId from GOG userData.json", async () => {
    const fetchImpl: typeof fetch = async (input) => {
      expect(String(input)).toBe(GOG_USERDATA_URL);
      return new Response(JSON.stringify({ isLoggedIn: true, userId: "48628349957132247" }), { status: 200 });
    };
    await expect(verifyGogAccessToken("gogdl-access-token", fetchImpl)).resolves.toBe("48628349957132247");
  });
});
