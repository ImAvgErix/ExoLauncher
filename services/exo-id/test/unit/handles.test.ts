import { describe, expect, it } from "vitest";
import { parseHandle, handleSkeleton } from "../../src/handles.ts";
import { RESERVED_HANDLES } from "../../src/reserved.ts";
import { ApiError } from "../../src/errors.ts";

describe("handles", () => {
  it("preserves display casing and case-folds for comparison", () => {
    const parsed = parseHandle("Erix");
    expect(parsed.display).toBe("Erix");
    expect(parsed.normalized).toBe("erix");
  });

  it("maps rn to m and 0/1 to o/l on the skeleton", () => {
    expect(handleSkeleton("barn")).toBe("bam");
    expect(handleSkeleton("ex0")).toBe("exo");
    expect(handleSkeleton("adm1n")).toBe("admln");
  });

  it("rejects reserved words including skeleton lookalikes", () => {
    expect(RESERVED_HANDLES).toContain("admin");
    expect(RESERVED_HANDLES).toContain("exo");
    expect(RESERVED_HANDLES).toContain("support");
    expect(RESERVED_HANDLES).toContain("system");
    expect(() => parseHandle("admin")).toThrow(ApiError);
    expect(() => parseHandle("ex0")).toThrow(ApiError);
    expect(() => parseHandle("SUPPORT")).toThrow(ApiError);
  });

  it("rejects Cyrillic lookalikes and other non-ASCII", () => {
    expect(() => parseHandle("еrix")).toThrow(ApiError);
    try {
      parseHandle("еrix");
    } catch (err) {
      expect(err).toBeInstanceOf(ApiError);
      expect((err as ApiError).code).toBe("HANDLE_CONFUSABLE");
    }
  });

  it("rejects short numeric traps and empty", () => {
    expect(() => parseHandle("123")).toThrow(ApiError);
    expect(() => parseHandle("___")).toThrow(ApiError);
    expect(() => parseHandle("ab")).toThrow(ApiError);
  });
});
