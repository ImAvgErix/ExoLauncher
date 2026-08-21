import { describe, expect, it } from "vitest";
import {
  authoritativeOfflinePresence,
  parseClientPresenceMessage,
  rosterFromSteamSummary,
  unavailablePresence,
} from "../../src/presence.ts";

describe("presence messages", () => {
  it("accepts only bounded client-owned heartbeat and status fields", () => {
    expect(parseClientPresenceMessage('{"type":"heartbeat"}')).toEqual({ type: "heartbeat" });
    expect(
      parseClientPresenceMessage(
        '{"type":"status","status":"in_game","gameId":"steam:10","gameTitle":"Counter-Strike"}',
      ),
    ).toEqual({
      type: "status",
      status: "in_game",
      gameId: "steam:10",
      gameTitle: "Counter-Strike",
    });

    for (const invalid of [
      '{"type":"heartbeat","userId":"someone-else"}',
      '{"type":"status","status":"offline"}',
      '{"type":"status","status":"online","gameId":"steam:10"}',
      '{"type":"unknown"}',
      "not-json",
      JSON.stringify({ type: "heartbeat", padding: "x".repeat(4096) }),
    ]) {
      expect(() => parseClientPresenceMessage(invalid)).toThrowError(/presence message/i);
    }
  });
});

describe("Steam presence mapping", () => {
  it("maps explicit Steam Offline to authoritative Offline and private/unknown to Unavailable", () => {
    expect(rosterFromSteamSummary({ userId: "u1", personaState: 0 })).toEqual(
      authoritativeOfflinePresence("u1"),
    );
    expect(rosterFromSteamSummary({
      userId: "u2",
      personaState: null,
      lastLogoffUnix: 1_700_000_000,
    })).toEqual(authoritativeOfflinePresence("u2", "2023-11-14T22:13:20.000Z"));
    expect(rosterFromSteamSummary({ userId: "u3", personaState: null })).toEqual(
      unavailablePresence("u3"),
    );
    expect(rosterFromSteamSummary({ userId: "u4", personaState: undefined })).toEqual(
      unavailablePresence("u4"),
    );
    expect(rosterFromSteamSummary({
      userId: "u5",
      personaState: 1,
      inGame: true,
      gameId: "steam:10",
      gameTitle: "Counter-Strike",
    })).toEqual({
      userId: "u5",
      status: "in_game",
      gameId: "steam:10",
      gameTitle: "Counter-Strike",
      lastSeen: null,
      availability: "available",
    });
    expect(unavailablePresence("u3").status).toBe("unknown");
    expect(unavailablePresence("u3").availability).toBe("unavailable");
    expect(authoritativeOfflinePresence("u1").status).toBe("offline");
    expect(authoritativeOfflinePresence("u1").availability).toBe("available");
  });
});
