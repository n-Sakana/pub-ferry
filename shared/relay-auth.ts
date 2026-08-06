// Pairing and request signing for the Tailscale-side contract.
//
// Tailscale already encrypts and authenticates the transport, so this layer is
// not trying to replace it. What it adds is that a host only accepts transfers
// from devices its owner deliberately paired with it — being on the tailnet is
// not by itself permission to write files onto a machine — and that a request
// which is replayed, re-ordered, truncated or altered in flight is refused
// rather than half-applied.
//
// Runs unchanged in Node and in the browser: everything here is Web Crypto.

const encoder = new TextEncoder();

export const SECRET_BYTES = 32;
export const NONCE_BYTES = 16;

/** Base32-ish alphabet with the characters that get misread out loud or
 *  mistyped removed: no 0/O, no 1/I/L, no 8/B. What is left is 27 symbols. */
const CODE_ALPHABET = "ABCDEFGHJKMNPQRSTUVWXYZ2345679";
/** 10 characters over a 30-symbol alphabet is a shade over 49 bits. Paired
 *  with the expiry and the attempt cap below, guessing is not a way in. */
export const PAIR_CODE_LENGTH = 10;
export const PAIR_CODE_TTL_SECONDS = 600;
/** A code is destroyed after this many failed attempts, not merely rate
 *  limited: the owner can always print another one. */
export const PAIR_CODE_MAX_ATTEMPTS = 5;

export function randomBytes(length: number): Uint8Array {
  const out = new Uint8Array(length);
  crypto.getRandomValues(out);
  return out;
}

export function toBase64Url(bytes: Uint8Array): string {
  let binary = "";
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

export function fromBase64Url(text: string): Uint8Array | null {
  if (!/^[A-Za-z0-9_-]+$/.test(text)) return null;
  const padded = text.replace(/-/g, "+").replace(/_/g, "/");
  try {
    const binary = atob(padded);
    const out = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i++) out[i] = binary.charCodeAt(i);
    return out;
  } catch {
    return null;
  }
}

/** A pairing code a human reads off one screen and types into another.
 *  Formatted in groups when displayed; the groups are not part of the value. */
export function generatePairCode(): string {
  const bytes = randomBytes(PAIR_CODE_LENGTH * 2);
  let code = "";
  // Rejection sampling so every symbol is equally likely — a modulo of 256 by
  // 30 would make the first 16 symbols slightly more common.
  for (let i = 0; code.length < PAIR_CODE_LENGTH && i < bytes.length; i++) {
    const value = bytes[i]!;
    if (value >= 240) continue;
    code += CODE_ALPHABET[value % CODE_ALPHABET.length];
  }
  return code.length === PAIR_CODE_LENGTH ? code : generatePairCode();
}

export function formatPairCode(code: string): string {
  return code.replace(/(.{5})(?=.)/g, "$1-");
}

/** Accept what a human typed: spacing, hyphens and case are theirs, not ours.
 *  Returns null when it cannot be a code at all. */
export function normalizePairCode(input: string): string | null {
  const cleaned = input.toUpperCase().replace(/[^A-Z0-9]/g, "");
  if (cleaned.length !== PAIR_CODE_LENGTH) return null;
  for (const character of cleaned) {
    if (!CODE_ALPHABET.includes(character)) return null;
  }
  return cleaned;
}

export async function sha256(bytes: Uint8Array): Promise<Uint8Array> {
  return new Uint8Array(await crypto.subtle.digest("SHA-256", Uint8Array.from(bytes)));
}

export function toHex(bytes: Uint8Array): string {
  let out = "";
  for (const byte of bytes) out += byte.toString(16).padStart(2, "0");
  return out;
}

export async function sha256Hex(bytes: Uint8Array): Promise<string> {
  return toHex(await sha256(bytes));
}

/** Comparison that does not stop at the first differing byte. */
export function timingSafeEqual(a: string, b: string): boolean {
  if (typeof a !== "string" || typeof b !== "string") return false;
  if (a.length !== b.length) return false;
  let diff = 0;
  for (let i = 0; i < a.length; i++) diff |= a.charCodeAt(i) ^ b.charCodeAt(i);
  return diff === 0;
}

async function hmacKey(secret: Uint8Array): Promise<CryptoKey> {
  return crypto.subtle.importKey(
    "raw",
    Uint8Array.from(secret),
    { name: "HMAC", hash: "SHA-256" },
    false,
    ["sign"],
  );
}

export interface SignedRequestFields {
  method: string;
  path: string;
  timestamp: number;
  nonce: string;
  bodyDigest: string;
}

/**
 * The string that is signed.
 *
 * Every field is length-delimited by the newline and none of them may contain
 * one, so no combination of values can be reinterpreted as a different request
 * — `GET /a\nb` and `GET /a` + a header cannot collide.
 */
export function canonicalRequest(fields: SignedRequestFields): string {
  const { method, path, timestamp, nonce, bodyDigest } = fields;
  for (const part of [method, path, nonce, bodyDigest]) {
    if (part.includes("\n")) throw new Error("signable field contains a newline");
  }
  return [
    "decimen-relay-v1",
    method.toUpperCase(),
    path,
    String(timestamp),
    nonce,
    bodyDigest,
  ].join("\n");
}

export async function signRequest(
  secret: Uint8Array,
  fields: SignedRequestFields,
): Promise<string> {
  const key = await hmacKey(secret);
  const mac = await crypto.subtle.sign(
    "HMAC",
    key,
    encoder.encode(canonicalRequest(fields)),
  );
  return toBase64Url(new Uint8Array(mac));
}

export async function verifyRequestSignature(
  secret: Uint8Array,
  fields: SignedRequestFields,
  signature: string,
): Promise<boolean> {
  return timingSafeEqual(await signRequest(secret, fields), signature);
}

export function generateNonce(): string {
  return toBase64Url(randomBytes(NONCE_BYTES));
}

/** Ids are shown in the host's own device list and appear in a header. */
export function generateDeviceId(): string {
  return toBase64Url(randomBytes(9));
}
