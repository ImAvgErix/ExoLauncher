import { describe, expect, it } from "vitest";
import { parseFieldWrite } from "../../src/fields.ts";

describe("profile/sync timestamps", () => {
  it("rejects unreasonably future updatedAt values", () => {
    const deviceId = "pc-a";
    expect(parseFieldWrite({ value: "ok", updatedAt: new Date().toISOString() }, deviceId)).not.toBeNull();
    const future = new Date(Date.now() + 24 * 60 * 60 * 1000).toISOString();
    expect(parseFieldWrite({ value: "nope", updatedAt: future, deviceId }, deviceId)).toBeNull();
  });
});
