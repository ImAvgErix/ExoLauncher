import type { Store } from "./stores.ts";

const VERSION = "v1";
const encoder = new TextEncoder();

function base64Url(bytes: Uint8Array): string {
  let binary = "";
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary).replaceAll("+", "-").replaceAll("/", "_").replaceAll("=", "");
}

function fromBase64Url(value: string): Uint8Array {
  if (!/^[A-Za-z0-9_-]+$/.test(value)) throw new Error("Stored link identity is not valid.");
  const padding = "=".repeat((4 - (value.length % 4)) % 4);
  let binary: string;
  try {
    binary = atob(value.replaceAll("-", "+").replaceAll("_", "/") + padding);
  } catch {
    throw new Error("Stored link identity is not valid.");
  }
  return Uint8Array.from(binary, (character) => character.charCodeAt(0));
}

async function encryptionKey(secret: string): Promise<CryptoKey> {
  const material = await crypto.subtle.digest(
    "SHA-256",
    encoder.encode(`exo-id/store-link/aes-gcm/${VERSION}\0${secret}`),
  );
  return crypto.subtle.importKey("raw", material, { name: "AES-GCM" }, false, ["encrypt", "decrypt"]);
}

function additionalData(userId: string, store: Store): Uint8Array {
  return encoder.encode(`exo-id/store-link/${VERSION}\0${userId}\0${store}`);
}

export async function encryptStoreExternalId(
  secret: string,
  userId: string,
  store: Store,
  externalId: string,
): Promise<string> {
  const iv = crypto.getRandomValues(new Uint8Array(12));
  const ciphertext = await crypto.subtle.encrypt(
    { name: "AES-GCM", iv, additionalData: additionalData(userId, store), tagLength: 128 },
    await encryptionKey(secret),
    encoder.encode(externalId),
  );
  return `${VERSION}.${base64Url(iv)}.${base64Url(new Uint8Array(ciphertext))}`;
}

export async function decryptStoreExternalId(
  secret: string,
  userId: string,
  store: Store,
  encoded: string,
): Promise<string> {
  const parts = encoded.split(".");
  if (parts.length !== 3 || parts[0] !== VERSION) throw new Error("Stored link identity is not valid.");
  const iv = fromBase64Url(parts[1]);
  const ciphertext = fromBase64Url(parts[2]);
  if (iv.length !== 12 || ciphertext.length < 17) throw new Error("Stored link identity is not valid.");
  try {
    const plaintext = await crypto.subtle.decrypt(
      { name: "AES-GCM", iv, additionalData: additionalData(userId, store), tagLength: 128 },
      await encryptionKey(secret),
      ciphertext,
    );
    return new TextDecoder("utf-8", { fatal: true, ignoreBOM: false }).decode(plaintext);
  } catch {
    throw new Error("Stored link identity could not be decrypted.");
  }
}
