import { describe, expect, it } from "vitest";
import { parseLoopbackRedirect } from "../../src/loopback.ts";
import { classifySyncKey, SYNC_DENYLIST } from "../../src/sync.ts";
import { ApiError } from "../../src/errors.ts";

describe("loopback redirect", () => {
  it("accepts 127.0.0.1 with any ephemeral port and /callback", () => {
    const a = parseLoopbackRedirect("http://127.0.0.1:54321/callback");
    const b = parseLoopbackRedirect("http://127.0.0.1:12345/callback");
    expect(a.port).toBe(54321);
    expect(b.port).toBe(12345);
    expect(a.path).toBe("/callback");
  });

  it("accepts IPv6 loopback", () => {
    expect(parseLoopbackRedirect("http://[::1]:49152/callback").href).toBe("http://[::1]:49152/callback");
  });

  it("refuses localhost, https, and non-callback paths", () => {
    expect(() => parseLoopbackRedirect("http://localhost:8080/callback")).toThrow(ApiError);
    expect(() => parseLoopbackRedirect("https://127.0.0.1:8080/callback")).toThrow(ApiError);
    expect(() => parseLoopbackRedirect("http://127.0.0.1:8080/oauth")).toThrow(ApiError);
    expect(() => parseLoopbackRedirect("http://example.com/callback")).toThrow(ApiError);
  });
});

describe("sync denylist", () => {
  it("denies machine-specific keys by default", () => {
    expect(classifySyncKey("defaultInstallRoot")).toBe("deny");
    expect(classifySyncKey("launchOverrides")).toBe("deny");
    expect(classifySyncKey("trophyNotificationPositionX")).toBe("deny");
    expect(classifySyncKey("onboardingComplete")).toBe("deny");
    expect(classifySyncKey("windowBounds")).toBe("deny");
    expect(classifySyncKey("unknownKey")).toBe("deny");
    expect(SYNC_DENYLIST).toContain("defaultInstallRoot");
  });

  it("allows only the portable preference keys", () => {
    expect(classifySyncKey("sortMode")).toBe("allow");
    expect(classifySyncKey("trophyNotificationsEnabled")).toBe("allow");
    expect(classifySyncKey("trophyNotificationPosition")).toBe("allow");
  });
});
