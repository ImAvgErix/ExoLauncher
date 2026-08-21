import { describe, expect, it } from "vitest";
import {
  createProfileMediaVersion,
  inspectAndSanitizeProfileMedia,
  profileMediaObjectKey,
  ProfileMediaError,
  readBoundedMediaBody,
} from "../../src/media.ts";

const encoder = new TextEncoder();

function concat(...parts: Uint8Array[]): Uint8Array {
  const out = new Uint8Array(parts.reduce((total, part) => total + part.length, 0));
  let offset = 0;
  for (const part of parts) {
    out.set(part, offset);
    offset += part.length;
  }
  return out;
}

function u32be(value: number): Uint8Array {
  return new Uint8Array([
    (value >>> 24) & 0xff,
    (value >>> 16) & 0xff,
    (value >>> 8) & 0xff,
    value & 0xff,
  ]);
}

function u32le(value: number): Uint8Array {
  return new Uint8Array([value & 0xff, (value >>> 8) & 0xff, (value >>> 16) & 0xff, (value >>> 24) & 0xff]);
}

function u24le(value: number): Uint8Array {
  return new Uint8Array([value & 0xff, (value >>> 8) & 0xff, (value >>> 16) & 0xff]);
}

function crc32(bytes: Uint8Array): number {
  let crc = 0xffffffff;
  for (const byte of bytes) {
    crc ^= byte;
    for (let bit = 0; bit < 8; bit++) crc = (crc >>> 1) ^ (crc & 1 ? 0xedb88320 : 0);
  }
  return (crc ^ 0xffffffff) >>> 0;
}

function pngChunk(type: string, data: Uint8Array): Uint8Array {
  const typeBytes = encoder.encode(type);
  return concat(u32be(data.length), typeBytes, data, u32be(crc32(concat(typeBytes, data))));
}

function png(width: number, height: number, extra: Uint8Array[] = []): Uint8Array {
  const ihdr = new Uint8Array(13);
  ihdr.set(u32be(width), 0);
  ihdr.set(u32be(height), 4);
  ihdr.set([8, 6, 0, 0, 0], 8);
  return concat(
    new Uint8Array([137, 80, 78, 71, 13, 10, 26, 10]),
    pngChunk("IHDR", ihdr),
    ...extra,
    pngChunk("IDAT", new Uint8Array([0x78, 0x9c, 0x03, 0, 0, 0, 0, 1])),
    pngChunk("IEND", new Uint8Array()),
  );
}

function jpegSegment(marker: number, data: Uint8Array): Uint8Array {
  const length = data.length + 2;
  return concat(new Uint8Array([0xff, marker, (length >>> 8) & 0xff, length & 0xff]), data);
}

function jpeg(width: number, height: number, metadata: Uint8Array[] = []): Uint8Array {
  const frame = new Uint8Array([
    8,
    (height >>> 8) & 0xff,
    height & 0xff,
    (width >>> 8) & 0xff,
    width & 0xff,
    3,
    1,
    0x11,
    0,
    2,
    0x11,
    0,
    3,
    0x11,
    0,
  ]);
  const scan = new Uint8Array([3, 1, 0, 2, 0x11, 3, 0x11, 0, 63, 0]);
  return concat(
    new Uint8Array([0xff, 0xd8]),
    ...metadata,
    jpegSegment(0xc0, frame),
    jpegSegment(0xda, scan),
    new Uint8Array([0x11, 0x22, 0xff, 0x00, 0x33, 0xff, 0xd9]),
  );
}

function riffChunk(type: string, data: Uint8Array): Uint8Array {
  return concat(encoder.encode(type), u32le(data.length), data, data.length % 2 ? new Uint8Array([0]) : new Uint8Array());
}

function webp(width: number, height: number, metadata: Uint8Array[] = []): Uint8Array {
  const extended = new Uint8Array(10);
  extended[0] = metadata.length ? 0x2c : 0;
  extended.set(u24le(width - 1), 4);
  extended.set(u24le(height - 1), 7);
  const lossy = new Uint8Array(10);
  lossy.set([0x10, 0, 0, 0x9d, 0x01, 0x2a], 0);
  lossy[6] = width & 0xff;
  lossy[7] = (width >>> 8) & 0x3f;
  lossy[8] = height & 0xff;
  lossy[9] = (height >>> 8) & 0x3f;
  const chunks = concat(riffChunk("VP8X", extended), ...metadata, riffChunk("VP8 ", lossy));
  return concat(encoder.encode("RIFF"), u32le(chunks.length + 4), encoder.encode("WEBP"), chunks);
}

function gif(width: number, height: number, comment = "private metadata", frames = 1): Uint8Array {
  const u16 = (value: number) => new Uint8Array([value & 0xff, (value >>> 8) & 0xff]);
  const commentBytes = encoder.encode(comment);
  const frame = concat(
    new Uint8Array([0x21, 0xf9, 0x04, 0x00, 0x05, 0x00, 0x00, 0x00]),
    new Uint8Array([0x2c, 0, 0, 0, 0, 1, 0, 1, 0, 0]),
    new Uint8Array([0x02, 0x02, 0x4c, 0x01, 0x00]),
  );
  const frameParts = Array.from({ length: Math.max(1, frames) }, () => frame);
  return concat(
    encoder.encode("GIF89a"),
    u16(width),
    u16(height),
    new Uint8Array([0x80, 0x00, 0x00]),
    new Uint8Array([0, 0, 0, 255, 255, 255]),
    new Uint8Array([0x21, 0xfe, commentBytes.length]),
    commentBytes,
    new Uint8Array([0x00]),
    ...frameParts,
    new Uint8Array([0x3b]),
  );
}

async function expectMediaError(promise: Promise<unknown>, code: string): Promise<void> {
  try {
    await promise;
    throw new Error("expected media error");
  } catch (error) {
    expect(error).toBeInstanceOf(ProfileMediaError);
    expect((error as ProfileMediaError).code).toBe(code);
  }
}

describe("profile media inspection", () => {
  it("accepts a valid avatar PNG and strips textual metadata before hashing", async () => {
    const raw = png(256, 256, [pngChunk("tEXt", encoder.encode("Comment\0private location"))]);

    const result = await inspectAndSanitizeProfileMedia("avatar", "image/png", raw);

    expect(result).toMatchObject({ contentType: "image/png", extension: "png", width: 256, height: 256 });
    expect(result.bytes.length).toBeLessThan(raw.length);
    expect(new TextDecoder().decode(result.bytes)).not.toContain("private location");
    expect(result.sha256).toMatch(/^[a-f0-9]{64}$/);
  });

  it("strips JPEG APP metadata and comments while preserving scan data", async () => {
    const raw = jpeg(512, 512, [
      jpegSegment(0xe1, concat(encoder.encode("Exif\0\0"), encoder.encode("private gps"))),
      jpegSegment(0xfe, encoder.encode("private comment")),
    ]);

    const result = await inspectAndSanitizeProfileMedia("avatar", "image/jpeg", raw);

    expect(result).toMatchObject({ contentType: "image/jpeg", extension: "jpg", width: 512, height: 512 });
    expect(new TextDecoder().decode(result.bytes)).not.toContain("private");
    expect(result.bytes.at(-2)).toBe(0xff);
    expect(result.bytes.at(-1)).toBe(0xd9);
  });

  it("accepts bounded GIF animation and strips comments", async () => {
    const raw = gif(512, 512);
    const result = await inspectAndSanitizeProfileMedia("avatar", "image/gif", raw);
    expect(result).toMatchObject({ contentType: "image/gif", extension: "gif", width: 512, height: 512 });
    expect(new TextDecoder().decode(result.bytes)).not.toContain("private metadata");
  });

  it("rejects oversized GIF animation and gallery MIME/dimension violations", async () => {
    const tooMany = gif(128, 128, "x", 121);
    await expectMediaError(inspectAndSanitizeProfileMedia("gallery0", "image/gif", tooMany), "MEDIA_INVALID");
    await expectMediaError(
      inspectAndSanitizeProfileMedia("gallery0", "image/png", gif(512, 512)),
      "MEDIA_INVALID",
    );
    await expectMediaError(
      inspectAndSanitizeProfileMedia("gallery0", "image/gif", gif(64, 64)),
      "MEDIA_DIMENSIONS_INVALID",
    );
    await expect(inspectAndSanitizeProfileMedia("gallery0", "image/gif", gif(640, 360))).resolves.toMatchObject({
      contentType: "image/gif",
      width: 640,
      height: 360,
    });
  });

  it("strips WebP EXIF, XMP, and ICC chunks and recalculates RIFF size", async () => {
    const raw = webp(1600, 900, [
      riffChunk("ICCP", encoder.encode("private profile")),
      riffChunk("EXIF", encoder.encode("private exif")),
      riffChunk("XMP ", encoder.encode("private xmp")),
    ]);

    const result = await inspectAndSanitizeProfileMedia("banner", "image/webp", raw);

    expect(result).toMatchObject({ contentType: "image/webp", extension: "webp", width: 1600, height: 900 });
    expect(new TextDecoder().decode(result.bytes)).not.toContain("private");
    expect(new DataView(result.bytes.buffer, result.bytes.byteOffset).getUint32(4, true)).toBe(result.bytes.length - 8);
    expect(result.bytes[20]! & 0x2c).toBe(0);
  });

  it("rejects declared MIME/signature mismatches, GIF, SVG, trailing polyglots, and truncation", async () => {
    await expectMediaError(inspectAndSanitizeProfileMedia("avatar", "image/png", jpeg(256, 256)), "MEDIA_INVALID");
    await expectMediaError(inspectAndSanitizeProfileMedia("avatar", "image/gif", encoder.encode("GIF89a")), "MEDIA_INVALID");
    await expectMediaError(
      inspectAndSanitizeProfileMedia("avatar", "image/png", encoder.encode("<svg xmlns='http://www.w3.org/2000/svg'/>")),
      "MEDIA_INVALID",
    );
    await expectMediaError(
      inspectAndSanitizeProfileMedia("avatar", "image/png", concat(png(256, 256), encoder.encode("<script>"))),
      "MEDIA_INVALID",
    );
    await expectMediaError(
      inspectAndSanitizeProfileMedia("avatar", "image/jpeg", jpeg(256, 256).subarray(0, -2)),
      "MEDIA_INVALID",
    );
  });

  it("enforces slot dimensions without requiring a landscape banner", async () => {
    await expectMediaError(inspectAndSanitizeProfileMedia("avatar", "image/png", png(255, 256)), "MEDIA_DIMENSIONS_INVALID");
    await expectMediaError(
      inspectAndSanitizeProfileMedia("banner", "image/png", png(63, 64)),
      "MEDIA_DIMENSIONS_INVALID",
    );
    await expectMediaError(
      inspectAndSanitizeProfileMedia("banner", "image/png", png(1280, 4097)),
      "MEDIA_DIMENSIONS_INVALID",
    );
    await expect(inspectAndSanitizeProfileMedia("banner", "image/png", png(1280, 720))).resolves.toMatchObject({
      width: 1280,
      height: 720,
    });
    await expect(inspectAndSanitizeProfileMedia("banner", "image/png", png(900, 1600))).resolves.toMatchObject({
      width: 900,
      height: 1600,
    });
  });

  it("enforces the slot byte limit even when called without an HTTP stream", async () => {
    await expectMediaError(
      inspectAndSanitizeProfileMedia("avatar", "image/png", new Uint8Array(4 * 1024 * 1024 + 1)),
      "MEDIA_TOO_LARGE",
    );
  });
});

describe("bounded upload reads and keys", () => {
  it("cancels an upload stream as soon as it crosses the byte limit", async () => {
    let cancelled = false;
    const body = new ReadableStream<Uint8Array>({
      start(controller) {
        controller.enqueue(new Uint8Array(6));
        controller.enqueue(new Uint8Array(5));
      },
      cancel() {
        cancelled = true;
      },
    });

    await expectMediaError(readBoundedMediaBody(body, 10, null), "MEDIA_TOO_LARGE");
    expect(cancelled).toBe(true);
  });

  it("rejects an oversized Content-Length before consuming the body", async () => {
    let pulled = false;
    const body = new ReadableStream<Uint8Array>({
      pull(controller) {
        pulled = true;
        controller.close();
      },
    });

    await expectMediaError(readBoundedMediaBody(body, 10, "11"), "MEDIA_TOO_LARGE");
    expect(pulled).toBe(false);
  });

  it("builds only ownership-scoped keys from cryptographic versions", () => {
    const version = createProfileMediaVersion();
    expect(version).toMatch(/^[a-f0-9]{64}$/);
    expect(profileMediaObjectKey("user_123", "avatar", version, "png")).toBe(
      `users/user_123/avatar/${version}.png`,
    );
    expect(() => profileMediaObjectKey("../other", "avatar", version, "png")).toThrow(ProfileMediaError);
    expect(() => profileMediaObjectKey("user_123", "avatar", "chosen-name", "png")).toThrow(ProfileMediaError);
  });
});
