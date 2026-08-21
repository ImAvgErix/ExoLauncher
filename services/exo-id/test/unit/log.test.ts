import { afterEach, describe, expect, it, vi } from "vitest";
import { logError } from "../../src/log.ts";

afterEach(() => vi.restoreAllMocks());

describe("structured log redaction", () => {
  it("redacts normalized cookie and auth-token header keys", () => {
    const sink = vi.spyOn(console, "error").mockImplementation(() => undefined);
    logError("request failed", {
      "set-cookie": "session=secret-cookie",
      "set-auth-token": "secret-token",
      nested: { access_token: "secret-access" },
    });

    const output = String(sink.mock.calls[0]?.[0] ?? "");
    expect(output).toContain('"level":"error"');
    expect(output).not.toContain("secret-cookie");
    expect(output).not.toContain("secret-token");
    expect(output).not.toContain("secret-access");
  });

  it("redacts every password-shaped field recursively", () => {
    const sink = vi.spyOn(console, "error").mockImplementation(() => undefined);
    logError("account request failed", {
      password: "original-secret",
      currentPassword: "current-secret",
      nested: {
        new_password: "new-secret",
        passwordConfirmation: "confirmed-secret",
      },
    });

    const output = String(sink.mock.calls[0]?.[0] ?? "");
    expect(output).not.toContain("original-secret");
    expect(output).not.toContain("current-secret");
    expect(output).not.toContain("new-secret");
    expect(output).not.toContain("confirmed-secret");
  });
});
