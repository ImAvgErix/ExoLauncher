export const PROFILE_MEDIA_CACHE_CONTROL = "public, max-age=0, must-revalidate";
export const PROFILE_MEDIA_CACHE_CONTROL_LEGACY = "public, max-age=31536000, immutable";

export const PROFILE_GALLERY_KINDS = ["gallery0", "gallery1", "gallery2", "gallery3", "gallery4", "gallery5"] as const;
export type ProfileGalleryKind = (typeof PROFILE_GALLERY_KINDS)[number];
export type ProfileMediaKind = "avatar" | "banner" | ProfileGalleryKind;
export function isProfileMediaKind(value: string): value is ProfileMediaKind {
  return value === "avatar" || value === "banner" || PROFILE_GALLERY_KINDS.includes(value as ProfileGalleryKind);
}
export type ProfileMediaErrorCode =
  | "MEDIA_UNSUPPORTED"
  | "MEDIA_TOO_LARGE"
  | "MEDIA_INVALID"
  | "MEDIA_DIMENSIONS_INVALID"
  | "MEDIA_CONFLICT";

export class ProfileMediaError extends Error {
  readonly status: number;
  readonly code: ProfileMediaErrorCode;

  constructor(status: number, code: ProfileMediaErrorCode, message: string) {
    super(message);
    this.status = status;
    this.code = code;
  }
}

export type InspectedProfileMedia = {
  bytes: Uint8Array;
  contentType: "image/png" | "image/jpeg" | "image/webp" | "image/gif";
  extension: "png" | "jpg" | "webp" | "gif";
  width: number;
  height: number;
  sha256: string;
  sha256Bytes: ArrayBuffer;
};

export type ProfileMediaRow = {
  user_id: string;
  kind: ProfileMediaKind;
  version: string;
  object_key: string;
  content_type: InspectedProfileMedia["contentType"];
  byte_size: number;
  width: number;
  height: number;
  sha256: string;
  created_at: string;
  updated_at: string;
};

export type PublicProfileMedia = {
  kind: ProfileMediaKind;
  version: string;
  url: string;
  contentType: InspectedProfileMedia["contentType"];
  size: number;
  width: number;
  height: number;
  sha256: string;
  updatedAt: string;
};

export const PROFILE_MEDIA_LIMITS: Record<ProfileMediaKind, number> = {
  avatar: 4 * 1024 * 1024,
  banner: 8 * 1024 * 1024,
  gallery0: 8 * 1024 * 1024,
  gallery1: 8 * 1024 * 1024,
  gallery2: 8 * 1024 * 1024,
  gallery3: 8 * 1024 * 1024,
  gallery4: 8 * 1024 * 1024,
  gallery5: 8 * 1024 * 1024,
};

const PNG_SIGNATURE = new Uint8Array([137, 80, 78, 71, 13, 10, 26, 10]);
const PNG_SAFE_ANCILLARY = new Set(["cHRM", "gAMA", "sBIT", "sRGB", "tRNS"]);

function invalidImage(message = "The upload is not a valid supported image."): ProfileMediaError {
  return new ProfileMediaError(400, "MEDIA_INVALID", message);
}

function bytesEqualAt(bytes: Uint8Array, expected: Uint8Array, offset = 0): boolean {
  if (offset + expected.length > bytes.length) return false;
  for (let index = 0; index < expected.length; index++) {
    if (bytes[offset + index] !== expected[index]) return false;
  }
  return true;
}

function readU32Be(bytes: Uint8Array, offset: number): number {
  return (
    bytes[offset]! * 0x1000000 +
    bytes[offset + 1]! * 0x10000 +
    bytes[offset + 2]! * 0x100 +
    bytes[offset + 3]!
  );
}

function writeU32Be(value: number): Uint8Array {
  return new Uint8Array([
    (value >>> 24) & 0xff,
    (value >>> 16) & 0xff,
    (value >>> 8) & 0xff,
    value & 0xff,
  ]);
}

function readU32Le(bytes: Uint8Array, offset: number): number {
  return (
    bytes[offset]! +
    bytes[offset + 1]! * 0x100 +
    bytes[offset + 2]! * 0x10000 +
    bytes[offset + 3]! * 0x1000000
  );
}

function readU16Le(bytes: Uint8Array, offset: number): number {
  return bytes[offset]! + bytes[offset + 1]! * 0x100;
}

function readU24Le(bytes: Uint8Array, offset: number): number {
  return bytes[offset]! + bytes[offset + 1]! * 0x100 + bytes[offset + 2]! * 0x10000;
}

function writeU32Le(value: number): Uint8Array {
  return new Uint8Array([value & 0xff, (value >>> 8) & 0xff, (value >>> 16) & 0xff, (value >>> 24) & 0xff]);
}

function concatBytes(parts: Uint8Array[]): Uint8Array {
  const length = parts.reduce((total, part) => total + part.length, 0);
  const out = new Uint8Array(length);
  let offset = 0;
  for (const part of parts) {
    out.set(part, offset);
    offset += part.length;
  }
  return out;
}

function crc32(bytes: Uint8Array): number {
  let crc = 0xffffffff;
  for (const byte of bytes) {
    crc ^= byte;
    for (let bit = 0; bit < 8; bit++) {
      crc = (crc >>> 1) ^ (crc & 1 ? 0xedb88320 : 0);
    }
  }
  return (crc ^ 0xffffffff) >>> 0;
}

function pngChunkType(bytes: Uint8Array, offset: number): string {
  return String.fromCharCode(bytes[offset]!, bytes[offset + 1]!, bytes[offset + 2]!, bytes[offset + 3]!);
}

function validatePngHeader(data: Uint8Array): { width: number; height: number; colorType: number } {
  if (data.length !== 13) throw invalidImage("PNG IHDR is invalid.");
  const width = readU32Be(data, 0);
  const height = readU32Be(data, 4);
  const bitDepth = data[8]!;
  const colorType = data[9]!;
  const validDepths: Record<number, readonly number[]> = {
    0: [1, 2, 4, 8, 16],
    2: [8, 16],
    3: [1, 2, 4, 8],
    4: [8, 16],
    6: [8, 16],
  };
  if (
    width === 0 ||
    height === 0 ||
    !validDepths[colorType]?.includes(bitDepth) ||
    data[10] !== 0 ||
    data[11] !== 0 ||
    (data[12] !== 0 && data[12] !== 1)
  ) {
    throw invalidImage("PNG IHDR is invalid.");
  }
  return { width, height, colorType };
}

function sanitizePng(bytes: Uint8Array): { bytes: Uint8Array; width: number; height: number } {
  if (!bytesEqualAt(bytes, PNG_SIGNATURE)) throw invalidImage("PNG signature does not match the declared type.");
  const kept: Uint8Array[] = [PNG_SIGNATURE];
  let offset = PNG_SIGNATURE.length;
  let width = 0;
  let height = 0;
  let colorType = -1;
  let sawHeader = false;
  let sawPalette = false;
  let sawImageData = false;
  let endedImageData = false;
  let sawEnd = false;

  while (offset < bytes.length) {
    if (offset + 12 > bytes.length) throw invalidImage("PNG is truncated.");
    const dataLength = readU32Be(bytes, offset);
    const chunkEnd = offset + 12 + dataLength;
    if (!Number.isSafeInteger(chunkEnd) || chunkEnd > bytes.length) throw invalidImage("PNG is truncated.");
    const typeOffset = offset + 4;
    const dataOffset = offset + 8;
    const type = pngChunkType(bytes, typeOffset);
    if (!/^[A-Za-z]{4}$/.test(type)) throw invalidImage("PNG chunk type is invalid.");
    const expectedCrc = readU32Be(bytes, dataOffset + dataLength);
    const actualCrc = crc32(bytes.subarray(typeOffset, dataOffset + dataLength));
    if (actualCrc !== expectedCrc) throw invalidImage("PNG chunk checksum is invalid.");
    const chunk = bytes.slice(offset, chunkEnd);
    const data = bytes.subarray(dataOffset, dataOffset + dataLength);

    if (!sawHeader && type !== "IHDR") throw invalidImage("PNG IHDR must be first.");
    if (sawEnd) throw invalidImage("PNG contains trailing data.");

    if (type === "IHDR") {
      if (sawHeader) throw invalidImage("PNG contains more than one IHDR.");
      ({ width, height, colorType } = validatePngHeader(data));
      sawHeader = true;
      kept.push(chunk);
    } else if (type === "PLTE") {
      if (sawImageData || sawPalette || dataLength === 0 || dataLength > 768 || dataLength % 3 !== 0) {
        throw invalidImage("PNG palette is invalid.");
      }
      if (colorType === 0 || colorType === 4) throw invalidImage("PNG palette is invalid for its color type.");
      sawPalette = true;
      kept.push(chunk);
    } else if (type === "IDAT") {
      if (endedImageData || (colorType === 3 && !sawPalette)) throw invalidImage("PNG image-data order is invalid.");
      sawImageData = true;
      kept.push(chunk);
    } else if (type === "IEND") {
      if (dataLength !== 0 || !sawImageData) throw invalidImage("PNG IEND is invalid.");
      sawEnd = true;
      kept.push(chunk);
      if (chunkEnd !== bytes.length) throw invalidImage("PNG contains trailing data.");
    } else {
      if (sawImageData) endedImageData = true;
      const critical = type.charCodeAt(0) >= 65 && type.charCodeAt(0) <= 90;
      if (critical) throw invalidImage("PNG contains an unknown critical chunk.");
      if (PNG_SAFE_ANCILLARY.has(type)) kept.push(chunk);
    }
    offset = chunkEnd;
  }

  if (!sawHeader || !sawImageData || !sawEnd) throw invalidImage("PNG is truncated.");
  return { bytes: concatBytes(kept), width, height };
}

const JPEG_START_OF_FRAME = new Set([0xc0, 0xc1, 0xc2, 0xc3, 0xc5, 0xc6, 0xc7, 0xc9, 0xca, 0xcb, 0xcd, 0xce, 0xcf]);

function sanitizeJpeg(bytes: Uint8Array): { bytes: Uint8Array; width: number; height: number } {
  if (bytes.length < 4 || bytes[0] !== 0xff || bytes[1] !== 0xd8) {
    throw invalidImage("JPEG signature does not match the declared type.");
  }
  const kept: Uint8Array[] = [bytes.slice(0, 2)];
  let offset = 2;
  let width = 0;
  let height = 0;
  let sawFrame = false;
  let sawScan = false;
  let sawEnd = false;

  while (offset < bytes.length) {
    if (bytes[offset] !== 0xff) throw invalidImage("JPEG marker stream is invalid.");
    const markerStart = offset;
    while (offset < bytes.length && bytes[offset] === 0xff) offset++;
    if (offset >= bytes.length) throw invalidImage("JPEG is truncated.");
    const marker = bytes[offset]!;
    const markerEnd = offset + 1;
    if (marker === 0x00) throw invalidImage("JPEG marker stream is invalid.");

    if (marker === 0xd9) {
      kept.push(bytes.slice(markerStart, markerEnd));
      if (markerEnd !== bytes.length) throw invalidImage("JPEG contains trailing data.");
      sawEnd = true;
      offset = markerEnd;
      break;
    }
    if (marker === 0xd8) throw invalidImage("JPEG contains an unexpected SOI marker.");
    if (marker === 0x01 || (marker >= 0xd0 && marker <= 0xd7)) {
      kept.push(bytes.slice(markerStart, markerEnd));
      offset = markerEnd;
      continue;
    }
    if (markerEnd + 2 > bytes.length) throw invalidImage("JPEG is truncated.");
    const segmentLength = bytes[markerEnd]! * 0x100 + bytes[markerEnd + 1]!;
    if (segmentLength < 2) throw invalidImage("JPEG segment length is invalid.");
    const segmentEnd = markerEnd + segmentLength;
    if (segmentEnd > bytes.length) throw invalidImage("JPEG is truncated.");
    const dataStart = markerEnd + 2;
    const dataLength = segmentLength - 2;

    if (JPEG_START_OF_FRAME.has(marker)) {
      if (dataLength < 6) throw invalidImage("JPEG frame header is invalid.");
      const frameHeight = bytes[dataStart + 1]! * 0x100 + bytes[dataStart + 2]!;
      const frameWidth = bytes[dataStart + 3]! * 0x100 + bytes[dataStart + 4]!;
      const components = bytes[dataStart + 5]!;
      if (frameWidth === 0 || frameHeight === 0 || components === 0 || dataLength !== 6 + components * 3) {
        throw invalidImage("JPEG frame dimensions are invalid.");
      }
      if (sawFrame && (width !== frameWidth || height !== frameHeight)) {
        throw invalidImage("JPEG contains inconsistent frames.");
      }
      width = frameWidth;
      height = frameHeight;
      sawFrame = true;
    }

    const isMetadata = (marker >= 0xe0 && marker <= 0xef) || marker === 0xfe;
    if (!isMetadata) kept.push(bytes.slice(markerStart, segmentEnd));

    if (marker !== 0xda) {
      offset = segmentEnd;
      continue;
    }
    if (!sawFrame || dataLength < 6) throw invalidImage("JPEG scan header is invalid.");
    const scanComponents = bytes[dataStart]!;
    if (scanComponents === 0 || dataLength !== 4 + scanComponents * 2) {
      throw invalidImage("JPEG scan header is invalid.");
    }
    sawScan = true;
    const scanStart = segmentEnd;
    let scanOffset = scanStart;
    let foundMarker = false;
    while (scanOffset < bytes.length) {
      if (bytes[scanOffset] !== 0xff) {
        scanOffset++;
        continue;
      }
      const nextMarkerStart = scanOffset;
      while (scanOffset < bytes.length && bytes[scanOffset] === 0xff) scanOffset++;
      if (scanOffset >= bytes.length) throw invalidImage("JPEG is truncated.");
      const scanMarker = bytes[scanOffset]!;
      if (scanMarker === 0x00 || (scanMarker >= 0xd0 && scanMarker <= 0xd7)) {
        scanOffset++;
        continue;
      }
      kept.push(bytes.slice(scanStart, nextMarkerStart));
      offset = nextMarkerStart;
      foundMarker = true;
      break;
    }
    if (!foundMarker) throw invalidImage("JPEG is truncated.");
  }

  if (!sawFrame || !sawScan || !sawEnd) throw invalidImage("JPEG is truncated.");
  return { bytes: concatBytes(kept), width, height };
}

function webpChunk(type: string, data: Uint8Array): Uint8Array {
  const padding = data.length % 2 === 0 ? new Uint8Array() : new Uint8Array([0]);
  return concatBytes([new TextEncoder().encode(type), writeU32Le(data.length), data, padding]);
}

function vp8Dimensions(data: Uint8Array): { width: number; height: number } {
  if (
    data.length < 10 ||
    data[3] !== 0x9d ||
    data[4] !== 0x01 ||
    data[5] !== 0x2a
  ) {
    throw invalidImage("WebP VP8 frame header is invalid.");
  }
  const width = (data[6]! + data[7]! * 0x100) & 0x3fff;
  const height = (data[8]! + data[9]! * 0x100) & 0x3fff;
  if (width === 0 || height === 0) throw invalidImage("WebP dimensions are invalid.");
  return { width, height };
}

function vp8lDimensions(data: Uint8Array): { width: number; height: number } {
  if (data.length < 5 || data[0] !== 0x2f) throw invalidImage("WebP VP8L frame header is invalid.");
  const width = 1 + data[1]! + ((data[2]! & 0x3f) << 8);
  const height = 1 + ((data[2]! & 0xc0) >> 6) + (data[3]! << 2) + ((data[4]! & 0x0f) << 10);
  return { width, height };
}

function sanitizeWebp(bytes: Uint8Array): { bytes: Uint8Array; width: number; height: number } {
  const riff = new TextEncoder().encode("RIFF");
  const webp = new TextEncoder().encode("WEBP");
  if (bytes.length < 20 || !bytesEqualAt(bytes, riff) || !bytesEqualAt(bytes, webp, 8)) {
    throw invalidImage("WebP signature does not match the declared type.");
  }
  if (readU32Le(bytes, 4) + 8 !== bytes.length) throw invalidImage("WebP RIFF length is invalid.");

  const kept: Uint8Array[] = [];
  let offset = 12;
  let width = 0;
  let height = 0;
  let canvasWidth = 0;
  let canvasHeight = 0;
  let extendedFlags = 0;
  let sawExtended = false;
  let sawImage = false;
  let sawMetadata = false;

  while (offset < bytes.length) {
    if (offset + 8 > bytes.length) throw invalidImage("WebP is truncated.");
    const type = String.fromCharCode(bytes[offset]!, bytes[offset + 1]!, bytes[offset + 2]!, bytes[offset + 3]!);
    const dataLength = readU32Le(bytes, offset + 4);
    const dataStart = offset + 8;
    const dataEnd = dataStart + dataLength;
    const chunkEnd = dataEnd + (dataLength & 1);
    if (!Number.isSafeInteger(chunkEnd) || chunkEnd > bytes.length) throw invalidImage("WebP is truncated.");
    const data = bytes.slice(dataStart, dataEnd);

    if (type === "VP8X") {
      if (offset !== 12 || sawExtended || dataLength !== 10) throw invalidImage("WebP extended header is invalid.");
      extendedFlags = data[0]!;
      if ((extendedFlags & 0xc1) !== 0 || data[1] !== 0 || data[2] !== 0 || data[3] !== 0) {
        throw invalidImage("WebP extended header is invalid.");
      }
      if ((extendedFlags & 0x02) !== 0) {
        throw invalidImage("Animated WebP is not accepted because it cannot be safely sanitized without re-encoding.");
      }
      canvasWidth = readU24Le(data, 4) + 1;
      canvasHeight = readU24Le(data, 7) + 1;
      const sanitizedHeader = data.slice();
      sanitizedHeader[0] = extendedFlags & ~0x2c;
      kept.push(webpChunk(type, sanitizedHeader));
      sawExtended = true;
    } else if (type === "VP8 " || type === "VP8L") {
      if (sawImage) throw invalidImage("WebP contains multiple image bitstreams.");
      ({ width, height } = type === "VP8 " ? vp8Dimensions(data) : vp8lDimensions(data));
      sawImage = true;
      kept.push(webpChunk(type, data));
    } else if (type === "ALPH") {
      if (!sawExtended || dataLength === 0) throw invalidImage("WebP alpha chunk is invalid.");
      kept.push(webpChunk(type, data));
    } else if (type === "ANIM" || type === "ANMF") {
      throw invalidImage("Animated WebP is not accepted because it cannot be safely sanitized without re-encoding.");
    } else if (type === "EXIF" || type === "XMP " || type === "ICCP") {
      sawMetadata = true;
    }
    offset = chunkEnd;
  }

  if (!sawImage || offset !== bytes.length) throw invalidImage("WebP has no image bitstream.");
  if (sawMetadata && !sawExtended) throw invalidImage("WebP metadata requires an extended header.");
  if (sawExtended && (width !== canvasWidth || height !== canvasHeight)) {
    throw invalidImage("WebP canvas dimensions do not match its image.");
  }
  const body = concatBytes(kept);
  const sanitized = concatBytes([riff, writeU32Le(body.length + 4), webp, body]);
  return { bytes: sanitized, width, height };
}

const GIF87A = new TextEncoder().encode("GIF87a");
const GIF89A = new TextEncoder().encode("GIF89a");
const GIF_MAX_FRAMES = 120;
const GIF_MAX_DECODED_PIXELS = 80_000_000;

function gifSubBlocksEnd(bytes: Uint8Array, start: number): number {
  let offset = start;
  while (offset < bytes.length) {
    const length = bytes[offset]!;
    offset += 1;
    if (length === 0) return offset;
    if (offset + length > bytes.length) throw invalidImage("GIF sub-blocks are truncated.");
    offset += length;
  }
  throw invalidImage("GIF sub-blocks are truncated.");
}

function sanitizeGif(bytes: Uint8Array): { bytes: Uint8Array; width: number; height: number } {
  if (bytes.length < 14 || (!bytesEqualAt(bytes, GIF87A) && !bytesEqualAt(bytes, GIF89A))) {
    throw invalidImage("GIF signature does not match the declared type.");
  }
  const width = readU16Le(bytes, 6);
  const height = readU16Le(bytes, 8);
  if (width === 0 || height === 0) throw invalidImage("GIF dimensions are invalid.");

  const packed = bytes[10]!;
  const globalTableBytes = (packed & 0x80) !== 0 ? 3 * (1 << ((packed & 0x07) + 1)) : 0;
  let offset = 13 + globalTableBytes;
  if (offset > bytes.length) throw invalidImage("GIF color table is truncated.");
  const kept: Uint8Array[] = [bytes.slice(0, offset)];
  let pendingControl: Uint8Array | null = null;
  let frames = 0;
  let decodedPixels = 0;
  let sawLoop = false;
  let sawTrailer = false;

  while (offset < bytes.length) {
    const marker = bytes[offset]!;
    if (marker === 0x3b) {
      if (offset + 1 !== bytes.length || frames === 0) throw invalidImage("GIF trailer is invalid.");
      kept.push(new Uint8Array([0x3b]));
      sawTrailer = true;
      offset += 1;
      break;
    }

    if (marker === 0x21) {
      if (offset + 2 > bytes.length) throw invalidImage("GIF extension is truncated.");
      const label = bytes[offset + 1]!;
      if (label === 0xf9) {
        if (offset + 8 > bytes.length || bytes[offset + 2] !== 4 || bytes[offset + 7] !== 0) {
          throw invalidImage("GIF graphic control is invalid.");
        }
        const delay = Math.max(2, readU16Le(bytes, offset + 4));
        pendingControl = new Uint8Array([
          0x21, 0xf9, 0x04, bytes[offset + 3]! & 0x1f,
          delay & 0xff, (delay >>> 8) & 0xff, bytes[offset + 6]!, 0x00,
        ]);
        offset += 8;
        continue;
      }

      if (offset + 3 > bytes.length) throw invalidImage("GIF extension is truncated.");
      const headerLength = bytes[offset + 2]!;
      const headerEnd = offset + 3 + headerLength;
      if (headerEnd > bytes.length) throw invalidImage("GIF extension is truncated.");
      const extensionEnd = gifSubBlocksEnd(bytes, headerEnd);
      if (label === 0xff && headerLength === 11) {
        const name = String.fromCharCode(...bytes.subarray(offset + 3, headerEnd));
        const payload = bytes.subarray(headerEnd, extensionEnd);
        if (name === "NETSCAPE2.0" && !sawLoop && payload.length === 5 && payload[0] === 3 && payload[1] === 1 && payload[4] === 0) {
          kept.push(concatBytes([
            new Uint8Array([0x21, 0xff, 0x0b]),
            new TextEncoder().encode("NETSCAPE2.0"),
            new Uint8Array([0x03, 0x01, payload[2]!, payload[3]!, 0x00]),
          ]));
          sawLoop = true;
        }
      }
      // Comments, plain-text, and unknown application data are metadata and are dropped.
      offset = extensionEnd;
      continue;
    }

    if (marker !== 0x2c || offset + 10 > bytes.length) throw invalidImage("GIF block stream is invalid.");
    const left = readU16Le(bytes, offset + 1);
    const top = readU16Le(bytes, offset + 3);
    const frameWidth = readU16Le(bytes, offset + 5);
    const frameHeight = readU16Le(bytes, offset + 7);
    if (
      frameWidth === 0 || frameHeight === 0 ||
      left + frameWidth > width || top + frameHeight > height
    ) {
      throw invalidImage("GIF frame dimensions are invalid.");
    }
    const framePacked = bytes[offset + 9]!;
    const localTableBytes = (framePacked & 0x80) !== 0 ? 3 * (1 << ((framePacked & 0x07) + 1)) : 0;
    const imageDataStart = offset + 10 + localTableBytes;
    if (imageDataStart + 2 > bytes.length) throw invalidImage("GIF image data is truncated.");
    const minimumCodeSize = bytes[imageDataStart]!;
    if (minimumCodeSize < 2 || minimumCodeSize > 8 || bytes[imageDataStart + 1] === 0) {
      throw invalidImage("GIF image data is invalid.");
    }
    const frameEnd = gifSubBlocksEnd(bytes, imageDataStart + 1);
    frames += 1;
    decodedPixels += frameWidth * frameHeight;
    if (frames > GIF_MAX_FRAMES || decodedPixels > GIF_MAX_DECODED_PIXELS) {
      throw invalidImage("GIF animation is too complex.");
    }
    if (pendingControl) kept.push(pendingControl);
    kept.push(bytes.slice(offset, frameEnd));
    pendingControl = null;
    offset = frameEnd;
  }

  if (!sawTrailer || offset !== bytes.length) throw invalidImage("GIF is truncated.");
  return { bytes: concatBytes(kept), width, height };
}

function normalizeMime(value: string): string {
  return value.split(";", 1)[0]!.trim().toLowerCase();
}

function validateDimensions(kind: ProfileMediaKind, width: number, height: number): void {
  if (kind === "avatar") {
    if (width < 256 || height < 256 || width > 4096 || height > 4096) {
      throw new ProfileMediaError(
        400,
        "MEDIA_DIMENSIONS_INVALID",
        "Avatar dimensions must be between 256 and 4096 pixels on each side.",
      );
    }
    return;
  }
  const aspect = width / height;
  if (kind.startsWith("gallery")) {
    if (width < 128 || width > 4096 || height < 128 || height > 4096 || aspect < 0.25 || aspect > 4) {
      throw new ProfileMediaError(
        400,
        "MEDIA_DIMENSIONS_INVALID",
        "Gallery media must be 128-4096 pixels per side with a usable aspect ratio.",
      );
    }
    return;
  }
  if (width < 320 || width > 8192 || height < 120 || height > 4096 || aspect < 1.5 || aspect > 8) {
    throw new ProfileMediaError(
      400,
      "MEDIA_DIMENSIONS_INVALID",
      "Banner dimensions must be landscape, 320-8192 pixels wide, and 120-4096 pixels tall.",
    );
  }
}

function hex(bytes: Uint8Array): string {
  let out = "";
  for (const byte of bytes) out += byte.toString(16).padStart(2, "0");
  return out;
}

async function cancelStream(stream: ReadableStream<Uint8Array>, reason: string): Promise<void> {
  try {
    await stream.cancel(reason);
  } catch {
    // The original validation error is more useful than a secondary cancellation failure.
  }
}

export async function readBoundedMediaBody(
  body: ReadableStream<Uint8Array> | null,
  maxBytes: number,
  contentLength: string | null,
): Promise<Uint8Array> {
  if (!body) throw invalidImage("An image body is required.");
  if (contentLength !== null) {
    if (!/^(0|[1-9][0-9]*)$/.test(contentLength)) {
      await cancelStream(body, "invalid content length");
      throw invalidImage("Content-Length is invalid.");
    }
    const declared = Number(contentLength);
    if (!Number.isSafeInteger(declared)) {
      await cancelStream(body, "invalid content length");
      throw invalidImage("Content-Length is invalid.");
    }
    if (declared > maxBytes) {
      await cancelStream(body, "upload exceeds limit");
      throw new ProfileMediaError(413, "MEDIA_TOO_LARGE", `Image must be ${maxBytes} bytes or smaller.`);
    }
  }

  const reader = body.getReader();
  const chunks: Uint8Array[] = [];
  let total = 0;
  try {
    while (true) {
      const next = await reader.read();
      if (next.done) break;
      if (!(next.value instanceof Uint8Array)) throw invalidImage("Upload stream is invalid.");
      total += next.value.length;
      if (total > maxBytes) {
        try {
          await reader.cancel("upload exceeds limit");
        } catch {
          // Preserve the stable size error even if the source rejects cancellation.
        }
        throw new ProfileMediaError(413, "MEDIA_TOO_LARGE", `Image must be ${maxBytes} bytes or smaller.`);
      }
      chunks.push(next.value);
    }
  } finally {
    reader.releaseLock();
  }
  return concatBytes(chunks);
}

export function createProfileMediaVersion(): string {
  const version = new Uint8Array(32);
  crypto.getRandomValues(version);
  return hex(version);
}

export function profileMediaObjectKey(
  userId: string,
  kind: ProfileMediaKind,
  version: string,
  extension: InspectedProfileMedia["extension"],
): string {
  if (!/^[A-Za-z0-9_-]{1,128}$/.test(userId) || !/^[a-f0-9]{64}$/.test(version)) {
    throw invalidImage("Media ownership path is invalid.");
  }
  if (!isProfileMediaKind(kind) || !["png", "jpg", "webp", "gif"].includes(extension)) {
    throw invalidImage("Media ownership path is invalid.");
  }
  return `users/${userId}/${kind}/${version}.${extension}`;
}

function extensionForContentType(contentType: InspectedProfileMedia["contentType"]): InspectedProfileMedia["extension"] {
  if (contentType === "image/png") return "png";
  if (contentType === "image/jpeg") return "jpg";
  if (contentType === "image/webp") return "webp";
  return "gif";
}

export function publicProfileMedia(row: ProfileMediaRow, baseUrl?: string): PublicProfileMedia {
  const path = `/v1/media/${row.user_id}/${row.kind}/${row.version}`;
  return {
    kind: row.kind,
    version: row.version,
    url: baseUrl ? new URL(path, baseUrl).toString() : path,
    contentType: row.content_type,
    size: row.byte_size,
    width: row.width,
    height: row.height,
    sha256: row.sha256,
    updatedAt: row.updated_at,
  };
}

export async function getProfileMediaProjection(
  db: D1Database,
  userId: string,
): Promise<{ avatar: PublicProfileMedia | null; banner: PublicProfileMedia | null } & Partial<Record<ProfileGalleryKind, PublicProfileMedia>>> {
  const rows = await db
    .prepare(
      `SELECT user_id, kind, version, object_key, content_type, byte_size, width, height, sha256, created_at, updated_at
       FROM profile_media WHERE user_id = ? ORDER BY kind`,
    )
    .bind(userId)
    .all<ProfileMediaRow>();
  let avatar: PublicProfileMedia | null = null;
  let banner: PublicProfileMedia | null = null;
  const gallery: Partial<Record<ProfileGalleryKind, PublicProfileMedia>> = {};
  for (const row of rows.results ?? []) {
    if (!profileMediaRecordHasOwnedKey(row)) continue;
    if (row.kind === "avatar") avatar = publicProfileMedia(row);
    if (row.kind === "banner") banner = publicProfileMedia(row);
    if (row.kind.startsWith("gallery")) gallery[row.kind as ProfileGalleryKind] = publicProfileMedia(row);
  }
  return { avatar, banner, ...gallery };
}

export async function currentProfileMedia(
  db: D1Database,
  userId: string,
  kind: ProfileMediaKind,
  version?: string,
): Promise<ProfileMediaRow | null> {
  const versionClause = version === undefined ? "" : " AND version = ?";
  const statement = db.prepare(
    `SELECT user_id, kind, version, object_key, content_type, byte_size, width, height, sha256, created_at, updated_at
     FROM profile_media WHERE user_id = ? AND kind = ?${versionClause} LIMIT 1`,
  );
  return version === undefined
    ? statement.bind(userId, kind).first<ProfileMediaRow>()
    : statement.bind(userId, kind, version).first<ProfileMediaRow>();
}

export async function replaceProfileMedia(
  db: D1Database,
  bucket: R2Bucket,
  userId: string,
  kind: ProfileMediaKind,
  media: InspectedProfileMedia,
  options: { version?: string; now?: string } = {},
): Promise<{ row: ProfileMediaRow; cleanupPending: boolean }> {
  const version = options.version ?? createProfileMediaVersion();
  const key = profileMediaObjectKey(userId, kind, version, media.extension);
  const existing = await currentProfileMedia(db, userId, kind);
  const stamp = options.now ?? new Date().toISOString();
  await bucket.put(key, media.bytes, {
    httpMetadata: { contentType: media.contentType, cacheControl: PROFILE_MEDIA_CACHE_CONTROL },
    customMetadata: {
      kind,
      width: String(media.width),
      height: String(media.height),
      sha256: media.sha256,
    },
    sha256: media.sha256Bytes,
  });

  let writeSucceeded = false;
  try {
    const result = existing
      ? await db
          .prepare(
            `UPDATE profile_media SET
               version = ?, object_key = ?, content_type = ?, byte_size = ?, width = ?, height = ?,
               sha256 = ?, updated_at = ?
             WHERE user_id = ? AND kind = ? AND version = ?`,
          )
          .bind(
            version,
            key,
            media.contentType,
            media.bytes.length,
            media.width,
            media.height,
            media.sha256,
            stamp,
            userId,
            kind,
            existing.version,
          )
          .run()
      : await db
          .prepare(
            `INSERT INTO profile_media
               (user_id, kind, version, object_key, content_type, byte_size, width, height, sha256, created_at, updated_at)
             VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
             ON CONFLICT(user_id, kind) DO NOTHING`,
          )
          .bind(
            userId,
            kind,
            version,
            key,
            media.contentType,
            media.bytes.length,
            media.width,
            media.height,
            media.sha256,
            stamp,
            stamp,
          )
          .run();
    if (result.meta.changes !== 1) {
      throw new ProfileMediaError(409, "MEDIA_CONFLICT", "Profile media changed concurrently. Retry the upload.");
    }
    writeSucceeded = true;
  } finally {
    if (!writeSucceeded) await bucket.delete(key);
  }

  let cleanupPending = false;
  if (existing && existing.object_key !== key) {
    try {
      await bucket.delete(existing.object_key);
    } catch {
      cleanupPending = true;
    }
  }
  return {
    row: {
      user_id: userId,
      kind,
      version,
      object_key: key,
      content_type: media.contentType,
      byte_size: media.bytes.length,
      width: media.width,
      height: media.height,
      sha256: media.sha256,
      created_at: existing?.created_at ?? stamp,
      updated_at: stamp,
    },
    cleanupPending,
  };
}

export async function deleteCurrentProfileMedia(
  db: D1Database,
  bucket: R2Bucket,
  userId: string,
  kind: ProfileMediaKind,
): Promise<boolean> {
  const existing = await currentProfileMedia(db, userId, kind);
  if (!existing) return false;
  const deleted = await db
    .prepare(`DELETE FROM profile_media WHERE user_id = ? AND kind = ? AND version = ?`)
    .bind(userId, kind, existing.version)
    .run();
  if (deleted.meta.changes !== 1) {
    throw new ProfileMediaError(409, "MEDIA_CONFLICT", "Profile media changed concurrently. Retry the delete.");
  }
  await bucket.delete(existing.object_key);
  return true;
}

export async function cleanupProfileMediaForAccount(
  db: D1Database,
  bucket: R2Bucket,
  userId: string,
): Promise<void> {
  if (!/^[A-Za-z0-9_-]{1,128}$/.test(userId)) throw invalidImage("Media ownership path is invalid.");
  const prefix = `users/${userId}/`;
  let cursor: string | undefined;
  while (true) {
    const listed = await bucket.list({ prefix, limit: 1000, cursor });
    if (listed.objects.length > 0) await bucket.delete(listed.objects.map((object) => object.key));
    if (!listed.truncated) break;
    cursor = listed.cursor;
  }
  await db.prepare(`DELETE FROM profile_media WHERE user_id = ?`).bind(userId).run();
}

export function profileMediaRecordHasOwnedKey(row: ProfileMediaRow): boolean {
  return row.object_key === profileMediaObjectKey(
    row.user_id,
    row.kind,
    row.version,
    extensionForContentType(row.content_type),
  );
}

export async function inspectAndSanitizeProfileMedia(
  kind: ProfileMediaKind,
  declaredContentType: string,
  rawBytes: Uint8Array,
): Promise<InspectedProfileMedia> {
  const maxBytes = PROFILE_MEDIA_LIMITS[kind];
  if (rawBytes.length > maxBytes) {
    throw new ProfileMediaError(413, "MEDIA_TOO_LARGE", `Image must be ${maxBytes} bytes or smaller.`);
  }
  const contentType = normalizeMime(declaredContentType);
  if (contentType !== "image/png" && contentType !== "image/jpeg" && contentType !== "image/webp" && contentType !== "image/gif") {
    throw new ProfileMediaError(415, "MEDIA_UNSUPPORTED", "Use a PNG, JPEG, WebP, or GIF image.");
  }
  const inspected =
    contentType === "image/png"
      ? { ...sanitizePng(rawBytes), extension: "png" as const }
      : contentType === "image/jpeg"
        ? { ...sanitizeJpeg(rawBytes), extension: "jpg" as const }
        : contentType === "image/webp"
          ? { ...sanitizeWebp(rawBytes), extension: "webp" as const }
          : { ...sanitizeGif(rawBytes), extension: "gif" as const };
  validateDimensions(kind, inspected.width, inspected.height);
  const digest = await crypto.subtle.digest("SHA-256", inspected.bytes);
  return {
    bytes: inspected.bytes,
    contentType,
    extension: inspected.extension,
    width: inspected.width,
    height: inspected.height,
    sha256: hex(new Uint8Array(digest)),
    sha256Bytes: digest,
  };
}
